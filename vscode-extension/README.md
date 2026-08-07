# LLM-TokenOptimizer (VS Code extension)

A **thin wrapper**, not a reimplementation. Every action here shells out to
the bundled `scripts/LLM-TokenOptimizer.ps1` (a copy of the standalone
launcher, `../LLM-TokenOptimizer.ps1`) via a VS Code integrated terminal. All
the actual behavior - Graphify extraction, OmniRoute setup and compression,
companion tooling (claude-mem/headroom/claude-code-setup/task-observer/
claude-md-management), the v5.0 quota auto-retry watcher, and multi-session
support - lives in that script, unchanged. This extension only adds a VS
Code-native front door instead of a standalone console window you'd have to
launch by hand.

This was a deliberate scope choice over a full native rewrite: it ships fast
and inherits everything already verified in the standalone script (syntax
parse, C# type compile/smoke-test, live console test) instead of
re-introducing risk into every reimplemented piece. See `../AUDIT.md` for why
the underlying script's tooling choices were made the way they were.

## Install (one click, no terminal needed)

Double-click **`Install.bat`** in this folder. That's it.

Don't double-click the `.vsix` file directly - Windows also associates
`.vsix` with Visual Studio's own installer (a *different*, unrelated format
that happens to share the extension), which will refuse it with a
"try installing in Visual Studio Code" error. `Install.bat` goes through VS
Code's own `code.cmd` launcher instead, which is the correct path - verified
by actually uninstalling and reinstalling through it, not just written and
assumed to work.

## Using it - everything is UI, no command to remember

There is exactly **one** entry in the Command Palette:
**"LLM-TokenOptimizer: Start"**. Everything else lives in three places that
all point at the same five underlying actions, so nothing is Command-Palette
-only or hidden:

1. **Activity Bar** - a rocket icon on the left edge of VS Code opens the
   LLM-TokenOptimizer view: every action as a clickable row, no typing.
2. **Status bar** - the `$(rocket) TokenOptimizer` item (bottom of the
   window) opens the same Start menu as a quick-pick.
3. **Chat view** - type `@tokenoptimizer` in VS Code's built-in Chat panel
   (works alongside the Anthropic Claude Code chat extension, if installed)
   and either describe what you want in plain language ("open this
   workspace", "reset config") or click one of the listed actions - the chat
   participant lists the same five actions as clickable links.

The five actions, reachable from all three surfaces:

| Action | What it runs |
|---|---|
| **Open Current Workspace as Project** | `-ProjectPath <workspace folder> -ChildWindow` - the same code path the standalone launcher's own picker spawns for a chosen subfolder. Includes the v5.0 resume-mode prompt (Continue / Pick a past session / New) and the rate-limit watcher. |
| **Open Launcher (Master Folder Picker)** | `-MasterFolder <folder>` - the interactive picker over that folder's subprojects, same as running the script with no arguments. |
| **Change Master Folder** | Native VS Code folder picker, remembered for next time. |
| **Reconfigure OmniRoute** | `-ReconfigureOmniRoute` - forgets the saved key and redoes onboarding. |
| **Reset Configuration** | `-ResetConfig`, behind a confirmation modal - forgets everything saved. |

The Command Palette itself only shows **Start** (a quick-pick over the same
five actions) - the other five commands are still real, registered VS Code
commands (so the tree view and chat participant can invoke them), just
deliberately hidden from the palette (`contributes.menus.commandPalette`,
`"when": "false"`) so there's one obvious way in instead of six competing
entries.

## Settings

- `llmTokenOptimizer.scriptPath` - override the bundled script with a
  different copy (e.g. to point at `../LLM-TokenOptimizer.ps1` directly
  during development instead of the bundled copy, so edits don't need
  re-copying).
- `llmTokenOptimizer.powershellExecutable` - default `powershell.exe`;
  set to `pwsh.exe` for PowerShell 7+.
- `llmTokenOptimizer.model` - forwards `-Model sonnet|opus` for a
  session-only override.
- `llmTokenOptimizer.compressionMode` - forwards `-CompressionMode
  stacked|ultra|off` for a session-only override of OmniRoute's pinned
  Stacked compression. See `../AUDIT.md` Finding 3: Stacked may forfeit
  Anthropic's prompt-cache discount on long multi-turn sessions by rewriting
  prompt bytes turn-to-turn; use `off` to keep the cache prefix stable while
  measuring, or to opt out on sessions where that matters more than
  compression's own savings.
- `llmTokenOptimizer.isolateClaudeConfig` - forwards `-IsolateClaudeConfig`.
- `llmTokenOptimizer.verboseMode` - forwards `-VerboseMode`.

## Why a terminal, not a spawned hidden process

The wrapped script is deeply interactive (`Read-Host` prompts, colored
console output, and - as of v5.0 - a rate-limit watcher that reads and
writes real Win32 console input/output events). Running it in a real VS Code
integrated terminal preserves all of that instead of trying to proxy stdin/
stdout through the extension, which is both simpler and doesn't fight the
console-buffer APIs the watcher depends on.

## Building from source

```bash
npm install
npm run compile
npm test              # runs the automated suite in a real headless VS Code
npx @vscode/vsce package --allow-missing-repository --skip-license
```

`F5` in VS Code (with this folder open) launches an Extension Development
Host with the extension active, for interactive manual testing.

## Verification record

- Full TypeScript compile: clean, including the test suite.
- 6 automated tests, run in a genuine headless VS Code Extension Host (not
  mocked): extension activates without throwing; all commands register; the
  no-workspace guard on "Open Current Workspace" doesn't throw; the settings
  schema matches; the Activity Bar view, its container, and the chat
  participant are all correctly declared in `package.json`; and the Command
  Palette shows exactly one visible command (Start) with the other five
  correctly hidden but still present.
- Packaged with `vsce package`, installed via `code --install-extension`,
  confirmed present via `code --list-extensions`.
- `Install.bat` itself was live-tested, not just written: the extension was
  uninstalled, then reinstalled purely by running the batch file, then
  reconfirmed via `code --list-extensions`. This caught a real bug on the
  first attempt - Windows PATH has both an extensionless `code` (a
  Unix/git-bash-style shim, not directly runnable by `cmd.exe`) and the real
  `code.cmd` launcher in the same directory; a bare `where code` check
  matched the wrong one and failed with a garbled path error. Fixed by
  resolving `code.cmd` specifically, then re-tested clean.

Not verified (would need a human at the keyboard): actually clicking through
the Activity Bar view, status bar item, and `@tokenoptimizer` chat mention in
a live, visible VS Code window. The structural/activation-level checks above
confirm they're wired correctly; a first-hand look is still worth doing.
