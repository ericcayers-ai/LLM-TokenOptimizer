"""
freetoken_local
===============

FreeToken-backed local LLM handler for the LLM-TokenOptimizer project.

FreeToken (https://github.com/FlashML-org/FreeToken) is an edge-native
MoE serving engine. On Windows the only supported runtime is the official
desktop app from https://www.flashml.ai/ (the PyPI ``freetoken[accel]``
package only ships Linux wheels). The app serves an OpenAI-compatible API
on http://127.0.0.1:1919 .

This package is stdlib-only (no third-party deps) so it runs on a stock
Windows Python 3.10+ with nothing to install.

Quick start
-----------
    from freetoken_local import LocalLLM
    llm = LocalLLM.make_default()        # connects to 127.0.0.1:1919
    print(llm.complete("explain mixture-of-experts in one sentence"))

CLI
---
    python -m freetoken_local selftest [--auto-launch]
    python -m freetoken_local chat "your prompt here"
"""

from .client import (
    DEFAULT_HOST,
    DEFAULT_PORT,
    ChatMessage,
    FreeTokenAPIError,
    FreeTokenClient,
    FreeTokenConnectionError,
    FreeTokenError,
)
from .handler import LocalLLM
from .launcher import (
    find_app_executable,
    find_installer,
    install_from_cache,
    launch,
    locate,
)

__all__ = [
    "LocalLLM",
    "FreeTokenClient",
    "FreeTokenError",
    "FreeTokenConnectionError",
    "FreeTokenAPIError",
    "ChatMessage",
    "DEFAULT_HOST",
    "DEFAULT_PORT",
    "launch",
    "locate",
    "find_app_executable",
    "find_installer",
    "install_from_cache",
]
