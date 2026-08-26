"""Module entry point: ``python -m freetoken_local <command>``."""

from .cli import main

if __name__ == "__main__":
    import sys

    sys.exit(main())
