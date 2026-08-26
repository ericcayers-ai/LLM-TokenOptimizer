# freetoken_local — FreeToken-backed local LLM handler

A real, stdlib-only Python handler that drives a **FreeToken** local LLM
server (OpenAI-compatible API on `http://127.0.0.1:1919`) and is intended
to be the **main local-LLM layer** for the LLM-TokenOptimizer project.

- Repo: https://github.com/FlashML-org/FreeToken
- Download (Windows): https://www.flashml.ai/

## Why this exists / Windows reality

The FreeToken PyPI engine (`freetoken[accel]`) only ships **Linux wheels**
— `triton==3.6.0` has no `win_amd64` build, so it cannot run natively on
Windows. The **only** supported Windows runtime is the official desktop
app installer `FreeToken-Setup-win-x64.exe`, which bundles a prebuilt
Windows engine. This module talks to that app's HTTP API, so it runs on a
stock Windows Python 3.10+ with **zero third-party dependencies**.

## What it does

- Discovers and launches the installed FreeToken desktop app
  (`launcher.launch()`), waiting for the API port to open.
- OpenAI-compatible client with real SSE streaming, timeouts, retries
  (`client.FreeTokenClient`).
- A high-level `LocalLLM` handler: `health()`, `models()`, `complete()`,
  `stream()`, with honest usage reporting (flags estimated tokens).
- A live `selftest` that performs a **real** round-trip (no mocks, no fake
  server). If the server isn't up it reports that clearly and exits
  non-zero.

## Usage

```python
from freetoken_local import LocalLLM

llm = LocalLLM.make_default()          # connects to 127.0.0.1:1919
print(llm.complete("Explain mixture-of-experts in one sentence"))

for delta in llm.stream("Count to 3:"):
    print(delta, end="", flush=True)
```

CLI:

```bash
python -m freetoken_local selftest [--auto-launch]
python -m freetoken_local chat "your prompt here"
```

## Requirements before a live test

1. FreeToken desktop app installed (run the cached installer at
   `%LOCALAPPDATA%\hermes\cache\freetoken\FreeToken-Setup-win-x64.exe`,
   or download from flashml.ai).
2. Launch the app and **load a model** in its GUI — the API port 1919 only
   opens once a model is loaded.
3. Then run `python -m freetoken_local selftest` → expects `RESULT: PASS`.

## Files

| File | Purpose |
|------|---------|
| `client.py`   | OpenAI-compatible HTTP client (streaming, retries, timeouts) |
| `launcher.py` | Locate / install / launch the Windows desktop app |
| `handler.py`  | `LocalLLM` main abstraction (health, models, complete, stream) |
| `selftest.py` | Real end-to-end live test |
| `cli.py`      | `python -m freetoken_local` commands |
| `__main__.py` | Module entry point |
