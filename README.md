<div align="center">

# TokenOptimizer

**A Windows launcher for Claude Code that indexes your project, installs the right tooling, and keeps a session alive across five different backends when one runs dry.**

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com/download)
[![Platform: Windows](https://img.shields.io/badge/platform-Windows-0078D6.svg)](#)
[![Contributor Covenant](https://img.shields.io/badge/Contributor%20Covenant-2.1-4baaaa.svg)](CODE_OF_CONDUCT.md)

[Install](#install) · [Build from source](#build-from-source) · [How it works](#how-the-pieces-fit-together) · [Contributing](CONTRIBUTING.md)

</div>

---

## What it does

Point TokenOptimizer at a project and it:

- Indexes the codebase with **Graphify** so Claude Code can query a knowledge graph instead of grepping blind.
- Installs matching skills automatically via `autoskills`.
- Launches **Claude Code** with real token-saving tooling wired in on first run: **Caveman** for terser model output, **RTK** for compressed terminal/tool output, plus `claude-mem`, `headroom`, and a few other companion plugins.
- Falls back automatically when Claude Code itself is unavailable - Antigravity, then OpenCode Go, then a locally-run model via the Unsloth CLI - so a rate limit or an outage doesn't stop your session.
- Adds Groq and Codex/Cursor as manual, one-click alternatives when you want to switch deliberately instead of automatically.

Everything above is one Avalonia desktop app (`app/`). A companion VS Code extension gives you the same launcher and a live dashboard from inside the editor.

## Install

Download the latest `TokenOptimizer.msi` from [Releases](../../releases) and run it. No PowerShell, no manual dependency installs - the installer bundles the app itself and the VS Code extension, with Start Menu shortcuts from the WiX-built MSI.

## Build from source

Prerequisites:

- **.NET 10 SDK** - [dotnet.microsoft.com](https://dotnet.microsoft.com/download)
- **Node.js + npm** - only needed to build the VS Code extension or the MSI, not for `dotnet build`

```powershell
cd app
dotnet build TokenOptimizer.slnx
dotnet run --project src\TokenOptimizer.App
```

Run the test suite:

```powershell
dotnet test TokenOptimizer.slnx
```

To build the installable `TokenOptimizer.msi` yourself (what CI/Releases produce):

```powershell
# one-time setup
dotnet tool install --global wix --version 5.0.2
cd app\installer
wix extension add WixToolset.UI.wixext/5.0.2 WixToolset.Util.wixext/5.0.2

# build the MSI
.\build-installer.ps1
```

WiX is pinned to **5.0.2** on purpose - v6+ requires a paid-tier EULA for some usage, and 5.0.2 predates that and is free. The output lands at `app\installer\TokenOptimizer.msi`.

## How the pieces fit together

| Piece | What it is | Where |
|---|---|---|
| **TokenOptimizer.App** | The product itself - an Avalonia desktop app with provider adapters for Claude Code, Antigravity, Groq, OpenCode Go, Codex/Cursor handoff, and locally-run Unsloth models, all behind one fallback-chain resolver. | `app/src/TokenOptimizer.App` |
| **VS Code extension** | Sidebar/chat-participant that launches `TokenOptimizer.App.exe`. | `vscode-extension/` |

## Fallback chain

Automatic, no setup beyond saving credentials once:

1. **Claude Code** - primary, always tried first
2. **Antigravity** - Google's IDE, if installed and its credential is registered
3. **OpenCode Go** - a low-cost gateway to open coding models, if its API key is saved
4. **Local model** - the best-scoring Unsloth-served model on your own machine

**Manual only** - reachable by picking them directly, since they're separate products rather than swappable backends:

- **Groq** - fast inference API, bridged to Claude Code's Messages schema locally
- **Codex** / **Cursor** - session context and skills bundled into a handoff file, referenced from `AGENTS.md` so the receiving tool sees it on open

## Contributing

Issues and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for the development setup, test commands, and PR checklist, and [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) for community expectations.

## License

MIT - see [LICENSE](LICENSE). A rough example EULA for a compiled-binary distribution (not required for the MIT-licensed source itself) lives at [`docs/EULA-example.md`](docs/EULA-example.md).

---

## Legacy: the PowerShell launcher

`LLM-TokenOptimizer.ps1` is the original self-bootstrapping launcher that the C# app replaced in v6.0. It still works and is kept for reference, but new users should use the MSI/app path above instead. Full behavior, flags, and troubleshooting for it are documented in [AUDIT.md](AUDIT.md), which covers v5.0 through v5.5 and predates the v6.0 app - treat it as project history, not current-state documentation.

Before first use:

```powershell
Set-ExecutionPolicy RemoteSigned -Scope CurrentUser
.\LLM-TokenOptimizer.ps1
```

Quick reference for the flags most people actually reach for:

| Flag | Effect |
|---|---|
| `-ProjectPath "C:\path"` | Opens a single project directly (bypasses the multi-window picker). |
| `-Model sonnet` \| `-Model opus` | Forces this one session onto Sonnet 5 or Opus 5. |
| `-IsolateClaudeConfig` | Gives this project its own `CLAUDE_CONFIG_DIR`, separate from your normal `~/.claude`. |
| `-SetupProxy` | Interactive credential setup for the Antigravity/Codex/Cursor fallback backends. |
| `-TransferTo Codex` \| `-TransferTo Cursor` | Hands off the current session to Codex or Cursor with context bundled in. |

For the full flag list, run `Get-Help .\LLM-TokenOptimizer.ps1 -Full`, or see [AUDIT.md](AUDIT.md) for historical documentation (predates v6.0, may not reflect every current flag).
