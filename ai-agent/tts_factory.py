"""
tts_factory.py - Multi-Engine Audio Synthesis Provider Factory.
Supports:
1. Edge-TTS (Default, Free, Fast Neural Voices with custom Pitch/Rate)
2. Kokoro-82M (Open-Source, CPU-Optimized, Natural Human Cadence)
3. Sarvam AI Bulbul (Dedicated Indian Hindi/Hinglish/Regional Voice Model API)

User/Admin can configure the active provider via:
- Call metadata (tts_provider: 'edge' | 'kokoro' | 'sarvam')
- Environment variable: DEFAULT_TTS_PROVIDER=edge
- Dynamic Pitch/Rate adjustments (e.g. -5Hz / -10% for romantic persona)
"""

import os
import io
import json
import base64
import logging
import asyncio
import uuid
from typing import Optional
import aiohttp
from pydub import AudioSegment
import edge_tts

from livekit.agents import tts
from livekit import rtc
from edge_tts_wrapper import EdgeTTS

logger = logging.getLogger("tts_factory")


# =========================================================================
# 1. Sarvam AI (Bulbul) TTS Provider
# =========================================================================
class SarvamTTS(tts.TTS):
    """
    LiveKit TTS implementation for Sarvam AI (Bulbul) API.
    Designed specifically for expressive Indian languages (Hindi, Hinglish, Bengali, Tamil, etc.)
    """

    def __init__(
        self,
        api_key: str,
        target_language_code: str = "hi-IN",
        speaker: str = "meera",
        pace: float = 1.0,
        pitch: float = 0.0,
    ):
        super().__init__(
            capabilities=tts.TTSCapabilities(streaming=False),
            sample_rate=24000,
            num_channels=1,
        )
        self.api_key = api_key
        self.target_language_code = target_language_code
        self.speaker = speaker
        self.pace = pace
        self.pitch = pitch

    def synthesize(self, text: str) -> "tts.ChunkedStream":
        return SarvamChunkedStream(self, text)


class SarvamChunkedStream(tts.ChunkedStream):
    def __init__(self, tts_instance: SarvamTTS, text: str):
        super().__init__(tts=tts_instance, input_text=text)
        self._tts = tts_instance
        self._text = text
        self._request_id = str(uuid.uuid4())

    async def _run(self) -> None:
        if not self._text.strip():
            return

        url = "https://api.sarvam.ai/text-to-speech"
        headers = {
            "api-subscription-key": self._tts.api_key,
            "Content-Type": "application/json",
        }
        payload = {
            "inputs": [self._text.strip()],
            "target_language_code": self._tts.target_language_code,
            "speaker": self._tts.speaker,
            "pitch": self._tts.pitch,
            "pace": self._tts.pace,
            "loudness": 1.2,
            "speech_sample_rate": 24000,
            "enable_preprocessing": True,
            "model": "bulbul:v1",
        }

        try:
            async with aiohttp.ClientSession() as session:
                async with session.post(url, headers=headers, json=payload, timeout=aiohttp.ClientTimeout(total=8)) as resp:
                    if resp.status != 200:
                        err_text = await resp.text()
                        logger.error(f"[SarvamTTS] API Error {resp.status}: {err_text}")
                        return
                    data = await resp.json()
                    audios = data.get("audios", [])
                    if not audios:
                        return
                    wav_bytes = base64.b64decode(audios[0])

            audio_seg = AudioSegment.from_file(io.BytesIO(wav_bytes), format="wav")
            audio_seg = audio_seg.set_frame_rate(24000).set_channels(1).set_sample_width(2)

            raw_pcm = audio_seg.raw_data
            chunk_size_samples = 480  # 20ms @ 24kHz
            chunk_size_bytes = chunk_size_samples * 2

            offset = 0
            while offset < len(raw_pcm):
                chunk_bytes = raw_pcm[offset : offset + chunk_size_bytes]
                chunk_samples = len(chunk_bytes) // 2
                frame = rtc.AudioFrame(
                    data=chunk_bytes,
                    sample_rate=24000,
                    num_channels=1,
                    samples_per_channel=chunk_samples,
                )
                self._event_ch.send_nowait(
                    tts.SynthesizedAudio(
                        request_id=self._request_id,
                        frame=frame,
                    )
                )
                offset += chunk_size_bytes
                await asyncio.sleep(0.001)

        except Exception as e:
            logger.error(f"[SarvamTTS] Synthesis failed: {e}")


# =========================================================================
# 2. Kokoro-82M Open-Source TTS Provider
# =========================================================================
class KokoroTTS(tts.TTS):
    """
    LiveKit TTS implementation backed by Kokoro-82M (Apache 2.0).
    Runs lightweight ONNX/PyTorch model on CPU with near-ElevenLabs quality.
    """

    def __init__(self, voice: str = "af_heart", speed: float = 1.0):
        super().__init__(
            capabilities=tts.TTSCapabilities(streaming=False),
            sample_rate=24000,
            num_channels=1,
        )
        self.voice = voice
        self.speed = speed
        self._pipeline = None
        self._init_kokoro()

    def _init_kokoro(self):
        try:
            from kokoro import KPipeline
            self._pipeline = KPipeline(lang_code="a")
            logger.info(f"[KokoroTTS] Initialized KPipeline with voice: {self.voice}")
        except Exception as e:
            logger.warning(f"[KokoroTTS] Kokoro not fully installed: {e}. Will fallback if invoked.")

    def synthesize(self, text: str) -> "tts.ChunkedStream":
        return KokoroChunkedStream(self, text)


class KokoroChunkedStream(tts.ChunkedStream):
    def __init__(self, tts_instance: KokoroTTS, text: str):
        super().__init__(tts=tts_instance, input_text=text)
        self._tts = tts_instance
        self._text = text
        self._request_id = str(uuid.uuid4())

    async def _run(self) -> None:
        if not self._text.strip():
            return

        if self._tts._pipeline is None:
            # Fallback to EdgeTTS dynamically
            logger.info("[KokoroTTS] Pipeline unavailable, dynamically falling back to EdgeTTS.")
            fallback = EdgeTTS()
            stream = fallback.synthesize(self._text)
            async for event in stream:
                self._event_ch.send_nowait(event)
            return

        try:
            import numpy as np
            loop = asyncio.get_running_loop()

            def _generate():
                generator = self._tts._pipeline(self._text, voice=self._tts.voice, speed=self._tts.speed)
                all_audio = []
                for _, _, audio in generator:
                    all_audio.append(audio)
                if all_audio:
                    return np.concatenate(all_audio)
                return np.array([], dtype=np.float32)

            audio_data = await loop.run_in_executor(None, _generate)
            if len(audio_data) == 0:
                return

            # Convert float32 [-1, 1] to int16 PCM
            pcm_data = (np.clip(audio_data, -1.0, 1.0) * 32767).astype(np.int16).tobytes()

            chunk_size_samples = 480
            chunk_size_bytes = chunk_size_samples * 2
            offset = 0
            while offset < len(pcm_data):
                chunk_bytes = pcm_data[offset : offset + chunk_size_bytes]
                chunk_samples = len(chunk_bytes) // 2
                frame = rtc.AudioFrame(
                    data=chunk_bytes,
                    sample_rate=24000,
                    num_channels=1,
                    samples_per_channel=chunk_samples,
                )
                self._event_ch.send_nowait(
                    tts.SynthesizedAudio(
                        request_id=self._request_id,
                        frame=frame,
                    )
                )
                offset += chunk_size_bytes
                await asyncio.sleep(0.001)

        except Exception as e:
            logger.error(f"[KokoroTTS] Synthesis error: {e}")


# =========================================================================
# 3. Unified Factory Resolver
# =========================================================================
def create_tts_engine(
    provider: Optional[str] = None,
    voice: Optional[str] = None,
    pitch: Optional[str] = None,
    rate: Optional[str] = None,
    language: str = "hinglish",
    gender: str = "female",
) -> tts.TTS:
    """
    Creates and returns the requested TTS engine based on user preference or defaults.
    Supported providers: 'edge' (default), 'kokoro', 'sarvam'
    """
    chosen_provider = (provider or os.getenv("DEFAULT_TTS_PROVIDER", "edge")).strip().lower()
    active_pitch = pitch or "+0Hz"
    active_rate = rate or "+0%"

    logger.info(f"[TTSFactory] Creating TTS engine: '{chosen_provider}' (Pitch: {active_pitch}, Rate: {active_rate})")

    # 1. Sarvam AI
    if chosen_provider == "sarvam":
        sarvam_key = os.getenv("SARVAM_API_KEY", "").strip()
        if sarvam_key:
            speaker = "meera" if gender == "female" else "dhruv"
            pace_val = 0.9 if "-10%" in active_rate else (1.1 if "+8%" in active_rate else 1.0)
            pitch_val = -0.5 if "-5Hz" in active_pitch else (0.8 if "+20Hz" in active_pitch else 0.0)
            logger.info(f"[TTSFactory] Initialized Sarvam AI Bulbul (Speaker: {speaker}, Pace: {pace_val}, Pitch: {pitch_val})")
            return SarvamTTS(
                api_key=sarvam_key,
                target_language_code="hi-IN",
                speaker=speaker,
                pace=pace_val,
                pitch=pitch_val,
            )
        else:
            logger.warning("[TTSFactory] SARVAM_API_KEY is missing in ai-agent/.env! Gracefully falling back to EdgeTTS.")

    # 2. Kokoro-82M
    elif chosen_provider == "kokoro":
        try:
            import kokoro
            kokoro_voice = "af_heart" if gender == "female" else "am_adam"
            speed_val = 0.9 if "-10%" in active_rate else (1.1 if "+8%" in active_rate else 1.0)
            logger.info(f"[TTSFactory] Initialized Kokoro-82M (Voice: {kokoro_voice}, Speed: {speed_val})")
            return KokoroTTS(voice=kokoro_voice, speed=speed_val)
        except ImportError:
            logger.warning("[TTSFactory] 'kokoro' package not installed in venv! Gracefully falling back to EdgeTTS.")

    # 3. Microsoft Edge-TTS (Default Free)
    default_voice = voice or ("hi-IN-SwaraNeural" if gender == "female" else "hi-IN-MadhurNeural")
    logger.info(f"[TTSFactory] Initialized EdgeTTS (Voice: {default_voice}, Pitch: {active_pitch}, Rate: {active_rate})")
    return EdgeTTS(
        voice=default_voice,
        pitch=active_pitch,
        rate=active_rate,
    )
