"""
registry.py - Dynamic Persona Registry and Voice Resolution Hub.
Provides lookup for all modular personas and maps language/gender to optimal Edge-TTS neural voices.
"""

from typing import Dict, List
from .base_persona import BasePersona
from .persona_role_hr_interview import HrInterviewPersona
from .persona_role_kids_learning import KidsLearningPersona
from .persona_role_romantic_chat import RomanticChatPersona
from .persona_role_parents_care import ParentsCarePersona
from .persona_role_english_tutor import EnglishTutorPersona
from .persona_role_cab_booking import CabBookingPersona
from .persona_role_hr_job import HrJobScreeningPersona

# Primary Registry mapping with persona_role_* IDs
PERSONA_REGISTRY: Dict[str, BasePersona] = {
    "persona_role_cab_booking": CabBookingPersona(),
    "persona_role_hr_job": HrJobScreeningPersona(),
    "persona_role_hr_interview": HrInterviewPersona(),
    "persona_role_english_tutor": EnglishTutorPersona(),
    "persona_role_kids_learning": KidsLearningPersona(),
    "persona_role_romantic_chat": RomanticChatPersona(),
    "persona_role_parents_care": ParentsCarePersona(),
}

# Aliases for backwards compatibility
PERSONA_ALIASES = {
    "cab_booking": "persona_role_cab_booking",
    "cab": "persona_role_cab_booking",
    "hr_job": "persona_role_hr_job",
    "hr_screening": "persona_role_hr_job",
    "hr_interview": "persona_role_hr_interview",
    "kids_learning": "persona_role_kids_learning",
    "romantic": "persona_role_romantic_chat",
    "parents_care": "persona_role_parents_care",
    "english_tutor": "persona_role_english_tutor",
}

DEFAULT_PERSONA_ID = "persona_role_hr_interview"

# Free Neural Voices mapping from Microsoft Edge-TTS
EDGE_TTS_VOICES = {
    "hindi": {
        "female": "hi-IN-SwaraNeural",
        "male": "hi-IN-MadhurNeural",
    },
    "hinglish": {
        "female": "hi-IN-SwaraNeural",
        "male": "hi-IN-MadhurNeural",
    },
    "english": {
        "female": "en-IN-NeerjaNeural",
        "male": "en-IN-PrabhatNeural",
    },
}


def get_persona(persona_id: str) -> BasePersona:
    """Retrieve persona instance by ID with alias resolution and fallback to default."""
    clean_id = (persona_id or "").strip().lower()
    if clean_id in PERSONA_ALIASES:
        clean_id = PERSONA_ALIASES[clean_id]

    return PERSONA_REGISTRY.get(clean_id, PERSONA_REGISTRY[DEFAULT_PERSONA_ID])


def list_personas() -> List[dict]:
    """List metadata for all registered personas."""
    return [
        {
            "id": p.id,
            "name": p.name,
            "role": p.role,
            "category": p.category,
            "temperature": p.temperature,
            "stages": p.stages,
        }
        for p in PERSONA_REGISTRY.values()
    ]


def get_tts_voice(language: str = "hinglish", gender: str = "female") -> str:
    """Map language and gender to optimal Edge-TTS neural voice."""
    lang_key = (language or "hinglish").strip().lower()
    if lang_key not in EDGE_TTS_VOICES:
        lang_key = "hinglish"

    gender_key = (gender or "female").strip().lower()
    if gender_key not in ["female", "male"]:
        gender_key = "female"

    return EDGE_TTS_VOICES[lang_key][gender_key]
