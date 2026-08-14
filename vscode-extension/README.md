# LLM-TokenOptimizer (VS Code extension)

A **front door onto TokenOptimizer.App.exe's headless CLI**, not a
reimplementation. Every action here runs `TokenOptimizer.App.exe --cli
<command>` and reads back one JSON object. All the actual behavior -
Graphify extraction, companion tooling (claude-mem/headroom/RTK/Caveman/
context-mode/impeccable/...), the fallback chain (Claude Code -> Antigravity
-> local LM Studio model), provider hotswap, and benchmarking - lives in the
C# app (`TokenOptimizer.Core` / `TokenOptimizer.Providers`) and nowhere
else. This extension and the desktop app UI drive the exact same code, so
they can never drift into different behavior. There is no PowerShell
dependency anymore.

## Install (one click, no terminal needed)

Double-click **`Install.bat`** in this folder. That's it.

Don't double-click the `.vsix` file directly - Windows also associates
`.vsix` with Visual Studio's own installer (a *different*, unrelated format
that happens to share the extension), which will refuse it with a
"try installing in Visual Studio Code" error. `Install.bat` goes through VS
Code's own `code.cmd` launcher instead.

`TokenOptimizer.App.exe` needs to be reachable too - the MSI installer places
it next to this extension automatically; building from a repo checkout,
`dotnet build` under `app/` produces it and the extension auto-detects the
build output, or set `llmTokenOptimizer.appExecutablePath` explicitly.

## Using it - everything is UI, no command to remember

There is exactly **one** entry in the Command Palette:
**"LLM-TokenOptimizer: Start"**. Everything else lives in three places that
all point at the same actions, so nothing is Command-Palette-only or hidden:

1. **Activity Bar** - a rocket icon on the left edge of VS Code opens the
   LLM-TokenOptimizer view: every action as a clickable row, no typing.
2. **Status bar** - the `$(rocket) TokenOptimizer` item (bottom of the
   window) opens the same Start menu as a quick-pick.
3. **Chat view** - type `@tokenoptimizer` in VS Code's built-in Chat panel
   (works alongside the Anthropic Claude Code chat extension, if installed)
   and either describe what you want in plain language ("open this
   workspace", "reset config") or click one of the listed actions.

The actions, reachable from all three surfaces:

| Action | CLI command it runs |
|---|---|
| **Open Current Workspace as Project** | `--cli launch --project <workspace folder>` - resolves the fallback chain (or the configured provider), preps the shared `~/.claude` environment (Graphify hook, claude-mem tuning), and launches/resumes the session. |
| **Open Launcher (Master Folder Picker)** | `--cli master-folder-list --path <folder>` to list subprojects in a native multi-select QuickPick, then one `--cli launch` per selection - multiple independent sessions, same as the old multi-window picker. |
| **Change Master Folder** | Native VS Code folder picker + `--cli master-folder-set`, remembered for next time. |
| **Set Up Fallback Providers** | `--cli set-credential` (Codex/Groq, via a masked VS Code input box - the key never touches this extension's own logic, only the CLI argument) or `--cli opt-in` (Antigravity/Cursor). |
| **Transfer Session to Codex / Cursor** | `--cli launch --project <path> --provider Codex\|Cursor` - the provider adapter itself exports the session handoff (`.claude-handoff/session-handoff.md` + `AGENTS.md`) before launching, so this is one call, not two. |
| **Continue Locally** | `--cli launch --project <path> --provider "LM Studio (local)"` - uses the app's configured local model (manual override or the benchmark auto-pick), no credential needed. |
| **Reset Configuration** | `--cli reset-config`, behind a confirmation modal - deletes the saved config and starts fresh. |
| **Open Dashboard** | Independent of the CLI - tails the project's own Claude Code session transcript and `rtk gain --format json` directly, live. |
| **Open TokenOptimizer App** | Spawns `TokenOptimizer.App.exe` directly (no `--cli`) with this workspace pre-selected - the full desktop UI (provider dropdown, benchmark tab, dependency dashboard) for anything not exposed as a quick action here. |

The Command Palette itself only shows **Start** (a quick-pick over every
action) - the rest are still real, registered VS Code commands (so the tree
view and chat participant can invoke them), just deliberately hidden from
the palette (`contributes.menus.commandPalette`, `"when": "false"`) so
there's one obvious way in instead of many competing entries.

## Settings

- `llmTokenOptimizer.model` - forwards `--cli launch --model <id>` for a
  session-only override. Leave blank to use the provider's own default (or,
  for LM Studio, the app's configured local model).
- `llmTokenOptimizer.isolateClaudeConfig` - forwards `--cli launch --isolate`
  (gives the project its own `CLAUDE_CONFIG_DIR`).
- `llmTokenOptimizer.appExecutablePath` - path to `TokenOptimizer.App.exe`.
  Leave blank to auto-detect (bundled next to this extension via the MSI, or
  a local `dotnet build` output under this repo's `app/` folder).

## Why the CLI, not a reimplementation

Two front-ends (this extension and the Avalonia desktop UI) driving separate
copies of provider/fallback-chain/companion-tooling logic would drift the
moment one got a fix the other didn't. `TokenOptimizer.App.exe --cli` is the
single source of truth both call into - one JSON-over-stdout command per
action, no display server required, so it runs identically from a VS Code
child process or the desktop UI's own service layer.

## Building from source

```bash
npm install
npm run compile
npm test              # runs the automated suite in a real headless VS Code
npx @vscode/vsce package --allow-missing-repository --skip-license
```

`F5` in VS Code (with this folder open) launches an Extension Development
Host with the extension active, for interactive manual testing. Build
`TokenOptimizer.App.exe` first (`dotnet build` under `../app`) so the
extension's auto-detect finds something to call.
