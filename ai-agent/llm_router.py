"""
llm_router.py - Multi-Provider Resilient LLM Router with Failover & Latency Telemetry.
Fallback chain: Groq -> Cerebras -> Gemini -> Local LLM (Ollama/vLLM).
"""

import asyncio
import json
import logging
import os
import time
from typing import AsyncGenerator, Dict, List, Optional, Tuple
import aiohttp
from openai import AsyncOpenAI

logger = logging.getLogger("llm_router")


class LLMProviderConfig:
    def __init__(
        self,
        name: str,
        display_name: str,
        api_key: str,
        model: str,
        base_url: str,
        priority: int = 1,
        temperature: float = 0.6,
        is_active: bool = True,
    ):
        self.name = name
        self.display_name = display_name
        self.api_key = api_key
        self.model = model
        self.base_url = base_url.rstrip("/")
        self.priority = priority
        self.temperature = temperature
        self.is_active = is_active


class MultiLLMRouter:
    def __init__(self, api_base_url: str = "http://localhost:5063"):
        self.api_base_url = api_base_url
        self.providers: List[LLMProviderConfig] = []
        self._last_sync = 0.0
        self._sync_interval = 60.0  # Sync with DB every 60s
        self._load_fallback_defaults()

    def _load_fallback_defaults(self):
        """Loads default providers from environment variables as immediate baseline."""
        groq_key = os.getenv("GROQ_API_KEY", "")
        groq_model = os.getenv("GROQ_MODEL", "qwen/qwen3.8-27b")

        defaults = [
            LLMProviderConfig(
                name="groq",
                display_name="Groq (Ultra-Fast)",
                api_key=groq_key,
                model=groq_model,
                base_url="https://api.groq.com/openai/v1",
                priority=1,
                is_active=bool(groq_key),
            ),
            LLMProviderConfig(
                name="cerebras",
                display_name="Cerebras (Failover 1)",
                api_key=os.getenv("CEREBRAS_API_KEY", ""),
                model=os.getenv("CEREBRAS_MODEL", "llama3.1-8b"),
                base_url="https://api.cerebras.ai/v1",
                priority=2,
                is_active=bool(os.getenv("CEREBRAS_API_KEY")),
            ),
            LLMProviderConfig(
                name="gemini",
                display_name="Google Gemini (Failover 2)",
                api_key=os.getenv("GEMINI_API_KEY", ""),
                model=os.getenv("GEMINI_MODEL", "gemini-2.0-flash"),
                base_url="https://generativelanguage.googleapis.com/v1beta/openai",
                priority=3,
                is_active=bool(os.getenv("GEMINI_API_KEY")),
            ),
            LLMProviderConfig(
                name="local",
                display_name="Local LLM (Ollama/vLLM)",
                api_key=os.getenv("LOCAL_LLM_KEY", "ollama"),
                model=os.getenv("LOCAL_LLM_MODEL", "qwen2.5:7b"),
                base_url=os.getenv("LOCAL_LLM_URL", "http://127.0.0.1:11434/v1"),
                priority=4,
                is_active=False,
            ),
        ]
        self.providers = defaults

    async def sync_from_api(self):
        """Syncs active models from hello_api in-memory cache."""
        now = time.time()
        if now - self._last_sync < self._sync_interval and self.providers:
            return

        url = f"{self.api_base_url}/api/admin/integrations/active/model"
        try:
            async with aiohttp.ClientSession() as session:
                async with session.get(url, timeout=aiohttp.ClientTimeout(total=3)) as resp:
                    if resp.status == 200:
                        rows = await resp.json()
                        synced: List[LLMProviderConfig] = []
                        for r in rows:
                            cfg = r.get("config_json") or {}
                            if isinstance(cfg, str):
                                try:
                                    cfg = json.loads(cfg)
                                except Exception:
                                    cfg = {}

                            p_name = r.get("provider_name", "groq")
                            api_key = cfg.get("api_key") or os.getenv("GROQ_API_KEY", "")
                            model = cfg.get("model") or "qwen/qwen3.8-27b"
                            base_url = cfg.get("base_url") or "https://api.groq.com/openai/v1"
                            temp = float(cfg.get("temperature") or 0.6)

                            synced.append(
                                LLMProviderConfig(
                                    name=p_name,
                                    display_name=r.get("display_name", p_name),
                                    api_key=api_key,
                                    model=model,
                                    base_url=base_url,
                                    priority=int(r.get("priority", 1)),
                                    temperature=temp,
                                    is_active=bool(r.get("is_active", True)),
                                )
                            )

                        if synced:
                            synced.sort(key=lambda p: p.priority)
                            self.providers = synced
                            self._last_sync = now
                            logger.info(f"[MultiLLMRouter] Synced {len(synced)} active providers from API.")
        except Exception as e:
            logger.warning(f"[MultiLLMRouter] Could not sync models from API ({e}), using in-memory defaults.")

    def get_active_providers(self) -> List[LLMProviderConfig]:
        """Returns sorted active providers."""
        active = [p for p in self.providers if p.is_active and (p.api_key or p.name == "local")]
        if not active:
            # Fallback to whatever groq config is in env
            self._load_fallback_defaults()
            return [p for p in self.providers if p.is_active and p.api_key]
        return active

    async def stream_chat(
        self,
        messages: List[Dict[str, str]],
        first_token_timeout: float = 1.8,
    ) -> AsyncGenerator[Tuple[str, str, str, float], None]:
        """
        Streams LLM tokens from primary provider, auto-failing over if primary fails or times out.
        Yields (chunk_text, provider_name, model_name, ttft_ms).
        """
        await self.sync_from_api()
        providers = self.get_active_providers()

        if not providers:
            logger.error("[MultiLLMRouter] No active LLM providers configured!")
            yield ("Maaf kijiye, abhi AI server connect nahi ho pa raha hai.", "none", "none", 0.0)
            return

        last_error = None

        for provider in providers:
            logger.info(f"[MultiLLMRouter] Attempting generation with {provider.display_name} ({provider.model})...")
            start_time = time.time()
            ttft = 0.0
            first_token_received = False

            try:
                client = AsyncOpenAI(
                    api_key=provider.api_key or "local",
                    base_url=provider.base_url,
                    timeout=15.0,
                )

                stream = await client.chat.completions.create(
                    model=provider.model,
                    messages=messages,
                    temperature=provider.temperature,
                    stream=True,
                )

                async def get_first_chunk():
                    nonlocal ttft, first_token_received
                    async for chunk in stream:
                        delta = chunk.choices[0].delta.content if chunk.choices else None
                        if delta:
                            if not first_token_received:
                                first_token_received = True
                                ttft = (time.time() - start_time) * 1000.0
                                logger.info(f"[MultiLLMRouter] {provider.name} TTFT: {ttft:.1f}ms")
                            yield delta

                chunk_generator = get_first_chunk()

                # Await first token with fast timeout
                try:
                    first_delta = await asyncio.wait_for(
                        chunk_generator.__anext__(), timeout=first_token_timeout
                    )
                    yield (first_delta, provider.name, provider.model, ttft)
                except (asyncio.TimeoutError, StopAsyncIteration) as timeout_err:
                    logger.warning(
                        f"[MultiLLMRouter] {provider.name} first token timed out (> {first_token_timeout}s). Switching to failover..."
                    )
                    continue

                # Stream the rest of the response
                async for remaining_delta in chunk_generator:
                    yield (remaining_delta, provider.name, provider.model, ttft)

                # Successfully completed stream from this provider
                return

            except Exception as ex:
                last_error = ex
                logger.warning(f"[MultiLLMRouter] Error with {provider.name} ({ex}). Failing over to next provider...")
                continue

        logger.error(f"[MultiLLMRouter] All LLM providers in fallback chain failed! Last error: {last_error}")
        yield ("Server vyast hai, kripya thodi der baad prayas karein.", "fallback", "fallback", 0.0)


# Global singleton router
llm_router = MultiLLMRouter()
