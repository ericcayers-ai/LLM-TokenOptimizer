"""
freetoken_local.launcher
=========================

Locates and launches the **Windows** FreeToken desktop app.

The FreeToken PyPI engine (``freetoken[accel]``) only ships Linux wheels
(triton has no win_amd64 build), so on Windows the only supported runtime
is the official desktop installer ``FreeToken-Setup-win-x64.exe`` from
https://www.flashml.ai/ . This module knows where that installer lives and
where the app installs to, and can launch it headless-ish (the GUI still
opens, but we wait for the API port rather than requiring user clicks).

Everything here is real: no fake server, no mock. If the app is not
installed and the installer is not present, ``locate()`` returns None and
``launch()`` raises a clear, actionable error instead of pretending.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import time
from pathlib import Path
from typing import Optional

from .client import FreeTokenClient, FreeTokenConnectionError

# Where we cached the official installer during setup.
CACHED_INSTALLER = (
    Path(os.environ.get("LOCALAPPDATA", ""))
    / "hermes"
    / "cache"
    / "freetoken"
    / "FreeToken-Setup-win-x64.exe"
)

# Common install locations for the FreeToken desktop app on Windows.
def _install_candidates() -> list[Path]:
    roots = []
    pf = os.environ.get("ProgramFiles")
    pf86 = os.environ.get("ProgramFiles(x86)")
    local = os.environ.get("LOCALAPPDATA")
    appdata = os.environ.get("APPDATA")
    if pf:
        roots.append(Path(pf))
    if pf86:
        roots.append(Path(pf86))
    if local:
        roots.append(Path(local) / "Programs")
    if appdata:
        roots.append(Path(appdata))
    # also the user's local app-ish dirs
    if local:
        roots.append(Path(local) / "FreeToken")
    cands: list[Path] = []
    for r in roots:
        cands.append(r / "FreeToken" / "FreeToken.exe")
        cands.append(r / "FreeToken" / "FreeToken.Desktop.exe")
        cands.append(r / "freetoken" / "freetoken.exe")
        cands.append(r / "FreeToken" / "freetoken-desktop.exe")
    return cands


def find_app_executable() -> Optional[Path]:
    """Return the path to the installed FreeToken desktop exe, or None."""
    for c in _install_candidates():
        if c.is_file():
            return c
    # fall back to PATH
    found = shutil.which("FreeToken") or shutil.which("freetoken")
    if found:
        return Path(found)
    return None


def find_installer() -> Optional[Path]:
    """Return the cached installer path if it exists, else None."""
    return CACHED_INSTALLER if CACHED_INSTALLER.is_file() else None


def locate() -> Optional[Path]:
    """Best-effort: an installed exe, else the cached installer."""
    return find_app_executable() or find_installer()


def install_from_cache() -> Path:
    """Run the cached installer (user must click through the GUI wizard).

    Returns the installer path that was launched. Raises if no installer
    is present so the caller can tell the user to download it.
    """
    inst = find_installer()
    if not inst:
        raise FileNotFoundError(
            "FreeToken installer not found. Download FreeToken-Setup-win-x64.exe "
            "from https://www.flashml.ai/ and place it at: "
            f"{CACHED_INSTALLER}"
        )
    subprocess.Popen(
        [str(inst)],
        shell=False,
        creationflags=0x00000008,  # DETACHED_PROCESS-ish; GUI still shows
    )
    return inst


def launch(
    client: Optional[FreeTokenClient] = None,
    wait_timeout: float = 90.0,
    auto_install: bool = False,
) -> bool:
    """Launch the FreeToken desktop app and wait until its API is reachable.

    Returns True if the server came up. Raises a clear error if launch is
    impossible (no app, no installer) or the port never opened in time.
    """
    client = client or FreeTokenClient()
    exe = find_app_executable()
    if exe is None:
        if auto_install and find_installer() is not None:
            install_from_cache()
            # After install the exe may now exist; re-locate.
            exe = find_app_executable()
        if exe is None:
            inst = find_installer()
            msg = (
                "FreeToken desktop app is not installed. "
            )
            if inst:
                msg += (
                    f"An installer was found at {inst}. Run it (or call "
                    "launcher.install_from_cache()) to install, then re-launch."
                )
            else:
                msg += (
                    "No installer cached either. Download "
                    "FreeToken-Setup-win-x64.exe from https://www.flashml.ai/ ."
                )
            raise RuntimeError(msg)

    # Already up?
    try:
        if client.health():
            return True
    except FreeTokenConnectionError:
        pass

    # Launch the GUI app detached; it will open its own window and bind :1919.
    subprocess.Popen(
        [str(exe)],
        shell=False,
        creationflags=0x00000008,
    )

    deadline = time.time() + wait_timeout
    last_err: Optional[Exception] = None
    while time.time() < deadline:
        try:
            if client.health():
                return True
        except FreeTokenConnectionError as e:
            last_err = e
        time.sleep(1.5)
    raise TimeoutError(
        f"FreeToken did not open its API on {client.base_url} within "
        f"{wait_timeout}s. Last error: {last_err}. The desktop app GUI may "
        "need you to load a model first; open it and start a model, then "
        "retry. Server must listen on http://127.0.0.1:1919 ."
    )
