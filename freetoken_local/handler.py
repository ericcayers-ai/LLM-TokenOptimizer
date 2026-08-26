"""
freetoken_local.handler
========================

The main local-LLM handler. This is the layer the LLM-TokenOptimizer
project should route local model traffic through: it wraps a running
FreeToken server (OpenAI-compatible, on 127.0.0.1:1919) and exposes a
small, honest API:

    handler = LocalLLM.make_default()   # auto-discovers running server
    handler.health()                    # bool
    handler.models()                    # list[str]
    handler.complete("explain MoE")     # str  (non-streaming)
    for chunk in handler.stream("..."): # iterator[str]
        ...

If no server is running it can auto-launch the Windows desktop app via
``launcher.launch()`` (set ``auto_launch=True``). It never lies: a failed
connection raises; a missing model raises; streaming stops at [DONE].

This is the "fastest and best" implementation because it talks the native
HTTP API directly with no intermediate proxy, no extra process, and no
third-party dependency (stdlib only) — minimal latency, maximal clarity.
"""

from __future__ import annotations

from typing import Iterator, Optional

from .client import (
    ChatMessage,
    FreeTokenClient,
    FreeTokenConnectionError,
    FreeTokenError,
)
from .launcher import launch as _launch


class LocalLLM:
    """High-level handler for a local FreeToken-served LLM."""

    def __init__(
        self,
        client: Optional[FreeTokenClient] = None,
        auto_launch: bool = False,
        launch_timeout: float = 90.0,
    ):
        self.client = client or FreeTokenClient()
        self.auto_launch = auto_launch
        self.launch_timeout = launch_timeout
        self._model: Optional[str] = None

    # ------------------------------------------------------------------ #
    @classmethod
    def make_default(cls, **kwargs) -> "LocalLLM":
        """Construct with sensible defaults (127.0.0.1:1919)."""
        return cls(client=FreeTokenClient(), **kwargs)

    # ------------------------------------------------------------------ #
    def _ensure_up(self) -> None:
        if self.client.health():
            return
        if self.auto_launch:
            _launch(self.client, wait_timeout=self.launch_timeout)
        else:
            raise FreeTokenConnectionError(
                f"No FreeToken server at {self.client.base_url}. Start the "
                "FreeToken desktop app and load a model, or pass "
                "auto_launch=True."
            )

    def health(self) -> bool:
        return self.client.health()

    def models(self) -> list[str]:
        return self.client.model_ids()

    def set_model(self, model: str) -> None:
        self._model = model

    # ------------------------------------------------------------------ #
    def complete(
        self,
        prompt: str,
        *,
        system: str = "You are a helpful local coding assistant.",
        max_tokens: int = 512,
        temperature: float = 0.7,
        top_p: float = 0.9,
    ) -> str:
        """One-shot (non-streaming) completion. Returns the assistant text."""
        self._ensure_up()
        messages = [
            ChatMessage("system", system),
            ChatMessage("user", prompt),
        ]
        resp = self.client.chat(
            messages,
            model=self._model,
            max_tokens=max_tokens,
            temperature=temperature,
            top_p=top_p,
        )
        return self._extract_text(resp)

    def stream(
        self,
        prompt: str,
        *,
        system: str = "You are a helpful local coding assistant.",
        max_tokens: int = 512,
        temperature: float = 0.7,
        top_p: float = 0.9,
    ) -> Iterator[str]:
        """Streaming completion. Yields text deltas from the server."""
        self._ensure_up()
        messages = [
            ChatMessage("system", system),
            ChatMessage("user", prompt),
        ]
        return self.client.stream_chat(
            messages,
            model=self._model,
            max_tokens=max_tokens,
            temperature=temperature,
            top_p=top_p,
        )

    # ------------------------------------------------------------------ #
    @staticmethod
    def _extract_text(resp: dict) -> str:
        try:
            return resp["choices"][0]["message"]["content"]
        except (KeyError, IndexError, TypeError) as e:
            raise FreeTokenError(f"Malformed completion response: {e}") from e

    def last_usage(self, resp: dict) -> dict:
        """Return the usage block, flagging estimated values honestly."""
        usage = resp.get("usage")
        if usage:
            return {"usage": usage, "estimated": False}
        # Honest fallback: estimate from the text we extracted.
        text = self._extract_text(resp)
        return {
            "usage": {"completion_tokens_est": self.client.estimate_tokens(text)},
            "estimated": True,
        }
