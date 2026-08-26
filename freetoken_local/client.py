"""
freetoken_local.client
=======================

Real, stdlib-only OpenAI-compatible client for the FreeToken local LLM
engine.

FreeToken (https://github.com/FlashML-org/FreeToken) serves an
OpenAI-compatible HTTP API on http://127.0.0.1:1919 by default:

    POST /v1/chat/completions   (OpenAI chat)
    GET  /v1/models             (list loaded models)
    POST /v1/messages           (Anthropic-compatible, optional)

This module talks to that server with nothing but the Python standard
library, so it runs on a stock Windows Python 3.10+ install with zero
third-party packages. No stubs, no mocks: every call performs a real
socket round-trip and raises on failure.

Design notes
------------
* Synchronous + streaming both supported.
* Streaming parses the real SSE `data: {...}` frames FreeToken emits.
* Token usage is read from the `usage` object in the response (FreeToken
  reports prompt/completion tokens). When the server omits it we fall back
  to a transparent length-based estimate and flag it as estimated.
* All errors raise ``FreeTokenError`` (or a subclass) with actionable text.
"""

from __future__ import annotations

import json
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from typing import Iterator, Optional

__all__ = [
    "FreeTokenError",
    "FreeTokenConnectionError",
    "FreeTokenAPIError",
    "ChatMessage",
    "FreeTokenClient",
    "DEFAULT_HOST",
    "DEFAULT_PORT",
]

DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 1919


class FreeTokenError(RuntimeError):
    """Base error for all FreeToken client failures."""


class FreeTokenConnectionError(FreeTokenError):
    """Raised when the server cannot be reached (not running / wrong port)."""


class FreeTokenAPIError(FreeTokenError):
    """Raised when the server responds with a non-2xx status."""

    def __init__(self, status: int, body: str, url: str):
        self.status = status
        self.body = body
        self.url = url
        super().__init__(f"FreeToken API {status} at {url}: {body[:300]}")


@dataclass
class ChatMessage:
    role: str
    content: str

    def to_dict(self) -> dict:
        return {"role": self.role, "content": self.content}


class FreeTokenClient:
    """Minimal but complete OpenAI-compatible client for a FreeToken server."""

    def __init__(
        self,
        host: str = DEFAULT_HOST,
        port: int = DEFAULT_PORT,
        timeout: float = 120.0,
        api_key: str = "freetoken",
    ):
        self.host = host
        self.port = port
        self.timeout = timeout
        self.api_key = api_key
        self.base_url = f"http://{host}:{port}"

    # ------------------------------------------------------------------ #
    # low-level request helpers
    # ------------------------------------------------------------------ #
    def _url(self, path: str) -> str:
        return f"{self.base_url}{path}"

    def _headers(self) -> dict:
        return {
            "Content-Type": "application/json",
            "Authorization": f"Bearer {self.api_key}",
            "Accept": "application/json",
        }

    def _request(self, method: str, path: str, payload: Optional[dict] = None) -> dict:
        url = self._url(path)
        data = json.dumps(payload).encode("utf-8") if payload is not None else None
        req = urllib.request.Request(
            url, data=data, headers=self._headers(), method=method
        )
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                raw = resp.read().decode("utf-8", "replace")
                if resp.status >= 400:
                    raise FreeTokenAPIError(resp.status, raw, url)
                return json.loads(raw) if raw else {}
        except urllib.error.HTTPError as e:  # pragma: no cover - network
            body = e.read().decode("utf-8", "replace")
            raise FreeTokenAPIError(e.code, body, url) from e
        except urllib.error.URLError as e:  # pragma: no cover - network
            raise FreeTokenConnectionError(
                f"Cannot reach FreeToken at {url}: {e.reason}"
            ) from e

    # ------------------------------------------------------------------ #
    # public API
    # ------------------------------------------------------------------ #
    def health(self) -> bool:
        """Return True iff the server answers /v1/models."""
        try:
            self.list_models()
            return True
        except FreeTokenError:
            return False

    def list_models(self) -> list[dict]:
        """GET /v1/models -> list of model dicts (id, owned_by, ...)."""
        resp = self._request("GET", "/v1/models")
        return resp.get("data", [])

    def model_ids(self) -> list[str]:
        return [m.get("id", "") for m in self.list_models() if m.get("id")]

    def chat(
        self,
        messages: list[ChatMessage],
        model: Optional[str] = None,
        max_tokens: int = 512,
        temperature: float = 0.7,
        top_p: float = 0.9,
        stream: bool = False,
        extra: Optional[dict] = None,
    ) -> dict:
        """Non-streaming chat completion. Returns the full JSON response."""
        model = model or self._auto_model()
        payload = {
            "model": model,
            "messages": [m.to_dict() for m in messages],
            "max_tokens": max_tokens,
            "temperature": temperature,
            "top_p": top_p,
            "stream": False,
        }
        if extra:
            payload.update(extra)
        return self._request("POST", "/v1/chat/completions", payload)

    def stream_chat(
        self,
        messages: list[ChatMessage],
        model: Optional[str] = None,
        max_tokens: int = 512,
        temperature: float = 0.7,
        top_p: float = 0.9,
        extra: Optional[dict] = None,
    ) -> Iterator[str]:
        """Streaming chat completion. Yields text deltas as they arrive.

        Real SSE parsing of the ``data: {json}`` frames FreeToken emits.
        The terminal ``[DONE]`` frame stops iteration.
        """
        model = model or self._auto_model()
        payload = {
            "model": model,
            "messages": [m.to_dict() for m in messages],
            "max_tokens": max_tokens,
            "temperature": temperature,
            "top_p": top_p,
            "stream": True,
        }
        if extra:
            payload.update(extra)

        url = self._url("/v1/chat/completions")
        data = json.dumps(payload).encode("utf-8")
        req = urllib.request.Request(
            url, data=data,
            headers={**self._headers(), "Accept": "text/event-stream"},
            method="POST",
        )
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                buffer = ""
                while True:
                    chunk = resp.read(1)
                    if not chunk:
                        break
                    buffer += chunk.decode("utf-8", "replace")
                    # SSE frames end on a blank line
                    while "\n\n" in buffer:
                        frame, buffer = buffer.split("\n\n", 1)
                        for line in frame.splitlines():
                            line = line.strip()
                            if not line.startswith("data:"):
                                continue
                            data_str = line[len("data:"):].strip()
                            if data_str == "[DONE]":
                                return
                            try:
                                obj = json.loads(data_str)
                            except json.JSONDecodeError:
                                continue
                            delta = (
                                obj.get("choices", [{}])[0]
                                .get("delta", {})
                                .get("content", "")
                            )
                            if delta:
                                yield delta
        except urllib.error.URLError as e:  # pragma: no cover - network
            raise FreeTokenConnectionError(
                f"Stream broke against {url}: {e.reason}"
            ) from e

    # ------------------------------------------------------------------ #
    def _auto_model(self) -> str:
        """Pick the first available model; FreeToken's /v1/models is authoritative."""
        ids = self.model_ids()
        if not ids:
            # FreeToken's served-model-name defaults to the checkpoint basename;
            # if list is empty (engine still warming) we still let the call try.
            return "local-model"
        return ids[0]

    @staticmethod
    def estimate_tokens(text: str) -> int:
        """Transparent heuristic estimate (~4 chars/token) used ONLY when the
        server does not report real usage. Flagged as estimated by callers."""
        return max(1, len(text) // 4)
