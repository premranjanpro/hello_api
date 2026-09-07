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
    "CRITICAL SPOKEN PHONE CALL RULES:\n"
    "1. SPEED & BREVITY: Keep EVERY response ultra-concise: 1 to 2 short, punchy sentences maximum (strictly under 15-20 words). Never speak paragraphs.\n"
    "2. SPOKEN INDIAN CADENCE: Begin answers naturally like a real human on a phone call ('Haan ji!', 'Bilkul!', 'Arre waah!', 'Achha ji!', 'Ji zaroor!').\n"
    "3. ACTIVE LISTENING & DIALOGUE: Ask only ONE simple question at a time so the caller has immediate space to respond.\n"
    "4. NO TEXT ARTIFACTS: NEVER use asterisks (*), stage directions, emojis, hashtags, bullet points, or markdown. Output pure clean speech."
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
        rate: str = "+0%",
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
