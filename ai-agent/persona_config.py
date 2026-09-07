"""
persona_config.py - Compatibility Facade forwarding to the modular personas/ package.
All new persona definitions are located under ai-agent/personas/:
- persona_role_hr_interview.py
- persona_role_kids_learning.py
- persona_role_romantic_chat.py
- persona_role_parents_care.py
- persona_role_english_tutor.py
"""

from personas.registry import (
    get_persona,
    list_personas,
    get_tts_voice,
    DEFAULT_PERSONA_ID,
    EDGE_TTS_VOICES,
)


def get_persona_config(persona_id: str) -> dict:
    persona = get_persona(persona_id)
    return {
        "id": persona.id,
        "name": persona.name,
        "role": persona.role,
        "category": persona.category,
        "temperature": persona.temperature,
        "base_prompt": persona.base_prompt,
        "stages": persona.stages,
        "greetings": persona.greetings,
    }


def get_system_prompt(persona_id: str, language: str = "hinglish") -> str:
    persona = get_persona(persona_id)
    return persona.get_system_prompt(language)


def get_greeting(persona_id: str, language: str = "hinglish") -> str:
    persona = get_persona(persona_id)
    return persona.get_greeting(language)
