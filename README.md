# LLM-TokenOptimizer

A self-bootstrapping, production-quality PowerShell launcher for Windows that indexes a local codebase with [Graphify](https://graphify.com), installs matching AI skills with `autoskills`, and launches [Claude Code](https://claude.ai) routed through [OmniRoute](https://github.com/diegosouzapw/OmniRoute) for automatic prompt compression — all with zero manual setup, on a completely clean Windows install.

Ensure the command is run in powershell beforehand: Set-ExecutionPolicy RemoteSigned -Scope CurrentUser

## What it does

- **Bootstraps a clean Windows PC from scratch.** Detects Git, Node.js, npm, Python, and pip; auto-installs anything missing via `winget` (falling back to a per-user install if machine-scope needs admin rights it doesn't have). If `winget` itself isn't available, it prints manual install links instead of failing.
- **Installs and verifies Graphify.** Auto-installs via `pip` if missing, verifies the version, and on every launch either does a full scan (`graphify .`, first run in a project) or an incremental one (`graphify update`, every run after — only re-parses changed files, with an automatic fallback to a full rescan if `update` isn't supported by the installed version).
- **Installs and runs `autoskills`.** Detects the project's tech stack and installs matching Claude Code skills from the skills.sh registry on every launch, fully non-interactively (`npx -y autoskills -y -a claude-code`).
- **Finds or installs Claude Code.** Checks PATH, common install directories, and the Windows registry; installs via `npm install -g @anthropic-ai/claude-code` if not found; falls back to a native file picker if all else fails. Remembers the resolved path for next time.
- **Auto-installs and starts OmniRoute.** Installs via `npm install -g omniroute@latest` if missing, checks for updates, and launches it in its own minimized, titled console window (separate from both this launcher and Claude Code) with a progress bar while it comes online.
- **Routes Claude Code through OmniRoute**, applying OmniRoute's compression pipeline (RTK → Caveman → LLMLingua → Lite) to every request automatically. No model-switching logic lives in this script — model selection happens entirely inside Claude Code's own `/model` picker.
- **Pins the `/model` picker to four exact models, all served through OmniRoute** — Opus 4.8, Sonnet 5, Fable 5, and Haiku 4.5 — by resolving each one against OmniRoute's live model catalog and writing `ANTHROPIC_DEFAULT_OPUS_MODEL` / `_SONNET_MODEL` / `_FABLE_MODEL` / `_HAIKU_MODEL` for the session, and restricting `~/.claude/settings.json`'s `availableModels` so no other version or `auto/*` combo shows up in the picker. If OmniRoute doesn't have an exact match for a family, that family is left on Claude Code's own built-in default instead of silently substituting a generic model.
- **Detects (and helps you complete) the one manual step OmniRoute requires**: connecting your Claude.ai account as a provider. This is a browser OAuth sign-in and can't be automated — the script checks `omniroute providers list --json` and, if nothing's connected yet, opens the dashboard page directly to `/dashboard/providers/claude` and waits for you to click **+ Add**.
- **Resumes your previous Claude session automatically** when you reopen a project you've used before (`claude --continue`), and if there's no previous session to resume, falls back to starting a fresh one instead of erroring out.
- **Lets you force a specific model for one launch** with `-Model sonnet` or `-Model opus`, without touching whatever's saved as your Claude Code default.
- **Remembers your last 20 project paths**, navigable with Up/Down arrow keys in the path prompt, with Delete to remove an entry.
- **Asks about updates fresh every launch** ("Check for updates now?") rather than persisting or throttling a schedule — say yes and it checks Git, Node, Python, Graphify, Claude Code, OmniRoute, and `autoskills` for newer versions.
- **Single-instance protected** via a global, per-user `.NET Mutex`, so a duplicate launch closes immediately instead of causing environment conflicts.
- **Logs everything** with millisecond timestamps to `%LOCALAPPDATA%\LLM-TokenOptimizer\logs\`, with automatic rotation (keeps the last 10 log files).
- **Cleans up guaranteed on exit** — mutex release and config save run inside `try/catch/finally` and a `PowerShell.Exiting` engine event, so they run even on a crash or Ctrl+C.

## Requirements

Nothing needs to be pre-installed. On a totally clean Windows 10 (2004+) or Windows 11 machine, the script installs everything itself via `winget` and `npm`/`pip` as needed:

- Git
- Node.js + npm
- Python + pip
- Graphify CLI (via pip)
- Claude Code CLI (via npm)
- OmniRoute CLI (via npm)
- `autoskills` (via npm, on first use)

If `winget` isn't available on the machine (very old Windows 10 builds, or it's disabled by policy), the script degrades gracefully: it tells you exactly what's missing and where to get it manually instead of failing outright.

## Usage

```powershell
.\LLM-TokenOptimizer.ps1
```

### Flags

| Flag | Effect |
|---|---|
| `-VerboseMode` | Prints detailed debug logs to the console. |
| `-ForceUpdate` | (Reserved for future use alongside the update-check prompt.) |
| `-SkipOmniRoute` | Skips OmniRoute entirely — Claude Code launches directly, uncompressed. |
| `-ResetConfig` | Deletes the saved JSON config and starts fresh (re-asks every first-run prompt). |
| `-Model sonnet` \| `-Model opus` | Forces this one session onto Sonnet or Opus, regardless of whatever Claude Code has saved as its default. Session-only — doesn't persist. |

### First run

1. **Windows/dependency check** — the script verifies Windows 10+, then detects and auto-installs any missing tools.
2. **Graphify install/verify** — installed via pip if missing, version printed.
3. **Update check** — "Check for updates now?" (asked fresh every launch, not just first run).
4. **Claude Code detection** — found on PATH/registry/common dirs, or auto-installed, or you're prompted for the path.
5. **OmniRoute routing** — "Route Claude Code through OmniRoute?" (Y/n). If yes, you'll be asked once for your OmniRoute API key (stored encrypted, DPAPI, tied to your Windows account). OmniRoute is then auto-started in its own window.
6. **Claude Code provider connection** — if OmniRoute has no Claude.ai account connected yet, your browser opens straight to the dashboard's Claude provider page; click **+ Add**, sign in, then press Enter back in the console.
7. **Project path** — drag-and-drop a folder or type a path; Up/Down cycles your history, Delete removes an entry.
8. **Graphify extraction** — full scan on a new project, incremental `update` on repeat runs; builds the interactive HTML graph.
9. **autoskills** — detects your stack and installs matching Claude Code skills automatically.
10. **Launch** — press Enter to start Claude Code (resuming your previous session if this project's been used before), or `X` to exit without launching.

Every subsequent run in the same project just resumes: no re-prompting for OmniRoute, provider connection, or dependency installs unless something's actually missing or reset.

### Picking a model

Model selection lives entirely inside Claude Code's own `/model` picker — this script doesn't prompt for it. The picker is restricted to exactly four entries, all routed through OmniRoute's compression pipeline:

- **Opus 4.8**
- **Sonnet 5**
- **Fable 5**
- **Haiku 4.5**

No `auto/*` combo or older/duplicate model version appears in the list.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 99 | Unexpected error |
| 100 | Duplicate instance already running |
| 101 | Unsupported Windows version |
| 102 | Missing required dependency that couldn't be auto-installed |
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

- **"Missing closing '}'" or other parse errors** — the `.ps1` file got truncated during a copy/download. Re-copy it fresh rather than re-running a partial copy; verify the file ends with a bare `Main` call on the last line.
- **OmniRoute never comes online** — it can take 10–20s on first boot; the script waits up to 45s with a progress bar. If it still fails, start it manually with `omniroute` in its own terminal.
- **`/model` picker doesn't show gateway models** — needs Claude Code v2.1.129+ and `CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY=1` (set automatically by this script whenever OmniRoute routing is active).
- **Claude keeps launching on the wrong model** — Claude Code caches your last `/model` pick as the session default. Either pick Opus/Sonnet/Fable/Haiku again in `/model`, or launch with `-Model sonnet` / `-Model opus` to force it for one session.
