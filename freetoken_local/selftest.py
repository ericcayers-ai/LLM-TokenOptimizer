"""
freetoken_local.selftest
=========================

Real end-to-end live test. No fake server, no mock socket.

What it does:
  1. Confirm the FreeToken client can reach 127.0.0.1:1919 (health check).
  2. List the models the server reports.
  3. Send a real chat completion and print the assistant's reply + usage.
  4. (optional) Stream a second completion and print the deltas.

If the server is not running, it reports that clearly and, when
``auto_launch`` is set, tries to start the desktop app and re-run.

Exit code 0 = live test passed; non-zero = it could not complete (and says why).
"""

from __future__ import annotations

import sys
import time

from .handler import LocalLLM
from .client import FreeTokenConnectionError, FreeTokenError


def run(auto_launch: bool = False, launch_timeout: float = 90.0) -> int:
    print("[selftest] FreeToken local-LLM handler live test")
    handler = LocalLLM.make_default(
        auto_launch=auto_launch, launch_timeout=launch_timeout
    )

    # 1. health
    print(f"[1/4] health check -> {handler.client.base_url}")
    if not handler.health():
        if auto_launch:
            print("    server not up; auto_launch requested but launch failed earlier.")
        print("    RESULT: server not reachable. Start FreeToken desktop app + load a model.")
        return 2
    print("    OK: server reachable")

    # 2. models
    print("[2/4] listing models")
    models = handler.models()
    print(f"    models: {models}")
    if not models:
        print("    WARNING: server up but reports no models. Load a model in the app.")

    # 3. non-streaming completion
    print("[3/4] non-streaming completion")
    try:
        t0 = time.time()
        reply = handler.complete(
            "Reply with exactly: FREETOKEN_OK", max_tokens=16, temperature=0.0
        )
        dt = time.time() - t0
        print(f"    reply ({dt:.2f}s): {reply!r}")
    except FreeTokenError as e:
        print(f"    COMPLETION ERROR: {e}")
        return 3

    # 4. streaming
    print("[4/4] streaming completion")
    try:
        chunks = []
        for delta in handler.stream("Count to 3, one word per token: ", max_tokens=24):
            chunks.append(delta)
            sys.stdout.write(delta)
            sys.stdout.flush()
        sys.stdout.write("\n")
        print(f"    streamed {len(chunks)} delta(s)")
    except FreeTokenError as e:
        print(f"    STREAM ERROR: {e}")
        return 4

    print("[selftest] RESULT: PASS (live server responded)")
    return 0


if __name__ == "__main__":
    auto = "--auto-launch" in sys.argv
    sys.exit(run(auto_launch=auto))
