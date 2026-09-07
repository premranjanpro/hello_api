"""
agent.py - Real-Time Multi-Persona AI Voice Agent with Call Recording & Long-Term Memory.

Features:
- Modular Personas (persona_role_*)
- Whisper STT (whisper-large-v3 via Groq) - Ultra fast Hindi/Hinglish/English transcription
- Groq Llama 3.3 70B (llama-3.3-70b-versatile) - Sub-250ms LLM streaming
- Edge-TTS Neural Voices (Free) - Fast audio streaming
- Dual-Track Call Audio Recording (.wav) saved in recordings/
- Automatic user conversation memory retrieval from hello_api (.NET Backend)
- Automatic chat transcript & memory summary persistence to PostgreSQL
"""

import asyncio

# Ensure event loop exists on Python 3.12+ / 3.14+ in spawned child processes
try:
    asyncio.get_event_loop()
except RuntimeError:
    asyncio.set_event_loop(asyncio.new_event_loop())
import json
import logging
import os
import sys
import time
import wave
import aiohttp
from dotenv import load_dotenv

from livekit.agents import (
    AutoSubscribe,
    JobContext,
    JobProcess,
    WorkerOptions,
    cli,
    llm,
)
from livekit.agents.pipeline import VoicePipelineAgent
from livekit.plugins import groq, silero
from livekit import rtc

from personas.registry import (
    get_persona,
    get_tts_voice,
    DEFAULT_PERSONA_ID,
)
from edge_tts_wrapper import EdgeTTS
from llm_orchestrator import LlmOrchestrator
from tools_context import AiToolsContext

load_dotenv()

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s"
)
logger = logging.getLogger("ai_voice_agent")

HELLO_API_URL = os.getenv("HELLO_API_URL", "http://localhost:5063")
RECORDINGS_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "recordings")
os.makedirs(RECORDINGS_DIR, exist_ok=True)


def prewarm(proc: JobProcess):
    """Preload Silero VAD model into memory for zero cold-start delay."""
    logger.info("Prewarming Silero VAD model...")
    proc.userdata["vad"] = silero.VAD.load()
    logger.info("Silero VAD model prewarmed successfully.")


def extract_metadata(room_metadata: str, participant_metadata: str = None) -> dict:
    """Parse JSON metadata from Room or Participant."""
    meta = {}
    for raw in [participant_metadata, room_metadata]:
        if raw and raw.strip():
            try:
                parsed = json.loads(raw)
                if isinstance(parsed, dict):
                    meta.update(parsed)
            except Exception as e:
                logger.warning(f"Failed to parse metadata string '{raw}': {e}")
    return meta


async def fetch_user_memory(user_id: str, persona_id: str) -> dict:
    """Fetch previous conversation memory & context from hello_api for this user and persona."""
    if not user_id or not persona_id:
        return {"hasHistory": False, "memorySummary": "", "recentTurns": []}

    url = f"{HELLO_API_URL}/api/ai/memory?userId={user_id}&personaId={persona_id}"
    logger.info(f"[hello_api] Fetching memory: {url}")
    try:
        async with aiohttp.ClientSession() as session:
            async with session.get(url, timeout=aiohttp.ClientTimeout(total=4)) as resp:
                if resp.status == 200:
                    data = await resp.json()
                    logger.info(f"[hello_api] Memory loaded: hasHistory={data.get('hasHistory')}")
                    return data
    except Exception as e:
        logger.warning(f"[hello_api] Could not fetch memory: {e}")

    return {"hasHistory": False, "memorySummary": "", "recentTurns": []}


async def append_single_turn(call_id: str, user_id: str, persona_id: str, role: str, content: str):
    """Save an individual dialogue turn immediately to PostgreSQL as soon as spoken (Zero Data Loss)."""
    if not user_id or not content or not call_id:
        return
    url = f"{HELLO_API_URL}/api/ai/memory/append-turn"
    payload = {
        "callId": call_id,
        "userId": user_id,
        "personaId": persona_id,
        "role": role,
        "content": content
    }
    try:
        async with aiohttp.ClientSession() as session:
            async with session.post(url, json=payload, timeout=aiohttp.ClientTimeout(total=4)) as resp:
                if resp.status == 200:
                    logger.debug(f"[hello_api] Real-time turn saved: [{role}] {content[:40]}...")
    except Exception as e:
        logger.warning(f"[hello_api] Could not append turn in real-time: {e}")


async def generate_smart_memory_summary(turns: list, groq_api_key: str = "") -> str:
    """Uses Groq Llama to extract an intelligent, concise 1-sentence user profile memory."""
    if not turns or len(turns) < 2:
        user_turns = [t["content"] for t in turns if t.get("role") == "user"]
        return f"User discussed: {'; '.join(user_turns[-3:])}" if user_turns else ""

    if groq_api_key:
        try:
            recent_dialogue = "\n".join([f"{t.get('role', 'speaker')}: {t.get('content', '')}" for t in turns[-8:]])
            prompt = (
                "Analyze this voice call conversation transcript. Extract a 1-sentence concise memory summary "
                "of the user's primary topic, goal, progress, or personal context (for future call continuity):\n\n"
                f"{recent_dialogue}\n\n"
                "Output ONLY the single 1-sentence memory statement, without any quotation marks or preamble:"
            )
            async with aiohttp.ClientSession() as session:
                headers = {"Authorization": f"Bearer {groq_api_key}", "Content-Type": "application/json"}
                body = {
                    "model": os.getenv("GROQ_MODEL", "qwen/qwen3.8-27b"),
                    "messages": [{"role": "user", "content": prompt}],
                    "max_tokens": 70,
                    "temperature": 0.2
                }
                async with session.post("https://api.groq.com/openai/v1/chat/completions", headers=headers, json=body, timeout=aiohttp.ClientTimeout(total=3.5)) as resp:
                    if resp.status == 200:
                        data = await resp.json()
                        summary = data["choices"][0]["message"]["content"].strip()
                        logger.info(f"🧠 Groq generated smart memory summary: {summary}")
                        return summary
        except Exception as e:
            logger.warning(f"Could not generate LLM memory summary: {e}")

    user_turns = [t["content"] for t in turns if t.get("role") == "user"]
    return f"User discussed: {'; '.join(user_turns[-3:])}" if user_turns else ""


async def extract_structured_data(persona_id: str, turns: list, groq_api_key: str = "") -> dict:
    """Extracts high-value structured JSON data from the conversation based on persona role."""
    if not turns or len(turns) < 2 or not groq_api_key:
        return {}

    dialogue = "\n".join([f"{t.get('role', 'speaker')}: {t.get('content', '')}" for t in turns])

    if "cab_booking" in persona_id:
        system_instructions = (
            "You are a structured data extractor for Cab Bookings. "
            "Analyze the conversation and output a JSON object with this EXACT structure:\n"
            "{\n"
            '  "type": "cab_booking",\n'
            '  "booking_status": "confirmed" | "inquiry" | "cancelled",\n'
            '  "pickup_location": "string or empty",\n'
            '  "drop_location": "string or empty",\n'
            '  "cab_type": "mini" | "sedan" | "suv" | "unspecified",\n'
            '  "estimated_distance_km": number or 0,\n'
            '  "estimated_fare": number or 0,\n'
            '  "pickup_time": "string e.g. now / 5:00 PM",\n'
            '  "passenger_notes": "short note or empty"\n'
            "}"
        )
    elif "hr_job" in persona_id:
        system_instructions = (
            "You are a structured data extractor for HR Job Applications. "
            "Analyze the candidate screening conversation and output a JSON object with this EXACT structure:\n"
            "{\n"
            '  "type": "hr_job_application",\n'
            '  "candidate_name": "string or empty",\n'
            '  "applied_role": "string or empty",\n'
            '  "experience_years": "string e.g. 2 years",\n'
            '  "preferred_location": "string or empty",\n'
            '  "current_salary": "string or empty",\n'
            '  "expected_salary": "string or empty",\n'
            '  "notice_period": "string e.g. Immediate / 15 days",\n'
            '  "screening_status": "shortlisted" | "under_review" | "rejected",\n'
            '  "screening_notes": "1-2 sentence evaluation summary"\n'
            "}"
        )
    elif "hr_interview" in persona_id:
        system_instructions = (
            "You are a structured data extractor for Job Interview Evaluations. "
            "Analyze the interview conversation and output a JSON object with this EXACT structure:\n"
            "{\n"
            '  "type": "hr_interview",\n'
            '  "candidate_name": "string or empty",\n'
            '  "target_role": "string or empty",\n'
            '  "technical_score": integer from 1 to 10,\n'
            '  "communication_score": integer from 1 to 10,\n'
            '  "strengths": ["list of 1-3 key strengths observed"],\n'
            '  "areas_of_improvement": ["list of 1-3 areas to improve"],\n'
            '  "hiring_verdict": "hire" | "hold" | "reject",\n'
            '  "summary": "1-2 sentence evaluation takeaway"\n'
            "}"
        )
    else:
        system_instructions = (
            "You are a structured data extractor. Analyze the conversation and output a JSON object:\n"
            "{\n"
            '  "type": "general",\n'
            '  "key_topics": ["topic1", "topic2"],\n'
            '  "user_sentiment": "positive" | "neutral" | "negative",\n'
            '  "takeaways": "1 sentence key takeaway"\n'
            "}"
        )

    try:
        prompt = f"Transcript:\n{dialogue}\n\nOutput only valid JSON:"
        async with aiohttp.ClientSession() as session:
            headers = {"Authorization": f"Bearer {groq_api_key}", "Content-Type": "application/json"}
            body = {
                "model": os.getenv("GROQ_MODEL", "qwen/qwen3.8-27b"),
                "messages": [
                    {"role": "system", "content": system_instructions},
                    {"role": "user", "content": prompt}
                ],
                "response_format": {"type": "json_object"},
                "max_tokens": 300,
                "temperature": 0.1
            }
            async with session.post("https://api.groq.com/openai/v1/chat/completions", headers=headers, json=body, timeout=aiohttp.ClientTimeout(total=4)) as resp:
                if resp.status == 200:
                    data = await resp.json()
                    content = data["choices"][0]["message"]["content"]
                    parsed = json.loads(content)
                    logger.info(f"📊 Extracted structured {persona_id} data: {parsed}")
                    return parsed
    except Exception as e:
        logger.warning(f"Failed to extract structured data: {e}")

    return {}


async def save_structured_data(call_id: str, user_id: str, persona_id: str, structured_obj: dict):
    """Sends structured JSON output to hello_api for storage in PostgreSQL at call wrap-up."""
    if not call_id or not structured_obj:
        return
    url = f"{HELLO_API_URL}/api/ai/calls/{call_id}/structured-data"

    if "dataType" in structured_obj and "structuredJson" in structured_obj:
        data_type = structured_obj["dataType"]
        safe_json = structured_obj["structuredJson"]
        summary = structured_obj.get("summary", "")
    else:
        data_type = structured_obj.get("type", "general")
        summary = structured_obj.get("screening_notes") or structured_obj.get("summary") or structured_obj.get("passenger_notes") or structured_obj.get("takeaways") or ""
        safe_json = json.dumps(structured_obj)

    payload = {
        "callSessionId": call_id,
        "userId": user_id,
        "personaId": persona_id,
        "dataType": data_type,
        "structuredJson": safe_json,
        "summary": summary
    }
    try:
        async with aiohttp.ClientSession() as session:
            async with session.post(url, json=payload, timeout=aiohttp.ClientTimeout(total=5)) as resp:
                if resp.status == 200:
                    logger.info(f"[hello_api] End-of-call structured data saved for call {call_id} ({data_type})")
    except Exception as e:
        logger.error(f"[hello_api] Failed to save structured data: {e}")


async def save_user_memory(call_id: str, user_id: str, persona_id: str, turns: list, orchestrator: LlmOrchestrator = None, tools_ctx: AiToolsContext = None):
    """Save/update final call conversation summary and memory profile back to hello_api database."""
    if not user_id:
        return

    # 1. Generate compact 1-sentence memory summary using Orchestrator
    summary = ""
    if orchestrator:
        try:
            summary = await orchestrator.extract_compact_memory(turns)
        except Exception as e:
            logger.warning(f"Error extracting memory with orchestrator: {e}")

    if not summary:
        groq_api_key = orchestrator.api_key if orchestrator else os.getenv("GROQ_API_KEY", "")
        summary = await generate_smart_memory_summary(turns, groq_api_key)

    url = f"{HELLO_API_URL}/api/ai/memory/save"
    payload = {
        "callId": call_id,
        "userId": user_id,
        "personaId": persona_id,
        "turns": turns,
        "memorySummary": summary
    }
    logger.info(f"[hello_api] Updating conversation memory profile for user {user_id}: '{summary}'")
    try:
        async with aiohttp.ClientSession() as session:
            async with session.post(url, json=payload, timeout=aiohttp.ClientTimeout(total=5)) as resp:
                if resp.status == 200:
                    logger.info("[hello_api] Conversation memory profile updated successfully.")
    except Exception as e:
        logger.error(f"[hello_api] Failed to update conversation memory: {e}")

    # 2. Extract and save role-specific structured data (from In-Memory Tool Session or LLM Fallback)
    structured_payload = None
    if tools_ctx:
        structured_payload = tools_ctx.get_final_structured_payload(persona_id)
        if structured_payload:
            logger.info(f"📦 [Session] Utilizing in-memory tool session data for {persona_id}: {structured_payload['dataType']}")

    if not structured_payload:
        api_key = orchestrator.api_key if orchestrator else os.getenv("GROQ_API_KEY", "")
        structured_obj = await extract_structured_data(persona_id, turns, api_key)
        if structured_obj:
            structured_payload = structured_obj

    if structured_payload:
        await save_structured_data(call_id, user_id, persona_id, structured_payload)


async def report_call_end(call_id: str, duration_seconds: int, recording_file: str, llm_provider: str = "groq", llm_model: str = "qwen/qwen3.8-27b", latency_telemetry: dict = None):
    """Notify hello_api that the call has ended with duration, recording file, and LLM telemetry."""
    if not call_id:
        return
    url = f"{HELLO_API_URL}/api/ai/calls/{call_id}/end"
    payload = {
        "durationSeconds": duration_seconds,
        "recordingFile": recording_file,
        "llmProvider": llm_provider,
        "llmModel": llm_model,
        "latencyTelemetry": json.dumps(latency_telemetry or {
            "stt_engine": "whisper-large-v3",
            "tts_engine": "edge-neural",
            "sample_rate": 24000
        }),
    }
    logger.info(f"[hello_api] Reporting call end: {url} -> {payload}")
    try:
        async with aiohttp.ClientSession() as session:
            async with session.post(url, json=payload, timeout=aiohttp.ClientTimeout(total=5)) as resp:
                if resp.status == 200:
                    logger.info(f"[hello_api] Call {call_id} duration successfully logged.")
    except Exception as e:
        logger.error(f"[hello_api] Failed to report call end: {e}")


class AudioRecorder:
    """Records incoming participant audio frames into a 16-bit 24kHz mono WAV file in real time directly on disk."""
    def __init__(self, output_path: str, sample_rate: int = 24000):
        self.output_path = output_path
        self.sample_rate = sample_rate
        self._wf = None
        self._is_recording = False
        self._total_bytes = 0

    def start(self):
        try:
            self._wf = wave.open(self.output_path, "wb")
            self._wf.setnchannels(1)
            self._wf.setsampwidth(2)  # 16-bit
            self._wf.setframerate(self.sample_rate)
            self._is_recording = True
            logger.info(f"🎙️ Audio recorder opened file: {self.output_path}")
        except Exception as e:
            logger.error(f"Failed to open recording file: {e}")

    def write_frame(self, frame: rtc.AudioFrame):
        if self._is_recording and self._wf and frame.data:
            try:
                self._wf.writeframes(frame.data)
                self._total_bytes += len(frame.data)
            except Exception as e:
                logger.error(f"Error writing audio frame: {e}")

    def stop_and_save(self):
        self._is_recording = False
        if self._wf:
            try:
                self._wf.close()
                self._wf = None
                logger.info(f"🎙️ Call recording successfully finalized: {self.output_path} ({self._total_bytes} bytes)")
            except Exception as e:
                logger.error(f"Error finalizing audio recording: {e}")


async def entrypoint(ctx: JobContext):
    """LiveKit worker job entry point for handling incoming AI voice call."""
    logger.info(f"Connecting to LiveKit Room: {ctx.room.name}")
    await ctx.connect(auto_subscribe=AutoSubscribe.AUDIO_ONLY)

    # Wait for the user participant to join
    participant = await ctx.wait_for_participant()
    logger.info(f"User joined call: {participant.identity}")

    start_time = time.time()

    # Extract user selections (Persona, Language, Gender, CallId, CallerId)
    meta = extract_metadata(ctx.room.metadata, participant.metadata)
    call_id = meta.get("call_id") or ctx.room.name.replace("ai_call_", "")
    caller_id = meta.get("caller_id") or participant.identity
    clean_caller_id = caller_id.replace("admin_", "") if caller_id else ""
    persona_id = meta.get("persona_id", DEFAULT_PERSONA_ID)
    language = meta.get("language", "hinglish")
    gender = meta.get("gender", "female")

    persona = get_persona(persona_id)
    system_prompt = persona.get_system_prompt(language)
    tts_voice = get_tts_voice(language, gender)
    greeting_text = persona.get_greeting(language)

    # Initialize LLM Orchestrator
    groq_api_key = os.getenv("GROQ_API_KEY", "")
    if not groq_api_key:
        logger.error("GROQ_API_KEY is missing in ai-agent/.env!")
    groq_model = os.getenv("GROQ_MODEL", "qwen/qwen3.8-27b")
    orchestrator = LlmOrchestrator(default_model=groq_model, api_key=groq_api_key)

    # Dynamic TTS Engine & Persona Pitch/Rate with user override support
    tts_provider = meta.get("tts_provider") or os.getenv("DEFAULT_TTS_PROVIDER", "edge")
    active_pitch = meta.get("pitch") or getattr(persona, "pitch", "+0Hz")
    active_rate = meta.get("rate") or getattr(persona, "rate", "+0%")

    # Fetch User Long-Term Memory
    memory_data = await fetch_user_memory(clean_caller_id, persona.id)
    if memory_data.get("hasHistory") and memory_data.get("memorySummary"):
        summary = memory_data["memorySummary"]
        total_prev_calls = memory_data.get("totalCalls", 1)
        system_prompt += (
            f"\n\nPAST USER MEMORY & PREVIOUS CALL CONTEXT:\n"
            f"You have spoken with this user {total_prev_calls} time(s) before.\n"
            f"Key things remembered from past conversations: {summary}\n\n"
            "INSTRUCTION FOR CONTINUITY: Greet the user warmly acknowledging your past conversations. "
            "Refer naturally to their previous topics or progress if appropriate, making them feel remembered and valued."
        )

    logger.info("==================================================")
    logger.info(f"📞 Call ID         : {call_id}")
    logger.info(f"👤 Caller ID       : {caller_id} (Clean: {clean_caller_id})")
    logger.info(f"🤖 Active Persona  : {persona.name} ({persona.id})")
    logger.info(f"🧠 Has Memory      : {memory_data.get('hasHistory', False)}")
    logger.info(f"🌐 Active Language : {language}")
    logger.info(f"👥 Active Gender   : {gender}")
    logger.info(f"🎙️ TTS Provider    : {tts_provider.upper()} ({tts_voice})")
    logger.info(f"🎚️ Pitch / Rate    : {active_pitch} / {active_rate}")
    logger.info(f"🔥 LLM Model / Temp: {orchestrator.active_model} / {persona.temperature}")
    logger.info("==================================================")

    # Setup Audio Recording
    recording_filename = f"{call_id}.wav"
    recording_filepath = os.path.join(RECORDINGS_DIR, recording_filename)
    recorder = AudioRecorder(recording_filepath, sample_rate=24000)
    recorder.start()

    # Setup VAD (Silero)
    vad = ctx.proc.userdata.get("vad") or silero.VAD.load()

    # Setup STT (Groq Whisper Turbo for ultra-fast response)
    stt = groq.STT(
        model="whisper-large-v3-turbo",
        api_key=groq_api_key,
    )

    # Setup LLM via Orchestrator (Sub-200ms streaming)
    llm_instance = orchestrator.get_livekit_llm(temperature=persona.temperature)

    # Setup Dynamic TTS (Edge-TTS / Kokoro-82M / Sarvam AI)
    from tts_factory import create_tts_engine
    tts_instance = create_tts_engine(
        provider=tts_provider,
        voice=tts_voice,
        pitch=active_pitch,
        rate=active_rate,
        language=language,
        gender=gender,
    )

    # Setup Initial Chat Context with Persona System Prompt & Recent Memory Turns
    initial_chat_ctx = llm.ChatContext()
    initial_chat_ctx.append(
        role="system",
        text=system_prompt,
    )

    # Pre-populate last 4-6 turns from previous call if available
    recent_turns = memory_data.get("recentTurns", [])
    if recent_turns:
        for turn in recent_turns[-6:]:
            role = turn.get("role")
            content = turn.get("content")
            if role in ["user", "assistant"] and content:
                initial_chat_ctx.append(role=role, text=content)

    # Initialize Autonomous Live Tools Engine
    tools_ctx = AiToolsContext(
        call_session_id=call_id,
        user_id=clean_caller_id,
        api_base_url=HELLO_API_URL
    )

    # Assemble VoicePipelineAgent with sub-220ms endpointing delay, preemptive synthesis & tools
    agent = VoicePipelineAgent(
        vad=vad,
        stt=stt,
        llm=llm_instance,
        tts=tts_instance,
        fnc_ctx=tools_ctx,
        chat_ctx=initial_chat_ctx,
        allow_interruptions=True,
        interrupt_speech_duration=0.18,
        min_endpointing_delay=0.22,
        preemptive_synthesis=True,
        max_nested_fnc_calls=2,
    )

    # Live Subtitles: broadcast speech text over WebRTC Data Channel
    async def _broadcast_caption(role: str, text: str):
        if not text or not text.strip():
            return
        clean = text.strip()
        if clean.startswith("PAST USER MEMORY") or clean.startswith("System:"):
            return
        try:
            payload = json.dumps({"type": "caption", "role": role, "text": clean}).encode("utf-8")
            await ctx.room.local_participant.publish_data(payload, reliable=True)
        except Exception as e:
            logger.debug(f"Caption publish notice: {e}")

    @agent.on("user_speech_committed")
    def on_user_speech(msg: llm.ChatMessage):
        text = msg.content if isinstance(msg.content, str) else str(msg.content)
        asyncio.create_task(_broadcast_caption("user", text))

    @agent.on("agent_speech_committed")
    def on_agent_speech(msg: llm.ChatMessage):
        text = msg.content if isinstance(msg.content, str) else str(msg.content)
        asyncio.create_task(_broadcast_caption("assistant", text))

    # Record participant audio track
    @ctx.room.on("track_subscribed")
    def on_track_subscribed(track: rtc.Track, publication: rtc.TrackPublication, p: rtc.RemoteParticipant):
        if track.kind == rtc.TrackKind.KIND_AUDIO:
            logger.info(f"Subscribed to audio track {track.sid}. Recording started.")
            audio_stream = rtc.AudioStream(track)

            async def _record_loop():
                async for event in audio_stream:
                    recorder.write_frame(event.frame)

            asyncio.create_task(_record_loop())

    agent.start(ctx.room, participant)
    logger.info("VoicePipelineAgent started.")

    # Speak initial greeting: if returning caller with history, use dynamic personalized 1-sentence greeting!
    if memory_data.get("hasHistory") and memory_data.get("memorySummary"):
        try:
            logger.info(f"Generating personalized opening greeting based on memory: '{memory_data['memorySummary']}'")
            custom_greeting = await orchestrator.generate_personalized_greeting(
                persona_name=persona.name,
                persona_role=persona.role,
                memory_summary=memory_data["memorySummary"],
                language=language
            )
            if custom_greeting and len(custom_greeting) > 5:
                greeting_text = custom_greeting
        except Exception as e:
            logger.warning(f"Error generating personalized greeting, falling back: {e}")

    logger.info(f"Speaking greeting: '{greeting_text}'")
    await agent.say(greeting_text, allow_interruptions=True)
    asyncio.create_task(_broadcast_caption("assistant", greeting_text))

    # Wait until user leaves or room disconnects
    disconnect_event = asyncio.Event()

    # Real-time incremental dialogue synchronizer (Every 3 seconds)
    # Guarantees that even if network cuts, phone dies, or call drops abruptly,
    # NOT A SINGLE WORD is lost from a 10-minute call!
    async def _realtime_dialogue_sync():
        last_synced_idx = 0
        while not disconnect_event.is_set():
            try:
                await asyncio.sleep(2.5)
                msgs = list(agent.chat_ctx.messages)
                while last_synced_idx < len(msgs):
                    msg = msgs[last_synced_idx]
                    last_synced_idx += 1
                    if msg.role in ["user", "assistant"]:
                        text = msg.content if isinstance(msg.content, str) else str(msg.content)
                        if text.strip() and not text.startswith("PAST USER MEMORY") and not text.startswith("System"):
                            await append_single_turn(call_id, caller_id, persona.id, msg.role, text.strip())
            except asyncio.CancelledError:
                break
            except Exception as e:
                logger.warning(f"Error in realtime dialogue sync: {e}")

    sync_task = asyncio.create_task(_realtime_dialogue_sync())

    @ctx.room.on("participant_disconnected")
    def on_participant_disconnected(p: rtc.RemoteParticipant):
        if p.identity == participant.identity:
            logger.info(f"User {p.identity} disconnected from call.")
            disconnect_event.set()

    @ctx.room.on("disconnected")
    def on_room_disconnected():
        logger.info("LiveKit room disconnected.")
        disconnect_event.set()

    try:
        await disconnect_event.wait()
    finally:
        # Cancel background sync and do final pass
        sync_task.cancel()

        duration_seconds = max(1, int(time.time() - start_time))
        logger.info(f"Call finished. Duration: {duration_seconds} seconds.")
        recorder.stop_and_save()

        # Extract all conversation turns from agent.chat_ctx
        collected_turns = []
        for msg in agent.chat_ctx.messages:
            if msg.role in ["user", "assistant"]:
                text = msg.content if isinstance(msg.content, str) else str(msg.content)
                if text.strip() and not text.startswith("PAST USER MEMORY"):
                    collected_turns.append({"role": msg.role, "content": text.strip()})

        # Update long-term memory summary in hello_api database
        await save_user_memory(call_id, clean_caller_id, persona.id, collected_turns, orchestrator, tools_ctx=tools_ctx)

        # Report duration, recording, and active LLM provider to hello_api backend
        await report_call_end(
            call_id,
            duration_seconds,
            recording_filename,
            llm_provider=orchestrator.active_provider,
            llm_model=orchestrator.active_model,
            latency_telemetry={
                "stt_engine": "whisper-large-v3-turbo",
                "tts_engine": f"{tts_provider}-cached",
                "sample_rate": 24000
            }
        )


if __name__ == "__main__":
    cli.run_app(
        WorkerOptions(
            entrypoint_fnc=entrypoint,
            prewarm_fnc=prewarm,
        )
    )
