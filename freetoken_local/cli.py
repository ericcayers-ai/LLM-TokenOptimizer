"""
freetoken_local.cli
===================

Command-line entry point:

    python -m freetoken_local selftest [--auto-launch]
    python -m freetoken_local chat "your prompt here"
"""

from __future__ import annotations

import sys

from .handler import LocalLLM
from .selftest import run as _selftest_run


def _cmd_selftest(argv: list[str]) -> int:
    auto = "--auto-launch" in argv
    return _selftest_run(auto_launch=auto)


def _cmd_chat(argv: list[str]) -> int:
    if not argv:
        print("usage: python -m freetoken_local chat \"prompt\"")
        return 1
    prompt = " ".join(argv)
    llm = LocalLLM.make_default()
    if not llm.health():
        print("FreeToken server not reachable at", llm.client.base_url)
        print("Start the FreeToken desktop app and load a model, then retry.")
        return 2
    for delta in llm.stream(prompt):
        sys.stdout.write(delta)
        sys.stdout.flush()
    sys.stdout.write("\n")
    return 0


def main(argv: list[str] | None = None) -> int:
    argv = argv if argv is not None else sys.argv[1:]
    if not argv:
        print("commands: selftest, chat")
        return 1
    cmd, rest = argv[0], argv[1:]
    if cmd == "selftest":
        return _cmd_selftest(rest)
    if cmd == "chat":
        return _cmd_chat(rest)
    print(f"unknown command: {cmd}")
    return 1


if __name__ == "__main__":
    sys.exit(main())
