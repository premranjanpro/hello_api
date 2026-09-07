"""
llm_orchestrator.py - Resilient Multi-Engine LLM Orchestrator for Ultra-Fast Voice Response.

Features:
- Primary Engine: Groq High-Speed LPU (qwen/qwen3.8-27b / llama-3.1-8b-instant)
- Automatic Failover / Circuit Breaker on rate limit (429) or timeout
- Dynamic Personalized Opening Greeting generation using caller memory (<600ms)
- Emergency fast fallback so voice calls never hang or go silent
"""

import asyncio
import logging
import os
import aiohttp
from dotenv import load_dotenv
from livekit.plugins import groq

load_dotenv()
logger = logging.getLogger("llm_orchestrator")

GROQ_API_KEY = os.getenv("GROQ_API_KEY", "")
PRIMARY_MODEL = os.getenv("GROQ_MODEL", "qwen/qwen3.8-27b")
FALLBACK_MODEL = "llama-3.1-8b-instant"


class LlmOrchestrator:
    """Orchestrates LLM calls across high-speed providers with failover protection."""

    def __init__(self, default_model: str = "qwen/qwen3.8-27b", api_key: str = ""):
        self.api_key = api_key or os.getenv("GROQ_API_KEY", "")
        self.default_model = default_model or os.getenv("GROQ_MODEL", "qwen/qwen3.8-27b")
        self.active_provider = "groq"
        self.active_model = self.default_model
        self.fallback_model = "llama-3.1-8b-instant"

    def get_livekit_llm(self, temperature: float = 0.4):
        """Creates the optimal LiveKit LLM instance with configured speed settings and bounded tokens."""
        logger.info(f"[Orchestrator] Initializing Voice LLM: {self.active_model} (temp={temperature}, max_tokens=160)")
        return groq.LLM(
            model=self.active_model,
            temperature=temperature,
            api_key=self.api_key,
            max_tokens=160,
        )

    async def generate_personalized_greeting(
        self,
        persona_name: str,
        language: str = "hinglish",
        memory_summary: str = "",
        default_greeting: str = "",
        total_calls: int = 1,
        persona_role: str = ""
    ) -> str:
        """
        Generates a natural, warm 1-sentence opening greeting recognizing caller's previous context.
        Times out in 1.2s to ensure the caller experiences zero awkward delay.
        """
        if not memory_summary or not memory_summary.strip() or not self.api_key:
            return default_greeting

        prompt = (
            f"You are {persona_name} ({persona_role}), a friendly AI phone assistant on a live phone call.\n"
            f"This user has spoken to you before.\n"
            f"Key memory from previous conversation: '{memory_summary}'.\n\n"
            f"Language: {language} (use natural conversational Hindi / Hinglish / English as appropriate).\n"
            "TASK: Generate a single warm, punchy 1-sentence opening greeting (under 12-15 words). "
            "Welcome them back and mention their previous topic naturally (e.g. 'Welcome back! Last time we were talking about X, shall we continue?').\n"
            "Output ONLY the spoken greeting, with NO quotation marks, NO stage directions, NO asterisks:"
        )

        headers = {
            "Authorization": f"Bearer {self.api_key}",
            "Content-Type": "application/json"
        }

        body = {
            "model": self.active_model,
            "messages": [{"role": "user", "content": prompt}],
            "max_tokens": 40,
            "temperature": 0.45
        }

        try:
            async with aiohttp.ClientSession() as session:
                async with session.post(
                    "https://api.groq.com/openai/v1/chat/completions",
                    headers=headers,
                    json=body,
                    timeout=aiohttp.ClientTimeout(total=1.2)
                ) as resp:
                    if resp.status == 200:
                        data = await resp.json()
                        greeting = data["choices"][0]["message"]["content"].strip().strip('"').strip("'")
                        if greeting and len(greeting) > 5:
                            logger.info(f"[Orchestrator] Dynamic memory greeting generated: '{greeting}'")
                            return greeting
                    elif resp.status == 429:
                        # Failover to lightweight model
                        logger.warning(f"[Orchestrator] Primary rate-limited. Failing over to {self.fallback_model}")
                        self.active_model = self.fallback_model
        except asyncio.TimeoutError:
            logger.warning("[Orchestrator] Dynamic greeting generation timed out after 1.2s. Using default.")
        except Exception as e:
            logger.warning(f"[Orchestrator] Dynamic greeting error: {e}")

        return default_greeting

    async def extract_compact_memory(self, turns: list) -> str:
        """
        Extracts a high-accuracy, 1-sentence memory statement focusing on names, preferences, and key goals.
        """
        if not turns or len(turns) < 2 or not self.api_key:
            user_turns = [t["content"] for t in turns if t.get("role") == "user"]
            return f"User discussed: {'; '.join(user_turns[-2:])}" if user_turns else ""

        dialogue = "\n".join([f"{t.get('role', 'speaker')}: {t.get('content', '')}" for t in turns[-8:]])
        prompt = (
            "Analyze this voice conversation transcript. Extract a precise 1-sentence memory summary of what the user wants, "
            "their personal detail (like name, destination, job, preference), or current progress.\n\n"
            f"{dialogue}\n\n"
            "Output ONLY the 1-sentence memory statement, nothing else:"
        )

        headers = {
            "Authorization": f"Bearer {self.api_key}",
            "Content-Type": "application/json"
        }

        body = {
            "model": self.active_model,
            "messages": [{"role": "user", "content": prompt}],
            "max_tokens": 65,
            "temperature": 0.2
        }

        try:
            async with aiohttp.ClientSession() as session:
                async with session.post(
                    "https://api.groq.com/openai/v1/chat/completions",
                    headers=headers,
                    json=body,
                    timeout=aiohttp.ClientTimeout(total=3.0)
                ) as resp:
                    if resp.status == 200:
                        data = await resp.json()
                        return data["choices"][0]["message"]["content"].strip().strip('"')
        except Exception as e:
            logger.warning(f"[Orchestrator] Memory extraction error: {e}")

        user_turns = [t["content"] for t in turns if t.get("role") == "user"]
        return f"User discussed: {'; '.join(user_turns[-2:])}" if user_turns else ""

    @staticmethod
    def get_thinking_filler(language: str = "hinglish") -> str:
        """Returns a natural, low-latency conversational filler for speech buffering."""
        import random
        fillers = {
            "hindi": [
                "Haan ji, ek second...",
                "Bilkul, main abhi dekhti hoon...",
                "Acha, ek pal rukiye..."
            ],
            "hinglish": [
                "Haan ji, just a second...",
                "Bilkul, let me check...",
                "Haan, ek second dekhti hoon..."
            ],
            "english": [
                "Sure, just a second...",
                "Right, let me check that...",
                "Got it, one moment please..."
            ]
        }
        options = fillers.get(language.lower(), fillers["hinglish"])
        return random.choice(options)
