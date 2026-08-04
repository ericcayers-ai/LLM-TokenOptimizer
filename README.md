# LLM-TokenOptimizer

A self-bootstrapping, production-quality PowerShell launcher for Windows that indexes a local codebase with [Graphify](https://graphify.com), installs matching AI skills with `autoskills`, and launches [Claude Code](https://claude.ai) routed through [OmniRoute](https://github.com/diegosouzapw/OmniRoute) for automatic prompt compression — all with zero manual setup, on a completely clean Windows install.

**Ensure you run this command in PowerShell before first use:**
```powershell
Set-ExecutionPolicy RemoteSigned -Scope CurrentUser
```

## What it does

- **Bootstraps a clean Windows PC from scratch.** Detects Git, Node.js, npm, Python, and pip; auto-installs anything missing via `winget` (falling back to a per-user install if machine-scope requires admin rights). If `winget` itself isn't available, it prints manual install links instead of failing.
- **Installs and verifies Graphify.** Automatically upgrades `pip`, then installs/upgrades Graphify via `pip install --upgrade graphifyy`. Discovers and adds Python's user‑scripts directory (including non‑standard locations like Microsoft Store Python) to the session PATH so `graphify` is immediately found. On every launch, either does a full scan (`graphify .`) for a new project or an incremental update (`graphify update`) for a project with an existing graph, falling back to a full rescan when the installed version doesn't support `update`.
- **Installs and runs `autoskills`.** Detects the project's tech stack and installs matching Claude Code skills from the skills.sh registry on every launch, fully non‑interactively (`npx -y autoskills -y -a claude-code`).
- **Finds or installs Claude Code.** Checks PATH, common install directories, and the Windows registry; installs via `npm install -g @anthropic-ai/claude-code` if not found; falls back to a native file picker if all else fails. Remembers the resolved path for next time.
- **Auto‑installs and starts OmniRoute.** Installs via `npm install -g omniroute@latest` if missing, checks for updates, and launches it in its own minimized, titled console window (separate from both the launcher and Claude Code). The server is started once and shared by all project windows.
- **Routes Claude Code through OmniRoute**, applying OmniRoute's compression pipeline (RTK → Caveman → LLMLingua → Lite) to every request automatically. Model selection happens inside Claude Code's own `/model` picker; the launcher pins exactly two 1M‑context models and restricts the picker to them.
- **Pins the `/model` picker to Opus 5 (1M) and Sonnet 5 (1M), resolved from OmniRoute’s live catalog.** Both models inherently carry a 1‑million token context window — there is no smaller variant. The launcher writes `ANTHROPIC_DEFAULT_OPUS_MODEL` / `ANTHROPIC_DEFAULT_SONNET_MODEL` for the session, sets `CLAUDE_CODE_AUTO_COMPACT_WINDOW` to 900k tokens and `CLAUDE_CODE_MAX_OUTPUT_TOKENS` to 128k (matching the models), and restricts `~/.claude/settings.json`’s `availableModels` so only these two OmniRoute‑resolved IDs appear in the picker. No `auto/*` combo or older model versions are listed.
- **Detects (and helps you complete) the one manual step OmniRoute requires**: connecting your Claude.ai account as a provider. This is a browser OAuth sign‑in and can’t be automated — the script checks OmniRoute’s live catalog; if no Claude models are found, it opens the dashboard directly to `/dashboard/providers/claude` and waits for you to click **+ Add** and sign in. Once the provider is verified (even once), the launcher remembers it and never asks again.
- **Remembers your OmniRoute setup permanently.** The API key is validated against OmniRoute’s `/v1/models` endpoint; a rejected key (401/403) is the **only** thing that triggers a re‑prompt. An unreachable server never discards a working key. The “provider connected” flag is recorded after the first successful catalog read and short‑circuits all future onboarding.
- **Multi‑window: run several projects at the same time.** The launcher prompts once for a **master folder** (the parent directory containing your project subfolders). You then pick which subfolders to open; each chosen project gets its own independent console window with its own Graphify extraction and Claude Code session. The launcher stays open as a control panel so you can open more windows whenever you want.
- **Per‑project instance lock.** Two windows on the *same* folder are still blocked (they would fight over the same `.graphify` output and `.claude/settings.json`), but different projects run simultaneously without conflict. Config is written with a cross‑process lock so concurrent windows don’t clobber each other’s project history.
- **Resumes your previous Claude session automatically** when you reopen a project you’ve used before (`claude --continue`), and if there’s no previous session to resume, falls back to starting a fresh one instead of erroring out.
- **Lets you force a specific model for one launch** with `-Model sonnet` or `-Model opus`, without touching whatever’s saved as your Claude Code default.
- **Remembers your last 20 project paths and master folders**, navigable with Up/Down arrow keys in the inline path editor, with Delete to remove an entry.
- **Asks about updates fresh every launch** ("Check for updates now?") — say yes and it checks Git, Node, Python, Graphify, Claude Code, OmniRoute, and `autoskills` for newer versions.
- **Logs everything** with millisecond timestamps and PID to `%LOCALAPPDATA%\LLM-TokenOptimizer\logs\`, with automatic rotation (keeps the last 10 log files).
- **Cleans up guaranteed on exit** — mutex release and config save run inside `try/catch/finally` and a `PowerShell.Exiting` engine event, so they run even on a crash or Ctrl+C.

## Requirements

Nothing needs to be pre‑installed. On a totally clean Windows 10 (2004+) or Windows 11 machine, the script installs everything itself via `winget` and `npm`/`pip` as needed:

- Git
- Node.js + npm
- Python + pip
- Graphify CLI (via pip)
- Claude Code CLI (via npm)
- OmniRoute CLI (via npm)
- `autoskills` (via npm, on first use)

If `winget` isn’t available on the machine (very old Windows 10 builds, or it’s disabled by policy), the script degrades gracefully: it tells you exactly what’s missing and where to get it manually instead of failing outright.

## Usage

```powershell
.\LLM-TokenOptimizer.ps1
```

### Flags

| Flag | Effect |
|---|---|
| `-VerboseMode` | Prints detailed debug logs to the console. |
| `-SkipOmniRoute` | Skips OmniRoute entirely — Claude Code launches directly, uncompressed. |
| `-ResetConfig` | Deletes the saved JSON config and starts fresh (re‑asks every first‑run prompt). |
| `-Model sonnet` \| `-Model opus` | Forces this one session onto Sonnet 5 or Opus 5, regardless of whatever Claude Code has saved as its default. Session‑only — doesn’t persist. |
| `-MasterFolder "C:\path"` | Skips the master‑folder prompt and uses the given directory as the project parent. |
| `-ProjectPath "C:\path"` | Opens a single project directly (bypasses the multi‑window picker). |
| `-IsolateClaudeConfig` | Gives this project its own `CLAUDE_CONFIG_DIR` so settings, history, and credentials are separate from your normal `~/.claude`. |
| `-ReconfigureOmniRoute` | Forgets the saved OmniRoute API key and setup state, then re‑prompts as if it’s the first run. |

### First run

1. **Windows/dependency check** — the script verifies Windows 10+, then detects and auto‑installs any missing tools.
2. **Graphify install/verify** — installed via pip if missing, version printed.
3. **Update check** — “Check for updates now?” (asked fresh every launch in the launcher window).
4. **Claude Code detection** — found on PATH/registry/common dirs, or auto‑installed, or you’re prompted for the path.
5. **OmniRoute routing** — “Route Claude Code through OmniRoute?” (Y/n). If yes, you’ll be asked once for your OmniRoute API key (stored encrypted, DPAPI, tied to your Windows account). OmniRoute is then auto‑started in its own window.
6. **Claude Code provider connection** — if OmniRoute has no Claude.ai account connected yet, your browser opens straight to the dashboard’s Claude provider page; click **+ Add**, sign in, then press Enter back in the console. Once verified, this step never reappears.
7. **Master folder** — drag‑and‑drop or type the path to the parent directory that holds your projects.
8. **Project picker** — you see a numbered list of subfolders; open one, several, or all of them, each in its own window.
9. **Per‑project setup (runs in each window)** — Graphify extraction (full scan or incremental update), `autoskills`, then launch Claude Code (resuming your previous session if this project’s been used before).

Every subsequent run in the same project just resumes: no re‑prompting for OmniRoute, provider connection, or dependency installs unless something’s actually missing or reset.

### Picking a model

Model selection lives entirely inside Claude Code’s own `/model` picker — the launcher doesn’t prompt for it. The picker is restricted to exactly two entries, both routed through OmniRoute’s compression pipeline with the full 1M‑token context window:

- **Opus 5 – 1M – OmniRoute**
- **Sonnet 5 – 1M – OmniRoute**

No `auto/*` combo or older model versions appear in the list.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 99 | Unexpected error |
| 100 | This project is already open in another LLM‑TokenOptimizer window |
| 101 | Unsupported Windows version |
| 102 | Missing required dependency that couldn’t be auto‑installed |
| 103 | Claude Code not found |
| 104 | Graphify installation failed |
| 106 | Graph extraction failed |

## Where things live

| What | Location |
|---|---|
| Config (project history, saved paths, encrypted API key) | `%LOCALAPPDATA%\LLM-TokenOptimizer\config.json` |
| Logs | `%LOCALAPPDATA%\LLM-TokenOptimizer\logs\` |
| Graph output + studio | `<project>\.graphify\graph.json`, `<project>\.graphify\studio\studio.html` |
| Claude Code model restrictions | `~\.claude\settings.json` (`availableModels`) |

## Troubleshooting

- **"Missing closing '}'" or other parse errors** — the `.ps1` file got truncated during a copy/download. Re‑copy it fresh rather than re‑running a partial copy; verify the file ends with a bare `Main` call on the last line.
- **OmniRoute never comes online** — it can take 10–20 s on first boot; the script waits up to 25 s. If it still fails, start it manually with `omniroute` in its own terminal.
- **`/model` picker doesn’t show gateway models** — needs Claude Code v2.1.129+ and `CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY=1` (set automatically by this script whenever OmniRoute routing is active).
- **Claude keeps launching on the wrong model** — Claude Code caches your last `/model` pick as the session default. Either pick Opus 5 / Sonnet 5 again in `/model`, or launch with `-Model sonnet` / `-Model opus` to force it for one session.
- **Graphify isn’t found even after `pip install graphifyy` succeeded** — this can happen with Microsoft Store Python, which installs scripts to a non‑standard location. The launcher now automatically adds Python’s user‑scripts directory to PATH after every Graphify install, but if you ran an older version of the script you may need to close and reopen the launcher so the detection cache is refreshed.
- **Project window says “already open”** — only one window per project folder is allowed. Switch to the existing window, or close it and try again.
