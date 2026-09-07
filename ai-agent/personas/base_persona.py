"""
base_persona.py - Base Abstract Class for all AI Voice Personas.
Defines common interface, prompt compilation, language injection, and conversational constraints.
"""

from abc import ABC, abstractmethod
from typing import Dict, List, Optional

LANGUAGE_INSTRUCTIONS = {
    "hindi": (
        "LANGUAGE RULE: You MUST speak entirely in natural, polite Hindi. "
        "Use respectful phrasing. Do NOT use English sentences unless necessary for technical words."
    ),
    "english": (
        "LANGUAGE RULE: You MUST speak in clear, fluent Indian English. "
        "Keep the rhythm natural, friendly, and clear."
    ),
    "hinglish": (
        "LANGUAGE RULE: You MUST speak in natural, everyday conversational Hinglish (a seamless blend of Hindi and English "
        "commonly used in Indian daily life, written in Roman script for TTS). "
        "Example style: 'Bilkul, main aapki help kar sakta hoon. Let's discuss this step by step.'"
    )
}

VOICE_CHAT_CONSTRAINTS = (
    "CRITICAL SPOKEN PHONE CALL RULES (REAL HUMAN ILLUSION):\n"
    "1. ULTRA-CONCISE & FAST: Speak ONLY 1 to 2 short, crisp sentences (strictly under 12-18 words). Never speak in paragraphs, never lecture.\n"
    "2. HUMAN PHONE MANNERISMS: Sound like a warm, alert, real human friend or phone dispatcher. Use natural Indian openers ('Haan ji!', 'Achha theek hai', 'Bilkul!', 'Arre waah!', 'Ji samajh gayi!').\n"
    "3. ACTIVE TURN-TAKING: Ask exactly ONE question at a time so the caller has immediate room to reply.\n"
    "4. NO AI SOUNDING BOILERPLATE: Never say 'As an AI...', 'How may I assist you further', or sound like a scripted IVR machine.\n"
    "5. PURE SPOKEN WORDS: No asterisks (*), stage directions, emojis, hashtags, bullet points, or markdown. Output only clean natural spoken speech."
)


class BasePersona(ABC):
    def __init__(
        self,
        persona_id: str,
        name: str,
        role: str,
        category: str,
        temperature: float = 0.6,
        base_prompt: str = "",
        stages: Optional[List[str]] = None,
        greetings: Optional[Dict[str, str]] = None,
        pitch: str = "+0Hz",
        rate: str = "+10%",
    ):
        self.id = persona_id
        self.name = name
        self.role = role
        self.category = category
        self.temperature = temperature
        self.base_prompt = base_prompt
        self.stages = stages or []
        self.greetings = greetings or {}
        self.pitch = pitch
        self.rate = rate

    def get_greeting(self, language: str = "hinglish") -> str:
        lang_key = (language or "hinglish").strip().lower()
        if lang_key in self.greetings:
            return self.greetings[lang_key]
        return self.greetings.get("hinglish", f"Hello! Welcome to {self.name}. How are you doing today?")

    def get_system_prompt(self, language: str = "hinglish") -> str:
        lang_key = (language or "hinglish").strip().lower()
        lang_rule = LANGUAGE_INSTRUCTIONS.get(lang_key, LANGUAGE_INSTRUCTIONS["hinglish"])

        stages_text = ""
        if self.stages:
            stages_formatted = "\n".join([f"- {s}" for s in self.stages])
            stages_text = f"\n\nCONVERSATION PHASES / STAGES:\n{stages_formatted}"

        return (
            f"{self.base_prompt}"
            f"{stages_text}\n\n"
            f"{lang_rule}\n\n"
            f"{VOICE_CHAT_CONSTRAINTS}"
        )
