# LLM-TokenOptimizer

A self-bootstrapping, production-quality PowerShell launcher for Windows that indexes a local codebase with [Graphify](https://graphify.com), installs matching AI skills with `autoskills`, and launches [Claude Code](https://claude.ai) with real token-saving tooling installed directly — [Caveman](https://github.com/JuliusBrussee/caveman) for terser model output and [RTK](https://github.com/rtk-ai/rtk) for terminal/tool-output compression — all with zero manual setup, on a completely clean Windows install.

As of v5.5, this no longer routes through [OmniRoute](https://github.com/diegosouzapw/OmniRoute) or any other third-party gateway. Claude Code launches natively, on your own account, with its own default models. See AUDIT.md Finding 0 and Finding 10 for why.

**Ensure you run this command in PowerShell before first use:**

```powershell
Set-ExecutionPolicy RemoteSigned -Scope CurrentUser
```

## What it does

- **Bootstraps a clean Windows PC from scratch.** Detects Git, Node.js, npm, Python, and pip; auto-installs anything missing via `winget` (falling back to a per-user install if machine-scope requires admin rights, and checking the install result's numeric exit code as well as its text so this works on non-English-language Windows too). If `winget` itself isn't available, it prints manual install links instead of failing.
- **Installs and verifies Graphify.** Installs/upgrades Graphify via `pip install --upgrade graphifyy`. Discovers and adds Python's user-scripts directory (including non-standard locations like Microsoft Store Python) to the session PATH so `graphify` is immediately found. Also registers Graphify's own Claude Code integration: a `PreToolUse` hook and strict mode (`graphify claude install`), which block a raw source-file read before the graph exists and redirect you to `graphify query`/`path`/`explain` instead — best-effort, and skipped with a warning rather than failing the launch if your Graphify version doesn't support it.
- **Extracts a knowledge graph of the project.** Runs a full scan (`graphify .`) for a new project, or an incremental `graphify update` once a graph already exists (falling back to a full rescan if the installed version doesn't support `update`). A brand-new or non-code-only folder can trip Graphify's own semantic-extraction gate ("detected non-code corpus files..."); the launcher retries once with the appropriate skip flag, and if it still can't build a graph, it says so and launches Claude anyway with no graph rather than stopping.
- **Installs and runs `autoskills`.** Detects the project's tech stack and installs matching Claude Code skills from the skills.sh registry on every launch, fully non-interactively (`npx -y autoskills -y -a claude-code`).
- **Installs a fixed set of companion tooling once, at user scope**, so every project gets it with no per-project step and no on/off switch: `claude-mem` (persistent cross-session memory), `headroom` (a context-window usage bar in the statusline, via Git Bash), the official `claude-code-setup` plugin (scans a project and recommends MCP servers/skills/hooks), `task-observer` (a skill that logs workflow friction), the official `claude-md-management` plugin (audits and maintains CLAUDE.md itself), **Caveman** (a real Claude Code plugin that makes the model's own responses terser, active from message one), and **RTK** (a standalone local binary wired in as a Claude Code hook that compresses terminal/tool output before it reaches the model). The same step also clones the Superpowers framework and installs a handful of prompt-skill manifests (`last30days`, `frontend-design`, `bencium-controlled-ux-designer`, `graphify`, `impeccable`) into `~/.claude/skills`. Each piece is independently best-effort — a failed install warns and moves on rather than stopping the launch — and every one is skipped once it's already recorded as installed.
- **Finds or installs Claude Code.** Checks PATH, common install directories, and the Windows registry; runs the official `irm https://claude.ai/install.ps1 | iex` installer if not found; falls back to a native file picker if all else fails. Actually runs `claude --version` to confirm the resolved path works (not just that a file exists) before trusting it, and remembers the resolved path for next time.
- **Caveman ([github.com/JuliusBrussee/caveman](https://github.com/JuliusBrussee/caveman), MIT)** — installed as a real Claude Code plugin (`claude plugin marketplace add JuliusBrussee/caveman` + `claude plugin install caveman@caveman`), not a stub. Its `SessionStart` hook makes the model's own responses terser from the first message, with no manual enable step. Fully local: no API key, no network calls after install. Adjust or disable anytime inside a session with `/caveman [lite|full|ultra|off]`.
- **RTK ([github.com/rtk-ai/rtk](https://github.com/rtk-ai/rtk), Apache-2.0)** — a standalone local binary, downloaded directly from its official GitHub release (no winget package exists for it yet) and registered as a Claude Code `PreToolUse` hook (`rtk init -g`) that transparently rewrites Bash tool calls (e.g. `git log` → `rtk git log`) so command/tool output is filtered and compressed before it reaches the model. Fully local: no API key, no gateway server. RTK's own hook script needs `bash` + `jq`; `bash` comes from Git for Windows (already a required dependency here), and `jq` is installed via winget if missing.
- **Multi-window: run several projects at the same time.** The launcher prompts once for a **master folder** (the parent directory containing your project subfolders). You then pick which subfolders to open — one, several (`1,3,7`), or all of them (`a`) — and each chosen project gets its own independent console window with its own Graphify extraction and Claude Code session. `n` creates a brand-new folder inside the master folder on the spot (it shows up as a numbered project on the next refresh); `m` opens the master folder itself as a single project. The launcher stays open as a control panel so you can open more windows whenever you want.
- **Per-project instance lock.** Two windows on the *same* folder are still blocked (they would fight over the same `.graphify` output and `.claude/settings.json`), but different projects run simultaneously without conflict. Config is written with a cross-process lock and merge so concurrent windows don't clobber each other's project history, saved credentials, or "already installed" flags.
- **Resumes your previous Claude session automatically** when you reopen a project you've used before (`claude --continue`), and if there's no previous session to resume, falls back to starting a fresh one instead of erroring out.
- **Lets you force a specific model for one launch** with `-Model sonnet` or `-Model opus`, without touching whatever's saved as your Claude Code default.
- **Remembers your last 20 project paths and master folders**, navigable with Up/Down arrow keys in the inline path editor, with Delete to remove an entry.
- **Asks about updates fresh every launch, but only in the launcher window** ("Check for updates now?") — say yes and it checks Git, Node, Python, npm, Graphify, and Claude Code for newer versions.
- **A hidden full-uninstall shortcut.** Typing `rm` within the first few seconds of a launcher window (not a project window) and confirming with `X` uninstalls the global npm tools this script manages — Claude Code CLI, `claude-mem`, `autoskills`, plus RTK and the Caveman/other plugins.
- **Logs everything** with millisecond timestamps and PID to `%LOCALAPPDATA%\LLM-TokenOptimizer\logs\`, with automatic rotation (keeps the last 10 log files).
- **Cleans up guaranteed on exit** — mutex release and config save run inside `try/catch/finally` and a `PowerShell.Exiting` engine event, so they run even on a crash or Ctrl+C. Blocking prompts are also never reached in an unattended, spawned project window — they warn and degrade instead of waiting on a keypress nobody may give.

## Requirements

Nothing needs to be pre-installed. On a totally clean Windows 10 (2004+) or Windows 11 machine, the script installs everything itself via `winget`, `npm`, and `pip` as needed:

- Git (also provides the Git Bash used to install the `headroom` statusline)
- Node.js + npm
- Python + pip
- Graphify CLI (via pip)
- Claude Code CLI (via the official installer)
- `autoskills` (via npm, on first use)
- `claude-mem`, `headroom`, `claude-code-setup`, `task-observer`, `claude-md-management`, `caveman`, `rtk` + `jq` (companion tooling, installed once at user scope)

If `winget` isn't available on the machine (very old Windows 10 builds, or it's disabled by policy), the script degrades gracefully: it tells you exactly what's missing and where to get it manually instead of failing outright.

## Usage

```powershell
.\LLM-TokenOptimizer.ps1
```

### Flags

| Flag | Effect |
|---|---|
| `-VerboseMode` | Prints detailed debug logs to the console. |
| `-ForceUpdate` | Runs the update check for Git/Node/Python/npm/Graphify/Claude Code without asking first. |
| `-SkipUpdateCheck` | Skips the "Check for updates now?" step entirely — no prompt, no check. `-ForceUpdate` wins if both are passed. |
| `-ResetConfig` | Deletes the saved JSON config and starts fresh (re-asks every first-run prompt). Launcher window only. |
| `-Model sonnet` \| `-Model opus` | Forces this one session onto Sonnet 5 or Opus 5, regardless of whatever Claude Code has saved as its default. Session-only — doesn't persist. |
| `-MasterFolder "C:\path"` | Skips the master-folder prompt and uses the given directory as the project parent. |
| `-ProjectPath "C:\path"` | Opens a single project directly (bypasses the multi-window picker). |
| `-ChildWindow` | Internal: marks this process as a spawned project window so it skips setup the launcher window already did, and never blocks on a prompt nobody may be watching. Set automatically when the launcher opens a project for you. |
| `-IsolateClaudeConfig` | Gives this project its own `CLAUDE_CONFIG_DIR` so settings, history, and credentials are separate from your normal `~/.claude`. |

### First run

**Launcher window** (numbered `[N/5]` steps, plus one unnumbered one):

1. **Environment** — verifies Windows 10+.
2. **Dependencies** — detects Git/Node.js/npm/Python/pip/Graphify/Claude, auto-installing anything missing via `winget`.
3. **Graphify** — installed via pip if missing, version printed.
4. **Claude Code** — found on PATH/registry/common dirs, or installed via the official installer, or you're prompted for the path. The resolved path is actually run (`--version`) to confirm it works before it's trusted.
5. **Companion tooling** — `claude-mem`, `headroom`, `claude-code-setup`, `task-observer`, `claude-md-management`, `caveman`, `rtk`, Superpowers, and the bundled prompt skills, each installed once and skipped on later launches.
6. *(unnumbered, opt-in)* **Update checks** — "Check for updates now?"

**Then, in the launcher's picker:**

7. **Master folder** — type or browse to the parent directory that holds your projects.
8. **Project picker** — a numbered list of subfolders. Enter a number to open one, several numbers (`1,3,7`) to open that many at once, `a` for all of them, `n` to create a new folder on the spot, or `m` to open the master folder itself as a single project.

**Per-project window** (numbered `[N/3]` steps, plus a few unnumbered detail sections):

9. **Environment / Graphify / Claude Code** — the same toolchain checks, quick since the launcher already ran them.
10. **Graphify setup** — registers the PreToolUse hook and strict mode for this project (best-effort).
11. **Graph extraction** — full scan or incremental update; continues without a graph rather than failing the whole launch if extraction can't complete.
12. **AutoSkills** — detects the stack, installs matching skills.
13. **Launch Claude** — resumes your previous session in this project if one exists, otherwise starts fresh.

Every subsequent run in the same project just resumes: no re-prompting or dependency installs unless something's actually missing or reset.

### Picking a model

Model selection lives entirely inside Claude Code's own `/model` picker — the launcher doesn't touch it. Claude Code launches with its own native model defaults; there's no gateway, no pinned model IDs, and no restricted picker.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 99 | Unexpected error |
| 100 | This project is already open in another LLM-TokenOptimizer window |
| 101 | Unsupported Windows version |
| 102 | Missing required dependency that couldn't be auto-installed |
| 103 | Claude Code not found or couldn't be verified working |
| 104 | Graphify installation failed |
| 106 | Project folder is not usable (bad path, no write permission, etc.) |

Graph extraction failing on its own is **not** a fatal exit condition — the launcher logs it and continues without a graph.

## Where things live

| What | Location |
|---|---|
| Config (project history, saved paths) | `%LOCALAPPDATA%\LLM-TokenOptimizer\config.json` (backed up to `config.json.corrupt-<timestamp>` if it's ever found unparseable) |
| Logs | `%LOCALAPPDATA%\LLM-TokenOptimizer\logs\` |
| Isolated Claude profiles (`-IsolateClaudeConfig`) | `%LOCALAPPDATA%\LLM-TokenOptimizer\claude-profiles\<project-slug>\` |
| Graph output + studio | `<project>\.graphify\graph.json`, `<project>\.graphify\studio\studio.html` |
| RTK binary | `%LOCALAPPDATA%\rtk\rtk.exe` |
| RTK's Claude Code hook | `~\.claude\hooks\rtk-rewrite.sh` |

## Troubleshooting

- **"Missing closing '}'" or other parse errors** — the `.ps1` file got truncated during a copy/download. Re-copy it fresh rather than re-running a partial copy.
- **Graph extraction fails on a brand-new or mostly-empty project** — this is Graphify's own semantic-extraction gate rejecting a folder with no code in it yet, not a launcher bug. The script retries once with a skip flag and, if it still can't build a graph, continues without one and launches Claude normally. Add some code and refresh (or reopen the project) once there's something for Graphify to index.
- **Graphify isn't found even after `pip install graphifyy` succeeded** — this can happen with Microsoft Store Python, which installs scripts to a non-standard location. The launcher automatically adds Python's user-scripts directory to PATH after every Graphify install; if detection still doesn't pick it up, close and reopen the launcher window.
- **RTK's hook doesn't seem to do anything** — it needs `bash` and `jq` on PATH. `bash` comes from Git for Windows; if `jq` failed to install via winget, install it manually (`winget install jqlang.jq`) and restart the launcher.
- **Caveman doesn't seem active** — check inside a session with `/caveman` (no arguments shows current status). It activates via a `SessionStart` hook, so a session that was already running before Caveman was installed won't have it until you start a new one.
- **Project window says "already open"** — only one window per project folder is allowed. Switch to the existing window, or close it and try again.
- **A spawned project window seems to skip a step or warn instead of prompting** — by design. Project windows never block on a prompt nobody may be watching (multi-window mode can open several at once); they warn and fall back to a safe default instead. Run the launcher window itself if you need to complete a one-time setup step.
