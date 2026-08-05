# LLM-TokenOptimizer

A self-bootstrapping, production-quality PowerShell launcher for Windows that indexes a local codebase with [Graphify](https://graphify.com), installs matching AI skills with `autoskills`, and launches [Claude Code](https://claude.ai) routed through [OmniRoute](https://github.com/diegosouzapw/OmniRoute) for automatic prompt compression — all with zero manual setup, on a completely clean Windows install.

**Ensure you run this command in PowerShell before first use:**

```powershell
Set-ExecutionPolicy RemoteSigned -Scope CurrentUser
```

## What it does

- **Bootstraps a clean Windows PC from scratch.** Detects Git, Node.js, npm, Python, and pip; auto-installs anything missing via `winget` (falling back to a per-user install if machine-scope requires admin rights, and checking the install result's numeric exit code as well as its text so this works on non-English-language Windows too). If `winget` itself isn't available, it prints manual install links instead of failing.
- **Installs and verifies Graphify.** Installs/upgrades Graphify via `pip install --upgrade graphifyy`. Discovers and adds Python's user-scripts directory (including non-standard locations like Microsoft Store Python) to the session PATH so `graphify` is immediately found. Also registers Graphify's own Claude Code integration: a `PreToolUse` hook and strict mode (`graphify claude install`), which block a raw source-file read before the graph exists and redirect you to `graphify query`/`path`/`explain` instead — best-effort, and skipped with a warning rather than failing the launch if your Graphify version doesn't support it.
- **Extracts a knowledge graph of the project.** Runs a full scan (`graphify .`) for a new project, or an incremental `graphify update` once a graph already exists (falling back to a full rescan if the installed version doesn't support `update`). A brand-new or non-code-only folder can trip Graphify's own semantic-extraction gate ("detected non-code corpus files..."); the launcher retries once with the appropriate skip flag, and if it still can't build a graph, it says so and launches Claude anyway with no graph rather than stopping.
- **Installs and runs `autoskills`.** Detects the project's tech stack and installs matching Claude Code skills from the skills.sh registry on every launch, fully non-interactively (`npx -y autoskills -y -a claude-code`).
- **Installs a fixed set of companion tooling once, at user scope**, so every project gets it with no per-project step and no on/off switch: `claude-mem` (persistent cross-session memory), `headroom` (a context-window usage bar in the statusline, via Git Bash), the official `claude-code-setup` plugin (scans a project and recommends MCP servers/skills/hooks), `task-observer` (a skill that logs workflow friction), and the official `claude-md-management` plugin (audits and maintains CLAUDE.md itself). The same step also clones the Superpowers framework and installs a handful of prompt-skill manifests (`last30days`, `frontend-design`, `bencium-controlled-ux-designer`, `graphify`, `impeccable`) into `~/.claude/skills`. Each piece is independently best-effort — a failed install warns and moves on rather than stopping the launch — and every one is skipped once it's already recorded as installed.
- **Finds or installs Claude Code.** Checks PATH, common install directories, and the Windows registry; runs the official `irm https://claude.ai/install.ps1 | iex` installer if not found; falls back to a native file picker if all else fails. Actually runs `claude --version` to confirm the resolved path works (not just that a file exists) before trusting it, and remembers the resolved path for next time.
- **Auto-installs and starts OmniRoute.** Installs via `npm install -g omniroute@latest` if missing, checks for updates, and launches it in its own minimized, titled console window (separate from both the launcher and Claude Code). The server is started once and shared by all project windows.
- **Routes Claude Code through OmniRoute automatically — there's no flag or prompt to turn this off.** OmniRoute is pushed to its **Stacked** compression mode (RTK → Caveman, the strongest documented combo) on every launch, using only the already-saved API key; the launcher reads the setting back after writing it to confirm it actually took effect (OmniRoute has a known issue where success isn't always reliably reported) and re-verifies it periodically rather than trusting a single push forever. Model selection happens inside Claude Code's own `/model` picker; the launcher pins exactly two 1M-context models and restricts the picker to them.
- **Pins the `/model` picker to Opus 5 (1M) and Sonnet 5 (1M), resolved from OmniRoute's live catalog.** Both models inherently carry a 1-million token context window — there is no smaller variant. The launcher writes `ANTHROPIC_DEFAULT_OPUS_MODEL` / `ANTHROPIC_DEFAULT_SONNET_MODEL` for the session, sets `CLAUDE_CODE_AUTO_COMPACT_WINDOW` to 900k tokens and `CLAUDE_CODE_MAX_OUTPUT_TOKENS` to 128k (matching the models), and restricts `~/.claude/settings.json`'s `availableModels` so only these two OmniRoute-resolved IDs appear in the picker. No `auto/*` combo or older model versions are listed. If `settings.json` already exists but fails to parse, the write is aborted rather than replacing it with an empty file, and a `.bak` copy is kept before every successful update.
- **Sets up OmniRoute itself with no manual steps beyond one real account sign-in.** The dashboard login and API key are obtained headlessly (a local login against OmniRoute's own session endpoint, trying a remembered password and then its documented first-run default, then minting a key the same way the dashboard's own "create key" button does) — falling back to a manual key prompt only if that fails. The one step that genuinely can't be automated is connecting your actual Claude.ai account as a provider (a real OAuth sign-in); if OmniRoute's catalog shows no Claude models yet, the script opens the dashboard straight to `/dashboard/providers/claude` and waits for you to click **+ Add** and sign in. Once verified, none of this is asked again.
- **Registers OmniRoute as an MCP server for Claude Code** (`claude mcp add ... omniroute`, user scope) once a verified key exists, so a Claude Code session can inspect and adjust OmniRoute's own routing/compression/quota state as tools, not just run behind it.
- **Remembers your OmniRoute setup permanently.** The API key is validated against OmniRoute's `/v1/models` endpoint; a rejected key (401/403) is the **only** thing that triggers a re-prompt. An unreachable server never discards a working key.
- **Multi-window: run several projects at the same time.** The launcher prompts once for a **master folder** (the parent directory containing your project subfolders). You then pick which subfolders to open — one, several (`1,3,7`), or all of them (`a`) — and each chosen project gets its own independent console window with its own Graphify extraction and Claude Code session. `n` creates a brand-new folder inside the master folder on the spot (it shows up as a numbered project on the next refresh); `m` opens the master folder itself as a single project. The launcher stays open as a control panel so you can open more windows whenever you want.
- **Per-project instance lock.** Two windows on the *same* folder are still blocked (they would fight over the same `.graphify` output and `.claude/settings.json`), but different projects run simultaneously without conflict. Config is written with a cross-process lock and merge so concurrent windows don't clobber each other's project history, saved credentials, or "already installed" flags.
- **Resumes your previous Claude session automatically** when you reopen a project you've used before (`claude --continue`), and if there's no previous session to resume, falls back to starting a fresh one instead of erroring out.
- **Lets you force a specific model for one launch** with `-Model sonnet` or `-Model opus`, without touching whatever's saved as your Claude Code default.
- **Remembers your last 20 project paths and master folders**, navigable with Up/Down arrow keys in the inline path editor, with Delete to remove an entry.
- **Asks about updates fresh every launch, but only in the launcher window** ("Check for updates now?") — say yes and it checks Git, Node, Python, npm, Graphify, Claude Code, and OmniRoute for newer versions.
- **A hidden full-uninstall shortcut.** Typing `rm` within the first few seconds of a launcher window (not a project window) and confirming with `X` uninstalls the global npm tools this script manages — Claude Code CLI, OmniRoute, `claude-mem`, `autoskills`.
- **Logs everything** with millisecond timestamps and PID to `%LOCALAPPDATA%\LLM-TokenOptimizer\logs\`, with automatic rotation (keeps the last 10 log files).
- **Cleans up guaranteed on exit** — mutex release and config save run inside `try/catch/finally` and a `PowerShell.Exiting` engine event, so they run even on a crash or Ctrl+C. Blocking prompts are also never reached in an unattended, spawned project window — they warn and degrade instead of waiting on a keypress nobody may give.

## Requirements

Nothing needs to be pre-installed. On a totally clean Windows 10 (2004+) or Windows 11 machine, the script installs everything itself via `winget`, `npm`, and `pip` as needed:

- Git (also provides the Git Bash used to install the `headroom` statusline)
- Node.js + npm
- Python + pip
- Graphify CLI (via pip)
- Claude Code CLI (via the official installer)
- OmniRoute CLI (via npm)
- `autoskills` (via npm, on first use)
- `claude-mem`, `headroom`, `claude-code-setup`, `task-observer`, `claude-md-management` (companion tooling, installed once at user scope)

If `winget` isn't available on the machine (very old Windows 10 builds, or it's disabled by policy), the script degrades gracefully: it tells you exactly what's missing and where to get it manually instead of failing outright.

## Usage

```powershell
.\LLM-TokenOptimizer.ps1
```

### Flags

| Flag | Effect |
|---|---|
| `-VerboseMode` | Prints detailed debug logs to the console. |
| `-ForceUpdate` | Runs the update check for Git/Node/Python/npm/Graphify/Claude Code/OmniRoute without asking first. |
| `-SkipUpdateCheck` | Skips the "Check for updates now?" step entirely — no prompt, no check. `-ForceUpdate` wins if both are passed. |
| `-ResetConfig` | Deletes the saved JSON config and starts fresh (re-asks every first-run prompt). Launcher window only. |
| `-Model sonnet` \| `-Model opus` | Forces this one session onto Sonnet 5 or Opus 5, regardless of whatever Claude Code has saved as its default. Session-only — doesn't persist. |
| `-MasterFolder "C:\path"` | Skips the master-folder prompt and uses the given directory as the project parent. |
| `-ProjectPath "C:\path"` | Opens a single project directly (bypasses the multi-window picker). |
| `-ChildWindow` | Internal: marks this process as a spawned project window so it skips setup the launcher window already did, and never blocks on a prompt nobody may be watching. Set automatically when the launcher opens a project for you. |
| `-IsolateClaudeConfig` | Gives this project its own `CLAUDE_CONFIG_DIR` so settings, history, and credentials are separate from your normal `~/.claude`. |
| `-ReconfigureOmniRoute` | Forgets the saved OmniRoute API key and setup state, then re-prompts as if it's the first run; also forces an immediate re-check of the compression setting instead of waiting for the periodic recheck. |

OmniRoute routing itself has no on/off flag — it's always part of the launch. If OmniRoute can't be reached at all, the script warns and launches Claude Code directly (uncompressed, asking you to log in normally) rather than blocking.

### First run

**Launcher window** (numbered `[N/6]` steps, plus two unnumbered ones):

1. **Environment** — verifies Windows 10+.
2. **Dependencies** — detects Git/Node.js/npm/Python/pip/Graphify/Claude, auto-installing anything missing via `winget`.
3. **Graphify** — installed via pip if missing, version printed.
4. **Claude Code** — found on PATH/registry/common dirs, or installed via the official installer, or you're prompted for the path. The resolved path is actually run (`--version`) to confirm it works before it's trusted.
5. **Companion tooling** — `claude-mem`, `headroom`, `claude-code-setup`, `task-observer`, `claude-md-management`, Superpowers, and the bundled prompt skills, each installed once and skipped on later launches.
6. *(unnumbered, opt-in)* **Update checks** — "Check for updates now?"
7. **OmniRoute routing** — starts OmniRoute in its own window, configures Stacked compression automatically, obtains an API key headlessly (falling back to a manual prompt only if that fails), and — the one manual step — opens your browser straight to the dashboard's Claude provider page the first time no Claude.ai account is connected yet. Once verified, this never reappears.

**Then, in the launcher's picker:**

8. **Master folder** — type or browse to the parent directory that holds your projects.
9. **Project picker** — a numbered list of subfolders. Enter a number to open one, several numbers (`1,3,7`) to open that many at once, `a` for all of them, `n` to create a new folder on the spot, or `m` to open the master folder itself as a single project.

**Per-project window** (numbered `[N/4]` steps, plus a few unnumbered detail sections):

10. **Environment / Graphify / Claude Code** — the same toolchain checks, quick since the launcher already ran them.
11. **Graphify setup** — registers the PreToolUse hook and strict mode for this project (best-effort).
12. **Graph extraction** — full scan or incremental update; continues without a graph rather than failing the whole launch if extraction can't complete.
13. **AutoSkills** — detects the stack, installs matching skills.
14. **Launch Claude** — resumes your previous session in this project if one exists, otherwise starts fresh.

Every subsequent run in the same project just resumes: no re-prompting for OmniRoute, provider connection, or dependency installs unless something's actually missing or reset.

### Picking a model

Model selection lives entirely inside Claude Code's own `/model` picker — the launcher doesn't prompt for it. The picker is restricted to exactly two entries, both routed through OmniRoute's compression pipeline with the full 1M-token context window:

- **Opus 5 – 1M – OmniRoute**
- **Sonnet 5 – 1M – OmniRoute**

No `auto/*` combo or older model versions appear in the list.

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
| Config (project history, saved paths, encrypted API key/password) | `%LOCALAPPDATA%\LLM-TokenOptimizer\config.json` (backed up to `config.json.corrupt-<timestamp>` if it's ever found unparseable) |
| Logs | `%LOCALAPPDATA%\LLM-TokenOptimizer\logs\` |
| Isolated Claude profiles (`-IsolateClaudeConfig`) | `%LOCALAPPDATA%\LLM-TokenOptimizer\claude-profiles\<project-slug>\` |
| Graph output + studio | `<project>\.graphify\graph.json`, `<project>\.graphify\studio\studio.html` |
| Claude Code model restrictions | `~\.claude\settings.json` (`availableModels`; a `.bak` copy is kept before every write) |

## Troubleshooting

- **"Missing closing '}'" or other parse errors** — the `.ps1` file got truncated during a copy/download. Re-copy it fresh rather than re-running a partial copy.
- **OmniRoute never comes online** — it can take 10–20 s on first boot; the script waits up to 25 s. If it still fails, start it manually with `omniroute` in its own terminal.
- **Graph extraction fails on a brand-new or mostly-empty project** — this is Graphify's own semantic-extraction gate rejecting a folder with no code in it yet, not a launcher bug. The script retries once with a skip flag and, if it still can't build a graph, continues without one and launches Claude normally. Add some code and refresh (or reopen the project) once there's something for Graphify to index.
- **`/model` picker doesn't show gateway models** — needs Claude Code v2.1.129+ and `CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY=1` (set automatically by this script whenever OmniRoute routing is active).
- **Claude keeps launching on the wrong model** — Claude Code caches your last `/model` pick as the session default. Either pick Opus 5 / Sonnet 5 again in `/model`, or launch with `-Model sonnet` / `-Model opus` to force it for one session.
- **Graphify isn't found even after `pip install graphifyy` succeeded** — this can happen with Microsoft Store Python, which installs scripts to a non-standard location. The launcher automatically adds Python's user-scripts directory to PATH after every Graphify install; if detection still doesn't pick it up, close and reopen the launcher window.
- **Project window says "already open"** — only one window per project folder is allowed. Switch to the existing window, or close it and try again.
- **A spawned project window seems to skip a step or warn instead of prompting** — by design. Project windows never block on a prompt nobody may be watching (multi-window mode can open several at once); they warn and fall back to a safe default instead. Run the launcher window itself if you need to complete a one-time step like entering a rejected API key.
