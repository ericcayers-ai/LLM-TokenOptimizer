#Requires -Version 5.1
<#
.SYNOPSIS
    LLM-TokenOptimizer - Production Quality v4.0
.DESCRIPTION
    Self-bootstrapping launcher that verifies the environment, installs
    dependencies, generates Graphify graphs, and launches Claude Code reliably
    on any Windows 10/11 PC. References itself as LLM-TokenOptimizer throughout.

    v4.0 - three changes:

    1) MULTI-WINDOW. The launcher no longer runs one project at a time behind a
       global single-instance mutex. You now pick a MASTER FOLDER once (the
       parent directory that holds your projects); the launcher lists the
       subfolders inside it and you choose which ones to open. Each chosen
       subfolder gets its own independent console window running its own
       Graphify extraction and its own Claude Code session, and they all run
       at the same time. The launcher window stays open as a control panel so
       you can open more project windows whenever you want. The instance lock
       is now per-project (two windows on the SAME folder is still blocked -
       they would fight over the same .graphify output), and config.json is
       written with a cross-process lock + merge so concurrent windows don't
       clobber each other's project history.

    2) SETUP IS REMEMBERED. Previous versions re-ran the OmniRoute onboarding
       (API key prompt, "open the dashboard and connect Claude Code") on
       basically every launch, because the only connectivity probe was
       `omniroute providers list --json` and any failure of that command read
       as "not connected". Now the saved API key is validated against
       OmniRoute's own /v1/models endpoint, a rejected key (401/403) is the
       ONLY thing that triggers a re-prompt, an unreachable server never
       discards a good key, and once the Claude provider has been seen working
       the result is recorded in config.json and never asked about again.
       Use -ReconfigureOmniRoute to deliberately redo that setup.

    3) 1M-CONTEXT MODELS, DISTINCT FROM THE DEFAULTS. Claude Opus 5 and Claude
       Sonnet 5 both carry a 1M-token context window as BOTH the default and
       the maximum - per Anthropic's model docs there is no smaller context
       variant and no separate "1m" model ID for either one, so the old
       `claude-sonnet-5(?!.*1m)` exclusion was filtering for something that
       does not exist. Model resolution now reads OmniRoute's live /v1/models
       catalog, prefers the `cc/` (Claude Code OAuth) provider prefix that
       OmniRoute documents for Claude-family models, and accepts an entry only
       when the catalog agrees it carries a >=1M context window (or is a
       -5 model, which is 1M by definition). Claude Code's auto-compaction
       window and output cap are raised to match, otherwise the client
       compacts at ~190k and the 1M window goes unused. The two entries are
       pinned to their resolved OmniRoute catalog IDs and labelled
       "Opus 5 - 1M - OmniRoute" / "Sonnet 5 - 1M - OmniRoute" so they are
       visibly distinct from Claude Code's built-in defaults, and
       availableModels is restricted to exactly those two.

    v3.1: fixed the Graphify output path for Graphify 0.17.1+, which now
    writes to a hidden .graphify\graph.json (not graphify-out\graph.json) and
    auto-generates the HTML studio during `extract` itself.

    v3.0: pxpipe removed entirely; Claude Code routes through OmniRoute, which
    applies its own compression pipeline (RTK -> Caveman -> LLMLingua -> Lite).

    v4.0.1 - bug-fix pass:
    - 'm' (open the master folder itself) could go unreachable: a master
      folder with zero project subfolders bounced you straight back to the
      "pick a master folder" prompt before you ever saw the picker menu that
      the 'm' key lives on. The picker now always shows, even with an empty
      list.
    - New 'n' key in the picker: creates a new folder directly inside the
      master folder, which then shows up as a numbered project on the next
      refresh.
    - Empty folders (freshly created, or an empty git clone target) are now
      valid projects. Test-ProjectDirectory / Test-MasterFolder used to
      hard-reject anything with zero files in it.
    - Install-Graphify was called in two places but never defined anywhere
      in the script - any machine without Graphify already on PATH (i.e.
      every clean install) hit an undefined-function error and stopped dead.
      Added a real implementation (pip install, with a --user fallback and a
      PATH refresh so it's usable immediately without reopening the shell).
    - Test-GraphifyVersion called Get-GraphifyCommand, which also didn't
      exist anywhere - this ran on every single launch and would have
      crashed the launcher window immediately. Fixed to call graphify
      directly, same as the rest of the Graphify functions.
    - Removed ~160 lines of dead, fully-shadowed duplicate function
      definitions (Install-GraphifyPlatform/Hook/StrictMode,
      Invoke-GraphifyExtract, Find-GraphifySkipSemanticFlag all existed
      twice; PowerShell silently ran only the later copy).
    - Added a TLS 1.2/1.3 floor at startup for the script's own web calls,
      since a from-scratch Windows 11 install can otherwise start a
      PowerShell 5.1 session on an older default.

    v4.0.2 - flow reorganization:
    - Fixed Update-GraphifyIfNeeded, which was called from the (opt-in)
      update-check path but never defined - taking that path would have
      crashed the launcher.
    - The launcher no longer runs a blocking, unconditional `npm install -g
      npm@latest` before it even shows its title banner. That call ran on
      every single launch regardless of whether npm existed yet or whether
      you wanted an update check at all. It's now part of the same opt-in
      update-check step as everything else (Git/Node/Python/Graphify/Claude
      Code), so a normal launch is faster and the flow is consistent.
    - New -SkipUpdateCheck switch (skip the update step with no prompt) and
      the existing-but-previously-unused -ForceUpdate switch now actually
      does something (run it without prompting).
    - Both the launcher and each project window now run their setup in a
      strict dependency order - OS support, then PATH, then required tools,
      then Graphify, then Claude Code, then (optional) updates, then
      OmniRoute routing, which needs Claude Code to already be found - and
      show it as numbered steps ([1/6], [2/6], ...) so it reads like a
      checklist instead of a scroll of unlabelled sections.
    - A project window used to install Graphify, detect Claude Code, and
      run OmniRoute onboarding BEFORE checking whether the project folder
      itself was even usable - so a bad path failed only after all that
      work. Folder validation now runs first.

    v4.1.0 - companion tooling was defined but never wired in, plus fully
    headless OmniRoute onboarding:
    - Install-CompanionTooling (claude-mem, headroom, claude-code-setup,
      task-observer) existed as a function but was never called from either
      Invoke-LauncherMode or Invoke-ProjectMode - on a clean install none of
      the four ever actually installed. It's now step [5/6] in the launcher
      (after Claude Code is found, before the optional update check) and
      also runs from a standalone project window opened without the
      launcher, guarded so it's skipped once all five are recorded present.
    - Added a fifth companion tool: claude-md-management (Anthropic's own
      official plugin, same anthropics/claude-plugins-official marketplace
      as claude-code-setup). It audits CLAUDE.md quality and captures
      session learnings via /revise-claude-md - directly relevant here since
      this script already writes/merges CLAUDE.md itself.
    - claude-mem's installer is interactive by default (IDE multi-select,
      LLM-provider prompt) unless targeted with --ide. Added
      `--ide claude-code` to skip the IDE-detection prompt for the one IDE
      this launcher cares about; the existing 180s timeout is the fallback
      if a prompt still appears (it did before too - the call just used to
      spend the timeout on something that could never install).
    - Set-ProjectClaudeMdDirective now also writes a "Companion tooling"
      section (claude-mem / headroom / claude-code-setup / task-observer /
      claude-md-management, and how they coexist) alongside the existing
      Graphify section, so every project's CLAUDE.md documents the full
      toolset, not just Graphify.
    - OmniRoute's API key no longer requires a manual trip to the dashboard.
      Request-OmniRouteApiKeyAutomatically logs in headlessly against
      OmniRoute's own dashboard-session endpoint (POST /api/auth/login),
      trying a remembered password first and OmniRoute's documented
      first-run default (CHANGEME) after that, then mints a key via
      POST /api/keys using that session - matching what the bug tracker
      confirms is the same endpoint the dashboard's own "create API key"
      button calls. Only if every automatic attempt fails does it fall back
      to the original interactive Read-OmniRouteApiKey prompt, so a machine
      where the password was already changed by hand behaves exactly as
      before. The Claude Code PROVIDER connection inside OmniRoute (the
      OAuth sign-in to your actual Claude.ai account) is deliberately left
      alone - that's a real account sign-in, not something this script
      automates password entry for.
    - OmniRoute is now also registered as an MCP server for Claude Code
      itself (`claude mcp add --transport http --scope user omniroute ...`),
      once a verified key exists, so a Claude Code session can inspect and
      adjust OmniRoute's own routing/compression/quota state as tools
      instead of only ever being a client behind it.
    - Compression stays pinned to Stacked (still the strongest documented
      combo). Noted in comments only: OmniRoute issue #4268 reports Stacked
      sometimes under-reporting savings in the dashboard's analytics on real
      agent sessions even though it's compressing - if the dashboard numbers
      look flat, that's a known upstream display issue, not a sign this
      script's PUT to /api/settings/compression failed.

    v4.2.0 - robustness sweep, verified auto-compression, install
    verification, no behavioral change to what gets installed:
    - Several blocking Read-Host prompts (Start-OmniRoute's "press Enter"
      waits, Confirm-ClaudeCodeProvider's browser-signin wait, the manual
      OmniRoute API key prompt, Find-ClaudeExecutable's last-resort file
      picker) could be reached from a spawned/child project window with no
      guarantee anyone is watching it - the multi-window picker can open
      several at once. All now check $script:IsChild and skip straight to a
      warn-and-degrade path instead of blocking a window nobody may be
      looking at; the interactive launcher window is unaffected.
    - Invoke-CompleteUninstaller's "type rm to uninstall" listener used to
      run in every window, including spawned project windows - a child
      window is the wrong place to offer removing shared global tools out
      from under its sibling windows. Now launcher-only.
    - The official Claude Code installer fetch (irm https://claude.ai/
      install.ps1) had no timeout at all; a stalled download could hang the
      launcher indefinitely. Added -TimeoutSec 60.
    - Stop-Script's final "press Enter to close" wait was unbounded; it now
      gives up after 15 minutes and exits anyway, so a window nobody comes
      back to still closes instead of sitting open forever.
    - Test-ClaudeExecutable's native-binary check read the child process's
      output with a blocking, unbounded ReadToEnd() before ever applying its
      5-second WaitForExit - a hung `claude.exe --version` could block
      forever. Worse, if the version check failed OR threw, the catch block
      swallowed it and the function fell through to reporting success
      anyway ("Verified Claude binary path") purely because the file
      existed - so a broken Claude install was never actually caught. Now
      reuses Invoke-ExternalCommand (async reads, real timeout) and only
      returns true when the version check itself actually succeeded.
    - Both launcher and project-window setup now check Test-ClaudeExecutable's
      result instead of discarding it, retry via a manual path prompt
      (launcher only), and stop with exit code 103 (documented since v4.0 but
      never actually used) if Claude Code still can't be verified, rather
      than pressing on with a ClaudePath that was never confirmed to work.
    - Set-OmniRouteBestCompression now does a GET read-back after its PUT and
      retries once if the active mode doesn't match what was requested
      (OmniRoute's own issue #4268 notes success isn't always reliably
      reported). Still configures Stacked only, still uses only the
      already-saved API key, still doesn't nag after a manual dashboard
      change - but a new OmniRouteCompressionLastCheckedUtc timestamp makes
      it re-verify periodically (every 7 days) instead of trusting a single
      long-ago push forever, and -ReconfigureOmniRoute now forces an
      immediate re-check too.
    - claude-mem, claude-code-setup, claude-md-management, and headroom
      install-verification strengthened beyond "does a directory exist" /
      "did the shell command exit 0": claude-mem checks the marketplace
      directory actually has files in it, the two official plugins
      cross-check against `claude plugin list`, and headroom checks whether
      its statusline actually got wired into settings.json.

    v4.3.0 - final correctness pass: multi-window config races, dead control
    flow, resume-retry parity, and one remaining hang risk:
    - Save-Configuration's per-field merge only ever protected
      OmniRouteApiKeyEnc / OmniRouteKeyVerifiedUtc / OmniRouteProviderVerifiedUtc /
      ClaudePath / LastGraphifyVersion / MasterFolder / LastProject from being
      clobbered back to blank by a window that loaded its in-memory config
      before another window recorded one of these. OmniRouteDashboardPasswordEnc,
      OmniRouteDashboardLoginVerifiedUtc, and the new
      OmniRouteCompressionLastCheckedUtc were missing from that list - a second
      window saving config.json for an unrelated reason (adding a project to
      history, for instance) could silently erase a just-remembered dashboard
      password or reset the compression recheck clock back to blank. All three
      now get the same never-blank-over-a-value protection. The same race
      applied to every "already installed/configured" boolean
      (ClaudeMemInstalled, HeadroomInstalled, ClaudeCodeSetupPluginInstalled,
      TaskObserverInstalled, ClaudeMdManagementPluginInstalled,
      OmniRouteMcpRegistered, OmniRouteCompressionConfigured,
      FirstRunComplete) - a stale window's own not-yet-installed copy of one of
      these could overwrite another window's already-recorded success back to
      false, triggering a needless reinstall attempt on the next launch. These
      now follow the same "sticky true" rule already used for
      OmniRouteProviderPromptSuppressed: once any window's on-disk value is
      true, it stays true for every window from then on.
    - Invoke-GraphifyExtract always returns $true by design - a failed
      extraction warns and lets Claude Code start anyway, per its own inline
      comments - which made Invoke-ProjectMode's
      "if (-not (Invoke-GraphifyExtract)) { Stop-Script -Code 106 }" dead code
      that could never actually fire. Removed the unreachable check and the
      now-provably-unused exit code 106 from the documented exit-code list,
      rather than leave a control-flow branch that reads as load-bearing but
      isn't.
    - Start-ClaudeSession's "--continue failed, retry as a new session"
      recovery only existed on the native-binary launch path; the Node.js
      fallback path (used when the native install didn't complete) had no
      equivalent, so resuming a project with no prior conversation would just
      fail there instead of falling back to a new session the way the primary
      path does. Both paths now behave the same way.
    - Install-ClaudePluginsAndSkills cloned the Superpowers plugin via a raw
      `cmd /c git clone ... >nul 2>&1` with no timeout - the one remaining
      unbounded external call after the v4.2.0 timeout sweep, able to hang the
      launcher indefinitely on a stalled clone. Now goes through
      Invoke-ExternalCommand with a 60s timeout (and GIT_TERMINAL_PROMPT=0),
      the same pattern used for every other external call in the script.
    - Read-PathWithHistory's fast-input drain (added to keep up with a pasted
      path) appended every already-buffered keystroke's raw character
      unconditionally - if Enter/Backspace/Escape/an arrow key was already
      queued behind a paste (typing or pasting a path and immediately pressing
      Enter is the common case), its control character got typed into the
      path text instead of being handled, silently corrupting the input. The
      drain now recognizes control keys and hands them back to the main loop
      instead of appending them as literal text.

    v4.3.1 - audit follow-up: a config-destroying bug, two more unguarded
    child-window prompts, and five smaller correctness fixes:
    - Set-ClaudeAvailableModels could silently wipe the user's entire shared
      ~/.claude/settings.json: on a JSON parse failure of the existing file,
      it substituted an empty object and then wrote that (plus the new
      availableModels field) back over the real file, destroying every MCP
      server registration, permission, hook, and statusline config on the
      machine - not just this launcher's. A parse failure now aborts the
      write entirely and leaves the file untouched, and a settings.json.bak
      backup is written before every successful overwrite of a file that
      actually parsed, so a bad write is always recoverable. The same
      "returns $null on parse failure, read by the caller as genuinely no
      config yet" pattern in ConvertTo-Configuration was lower blast-radius
      but the same bug class - Save-Configuration's merge would silently
      skip merging and overwrite config.json with fresh defaults on the next
      save after any transient corruption. An existing-but-unparseable
      config.json is now backed up to config.json.corrupt-<timestamp> with a
      WARN logged, distinct from the genuinely-missing case.
    - Two blocking prompts in Invoke-ProjectMode were not guarded by
      $script:IsChild, contradicting the v4.2.0 hardening pass: Show-
      GraphResult's "Open the graph now?" and the "Press Enter to launch
      Claude, or X to exit" prompt both now skip straight to their default
      (don't open / launch immediately) in a spawned project window, the
      same pattern already used everywhere else. The final "Press Enter to
      close this window" wait was also unbounded, unlike Stop-Script's
      equivalent wait - both now share a new Wait-KeyPressBounded helper
      (extracted from Stop-Script) so neither can hang a window forever.
    - Set-OmniRouteLaunchEnvironment's fallback to the blocking secure-string
      Read-OmniRouteApiKey prompt (reached when Get-OmniRouteApiKey returns
      $null, e.g. a DPAPI decrypt failure) had no $script:IsChild check,
      unlike every other missing-key path in Initialize-OmniRoute. Now warns
      and falls back to launching Claude directly (unrouted) in a child
      window instead of blocking it.
    - Install-CompanionTooling and Invoke-UpdateCheckIfRequested both printed
      [5/6], with OmniRoute setup then printing [6/6] - two steps sharing one
      number. Invoke-UpdateCheckIfRequested is opt-in and was never supposed
      to be a numbered step (the comment already said so); it no longer
      passes -Step/-TotalSteps to Write-Section.
    - Test-OmniRouteProviderViaCli aborted its whole provider scan on one
      malformed catalog entry: under Set-StrictMode, a missing .id/.name/
      .status on any single entry threw, was caught by the function's outer
      try/catch, and returned $false immediately even if a later entry was
      the actual connected provider. Each entry now gets its own try/catch
      that skips past a bad one instead of aborting the scan.
    - Install-ViaWinget/Update-ViaWinget detected success/failure from
      English-only text matches, missing on non-English-language Windows
      installs despite the existing comment already naming the locale-
      independent numeric winget codes. Both now also check $result.ExitCode
      numerically (-1978335189 for already-installed, -2147024891 /
      0x80070005 for access-denied/needs-elevation) alongside the existing
      text matching.
    - A bad-project-folder check in project-mode setup used exit code 102,
      which .NOTES documents (and Test-RequiredDependencies actually uses)
      exclusively for a missing required dependency. It now uses 106 (freed
      by the v4.3.0 cleanup) and .NOTES documents it.
    - AutoUpdateGraphify was defined in Get-DefaultConfiguration but never
      read anywhere. Wired in: when true, Invoke-UpdateCheckIfRequested now
      runs Update-GraphifyIfNeeded even if the general interactive update
      check is declined or skipped via -SkipUpdateCheck, since it's a
      standing "auto-do this" toggle rather than a "did we already do this"
      marker like most of this config's other flags.
    v4.3.2 - live-run hotfix: the 'n' (new project folder) picker key threw
    an unhandled "The property 'Count' cannot be found on this object" and
    crashed the launcher (exit code 99) the first time a typed folder name
    produced zero or exactly one invalid-character match. New-ProjectFolder's
    validation checked `($name.ToCharArray() | Where-Object {...}).Count` -
    when that pipeline matches 0 or 1 characters, PowerShell unwraps the
    result to $null or a bare [char] rather than an array, and neither has a
    .Count property under Set-StrictMode. Wrapped in @(...) to force a real
    array, matching every other .Count check already in the file (a repo-
    wide grep for the same unwrapped-pipeline-.Count pattern found this was
    the only remaining instance).

    v4.3.3 - live-run hotfix: choosing a single project number or 'm' (open
    the master folder) in the picker silently did nothing and just redrew
    the same menu. Select-Projects returned single-path results as `return
    @($path)` - but PowerShell enumerates any array written to a function's
    output stream, so a ONE-element array collapses right back down to a
    bare string by the time the caller receives it (multi-path results with
    2+ entries were unaffected, which is why 'a' and "1,3,7" already worked).
    Invoke-LauncherMode's picker loop then saw what looked like a plain
    string, didn't match it against 'q'/'c'/'n', and fell through to
    `continue` - redrawing the menu instead of opening anything. Fixed by
    changing the three affected `return @(...)` statements (the 'm' case,
    the 'a' case, and the final numbered-selection case) to `return
    ,@(...)` - the leading comma wraps the array in one more layer so
    enumeration only ever unwraps down to the intended array, never past it,
    regardless of how many paths it holds.

    v5.0 - audit pass, quota auto-retry, and multi-session support:
    - AUDIT.md added alongside this script: verified every installed tool
      (OmniRoute, headroom, claude-mem, claude-code-setup,
      claude-md-management, task-observer) is a real, maintained project, not
      vaporware, and flagged two unresolved risks worth live-testing rather
      than assuming: OmniRoute's always-on Stacked compression may be
      forfeiting Anthropic's ~90%-off prompt-cache discount on long sessions
      in exchange for its own 20-40%-off compression discount (never
      measured together before now), and four separate systems (Graphify's
      "non-negotiable" enforcement, task-observer, claude-mem's hooks,
      headroom's statusline) all add standing overhead on every single
      session start with no combined accounting of the cost.
    - Install-ClaudePluginsAndSkills no longer fabricates empty placeholder
      SKILL.md stubs (last30days, frontend-design,
      bencium-controlled-ux-designer, graphify, impeccable) whose entire body
      was the literal string "Active and ready for tool execution." - these
      were pure token overhead in every session's skill list with zero real
      functionality (confirmed live: they render as bare `---`-description
      entries). v5.0 actively deletes any stub a prior run left behind so
      upgrading reclaims the tokens, not just stops new ones.
    - Quota-exhaustion auto-retry: Start-ClaudeSession now runs a background
      rate-limit watcher (new RateLimitWatcher .NET type, loaded via Add-Type)
      alongside the Claude process. It reads the VISIBLE console screen
      buffer (Win32 ReadConsoleOutputCharacter - no stdin/stdout redirection,
      so the interactive TTY is untouched) for Claude Code's own rate-limit
      text ("5-hour limit reached", "weekly limit", "session limit"), and on
      a match looks for Claude Code's own "Stop and wait" menu and selects it
      via a real injected console input event (Win32 WriteConsoleInput) -
      preferring Claude Code's built-in wait/resume flow over reimplementing
      one. If that menu doesn't appear, falls back to parsing a reset time
      out of the matched text and waiting it out itself, then sends
      "continue". Best-effort throughout: any failure to load the .NET type
      or read the console just disables the watcher for that session, never
      blocks the launch.
    - Multi-session-per-project: Initialize-InstanceLock's per-project mutex
      used to be held for a whole project window's lifetime, hard-blocking a
      second window on the same folder (exit code 100) even though Claude
      Code itself supports multiple concurrent sessions against one
      directory natively (each conversation is its own JSONL under
      ~/.claude/projects/<slug>/). The lock now only guards the actual
      file-write race it exists to prevent - Graphify extraction plus the
      project's CLAUDE.md/.claude/settings.json writes - and is released
      BEFORE Claude launches, not at window-close. A second window on an
      already-open folder now just skips that setup phase (trusting the
      window that holds the lock to keep it current) and launches its own
      independent session immediately, instead of refusing to start. The
      picker (Show-ProjectMenu/Select-Projects/Start-ProjectWindow) no longer
      hard-skips an "open" project - it warns setup will be skipped there and
      opens it anyway. Start-ClaudeSession's -Resume switch became
      -ResumeMode (Continue/Pick/New): a returning, non-child project window
      now gets an actual choice between continuing the most recent
      conversation, opening Claude Code's own --resume picker over every past
      session in that folder, or starting fresh - no new session-tracking
      storage needed since Claude Code already owns that state.

    v5.1 - OmniRoute window, Graphify size-gating, session-hygiene tips:
    - Start-OmniRoute now launches a real minimized PowerShell window
      (titled "OmniRoute Server") instead of cmd.exe - consistent with every
      other window this launcher opens, and still out of the way. Verified
      live: window opens with the correct title and IsIconic (minimized)
      confirmed true via a direct Win32 check, not just assumed from the
      -WindowStyle flag.
    - Graphify is now skipped ENTIRELY (not installed, not run, no CLAUDE.md
      section written about it) for any project under the existing
      $script:GRAPHIFY_STRICT_FILE_THRESHOLD (150 files) - previously
      (v5.0) Graphify was always installed and run regardless of project
      size, with only its "non-negotiable" strict-mode enforcement gated by
      size. AUDIT.md Finding 2 is now fully addressed, not just half of it:
      a small project's CLAUDE.md gets only the companion-tooling section,
      with no heading at all for a tool that isn't even present. Fixed a
      real idempotency bug caught by live testing while building this: the
      original merge-detection logic checked "does the Graphify heading
      exist yet" to decide whether to append it, which for a project that
      will NEVER get that heading (below threshold) evaluated false on
      every single launch and would have re-appended the companion-tooling
      section on top of itself forever. Fixed and verified with a real
      create-then-rerun test on both a below-threshold and an
      above-threshold project directory - both are idempotent on rerun,
      confirmed via before/after content diff, not just code review.
    - Session-hygiene guidance, requested directly: a "Session tips" block
      now prints in the console right before Claude launches (watch the
      headroom statusline bar; ~70-80% used or an unrelated topic shift ->
      /compact at a natural checkpoint, not mid-task; genuinely new work ->
      prefer a NEW session over compacting unrelated history in, using this
      launcher's v5.0 multi-session support and Claude Code's own
      --resume picker to come back to old work by name). The same guidance
      is also written into the companion-tooling section of CLAUDE.md
      (under "Session hygiene") so it's available mid-session too, not just
      at launch.
    - Investigated per user request: an OmniRoute-native "compression mode
      that's also cache-safe" does not exist per OmniRoute's own
      documentation (checked directly - no mode is documented as
      cache-aware; the closest mechanism is a per-model compression
      EXCLUSION list, which trades away compression entirely for the
      excluded model rather than combining both). Compression setup itself
      was already fully automatic before this release (Set-
      OmniRouteBestCompression runs unconditionally during OmniRoute
      onboarding); AUDIT.md Finding 3 remains open and unresolved - a
      pinned default was not changed based on this research alone, per the
      finding's own standing objection to a hypothesis-driven change
      without a live measurement.

    v5.2 - OmniRoute Claude routing disabled by default (compliance, not a
    token-savings decision):
    - Researched, per direct request, whether OmniRoute's compression is
      still worth it alongside claude-mem and headroom, or whether the
      three overlap/conflict. Finding: they don't overlap at all - neither
      claude-mem (cross-session memory) nor the ACTUAL headroom project this
      script installs (`henchmarketing-rgb/headroom`) does any compression;
      headroom is a passive statusline monitor only (confirmed by reading
      its real README directly - an earlier, wrong assumption this script's
      own comments almost repeated was that it compresses tool output the
      way a *different*, similarly-named `headroom-ai` package claims to;
      it does not). OmniRoute was and is the only actual token-compression
      mechanism in this stack.
    - That research surfaced a bigger issue than redundancy: OmniRoute's
      Claude Code integration (the `cc/` provider) authenticates using
      Claude Code's own subscription OAuth token, routed through OmniRoute's
      local gateway. Anthropic's Consumer Terms of Service, updated
      2026-02-20 and enforced since 2026-04-04, explicitly prohibit exactly
      this: "Using OAuth tokens obtained through Claude Free, Pro, or Max
      accounts in any other product, tool, or service... is not permitted."
      OmniRoute's own project still ships and actively patches this code
      path (a v3.8.0 fix for OAuth quota-error classification postdates the
      policy), so the capability exists but using it risks the underlying
      Claude subscription being restricted - a cost with no compression
      savings could offset.
    - New `$script:OMNIROUTE_ROUTE_CLAUDE` toggle (default `$false`, near
      the other OmniRoute constants). `Set-OmniRouteLaunchEnvironment`
      no-ops entirely when it's off - Claude Code launches natively, with
      none of its traffic touched by OmniRoute, and neither the 1M-context
      model pinning nor the `availableModels` restriction run (both only
      make sense when actually routed through OmniRoute). Both
      `Initialize-OmniRoute` call sites (project window, launcher window)
      now check the flag first and skip onboarding/booting OmniRoute
      entirely rather than starting a server nothing will route through.
      This is NOT a deletion of the OmniRoute integration - every function
      is still present and functional, gated behind a single constant,
      documented as safe to re-enable only after switching to a real,
      metered Anthropic Console API key (the path Anthropic's policy
      actually permits) rather than subscription OAuth, which the code has
      no way to verify was actually done.

    v5.3 - researched and added further token-saving techniques that don't
    depend on OmniRoute at all, per direct request (sourced from Anthropic's
    own Claude Code documentation, not guessed):
    - Code intelligence plugins: Anthropic's own best-practices guidance
      ("If you work with a typed language, install a code intelligence
      plugin...") comes with an exact table of plugin IDs and the language-
      server binary each one activates. New `Install-CodeIntelligencePlugin`
      detects a project's dominant language by file-extension count (same
      exclude-dir pattern as the Graphify threshold check), and - ONLY if
      the matching LSP binary is already on PATH - installs the matching
      official-marketplace plugin (`typescript-lsp`, `pyright-lsp`,
      `gopls-lsp`, `rust-analyzer-lsp`, `jdtls-lsp`, `csharp-lsp`,
      `clangd-lsp`, `kotlin-lsp`, `lua-lsp`, `php-lsp`, `swift-lsp`). This
      script never installs the language-server binaries themselves -
      installing arbitrary compiler/language tooling is out of scope, and
      the plugin is inert without one anyway. Verified live: language
      detection tested against real scratch directories for Python-
      dominant, TypeScript-dominant, and no-mapped-extension projects - all
      three resolved correctly.
    - claude-mem's context-injection defaults (`CLAUDE_MEM_CONTEXT_OBSERVATIONS`
      =50, `CLAUDE_MEM_CONTEXT_SESSION_COUNT`=10, `CLAUDE_MEM_CONTEXT_FULL_COUNT`
      =5, per docs.claude-mem.ai/configuration) are sized for large, long-
      lived codebases. Below the same Graphify-use threshold, these are now
      reduced (20/5/2) via process-scoped env vars set right before Claude
      launches - same pattern as OmniRoute's old env-var wiring, never
      touches the shared `~/.claude-mem/settings.json`, so it has zero
      effect on any other project or window.
    - CLAUDE.md bloat warning: Anthropic's own guidance states a bloated
      CLAUDE.md causes Claude to ignore half of it. New `Test-ClaudeMdBloat`
      warns (never edits) once a project's CLAUDE.md crosses 300 lines
      (`$script:CLAUDE_MD_BLOAT_LINE_THRESHOLD`). Verified live against a
      50-line file (silent), a 350-line file (warns), and a missing file
      (silent no-op).
    - Session tips (console + CLAUDE.md, kept in sync) expanded with three
      more pieces of official guidance: `/clear` to reset context between
      unrelated tasks within the same session; after two failed corrections
      on the same issue, `/clear` and rewrite the prompt rather than
      layering a third correction onto a polluted context; and a direct
      warning that switching models or reloading MCP/plugins mid-session
      invalidates Claude Code's own automatic prompt cache (confirmed via
      Anthropic's docs: system prompt, tool definitions, and CLAUDE.md are
      cached automatically, and cannot be layered on top of - some of this
      script's OWN pre-v5.2 behavior, like registering an OmniRoute MCP
      server or forcing `--model` mid-session, was exactly this kind of
      cache-invalidating action; the v5.2 OmniRoute default-off change
      incidentally also removes one such risk).

    v5.4 - broad research pass for more standalone (non-OmniRoute-shaped)
    token-saving tools, per direct request, starting from
    github.com/PrimeIntellect-ai/prime-agent:
    - prime-agent itself isn't integrable here - it's a standalone competing
      agentic CLI (its own RLM/continual-harness architecture), not a Claude
      Code companion, and requires its own separate subscription/API-key
      login. Not added; noted so the research trail is complete.
    - Widened the search to real, actively-maintained Claude Code companion
      tools and evaluated each against the same bar Finding 0 set: does it
      touch Claude Code's own Anthropic API traffic (ANTHROPIC_BASE_URL) or
      subscription OAuth token in any way?
      - **headroom-ai** (`headroomlabs-ai/headroom`, NOT the statusline
        `henchmarketing-rgb/headroom` already installed - different project,
        confusingly similar name) looked like a strong candidate for a real
        compression MCP tool. Live-tested (installed via pip, ran `headroom
        mcp --help`) before writing anything - and its own help text confirms
        the actual compression only happens by routing ALL traffic through a
        local proxy via `ANTHROPIC_BASE_URL`, explicitly "for subscription
        users who don't have API access." That is the exact same OAuth-in-
        a-third-party-tool shape Finding 0 flagged in OmniRoute. The
        `headroom_retrieve` MCP tool alone does nothing useful without that
        proxy running. Declined; test install removed.
      - **claude-rolling-context** - same shape again: "configures
        ANTHROPIC_BASE_URL to route requests through a local proxy," using
        "existing Claude Code authentication." Declined for the same reason.
      - **foldback-ai / claude-context / code-review-graph** - declined as
        redundant rather than risky: compression overlap with the (declined)
        headroom-ai research, and codebase-graph/semantic-search overlap
        with Graphify, which is already installed and already size-gated
        (v5.1). Stacking multiple overlapping tools without evidence each
        adds distinct value repeats the exact anti-pattern this audit exists
        to catch.
      - **Context7** (`@upstash/context7-mcp`) - added. Injects version-
        specific library/API docs on demand instead of Claude guessing from
        training data or spending turns grepping dependency source. Pure
        stdio MCP server - confirmed via its own `--help` output there's no
        ANTHROPIC_BASE_URL or proxy involvement at all, architecturally
        identical to any other sanctioned MCP integration (GitHub, Notion,
        etc.) Claude Code already documents as supported. Works without an
        API key (lower rate limit only). New `Register-Context7Mcp`,
        registered once at user scope alongside the other companion tooling.
        Live-tested end to end on a real machine: registered via `claude mcp
        add`, confirmed "✔ Connected" via `claude mcp list`.
      - **MCP Tool Search** (lazy-loads MCP tool definitions, up to 95% less
        context overhead per added server) is already enabled by default in
        current Claude Code versions - nothing to configure, noted so it
        isn't mistaken for a gap.
    - New `Remove-StaleOmniRouteMcpServer`: v5.2 stopped registering
      OmniRoute as an MCP server by default, but never cleaned up a
      registration from before the upgrade (or from `-ReconfigureOmniRoute`
      on an older version) - found on a real, actual machine during this
      pass: `claude mcp list` showed `omniroute: ... - Failed to connect`,
      a permanently-dead entry nothing would ever fix by itself. Now removed
      once, automatically, whenever OmniRoute routing is off. Live-tested
      on that same real registration: confirmed removed via `claude mcp
      remove`, confirmed gone from `claude mcp list` afterward.
    v5.5 - OmniRoute removed entirely; replaced with the real open-source
    tools it wrapped:
    - Every OmniRoute-specific function, config field, and CLI param
      (-CompressionMode, -ReconfigureOmniRoute) is gone: the local gateway
      server, its headless-dashboard-login/API-key machinery, its MCP
      registration, and its 1M-context model-catalog resolution/availableModels
      picker restriction. Claude Code now launches with its own native model
      defaults - there is no gateway to configure. -Model sonnet|opus still
      works (now applied directly in Start-ClaudeSession).
    - OmniRoute's Stacked pipeline (RTK -> Caveman) is replaced by the actual
      upstream projects it reimplemented, installed directly instead of
      through a third-party gateway:
        - Caveman (github.com/JuliusBrussee/caveman, MIT) - a real Claude
          Code plugin (SessionStart hook, active from message one) that
          makes the MODEL's own responses terser. See Install-CavemanPlugin.
        - RTK (github.com/rtk-ai/rtk, Apache-2.0) - a real standalone local
          binary, wired in as a Claude Code PreToolUse hook that compresses
          terminal/tool output (git, test runners, build tools, etc.) before
          it reaches the model. No winget package exists yet upstream, so
          this downloads the official Windows release .zip directly; the
          hook itself is a bash+jq script, run through Git Bash (already a
          required dependency here for headroom). See Install-RtkCli.
      Both are genuinely functional installs (verified against the real
      marketplace.json/plugin.json and release assets before wiring them
      in), not stubs, and both are fully local - no API key, no gateway
      server, no proxying of Claude's own traffic. This also resolves
      AUDIT.md Finding 0 (OmniRoute's Claude-routing violated Anthropic's
      Consumer ToS) by removing the mechanism entirely rather than just
      defaulting it off.
    v5.6 - added Context Mode, plus runtime verification that compression
    tooling is actually active (not just "installed once, trusted forever"):
    - Context Mode (mksglu/context-mode) added per a r/ClaudeCode thread
      surveying token-optimizer projects. Checked every tool listed there
      against the two bars already established (no ANTHROPIC_BASE_URL/OAuth
      proxying - Finding 0; no redundancy with what's already installed):
      RTK/Caveman/Context7 already covered; Repomix, Codebase-Memory-mcp,
      Jcodemunch-mcp, Codegraph, Sigmap, Distill, and Tokf declined as
      redundant with Graphify (code-graph tools) or RTK (CLI-output
      compression) - same anti-pattern the v5.4 audit already named; Lean CTX
      and a third, unrelated project also named "headroom" (chopratejas/
      headroom, distinct from both the statusline headroom already installed
      and the proxy-based headroom-ai declined in v5.4) both declined for the
      same ANTHROPIC_BASE_URL-proxy shape as Finding 0. Context Mode is the
      one genuinely distinct, non-proxying addition: an MCP server that
      sandboxes tool output and adds cross-session SQLite-backed memory,
      neither of which RTK or Caveman do. See Install-ContextModeMcp.
    - New Test-CompressionMethodsActive: the install-time flags
      (CavemanInstalled, RtkInstalled, Context7McpRegistered,
      ContextModeMcpRegistered) are "sticky true" by design (v4.3.0) - set
      once and never re-checked, so a plugin silently uninstalled or an MCP
      server that dropped connection would go undetected forever. This now
      runs read-only, right before Claude launches, and prints an actual
      "Compression active: caveman [OK]  rtk [OK]  ..." line (warning instead
      of silently trusting a stale flag) so what the console reports matches
      what's really live for that session, not what installed successfully
      at some point in the past.
    v5.7 - self-heal for claude-mem's tracked Windows worker bug, which
    reached this launcher for real (thedotmack/claude-mem#2926, still open
    upstream as of this writing):
    - claude-mem's background worker can die without releasing its listener
      on port 37777 (CLAUDE_MEM_WORKER_PORT); the next session's worker then
      fails to bind ("Failed to start server. Is port 37777 in use?") and
      never comes up. Because the UserPromptSubmit hook fails CLOSED instead
      of open when the worker is unreachable, every single prompt gets
      blocked and the failure counter in
      ~/.claude-mem/state/hook-failures.json climbs without bound (seen live:
      "consecutiveFailures":127) - the whole session is unusable until
      someone manually kills the orphaned process and clears that file. New
      Repair-ClaudeMemWorker runs this exact fix automatically, right before
      Claude launches, matching the two mitigations the upstream issue itself
      proposes since no real fix has shipped yet: (1) reclaim the port only
      if the process holding it is orphaned (not enumerable from this
      session - Get-Process on its PID returns nothing, the same signature
      the bug report captured via Get-NetTCPConnection), never touching a
      live, still-enumerable worker; (2) clear
      hook-failures.json/supervisor.json once the failure count is clearly
      stuck (>=10 in a row) so a fresh worker gets a clean slate instead of
      inheriting a counter that only resets on a full manual cleanup. Entirely
      best-effort - wrapped in try/catch throughout, a no-op if claude-mem
      was never installed, and never blocks or delays launch on its own
      failure. Called from Start-ClaudeSession right before
      Test-CompressionMethodsActive, same "right before Claude launches"
      placement.
.NOTES
    Version: 5.7.0
    Exit Codes:
        0   - Success
        99  - Unexpected error
        100 - Reserved (no longer used as of v5.0 - see the v5.0 changelog
              entry: the per-project lock is no longer fatal when held by
              another window)
        101 - Unsupported Windows version
        102 - Missing required dependency
        103 - Claude not found
        104 - Graphify installation failed
        106 - Project folder is not usable
#>

[CmdletBinding()]
param(
    [switch]$VerboseMode,
    [switch]$ForceUpdate,
    # Skip the "Check for updates now?" step entirely - no prompt, no check.
    # Useful for a fast/offline launch. -ForceUpdate wins if both are passed.
    [switch]$SkipUpdateCheck,
    [switch]$ResetConfig,
    # One-time launch override: forces this session onto Sonnet or Opus via
    # `claude --model <alias>`, regardless of whatever Claude Code last saved
    # as its default. Session-scoped only.
    [ValidateSet('sonnet', 'opus')]
    [string]$Model,

    # ---- v4.0 multi-window parameters -------------------------------------
    # The parent directory holding your projects. Supply it to skip the
    # master-folder prompt entirely.
    [string]$MasterFolder,
    # Child mode: run directly against this one project folder and launch
    # Claude there. This is what the launcher passes to each window it spawns;
    # you can also use it by hand to open a single project without the picker.
    [string]$ProjectPath,
    # Internal marker set on spawned windows so they skip the shared,
    # already-completed setup work (winget dependency installs, update
    # prompts) that the launcher window already did.
    [switch]$ChildWindow,
    # Give this project its own CLAUDE_CONFIG_DIR (separate settings,
    # credentials, history and cache). Off by default so windows keep sharing
    # your normal ~/.claude setup - MCP servers, custom settings and all.
    [switch]$IsolateClaudeConfig
)

# ============================================================================
# STRICT MODE AND GLOBAL STATE
# ============================================================================
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# A clean Windows 11 install's .NET networking stack sometimes still starts a
# PowerShell 5.1 host on SystemDefault / TLS 1.0-1.1 until something forces
# it up. winget itself doesn't need this, but the script's own web calls
# (task-observer/headroom downloads, any Invoke-WebRequest/Invoke-RestMethod
# use) do.
# Best-effort - never fatal.
try {
    [System.Net.ServicePointManager]::SecurityProtocol = `
        [System.Net.ServicePointManager]::SecurityProtocol -bor `
        [System.Net.SecurityProtocolType]::Tls12 -bor `
        [System.Net.SecurityProtocolType]::Tls13
} catch {
    try {
        [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
    } catch {}
}

# Application constants
$script:APP_NAME = "LLM-TokenOptimizer"
$script:APP_VERSION = "5.7.0"
$script:MAX_HISTORY = 20
$script:MAX_LOG_FILES = 10
# Upper bound on Stop-Script's "press Enter to close" wait, so a window
# nobody comes back to still exits eventually instead of hanging forever.
$script:STOP_SCRIPT_MAX_WAIT_SECONDS = 900

# Paths (computed once, never hardcoded)
$script:AppDataDir = Join-Path $env:LOCALAPPDATA $script:APP_NAME
$script:ConfigPath = Join-Path $script:AppDataDir "config.json"
$script:LogDir = Join-Path $script:AppDataDir "logs"
$script:ProfileRoot = Join-Path $script:AppDataDir "claude-profiles"
$script:GlobalGateFile = Join-Path $env:USERPROFILE ".graphify_platform_claude_done"

# Mutable global state (minimized)
$script:Config = $null
$script:InstanceMutex = $null
$script:StartTime = Get-Date
$script:DependencyCache = @{}
$script:CleanupRegistered = $false
# Session-only "-Model sonnet|opus" override; set inside Start-ClaudeSession.
$script:ForcedModelAlias = $null
# True when this process is one of the per-project windows the launcher spawned
# (or was started by hand with -ProjectPath). Child windows skip the shared
# environment bootstrap the launcher window already completed.
$script:IsChild = [bool]($ChildWindow -or $ProjectPath)
# Resolved once per process so respawning works no matter how we were started.
$script:SelfPath = if ($PSCommandPath) { $PSCommandPath } else { $MyInvocation.MyCommand.Path }
$script:ClaudeJsPath = $null   # fallback JS path when wrapper is broken

# ============================================================================
# UI TOOLKIT (ASCII only - safe in any console/encoding)
# ============================================================================

function Get-SafeConsoleWidth {
    try { $w = [Console]::WindowWidth; if ($w -gt 0) { return $w } } catch {}
    return 80
}

function Get-Rule {
    return ('-' * [Math]::Min(52, [Math]::Max(20, (Get-SafeConsoleWidth) - 4)))
}

function Write-Status {
    [CmdletBinding()]
    param(
        [string]$Tag,
        [System.ConsoleColor]$Color,
        [string]$Message,
        [System.ConsoleColor]$MessageColor = [System.ConsoleColor]::Gray
    )
    Write-Host ("  " + $Tag.PadRight(6)) -ForegroundColor $Color -NoNewline
    Write-Host $Message -ForegroundColor $MessageColor
}

function Write-Success { [CmdletBinding()] param([Parameter(Mandatory)][string]$Message) Write-Status "ok"   ([System.ConsoleColor]::Green)    $Message ([System.ConsoleColor]::Gray) }
function Write-Info    { [CmdletBinding()] param([Parameter(Mandatory)][string]$Message) Write-Status "info" ([System.ConsoleColor]::DarkCyan) $Message ([System.ConsoleColor]::Gray) }
function Write-Warning { [CmdletBinding()] param([Parameter(Mandatory)][string]$Message) Write-Status "warn" ([System.ConsoleColor]::Yellow)   $Message ([System.ConsoleColor]::Yellow) }
function Write-Fail    { [CmdletBinding()] param([Parameter(Mandatory)][string]$Message) Write-Status "fail" ([System.ConsoleColor]::Red)      $Message ([System.ConsoleColor]::Red) }
function Write-Hint    { [CmdletBinding()] param([string]$Message = "") Write-Host "  $Message" -ForegroundColor DarkGray }

function Write-ProgressBar {
    # Determinate progress bar (ASCII only). Redraws in place via `r - call
    # Clear-ProgressLine (or just Write-Host "") once the operation finishes.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$Percent,
        [string]$Label = "",
        [int]$Width = 28
    )
    $pct = [Math]::Max(0, [Math]::Min(100, $Percent))
    $filled = [Math]::Round($Width * $pct / 100)
    $bar = ('#' * $filled) + ('-' * ($Width - $filled))
    $line = "  [$bar] {0,3}%  $Label" -f $pct
    $maxWidth = [Math]::Max(20, (Get-SafeConsoleWidth) - 1)
    if ($line.Length -gt $maxWidth) { $line = $line.Substring(0, $maxWidth) }
    Write-Host ("`r" + $line.PadRight($maxWidth)) -NoNewline -ForegroundColor Cyan
}

function Clear-ProgressLine {
    $maxWidth = [Math]::Max(20, (Get-SafeConsoleWidth) - 1)
    Write-Host ("`r" + (' ' * $maxWidth) + "`r") -NoNewline
}

# ============================================================================
# RATE-LIMIT WATCHER (WIN32 CONSOLE I/O)
#   Claude Code runs synchronously, attached to this window's own console -
#   there's no pipe to redirect without breaking its interactive TTY. Instead
#   this reads the VISIBLE screen buffer (not scrollback) via
#   ReadConsoleOutputCharacter and, on a match, injects real console input
#   events via WriteConsoleInput - indistinguishable from a human keypress,
#   no stdin redirection involved. Runs as a plain background .NET Thread
#   entirely inside the C# type below; no PowerShell runspace juggling needed
#   because Win32 console handles are process-wide, not thread-affine.
#   See AUDIT.md / v5.0 changelog for the design rationale.
# ============================================================================
$script:RateLimitWatcherTypeLoaded = $false
function Install-RateLimitWatcherType {
    if ($script:RateLimitWatcherTypeLoaded) { return }
    try {
        Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Threading;

namespace LLMTokenOptimizer {
    [StructLayout(LayoutKind.Sequential)]
    public struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SMALL_RECT { public short Left; public short Top; public short Right; public short Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct CONSOLE_SCREEN_BUFFER_INFO {
        public COORD dwSize;
        public COORD dwCursorPosition;
        public ushort wAttributes;
        public SMALL_RECT srWindow;
        public COORD dwMaximumWindowSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEY_EVENT_RECORD {
        public int bKeyDown;
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public char UnicodeChar;
        public uint dwControlKeyState;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT_RECORD {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
    }

    public static class ConsoleIo {
        const int STD_INPUT_HANDLE = -10;
        const int STD_OUTPUT_HANDLE = -11;
        const ushort KEY_EVENT = 0x0001;
        const ushort VK_RETURN = 0x0D;
        const ushort VK_DOWN = 0x28;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput, out CONSOLE_SCREEN_BUFFER_INFO lpInfo);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool ReadConsoleOutputCharacter(IntPtr hConsoleOutput, [Out] StringBuilder lpCharacter, uint nLength, COORD dwReadCoord, out uint lpNumberOfCharsRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteConsoleInput(IntPtr hConsoleInput, INPUT_RECORD[] lpBuffer, uint nLength, out uint lpNumberOfEventsWritten);

        public static string ReadVisibleScreen() {
            IntPtr h = GetStdHandle(STD_OUTPUT_HANDLE);
            if (h == IntPtr.Zero) return "";
            CONSOLE_SCREEN_BUFFER_INFO info;
            if (!GetConsoleScreenBufferInfo(h, out info)) return "";
            int top = info.srWindow.Top;
            int bottom = info.srWindow.Bottom;
            int width = info.dwSize.X;
            if (width <= 0 || bottom < top) return "";
            var sb = new StringBuilder();
            for (int row = top; row <= bottom; row++) {
                var line = new StringBuilder(width);
                uint read;
                COORD coord = new COORD { X = 0, Y = (short)row };
                if (ReadConsoleOutputCharacter(h, line, (uint)width, coord, out read)) {
                    sb.AppendLine(line.ToString());
                }
            }
            return sb.ToString();
        }

        static void SendKeyEvent(ushort vk, char ch, bool down) {
            IntPtr h = GetStdHandle(STD_INPUT_HANDLE);
            if (h == IntPtr.Zero) return;
            var rec = new INPUT_RECORD();
            rec.EventType = KEY_EVENT;
            rec.KeyEvent.bKeyDown = down ? 1 : 0;
            rec.KeyEvent.wRepeatCount = 1;
            rec.KeyEvent.wVirtualKeyCode = vk;
            rec.KeyEvent.wVirtualScanCode = 0;
            rec.KeyEvent.UnicodeChar = ch;
            rec.KeyEvent.dwControlKeyState = 0;
            var buf = new INPUT_RECORD[] { rec };
            uint written;
            WriteConsoleInput(h, buf, 1, out written);
        }

        public static void SendChar(char c) {
            SendKeyEvent(0, c, true);
            SendKeyEvent(0, c, false);
        }

        public static void SendString(string s) {
            foreach (char c in s) SendChar(c);
        }

        public static void SendEnter() {
            SendKeyEvent(VK_RETURN, '\r', true);
            SendKeyEvent(VK_RETURN, '\r', false);
        }

        public static void SendDown() {
            SendKeyEvent(VK_DOWN, '\0', true);
            SendKeyEvent(VK_DOWN, '\0', false);
        }
    }

    public static class RateLimitWatcher {
        static Thread _thread;
        static volatile bool _running;
        static readonly Regex RateLimitPattern = new Regex(
            @"(5-hour limit reached|weekly limit|session limit|You've hit your (weekly|session) limit|rate limit reached)",
            RegexOptions.IgnoreCase);
        static readonly Regex ResetTimePattern = new Regex(
            @"resets?\s+(?<time>\d{1,2}(:\d{2})?\s*(am|pm)?)", RegexOptions.IgnoreCase);
        static readonly Regex StopAndWaitPattern = new Regex(@"Stop and wait", RegexOptions.IgnoreCase);
        static DateTime _cooldownUntil = DateTime.MinValue;
        static readonly object LogFileLock = new object();

        // Set the moment a rate limit is detected, read by PowerShell AFTER
        // Stop-RateLimitWatcher (same process/AppDomain, so a plain static
        // field is enough - no marshaling needed) and persisted into Config
        // as ClaudeRateLimitedUntilUtc, so the NEXT launch's pre-flight
        // availability check (Test-ClaudeAvailable) can skip straight to the
        // Antigravity/local fallback instead of launching Claude Code only
        // to immediately hit the same limit again.
        public static bool RateLimitDetected;
        public static string ResumeAtUtcIso;

        // Deliberately a plain file path, NOT a PowerShell scriptblock/delegate.
        // A PowerShell scriptblock invoked as an Action<string> from THIS class's
        // background Thread silently fails - PowerShell scriptblocks are bound to
        // the runspace of the thread that created them, and a raw .NET Thread has
        // no runspace at all, so the call throws and is swallowed by the try/catch
        // below, producing total silence with no error surfaced anywhere. Verified
        // live: the pattern-matching/console-I/O logic worked correctly in an
        // isolated console test, but zero log lines were ever written until this
        // was changed to write the file directly instead of calling back into
        // PowerShell from a foreign thread.
        public static string LogFilePath;

        static void SafeLog(string msg) {
            try {
                if (string.IsNullOrEmpty(LogFilePath)) return;
                string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "][INFO] [RateLimitWatcher] " + msg + Environment.NewLine;
                lock (LogFileLock) {
                    File.AppendAllText(LogFilePath, line);
                }
            } catch { }
        }

        public static void Start(int pollIntervalMs) {
            if (_running) return;
            _running = true;
            _thread = new Thread(() => Loop(pollIntervalMs));
            _thread.IsBackground = true;
            _thread.Start();
        }

        public static void Stop() {
            _running = false;
            try { if (_thread != null && _thread.IsAlive) _thread.Join(2000); } catch { }
        }

        static void Loop(int pollIntervalMs) {
            while (_running) {
                try {
                    if (DateTime.UtcNow >= _cooldownUntil) {
                        string tail = ConsoleIo.ReadVisibleScreen();
                        if (!string.IsNullOrEmpty(tail) && RateLimitPattern.IsMatch(tail)) {
                            SafeLog("Rate-limit text detected in console output");
                            Handle(tail);
                            _cooldownUntil = DateTime.UtcNow.AddMinutes(2);
                        }
                    }
                } catch (Exception ex) {
                    SafeLog("Watcher loop error: " + ex.Message);
                }
                Thread.Sleep(pollIntervalMs);
            }
        }

        static void Handle(string tail) {
            RateLimitDetected = true;

            // Prefer Claude Code's own built-in "Stop and wait" flow over
            // reimplementing wait/retry - give it a few seconds to render.
            for (int i = 0; i < 5; i++) {
                Thread.Sleep(1000);
                string screen = ConsoleIo.ReadVisibleScreen();
                if (StopAndWaitPattern.IsMatch(screen)) {
                    SafeLog("Found 'Stop and wait' option - selecting it");
                    // Unknown exact resume time in this path - assume the
                    // documented 5-hour window so the next launch's
                    // pre-flight check still has a conservative estimate.
                    ResumeAtUtcIso = DateTime.UtcNow.AddHours(5).ToString("o");
                    ConsoleIo.SendEnter();
                    return;
                }
            }

            // Fallback: no menu appeared. Parse a reset time out of the
            // matched text and wait it out ourselves, then send "continue".
            var m = ResetTimePattern.Match(tail);
            TimeSpan wait = TimeSpan.FromHours(5); // documented Claude Code default window
            if (m.Success) {
                DateTime parsed;
                string timeStr = m.Groups["time"].Value.Trim();
                if (DateTime.TryParse(timeStr, out parsed)) {
                    DateTime target = DateTime.Today.Add(parsed.TimeOfDay);
                    if (target <= DateTime.Now) target = target.AddDays(1);
                    TimeSpan candidate = target - DateTime.Now;
                    if (candidate > TimeSpan.Zero && candidate < TimeSpan.FromHours(24)) wait = candidate;
                }
            }
            SafeLog("No 'Stop and wait' menu found - falling back to a timed wait of " + wait.ToString());
            wait = wait.Add(TimeSpan.FromSeconds(60)); // safety margin past reset
            DateTime resumeAt = DateTime.UtcNow.Add(wait);
            ResumeAtUtcIso = resumeAt.ToString("o");
            while (_running && DateTime.UtcNow < resumeAt) {
                Thread.Sleep(Math.Min(30000, (int)Math.Max(1000, (resumeAt - DateTime.UtcNow).TotalMilliseconds)));
            }
            if (!_running) return;
            SafeLog("Reset wait elapsed - sending 'continue'");
            ConsoleIo.SendString("continue");
            ConsoleIo.SendEnter();
        }
    }
}
"@ -ErrorAction Stop
        $script:RateLimitWatcherTypeLoaded = $true
    } catch {
        Write-Log "Failed to load RateLimitWatcher .NET type - quota auto-retry disabled for this session: $_" -Level "WARN"
    }
}

function Start-RateLimitWatcher {
    # Best-effort only: any failure here must never block a Claude Code
    # launch. No-ops silently if the console APIs aren't usable (redirected
    # output, ISE, non-Windows-console host).
    [CmdletBinding()]
    param([int]$PollIntervalMs = 3000)
    Install-RateLimitWatcherType
    if (-not $script:RateLimitWatcherTypeLoaded) { return }
    try {
        # Same daily log file Write-Log itself appends to (see Write-Log,
        # ~line 665) - the watcher's background thread writes to it directly
        # via plain File.AppendAllText rather than calling back into
        # PowerShell, since a PowerShell scriptblock invoked as an
        # Action<string> from a raw, runspace-less .NET Thread silently fails
        # (confirmed by testing: the watcher's console detection worked, but
        # a PowerShell-delegate Log callback never produced a single line).
        [LLMTokenOptimizer.RateLimitWatcher]::LogFilePath = Join-Path $script:LogDir "launcher_$((Get-Date).ToString('yyyyMMdd')).log"
        [LLMTokenOptimizer.RateLimitWatcher]::Start($PollIntervalMs)
        Write-Log "Rate-limit watcher started"
    } catch {
        Write-Log "Rate-limit watcher failed to start: $_" -Level "WARN"
    }
}

function Stop-RateLimitWatcher {
    if (-not $script:RateLimitWatcherTypeLoaded) { return }
    try {
        [LLMTokenOptimizer.RateLimitWatcher]::Stop()
        Write-Log "Rate-limit watcher stopped"
    } catch {
        Write-Log "Rate-limit watcher failed to stop cleanly: $_" -Level "WARN"
    }
}

$script:SpinnerFrames = @('|', '/', '-', '\')

function Write-Spinner {
    # One animation frame of an indeterminate spinner. Caller tracks frame
    # index and elapsed time; used by Invoke-ExternalCommand's -ShowSpinner.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Label, [Parameter(Mandatory)][int]$FrameIndex, [string]$Elapsed = "")
    $frame = $script:SpinnerFrames[$FrameIndex % $script:SpinnerFrames.Length]
    $suffix = if ($Elapsed) { " ($Elapsed)" } else { "" }
    $line = "  $frame $Label$suffix"
    $maxWidth = [Math]::Max(20, (Get-SafeConsoleWidth) - 1)
    if ($line.Length -gt $maxWidth) { $line = $line.Substring(0, $maxWidth) }
    Write-Host ("`r" + $line.PadRight($maxWidth)) -NoNewline -ForegroundColor DarkCyan
}

function Write-Title {
    [CmdletBinding()]
    param([string]$Subtitle = "")
    $width = [Math]::Min(64, [Math]::Max(40, (Get-SafeConsoleWidth) - 4))
    $bar = ('=' * $width)
    Write-Host ""
    Write-Host "  $bar" -ForegroundColor DarkCyan
    Write-Host "   LLM-TokenOptimizer " -ForegroundColor Cyan -NoNewline
    Write-Host "v$($script:APP_VERSION)" -ForegroundColor DarkGray
    if ($Subtitle) {
        Write-Host "   $Subtitle" -ForegroundColor DarkGray
    } else {
        Write-Host "   Self-bootstrapping environment for Claude Code" -ForegroundColor DarkGray
    }
    Write-Host "  $bar" -ForegroundColor DarkCyan
}

function Write-Section {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [int]$Step = 0, [int]$TotalSteps = 0)
    Write-Host ""
    Write-Host "  > " -ForegroundColor DarkCyan -NoNewline
    if ($Step -gt 0 -and $TotalSteps -gt 0) {
        Write-Host "[$Step/$TotalSteps] " -ForegroundColor DarkYellow -NoNewline
    }
    Write-Host $Name -ForegroundColor Cyan
    Write-Host ("  " + (Get-Rule)) -ForegroundColor DarkGray
}

function Get-Elapsed { return ((Get-Date) - $script:StartTime).ToString('mm\:ss') }

function Read-YesNo {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Prompt, [bool]$Default = $false)
    $suffix = if ($Default) { "[Y/n]" } else { "[y/N]" }
    $ans = Read-Host "  $Prompt $suffix"
    if ([string]::IsNullOrWhiteSpace($ans)) { return $Default }
    return ($ans -match '^\s*[Yy]')
}

function Get-Truncated {
    [CmdletBinding()]
    param([string]$Text, [int]$Max = 200)
    if ([string]::IsNullOrEmpty($Text)) { return "" }
    if ($Text.Length -le $Max) { return $Text }
    return $Text.Substring(0, $Max)
}

function Set-Marker {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)
    try { "done" | Out-File -FilePath $Path -Encoding ASCII -Force -NoNewline } catch {}
}

function Get-PathSlug {
    # Stable, filesystem-safe, collision-resistant identifier for a directory.
    # Used for per-project mutex names and per-project CLAUDE_CONFIG_DIR names.
    # Case-insensitive because Windows paths are.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)
    $normalized = $Path.TrimEnd('\', '/').ToLowerInvariant()
    $leaf = (($normalized -split '[\\/]') | Where-Object { $_ } | Select-Object -Last 1)
    if (-not $leaf) { $leaf = "root" }
    $leaf = ($leaf -replace '[^a-z0-9]', '-').Trim('-')
    if (-not $leaf) { $leaf = "project" }
    $md5 = [System.Security.Cryptography.MD5]::Create()
    try {
        $bytes = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($normalized))
        $hash = ([System.BitConverter]::ToString($bytes) -replace '-', '').Substring(0, 8).ToLowerInvariant()
    } finally { $md5.Dispose() }
    if ($leaf.Length -gt 24) { $leaf = $leaf.Substring(0, 24) }
    return "$leaf-$hash"
}

function Add-PythonUserScriptsToPath {
    <#
    .SYNOPSIS
        Locates Python's user‑site Scripts directory (especially for
        Microsoft Store Python) and adds it to $env:PATH for this process.
    #>
    if (-not (Test-CommandAvailable "python" -UseCache)) { return }

    try {
        # site.USER_BASE gives the root of the user‑site packages;
        # the Scripts folder lives directly inside it.
        $userBase = Invoke-ExternalCommand -Command "python" -Arguments "-c `"import site; print(site.USER_BASE + '\\Scripts')`"" -TimeoutSeconds 5 -Silent
        if (-not $userBase.Success) { return }

        $scriptsDir = $userBase.Output.Trim()
        if ($scriptsDir -and (Test-Path $scriptsDir -PathType Container)) {
            if ($env:PATH -notlike "*$scriptsDir*") {
                $env:PATH = "$scriptsDir;$env:PATH"
                Write-Log "Added to PATH: $scriptsDir" -Level "DEBUG"
            }
        }
    } catch {
        Write-Log "Failed to add Python user scripts to PATH: $_" -Level "DEBUG"
    }
}

# ============================================================================
# LOGGING SYSTEM
# ============================================================================

function Initialize-Logging {
    try {
        if (-not (Test-Path $script:LogDir)) {
            New-Item -ItemType Directory -Path $script:LogDir -Force | Out-Null
        }
        # Only the launcher window prunes old logs. Child windows starting up
        # concurrently would otherwise race each other deleting the same files.
        if (-not $script:IsChild) {
            Get-ChildItem -Path $script:LogDir -Filter "launcher_*.log" -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending |
                Select-Object -Skip $script:MAX_LOG_FILES |
                ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
        }
    } catch {}
}

function Write-Log {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Message,
        [ValidateSet("INFO", "WARN", "ERROR", "DEBUG", "SUCCESS")]
        [string]$Level = "INFO"
    )
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    # PID is in every line now: with several windows appending to the same
    # daily log, interleaved entries are otherwise impossible to untangle.
    $logEntry = "[$timestamp][$Level][pid $PID] $Message"
    $logFile = Join-Path $script:LogDir "launcher_$((Get-Date).ToString('yyyyMMdd')).log"
    # Append can transiently fail when two windows write at the same instant;
    # a couple of quick retries makes concurrent logging effectively reliable
    # without ever being able to block the launcher.
    foreach ($attempt in 1..3) {
        try {
            $logEntry | Out-File -FilePath $logFile -Append -Encoding UTF8 -ErrorAction Stop
            break
        } catch { Start-Sleep -Milliseconds (25 * $attempt) }
    }
    if ($VerboseMode -or $Level -eq "ERROR") { Write-Verbose $logEntry }
}

# ============================================================================
# CONTROLLED EXIT
# ============================================================================

function Wait-KeyPressBounded {
    # Bounded "press any key to continue" wait, shared by Stop-Script and
    # Invoke-ProjectMode's closing prompt. A normal human presses a key
    # immediately, but a window nobody comes back to (or a non-interactive/
    # redirected host where KeyAvailable behaves oddly) still returns on its
    # own eventually instead of hanging the process forever.
    [CmdletBinding()]
    param([int]$MaxWaitSeconds = $script:STOP_SCRIPT_MAX_WAIT_SECONDS)
    try {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        while ($sw.Elapsed.TotalSeconds -lt $MaxWaitSeconds) {
            if ([Console]::KeyAvailable) { $null = [Console]::ReadKey($true); break }
            Start-Sleep -Milliseconds 100
        }
    } catch { Start-Sleep -Seconds 15 }
}

function Stop-Script {
    [CmdletBinding()]
    param([int]$Code = 0, [string]$Reason = "")
    if ($Reason) { Write-Fail $Reason }
    Write-Host ""
    Write-Hint "The launcher stopped (exit code $Code). Press Enter to close..."
    # Bounded: see Wait-KeyPressBounded.
    Wait-KeyPressBounded
    exit $Code
}

# ============================================================================
# CONFIGURATION SYSTEM
#   Shared by every window. Because several windows can now be running at
#   once, every write goes through a named cross-process mutex and re-reads
#   the file first, so one window saving its project history never discards
#   what another window saved a moment earlier.
# ============================================================================

$script:CONFIG_MUTEX_NAME = "Global\LLMTokenOptimizer_v4_Config"

function Get-DefaultConfiguration {
    return [PSCustomObject]@{
        MasterFolder = ""
        MasterFolderHistory = [array]@()
        LastProject = ""
        ProjectHistory = [array]@()
        ClaudePath = ""
        # Unlike most flags in this config (which record "have we already
        # done X"), this one is a standing "should we auto-do X" toggle: when
        # true, Update-GraphifyIfNeeded (Graphify's own pip-based update
        # check) runs from Invoke-UpdateCheckIfRequested even if the general
        # interactive "Check for updates now?" prompt is declined or skipped
        # via -SkipUpdateCheck. No prompt sets this today; it's a config.json
        # opt-in for anyone who wants Graphify kept current every launch
        # without opting into the full update check each time.
        AutoUpdateGraphify = $false
        FirstRunComplete = $false
        LastGraphifyVersion = ""
        # Companion tooling installed once at user scope so every project
        # gets it automatically - see Install-CompanionTooling.
        ClaudeMemInstalled = $false
        HeadroomInstalled = $false
        ClaudeCodeSetupPluginInstalled = $false
        TaskObserverInstalled = $false
        ClaudeMdManagementPluginInstalled = $false
        # v5.5: Caveman (JuliusBrussee/caveman, MIT) - Claude Code plugin that
        # makes the model's own responses terser (SessionStart hook, active
        # from message one). Local only, no API key. See Install-CavemanPlugin.
        CavemanInstalled = $false
        # v5.5: RTK (rtk-ai/rtk, Apache-2.0) - standalone local binary that
        # compresses terminal/tool output via a Claude Code PreToolUse hook.
        # No API key, no network service. See Install-RtkCli.
        RtkInstalled = $false
        # v5.4: Context7 (upstash/context7-mcp) - version-specific library docs
        # injected on demand, reduces tokens wasted on Claude guessing at or
        # re-deriving API usage from source. Pure stdio MCP server, no
        # ANTHROPIC_BASE_URL involvement, no Anthropic OAuth risk - see
        # Register-Context7Mcp / AUDIT.md.
        Context7McpRegistered = $false
        # v5.6: Context Mode (mksglu/context-mode) - MCP server that sandboxes
        # tool output (captures only stdout, indexes the rest for on-demand
        # BM25 search) and persists session memory in a local SQLite DB.
        # Distinct mechanism from RTK (which rewrites Bash calls to compress
        # command output at the shell level) - Context Mode operates at the
        # MCP/tool layer instead, with intent-driven filtering and cross-
        # session memory RTK doesn't do. No proxy, no ANTHROPIC_BASE_URL, no
        # OAuth involvement - see Install-ContextModeMcp / AUDIT.md.
        ContextModeMcpRegistered = $false
        # v5.7: agent-skills ecosystem - see Install-AgentSkillsEcosystem and
        # AUDIT.md. Each flag below mirrors the "have we already done X"
        # convention used throughout this file.
        LMStudioSupportInstalled = $false
        AntigravityProxyInstalled = $false
        AgentSkillsPackInstalled = $false
        AgentReachInstalled = $false
        AppleDesignSkillInstalled = $false
        HermesGraphifyInstalled = $false
        WatchVideoSkillInstalled = $false
        PromptEngineeringSkillInstalled = $false
        # v5.8: Claude -> Antigravity -> local-LM-Studio fallback chain - see
        # Resolve-SessionBackend. Written by Start-ClaudeSession (from the
        # RateLimitWatcher's static fields) and by Update-BestLocalModelFromBenchmarks.
        ClaudeRateLimitedUntilUtc = ""
        BestLocalModelId = ""
        BestLocalModelTokensPerSec = 0
        BestLocalModelUpdatedUtc = ""
    }
}

function Invoke-WithConfigLock {
    # Runs a scriptblock while holding the cross-process config mutex. Falls
    # back to running it unguarded if the mutex can't be had within the
    # timeout - a slightly racy save is strictly better than a hung launcher.
    [CmdletBinding()]
    param([Parameter(Mandatory)][scriptblock]$Body, [int]$TimeoutMs = 5000)
    $mutex = $null
    $held = $false
    try {
        $mutex = New-Object System.Threading.Mutex($false, $script:CONFIG_MUTEX_NAME)
        try { $held = $mutex.WaitOne($TimeoutMs, $false) }
        catch [System.Threading.AbandonedMutexException] {
            # Another window died holding the lock. The mutex is now ours.
            $held = $true
            Write-Log "Config mutex was abandoned by a dead process - reclaimed" -Level "DEBUG"
        }
        if (-not $held) { Write-Log "Config mutex timeout - proceeding unguarded" -Level "WARN" }
        return (& $Body)
    } catch {
        Write-Log "Config lock error: $_" -Level "WARN"
        return (& $Body)
    } finally {
        if ($mutex) {
            if ($held) { try { $mutex.ReleaseMutex() } catch {} }
            try { $mutex.Dispose() } catch {}
        }
    }
}

function ConvertTo-Configuration {
    # Reads config.json from disk and back-fills any keys added since it was
    # written, so upgrading the script never loses or misreads an old config.
    #
    # Returns $null for two different situations, and the caller (Initialize-
    # Configuration / Save-Configuration) treats both the same way - fall back
    # to fresh defaults - but they are NOT the same underlying event:
    #   1. Genuinely no config yet (missing file, or an empty file).
    #   2. A config.json that EXISTS with real content but fails to parse
    #      (truncated write, disk corruption, hand-editing gone wrong).
    # Case 2 used to return $null exactly the same as case 1, which read as
    # "no config existed yet" - so Save-Configuration's merge logic (which
    # only merges when ConvertTo-Configuration returns something) skipped
    # merging entirely and silently overwrote config.json with fresh in-
    # memory defaults on the very next save, discarding the OmniRoute API
    # key/project history with no trace. Now case 2 backs up the bad file
    # (config.json.corrupt-<timestamp>) and logs a WARN before returning
    # $null, so the loss is visible and recoverable instead of silent.
    [CmdletBinding()]
    param([string]$Path)
    if (-not (Test-Path $Path)) { return $null }
    try {
        $raw = Get-Content $Path -Raw -Encoding UTF8
        if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
        $saved = $raw | ConvertFrom-Json
        foreach ($prop in (Get-DefaultConfiguration).PSObject.Properties) {
            if (-not ($saved.PSObject.Properties.Name -contains $prop.Name)) {
                $saved | Add-Member -NotePropertyName $prop.Name -NotePropertyValue $prop.Value
            }
        }
        return $saved
    } catch {
        Write-Log "Failed to parse config: $_" -Level "WARN"
        try {
            $backupPath = "$Path.corrupt-$((Get-Date).ToString('yyyyMMdd-HHmmss'))"
            Copy-Item -Path $Path -Destination $backupPath -Force -ErrorAction Stop
            Write-Log "Config.json exists but is unparseable - backed up the bad file to $backupPath before falling back to defaults" -Level "WARN"
        } catch {
            Write-Log "Could not back up unparseable config.json: $_" -Level "WARN"
        }
        return $null
    }
}

function Initialize-Configuration {
    try {
        if (-not (Test-Path $script:AppDataDir)) {
            New-Item -ItemType Directory -Path $script:AppDataDir -Force | Out-Null
        }
    } catch {
        Write-Log "Failed to create app data directory: $_" -Level "ERROR"
    }

    # Only the launcher may reset - a spawned child doing it would wipe the
    # config out from under its siblings mid-run.
    if ($ResetConfig -and -not $script:IsChild -and (Test-Path $script:ConfigPath)) {
        Invoke-WithConfigLock { Remove-Item $script:ConfigPath -Force -ErrorAction SilentlyContinue }
        Write-Log "Configuration reset by user request"
    }

    $loaded = Invoke-WithConfigLock { ConvertTo-Configuration -Path $script:ConfigPath }
    if ($loaded) {
        $script:Config = $loaded
        Write-Log "Configuration loaded from: $($script:ConfigPath)"
    } else {
        $script:Config = Get-DefaultConfiguration
        Write-Log "No usable configuration found, using defaults"
    }

}

function Merge-ConfigurationLists {
    # Union of two ordered lists, ours first, de-duplicated case-insensitively,
    # capped at MAX_HISTORY. This is what keeps two windows from erasing each
    # other's project history.
    [CmdletBinding()]
    param([array]$Ours, [array]$Theirs)
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $merged = [System.Collections.ArrayList]::new()
    foreach ($item in (@($Ours) + @($Theirs))) {
        if (-not $item) { continue }
        if ($seen.Add([string]$item)) { $null = $merged.Add([string]$item) }
        if ($merged.Count -ge $script:MAX_HISTORY) { break }
    }
    return [array]$merged
}

function Save-Configuration {
    if (-not $script:Config) { return }
    Invoke-WithConfigLock {
        try {
            $onDisk = ConvertTo-Configuration -Path $script:ConfigPath
            $toWrite = $script:Config
            if ($onDisk) {
                # Lists merge; scalars are last-writer-wins EXCEPT that we never
                # overwrite a value another window has set with an empty one.
                $toWrite.ProjectHistory = Merge-ConfigurationLists -Ours @($script:Config.ProjectHistory) -Theirs @($onDisk.ProjectHistory)
                $toWrite.MasterFolderHistory = Merge-ConfigurationLists -Ours @($script:Config.MasterFolderHistory) -Theirs @($onDisk.MasterFolderHistory)
                foreach ($name in @(
                    'ClaudePath', 'LastGraphifyVersion', 'MasterFolder', 'LastProject')) {
                    $ours = $toWrite.$name
                    $theirs = $onDisk.$name
                    if ([string]::IsNullOrWhiteSpace([string]$ours) -and -not [string]::IsNullOrWhiteSpace([string]$theirs)) {
                        $toWrite.$name = $theirs
                    }
                }
                # A one-way "recorded" flag set by ANY window sticks for every
                # window - a window that loaded its config before another one
                # flipped one of these true must never overwrite it back to
                # false with its own stale copy on its own later save. Same
                # idea as the string fields above, just for booleans.
                foreach ($flagName in @(
                    'ClaudeMemInstalled', 'HeadroomInstalled', 'ClaudeCodeSetupPluginInstalled',
                    'TaskObserverInstalled', 'ClaudeMdManagementPluginInstalled',
                    'CavemanInstalled', 'RtkInstalled',
                    'FirstRunComplete', 'Context7McpRegistered')) {
                    if ($onDisk.$flagName) { $toWrite.$flagName = $true }
                }
            }
            # Write to a temp file and swap it in, so a window killed mid-write
            # can never leave a truncated config.json behind.
            $tmp = "$($script:ConfigPath).$PID.tmp"
            $toWrite | ConvertTo-Json -Depth 10 | Out-File -FilePath $tmp -Encoding UTF8 -Force
            Move-Item -Path $tmp -Destination $script:ConfigPath -Force
            Write-Log "Configuration saved" -Level "DEBUG"
        } catch {
            Write-Log "Failed to save configuration: $_" -Level "ERROR"
        }
    }
}

# ============================================================================
# PER-PROJECT INSTANCE LOCK
#   v3 held one global mutex, so a second window exited immediately with code
#   100. That is now scoped to the project folder instead: any number of
#   windows may run side by side as long as they are working on DIFFERENT
#   folders. Two windows on the SAME folder is still refused, because they
#   would both be writing .graphify\graph.json and the project's
#   .claude\settings.json at the same time.
# ============================================================================

function Initialize-InstanceLock {
    [CmdletBinding()]
    param([string]$ProjectDirectory)
    if (-not $ProjectDirectory) { return $true }   # launcher window takes no lock
    try {
        $slug = Get-PathSlug -Path $ProjectDirectory
        $mutexName = "Global\LLMTokenOptimizer_v4_Project_$slug"
        $script:InstanceMutex = New-Object System.Threading.Mutex($false, $mutexName)
        $acquired = $false
        try { $acquired = $script:InstanceMutex.WaitOne(0, $false) }
        catch [System.Threading.AbandonedMutexException] { $acquired = $true }
        if (-not $acquired) {
            # v5.0: no longer fatal. This mutex only ever guards the one-time
            # setup phase (Graphify extraction + CLAUDE.md/settings.json
            # writes) now, not the whole window lifetime - the caller skips
            # that phase and proceeds straight to an independent Claude Code
            # session instead of refusing to start. See Invoke-ProjectMode.
            Write-Log "Setup lock for $ProjectDirectory held by another window - will skip setup and launch independently" -Level "INFO"
            try { $script:InstanceMutex.Dispose() } catch {}
            $script:InstanceMutex = $null
            return $false
        }
        Write-Log "Project lock acquired: $mutexName"
        return $true
    } catch {
        Write-Log "Project lock creation failed (continuing): $_" -Level "WARN"
        return $true
    }
}

function Unlock-InstanceLock {
    if ($null -ne $script:InstanceMutex) {
        try {
            $script:InstanceMutex.ReleaseMutex()
            $script:InstanceMutex.Dispose()
            Write-Log "Project lock released"
        } catch {
            Write-Log "Project lock release error: $_" -Level "WARN"
        }
        $script:InstanceMutex = $null
    }
}

# ============================================================================
# CLEANUP SYSTEM
# ============================================================================

function Register-CleanupHandlers {
    if ($script:CleanupRegistered) { return }
    $script:CleanupRegistered = $true
    try {
        $null = Register-EngineEvent -SourceIdentifier PowerShell.Exiting -Action { Invoke-Cleanup } -ErrorAction SilentlyContinue
    } catch {
        Write-Log "Failed to register PowerShell.Exiting handler" -Level "WARN"
    }
}

function Invoke-Cleanup {
    Write-Log "Cleanup initiated"
    Unlock-InstanceLock
    Save-Configuration
    Write-Log "Cleanup complete"
}

# ============================================================================
# ENVIRONMENT VALIDATION
# ============================================================================

function Test-WindowsVersion {
    try {
        $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
        if (([version]$os.Version).Major -lt 10) {
            Write-Fail "Unsupported Windows version"
            Write-Hint "Detected: $($os.Caption) - requires Windows 10 or higher"
            Stop-Script -Code 101
        }
        Write-Success "Windows $($os.Version) detected"
        Write-Log "OS: $($os.Caption), Version: $($os.Version)"
    } catch {
        Write-Warning "Could not verify Windows version, continuing..."
        Write-Log "OS detection failed: $_" -Level "WARN"
    }
}

# ============================================================================
# PATH AUGMENTATION
# ============================================================================

function Add-StandardPaths {
    $patterns = @(
        "$env:LOCALAPPDATA\Programs\Python\*\Scripts",
        "$env:APPDATA\Python\*\Scripts",
        "$env:ProgramFiles\Python*\Scripts",
        "$env:USERPROFILE\.local\bin",
        "$env:ProgramFiles\Git\cmd",
        "$env:ProgramFiles\Git\bin",
        "${env:ProgramFiles(x86)}\Git\cmd",
        "$env:ProgramFiles\nodejs",
        "$env:LOCALAPPDATA\Programs\nodejs",
        "$env:USERPROFILE\scoop\shims",
        "$env:APPDATA\npm",
        "$env:ProgramData\chocolatey\bin",
        "$env:LOCALAPPDATA\Microsoft\WindowsApps"
    )
    $addedCount = 0
    foreach ($pattern in $patterns) {
        try {
            foreach ($resolvedPath in (Resolve-Path -Path $pattern -ErrorAction SilentlyContinue)) {
                $pathStr = $resolvedPath.Path
                if ($env:PATH -notlike "*$pathStr*") {
                    $env:PATH = "$env:PATH;$pathStr"
                    $addedCount++
                }
            }
        } catch {}
    }
    if ($addedCount -gt 0) { Write-Log "Added $addedCount directories to PATH" }
}

function Sync-ProcessPathFromRegistry {
    # Freshly-installed tools (via winget/npm) update the Machine/User PATH in
    # the registry, but this already-running process never re-reads it. Pull
    # both scopes and merge them into $env:PATH so new installs are usable
    # immediately, without restarting the shell.
    try {
        $machinePath = [System.Environment]::GetEnvironmentVariable("Path", "Machine")
        $userPath = [System.Environment]::GetEnvironmentVariable("Path", "User")
        $combined = @($machinePath, $userPath, $env:PATH) -join ';'
        $parts = $combined -split ';' | Where-Object { $_ -and $_.Trim() } | Select-Object -Unique
        $env:PATH = ($parts -join ';')
        Write-Log "Synced PATH from registry ($($parts.Count) entries)" -Level "DEBUG"
    } catch { Write-Log "PATH sync from registry failed: $_" -Level "DEBUG" }
    $script:DependencyCache = @{}
    Add-StandardPaths
}

# ============================================================================
# AUTO-INSTALL / AUTO-UPDATE (winget-based, best-effort on any Windows 10/11)
#   winget ships by default on Windows 11 and Windows 10 2004+ (via the App
#   Installer package). When it's missing (older Win10, winget disabled by
#   policy, etc.) we degrade gracefully to the old "tell the user where to get
#   it" behavior instead of failing.
#
#   Only the launcher window runs any of this. Several project windows racing
#   each other through winget/npm installs would be slow at best and would
#   corrupt a half-finished install at worst.
# ============================================================================

function Test-WingetAvailable {
    if ($script:DependencyCache.ContainsKey("__winget__")) { return $script:DependencyCache["__winget__"] }
    $available = [bool](Get-Command "winget" -ErrorAction SilentlyContinue)
    if ($available) {
        # winget can exist on PATH but still be a stub with no working source
        # (fresh machine, first launch). A cheap sanity call confirms it works.
        try {
            $probe = Invoke-ExternalCommand -Command "winget" -Arguments "--version" -TimeoutSeconds 10 -Silent -NoLog
            $available = $probe.Success
        } catch { $available = $false }
    }
    $script:DependencyCache["__winget__"] = $available
    return $available
}

function Install-ViaWinget {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$WingetId,
        [Parameter(Mandatory)][string]$FriendlyName,
        [int]$TimeoutSeconds = 300
    )
    if (-not (Test-WingetAvailable)) { return $false }
    Write-Info "Installing $FriendlyName via winget ($WingetId)..."
    $baseArgs = "install --id $WingetId -e --source winget --accept-package-agreements --accept-source-agreements --silent --disable-interactivity"
    $result = Invoke-ExternalCommand -Command "winget" -Arguments $baseArgs -TimeoutSeconds $TimeoutSeconds -ShowSpinner -SpinnerLabel "Installing $FriendlyName"
    # Exit code -1978335189 / 0x8A150061 = "already installed" in winget -
    # treat as success. Checked numerically FIRST because it's locale-
    # independent; the English-text match beside it is only a fallback for
    # winget builds that don't surface this exact code, and on its own it
    # would miss non-English-language Windows installs entirely, producing a
    # spurious failure warning and a needless per-user retry.
    if ($result.Success -or $result.ExitCode -eq -1978335189 -or $result.Output -match "already installed|No available upgrade") {
        Write-Success "$FriendlyName installed"
        Sync-ProcessPathFromRegistry
        return $true
    }

    # Machine-scope installs commonly fail silently (no UAC prompt possible in
    # --disable-interactivity mode) on a non-admin account, which is the
    # default on a clean Windows box. Retry per-user scope, which most
    # packages (Git, Node.js, Python) support and doesn't need elevation.
    # -2147024891 / 0x80070005 (E_ACCESSDENIED) is the locale-independent
    # signal for this case, checked numerically alongside the English-only
    # text match.
    if ($result.ExitCode -eq -2147024891 -or $result.Output -match "requires administrator|elevat|access is denied|0x80070005") {
        Write-Info "Machine-wide install needs admin - retrying as a per-user install..."
        $userArgs = "$baseArgs --scope user"
        $result = Invoke-ExternalCommand -Command "winget" -Arguments $userArgs -TimeoutSeconds $TimeoutSeconds
        if ($result.Success -or $result.ExitCode -eq -1978335189 -or $result.Output -match "already installed|No available upgrade") {
            Write-Success "$FriendlyName installed (per-user)"
            Sync-ProcessPathFromRegistry
            return $true
        }
    }

    Write-Warning "$FriendlyName installation via winget did not confirm success"
    Write-Log "winget install $WingetId output: $(Get-Truncated $result.Output 400)" -Level "WARN"
    Write-Hint "You may need to run this script as Administrator, or install $FriendlyName manually."
    return $false
}

function Update-ViaWinget {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$WingetId, [Parameter(Mandatory)][string]$FriendlyName)
    if (-not (Test-WingetAvailable)) { return }
    $wingetArgs = "upgrade --id $WingetId -e --source winget --accept-package-agreements --accept-source-agreements --silent --disable-interactivity"
    $result = Invoke-ExternalCommand -Command "winget" -Arguments $wingetArgs -TimeoutSeconds 180 -Silent
    if ($result.Success) { Write-Success "$FriendlyName up to date"; Sync-ProcessPathFromRegistry }
    # -1978335189 / 0x8A150061 checked numerically first (locale-independent),
    # same "already installed / nothing to do" signal used in Install-ViaWinget -
    # the English-only text match alone would miss this on a non-English
    # Windows install.
    elseif ($result.ExitCode -eq -1978335189 -or $result.Output -match "No applicable update|No installed package") { Write-Log "$FriendlyName already latest (winget)" -Level "DEBUG" }
    else { Write-Log "winget upgrade $WingetId output: $(Get-Truncated $result.Output 300)" -Level "DEBUG" }
}

function Install-MissingDependencies {
    [CmdletBinding()]
    param([array]$Missing)

    $installMap = [ordered]@{
        "Git"     = @{ WingetId = "Git.Git";                    FriendlyName = "Git" }
        "Node.js" = @{ WingetId = "OpenJS.NodeJS.LTS";           FriendlyName = "Node.js LTS" }
        # npm is not an independent package - it ships bundled with Node.js.
        # If npm is missing (but node itself might already be present) the
        # Node install is broken/incomplete; reinstalling Node.js repairs it.
        "npm"     = @{ WingetId = "OpenJS.NodeJS.LTS";           FriendlyName = "Node.js LTS (repairs npm)" }
        "Python"  = @{ WingetId = "Python.Python.3.12";          FriendlyName = "Python 3.12" }
    }
    $toInstall = @($Missing | Where-Object { $installMap.Contains($_.Name) })
    if ($toInstall.Count -eq 0) { return }

    if (-not (Test-WingetAvailable)) {
        Write-Warning "winget is not available on this machine - cannot auto-install missing tools"
        Write-Hint "Install winget from the Microsoft Store ('App Installer'), or install these manually:"
        foreach ($dep in $toInstall) { Write-Hint "  - $($dep.Name): $($dep.Info.Url)" }
        return
    }

    Write-Section "Auto-install"
    Write-Info "winget detected - installing missing tools automatically..."
    # Dedupe by WingetId - if both Node.js and npm are missing, that's one
    # Node.js reinstall, not two.
    $seenIds = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($dep in $toInstall) {
        $spec = $installMap[$dep.Name]
        if (-not $seenIds.Add($spec.WingetId)) { continue }
        $null = Install-ViaWinget -WingetId $spec.WingetId -FriendlyName $spec.FriendlyName
    }
    # npm/pip only exist once Node/Python are actually installed - re-detect.
    $script:DependencyCache = @{}
}

function Invoke-UpdateCheckIfRequested {
    # Only meaningful once the tools it checks actually exist, so this runs
    # after dependency detection/install and Graphify/Claude Code detection -
    # see the phase order in Invoke-LauncherMode. Deliberately NOT a numbered
    # step (no -Step/-TotalSteps) since it's opt-in - Install-CompanionTooling
    # is [5/5]; this runs right after it without taking a number of its own.
    Write-Section -Name "Update checks"
    if ($SkipUpdateCheck -and -not $ForceUpdate) {
        Write-Info "Skipping update check (-SkipUpdateCheck)"
        # AutoUpdateGraphify is a standing "keep Graphify current every
        # launch" opt-in, independent of the interactive update check above -
        # someone who's turned it on (by editing config.json; there's no
        # prompt for it) still wants Graphify's own lightweight pip-based
        # update even when skipping the general Git/Node/Python/npm/Claude
        # Code check.
        if ($script:Config.AutoUpdateGraphify) { Update-GraphifyIfNeeded }
        return
    }
    Write-Hint "Checks Git/Node/Python/npm/Graphify/Claude Code for newer versions."
    if (-not $ForceUpdate -and -not (Read-YesNo "Check for updates now?" $false)) {
        Write-Info "Skipping update check"
        if ($script:Config.AutoUpdateGraphify) { Update-GraphifyIfNeeded }
        return
    }
    Update-AllDependencies
    Update-GraphifyIfNeeded
}

function Update-AllDependencies {
    # Best-effort, short timeouts. Only runs when the user says yes to
    # Invoke-UpdateCheckIfRequested's prompt (or passes -ForceUpdate), and
    # only in the launcher window.
    Write-Section "Checking for updates"
    if (Test-WingetAvailable) {
        if (Test-CommandAvailable "git" -UseCache) { Update-ViaWinget -WingetId "Git.Git" -FriendlyName "Git" }
        if (Test-CommandAvailable "node" -UseCache) { Update-ViaWinget -WingetId "OpenJS.NodeJS.LTS" -FriendlyName "Node.js" }
        if (Test-CommandAvailable "python" -UseCache) { Update-ViaWinget -WingetId "Python.Python.3.12" -FriendlyName "Python" }
    } else {
        Write-Info "winget unavailable - skipping tool version checks"
    }
    # npm updates itself, independent of the Node.js version - a winget
    # Node.js upgrade doesn't necessarily bring npm along with it.
    if (Test-CommandAvailable "npm" -UseCache) {
        $result = Invoke-ExternalCommand -Command "npm" -Arguments "install -g npm@latest" -TimeoutSeconds 60 -ShowSpinner -SpinnerLabel "Updating npm"
        if ($result.Success) { Write-Success "npm up to date" }
    }
    if ((Test-CommandAvailable "npm" -UseCache) -and (Test-CommandAvailable "claude" -UseCache)) {
        $result = Invoke-ExternalCommand -Command "npm" -Arguments "update -g @anthropic-ai/claude-code" -TimeoutSeconds 120 -ShowSpinner -SpinnerLabel "Updating Claude Code"
        if ($result.Success) { Write-Success "Claude Code up to date" }
    }
    if (Test-CommandAvailable "autoskills" -UseCache) {
        $result = Invoke-ExternalCommand -Command "npm" -Arguments "update -g autoskills" -TimeoutSeconds 60 -ShowSpinner -SpinnerLabel "Updating autoskills"
        if ($result.Success) { Write-Success "autoskills up to date" }
    }
    Write-Success "Update check complete"
}

function Update-GraphifyIfNeeded {
    # Graphify ships via pip, outside winget's reach, so it gets its own
    # best-effort update step here rather than going through Update-ViaWinget.
    if (-not (Test-CommandAvailable "graphify" -UseCache)) { return }
    if (-not (Test-CommandAvailable "pip" -UseCache)) { return }
    $before = (Invoke-ExternalCommand -Command "graphify" -Arguments "--version" -TimeoutSeconds 10 -NoLog).Output.Trim()
    $result = Invoke-ExternalCommand -Command "pip" -Arguments "install --upgrade graphifyy" -TimeoutSeconds 120 -ShowSpinner -SpinnerLabel "Updating Graphify"
    if (-not $result.Success) {
        Write-Log "Graphify update check failed: $(Get-Truncated $result.Output 200)" -Level "DEBUG"
        return
    }
    $after = (Invoke-ExternalCommand -Command "graphify" -Arguments "--version" -TimeoutSeconds 10 -NoLog).Output.Trim()
    if ($after -and $after -ne $before) {
        Write-Success "Graphify updated: $before -> $after"
        $script:Config.LastGraphifyVersion = $after
        Save-Configuration
    } else {
        Write-Success "Graphify already up to date"
    }
}

# ============================================================================
# EXTERNAL COMMAND WRAPPER
# ============================================================================

function Invoke-ExternalCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Command,
        [string]$Arguments = "",
        [string]$WorkingDirectory = $PWD.Path,
        [int]$TimeoutSeconds = 0,
        [switch]$Silent,
        [switch]$NoLog,
        [switch]$ShowSpinner,
        [string]$SpinnerLabel = ""
    )
    $result = @{ Success = $false; Output = ""; ExitCode = -1; TimedOut = $false }
    if (-not $NoLog) { Write-Log "Exec: $Command $Arguments" -Level "DEBUG" }
    $process = $null
    try {
        # Resolve the command so we can correctly launch .cmd/.bat/.ps1 shims.
        # With UseShellExecute=$false the Windows process API cannot start a
        # batch file (npm.cmd, claude.cmd, etc.) directly - it must be run
        # through cmd.exe. Bare .exe/console commands are launched as-is.
        $fileName = $Command
        $effectiveArgs = $Arguments
        try { $resolved = Get-Command $Command -ErrorAction SilentlyContinue | Select-Object -First 1 } catch { $resolved = $null }
        if ($resolved -and $resolved.Source) {
            $src = $resolved.Source
            switch (([System.IO.Path]::GetExtension($src)).ToLowerInvariant()) {
                ".cmd" { $fileName = $env:ComSpec; $effectiveArgs = "/c `"`"$src`" $Arguments`"" }
                ".bat" { $fileName = $env:ComSpec; $effectiveArgs = "/c `"`"$src`" $Arguments`"" }
                ".ps1" { $fileName = "powershell.exe"; $effectiveArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$src`" $Arguments" }
                default { $fileName = $src }
            }
        }

        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $fileName
        $psi.Arguments = $effectiveArgs
        $psi.WorkingDirectory = $WorkingDirectory
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true
        $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
        $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
        $process = New-Object System.Diagnostics.Process
        $process.StartInfo = $psi
        if (-not $process.Start()) {
            Write-Log "Failed to start process: $Command" -Level "ERROR"
            $result.Output = "Process failed to start"
            return $result
        }
        # Capture stdout/stderr with async stream reads instead of scriptblock
        # event handlers. The add_OutputDataReceived / BeginOutputReadLine
        # pattern runs handlers on background threads and is unstable in
        # Windows PowerShell 5.1 (it can crash the whole process). Kicking off
        # both ReadToEndAsync reads BEFORE waiting drains the pipes so the child
        # never blocks on a full buffer, and avoids the classic deadlock.
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if ($ShowSpinner -and -not $Silent) {
            $label = if ($SpinnerLabel) { $SpinnerLabel } else { $Command }
            $frameIdx = 0
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $timedOut = $false
            while (-not $process.HasExited) {
                if ($TimeoutSeconds -gt 0 -and $sw.Elapsed.TotalSeconds -ge $TimeoutSeconds) { $timedOut = $true; break }
                Write-Spinner -Label $label -FrameIndex $frameIdx -Elapsed $sw.Elapsed.ToString('mm\:ss')
                Start-Sleep -Milliseconds 150
                $frameIdx++
            }
            Clear-ProgressLine
            if ($timedOut) {
                Write-Log "Process timeout: $Command (${TimeoutSeconds}s)" -Level "WARN"
                try { $process.Kill() } catch {}
                $result.TimedOut = $true
                $result.Output = "Command timed out after ${TimeoutSeconds}s"
                return $result
            }
        } elseif ($TimeoutSeconds -gt 0) {
            if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
                Write-Log "Process timeout: $Command (${TimeoutSeconds}s)" -Level "WARN"
                try { $process.Kill() } catch {}
                $result.TimedOut = $true
                $result.Output = "Command timed out after ${TimeoutSeconds}s"
                return $result
            }
        } else {
            $process.WaitForExit()
        }
        $stdout = ""; $stderr = ""
        try { $stdout = $stdoutTask.Result } catch {}
        try { $stderr = $stderrTask.Result } catch {}
        $result.ExitCode = $process.ExitCode
        $result.Success = ($process.ExitCode -eq 0)
        $result.Output = ($stdout + $stderr).Trim()
        if (-not $NoLog) { Write-Log "Exit: $($result.ExitCode) | Success: $($result.Success)" }
    } catch {
        Write-Log "Command exception ($Command): $_" -Level "ERROR"
        $result.Output = $_.Exception.Message
    } finally {
        if ($process) { try { $process.Dispose() } catch {} }
    }
    return $result
}

# ============================================================================
# DEPENDENCY DETECTION
# ============================================================================

function Test-CommandAvailable {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [switch]$UseCache)
    if ($UseCache -and $script:DependencyCache.ContainsKey($Name)) { return $script:DependencyCache[$Name] }
    $result = [bool](Get-Command $Name -ErrorAction SilentlyContinue)
    $script:DependencyCache[$Name] = $result
    return $result
}

function Test-CommandExecutes {
    # Get-Command / Test-Path only prove a name resolves to *something* on
    # PATH - on Windows that "something" is very often a Microsoft Store App
    # Execution Alias stub (python.exe, python3.exe, pip.exe under
    # WindowsApps, or a leftover Scripts\pip.exe from an unrelated broken
    # venv - both seen live on real machines) that fails with "cannot
    # execute: required file not found" the instant anything actually tries
    # to run it. Confirmed live: `Test-CommandAvailable python3` reported
    # true while `python3 --version` failed in both bash and PowerShell.
    # This actually launches the command and requires a real zero exit code.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path, [string]$Arguments = "--version")
    try {
        $result = Invoke-ExternalCommand -Command $Path -Arguments $Arguments -TimeoutSeconds 8 -Silent -NoLog
        return [bool]$result.Success
    } catch { return $false }
}

function Get-WorkingPythonExe {
    # Returns the full path to a Python interpreter that actually runs, not
    # just one that resolves on PATH (see Test-CommandExecutes). Cached for
    # the life of this process since PATH/installs don't change mid-run.
    if ($script:DependencyCache.ContainsKey("__WorkingPython")) { return $script:DependencyCache["__WorkingPython"] }
    $candidates = [System.Collections.ArrayList]::new()

    # 1. The `py` launcher, when present, is the most reliable resolver on
    #    Windows - it's registered by python.org installers specifically
    #    (not by Store aliases) and picks a real interpreter.
    $py = Get-Command "py" -ErrorAction SilentlyContinue
    if ($py) {
        try {
            $pyResult = Invoke-ExternalCommand -Command "py" -Arguments "-3 -c `"import sys; print(sys.executable)`"" -TimeoutSeconds 8 -Silent -NoLog
            if ($pyResult.Success -and $pyResult.Output.Trim()) { $null = $candidates.Add($pyResult.Output.Trim()) }
        } catch {}
    }

    # 2. Whatever "python" / "python3" resolve to on PATH.
    foreach ($name in @("python", "python3")) {
        $cmd = Get-Command $name -ErrorAction SilentlyContinue
        if ($cmd -and $cmd.Source) { $null = $candidates.Add($cmd.Source) }
    }

    # 3. Known python.org install locations, newest version first, in case
    #    none of the above are on PATH at all yet.
    $globs = @(
        "$env:LOCALAPPDATA\Programs\Python\Python3*\python.exe",
        "$env:ProgramFiles\Python3*\python.exe"
    )
    foreach ($glob in $globs) {
        Get-ChildItem $glob -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | ForEach-Object { $null = $candidates.Add($_.FullName) }
    }

    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if ((Test-Path $candidate -PathType Leaf) -and (Test-CommandExecutes -Path $candidate)) {
            $script:DependencyCache["__WorkingPython"] = $candidate
            return $candidate
        }
    }
    $script:DependencyCache["__WorkingPython"] = $null
    return $null
}

function Find-ExecutableInPaths {
    # SearchPaths, when given, is checked BEFORE the generic PATH lookup -
    # every current caller passes it to pin down one specific real install
    # (Git Bash) among several same-named executables that can legitimately
    # exist on PATH. Confirmed live: on a machine with WSL installed,
    # `Get-Command bash` resolves to C:\WINDOWS\system32\bash.exe (WSL's
    # launcher) ahead of Git's bash.exe, and WSL's bash chokes on the
    # Windows-style paths (C:/Users/...) this script hands it - every caller
    # of this function for "bash" would silently get the wrong interpreter
    # and fail with "No such file or directory" despite Git Bash being
    # correctly installed. Checking SearchPaths first makes the parameter's
    # entire reason for existing actually take effect.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [string[]]$SearchPaths)
    foreach ($basePath in $SearchPaths) {
        foreach ($candidate in @((Join-Path $basePath "$Name.exe"), (Join-Path $basePath "$Name.cmd"), (Join-Path $basePath $Name))) {
            if (Test-Path $candidate -PathType Leaf) { return $candidate }
        }
    }
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Get-DependencySummary {
    [CmdletBinding()]
    param([switch]$Quiet, [int]$Step = 0, [int]$TotalSteps = 0)
    if (-not $Quiet) { Write-Section -Name "Dependencies" -Step $Step -TotalSteps $TotalSteps }
    $dependencies = [ordered]@{
        "Git"      = @{ Command = "git";      Required = $true; Url = "https://git-scm.com/download/win"; Advice = "" }
        "Node.js"  = @{ Command = "node";     Required = $true; Url = "https://nodejs.org";               Advice = "Install the LTS version" }
        "npm"      = @{ Command = "npm";      Required = $true; Url = "https://nodejs.org";               Advice = "Included with Node.js" }
        "Python"   = @{ Command = "python";   Required = $true; Url = "https://python.org";               Advice = "Install Python 3.10+" }
        "pip"      = @{ Command = "pip";      Required = $true; Url = "https://python.org";               Advice = "Check 'Add pip' during Python install" }
        "Graphify" = @{ Command = "graphify"; Required = $true; Url = "pip install graphifyy";             Advice = "Auto-installed if missing" }
        "Claude"   = @{ Command = "claude";   Required = $true; Url = "https://claude.ai";                Advice = "Claude Code CLI" }
    }
    $missing = [System.Collections.ArrayList]::new()
    foreach ($name in $dependencies.Keys) {
        $dep = $dependencies[$name]
        if (Test-CommandAvailable -Name $dep.Command -UseCache) {
            $version = ""
            if (-not $Quiet) {
                try {
                    $verResult = Invoke-ExternalCommand -Command $dep.Command -Arguments "--version" -TimeoutSeconds 5 -Silent
                    if ($verResult.Success) { $version = ($verResult.Output.Trim() -replace "`r`n", " " -replace "`n", " ") }
                } catch {}
                Write-Success ("{0} {1}" -f $name.PadRight(9), $version)
            }
        } else {
            $null = $missing.Add(@{ Name = $name; Info = $dep })
            if (-not $Quiet) { Write-Fail ("{0} not found" -f $name.PadRight(9)) }
        }
    }
    return @{ Missing = @($missing); Dependencies = $dependencies }
}

function Test-RequiredDependencies {
    [CmdletBinding()]
    param([array]$Missing)

    # Graphify + Claude are excluded: Graphify is auto-installed via pip
    # below, and Claude has its own multi-strategy detection/install.
    $fatalMissing = @($Missing | Where-Object { $_.Info.Required -and $_.Name -notin @("Graphify", "Claude", "pip") })
    if ($fatalMissing.Count -eq 0) { return }

    # Try to auto-install whatever winget can handle (Git, Node.js, Python).
    Install-MissingDependencies -Missing $fatalMissing

    # Re-check after the install attempt.
    $depSummary = Get-DependencySummary
    $stillMissing = @($depSummary.Missing | Where-Object { $_.Info.Required -and $_.Name -notin @("Graphify", "Claude", "pip") })

    if (@($depSummary.Missing | Where-Object { $_.Name -eq "pip" }).Count -gt 0) {
        Write-Host ""
        Write-Fail "Python was found but pip is missing"
        Write-Hint "Reinstall Python with 'Add Python to PATH' and 'Install pip' checked."
        Stop-Script -Code 102
    }

    if ($stillMissing.Count -gt 0) {
        Write-Host ""
        Write-Fail "Some required dependencies could not be auto-installed:"
        foreach ($dep in $stillMissing) {
            Write-Hint "  - $($dep.Name): $($dep.Info.Url)"
            if ($dep.Info.Advice) { Write-Hint "      $($dep.Info.Advice)" }
        }
        Write-Hint "Install them manually, then run this script again."
        Stop-Script -Code 102
    }
}

# ============================================================================
# PYTHON STORE / USER SCRIPTS PATH AUTO-FIX
# ============================================================================
function Sync-PythonScriptsPath {
    # 1. Query Python directly for its user scripts directory
    if (Test-CommandAvailable "python" -UseCache) {
        try {
            $cmdResult = Invoke-ExternalCommand -Command "python" -Arguments "-c `"import site, os; print(os.path.join(site.USER_BASE, 'Scripts'))`"" -TimeoutSeconds 5 -Silent -NoLog
            if ($cmdResult.Success -and $cmdResult.Output) {
                $userScripts = $cmdResult.Output.Trim()
                if ($userScripts -and (Test-Path $userScripts) -and ($env:PATH -notlike "*$userScripts*")) {
                    $env:PATH = "$userScripts;$env:PATH"
                }
            }
        } catch {}
    }

    # 2. Fallback search for Microsoft Store Python package structures
    $storePaths = Get-ChildItem "$env:LOCALAPPDATA\Packages\PythonSoftwareFoundation.Python*\LocalCache\local-packages\Python*\Scripts" -ErrorAction SilentlyContinue
    foreach ($path in $storePaths) {
        if ($path.FullName -and (Test-Path $path.FullName) -and ($env:PATH -notlike "*$($path.FullName)*")) {
            $env:PATH = "$($path.FullName);$env:PATH"
        }
    }
}

# Run path sync immediately before dependency checking
Sync-PythonScriptsPath

# ============================================================================
# GRAPHIFY INSTALL + VERSION CHECK
#   (The rest of the Graphify management/operations functions - platform
#   registration, hook, strict mode, extract, skip-flag detection - live
#   further down under "GRAPHIFY OPERATIONS", next to Show-GraphResult and
#   Invoke-AutoSkills which they're used with. Older duplicate copies of
#   those five functions used to sit here too; PowerShell silently let the
#   later definitions win, which meant this whole block was dead code that
#   nobody editing it would ever see take effect. Removed.)
# ============================================================================

function Install-Graphify {
    # Called on any machine where `graphify` isn't already on PATH - which is
    # every clean install. Requires Python/pip (already validated as required
    # dependencies before this runs). Best-effort with a --user fallback for
    # machines where the system Python install directory isn't writable.
    if (Test-CommandAvailable "graphify" -UseCache) { return $true }

    # Never shell out to bare `pip` - on real machines it can resolve to a
    # broken Microsoft Store alias stub or a stale venv's leftover
    # Scripts\pip.exe (both seen live: `pip install ...` exited 1 with zero
    # output, no error text at all - just silently did nothing). Route
    # through a Python interpreter that's actually been proven to execute.
    $pythonExe = Get-WorkingPythonExe
    if (-not $pythonExe) {
        Write-Fail "No working Python interpreter found - cannot install Graphify"
        Write-Hint "Install Python from https://python.org with 'Add pip' checked, then run this script again."
        return $false
    }

    Write-Info "Installing Graphify (pip install graphifyy)..."
    $result = Invoke-ExternalCommand -Command $pythonExe -Arguments "-m pip install --upgrade graphifyy" -TimeoutSeconds 180 -ShowSpinner -SpinnerLabel "Installing Graphify"

    if (-not $result.Success -and ($result.Output -match "Permission denied|Access is denied|WinError 5")) {
        Write-Warning "System-wide install failed (no admin rights) - retrying with --user"
        $result = Invoke-ExternalCommand -Command $pythonExe -Arguments "-m pip install --upgrade --user graphifyy" -TimeoutSeconds 180 -ShowSpinner -SpinnerLabel "Installing Graphify (user)"
    }

    if (-not $result.Success) {
        Write-Fail "Graphify installation failed"
        foreach ($line in ($result.Output -split "`r?`n" | Select-Object -First 10)) { Write-Hint $line }
        Write-Hint "Try manually: pip install --user graphifyy"
        return $false
    }

    # A --user install lands in Python's user Scripts folder, which may not
    # be on PATH yet for this already-running process - pull it in without
    # requiring a shell restart.
    Sync-ProcessPathFromRegistry
    Add-PythonUserScriptsToPath
    $script:DependencyCache.Remove("graphify")

    if (-not (Test-CommandAvailable "graphify")) {
        Write-Fail "Graphify installed but 'graphify' is still not on PATH"
        Write-Hint "Close and reopen this window (or sign out/in) so the updated PATH takes effect, then run this script again."
        return $false
    }

    Write-Success "Graphify installed"
    return $true
}

function Test-GraphifyVersion {
    if (-not (Test-CommandAvailable "graphify" -UseCache)) { return $false }
    $result = Invoke-ExternalCommand -Command "graphify" -Arguments "--version" -TimeoutSeconds 10
    if ($result.Success) {
        $version = $result.Output.Trim() -replace "`r`n", ""
        Write-Success "$version ready"
        $script:Config.LastGraphifyVersion = $version
        Save-Configuration
        Write-Log "Graphify version: $version"
        return $true
    }
    return $false
}

# ============================================================================
# HELPER: CLAUDE USER PROMPT FALLBACK
# ============================================================================
function Request-ClaudePathFromUser {
    Write-Warning "Claude CLI path needs manual verification."
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        $dialog = New-Object System.Windows.Forms.OpenFileDialog
        $dialog.Title = "Select Claude Executable"
        $dialog.Filter = 'Executables (*.exe;*.cmd)|*.exe;*.cmd|All files (*.*)|*.*'
        if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK -and (Test-Path $dialog.FileName -PathType Leaf)) {
            $script:Config.ClaudePath = $dialog.FileName
            Save-Configuration
            Write-Success "Claude path saved: $($dialog.FileName)"
            return $dialog.FileName
        }
    } catch { Write-Log "File dialog unavailable: $_" -Level "DEBUG" }

    $manualPath = (Read-Host "  Enter full path to claude.exe").Trim().Trim('"')
    if ($manualPath -and (Test-Path $manualPath -PathType Leaf)) {
        $script:Config.ClaudePath = $manualPath
        Save-Configuration
        Write-Success "Claude path saved: $manualPath"
        return $manualPath
    }
    Write-Fail "Claude CLI path not provided."
    return $null
}

# ============================================================================
# OFFICIAL CLAUDE CODE INSTALLER & DETECTOR
# ============================================================================
function Find-ClaudeExecutable {
    [CmdletBinding()]
    param([switch]$Quiet, [int]$Step = 0, [int]$TotalSteps = 0)
    if (-not $Quiet) { Write-Section -Name "Claude Code Executable" -Step $Step -TotalSteps $TotalSteps }

    # Standard bin paths installed by `claude.exe install`
    $installerBinDirs = @(
        "$env:USERPROFILE\.local\bin",
        "$env:USERPROFILE\.claude\bin",
        "$env:LOCALAPPDATA\Programs\claude"
    )

    # Helper: Refresh session & registry PATH environment variables
    $SyncPath = {
        foreach ($binDir in $installerBinDirs) {
            if (Test-Path $binDir) {
                if ($env:PATH -notlike "*$binDir*") {
                    $env:PATH = "$binDir;$env:PATH"
                }
                $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
                if ($userPath -notlike "*$binDir*") {
                    [Environment]::SetEnvironmentVariable("Path", "$userPath;$binDir", "User")
                }
            }
        }
    }

    # 1. Sync PATH and check if already installed
    &$SyncPath
    foreach ($binDir in $installerBinDirs) {
        $exeCandidate = Join-Path $binDir "claude.exe"
        if (Test-Path $exeCandidate) {
            if (-not $Quiet) { Write-Success "Found standalone Claude binary: $exeCandidate" }
            $script:Config.ClaudePath = $exeCandidate
            Save-Configuration
            return $exeCandidate
        }
    }

    # 2. Check system PATH via Get-Command
    if (Test-CommandAvailable "claude" -UseCache) {
        $path = (Get-Command "claude" -ErrorAction Stop).Source
        # Avoid buggy global npm wrapper
        if ($path -notlike "*AppData\Roaming\npm\claude*") {
            if (-not $Quiet) { Write-Success "Found on PATH: $path" }
            $script:Config.ClaudePath = $path
            Save-Configuration
            return $path
        }
    }

    # 3. Trigger official installer: irm https://claude.ai/install.ps1 | iex
    # -TimeoutSec bounds only the download itself - a stalled/slow connection
    # used to hang the launcher here indefinitely with no way out.
    Write-Info "Executing official Claude Code installer (irm https://claude.ai/install.ps1 | iex)..."
    try {
        Invoke-RestMethod -Uri "https://claude.ai/install.ps1" -UseBasicParsing -TimeoutSec 60 | Invoke-Expression
    } catch {
        Write-Warning "Official web installer returned an error: $_"
    }

    # 4. Re-sync session PATH post-installation
    &$SyncPath

    # 5. Verify installed location
    foreach ($binDir in $installerBinDirs) {
        $exeCandidate = Join-Path $binDir "claude.exe"
        if (Test-Path $exeCandidate) {
            Write-Success "Successfully installed Claude Code: $exeCandidate"
            $script:Config.ClaudePath = $exeCandidate
            Save-Configuration
            return $exeCandidate
        }
    }

    # 6. Fallback to Node.js wrapper if native binary setup did not complete
    $claudeJs = Join-Path $env:APPDATA "npm\node_modules\@anthropic-ai\claude-code\cli.js"
    if (Test-Path $claudeJs) {
        Write-Info "Using Node runtime fallback for Claude Code"
        $script:Config.ClaudePath = "node"
        $script:ClaudeJsPath = $claudeJs
        Save-Configuration
        return "node"
    }

    # 7. Last resort is an interactive prompt (file dialog, or a typed path).
    # Never do that from a spawned/child project window - the multi-window
    # picker can open several at once and there's no guarantee anyone is
    # watching this particular one. The launcher window (always interactive)
    # is where this fallback actually gets used.
    if ($script:IsChild) {
        Write-Warning "Claude Code not found, and this is a spawned project window - not prompting"
        Write-Hint "Run the launcher window (no -ProjectPath) once to install/locate Claude Code."
        return $null
    }
    return Request-ClaudePathFromUser
}

function Test-ClaudeExecutable {
    # Actually runs `--version` and checks the result - a Test-Path/directory
    # -exists check alone can't tell a working install from a broken one
    # (wrong architecture, truncated download, permissions). Reuses
    # Invoke-ExternalCommand rather than hand-rolling a Process object here:
    # its async stdout/stderr reads plus a real WaitForExit timeout avoid a
    # hang if the child never exits, which a synchronous ReadToEnd() (the
    # previous implementation) could not.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        [string]$Path = $script:Config.ClaudePath
    )

    if (-not $Path) {
        Write-Warning "No Claude path configured to test."
        return $false
    }

    # Test Node fallback runtime
    if ($Path -eq "node" -and $script:ClaudeJsPath) {
        $result = Invoke-ExternalCommand -Command "node" -Arguments "`"$($script:ClaudeJsPath)`" --version" -TimeoutSeconds 15 -Silent
        if ($result.Success -and $result.Output -and $result.Output -notmatch "failed to run|not a valid") {
            Write-Success "Verified via Node engine ($($result.Output.Trim()))"
            return $true
        }
        Write-Warning "Node wrapper verification failed"
        Write-Log "Test-ClaudeExecutable (node) failed: exit=$($result.ExitCode) timedOut=$($result.TimedOut) output=$(Get-Truncated $result.Output 200)" -Level "WARN"
        return $false
    }

    # Test native binary executable
    if (-not (Test-Path $Path -PathType Leaf)) {
        Write-Warning "Claude path does not exist ($Path)"
        return $false
    }
    $result = Invoke-ExternalCommand -Command $Path -Arguments "--version" -TimeoutSeconds 15 -Silent
    if ($result.Success -and $result.Output -and $result.Output -notmatch "not a valid application|failed to run") {
        Write-Success "Verified Claude executable ($($result.Output.Trim()))"
        return $true
    }

    Write-Warning "Claude path could not be verified ($Path)"
    Write-Log "Test-ClaudeExecutable failed: exit=$($result.ExitCode) timedOut=$($result.TimedOut) output=$(Get-Truncated $result.Output 200)" -Level "WARN"
    return $false
}

# ============================================================================
# COMPANION TOOLING
#   Five Claude Code companions installed once at USER scope (not per
#   project), so every project window gets all five automatically with no
#   per-project install step and no per-tool on/off switch:
#     - claude-mem          persistent cross-session memory (plugin)
#     - headroom            context-window usage bar in the statusline
#     - claude-code-setup   official Anthropic plugin that scans a project
#                           and recommends tailored MCP servers/skills/hooks
#     - task-observer       skill that logs workflow friction for later review
#     - claude-md-management official Anthropic plugin that audits and
#                           maintains CLAUDE.md itself (quality checks,
#                           /revise-claude-md to capture session learnings)
#   Each is independently best-effort: a failed install warns and moves on,
#   the same way a failed Graphify hook install does elsewhere in this
#   script, rather than stopping the whole launch over an add-on. Wired in
#   via Install-CompanionTooling below, called from both Invoke-LauncherMode
#   and (as a fallback) Invoke-ProjectMode.
# ============================================================================

function Install-ClaudeMem {
    if ($script:Config.ClaudeMemInstalled) { Write-Success "claude-mem already installed"; return $true }
    if (-not (Test-CommandAvailable "npm" -UseCache)) { Write-Warning "npm not found - skipping claude-mem"; return $false }

    Write-Info "Installing claude-mem (persistent Claude Code memory)..."

    # 1. Pre-seed ~/.claude-mem/settings.json to satisfy the wizard's configuration step
    $cmemDir = Join-Path $env:USERPROFILE ".claude-mem"
    $cmemSettings = Join-Path $cmemDir "settings.json"
    if (-not (Test-Path $cmemSettings)) {
        try {
            New-Item -ItemType Directory -Path $cmemDir -Force | Out-Null
            $defaultConfig = [ordered]@{
                runtime = "worker"
                provider = "claude-agent-sdk"
                authMethod = "subscription"
                model = "claude-haiku-4-5-20251001"
                onboardingComplete = $true
                skipEmail = $true
            }
            $defaultConfig | ConvertTo-Json | Out-File -FilePath $cmemSettings -Encoding UTF8 -Force
            Write-Log "Pre-seeded default settings at $cmemSettings" -Level "DEBUG"
        } catch {
            Write-Log "Could not pre-seed claude-mem settings: $_" -Level "WARN"
        }
    }

    # 2. Run with CI=true and NON_INTERACTIVE=1 to force prompt libraries to skip TTY prompts
    $oldCi = $env:CI
    $oldNonInteractive = $env:NON_INTERACTIVE
    try {
        $env:CI = "true"
        $env:NON_INTERACTIVE = "1"

        # Pipe echo. as a secondary fallback for standard readline prompts
        $cmdArgs = '/c "echo. | npx -y claude-mem@latest install --ide claude-code"'
        $result = Invoke-ExternalCommand -Command "cmd.exe" -Arguments $cmdArgs -TimeoutSeconds 45 -ShowSpinner -SpinnerLabel "Installing claude-mem"
    } finally {
        $env:CI = $oldCi
        $env:NON_INTERACTIVE = $oldNonInteractive
    }

    # 3. Verify installation state by checking plugin registry and marketplace directories.
    # A bare Test-Path would pass on an empty/partially-cloned directory left
    # behind by a failed install - require it to actually contain files.
    $pluginPath = Join-Path $env:USERPROFILE ".claude\plugins\marketplaces\thedotmack\claude-mem"
    $pluginHasContent = (Test-Path $pluginPath) -and
        ([bool](Get-ChildItem -Path $pluginPath -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1))

    if ($result.Success -or $pluginHasContent) {
        Write-Success "claude-mem installed"
        $script:Config.ClaudeMemInstalled = $true
        Save-Configuration
        return $true
    }

    # 4. Fallback: If you've already completed the manual run on this machine, mark it installed!
    if (Test-Path $cmemSettings) {
        Write-Success "claude-mem config detected from previous run"
        $script:Config.ClaudeMemInstalled = $true
        Save-Configuration
        return $true
    }

    Write-Warning "claude-mem install did not confirm success - continuing without it"
    Write-Log "claude-mem install output: $(Get-Truncated $result.Output 300)" -Level "WARN"
    return $false
}

function Repair-HeadroomPython3Refs {
    # headroom's upstream installer hardcodes bare `python3` in both the
    # generated PostToolUse hook command (settings.json) and
    # ~/.claude/statusline.sh. On Windows that name routinely resolves to a
    # Microsoft Store App Execution Alias stub that fails with "cannot
    # execute: required file not found" the moment it's actually invoked -
    # confirmed live on this machine in both a spawned bash and PowerShell,
    # even though `command -v python3` / Get-Command both report it as
    # present. Rewrites both files to call an absolute, execution-verified
    # interpreter instead, so the statusline and hook actually work rather
    # than silently failing (fast enough - 3s hook timeout - that it's easy
    # to never notice).
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$PythonExe)
    $configDir = Get-ClaudeConfigDir
    $forwardSlashPython = $PythonExe -replace '\\', '/'
    $changed = $false
    # `Set-Content -Encoding UTF8` writes a UTF-8 BOM, which breaks bash's
    # shebang/comment parsing on statusline.sh - confirmed live: bash tried
    # to literally execute the BOM-glued "#!/bin/bash" line as a command
    # ("No such file or directory") because the invisible BOM bytes stop '#'
    # from being word-initial, so bash no longer treats it as a comment.
    # Write with a plain (no-BOM) UTF8Encoding instead.
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)

    $statuslinePath = Join-Path $configDir "statusline.sh"
    if (Test-Path $statuslinePath) {
        try {
            $content = Get-Content $statuslinePath -Raw -Encoding UTF8
            if ($content -notmatch 'PYTHON3=') {
                # Not patched yet: swap every bare `python3` call for
                # `"$PYTHON3"` FIRST, then inject the PYTHON3="..." var line
                # last. Order matters - PowerShell's -replace is
                # case-INSENSITIVE by default, so doing this the other way
                # round makes the `\bpython3\b` pass match the just-inserted
                # `PYTHON3=` line too (confirmed live: produced the broken
                # `"$PYTHON3"="C:/...python.exe"`). Idempotent: no-op once
                # PYTHON3= is already present.
                $newContent = $content -replace '\bpython3\b', '"$PYTHON3"'
                $newContent = $newContent -replace '(?m)^#!/bin/bash', "#!/bin/bash`nPYTHON3=`"$forwardSlashPython`""
                [System.IO.File]::WriteAllText($statuslinePath, $newContent, $utf8NoBom)
                $changed = $true
            }
        } catch { Write-Log "Failed to repair statusline.sh python3 refs: $_" -Level "WARN" }
    }

    $settingsPath = Join-Path $configDir "settings.json"
    if (Test-Path $settingsPath) {
        try {
            $raw = Get-Content $settingsPath -Raw -Encoding UTF8
            if ($raw -match '"command":\s*"python3 ') {
                # Use forward slashes (valid on Windows, no JSON backslash-
                # escaping to get wrong) so the interpreter path drops
                # straight into the JSON string literal.
                $newRaw = $raw -replace '"command":\s*"python3 ', "`"command`": `"\`"$forwardSlashPython\`" "
                [System.IO.File]::WriteAllText($settingsPath, $newRaw, $utf8NoBom)
                $changed = $true
            }
        } catch { Write-Log "Failed to repair settings.json python3 hook ref: $_" -Level "WARN" }
    }
    return $changed
}

function Test-HeadroomWorking {
    # Functional check, not a string grep: actually runs the installed
    # statusline script and confirms it exits cleanly. A prior version of
    # this installer only checked for the literal text "headroom" inside
    # settings.json, which is true even when the statusline silently fails
    # every single invocation (the exact bug this function now catches).
    $configDir = Get-ClaudeConfigDir
    $statuslinePath = Join-Path $configDir "statusline.sh"
    if (-not (Test-Path $statuslinePath)) { return $false }
    $bash = Find-ExecutableInPaths -Name "bash" -SearchPaths @(
        "$env:ProgramFiles\Git\bin", "${env:ProgramFiles(x86)}\Git\bin", "$env:ProgramFiles\Git\usr\bin"
    )
    if (-not $bash) { return $false }
    $result = Invoke-ExternalCommand -Command $bash -Arguments "-lc `"bash '$($statuslinePath -replace '\\','/')'`"" -TimeoutSeconds 10 -Silent -NoLog
    return $result.Success
}

function Install-HeadroomStatusline {
    # Context-window usage bar for Claude Code's statusline. Ships as a bash
    # installer with no native Windows path; Git for Windows - already a
    # required dependency of this script - provides the bash.exe needed to
    # run it, and Claude Code's own statusline command runs on the same
    # bash.exe afterward, so this doesn't add a new runtime requirement.
    #
    # Deliberately does NOT trust $script:Config.HeadroomInstalled as a
    # short-circuit on its own: that flag can be true from a prior run of
    # this exact installer that "succeeded" (its own exit code was 0) while
    # the statusline it wired up is actually broken (see Test-HeadroomWorking
    # doc comment) - confirmed live on this machine. Always re-verify.
    if ($script:Config.HeadroomInstalled -and (Test-HeadroomWorking)) {
        Write-Success "headroom statusline already installed and verified working"
        return $true
    }

    $pythonExe = Get-WorkingPythonExe
    if (-not $pythonExe) {
        Write-Warning "No working Python interpreter found - skipping the headroom statusline"
        return $false
    }

    $bash = Find-ExecutableInPaths -Name "bash" -SearchPaths @(
        "$env:ProgramFiles\Git\bin", "${env:ProgramFiles(x86)}\Git\bin", "$env:ProgramFiles\Git\usr\bin"
    )
    if (-not $bash) { Write-Warning "Git Bash not found - skipping the headroom statusline"; return $false }

    if ($script:Config.HeadroomInstalled) {
        Write-Info "headroom was marked installed but isn't actually working - repairing..."
    } else {
        Write-Info "Installing headroom (Claude Code context-usage statusline)..."
    }

    # Give the installer's internal `command -v python3` (and its generated
    # scripts, until Repair-HeadroomPython3Refs rewrites them below) a
    # python3 that actually runs, via a throwaway shim dir prepended to PATH
    # for just this one bash invocation - never touches the user's real PATH.
    $shimDir = Join-Path $env:TEMP "llm-token-optimizer-python3-shim"
    New-Item -ItemType Directory -Path $shimDir -Force | Out-Null
    $shimPath = Join-Path $shimDir "python3"
    $forwardSlashPython = $pythonExe -replace '\\', '/'
    Set-Content -Path $shimPath -Value "#!/bin/sh`nexec `"$forwardSlashPython`" `"`$@`"`n" -Encoding ASCII -NoNewline
    $shimPathPosix = ($shimDir -replace '\\', '/') -replace '^([A-Za-z]):', '/$1'
    $installerUrl = "https://raw.githubusercontent.com/henchmarketing-rgb/headroom/main/install.sh"
    $bashCmd = "chmod +x '${shimPathPosix}/python3' 2>/dev/null; PATH=`"${shimPathPosix}:`$PATH`" bash -c `"curl -fsSL $installerUrl | bash`""
    $result = Invoke-ExternalCommand -Command $bash -Arguments "-lc `"$bashCmd`"" -TimeoutSeconds 60 -ShowSpinner -SpinnerLabel "Installing headroom"
    try { Remove-Item -Path $shimDir -Recurse -Force -ErrorAction SilentlyContinue } catch {}

    if (-not $result.Success) {
        Write-Warning "headroom install did not confirm success - continuing without it"
        Write-Log "headroom install output: $(Get-Truncated $result.Output 300)" -Level "WARN"
        return $false
    }

    # The installer's own generated files still hardcode bare `python3` -
    # fix that regardless of whether it came from the shim path above so it
    # keeps working in every later Claude Code session and shell, not just
    # this one install run.
    Repair-HeadroomPython3Refs -PythonExe $pythonExe | Out-Null

    if (Test-HeadroomWorking) {
        Write-Success "headroom statusline installed and verified working"
        $script:Config.HeadroomInstalled = $true
        Save-Configuration
        return $true
    }

    Write-Warning "headroom installed but the statusline still doesn't run cleanly - leaving it in place, but check ~/.claude/statusline.sh manually"
    $script:Config.HeadroomInstalled = $false
    Save-Configuration
    return $false
}

function Test-ClaudePluginInstalled {
    # Confirms a plugin is actually registered with Claude Code rather than
    # trusting the install command's own exit code / "already installed"
    # text match alone - `claude plugin install` can report success even
    # when the marketplace add silently no-oped or the plugin failed to
    # activate. Best-effort: if `claude plugin list` itself isn't available
    # on this Claude Code version (unrecognized subcommand, non-zero exit),
    # falls back to trusting what the install command reported, so an older
    # CLI doesn't turn a real success into a false failure.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$PluginId, [Parameter(Mandatory)][bool]$InstallReportedSuccess)
    # `claude plugin list` does NOT accept a --scope flag (confirmed live on
    # Claude Code 2.1.224 - passing one errors "unknown option '--scope'" and
    # this function's own fallback below silently masked that on every prior
    # call). Scope is already shown per-plugin in the plain listing.
    $result = Invoke-ExternalCommand -Command "claude" -Arguments "plugin list" -TimeoutSeconds 15 -Silent -NoLog
    if (-not $result.Success) { return $InstallReportedSuccess }
    return [bool]($result.Output -match [regex]::Escape($PluginId))
}

function Install-ClaudeCodeSetupPlugin {
    # Official Anthropic plugin (claude-plugins-official marketplace) that
    # scans a project and recommends tailored MCP servers, skills, hooks,
    # and subagents. Read-only - it doesn't modify files itself.
    if ($script:Config.ClaudeCodeSetupPluginInstalled) { Write-Success "claude-code-setup plugin already installed"; return $true }
    if (-not (Test-CommandAvailable "claude" -UseCache)) { return $false }

    Write-Info "Installing the claude-code-setup plugin (official marketplace)..."
    # Defensive: Claude Code normally auto-registers the official marketplace
    # on first INTERACTIVE launch, but that registration is known to be
    # missed in non-interactive contexts like this one - so add it explicitly
    # rather than assuming it's already there.
    $null = Invoke-ExternalCommand -Command "claude" -Arguments "plugin marketplace add anthropics/claude-plugins-official" -TimeoutSeconds 30 -Silent
    $result = Invoke-ExternalCommand -Command "claude" -Arguments "plugin install claude-code-setup@claude-plugins-official --scope user" -TimeoutSeconds 60
    $reportedSuccess = [bool]($result.Success -or $result.Output -match "already installed")
    if (Test-ClaudePluginInstalled -PluginId "claude-code-setup" -InstallReportedSuccess $reportedSuccess) {
        Write-Success "claude-code-setup plugin installed"
        $script:Config.ClaudeCodeSetupPluginInstalled = $true
        Save-Configuration
        return $true
    }
    Write-Warning "claude-code-setup plugin install did not confirm success"
    Write-Log "claude-code-setup install output: $(Get-Truncated $result.Output 300)" -Level "WARN"
    return $false
}

# ============================================================================
# CODE INTELLIGENCE PLUGIN (v5.3, per code.claude.com/docs/en/best-practices)
#   Anthropic's own guidance: "If you work with a typed language, install a
#   code intelligence plugin to give Claude precise symbol navigation and
#   automatic error detection after edits" - exact language, and exact table
#   of plugin IDs / required binaries, taken directly from that page (not
#   guessed): a wrong plugin ID would just fail to install, but an accurate
#   one is what makes this worth doing at all.
# ============================================================================
$script:CodeIntelligencePluginMap = @{
    # Extension -> official-marketplace plugin id + the LSP binary that plugin
    # activates (the plugin does NOT install the binary - see AUDIT.md).
    '.ts'    = @{ Plugin = 'typescript-lsp';    Binary = 'typescript-language-server' }
    '.tsx'   = @{ Plugin = 'typescript-lsp';    Binary = 'typescript-language-server' }
    # .js/.jsx aren't a separate row in Anthropic's table (only "TypeScript"
    # is listed), but typescript-language-server is the de facto LSP for
    # plain JS too - this is a well-established fact about that binary, not
    # a guess about Claude Code's plugin catalog.
    '.js'    = @{ Plugin = 'typescript-lsp';    Binary = 'typescript-language-server' }
    '.jsx'   = @{ Plugin = 'typescript-lsp';    Binary = 'typescript-language-server' }
    '.py'    = @{ Plugin = 'pyright-lsp';       Binary = 'pyright-langserver' }
    '.go'    = @{ Plugin = 'gopls-lsp';         Binary = 'gopls' }
    '.rs'    = @{ Plugin = 'rust-analyzer-lsp'; Binary = 'rust-analyzer' }
    '.java'  = @{ Plugin = 'jdtls-lsp';         Binary = 'jdtls' }
    '.cs'    = @{ Plugin = 'csharp-lsp';        Binary = 'csharp-ls' }
    '.cpp'   = @{ Plugin = 'clangd-lsp';        Binary = 'clangd' }
    '.cc'    = @{ Plugin = 'clangd-lsp';        Binary = 'clangd' }
    '.c'     = @{ Plugin = 'clangd-lsp';        Binary = 'clangd' }
    '.h'     = @{ Plugin = 'clangd-lsp';        Binary = 'clangd' }
    '.hpp'   = @{ Plugin = 'clangd-lsp';        Binary = 'clangd' }
    '.kt'    = @{ Plugin = 'kotlin-lsp';        Binary = 'kotlin-language-server' }
    '.lua'   = @{ Plugin = 'lua-lsp';           Binary = 'lua-language-server' }
    '.php'   = @{ Plugin = 'php-lsp';           Binary = 'intelephense' }
    '.swift' = @{ Plugin = 'swift-lsp';         Binary = 'sourcekit-lsp' }
}

function Get-ProjectDominantLanguage {
    # Same exclude-dir pattern as Test-ProjectExceedsGraphifyThreshold, scoped
    # to extensions this script actually has a plugin mapping for.
    $excludeDirs = @('node_modules', '.git', '.graphify', 'graphify-out', 'dist',
        'build', 'out', 'bin', 'obj', '__pycache__', '.venv', 'venv', '.next', 'target')
    $pattern = '[\\/](' + ($excludeDirs -join '|') + ')[\\/]'
    try {
        $counts = @{}
        Get-ChildItem -Path $PWD -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch $pattern -and $script:CodeIntelligencePluginMap.ContainsKey($_.Extension.ToLowerInvariant()) } |
            ForEach-Object {
                $ext = $_.Extension.ToLowerInvariant()
                if (-not $counts.ContainsKey($ext)) { $counts[$ext] = 0 }
                $counts[$ext]++
            }
        if ($counts.Count -eq 0) { return $null }
        return ($counts.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1).Key
    } catch {
        Write-Log "Dominant-language detection failed: $_" -Level "DEBUG"
        return $null
    }
}

function Install-CodeIntelligencePlugin {
    # Only installs when the required language-server BINARY is already on
    # PATH - the plugin activates Claude's built-in LSP tool for an existing
    # language server, it doesn't install one, and this script isn't going to
    # install arbitrary compiler/language tooling on your behalf. Best-effort,
    # like every other companion-tooling installer here: never blocks launch.
    $ext = Get-ProjectDominantLanguage
    if (-not $ext) { return }
    $info = $script:CodeIntelligencePluginMap[$ext]
    if (-not (Test-CommandAvailable $info.Binary -UseCache)) {
        Write-Log "Code intelligence: $($info.Plugin) skipped - $($info.Binary) not found on PATH" -Level "DEBUG"
        return
    }
    if (Test-ClaudePluginInstalled -PluginId $info.Plugin -InstallReportedSuccess $false) {
        Write-Log "Code intelligence plugin $($info.Plugin) already installed" -Level "DEBUG"
        return
    }
    Write-Info "Installing code intelligence plugin for this project: $($info.Plugin) (found $($info.Binary) on PATH)"
    $null = Invoke-ExternalCommand -Command "claude" -Arguments "plugin marketplace add anthropics/claude-plugins-official" -TimeoutSeconds 30 -Silent
    $result = Invoke-ExternalCommand -Command "claude" -Arguments "plugin install $($info.Plugin)@claude-plugins-official --scope user" -TimeoutSeconds 60
    $reportedSuccess = [bool]($result.Success -or $result.Output -match "already installed")
    if (Test-ClaudePluginInstalled -PluginId $info.Plugin -InstallReportedSuccess $reportedSuccess) {
        Write-Success "Installed $($info.Plugin) - precise symbol navigation + auto diagnostics for this project"
    } else {
        Write-Warning "$($info.Plugin) install did not confirm success"
        Write-Log "Code intelligence plugin install output: $(Get-Truncated $result.Output 300)" -Level "WARN"
    }
}

# ============================================================================
# DEDICATED CLAUDE PLUGINS & SKILLS INSTALLER
# ============================================================================
function Install-ClaudePluginsAndSkills {
    [CmdletBinding()]
    param([switch]$Quiet, [int]$Step = 0, [int]$TotalSteps = 0)
    if (-not $Quiet) { Write-Section -Name "Installing Plugins & Prompt Skills" -Step $Step -TotalSteps $TotalSteps }

    $claudeBase  = Join-Path $env:USERPROFILE ".claude"
    $pluginsDir  = Join-Path $claudeBase "plugins"
    $skillsDir   = Join-Path $claudeBase "skills"

    # ------------------------------------------------------------------------
    # 1. SETUP .claude\plugins ARCHITECTURE
    # ------------------------------------------------------------------------
    $pluginSubfolders = @("cache", "data", "marketplaces")
    foreach ($folder in $pluginSubfolders) {
        $path = Join-Path $pluginsDir $folder
        if (-not (Test-Path $path)) {
            New-Item -ItemType Directory -Path $path -Force -ErrorAction Stop | Out-Null
        }
    }

    # Register installed plugins in installed_plugins.json
    $installedJsonPath = Join-Path $pluginsDir "installed_plugins.json"
    $pluginsRegistry = @{
        version = 1
        plugins = @{
            "superpowers"     = @{ scope = "user"; enabled = $true; source = "https://github.com/obra/superpowers.git" }
            "last30days"      = @{ scope = "user"; enabled = $true; source = "local" }
            "frontend-design" = @{ scope = "user"; enabled = $true; source = "local" }
        }
    }
    $pluginsRegistry | ConvertTo-Json -Depth 4 | Set-Content -Path $installedJsonPath -Encoding UTF8 -ErrorAction Stop
    if (-not $Quiet) { Write-Success "Updated plugin registry ($installedJsonPath)" }

    # Clone Superpowers into .claude\plugins\cache\superpowers
    $superpowersPluginPath = Join-Path $pluginsDir "cache\superpowers"
    if (-not (Test-Path $superpowersPluginPath)) {
        Write-Info "Cloning Superpowers framework into plugins cache..."
        # Was a raw `cmd /c git clone` with no timeout - the one unbounded
        # external call left after the v4.2.0 timeout sweep, able to hang the
        # launcher indefinitely on a stalled clone. GIT_TERMINAL_PROMPT=0 also
        # stops git's credential helper from popping up a blocking prompt.
        $oldGitPrompt = $env:GIT_TERMINAL_PROMPT
        try {
            $env:GIT_TERMINAL_PROMPT = "0"
            $cloneArgs = "clone --quiet ""https://github.com/obra/superpowers.git"" ""$superpowersPluginPath"""
            $null = Invoke-ExternalCommand -Command "git" -Arguments $cloneArgs -TimeoutSeconds 60 -ShowSpinner -SpinnerLabel "Cloning Superpowers"
        } finally {
            $env:GIT_TERMINAL_PROMPT = $oldGitPrompt
        }
        if (Test-Path $superpowersPluginPath) {
            Write-Success "Installed Superpowers plugin"
        } else {
            Write-Warning "Failed to clone Superpowers repository"
        }
    } else {
        Write-Info "Verified Superpowers plugin"
    }

    # ------------------------------------------------------------------------
    # 2. CLEAN UP LEGACY PLACEHOLDER SKILLS
    # ------------------------------------------------------------------------
    # v4.x used to fabricate empty placeholder SKILL.md stubs here (last30days,
    # frontend-design, bencium-controlled-ux-designer, graphify, impeccable) -
    # each file's entire body was the literal string "Active and ready for
    # tool execution.", no real instructions. They still showed up in every
    # session's skill list (consuming system-prompt tokens) for zero
    # functional benefit - see AUDIT.md. Real graphify skill wiring is done
    # separately by Install-GraphifyPlatform (`graphify install --platform
    # claude`), which is unaffected by this cleanup. v5.0 removes the
    # fabrication and deletes any stub left behind by a prior run so upgrading
    # actually reclaims the tokens instead of just stopping new ones.
    if (Test-Path $skillsDir) {
        foreach ($legacyStub in @("last30days", "frontend-design", "bencium-controlled-ux-designer", "impeccable")) {
            $stubDir = Join-Path $skillsDir $legacyStub
            $stubFile = Join-Path $stubDir "SKILL.md"
            if (Test-Path $stubFile) {
                $stubContent = Get-Content -Path $stubFile -Raw -ErrorAction SilentlyContinue
                if ($stubContent -and $stubContent -match 'Active and ready for tool execution\.') {
                    Remove-Item -Path $stubDir -Recurse -Force -ErrorAction SilentlyContinue
                    Write-Log "Removed legacy placeholder skill stub: $legacyStub" -Level "INFO"
                    if (-not $Quiet) { Write-Info "Removed empty placeholder skill: $legacyStub" }
                }
            }
        }
    }

    # ------------------------------------------------------------------------
    # 3. CLEAN UP DUPLICATES & WRAPPERS
    # ------------------------------------------------------------------------
    $legacyFolder = Join-Path $skillsDir "claude-skills-final"
    if (Test-Path $legacyFolder) {
        Remove-Item -Path $legacyFolder -Recurse -Force -ErrorAction SilentlyContinue
    }

    if (-not $Quiet) { Write-Success "Plugins and prompt skills installation complete!" }
}

function Install-ClaudeMdManagementPlugin {
    # Official Anthropic plugin, same anthropics/claude-plugins-official
    # marketplace as claude-code-setup above (so the marketplace add below
    # is a harmless no-op repeat if it's already registered). Audits
    # CLAUDE.md quality and captures session learnings via /revise-claude-md
    # and the in-session '#' shortcut - directly relevant here since this
    # script itself writes/merges CLAUDE.md (see Set-ProjectClaudeMdDirective).
    if ($script:Config.ClaudeMdManagementPluginInstalled) { Write-Success "claude-md-management plugin already installed"; return $true }
    if (-not (Test-CommandAvailable "claude" -UseCache)) { return $false }

    Write-Info "Installing the claude-md-management plugin (official marketplace)..."
    $null = Invoke-ExternalCommand -Command "claude" -Arguments "plugin marketplace add anthropics/claude-plugins-official" -TimeoutSeconds 30 -Silent
    $result = Invoke-ExternalCommand -Command "claude" -Arguments "plugin install claude-md-management@claude-plugins-official --scope user" -TimeoutSeconds 60
    $reportedSuccess = [bool]($result.Success -or $result.Output -match "already installed")
    if (Test-ClaudePluginInstalled -PluginId "claude-md-management" -InstallReportedSuccess $reportedSuccess) {
        Write-Success "claude-md-management plugin installed"
        $script:Config.ClaudeMdManagementPluginInstalled = $true
        Save-Configuration
        return $true
    }
    Write-Warning "claude-md-management plugin install did not confirm success"
    Write-Log "claude-md-management install output: $(Get-Truncated $result.Output 300)" -Level "WARN"
    return $false
}

function Install-TaskObserverSkill {
    # Ships as a single SKILL.md rather than a plugin. Dropping it into the
    # user-level skills folder (~/.claude/skills/) - rather than each
    # project's own .claude/skills/ - makes it available in every project
    # automatically, the same as the user-scope installs above.
    if ($script:Config.TaskObserverInstalled) { Write-Success "task-observer skill already installed"; return $true }

    $skillDir = Join-Path $env:USERPROFILE ".claude\skills\task-observer"
    $skillFile = Join-Path $skillDir "SKILL.md"
    try {
        if (-not (Test-Path $skillDir)) { New-Item -ItemType Directory -Path $skillDir -Force | Out-Null }
        $sourceUrl = "https://raw.githubusercontent.com/iamneilroberts/claude-skills/main/skills/task-observer/SKILL.md"
        Invoke-WebRequest -Uri $sourceUrl -OutFile $skillFile -TimeoutSec 30 -UseBasicParsing -ErrorAction Stop
        Write-Success "task-observer skill installed"
        $script:Config.TaskObserverInstalled = $true
        Save-Configuration
        return $true
    } catch {
        Write-Warning "Could not download the task-observer skill - continuing without it"
        Write-Log "task-observer download failed: $_" -Level "WARN"
        return $false
    }
}

function Install-CavemanPlugin {
    # Caveman (github.com/JuliusBrussee/caveman, MIT) - a real Claude Code
    # plugin, not a stub: registers a SessionStart hook (src/hooks/caveman-
    # activate.js) that's active from message one with no manual enable step,
    # plus a UserPromptSubmit tracker hook. Makes the MODEL's own responses
    # terser (measured ~65% output-token reduction) - this is the "Caveman"
    # half of OmniRoute's old Stacked pipeline, used directly instead of
    # OmniRoute's own regex reimplementation of the idea. Fully local: no API
    # key, and the README states zero network calls after install. Verified
    # against the real marketplace.json/plugin.json before wiring this in -
    # both the marketplace and plugin are named "caveman".
    if ($script:Config.CavemanInstalled) { Write-Success "Caveman plugin already installed"; return $true }
    if (-not (Test-CommandAvailable "claude" -UseCache)) { return $false }

    Write-Info "Installing the Caveman plugin (terser model output, JuliusBrussee/caveman)..."
    $null = Invoke-ExternalCommand -Command "claude" -Arguments "plugin marketplace add JuliusBrussee/caveman" -TimeoutSeconds 30 -Silent
    $result = Invoke-ExternalCommand -Command "claude" -Arguments "plugin install caveman@caveman --scope user" -TimeoutSeconds 60
    $reportedSuccess = [bool]($result.Success -or $result.Output -match "already installed")
    if (Test-ClaudePluginInstalled -PluginId "caveman" -InstallReportedSuccess $reportedSuccess) {
        Write-Success "Caveman plugin installed - terse mode active from message one"
        Write-Hint "Adjust anytime inside a session: /caveman [lite|full|ultra|off]"
        $script:Config.CavemanInstalled = $true
        Save-Configuration
        return $true
    }
    Write-Warning "Caveman plugin install did not confirm success"
    Write-Log "Caveman install output: $(Get-Truncated $result.Output 300)" -Level "WARN"
    return $false
}

function Install-RtkCli {
    # RTK ("Rust Token Killer", github.com/rtk-ai/rtk, Apache-2.0) - a real,
    # standalone, single-binary local CLI, not a stub: it registers a Claude
    # Code PreToolUse hook that transparently rewrites Bash tool calls (e.g.
    # `git log` -> `rtk git log`) so command/tool output (git, test runners,
    # build tools, Docker, etc.) is filtered/compressed before it reaches the
    # model - this is the "RTK" half of OmniRoute's old Stacked pipeline,
    # used directly instead of OmniRoute's own reimplementation. No API key,
    # no network service, fully local.
    #
    # No winget package exists yet for RTK (github.com/rtk-ai/rtk/issues/383
    # - confirmed, not guessed), so this downloads the official signed
    # Windows release .zip directly instead of assuming a package manager.
    # RTK's own hook (~/.claude/hooks/rtk-rewrite.sh, written by `rtk init
    # -g`) is a bash script requiring jq - Git Bash (already a required
    # dependency of this script, used for headroom) supplies bash.exe; jq is
    # installed via winget if missing, the same pattern as every other
    # winget-installed dependency here.
    if ($script:Config.RtkInstalled) { Write-Success "RTK already installed"; return $true }

    $rtkDir = Join-Path $env:LOCALAPPDATA "rtk"
    $rtkExe = Join-Path $rtkDir "rtk.exe"
    if (-not (Test-Path $rtkExe)) {
        Write-Info "Downloading RTK (terminal/tool-output compression)..."
        $zipPath = Join-Path $env:TEMP "rtk-windows-$PID.zip"
        try {
            if (-not (Test-Path $rtkDir)) { New-Item -ItemType Directory -Path $rtkDir -Force | Out-Null }
            Invoke-WebRequest -Uri "https://github.com/rtk-ai/rtk/releases/latest/download/rtk-x86_64-pc-windows-msvc.zip" `
                -OutFile $zipPath -TimeoutSec 60 -UseBasicParsing -ErrorAction Stop
            Expand-Archive -Path $zipPath -DestinationPath $rtkDir -Force
        } catch {
            Write-Warning "Could not download RTK - continuing without it"
            Write-Log "RTK download failed: $_" -Level "WARN"
            return $false
        } finally {
            Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
        }
    }
    if (-not (Test-Path $rtkExe)) {
        Write-Warning "RTK download did not produce rtk.exe - continuing without it"
        return $false
    }
    $script:DependencyCache.Remove("rtk")
    Sync-ProcessPathFromRegistry
    if ($env:PATH -notlike "*$rtkDir*") { $env:PATH = "$rtkDir;$env:PATH" }

    $bash = Find-ExecutableInPaths -Name "bash" -SearchPaths @(
        "$env:ProgramFiles\Git\bin", "${env:ProgramFiles(x86)}\Git\bin", "$env:ProgramFiles\Git\usr\bin"
    )
    if (-not $bash) {
        Write-Warning "Git Bash not found - RTK's Claude Code hook needs it, skipping hook registration"
        return $false
    }
    if (-not (Test-CommandAvailable "jq" -UseCache)) {
        Write-Info "Installing jq (required by RTK's Claude Code hook)..."
        $null = Install-ViaWinget -WingetId "jqlang.jq" -FriendlyName "jq" -TimeoutSeconds 120
    }

    Write-Info "Registering RTK as a Claude Code hook..."
    $bashRtkDir = ($rtkDir -replace '\\', '/') -replace '^([A-Za-z]):', '/$1'
    $result = Invoke-ExternalCommand -Command $bash `
        -Arguments "-lc `"export PATH='$bashRtkDir':`$PATH; rtk init -g`"" `
        -TimeoutSeconds 30 -ShowSpinner -SpinnerLabel "Registering RTK hook"
    if ($result.Success) {
        Write-Success "RTK installed and registered as a Claude Code hook"
        $script:Config.RtkInstalled = $true
        Save-Configuration
        return $true
    }
    Write-Warning "RTK hook registration did not confirm success"
    Write-Log "rtk init -g output: $(Get-Truncated $result.Output 300)" -Level "WARN"
    return $false
}

# ============================================================================
# AGENT SKILLS ECOSYSTEM (v5.7)
#   A second, more loosely-coupled tier below Install-CompanionTooling: local
#   model support, IDE/proxy integrations, and a handful of real third-party
#   Claude Code skills. Same idempotent, best-effort, never-blocks-launch
#   contract as everything above - but unlike the companion tools (all
#   verified low-risk: no credentials handled, no scraping, no OAuth), a
#   couple of these touch things that genuinely warrant a pause:
#     - Agent Reach stores platform login cookies and carries an explicit
#       upstream account-lockout warning, so it's gated behind Read-YesNo.
#     - The Antigravity proxy needs the USER's own Google OAuth login, which
#       this script will never perform (see CLAUDE.md safety rules) - it only
#       gets the code/config scaffolded, then prints the manual step.
#   Skipped entirely in spawned child windows for the same reason
#   Show-GraphResult skips its prompt there: nobody may be watching.
# ============================================================================

function Install-LMStudioSupport {
    # LM Studio (lmstudio.ai) - local model runner used by run_benchmarks.py.
    # This function only detects/verifies the `lms` CLI; it does not install
    # LM Studio itself (no reliable silent/winget install path confirmed) -
    # if missing, it points at the download page instead of guessing.
    if ($script:Config.LMStudioSupportInstalled) { Write-Success "LM Studio CLI already verified"; return $true }

    $lmsPath = $null
    if (Test-CommandAvailable "lms" -UseCache) { $lmsPath = "lms" }
    else {
        $fallback = Join-Path $env:USERPROFILE ".lmstudio\bin\lms.exe"
        if (Test-Path $fallback) { $lmsPath = $fallback }
    }
    if (-not $lmsPath) {
        Write-Info "LM Studio CLI (lms) not found - install LM Studio from https://lmstudio.ai, run it once, then re-run this launcher."
        return $false
    }

    $result = Invoke-ExternalCommand -Command $lmsPath -Arguments "--version" -TimeoutSeconds 15 -Silent -NoLog
    if ($result.Success) {
        Write-Success "LM Studio CLI verified ($lmsPath)"
        Write-Hint "Benchmark local models: uv run run_benchmarks.py --dry-run"
        $script:Config.LMStudioSupportInstalled = $true
        Save-Configuration
        return $true
    }
    Write-Warning "Found lms but couldn't confirm it runs - continuing without LM Studio support"
    return $false
}

function Install-AntigravityProxySupport {
    # Google Antigravity's own OpenAI-compatible endpoint doesn't (as of this
    # writing) accept a local LM Studio backend directly, so the community
    # standard bridge is an OpenAI-compatible gateway sat in front of it -
    # frieser/antigravity-proxy (MIT), the most actively maintained of the
    # several equivalent projects surveyed. This function only clones it and
    # scaffolds its .env from .env.example: the proxy needs YOUR OWN Google
    # account OAuth login to actually authenticate, which is a manual step by
    # design - this script does not perform sign-ins or handle credentials
    # (see the safety rules this project's CLAUDE.md is built under).
    if ($script:Config.AntigravityProxyInstalled) { Write-Success "Antigravity proxy already scaffolded"; return $true }
    if ($script:IsChild) { return $false }
    if (-not (Test-CommandAvailable "git" -UseCache) -or -not (Test-CommandAvailable "npm" -UseCache)) {
        Write-Log "Antigravity proxy skipped - git or npm not found" -Level "DEBUG"
        return $false
    }

    $toolsDir = Join-Path $env:USERPROFILE ".llm-token-optimizer\antigravity-proxy"
    if (-not (Test-Path $toolsDir)) {
        Write-Info "Cloning antigravity-proxy (frieser/antigravity-proxy)..."
        $parent = Split-Path $toolsDir -Parent
        if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        $oldGitPrompt = $env:GIT_TERMINAL_PROMPT
        try {
            $env:GIT_TERMINAL_PROMPT = "0"
            $cloneArgs = "clone --quiet ""https://github.com/frieser/antigravity-proxy.git"" ""$toolsDir"""
            $null = Invoke-ExternalCommand -Command "git" -Arguments $cloneArgs -TimeoutSeconds 60 -ShowSpinner -SpinnerLabel "Cloning antigravity-proxy"
        } finally { $env:GIT_TERMINAL_PROMPT = $oldGitPrompt }
    }
    if (-not (Test-Path $toolsDir)) { Write-Warning "Failed to clone antigravity-proxy"; return $false }

    $envExample = Join-Path $toolsDir ".env.example"
    $envFile = Join-Path $toolsDir ".env"
    if ((Test-Path $envExample) -and -not (Test-Path $envFile)) {
        Copy-Item $envExample $envFile -ErrorAction SilentlyContinue
    }

    Write-Info "Installing antigravity-proxy dependencies (npm install)..."
    $result = Invoke-ExternalCommand -Command "npm" -Arguments "install" -WorkingDirectory $toolsDir -TimeoutSeconds 180 -ShowSpinner -SpinnerLabel "npm install (antigravity-proxy)"
    if (-not $result.Success) {
        Write-Warning "antigravity-proxy npm install did not confirm success"
        Write-Log "antigravity-proxy npm install output: $(Get-Truncated $result.Output 300)" -Level "WARN"
        return $false
    }

    Write-Success "antigravity-proxy cloned and installed: $toolsDir"
    Write-Hint "Manual step required (this script does not handle Google sign-in): edit .env, then run 'npm start' inside that folder and sign in when prompted."
    $script:Config.AntigravityProxyInstalled = $true
    Save-Configuration
    return $true
}

function Install-AgentSkillsPack {
    # addyosmani/agent-skills (MIT) - 24 production-grade engineering skills
    # (code review checklists, testing discipline, etc.) distributed as plain
    # Markdown via the open `skills` CLI. Live-tested before wiring this in:
    # the bare `npx skills add addyosmani/agent-skills` installs PROJECT-
    # local (a `.agents/skills` folder under $PWD), which contradicts this
    # function's own "installed once at user scope" placement in
    # Install-CompanionTooling - confirmed fixed with `-g -a claude-code`
    # (global scope, Claude Code target only), which lands the 24 skills
    # directly in ~/.claude/skills/ instead, exit code 0, verified live.
    if ($script:Config.AgentSkillsPackInstalled) { Write-Success "agent-skills pack already installed"; return $true }
    if (-not (Test-CommandAvailable "npx" -UseCache)) { return $false }

    Write-Info "Installing addyosmani/agent-skills (npx skills add -g)..."
    $result = Invoke-ExternalCommand -Command "npx" -Arguments "-y skills add addyosmani/agent-skills -g -a claude-code -y" -TimeoutSeconds 90 -ShowSpinner -SpinnerLabel "Installing agent-skills"
    if ($result.Success -or $result.Output -match "already installed|up to date|Done!") {
        Write-Success "agent-skills pack installed (~/.claude/skills)"
        $script:Config.AgentSkillsPackInstalled = $true
        Save-Configuration
        return $true
    }
    Write-Warning "agent-skills install did not confirm success"
    Write-Log "agent-skills install output: $(Get-Truncated $result.Output 300)" -Level "WARN"
    return $false
}

function Install-AgentReachSkill {
    # Agent Reach (Panniantong/agent-reach) - gives Claude Code no-API-key
    # internet access (17 platforms: X, Reddit, GitHub, YouTube, etc.) via a
    # real pip-installable CLI (pyproject.toml at the repo root - confirmed,
    # not guessed). Deliberately NOT wired up via the project's own
    # documented install method ("paste this instruction and let your agent
    # fetch+follow a remote install.md") - that pattern is exactly the
    # fetch-and-execute-untrusted-remote-instructions shape this script's own
    # safety rules exist to avoid, regardless of the source's intent. Cloning
    # the repo and running its own pyproject-declared install is the same
    # auditable pattern already used for Superpowers/Caveman above.
    #
    # Also gated behind an explicit prompt (never silent, unlike the other
    # installers here): the tool stores platform login cookies and its own
    # README carries an explicit account-lockout risk warning for scripted
    # access to sites like X/Twitter - a real tradeoff only the user can
    # accept, not a default-on install.
    if ($script:Config.AgentReachInstalled) { Write-Success "Agent Reach already installed"; return $true }
    if ($script:IsChild) { return $false }
    if (-not (Test-CommandAvailable "git" -UseCache) -or -not (Test-CommandAvailable "pip" -UseCache)) {
        Write-Log "Agent Reach skipped - git or pip not found" -Level "DEBUG"
        return $false
    }

    Write-Host ""
    Write-Info "Agent Reach gives Claude Code internet access (X/Reddit/YouTube/GitHub/etc, no API keys)."
    Write-Hint "It stores platform login cookies locally and its own docs warn of account-lockout risk"
    Write-Hint "for scripted access - use a throwaway/test account, not your primary one."
    if (-not (Read-YesNo "Install Agent Reach?" $false)) {
        Write-Info "Skipped Agent Reach"
        return $false
    }

    $toolsDir = Join-Path $env:USERPROFILE ".llm-token-optimizer\agent-reach"
    if (-not (Test-Path $toolsDir)) {
        $parent = Split-Path $toolsDir -Parent
        if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        $oldGitPrompt = $env:GIT_TERMINAL_PROMPT
        try {
            $env:GIT_TERMINAL_PROMPT = "0"
            $cloneArgs = "clone --quiet ""https://github.com/Panniantong/agent-reach.git"" ""$toolsDir"""
            $null = Invoke-ExternalCommand -Command "git" -Arguments $cloneArgs -TimeoutSeconds 60 -ShowSpinner -SpinnerLabel "Cloning agent-reach"
        } finally { $env:GIT_TERMINAL_PROMPT = $oldGitPrompt }
    }
    if (-not (Test-Path $toolsDir)) { Write-Warning "Failed to clone agent-reach"; return $false }

    Write-Info "Installing agent-reach (pip install -e .)..."
    $result = Invoke-ExternalCommand -Command "pip" -Arguments "install -e ." -WorkingDirectory $toolsDir -TimeoutSeconds 180 -ShowSpinner -SpinnerLabel "pip install agent-reach"
    if (-not $result.Success) {
        Write-Warning "agent-reach pip install did not confirm success"
        Write-Log "agent-reach install output: $(Get-Truncated $result.Output 300)" -Level "WARN"
        return $false
    }

    Write-Success "Agent Reach installed: $toolsDir"
    Write-Hint "Not yet registered into your Claude Code skill directory - review $toolsDir\SECURITY.md, then run 'agent-reach install --system' yourself when ready."
    $script:Config.AgentReachInstalled = $true
    Save-Configuration
    return $true
}

function Install-AppleDesignSkill {
    # Apple HIG Designer (tristan-mcinnis/apple-hig-designer-skill-2026, MIT)
    # - a plain SKILL.md + references/ skill (confirmed: no CLI, no network
    # calls, no credentials) covering Apple Human Interface Guidelines across
    # iOS/macOS/watchOS/tvOS/visionOS including the current "Liquid Glass"
    # design language. Same git-clone-into-skills-dir pattern as Superpowers.
    if ($script:Config.AppleDesignSkillInstalled) { Write-Success "Apple design skill already installed"; return $true }
    if (-not (Test-CommandAvailable "git" -UseCache)) { return $false }

    $skillDir = Join-Path $env:USERPROFILE ".claude\skills\apple-hig-designer"
    if (-not (Test-Path $skillDir)) {
        Write-Info "Installing Apple HIG design skill (tristan-mcinnis/apple-hig-designer-skill-2026)..."
        $oldGitPrompt = $env:GIT_TERMINAL_PROMPT
        try {
            $env:GIT_TERMINAL_PROMPT = "0"
            $cloneArgs = "clone --quiet --depth 1 ""https://github.com/tristan-mcinnis/apple-hig-designer-skill-2026.git"" ""$skillDir"""
            $null = Invoke-ExternalCommand -Command "git" -Arguments $cloneArgs -TimeoutSeconds 60 -ShowSpinner -SpinnerLabel "Cloning Apple HIG skill"
        } finally { $env:GIT_TERMINAL_PROMPT = $oldGitPrompt }
        Remove-Item (Join-Path $skillDir ".git") -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (-not (Test-Path (Join-Path $skillDir "SKILL.md"))) {
        Write-Warning "Apple design skill clone did not produce SKILL.md"
        return $false
    }
    Write-Success "Apple design skill installed (iOS/macOS/watchOS/tvOS/visionOS, Liquid Glass)"
    $script:Config.AppleDesignSkillInstalled = $true
    Save-Configuration
    return $true
}

function Install-HermesGraphifyPlatform {
    # Hermes Agent (Nous Research) reads skills from ~/.hermes/skills/ - its
    # own "Skills Hub" mechanism (confirmed: hermes-agent.nousresearch.com/
    # docs/skills). Graphify already ships a `hermes install` target
    # (confirmed via `graphify --help` on this machine) that writes its own
    # skill there, the same way Install-GraphifyPlatform does for Claude
    # Code above - reusing that instead of inventing a separate "skill hub"
    # mechanism this project has no way to distribute skills through.
    if ($script:Config.HermesGraphifyInstalled) { Write-Success "Graphify already registered with Hermes"; return $true }
    if (-not (Test-CommandAvailable "graphify" -UseCache)) { return $false }
    $hermesDir = Join-Path $env:USERPROFILE ".hermes"
    if (-not (Test-Path $hermesDir)) {
        Write-Log "Hermes Agent not detected (no ~/.hermes) - skipping Hermes skill registration" -Level "DEBUG"
        return $false
    }
    Write-Info "Registering Graphify as a Hermes Agent skill (~/.hermes/skills/graphify)..."
    $result = Invoke-ExternalCommand -Command "graphify" -Arguments "install --platform hermes" -TimeoutSeconds 30
    if ($result.Success) {
        Write-Success "Graphify registered with Hermes Agent's skill hub"
        $script:Config.HermesGraphifyInstalled = $true
        Save-Configuration
        return $true
    }
    Write-Warning "Hermes registration did not confirm success"
    Write-Log "graphify install --platform hermes output: $(Get-Truncated $result.Output 300)" -Level "WARN"
    return $false
}

function Install-WatchVideoSkill {
    # Self-authored (not cloned from a third-party repo of unverified
    # quality/license) rather than one of the several community "/watch"
    # skills surveyed - all follow the same shape (transcript + sampled
    # frames via yt-dlp), and yt-dlp is already confirmed present on this
    # machine (see last30days setup), so this can be a genuinely self-
    # contained skill with no extra dependency to install.
    if ($script:Config.WatchVideoSkillInstalled) { Write-Success "Watch-video skill already installed"; return $true }

    $skillDir = Join-Path $env:USERPROFILE ".claude\skills\watch-video"
    $skillFile = Join-Path $skillDir "SKILL.md"
    try {
        if (-not (Test-Path $skillDir)) { New-Item -ItemType Directory -Path $skillDir -Force | Out-Null }
        $content = @"
---
name: watch-video
description: Watch a video (YouTube or any yt-dlp-supported URL) by extracting its transcript and sampled frames, so Claude can answer questions about what actually happens on screen, not just what's said. Use when the user pastes a video link and asks Claude to watch, summarize, review, or give feedback on it, or invokes /watch.
---

# Watch Video

Gives Claude the ability to "watch" a video: a timestamped transcript plus a
handful of representative still frames, so it can reason about visuals
(on-screen text, UI, cuts, framing) in addition to what's said.

## Steps

1. Confirm ``yt-dlp`` is on PATH (``yt-dlp --version``). If missing, tell the
   user to install it rather than trying to install it yourself mid-session.
2. Fetch the transcript first - it's cheap and often enough on its own:
   ``yt-dlp --write-auto-sub --sub-lang en --skip-download --sub-format vtt -o "<tmp>/%(id)s.%(ext)s" <URL>``
3. Only if the question needs visuals (UI walkthroughs, "what does this look
   like", design/motion feedback, hooks/retention analysis), also pull frames:
   - Get duration: ``yt-dlp --print duration <URL>``
   - Sample evenly, capped at 20 frames for a short video (<10 min) or 40 for
     longer ones - never uncapped, to keep token cost bounded.
   - Extract with ffmpeg at the chosen timestamps into ``<tmp>/frames/``.
4. Read the transcript file and the extracted frame images together. Cite
   timestamps (``[MM:SS]``) when referencing specific moments.
5. Clean up the temp directory when done.

## Notes

- Prefer the transcript alone when the question is purely about content/
  claims/narration - don't burn tokens on frames nobody asked about.
- For very long videos (>1 hour), summarize in chapters rather than trying
  to hold the whole transcript in context at once.
- This skill only reads publicly accessible video URLs; it does not download
  or redistribute copyrighted video content beyond the local temp frames/
  transcript needed to answer the user's question in this session.
"@
        [System.IO.File]::WriteAllText($skillFile, $content, (New-Object System.Text.UTF8Encoding($false)))
        Write-Success "Watch-video skill installed (/watch via yt-dlp transcript + sampled frames)"
        $script:Config.WatchVideoSkillInstalled = $true
        Save-Configuration
        return $true
    } catch {
        Write-Warning "Could not write the watch-video skill"
        Write-Log "watch-video skill write failed: $_" -Level "WARN"
        return $false
    }
}

function Install-PromptEngineeringSkill {
    # Deliberately NOT a verbatim copy of any leaked/proprietary system
    # prompt (from asgeirtj/system_prompts_leaks or any other such repo).
    # Those are unauthorized extracts of other companies' unpublished
    # product prompts - redistributing them wholesale into a user's own repo
    # is a real copyright/ToS gray area regardless of the leaking repo's own
    # license, not something to automate silently. What's actually useful
    # and safe to reuse is the STRUCTURE those leaks make visible across
    # dozens of tools - synthesized here as general technique, credited to
    # the source for anyone who wants primary material.
    if ($script:Config.PromptEngineeringSkillInstalled) { Write-Success "Prompt-engineering skill already installed"; return $true }

    $skillDir = Join-Path $env:USERPROFILE ".claude\skills\prompt-engineering-patterns"
    $skillFile = Join-Path $skillDir "SKILL.md"
    try {
        if (-not (Test-Path $skillDir)) { New-Item -ItemType Directory -Path $skillDir -Force | Out-Null }
        $content = @"
---
name: prompt-engineering-patterns
description: Structural patterns for writing system prompts, skill files, and agent instructions - sectioning, tool-use framing, tone control, safety boundaries. Use when authoring or reviewing a SKILL.md, CLAUDE.md, subagent prompt, or any other instruction document.
---

# Prompt Engineering Patterns

Techniques synthesized from studying publicly leaked system prompts across
dozens of production AI coding tools (see Source below) - not a copy of any
one of them. Apply the shape, not the wording.

## Structural patterns

- **Front-load identity and scope in one line**, then expand. Readers (and
  models) latch onto the first sentence; bury it and it gets skimmed past.
- **Group by concern, not by chronology.** Sections like "Tool use", "Safety
  boundaries", "Tone", "Output format" scan better than a narrative walk
  through a session.
- **State the negative space explicitly.** "Don't do X" is often more load-
  bearing than "do Y" - models (and juniors) default toward the common case;
  spell out the exception.
- **Put the highest-stakes rule first in its section**, not buried after
  three paragraphs of context - truncation and skimming both favor the top.
- **Prefer concrete failure examples over abstract principles** ("don't
  reproduce song lyrics" beats "respect copyright") - a rule you can check
  against a specific case is a rule that actually gets followed.

## Tool-use framing

- Describe WHEN to reach for a tool, not just what it does - "use X when Y"
  beats a bare capability list every time.
- Give an explicit fallback order when several tools overlap (as this
  project's own CLAUDE.md does for graphify vs. grep) - ambiguity here is
  where models thrash between options.

## Tone control

- If you want terse output, say so directly and give a length bound - vague
  requests like "be concise" undershoot less reliably than "under 3
  sentences unless asked for more."
- Match register to audience once, up front, rather than re-asserting it
  per section.

## Safety boundaries

- Separate "never do this" from "do this only with confirmation" - collapsing
  them into one list makes the truly hard boundaries easier to miss.
- State WHY a boundary exists when there's a concrete incident or risk behind
  it (see this project's own CLAUDE.md style) - a reason is easier to reason
  about at the edges than a bare rule.

## Source

Patterns synthesized from public analysis of https://github.com/asgeirtj/system_prompts_leaks
and similar collections - read that repo directly for primary material and
tool-specific examples; this skill intentionally does not reproduce any of
it verbatim.
"@
        [System.IO.File]::WriteAllText($skillFile, $content, (New-Object System.Text.UTF8Encoding($false)))
        Write-Success "Prompt-engineering-patterns skill installed"
        $script:Config.PromptEngineeringSkillInstalled = $true
        Save-Configuration
        return $true
    } catch {
        Write-Warning "Could not write the prompt-engineering-patterns skill"
        Write-Log "prompt-engineering skill write failed: $_" -Level "WARN"
        return $false
    }
}

function Test-AgentSkillsEcosystemComplete {
    return [bool](
        $script:Config.LMStudioSupportInstalled -and
        $script:Config.AgentSkillsPackInstalled -and
        $script:Config.AppleDesignSkillInstalled -and
        $script:Config.WatchVideoSkillInstalled -and
        $script:Config.PromptEngineeringSkillInstalled
    )
}

function Install-AgentSkillsEcosystem {
    # Second-tier installer, called from Install-CompanionTooling. Deliberately
    # excludes Test-AgentSkillsEcosystemComplete's gate from covering
    # Antigravity proxy / Agent Reach / Hermes: those three are legitimately
    # opt-in or environment-gated (manual OAuth, explicit risk confirmation,
    # Hermes-not-installed) rather than "should eventually become true for
    # everyone", so their own $script:Config flags plus each function's
    # internal checks are the only gate they need.
    if (Test-AgentSkillsEcosystemComplete) {
        Write-Success "agent-skills, apple-hig-designer, watch-video, prompt-engineering-patterns, LM Studio - all present"
    } else {
        Write-Hint "Installing the wider agent-skills ecosystem: addyosmani/agent-skills, Apple HIG"
        Write-Hint "design skill, /watch video skill, prompt-engineering patterns, and verifying"
        Write-Hint "LM Studio support."
        $null = Install-LMStudioSupport
        $null = Install-AgentSkillsPack
        $null = Install-AppleDesignSkill
        $null = Install-WatchVideoSkill
        $null = Install-PromptEngineeringSkill
    }
    # Environment-gated / opt-in - never part of the "complete" fast-path above.
    Install-HermesGraphifyPlatform | Out-Null
    if (-not $script:IsChild) {
        $null = Install-AntigravityProxySupport
        $null = Install-AgentReachSkill
    }
}

function Test-CompanionToolingComplete {
    # All eight recorded present - lets callers skip the section (and its
    # noise) entirely once there's nothing left to do.
    return [bool](
        $script:Config.ClaudeMemInstalled -and
        $script:Config.HeadroomInstalled -and
        $script:Config.ClaudeCodeSetupPluginInstalled -and
        $script:Config.TaskObserverInstalled -and
        $script:Config.ClaudeMdManagementPluginInstalled -and
        $script:Config.CavemanInstalled -and
        $script:Config.RtkInstalled -and
        $script:Config.Context7McpRegistered -and
        $script:Config.ContextModeMcpRegistered
    )
}

function Install-CompanionTooling {
    [CmdletBinding()]
    param([int]$Step = 0, [int]$TotalSteps = 0)
    $originalNineComplete = Test-CompanionToolingComplete
    if ($originalNineComplete) {
        Write-Section -Name "Companion tooling" -Step $Step -TotalSteps $TotalSteps
        Write-Success "claude-mem, headroom, claude-code-setup, task-observer, claude-md-management, caveman, rtk, context7, context-mode - all present"
    } else {
        Write-Section -Name "Companion tooling" -Step $Step -TotalSteps $TotalSteps
        Write-Hint "claude-mem (memory), headroom (context bar), claude-code-setup"
        Write-Hint "(auto-recommendations), task-observer (skill improvement),"
        Write-Hint "claude-md-management (keeps CLAUDE.md itself current), caveman"
        Write-Hint "(terser model output), rtk (terminal/tool-output compression),"
        Write-Hint "context7 (on-demand library docs), and context-mode (tool-output"
        Write-Hint "sandboxing + session memory) - installed once at user scope, so"
        Write-Hint "every project gets all nine."
        $null = Install-ClaudeMem
        $null = Install-HeadroomStatusline
        $null = Install-ClaudeCodeSetupPlugin
        $null = Install-TaskObserverSkill
        $null = Install-ClaudeMdManagementPlugin
        $null = Install-CavemanPlugin
        $null = Install-RtkCli
        Register-Context7Mcp
        Install-ContextModeMcp
        Install-ClaudePluginsAndSkills -Quiet
    }
    # NOT gated behind $originalNineComplete's early return above (that was a
    # real bug during development: this call sat after a `return` inside the
    # completeness branch, so on any machine where the original nine were
    # already installed - e.g. this one - Install-AgentSkillsEcosystem would
    # never run at all). It has its own internal completeness gate
    # (Test-AgentSkillsEcosystemComplete) so calling it unconditionally here
    # is still cheap once everything's already in place.
    Install-AgentSkillsEcosystem
}

# ============================================================================
# (OmniRoute removed in v5.5 - see AUDIT.md. Its role is now filled directly
# by the real open-source tools it wrapped: Install-CavemanPlugin and
# Install-RtkCli above, both called from Install-CompanionTooling. Claude
# Code launches with its own native model defaults; there is no gateway/
# 1M-context model-picker restriction to configure.)
# ============================================================================

function Register-Context7Mcp {
    # Context7 (upstash/context7-mcp): injects version-specific library/API
    # documentation on demand instead of Claude guessing from training data
    # or spending turns grepping node_modules/site-packages for the answer.
    # Pure stdio MCP server - no ANTHROPIC_BASE_URL, no proxying of Claude's
    # own API traffic, no Anthropic OAuth involvement of any kind, unlike
    # OmniRoute or the (declined - see AUDIT.md) headroom-ai proxy. Works
    # without an API key at reduced rate limits; not passing one here keeps
    # this a genuinely zero-config, zero-account addition.
    if ($script:Config.Context7McpRegistered) { return }
    if (-not (Test-CommandAvailable "claude" -UseCache)) { return }
    if (-not (Test-CommandAvailable "npx" -UseCache)) {
        Write-Log "Context7 MCP skipped - npx not found on PATH" -Level "DEBUG"
        return
    }

    Write-Info "Registering Context7 (library docs) as an MCP server for Claude Code..."
    $result = Invoke-ExternalCommand -Command "claude" -Arguments "mcp add --scope user context7 -- npx -y @upstash/context7-mcp" -TimeoutSeconds 30 -Silent
    if ($result.Success -or $result.Output -match "already exists|already added") {
        Write-Success "Context7 registered as an MCP server (user scope)"
        $script:Config.Context7McpRegistered = $true
        Save-Configuration
    } else {
        Write-Log "claude mcp add context7 did not confirm success: $(Get-Truncated $result.Output 200)" -Level "DEBUG"
    }
}

function Install-ContextModeMcp {
    # Context Mode (mksglu/context-mode): sandboxes tool output - e.g. a 56 KB
    # shell dump becomes a 299-byte summary with the full output indexed into
    # local SQLite FTS5 for on-demand BM25 search - plus persists session
    # memory across compaction instead of re-dumping history. No
    # ANTHROPIC_BASE_URL, no proxying of Claude's traffic, no OAuth
    # involvement - same "genuinely local" bar Context7 and RTK were held to.
    # Complementary to RTK rather than redundant: RTK compresses command
    # output at the shell/hook layer before Claude ever sees it; Context Mode
    # operates at the MCP layer with intent-driven filtering and cross-session
    # memory RTK doesn't provide. Both can run at once.
    #
    # Installed via the plugin marketplace (same pattern as Install-
    # CavemanPlugin), NOT a bare `npx context-mode-mcp` MCP registration - an
    # earlier version of this function guessed at that package name and it
    # doesn't exist. The real npm package is named `context-mode` (confirmed
    # against the npm registry), and the project's own README documents the
    # marketplace path as recommended over MCP-only, since it also wires up
    # the hook-based routing that nudges the model to prefer the sandbox
    # tools over raw Bash/Read. Live-tested end to end before wiring this in:
    # marketplace add + plugin install both succeeded, `claude plugin list`
    # showed it enabled, and `claude mcp list` showed the plugin's own MCP
    # server connected (`plugin:context-mode:context-mode: ... - Connected`).
    if ($script:Config.ContextModeMcpRegistered) { Write-Success "Context Mode plugin already installed"; return $true }
    if (-not (Test-CommandAvailable "claude" -UseCache)) { return $false }

    Write-Info "Installing the Context Mode plugin (tool-output sandboxing, mksglu/context-mode)..."
    $null = Invoke-ExternalCommand -Command "claude" -Arguments "plugin marketplace add mksglu/context-mode" -TimeoutSeconds 30 -Silent
    $result = Invoke-ExternalCommand -Command "claude" -Arguments "plugin install context-mode@context-mode --scope user" -TimeoutSeconds 60
    $reportedSuccess = [bool]($result.Success -or $result.Output -match "already installed")
    if (Test-ClaudePluginInstalled -PluginId "context-mode" -InstallReportedSuccess $reportedSuccess) {
        Write-Success "Context Mode plugin installed - tool-output sandboxing active"
        $script:Config.ContextModeMcpRegistered = $true
        Save-Configuration
        return $true
    }
    Write-Warning "Context Mode plugin install did not confirm success"
    Write-Log "Context Mode install output: $(Get-Truncated $result.Output 300)" -Level "WARN"
    return $false
}

function Test-ClaudeMemWorkerHealthy {
    # Plain TCP-connect liveness probe (~250ms) - deliberately does NOT
    # assume any particular HTTP path on claude-mem's worker (it's a bundled/
    # minified third-party service; guessing at "/health" and getting it
    # wrong would make this MORE dangerous, not less). A successful connect
    # means something is actively accepting connections on the port right
    # now, which is exactly the fact both Repair-ClaudeMemWorker and
    # Fix-ClaudeMemWorker.ps1 need before deciding whether killing it would
    # interrupt a session that's actually using it.
    [CmdletBinding()]
    param([int]$Port = $(if ($env:CLAUDE_MEM_WORKER_PORT) { [int]$env:CLAUDE_MEM_WORKER_PORT } else { 37777 }))
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $connectTask = $client.ConnectAsync("127.0.0.1", $Port)
        $ok = $connectTask.Wait(750) -and $client.Connected
        $client.Close()
        return [bool]$ok
    } catch {
        return $false
    }
}

function Repair-ClaudeMemWorker {
    # Self-heal for a tracked claude-mem bug on Windows
    # (github.com/thedotmack/claude-mem/issues/2926, open upstream as of this
    # writing): the background worker can die without releasing its listener
    # on port 37777 (CLAUDE_MEM_WORKER_PORT). The next session's worker then
    # fails to bind ("Failed to start server. Is port 37777 in use?") and
    # never comes up - and because claude-mem's UserPromptSubmit hook fails
    # CLOSED instead of open when the worker is unreachable, every prompt
    # gets blocked while ~/.claude-mem/state/hook-failures.json's
    # consecutiveFailures counter climbs without bound. Runs right before
    # Claude launches (same placement as Test-CompressionMethodsActive) so a
    # stale prior session can never carry over and block this one. No-op if
    # claude-mem was never installed. Entirely best-effort - every step is
    # wrapped so a failure here never blocks or delays launch.
    #
    # Multi-session safety: the worker is ONE process shared by every Claude
    # Code session on the machine (v5.0+ of this launcher deliberately allows
    # several project windows to run concurrently - see Start-ProjectWindow).
    # If two or three of those windows launch within moments of each other,
    # each calls this function - so the whole body runs under a short-lived
    # machine-wide mutex to stop them racing Test-Path/Get-Content/Remove-Item
    # against the SAME hook-failures.json at the same instant (confirmed
    # possible live: a delete landing between another window's Test-Path and
    # Get-Content throws a sharing-violation that the inner try/catch below
    # was silently swallowing as a DEBUG log - harmless, but it could skip a
    # repair that should have happened). A window that can't get the mutex
    # within 3s just skips its own repair pass rather than blocking launch -
    # whichever window holds it will have already left clean state behind.
    [CmdletBinding()]
    param()
    if (-not $script:Config.ClaudeMemInstalled) { return }

    $repairMutex = $null
    $haveMutex = $false
    try {
        $repairMutex = New-Object System.Threading.Mutex($false, "Global\LLMTokenOptimizer_ClaudeMemRepair")
        try { $haveMutex = $repairMutex.WaitOne(3000, $false) }
        catch [System.Threading.AbandonedMutexException] { $haveMutex = $true }
    } catch {
        Write-Log "Repair-ClaudeMemWorker mutex unavailable, proceeding unserialized: $_" -Level "DEBUG"
        $haveMutex = $true  # fail open - a missing mutex should never block the repair entirely
    }
    if (-not $haveMutex) {
        Write-Log "Repair-ClaudeMemWorker: another window is repairing right now - skipping this pass" -Level "DEBUG"
        if ($repairMutex) { try { $repairMutex.Dispose() } catch {} }
        return
    }

    try {
        # 1. Reclaim the worker port ONLY if the process holding it is
        # orphaned - i.e. no longer enumerable from this session (Get-Process
        # on its PID returns nothing), the exact signature the upstream bug
        # report captured via Get-NetTCPConnection/Get-Process. A live,
        # still-enumerable worker (this session's own, or a healthy one
        # another window is actively using) is left alone; killing that would
        # interrupt every other open Claude Code session's memory capture,
        # not repair anything.
        try {
            $port = if ($env:CLAUDE_MEM_WORKER_PORT) { [int]$env:CLAUDE_MEM_WORKER_PORT } else { 37777 }
            $conns = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
            foreach ($conn in $conns) {
                $ownerPid = $conn.OwningProcess
                $proc = Get-Process -Id $ownerPid -ErrorAction SilentlyContinue
                if (-not $proc) {
                    Write-Log "claude-mem worker port $port held by orphaned PID $ownerPid - reclaiming" -Level "WARN"
                    Stop-Process -Id $ownerPid -Force -ErrorAction SilentlyContinue
                }
            }
        } catch {
            # Get-NetTCPConnection needs the NetTCPIP module, not guaranteed on
            # every Windows install/PS edition - silently skip the port check
            # rather than fail the whole repair over it.
            Write-Log "Repair-ClaudeMemWorker port check skipped: $_" -Level "DEBUG"
        }

        # 2. Clear a stuck failure counter so a freshly (re)started worker gets
        # a clean slate instead of inheriting a count that upstream currently
        # only resets via a full manual cleanup. Threshold of 10 distinguishes
        # a transient blip (worker slow to cold-boot, or several sessions
        # cold-starting at once) from the actually-stuck case the bug report
        # describes (seen live: consecutiveFailures reaching 127) - AND only
        # fires if the worker port isn't currently answering, so a counter
        # that's climbing for some OTHER reason while the worker itself is
        # demonstrably healthy is left alone rather than masked.
        try {
            $cmemStateDir = Join-Path $env:USERPROFILE ".claude-mem\state"
            $failFile = Join-Path $cmemStateDir "hook-failures.json"
            if (Test-Path $failFile) {
                $state = Get-Content $failFile -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
                $count = 0
                if ($state.PSObject.Properties.Name -contains "consecutiveFailures") { $count = [int]$state.consecutiveFailures }
                if ($count -ge 10 -and -not (Test-ClaudeMemWorkerHealthy)) {
                    Write-Warning "claude-mem's worker was stuck unreachable ($count failed hooks in a row) - clearing its failure state"
                    Remove-Item $failFile -Force -ErrorAction SilentlyContinue
                    $supervisorFile = Join-Path $env:USERPROFILE ".claude-mem\supervisor.json"
                    Remove-Item $supervisorFile -Force -ErrorAction SilentlyContinue
                    Write-Log "Cleared claude-mem hook-failures.json (was $count) and supervisor.json" -Level "INFO"
                } elseif ($count -ge 10) {
                    Write-Log "hook-failures.json shows $count consecutive failures but the worker port answers now - leaving state alone (likely already recovering)" -Level "DEBUG"
                }
            }
        } catch {
            # A malformed/unreadable state file is exactly the kind of thing
            # this function exists to route around - log and move on rather
            # than let a JSON parse error block Claude from launching.
            Write-Log "Repair-ClaudeMemWorker failure-state check skipped: $_" -Level "DEBUG"
        }
    } finally {
        if ($repairMutex) {
            try { if ($haveMutex) { $repairMutex.ReleaseMutex() } } catch {}
            try { $repairMutex.Dispose() } catch {}
        }
    }
}

function Test-CompressionMethodsActive {
    # Install-time flags (CavemanInstalled, RtkInstalled, Context7McpRegistered,
    # ContextModeMcpRegistered) are "sticky true" by design (v4.3.0) - set once
    # and trusted forever, so a plugin that gets silently uninstalled, a hook
    # file deleted outside this script, or an MCP server that drops connection
    # would never be caught on a later launch. This runs once, right before
    # Claude launches, as a read-only status check (never re-installs, never
    # blocks launch) so the "Session tips" block reports what's ACTUALLY
    # active right now rather than what was true whenever it was installed.
    $lines = [System.Collections.Generic.List[string]]::new()

    if ($script:Config.CavemanInstalled -or $script:Config.ContextModeMcpRegistered) {
        # `claude plugin list` does NOT accept --scope (confirmed live on
        # Claude Code 2.1.224 - it errors "unknown option '--scope'"); scope
        # is already shown per-plugin in the plain listing.
        $pluginList = Invoke-ExternalCommand -Command "claude" -Arguments "plugin list" -TimeoutSeconds 15 -Silent -NoLog
        if ($script:Config.CavemanInstalled) {
            $active = [bool]($pluginList.Success -and $pluginList.Output -match "caveman")
            $lines.Add("caveman " + $(if ($active) { "[OK]" } else { "[MISSING]" }))
            if (-not $active) { Write-Warning "Caveman plugin was installed but `claude plugin list` no longer shows it active" }
        }
        if ($script:Config.ContextModeMcpRegistered) {
            $active = [bool]($pluginList.Success -and $pluginList.Output -match "context-mode")
            $lines.Add("context-mode " + $(if ($active) { "[OK]" } else { "[MISSING]" }))
            if (-not $active) { Write-Warning "Context Mode plugin was installed but `claude plugin list` no longer shows it active" }
        }
    }

    if ($script:Config.RtkInstalled) {
        $rtkHook = Join-Path $env:USERPROFILE ".claude\hooks\rtk-rewrite.sh"
        $active = Test-Path $rtkHook
        $lines.Add("rtk " + $(if ($active) { "[OK]" } else { "[MISSING]" }))
        if (-not $active) { Write-Warning "RTK was installed but its hook script is no longer at $rtkHook" }
    }

    if ($script:Config.Context7McpRegistered) {
        $mcpList = Invoke-ExternalCommand -Command "claude" -Arguments "mcp list" -TimeoutSeconds 15 -Silent -NoLog
        $active = [bool]($mcpList.Success -and $mcpList.Output -match "context7.*Connected")
        $lines.Add("context7 " + $(if ($active) { "[OK]" } else { "[MISSING]" }))
        if (-not $active) { Write-Warning "Context7 MCP was registered but `claude mcp list` doesn't show it connected" }
    }

    if ($lines.Count -gt 0) {
        Write-Hint ("Compression active: " + ($lines -join "  "))
    }
}

function Get-ClaudeConfigDir {
    # Honours CLAUDE_CONFIG_DIR when -IsolateClaudeConfig set it, so settings
    # land in the same place the launched `claude` process will read them.
    if ($env:CLAUDE_CONFIG_DIR) { return $env:CLAUDE_CONFIG_DIR }
    return (Join-Path $env:USERPROFILE ".claude")
}

function Initialize-IsolatedClaudeProfile {
    # -IsolateClaudeConfig only. Gives this project window its own
    # CLAUDE_CONFIG_DIR (separate settings, credentials, history, cache) so
    # concurrent windows can never write the same Claude Code state file at
    # the same time. Seeded once from your real ~/.claude so MCP servers and
    # personal settings carry over instead of starting from nothing.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ProjectDirectory)
    $slug = Get-PathSlug -Path $ProjectDirectory
    $profileDir = Join-Path $script:ProfileRoot $slug
    try {
        if (-not (Test-Path $profileDir)) {
            New-Item -ItemType Directory -Path $profileDir -Force | Out-Null
            $source = Join-Path $env:USERPROFILE ".claude"
            if (Test-Path $source) {
                foreach ($leaf in @("settings.json", "CLAUDE.md", "commands", "agents", "skills")) {
                    $src = Join-Path $source $leaf
                    if (Test-Path $src) {
                        Copy-Item -Path $src -Destination $profileDir -Recurse -Force -ErrorAction SilentlyContinue
                    }
                }
                Write-Log "Seeded isolated Claude profile from $source" -Level "DEBUG"
            }
            Write-Info "Created an isolated Claude config for this project"
        }
        $env:CLAUDE_CONFIG_DIR = $profileDir
        Write-Hint "CLAUDE_CONFIG_DIR = $profileDir"
        Write-Log "CLAUDE_CONFIG_DIR set to $profileDir"
    } catch {
        Write-Warning "Could not create an isolated Claude config - falling back to the shared one"
        Write-Log "Isolated profile setup failed: $_" -Level "WARN"
    }
}

# ============================================================================
# MASTER FOLDER + PROJECT SELECTION
#   v4 model: you choose ONE master folder (the parent directory your projects
#   live in). Its immediate subfolders are the projects. You pick which of
#   them to open, and each one gets its own window.
# ============================================================================

function Read-PathWithHistory {
    # Inline path editor: type a path, or arrow through previously used ones.
    # Returns $null if the user pressed Escape.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Label, [array]$History = @())
    Write-Hint "Up/Down cycle history   Del remove   Esc cancel"
    Write-Host ""
    $history = @($History | Where-Object { $_ })
    $index = $history.Count
    $currentInput = ""
    # A control key (Enter/Backspace/Escape/arrow/Delete) that the paste-drain
    # loop below pulled out of the input buffer but couldn't handle itself -
    # re-fed into the normal key-handling chain on the next iteration instead
    # of being silently dropped/typed as a literal character.
    $pendingKey = $null
    while ($true) {
        [Console]::CursorLeft = 0
        Write-Host (' ' * [Math]::Min((Get-SafeConsoleWidth) - 1, 120)) -NoNewline
        [Console]::CursorLeft = 0
        Write-Host "  $($Label): " -NoNewline -ForegroundColor White
        Write-Host $currentInput -NoNewline
        if ($pendingKey) {
            $key = $pendingKey
            $pendingKey = $null
        } else {
            if (-not [Console]::KeyAvailable) { Start-Sleep -Milliseconds 10; continue }
            $key = [Console]::ReadKey($true)
        }
        if ($key.Key -eq 'Enter') { Write-Host ""; break }
        elseif ($key.Key -eq 'UpArrow') { if ($history.Count -gt 0 -and $index -gt 0) { $index--; $currentInput = $history[$index] } }
        elseif ($key.Key -eq 'DownArrow') {
            if ($index -lt ($history.Count - 1)) { $index++; $currentInput = $history[$index] }
            else { $index = $history.Count; $currentInput = "" }
        }
        elseif ($key.Key -eq 'Backspace') { if ($currentInput.Length -gt 0) { $currentInput = $currentInput.Substring(0, $currentInput.Length - 1) } }
        elseif ($key.Key -eq 'Escape') { Write-Host ""; return $null }
        elseif ($key.Key -eq 'Delete') {
            if ($index -lt $history.Count -and $index -ge 0) {
                $removed = $history[$index]
                $history = @($history | Where-Object { $_ -ne $removed })
                Write-Host ""
                Write-Info "Removed from history: $removed"
                $index = [Math]::Min($index, $history.Count)
                $currentInput = if ($index -lt $history.Count) { $history[$index] } else { "" }
                # Persist the removal against whichever list this editor is on.
                $script:Config.MasterFolderHistory = @($script:Config.MasterFolderHistory | Where-Object { $_ -ne $removed })
                $script:Config.ProjectHistory = @($script:Config.ProjectHistory | Where-Object { $_ -ne $removed })
                Save-Configuration
                continue
            }
        }
        else {
            $currentInput += $key.KeyChar
            # Drain whatever's already buffered (keeps up with a paste)
            # without blocking - but a control key queued right behind a
            # paste (e.g. paste-then-Enter) must still be handled as that
            # key, not appended as a literal control character. Stash the
            # first one found and let the main loop process it next.
            while ([Console]::KeyAvailable) {
                $peeked = [Console]::ReadKey($true)
                if ($peeked.Key -in @('Enter', 'Backspace', 'Escape', 'UpArrow', 'DownArrow', 'Delete')) {
                    $pendingKey = $peeked
                    break
                }
                $currentInput += $peeked.KeyChar
            }
        }
    }
    $path = $currentInput.Trim().Trim('"').Trim()
    if ($path.EndsWith("\") -and $path -notmatch '^[A-Za-z]:\\$') { $path = $path.Substring(0, $path.Length - 1) }
    return $path
}

function Select-MasterFolderViaDialog {
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
        $dialog.Description = "Select the master folder that contains your projects"
        $dialog.ShowNewFolderButton = $false
        if ($dialog.ShowDialog() -eq "OK" -and $dialog.SelectedPath) { return $dialog.SelectedPath }
    } catch { Write-Log "Folder dialog unavailable: $_" -Level "DEBUG" }
    return $null
}

function Test-MasterFolder {
    # A master folder only needs to BE a writable directory. It does not need
    # to already contain project subfolders - a brand-new empty master folder
    # is valid too: the picker lets you create subfolders in it (n) or open
    # the master folder itself as a project (m).
    [CmdletBinding()] param([string]$Path)
    if (-not $Path) { Write-Fail "Input cannot be blank"; return $false }
    if (-not (Test-Path $Path -PathType Container)) { Write-Fail "Not a directory: $Path"; return $false }
    if ($Path -match '^[A-Za-z]:\\?$') { Write-Fail "Cannot use a drive root as the master folder"; return $false }
    try {
        $testFile = Join-Path $Path ".llmto_perm_test_$([guid]::NewGuid().ToString('N').Substring(0,8))"
        "test" | Out-File -FilePath $testFile -ErrorAction Stop -NoNewline
        Remove-Item $testFile -Force -ErrorAction Stop
    } catch {
        Write-Fail "Missing write permissions in: $Path"
        return $false
    }
    $subdirs = @(Get-ProjectCandidates -MasterPath $Path)
    $count = $subdirs.Count
    if ($count -eq 0) {
        Write-Success "Master folder: $Path (empty - no project subfolders yet)"
        Write-Hint "Use 'n' in the picker to create one, or 'm' to open this folder itself."
    } else {
        $plural = if ($count -eq 1) { "project" } else { "projects" }
        Write-Success "Master folder: $Path ($count $plural)"
    }
    return $true
}

function Read-MasterFolder {
    Write-Section "Master folder"
    Write-Hint "Pick the parent folder that holds your projects. Each subfolder in it"
    Write-Hint "can then be opened in its own window, running at the same time."
    Write-Host ""

    # -MasterFolder wins, then the saved one (confirmed, not silently reused),
    # then a fresh prompt.
    if ($MasterFolder) {
        if (Test-MasterFolder -Path $MasterFolder) { return $MasterFolder.TrimEnd('\') }
        Write-Warning "-MasterFolder is not usable - falling back to the prompt"
    }
    if ($script:Config.MasterFolder -and (Test-Path $script:Config.MasterFolder -PathType Container)) {
        Write-Info "Last used: $($script:Config.MasterFolder)"
        if (Read-YesNo "Use it again?" $true) {
            if (Test-MasterFolder -Path $script:Config.MasterFolder) { return $script:Config.MasterFolder }
        }
    }

    while ($true) {
        Write-Hint "Enter a path, press Enter on an empty line to browse, or Esc to quit."
        $path = Read-PathWithHistory -Label "Master folder" -History @($script:Config.MasterFolderHistory)
        if ($null -eq $path) {
            if (Read-YesNo "Exit launcher?" $false) { Write-Info "Exiting by request"; exit 0 }
            continue
        }
        if (-not $path) {
            $path = Select-MasterFolderViaDialog
            if (-not $path) { continue }
        }
        $path = $path.TrimEnd('\')
        if (Test-MasterFolder -Path $path) { return $path }
    }
}

function Save-MasterFolder {
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Path)
    $script:Config.MasterFolder = $Path
    $script:Config.MasterFolderHistory = Merge-ConfigurationLists -Ours @($Path) -Theirs @($script:Config.MasterFolderHistory)
    Save-Configuration
}

function Get-ProjectCandidates {
    # Immediate subdirectories of the master folder, minus the noise nobody
    # means to open as a project.
    [CmdletBinding()] param([Parameter(Mandatory)][string]$MasterPath)
    $excluded = @(
        'node_modules', '.git', '.svn', '.hg', '.venv', 'venv', 'env',
        '__pycache__', 'dist', 'build', 'out', 'target', '.idea', '.vscode',
        '.graphify', 'graphify-out', '.claude', 'bin', 'obj', '.next', '.cache'
    )
    try {
        return @(
            Get-ChildItem -Path $MasterPath -Directory -Force -ErrorAction SilentlyContinue |
                Where-Object {
                    $_.Name -notin $excluded -and
                    $_.Name -notlike '.*' -and
                    -not ($_.Attributes -band [System.IO.FileAttributes]::Hidden) -and
                    -not ($_.Attributes -band [System.IO.FileAttributes]::System)
                } |
                Sort-Object Name
        )
    } catch {
        Write-Log "Could not enumerate '$MasterPath': $_" -Level "WARN"
        return @()
    }
}

function Test-ProjectWindowOpen {
    # True when another LLM-TokenOptimizer window currently holds this
    # project's setup lock, so the picker can flag it as already open. v5.0:
    # this NO LONGER means opening it again is blocked - it only means the
    # new window will skip Graphify/settings.json setup (the other window
    # keeps it current) and launch its own independent Claude Code session
    # straight away. See Invoke-ProjectMode / Initialize-InstanceLock.
    [CmdletBinding()] param([Parameter(Mandatory)][string]$ProjectDirectory)
    try {
        $name = "Global\LLMTokenOptimizer_v4_Project_$(Get-PathSlug -Path $ProjectDirectory)"
        $existing = [System.Threading.Mutex]::OpenExisting($name)
        $existing.Dispose()
        return $true
    } catch { return $false }
}

function Show-ProjectMenu {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$MasterPath, [Parameter(Mandatory)][array]$Projects)
    Write-Section "Projects in $(Split-Path $MasterPath -Leaf)"
    Write-Hint $MasterPath
    Write-Host ""
    if ($Projects.Count -eq 0) {
        Write-Hint "(no project subfolders here yet)"
    } else {
        $i = 1
        foreach ($project in $Projects) {
            $isOpen = Test-ProjectWindowOpen -ProjectDirectory $project.FullName
            $known = (@($script:Config.ProjectHistory) -contains $project.FullName)
            $marker = if ($isOpen) { "open " } elseif ($known) { "seen " } else { "     " }
            $color = if ($isOpen) { [System.ConsoleColor]::DarkGray } else { [System.ConsoleColor]::Gray }
            Write-Host ("   {0,3}. " -f $i) -ForegroundColor DarkCyan -NoNewline
            Write-Host ("{0,-40}" -f $project.Name) -ForegroundColor $color -NoNewline
            Write-Host "  $marker" -ForegroundColor DarkYellow
            $i++
        }
    }
    Write-Host ""
    if ($Projects.Count -gt 0) {
        Write-Hint "1        open that project in its own window"
        Write-Hint "1,3,7    open several at once, one window each"
        Write-Hint "a        open all of them"
    }
    Write-Hint "n        create a new folder inside the master folder"
    Write-Hint "m        open the master folder itself as a single project"
    Write-Hint "r        refresh this list      c  change master folder      q  quit"
    Write-Hint "'open' = already running elsewhere too - picking it opens ANOTHER independent session (setup is skipped, Claude isn't)."
    Write-Hint "'seen' = opened before (offers to continue / pick / start fresh)."
}

function Select-Projects {
    # Parses the picker input into a list of full paths. Returns:
    #   array  -> open these
    #   'r'    -> refresh
    #   'c'    -> change master folder
    #   'n'    -> create a new project folder
    #   'q'    -> quit
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$MasterPath, [Parameter(Mandatory)][array]$Projects)
    $answer = (Read-Host "  Choose").Trim()
    if (-not $answer) { return 'r' }
    switch -Regex ($answer) {
        '^[Qq]$' { return 'q' }
        '^[Rr]$' { return 'r' }
        '^[Cc]$' { return 'c' }
        '^[Nn]$' { return 'n' }
        '^[Mm]$' { return ,@($MasterPath) }
        '^[Aa]$' {
            if ($Projects.Count -eq 0) { Write-Fail "No projects to open yet - use 'n' to create one"; return 'r' }
            return ,@($Projects | ForEach-Object { $_.FullName })
        }
    }
    if ($Projects.Count -eq 0) { Write-Fail "No numbered projects yet - use 'n' to create one, or 'm' to open the master folder"; return 'r' }
    $selected = [System.Collections.ArrayList]::new()
    $tokens = $answer -split '[,\s]+'
    foreach ($token in $tokens) {
        if ([string]::IsNullOrWhiteSpace($token)) { continue }
        if ($token -notmatch '^\d+$') { Write-Fail "Not a number: $token"; return 'r' }
        $index = [int]$token
        if ($index -lt 1 -or $index -gt $Projects.Count) { Write-Fail "Out of range: $index"; return 'r' }
        $path = $Projects[$index - 1].FullName
        if (-not ($selected -contains $path)) { $null = $selected.Add($path) }
    }
    if ($selected.Count -eq 0) { return 'r' }
    return ,@($selected)
}

function New-ProjectFolder {
    # Creates a new subfolder directly inside the master folder so it shows
    # up as a project candidate on the next refresh. Empty folders are valid
    # projects (Test-ProjectDirectory no longer rejects them), so the folder
    # can be opened immediately after creation.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$MasterPath)
    $name = (Read-Host "  New folder name").Trim()
    if (-not $name) { Write-Info "Cancelled"; return $null }

    $invalidChars = [System.IO.Path]::GetInvalidFileNameChars()
    if (@($name.ToCharArray() | Where-Object { $invalidChars -contains $_ }).Count -gt 0) {
        Write-Fail 'Name contains characters that are not allowed in a Windows folder name (e.g. \ / : * ? " < > |)'
        return $null
    }
    if ($name -in @('.', '..')) { Write-Fail "Not a valid folder name"; return $null }

    $newPath = Join-Path $MasterPath $name
    if (Test-Path $newPath) {
        Write-Warning "Already exists: $name"
        return $newPath
    }
    try {
        $null = New-Item -ItemType Directory -Path $newPath -Force -ErrorAction Stop
        Write-Success "Created: $newPath"
        return $newPath
    } catch {
        Write-Fail "Could not create folder: $_"
        return $null
    }
}

function Test-ProjectDirectory {
    # Empty folders are valid projects - a brand-new folder you just created
    # in the picker (or a fresh git clone target) has nothing in it yet, and
    # that's fine: Graphify extraction on an empty tree just yields an empty
    # graph, and Claude Code can start there.
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Path)
    if (-not $Path) { Write-Fail "Input cannot be blank"; return $false }
    if (-not (Test-Path $Path -PathType Container)) { Write-Fail "Not a directory: $Path"; return $false }
    if ($Path -match '^[A-Za-z]:\\$') { Write-Fail "Cannot process a drive root"; return $false }
    try {
        $testFile = Join-Path $Path ".graphify_perm_test_$([guid]::NewGuid().ToString('N').Substring(0,8))"
        "test" | Out-File -FilePath $testFile -ErrorAction Stop -NoNewline
        Remove-Item $testFile -Force -ErrorAction Stop
    } catch { Write-Fail "Missing write permissions"; return $false }
    if (-not (Get-ChildItem $Path -Force -ErrorAction SilentlyContinue)) {
        Write-Info "Directory is empty - opening as a new, empty project"
    } else {
        Write-Success "Validated: $(Split-Path $Path -Leaf)"
    }
    return $true
}

function Test-ProjectAlreadyKnown {
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Path)
    return (@($script:Config.ProjectHistory) -contains $Path)
}

function Add-ProjectToHistory {
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Path)
    $script:Config.ProjectHistory = Merge-ConfigurationLists -Ours @($Path) -Theirs @($script:Config.ProjectHistory)
    $script:Config.LastProject = $Path
    Save-Configuration
}

# ----------------------------------------------------------------------------
# WINDOW SPAWNING
#   Each project runs in a brand-new PowerShell console, re-invoking this same
#   script with -ProjectPath. Separate process, separate console, separate
#   Claude Code session - which is what lets several of them run at once.
# ----------------------------------------------------------------------------

function Start-ProjectWindow {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ProjectDirectory)

    if (-not $script:SelfPath -or -not (Test-Path $script:SelfPath -PathType Leaf)) {
        Write-Fail "Can't find this script's own path - unable to open a new window"
        Write-Hint "Run it from a file (not piped into powershell) so new windows can be spawned."
        return $false
    }
    # v5.0: no longer skipped when another window already has this project
    # open - Claude Code supports multiple concurrent sessions against the
    # same folder natively, and Invoke-ProjectMode's narrowed setup lock
    # makes it safe (the new window just skips setup instead of colliding on
    # .graphify/settings.json writes). Just let the user know what to expect.
    if (Test-ProjectWindowOpen -ProjectDirectory $ProjectDirectory) {
        Write-Info "$(Split-Path $ProjectDirectory -Leaf) is already open elsewhere - opening another independent session (setup will be skipped there)"
    }

    # Forward the flags that should apply to every window this launcher opens.
    $argList = [System.Collections.ArrayList]::new()
    $null = $argList.Add('-NoProfile')
    $null = $argList.Add('-ExecutionPolicy'); $null = $argList.Add('Bypass')
    $null = $argList.Add('-File');            $null = $argList.Add("`"$($script:SelfPath)`"")
    $null = $argList.Add('-ProjectPath');     $null = $argList.Add("`"$ProjectDirectory`"")
    $null = $argList.Add('-ChildWindow')
    if ($Model)               { $null = $argList.Add('-Model'); $null = $argList.Add($Model) }
    if ($VerboseMode)         { $null = $argList.Add('-VerboseMode') }
    if ($IsolateClaudeConfig) { $null = $argList.Add('-IsolateClaudeConfig') }

    try {
        $null = Start-Process -FilePath "powershell.exe" -ArgumentList $argList -WorkingDirectory $ProjectDirectory -ErrorAction Stop
        Write-Success "Opened window: $(Split-Path $ProjectDirectory -Leaf)"
        Write-Log "Spawned project window for $ProjectDirectory"
        return $true
    } catch {
        Write-Fail "Could not open a window for $(Split-Path $ProjectDirectory -Leaf)"
        Write-Log "Start-Process failed for ${ProjectDirectory}: $_" -Level "ERROR"
        return $false
    }
}

# ============================================================================
# GRAPHIFY OPERATIONS
#   NOTE: Graphify 0.17.1+ writes to a hidden .graphify\ directory (not
#   graphify-out\) and auto-generates the HTML studio during `extract` itself
#   - there is no separate `export html` step anymore.
#
#   Everything here is scoped to $PWD, which each project window has already
#   set to its own folder - so parallel windows never touch the same graph.
# ============================================================================

function Install-GraphifyPlatform {
    if (Test-Path $script:GlobalGateFile) { Write-Success "Platform registration cached"; return }
    Write-Info "Registering Graphify with the Claude platform..."
    $result = Invoke-ExternalCommand -Command "graphify" -Arguments "install --platform claude" -TimeoutSeconds 60
    if ($result.Success) { Set-Marker $script:GlobalGateFile; Write-Success "Platform registered" }
    else { Write-Warning "Platform registration may have failed"; Write-Log "Platform reg output: $($result.Output)" -Level "WARN" }
}

# ----------------------------------------------------------------------------
# AUDIT.md Finding 2: a mandatory "non-negotiable, graph before any raw read"
# rule plus a hard-blocking PreToolUse hook is a worse fit for a small project
# (a handful of files - the hook's own query round trip costs more than the
# Read/Grep it's replacing) than for genuine large-codebase exploration. This
# gates strict-mode enforcement behind a rough file-count threshold instead of
# applying it uniformly to every project this launcher touches.
# ----------------------------------------------------------------------------
$script:GRAPHIFY_STRICT_FILE_THRESHOLD = 150
# v5.3: line-count threshold for warning about a bloated CLAUDE.md. Anthropic's
# own guidance (code.claude.com/docs/en/best-practices): "Bloated CLAUDE.md
# files cause Claude to ignore your actual instructions" - important rules get
# lost in the noise. This is a warning, not an enforced limit; nothing here
# edits CLAUDE.md to shrink it.
$script:CLAUDE_MD_BLOAT_LINE_THRESHOLD = 300
function Test-ClaudeMdBloat {
    [CmdletBinding()] param()
    $claudeMdPath = Join-Path $PWD "CLAUDE.md"
    if (-not (Test-Path $claudeMdPath -PathType Leaf)) { return }
    try {
        $lines = (Get-Content -Path $claudeMdPath -ErrorAction Stop | Measure-Object -Line).Lines
        if ($lines -ge $script:CLAUDE_MD_BLOAT_LINE_THRESHOLD) {
            Write-Warning "CLAUDE.md is $lines lines (threshold $script:CLAUDE_MD_BLOAT_LINE_THRESHOLD) - a bloated CLAUDE.md causes Claude to ignore half of it (Anthropic's own guidance)."
            Write-Hint "Prune what Claude can already infer from the code. Move context that's only sometimes relevant into a skill instead - Claude loads those on demand, not every session."
        }
    } catch {
        Write-Log "CLAUDE.md bloat check failed: $_" -Level "DEBUG"
    }
}
function Test-ProjectExceedsGraphifyThreshold {
    $excludeDirs = @('node_modules', '.git', '.graphify', 'graphify-out', 'dist',
        'build', 'out', 'bin', 'obj', '__pycache__', '.venv', 'venv', '.next', 'target')
    $pattern = '[\\/](' + ($excludeDirs -join '|') + ')[\\/]'
    try {
        $count = (Get-ChildItem -Path $PWD -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch $pattern } |
            Measure-Object).Count
    } catch {
        Write-Log "Graphify threshold file count failed, defaulting to strict mode: $_" -Level "DEBUG"
        return $true
    }
    Write-Log "Graphify threshold check: $count files (threshold $script:GRAPHIFY_STRICT_FILE_THRESHOLD)" -Level "DEBUG"
    return $count -ge $script:GRAPHIFY_STRICT_FILE_THRESHOLD
}

function Install-GraphifyHook {
    $hookMarker = Join-Path $PWD ".graphify_hook_installed"
    if (Test-Path $hookMarker) { Write-Success "Hook already installed"; return }
    foreach ($attempt in 1..2) {
        if ($attempt -eq 1) { Write-Info "Installing Graphify hook..." }
        else { Write-Info "Retrying hook installation..."; Start-Sleep -Seconds 2 }
        $result = Invoke-ExternalCommand -Command "graphify" -Arguments "hook install" -TimeoutSeconds 30
        if ($result.Success) { Set-Marker $hookMarker; Write-Success "Hook installed"; return }
    }
    Write-Warning "Hook installation failed - continuing"
    Write-Log "Hook install failed after retries" -Level "WARN"
}

# ----------------------------------------------------------------------------
# Strict-mode enforcement: hard-blocks the first raw source read of a session
# and redirects it to the graph, then writes a mandatory `PreToolUse` hook into
# .claude\settings.json that intercepts file search (Glob/Grep) and bash
# commands so Claude can't bypass the graph by shelling out to `grep`/`find`.
# Runs every launch; each step is idempotent and marker-gated.
# ----------------------------------------------------------------------------
function Install-GraphifyStrictMode {
    $strictMarker = Join-Path $PWD ".graphify_strict_installed"
    if (-not (Test-Path $strictMarker)) {
        Write-Info "Installing Graphify strict mode (blocks raw source reads before the graph)..."
        $result = Invoke-ExternalCommand -Command "graphify" -Arguments "install --project --strict" -TimeoutSeconds 30
        if ($result.Success) {
            Set-Marker $strictMarker
            Write-Success "Strict mode installed"
        } else {
            Write-Warning "Strict mode install failed - continuing without the hard block"
            Write-Log "graphify install --project --strict failed: $($result.Output)" -Level "WARN"
        }
    } else {
        Write-Log "Strict mode already installed for this project" -Level "DEBUG"
    }

    # Keeps the block active for this process; strict installs alone are only
    # a marker file on disk, this env var is what Graphify's hook actually
    # checks at runtime before letting a raw read through.
    [Environment]::SetEnvironmentVariable("GRAPHIFY_HOOK_STRICT", "1", "Process")

    $claudeHookMarker = Join-Path $PWD ".graphify_claude_hook_installed"
    if (-not (Test-Path $claudeHookMarker)) {
        Write-Info "Wiring Graphify into Claude Code's PreToolUse hook..."
        $result = Invoke-ExternalCommand -Command "graphify" -Arguments "claude install" -TimeoutSeconds 30
        if ($result.Success) {
            Set-Marker $claudeHookMarker
            Write-Success "Claude Code hook installed (.claude\settings.json)"
        } else {
            Write-Warning "graphify claude install failed - PreToolUse hook not written"
            Write-Log "graphify claude install failed: $($result.Output)" -Level "WARN"
        }
    } else {
        Write-Log "Claude Code PreToolUse hook already installed" -Level "DEBUG"
    }
}

# ----------------------------------------------------------------------------
# Ensures every project this launcher touches has the graph-first directive in
# its CLAUDE.md, so strict mode is backed up by an explicit instruction even on
# a machine where the PreToolUse hook install failed.
# ----------------------------------------------------------------------------
function Set-ProjectClaudeMdDirective {
    # -UseGraphify controls whether a Graphify section is written AT ALL - see
    # AUDIT.md Finding 2 and Test-ProjectExceedsGraphifyThreshold above the
    # graphify install functions. v5.1: below the file-count threshold,
    # Graphify isn't installed/run for this project at all (see
    # Invoke-ProjectMode), so writing instructions about a tool that isn't
    # even present would just be clutter - the whole "# Graphify enforcement"
    # heading is omitted, not softened, when -UseGraphify isn't passed. Known
    # limitation: if a project later grows past the threshold, this does NOT
    # retroactively add the Graphify section to an already-written CLAUDE.md
    # (the marker-heading dedup below sees "no heading yet" and WILL add it
    # correctly on the next run once Invoke-ProjectMode itself notices the
    # project has grown - this function has no memory of a prior decision).
    [CmdletBinding()]
    param([switch]$UseGraphify)

    $claudeMdPath = Join-Path $PWD "CLAUDE.md"
    $markerHeading = "# Graphify enforcement"
    $companionMarkerHeading = "# Companion tooling"
    $graphifySection = if ($UseGraphify) {
        @"
CRITICAL: You must run ``graphify query`` or read ``graphify-out/GRAPH_REPORT.md`` (or ``.graphify/graph.json`` / ``.graphify/studio/studio.html`` on newer Graphify builds) before any raw file read, Glob, or Grep. This is non-negotiable.

$markerHeading

- Treat ``graphify`` as mandatory for understanding this codebase. ``grep``/``Grep`` and raw file reads are a fallback only, to be used after consulting the graph, never before it.
- Any subagent spawned inside this project must follow the same rule: query the graph first, fall back to grep only if the graph doesn't have the answer.
- At the start of a session: use ``graphify-out/GRAPH_REPORT.md`` (or the current project's ``.graphify/graph.json``) before searching files. Do not use raw grep first.
- Strict-mode enforcement is active for this project (``graphify install --project --strict``, ``GRAPHIFY_HOOK_STRICT=1``, and a ``PreToolUse`` hook installed via ``graphify claude install`` in ``.claude/settings.json``). The first raw source read of a session is hard-blocked and redirected to the graph; file search and bash commands are intercepted by the hook.
"@
    } else {
        $null
    }
    $companionSection = @"
$companionMarkerHeading

The following are installed once at user scope (``~/.claude/``) and are active in every session in this project, not just this one. They don't overlap or need to be invoked manually - each reacts to its own lifecycle hook or slash command:

- **claude-mem** - captures what happens in this session (files read/edited, decisions made) and injects relevant memories back in at the start of future sessions. Nothing to do here; it runs on Claude Code's own SessionStart/PostToolUse/Stop hooks.
- **headroom** - a live context-window usage bar in the statusline, reading the actual session JSONL rather than estimating.
- **Session hygiene** (headroom-driven): when the statusline bar gets to roughly 70-80% used, or the conversation has drifted onto a second, unrelated task, run ``/compact`` to summarize and free space - don't wait until it's nearly full, compaction is lossy and works best at a natural checkpoint, not mid-task. If the next thing you want to do is genuinely unrelated to what's already in context, prefer starting a NEW session over compacting an unrelated history into it - v5.0+ of this launcher allows multiple concurrent sessions against the same project folder for exactly this: open another window/session rather than dragging old, irrelevant context along. Use Claude Code's own ``--resume`` picker (offered on a returning project) to come back to a specific old session by name instead of losing it. Within one session, ``/clear`` resets context between unrelated tasks without a new window; if you've corrected the same mistake twice, ``/clear`` and rewrite the prompt rather than layering a third correction on a polluted context.
- **claude-code-setup** - read-only; if asked to recommend MCP servers, hooks, skills, or subagents for this project, this is the mechanism, invoked via its own skill.
- **task-observer** - a skill for spotting when an existing skill in this project is out of date or missing something, based on how it's actually being used.
- **claude-md-management** - this file. Run ``/revise-claude-md`` (or press ``#`` mid-session) to capture a learning - a discovered build flag, a naming convention you were corrected on - directly into this file instead of losing it at session end. Keep additions concise and merged into the relevant existing section rather than appended as a new one where one already fits.
- **context7** (MCP) - version-specific library/API docs on demand. Prefer it over guessing from training data or grepping through node_modules/site-packages when you need to know how a specific dependency version actually behaves - ask for it by name, e.g. "use context7 to check react-router v7's data loading API."
- **Prompt cache**: Claude Code caches its own system prompt, tool definitions, and this file automatically - no setup needed, and nothing to add on top of it. It IS fragile mid-session, though: switching models, or a plugin/MCP change that needs ``/reload-plugins``, invalidates the cache and the next turn re-reads the whole conversation at full price. Avoid both unless actually necessary. Delegate large, noisy reads (broad exploration, verbose command output you only need the conclusion of) to a subagent so that bulk never enters this session's own cached context at all.
"@
    $directiveBlock = if ($graphifySection) { "$graphifySection`n`n$companionSection" } else { $companionSection }

    try {
        if (-not (Test-Path $claudeMdPath -PathType Leaf)) {
            $directiveBlock | Out-File -FilePath $claudeMdPath -Encoding UTF8 -Force
            $createdWhat = if ($UseGraphify) { "Graphify + companion-tooling" } else { "companion-tooling (Graphify skipped - below the size threshold)" }
            Write-Success "Created CLAUDE.md with the $createdWhat directives"
            Write-Log "CLAUDE.md created at $claudeMdPath" -Level "DEBUG"
            return
        }

        $existing = Get-Content -Path $claudeMdPath -Raw -Encoding UTF8
        $hasGraphify = $existing -match [regex]::Escape($markerHeading)
        $hasCompanion = $existing -match [regex]::Escape($companionMarkerHeading)
        # "Graphify section wanted" is satisfied by either actually having it,
        # OR by this project deliberately not using Graphify at all ($graphifySection
        # is $null below threshold) - otherwise a small project would fail this
        # check every single launch (its CLAUDE.md will never gain a heading
        # nobody is trying to add) and re-append the companion section on top
        # of itself forever.
        $graphifySatisfied = $hasGraphify -or (-not $graphifySection)
        if ($graphifySatisfied -and $hasCompanion) {
            Write-Log "CLAUDE.md already has what this project needs - leaving as-is" -Level "DEBUG"
            return
        }

        # Each half of $directiveBlock is added independently so re-running
        # this on a CLAUDE.md that already has one section (e.g. an older
        # project that only ever got the Graphify half) only appends what's
        # missing instead of duplicating anything.
        $toAppend = if (-not $graphifySatisfied -and -not $hasCompanion) {
            $directiveBlock
        } elseif (-not $hasCompanion) {
            $directiveBlock.Substring($directiveBlock.IndexOf($companionMarkerHeading))
        } elseif ($graphifySection) {
            $directiveBlock.Substring(0, $directiveBlock.IndexOf($companionMarkerHeading)).TrimEnd()
        } else {
            # Companion section exists, Graphify section isn't wanted here -
            # nothing left to add.
            $null
        }

        if (-not $toAppend) {
            Write-Log "CLAUDE.md already has what this project needs - leaving as-is" -Level "DEBUG"
            return
        }

        $merged = $existing.TrimEnd() + "`r`n`r`n" + $toAppend
        $merged | Out-File -FilePath $claudeMdPath -Encoding UTF8 -Force
        Write-Success "Added the missing directive section(s) to existing CLAUDE.md"
        Write-Log "CLAUDE.md merged at $claudeMdPath (graphify existing=$hasGraphify, wanted=$([bool]$graphifySection), companion existing=$hasCompanion)" -Level "DEBUG"
    } catch {
        Write-Warning "Could not write/merge CLAUDE.md - continuing without it"
        Write-Log "CLAUDE.md write failed: $_" -Level "WARN"
    }
}

function Invoke-GraphifyExtract {
    # NOTE: every return path below is $true - graph extraction is treated as
    # a best-effort step, never a reason to stop the launch (see the comments
    # at each failure branch). The return value is kept boolean for callers
    # that want to log/branch on it, but don't add a "did this fail" check at
    # the call site expecting it to ever be $false; it can't be.
    Write-Section "Graph extraction"
    $graphFile = Join-Path (Join-Path $PWD ".graphify") "graph.json"
    # Graphify already tracks what it's seen. On a first run in this project it
    # does a full scan (`graphify .`); once a graph exists, `graphify update`
    # only re-parses files that changed since the last run.
    $isUpdate = Test-Path $graphFile -PathType Leaf
    $extractArgs = if ($isUpdate) { "update" } else { "." }
    $verb = if ($isUpdate) { "Updating changed files in" } else { "Extracting" }
    Write-Info "$verb project structure (also builds the HTML studio)..."
    Write-Log "Starting graph $extractArgs in: $($PWD.Path)"
    $extractStart = Get-Date
    $result = Invoke-ExternalCommand -Command "graphify" -Arguments $extractArgs -TimeoutSeconds 300 -ShowSpinner -SpinnerLabel "Scanning project graph"
    $extractTime = (Get-Date) - $extractStart

    # Newer Graphify builds refuse to run on a mixed repo (code + docs/PDFs/
    # images) unless you either point it at an LLM backend for semantic
    # extraction or tell it to skip the non-code files entirely. The exact skip
    # flag isn't consistent across versions, so read graphify's own --help
    # output and use whatever it actually advertises.
    if ((-not $result.Success) -and ($result.Output -match "non-code corpus files|--semantic|--backend")) {
        Write-Log "graphify $extractArgs hit the semantic-extraction gate: $(Get-Truncated $result.Output 200)" -Level "DEBUG"
        $skipFlag = Find-GraphifySkipSemanticFlag
        if ($skipFlag) {
            Write-Hint "Project has non-code files (docs/PDFs/images) - retrying with $skipFlag"
            $codeOnlyArgs = "$extractArgs $skipFlag"
            $result = Invoke-ExternalCommand -Command "graphify" -Arguments $codeOnlyArgs -TimeoutSeconds 300 -ShowSpinner -SpinnerLabel "Scanning project graph (code-only)"
            $extractTime = (Get-Date) - $extractStart
            if ($result.Success) { $extractArgs = $codeOnlyArgs }
        } else {
            Write-Log "No code-only/skip-semantic flag found in 'graphify --help' output" -Level "DEBUG"
        }
    }

    if (-not $result.Success) {
        if ($isUpdate) {
            # Older Graphify builds may not support `update` - fall back to a
            # full rescan rather than failing outright.
            Write-Log "graphify update failed, falling back to full scan: $(Get-Truncated $result.Output 200)" -Level "DEBUG"
            $result = Invoke-ExternalCommand -Command "graphify" -Arguments "." -TimeoutSeconds 300 -ShowSpinner -SpinnerLabel "Scanning project graph"
            $extractTime = (Get-Date) - $extractStart
            if ((-not $result.Success) -and ($result.Output -match "non-code corpus files|--semantic|--backend")) {
                $skipFlag = Find-GraphifySkipSemanticFlag
                if ($skipFlag) {
                    Write-Log "graphify . also hit the semantic-extraction gate, retrying with $skipFlag" -Level "DEBUG"
                    $result = Invoke-ExternalCommand -Command "graphify" -Arguments ". $skipFlag" -TimeoutSeconds 300 -ShowSpinner -SpinnerLabel "Scanning project graph (code-only)"
                    $extractTime = (Get-Date) - $extractStart
                }
            }
        }
        if (-not $result.Success) {
            Write-Fail "Graph extraction failed"
            foreach ($line in ($result.Output -split "`r?`n" | Select-Object -First 10)) { Write-Hint $line }
            Write-Warning "Continuing without a graph - Claude Code will still launch normally"
            return $true
        }
    }
    if (-not (Test-Path $graphFile -PathType Leaf)) {
        Write-Fail "Graph file missing: .graphify\graph.json"
        foreach ($line in ($result.Output -split "`r?`n" | Select-Object -First 10)) { Write-Hint $line }
        Write-Warning "Continuing without a graph - Claude Code will still launch normally"
        return $true
    }
    $stats = Get-GraphStatistics -GraphPath $graphFile
    Write-Success "Extracted in $($extractTime.ToString('mm\:ss'))"
    Write-Hint "Nodes $($stats.Nodes)   Edges $($stats.Edges)   Size $($stats.Size)"
    Write-Log "Extraction complete: $($stats.Nodes) nodes, $($stats.Edges) edges"
    return $true
}

# ----------------------------------------------------------------------------
# Graphify's exact flag for "index code, skip docs/PDFs/images that need
# semantic extraction" isn't consistent across versions. Read graphify's own
# --help text and pick whatever it advertises. Cached per-process.
# ----------------------------------------------------------------------------
$script:GraphifySkipFlagChecked = $false
$script:GraphifySkipFlagCached = $null
function Find-GraphifySkipSemanticFlag {
    if ($script:GraphifySkipFlagChecked) { return $script:GraphifySkipFlagCached }
    $script:GraphifySkipFlagChecked = $true
    try {
        $helpResult = Invoke-ExternalCommand -Command "graphify" -Arguments "--help" -TimeoutSeconds 15 -NoLog
        $helpText = $helpResult.Output
        if (-not $helpText) { return $null }
        $candidates = @(
            '--code-only', '--skip-semantic', '--no-semantic',
            '--ast-only', '--code-mode', '--skip-docs'
        )
        foreach ($candidate in $candidates) {
            if ($helpText -match [regex]::Escape($candidate)) {
                $script:GraphifySkipFlagCached = $candidate
                return $candidate
            }
        }
        $match = [regex]::Match($helpText, '--[a-z][a-z0-9-]*(code[a-z0-9-]*only|skip[a-z0-9-]*semantic|only[a-z0-9-]*code)[a-z0-9-]*')
        if ($match.Success) {
            $script:GraphifySkipFlagCached = $match.Value
            return $match.Value
        }
    } catch {
        Write-Log "Find-GraphifySkipSemanticFlag failed: $_" -Level "DEBUG"
    }
    return $null
}

function Get-GraphStatistics {
    [CmdletBinding()] param([string]$GraphPath)
    $stats = @{ Nodes = 0; Edges = 0; Size = "0 B" }
    try {
        $graph = Get-Content $GraphPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
        $graphProps = $graph.PSObject.Properties.Name
        if (($graphProps -contains "nodes") -and $graph.nodes) { $stats.Nodes = @($graph.nodes).Count }
        # Graphify uses the networkx node-link schema, where edges live under
        # "links". Fall back to "edges" for other/older formats.
        if (($graphProps -contains "links") -and $graph.links) { $stats.Edges = @($graph.links).Count }
        elseif (($graphProps -contains "edges") -and $graph.edges) { $stats.Edges = @($graph.edges).Count }
    } catch { Write-Log "Could not parse graph stats: $_" -Level "DEBUG" }
    try {
        $bytes = (Get-Item $GraphPath -ErrorAction Stop).Length
        if ($bytes -gt 1MB) { $stats.Size = "$([math]::Round($bytes / 1MB, 1)) MB" }
        elseif ($bytes -gt 1KB) { $stats.Size = "$([math]::Round($bytes / 1KB, 1)) KB" }
        else { $stats.Size = "$bytes B" }
    } catch { Write-Log "Could not get graph file size" -Level "DEBUG" }
    return $stats
}

function Show-GraphResult {
    Write-Section "Graph ready"
    $studioFile = Join-Path (Join-Path $PWD ".graphify") "studio\studio.html"
    if (-not (Test-Path $studioFile -PathType Leaf)) {
        Write-Warning "Studio HTML not found at .graphify\studio\studio.html - skipping preview"
        return
    }
    Write-Success "Interactive map generated"
    Write-Hint ("file:///" + $studioFile.Replace('\', '/'))
    # Never block a spawned project window on a prompt nobody may be watching
    # - the multi-window picker can open several at once. Same guard pattern
    # used throughout Find-ClaudeExecutable and elsewhere.
    if ($script:IsChild) { return }
    if (Read-YesNo "Open the graph now?" $false) { Start-Process $studioFile -ErrorAction Stop }
}

# ============================================================================
# AUTOSKILLS
#   npx autoskills detects the project's tech stack and installs matching
#   Claude Code skills from the skills.sh registry. Idempotent; `-y` on both
#   npx and autoskills skips every interactive prompt.
# ============================================================================

function Install-AutoSkillsCli {
    if (-not (Test-CommandAvailable "npm" -UseCache)) { return $false }
    if (Test-CommandAvailable "autoskills" -UseCache) { return $true }
    Write-Info "Installing autoskills globally (npm install -g autoskills)..."
    $result = Invoke-ExternalCommand -Command "npm" -Arguments "install -g autoskills" -TimeoutSeconds 120 -ShowSpinner -SpinnerLabel "Installing autoskills"
    if ($result.Success) {
        Sync-ProcessPathFromRegistry
        if (Test-CommandAvailable "autoskills") { Write-Success "autoskills installed"; return $true }
    }
    # Not fatal - `npx autoskills` below will fetch it on demand anyway.
    Write-Log "Global autoskills install did not confirm success: $(Get-Truncated $result.Output 200)" -Level "DEBUG"
    return $false
}

function Invoke-AutoSkills {
    Write-Section "AutoSkills"
    if (-not (Test-CommandAvailable "npm" -UseCache)) {
        Write-Info "npm not available - skipping autoskills"
        return
    }
    $null = Install-AutoSkillsCli
    Write-Info "Detecting stack and installing matching AI skills..."
    $result = Invoke-ExternalCommand -Command "npx" -Arguments "-y autoskills -y -a claude-code" -TimeoutSeconds 120 -ShowSpinner -SpinnerLabel "Running autoskills"
    if ($result.Success) {
        Write-Success "autoskills complete"
    } else {
        Write-Warning "autoskills did not complete cleanly"
        Write-Log "autoskills output: $(Get-Truncated $result.Output 300)" -Level "WARN"
    }
}

# ============================================================================
# SESSION BACKEND FALLBACK CHAIN (v5.8)
#   Claude Code (primary) -> Antigravity IDE -> local LM Studio model, in that
#   order. Checked once per launch - NOT a live mid-session hot-swap. v5.5
#   deliberately removed OmniRoute (a gateway that rerouted Claude Code's own
#   traffic through alternate providers/models) for the complexity/risk that
#   added; doing a live swap here would reintroduce exactly that. Resolve-
#   SessionBackend runs right before the launch call site in Invoke-
#   ProjectMode and decides which of the three to actually start.
# ============================================================================

function Test-ClaudeAvailable {
    # Two independent checks: is the CLI itself reachable at all (covers "not
    # installed", "broken install", "can't reach Anthropic to authenticate"),
    # and did the LAST session on this machine end because Claude Code's own
    # usage limit was hit (persisted from RateLimitWatcher's static fields by
    # Start-ClaudeSession below). The second is what actually answers "is
    # main Claude up" in the sense this fallback chain cares about - a fast
    # `claude --version` alone reports "available" seconds after a rate-limit
    # message, since the binary itself is fine; the account just isn't.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ClaudePath)

    if ($script:Config.ClaudeRateLimitedUntilUtc) {
        try {
            $until = [DateTime]::Parse($script:Config.ClaudeRateLimitedUntilUtc, $null, [System.Globalization.DateTimeStyles]::RoundtripKind)
            if ((Get-Date).ToUniversalTime() -lt $until) {
                Write-Log "Claude Code was rate-limited until $until (UTC) - treating as unavailable" -Level "INFO"
                return $false
            }
        } catch { Write-Log "Could not parse ClaudeRateLimitedUntilUtc - ignoring: $_" -Level "DEBUG" }
    }

    if ($ClaudePath -eq "node" -and $script:ClaudeJsPath) { return (Test-Path $script:ClaudeJsPath -PathType Leaf) }
    if (-not (Test-Path $ClaudePath -PathType Leaf) -and -not (Test-CommandAvailable $ClaudePath -UseCache)) { return $false }
    $result = Invoke-ExternalCommand -Command $ClaudePath -Arguments "--version" -TimeoutSeconds 10 -Silent -NoLog
    return $result.Success
}

function Find-AntigravityExecutable {
    # Google Antigravity IDE - confirmed install locations (Aug 2026):
    # per-user %LOCALAPPDATA%\Programs\Antigravity IDE\ (current) or
    # %LOCALAPPDATA%\Programs\Antigravity\ (pre-rename), machine-wide
    # C:\Program Files\Google\Antigravity\, plus the separate `agy` terminal
    # CLI at %LOCALAPPDATA%\agy\bin\agy.exe. Any one being present counts as
    # "Antigravity available" for this fallback chain.
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Antigravity IDE\Antigravity IDE.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Antigravity\Antigravity.exe"),
        "$env:ProgramFiles\Google\Antigravity\Antigravity IDE.exe",
        "$env:ProgramFiles\Google\Antigravity\Antigravity.exe",
        (Join-Path $env:LOCALAPPDATA "agy\bin\agy.exe")
    )
    foreach ($c in $candidates) {
        if (Test-Path $c -PathType Leaf) { return $c }
    }
    if (Test-CommandAvailable "agy" -UseCache) { return (Get-Command "agy" -ErrorAction SilentlyContinue).Source }
    return $null
}

function Test-AntigravityAvailable {
    return [bool](Find-AntigravityExecutable)
}

function Start-AntigravitySession {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ProjectDirectory)
    $exe = Find-AntigravityExecutable
    if (-not $exe) { return $false }
    Write-Info "Opening Antigravity for this project (Claude unavailable): $exe"
    try {
        if ($exe -like "*agy.exe") {
            # Terminal CLI - runs attached to this console, matching Start-
            # ClaudeSession's own synchronous/blocking launch shape.
            & $exe $ProjectDirectory
        } else {
            # Desktop IDE - same "open a folder" convention as VS Code/Cursor.
            Start-Process -FilePath $exe -ArgumentList "`"$ProjectDirectory`"" -ErrorAction Stop
        }
        Write-Success "Antigravity launched for $(Split-Path $ProjectDirectory -Leaf)"
        return $true
    } catch {
        Write-Warning "Could not launch Antigravity: $_"
        return $false
    }
}

function Update-BestLocalModelFromBenchmarks {
    # Reads benchmark_summary.json (written by run_benchmarks.py, see there)
    # from this launcher script's own directory and records whichever model
    # scored highest on composite_score - a SWE-bench-style capability score
    # (does the generated code actually parse and cover the prompt's stated
    # requirements) weighted 70/30 against raw speed, NOT pure tokens/second.
    # A fast model that writes broken code is a bad coding agent regardless
    # of how many tok/s it produces; see score_capability() and
    # compute_composite_score() in run_benchmarks.py for the actual scoring.
    # Falls back to avg_tokens_per_second alone only against an OLDER
    # benchmark_summary.json that predates composite_score (re-run
    # `uv run run_benchmarks.py --rescore` to backfill it without re-running
    # any model calls).
    [CmdletBinding()]
    param()
    $summaryPath = Join-Path (Split-Path $script:SelfPath -Parent) "benchmark_summary.json"
    if (-not (Test-Path $summaryPath -PathType Leaf)) { return $false }
    try {
        $rows = Get-Content $summaryPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
    } catch {
        Write-Log "Could not parse benchmark_summary.json: $_" -Level "WARN"
        return $false
    }
    $candidates = @($rows | Where-Object { $_.stage -eq "benchmark" -and $_.status -in @("ok", "partial") -and $_.avg_tokens_per_second })
    if ($candidates.Count -eq 0) {
        Write-Log "benchmark_summary.json has no successful benchmark rows" -Level "DEBUG"
        return $false
    }
    $hasCompositeScore = [bool]($candidates[0].PSObject.Properties.Name -contains "composite_score")
    $best = if ($hasCompositeScore) {
        $candidates | Sort-Object -Property composite_score -Descending | Select-Object -First 1
    } else {
        Write-Log "benchmark_summary.json predates composite_score - ranking by speed only (run --rescore to fix)" -Level "WARN"
        $candidates | Sort-Object -Property avg_tokens_per_second -Descending | Select-Object -First 1
    }
    $script:Config.BestLocalModelId = $best.model
    $script:Config.BestLocalModelTokensPerSec = $best.avg_tokens_per_second
    $script:Config.BestLocalModelUpdatedUtc = (Get-Date).ToUniversalTime().ToString("o")
    Save-Configuration
    if ($hasCompositeScore) {
        Write-Success "Best local coding agent per benchmark: $($best.model) (composite=$($best.composite_score), resolve_rate=$($best.resolve_rate), $($best.avg_tokens_per_second) tok/s)"
    } else {
        Write-Success "Best local model per benchmark: $($best.model) ($($best.avg_tokens_per_second) tok/s)"
    }
    return $true
}

function Test-LocalModelAvailable {
    # LM Studio support + a benchmark result to act on. Doesn't require the
    # server to be running right now - Start-LocalModelSession starts it.
    if (-not $script:Config.LMStudioSupportInstalled) { return $false }
    if (-not $script:Config.BestLocalModelId) {
        Update-BestLocalModelFromBenchmarks | Out-Null
    }
    return [bool]$script:Config.BestLocalModelId
}

function Start-LocalModelSession {
    # Last resort in the fallback chain: an interactive chat with the best
    # locally-benchmarked model via LM Studio's own `lms chat` CLI. Deliberately
    # NOT an attempt to make Claude Code itself talk to a local model through a
    # hand-rolled Anthropic<->OpenAI translation proxy - v5.5 removed OmniRoute
    # (which did exactly that kind of traffic rerouting) for the complexity/risk
    # it added; `lms chat` is a real, already-built surface for exactly this.
    [CmdletBinding()]
    param()
    $modelId = $script:Config.BestLocalModelId
    if (-not $modelId) { Write-Warning "No benchmarked local model on record"; return $false }

    $lmsPath = if (Test-CommandAvailable "lms" -UseCache) { "lms" } else {
        $fallback = Join-Path $env:USERPROFILE ".lmstudio\bin\lms.exe"
        if (Test-Path $fallback) { $fallback } else { $null }
    }
    if (-not $lmsPath) { Write-Warning "lms CLI not found - can't start a local session"; return $false }

    Write-Section "Local model session (Claude and Antigravity both unavailable)"
    Write-Info "Using $modelId - fastest in the last benchmark run ($($script:Config.BestLocalModelTokensPerSec) tok/s)"
    $null = Invoke-ExternalCommand -Command $lmsPath -Arguments "load `"$modelId`" --gpu max -y" -TimeoutSeconds 600 -ShowSpinner -SpinnerLabel "Loading $modelId"
    Write-Info "Starting interactive chat - type your request, Ctrl+C to exit."
    & $lmsPath chat $modelId
    $null = Invoke-ExternalCommand -Command $lmsPath -Arguments "unload --all" -TimeoutSeconds 30 -Silent
    return $true
}

function Resolve-SessionBackend {
    # Returns 'claude' | 'antigravity' | 'local' | 'none'. Pure decision
    # logic, no side effects beyond logging - callers do the actual launch.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ClaudePath)

    if (Test-ClaudeAvailable -ClaudePath $ClaudePath) { return 'claude' }
    Write-Warning "Claude Code is not available right now."
    if (Test-AntigravityAvailable) {
        Write-Info "Falling back to Antigravity."
        return 'antigravity'
    }
    Write-Info "Antigravity not found - checking for a local model fallback..."
    if (Test-LocalModelAvailable) {
        Write-Info "Falling back to the local model with the best benchmark result."
        return 'local'
    }
    Write-Warning "No fallback available - Antigravity isn't installed and no local model has been benchmarked yet."
    Write-Hint "Run 'uv run run_benchmarks.py' in this project's folder to enable the local fallback."
    return 'none'
}

# ============================================================================
# CLAUDE LAUNCH
# ============================================================================

function Start-ClaudeSession {
    # -ResumeMode: "Continue" (--continue, most recent conversation in this
    # folder), "Pick" (--resume, Claude Code's own interactive picker over
    # every past session in this folder - no session bookkeeping needed on
    # our side), or "New" (no flag, brand new conversation). Multiple "New"
    # or "Pick" sessions can now run concurrently against the same project
    # folder - see Invoke-ProjectMode's narrowed instance lock (v5.0).
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ClaudePath,
        [ValidateSet('Continue', 'Pick', 'New')]
        [string]$ResumeMode = "New"
    )
    Write-Section "Launch Claude"
    # -Model sonnet|opus: session-only override so this launch doesn't fall
    # back onto whatever Claude Code last saved as its default. Doesn't touch
    # the saved default and doesn't persist to next launch.
    $script:ForcedModelAlias = if ($Model) {
        Write-Info "Forcing this session onto $Model (via -Model flag)"
        $Model
    } else { $null }

    $claudeArgs = @()
    switch ($ResumeMode) {
        'Continue' {
            $claudeArgs += "--continue"
            Write-Info "Same workspace as before - resuming the most recent session"
        }
        'Pick' {
            $claudeArgs += "--resume"
            Write-Info "Opening Claude Code's own session picker for this project"
        }
        default {
            Write-Info "Starting a new session"
        }
    }
    if ($script:ForcedModelAlias) { $claudeArgs += @("--model", $script:ForcedModelAlias) }
    $isResumeAttempt = ($ResumeMode -eq 'Continue')

    # Best-effort quota-exhaustion watcher: reads the visible console for
    # Claude Code's own rate-limit text and drives its "Stop and wait" flow
    # (or falls back to a timed wait) so a session that hits its usage limit
    # resumes on its own instead of just dying. Never blocks the launch if
    # unavailable. See Start-RateLimitWatcher.
    Start-RateLimitWatcher
    try {
        # If we're using node + a script path, adjust the command
        if ($ClaudePath -eq "node" -and $script:ClaudeJsPath) {
            Write-Log "Launching Claude via node $($script:ClaudeJsPath) $($claudeArgs -join ' ')"
            try {
                & node $script:ClaudeJsPath @claudeArgs
                # Same recovery as the native-binary branch below: an empty
                # workspace has no prior conversation for --continue to resume,
                # and without this the Node fallback path would just dead-end
                # instead of falling back to a new session like the primary path.
                if ($isResumeAttempt -and $LASTEXITCODE -ne 0) {
                    Write-Warning "No previous conversation found to continue - starting a new session instead"
                    Write-Log "Claude --continue failed (exit $LASTEXITCODE) - retrying without --continue" -Level "WARN"
                    if ($script:ForcedModelAlias) { & node $script:ClaudeJsPath --model $script:ForcedModelAlias } else { & node $script:ClaudeJsPath }
                }
            } catch {
                Write-Warning "Claude exited with error: $_"
            }
        } else {
            Write-Log "Launching Claude: $ClaudePath $($claudeArgs -join ' ') in $($PWD.Path)"
            try {
                if ($claudeArgs.Count -gt 0) { & $ClaudePath @claudeArgs } else { & $ClaudePath }
                if ($isResumeAttempt -and $LASTEXITCODE -ne 0) {
                    Write-Warning "No previous conversation found to continue - starting a new session instead"
                    Write-Log "Claude --continue failed (exit $LASTEXITCODE) - retrying without --continue" -Level "WARN"
                    if ($script:ForcedModelAlias) { & $ClaudePath --model $script:ForcedModelAlias } else { & $ClaudePath }
                }
            } catch {
                Write-Warning "Claude exited with error: $_"
                Write-Log "Claude exit error: $_" -Level "ERROR"
            }
        }
    } finally {
        Stop-RateLimitWatcher
        # Persist what the watcher saw (if anything) so the NEXT launch's
        # Test-ClaudeAvailable can skip straight to the Antigravity/local
        # fallback instead of re-launching Claude only to hit the same limit
        # again seconds later. Safe to read after Stop(): same process/
        # AppDomain, and the watcher's background thread has already joined.
        if ($script:RateLimitWatcherTypeLoaded -and [LLMTokenOptimizer.RateLimitWatcher]::RateLimitDetected) {
            $resumeIso = [LLMTokenOptimizer.RateLimitWatcher]::ResumeAtUtcIso
            if ($resumeIso) {
                $script:Config.ClaudeRateLimitedUntilUtc = $resumeIso
                Save-Configuration
                Write-Log "Recorded Claude rate-limit until $resumeIso for next launch's fallback check" -Level "INFO"
            }
        }
    }

    Write-Success "Claude session ended"
}

function Show-SessionSummary {
    [CmdletBinding()]
    param(
        [string]$ProjectPath,
        [bool]$Resumed
    )
    Write-Section "Session summary"
    Write-Hint ("Project     " + (Split-Path $ProjectPath -Leaf))
    Write-Hint ("Session     " + $(if ($Resumed) { "resumed" } else { "new" }))
    Write-Hint ("Elapsed     " + (Get-Elapsed))
}

# ============================================================================
# PROJECT MODE - one spawned window, one project folder
#   Skips the machine-wide bootstrap the launcher window already did (winget
#   installs, update prompts) and gets straight to this project's graph and
#   its own Claude session.
# ============================================================================

function Invoke-ProjectMode {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    $projectName = Split-Path $Path -Leaf
    $host.UI.RawUI.WindowTitle = "LLM-TokenOptimizer - $projectName"
    Write-Title -Subtitle "Project window: $projectName"

    # Validate the project folder itself FIRST, before any machine-level
    # setup work runs - an unusable path (missing, permission-denied, a
    # drive root) should fail immediately, not after installing Graphify.
    if (-not (Test-ProjectDirectory -Path $Path)) {
        # Code 106 (not 102): 102 is reserved exclusively for a missing
        # required dependency (see Test-RequiredDependencies) - a bad project
        # folder is an unrelated failure and deserves its own code rather than
        # overloading that one. 106 was freed by the v4.3.0 cleanup that
        # removed the unreachable Invoke-GraphifyExtract failure branch.
        Stop-Script -Code 106 -Reason "Project folder is not usable: $Path"
    }

    # v5.0: the per-project lock now only guards the setup phase (Graphify
    # extraction + CLAUDE.md directive + project .claude\settings.json
    # writes) - the actual file-write races the lock exists to prevent. It is
    # released (see below) before Claude launches, so a SECOND window on the
    # same folder is now allowed to run its own independent Claude Code
    # session concurrently - Claude Code already supports this natively (each
    # conversation is its own JSONL under ~/.claude/projects/<slug>/). If this
    # window loses the race for the lock, it just skips the setup phase
    # (assuming the window that holds it keeps it current) instead of
    # refusing to start.
    $script:HasProjectSetupLock = Initialize-InstanceLock -ProjectDirectory $Path
    if (-not $script:HasProjectSetupLock) {
        Write-Warning "Another window already holds this project's setup lock."
        Write-Hint "Skipping Graphify/companion-tooling setup (that window keeps it current) - opening an independent Claude Code session here instead."
    }
    Register-CleanupHandlers
    Write-Log "=== PROJECT WINDOW === $Path (setup lock held: $($script:HasProjectSetupLock))"

    $isReturningProject = Test-ProjectAlreadyKnown -Path $Path
    Add-ProjectToHistory -Path $Path
    Set-Location $Path
    Write-Log "Working directory: $Path | Returning project: $isReturningProject"

    # v5.1: computed once, up front, and used everywhere below. Graphify is
    # now only used at all for codebases at/above this file-count threshold -
    # below it, Graphify is skipped entirely (not installed, not run, no
    # CLAUDE.md section about it) rather than just having its enforcement
    # softened. See AUDIT.md Finding 2: a mandatory graph-first hook is a bad
    # trade for a handful of files where a normal Read/Glob/Grep is already
    # fast and unambiguous.
    $useGraphify = Test-ProjectExceedsGraphifyThreshold

    # Three ordered setup phases, same dependency order as the launcher
    # window's (PATH -> Graphify -> Claude Code), scaled down since a child
    # window skips the winget/update work the launcher already did for the
    # machine as a whole.
    $totalSteps = 3

    Write-Section -Name "Environment" -Step 1 -TotalSteps $totalSteps
    Add-StandardPaths
    Add-PythonUserScriptsToPath
    $depSummary = Get-DependencySummary -Quiet
    $criticalMissing = @($depSummary.Missing | Where-Object { $_.Name -in @("Python", "pip", "npm") })
    if ($criticalMissing.Count -gt 0) {
        Write-Warning "Missing here: $(($criticalMissing | ForEach-Object { $_.Name }) -join ', ')"
        Write-Hint "Run the launcher window (no -ProjectPath) once to install them."
    } else {
        Write-Success "Toolchain present"
    }
    if ($IsolateClaudeConfig) { Initialize-IsolatedClaudeProfile -ProjectDirectory $Path }

    Write-Section -Name "Graphify" -Step 2 -TotalSteps $totalSteps
    if (-not $useGraphify) {
        Write-Info "Project is under the Graphify threshold ($script:GRAPHIFY_STRICT_FILE_THRESHOLD files) - skipping Graphify entirely for this session"
        Write-Hint "Plain Read/Glob/Grep are the right default at this size. See AUDIT.md Finding 2."
    } elseif (-not (Test-CommandAvailable "graphify" -UseCache)) {
        if (-not (Install-Graphify)) { Stop-Script -Code 104 -Reason "Cannot continue without Graphify" }
    } else {
        Write-Success "Graphify found"
    }

    Write-Section -Name "Claude Code" -Step 3 -TotalSteps $totalSteps
    $claudePath = Find-ClaudeExecutable -Quiet
    if (-not (Test-ClaudeExecutable -Path $claudePath)) {
        Stop-Script -Code 103 -Reason "Claude Code could not be found or verified in this project window (run the launcher window once first)"
    }
    Write-Success "Claude: $claudePath"

    # Same reasoning as the launcher window: a project window opened
    # directly (no launcher run first) may be the first thing that's ever
    # run on this machine, so this is the fallback place companion tooling
    # gets installed. Skipped with no output once all eight are present.
    if (-not (Test-CompanionToolingComplete)) { Install-CompanionTooling }

    # Gated by the narrowed per-project lock (see above): this is the part
    # that actually writes .graphify\graph.json and the project's
    # .claude\settings.json, which is exactly what two concurrent windows on
    # the same folder must not do at the same time. A window that lost the
    # lock race skips this entirely and trusts the window that holds it to
    # keep the graph/directives current.
    if ($script:HasProjectSetupLock) {
        if ($useGraphify) {
            Write-Section "Graphify setup"
            Install-GraphifyPlatform
            Install-GraphifyHook
            Install-GraphifyStrictMode
            Set-ProjectClaudeMdDirective -UseGraphify

            # Invoke-GraphifyExtract always returns $true by design - a failed
            # extraction warns and lets Claude Code start anyway (see its own
            # comments) - so there is deliberately no failure branch here to check.
            $null = Invoke-GraphifyExtract
            Show-GraphResult
        } else {
            # Still write the companion-tooling half of CLAUDE.md (claude-mem,
            # headroom, session-hygiene tips, etc.) even though Graphify itself
            # is skipped for a project this size - those tools apply regardless
            # of codebase size.
            Set-ProjectClaudeMdDirective
        }

        Invoke-AutoSkills

        # v5.3: install a code intelligence plugin (precise symbol navigation +
        # automatic diagnostics after edits) if this project's dominant
        # language already has its LSP binary on PATH - Anthropic's own
        # best-practices guidance. Runs alongside the other one-time,
        # user-scope plugin installs above.
        Install-CodeIntelligencePlugin

        Test-ClaudeMdBloat

        # Release the setup lock now, BEFORE Claude launches, rather than
        # holding it for the whole session lifetime (that's the whole point
        # of narrowing it - Invoke-Cleanup's Unlock-InstanceLock call at exit
        # is now just a safety net for windows that skip straight to launch
        # without ever entering this block, or that error out mid-setup).
        Unlock-InstanceLock
        $script:HasProjectSetupLock = $false
    } else {
        # Setup was skipped (another window holds the lock) - still worth a
        # bloat check since it's read-only and cheap, even though nothing
        # here can fix it.
        Test-ClaudeMdBloat
    }

    if (-not $useGraphify) {
        # v5.3: claude-mem's context-injection defaults (50 observations / 10
        # sessions / 5 full-detail, see docs.claude-mem.ai/configuration) are
        # tuned for larger, longer-lived codebases. A small project doesn't
        # need that much prior context replayed at every session start.
        # Process-scoped only - never touches the shared
        # ~/.claude-mem/settings.json, so it has no effect on any other
        # project.
        $env:CLAUDE_MEM_CONTEXT_OBSERVATIONS = "20"
        $env:CLAUDE_MEM_CONTEXT_SESSION_COUNT = "5"
        $env:CLAUDE_MEM_CONTEXT_FULL_COUNT = "2"
        Write-Log "Small project - reduced claude-mem context injection for this session (20 obs / 5 sessions / 2 full)" -Level "DEBUG"
    }

    Write-Section "Session tips"
    Write-Hint "Watch the headroom bar in your statusline while you work."
    Write-Hint "~70-80% used, or the topic has drifted -> /compact at a natural checkpoint (compaction is lossy - don't wait until it's nearly full, and don't compact mid-task)."
    Write-Hint "Starting genuinely new/unrelated work -> a NEW session beats compacting unrelated history in. This launcher supports running several sessions against this same project at once - see the Continue/Pick/New choice below on a returning project."
    Write-Hint "/clear resets context between unrelated tasks in the SAME session - cheaper than a new window when you're staying in this project."
    Write-Hint "Corrected Claude twice on the same issue? /clear and rewrite the prompt with what you learned - a clean session usually beats a polluted one."
    Write-Hint "Avoid switching models or toggling MCP servers mid-session if you can help it - each invalidates Claude Code's own prompt cache (Anthropic's own guidance) and the next turn re-reads the whole conversation at full price."
    Write-Hint "Full guidance is also in this project's CLAUDE.md (Companion tooling / Session hygiene)."
    Repair-ClaudeMemWorker
    Test-CompressionMethodsActive

    Write-Host ""
    # Never block a spawned project window on a prompt nobody may be
    # watching - the multi-window picker can open several at once. Same
    # guard pattern used throughout Find-ClaudeExecutable and elsewhere:
    # default to launching immediately instead of waiting for a keypress
    # that may never come.
    $exitRequested = $false
    if ($script:IsChild) {
        Write-Info "Launching Claude..."
    } else {
        $exitRequested = (Read-Host "  Press Enter to launch Claude, or X to exit") -match "^[Xx]"
    }
    if ($exitRequested) {
        Write-Info "Exiting without launching Claude"
        return
    }

    # v5.0: a returning, non-child project window gets a real choice instead
    # of always silently --continue-ing. A child window (spawned by the
    # launcher's picker, or any window nobody may be watching) keeps the old
    # default - never block on a prompt that may never get answered.
    $resumeMode = "New"
    if ($isReturningProject) {
        $resumeMode = "Continue"
        if (-not $script:IsChild) {
            Write-Host ""
            Write-Info "This project has been opened before."
            Write-Hint "[C] Continue the most recent session (default)   [P] Pick a past session   [N] Start a brand new session"
            $choice = Read-Host "  Resume how? [C/p/n]"
            $resumeMode = switch -Regex ($choice) {
                '^[Pp]' { 'Pick' }
                '^[Nn]' { 'New' }
                default { 'Continue' }
            }
        }
    }

    $backend = Resolve-SessionBackend -ClaudePath $claudePath
    switch ($backend) {
        'antigravity' { $null = Start-AntigravitySession -ProjectDirectory $Path }
        'local'       { $null = Start-LocalModelSession }
        'none'        { Write-Warning "Skipping session launch - no backend available." }
        default       { Start-ClaudeSession -ClaudePath $claudePath -ResumeMode $resumeMode }
    }
    Show-SessionSummary -ProjectPath $Path -Resumed ($resumeMode -ne 'New')

    Write-Section "Done"
    Write-Success "Completed in $(Get-Elapsed)"
    Write-Hint "Closing this window won't affect your other project windows."
    Write-Hint "Press Enter to close this window..."
    # Bounded the same way as Stop-Script's equivalent wait, rather than an
    # unbounded Read-Host - a window nobody comes back to still closes on
    # its own instead of sitting open forever.
    Wait-KeyPressBounded
}

# ============================================================================
# LAUNCHER MODE - the control panel window
#   Does the machine-wide setup once, then stays open so you can open (and
#   re-open) as many project windows as you like, all running simultaneously.
# ============================================================================

function Invoke-LauncherMode {
    $host.UI.RawUI.WindowTitle = "LLM-TokenOptimizer v$($script:APP_VERSION) - launcher"
    Write-Title
    Write-Log "=== LAUNCHER STARTED === v$($script:APP_VERSION) | User: $env:USERNAME | PID: $PID"
    Register-CleanupHandlers

    # Five ordered setup phases, each depending only on what came before it:
    # OS support -> PATH -> required tools -> Graphify -> Claude Code ->
    # companion tooling (needs Claude Code found; the optional update check
    # runs after companion tooling but isn't itself a numbered step since
    # it's opt-in). After that the interactive picker is the main task, not
    # a "step".
    $totalSteps = 5

    Write-Section -Name "Environment" -Step 1 -TotalSteps $totalSteps
    Test-WindowsVersion
    Add-StandardPaths

    $depSummary = Get-DependencySummary -Step 2 -TotalSteps $totalSteps
    Test-RequiredDependencies -Missing $depSummary.Missing

    Write-Section -Name "Graphify" -Step 3 -TotalSteps $totalSteps
    if (-not (Test-CommandAvailable "graphify" -UseCache)) {
        if (-not (Install-Graphify)) { Stop-Script -Code 104 -Reason "Cannot continue without Graphify" }
    }
    if (-not (Test-GraphifyVersion)) { Write-Warning "Could not verify Graphify version (continuing)" }

    Write-Section -Name "Claude Code" -Step 4 -TotalSteps $totalSteps
    $claudePath = Find-ClaudeExecutable -Quiet
    if (-not (Test-ClaudeExecutable -Path $claudePath)) {
        Write-Warning "Could not confirm Claude Code actually runs - trying manual path entry"
        $claudePath = Request-ClaudePathFromUser
        if (-not $claudePath -or -not (Test-ClaudeExecutable -Path $claudePath)) {
            Stop-Script -Code 103 -Reason "Claude Code could not be found or verified"
        }
    }

    # claude-mem / headroom / claude-code-setup / task-observer /
    # claude-md-management, installed once at user scope. Needs `claude` to
    # be found (just above) since three of the five install as Claude Code
    # plugins.
    Install-CompanionTooling -Step 5 -TotalSteps $totalSteps

    # Optional and opt-in (or -ForceUpdate / -SkipUpdateCheck). Runs after
    # the tools above are confirmed present.
    Invoke-UpdateCheckIfRequested

    # ---- Setup complete - interactive picker loop ----
    $masterPath = Read-MasterFolder
    Save-MasterFolder -Path $masterPath

    $openedCount = 0
    while ($true) {
        $projects = @(Get-ProjectCandidates -MasterPath $masterPath)

        Show-ProjectMenu -MasterPath $masterPath -Projects $projects
        Write-Host ""
        $choice = Select-Projects -MasterPath $masterPath -Projects $projects

        if ($choice -is [string]) {
            if ($choice -eq 'q') {
                Write-Section "Done"
                if ($openedCount -gt 0) {
                    Write-Success "$openedCount project window$(if ($openedCount -ne 1) { 's' }) opened this session"
                    Write-Hint "They keep running after this launcher closes."
                }
                Write-Success "Launcher finished in $(Get-Elapsed)"
                return
            }
            if ($choice -eq 'c') {
                $masterPath = Read-MasterFolder
                Save-MasterFolder -Path $masterPath
            }
            if ($choice -eq 'n') {
                $null = New-ProjectFolder -MasterPath $masterPath
            }
            continue   # 'r' / 'c' / 'n' / bad input -> redraw the menu
        }

        Write-Section "Opening windows"
        $targetProjects = @($choice)
        if ($targetProjects.Count -gt 5) {
            Write-Warning "You are about to launch $($targetProjects.Count) concurrent project windows."
            if (-not (Read-YesNo "Are you sure you want to spawn all of them at once?" $false)) {
                continue
            }
        }

        foreach ($project in $targetProjects) {
            if (Start-ProjectWindow -ProjectDirectory $project) {
                $openedCount++
                # Stagger the spawns slightly: several windows hitting pip
                # and npx in the same instant is a needless thundering herd
                # on a cold start.
                Start-Sleep -Milliseconds 700
            }
        }
        Write-Host ""
        Write-Hint "Windows are running independently. Pick more below, or 'q' to close the launcher."
        Write-Host ""
    }
}

function Invoke-CompleteUninstaller {
    <#
    .SYNOPSIS
        Monitors startup input for the sequence 'rm'. If typed within the window,
        prompts for an 'X' to confirm full uninstallation of script dependencies
        including the Claude Code CLI.
    #>
    [CmdletBinding()]
    param([int]$TimeoutSeconds = 3)

    Write-Host ""
    Write-Host "  [i] Type 'rm' now to initialize uninstaller..." -ForegroundColor DarkGray -NoNewline

    $typedSequence = ""
    $rmDetected = $false
    $startTime = Get-Date

    # Listen for 'rm' sequence during startup delay
    while (((Get-Date) - $startTime).TotalSeconds -lt $TimeoutSeconds) {
        if ([Console]::KeyAvailable) {
            $key = [Console]::ReadKey($true)
            $char = $key.KeyChar.ToString().ToLowerInvariant()

            if ($char -match '[a-z]') {
                $typedSequence += $char
                if ($typedSequence.EndsWith("rm")) {
                    $rmDetected = $true
                    break
                }
            }
        }
        Start-Sleep -Milliseconds 50
    }
    Write-Host "`r" + (' ' * 60) + "`r" -NoNewline # Clear indicator line

    if (-not $rmDetected) {
        return # 'rm' was not typed, continue normal startup
    }

    # Prompt for explicit 'X' confirmation
    Write-Host ""
    Write-Host "  ==========================================================" -ForegroundColor Red
    Write-Host "   UNINSTALL REQUESTED" -ForegroundColor Yellow
    Write-Host "  ==========================================================" -ForegroundColor Red
    Write-Host "  Are you sure you want to uninstall all script tools, Claude CLI & configs?" -ForegroundColor White
    Write-Host ""
    $confirmKey = Read-Host "  Press 'X' to confirm complete uninstall (or any other key to cancel)"
    Write-Host ""

    if ($confirmKey.Trim() -notmatch '^[Xx]$') {
        Write-Info "Uninstallation cancelled. Proceeding with normal launch..."
        Start-Sleep -Seconds 1
        return
    }

    Write-Section "LLM-TokenOptimizer - Complete Targeted Uninstallation"
    Write-Warning "Uninstalling script plugins, skills, MCP servers, Claude CLI, and tooling..."
    Write-Host ""

    $claudeBase = Join-Path $env:USERPROFILE ".claude"
    $skillsDir  = Join-Path $claudeBase "skills"
    $pluginsDir = Join-Path $claudeBase "plugins"

    # 1. Remove leftover Claude MCP Server registration from OmniRoute-era
    # installs (v5.5 removed OmniRoute entirely - this is cleanup for anyone
    # upgrading from an older version, harmless no-op otherwise).
    if (Test-CommandAvailable "claude" -UseCache) {
        Write-Info "Removing any leftover OmniRoute MCP server registration..."
        $null = Invoke-ExternalCommand -Command "claude" -Arguments "mcp remove omniroute --scope user" -TimeoutSeconds 15 -Silent -NoLog
    }

    # 2. Uninstall Official Claude Plugins installed by this script
    if (Test-CommandAvailable "claude" -UseCache) {
        Write-Info "Uninstalling Official Claude Code plugins..."
        $null = Invoke-ExternalCommand -Command "claude" -Arguments "plugin uninstall claude-code-setup@claude-plugins-official --scope user" -TimeoutSeconds 30 -Silent
        $null = Invoke-ExternalCommand -Command "claude" -Arguments "plugin uninstall claude-md-management@claude-plugins-official --scope user" -TimeoutSeconds 30 -Silent
        Write-Info "Uninstalling Caveman plugin..."
        $null = Invoke-ExternalCommand -Command "claude" -Arguments "plugin uninstall caveman@caveman --scope user" -TimeoutSeconds 30 -Silent
    }

    # 2b. Remove RTK (binary + its Claude Code hook registration)
    $rtkDir = Join-Path $env:LOCALAPPDATA "rtk"
    if (Test-Path $rtkDir) {
        Write-Info "Removing RTK..."
        Remove-Item -Path $rtkDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    $rtkHook = Join-Path $env:USERPROFILE ".claude\hooks\rtk-rewrite.sh"
    if (Test-Path $rtkHook) { Remove-Item -Path $rtkHook -Force -ErrorAction SilentlyContinue }

    # 3. Targeted Removal of Custom Installed Plugins from ~/.claude/plugins
    Write-Info "Cleaning up script plugins & cache..."
    $scriptPluginPaths = @(
        (Join-Path $pluginsDir "cache\superpowers"),
        (Join-Path $pluginsDir "marketplaces\thedotmack\claude-mem")
    )
    foreach ($path in $scriptPluginPaths) {
        if (Test-Path $path) {
            Remove-Item -Path $path -Recurse -Force -ErrorAction SilentlyContinue
            Write-Success "Removed plugin path: $path"
        }
    }

    # Clean script plugins from installed_plugins.json without destroying other plugin entries
    $installedJsonPath = Join-Path $pluginsDir "installed_plugins.json"
    if (Test-Path $installedJsonPath) {
        try {
            $json = Get-Content $installedJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($json.plugins) {
                $scriptKeys = @("superpowers", "last30days", "frontend-design")
                foreach ($key in $scriptKeys) {
                    if ($json.plugins.PSObject.Properties.Name -contains $key) {
                        $json.plugins.PSObject.Properties.Remove($key)
                    }
                }
                $json | ConvertTo-Json -Depth 4 | Set-Content -Path $installedJsonPath -Encoding UTF8
                Write-Success "Cleaned script plugin entries from installed_plugins.json"
            }
        } catch {
            Write-Log "Failed to update installed_plugins.json: $_" -Level "WARN"
        }
    }

    # 4. Targeted Removal of Skills created by Install-ClaudePluginsAndSkills & Task-Observer
    Write-Info "Removing script-installed skills from ~/.claude/skills..."
    $scriptSkills = @(
        "last30days",
        "frontend-design",
        "bencium-controlled-ux-designer",
        "graphify",
        "impeccable",
        "task-observer"
    )
    foreach ($skillName in $scriptSkills) {
        $targetSkillPath = Join-Path $skillsDir $skillName
        if (Test-Path $targetSkillPath) {
            Remove-Item -Path $targetSkillPath -Recurse -Force -ErrorAction SilentlyContinue
            Write-Success "Removed skill: $skillName"
        }
    }

    # 5. Uninstall Global NPM Packages including Claude Code CLI
    if (Test-CommandAvailable "npm" -UseCache) {
        Write-Info "Uninstalling global NPM tools (Claude CLI, claude-mem, autoskills)..."

        $null = Invoke-ExternalCommand `
            -Command "npm" `
            -Arguments "uninstall -g @anthropic-ai/claude-code omniroute claude-mem autoskills" `
            -TimeoutSeconds 120 `
            -ShowSpinner `
            -SpinnerLabel "Removing NPM packages"

        # Remove stale OmniRoute shims/package leftovers from pre-v5.5 installs
        try {
            $npmGlobal = Join-Path $env:APPDATA "npm"

            @(
                "omniroute",
                "omniroute.cmd",
                "omniroute.ps1"
            ) | ForEach-Object {
                Remove-Item `
                    (Join-Path $npmGlobal $_) `
                    -Force `
                    -ErrorAction SilentlyContinue
            }

            Remove-Item `
                (Join-Path $npmGlobal "node_modules\omniroute") `
                -Recurse `
                -Force `
                -ErrorAction SilentlyContinue
        }
        catch {}
    }

    # 6. Uninstall Python Packages installed by this script
    if (Test-CommandAvailable "pip" -UseCache) {
        Write-Info "Uninstalling Python packages (graphifyy)..."
        $null = Invoke-ExternalCommand -Command "pip" -Arguments "uninstall -y graphifyy" -TimeoutSeconds 60 -ShowSpinner -SpinnerLabel "Removing Graphify"
    }

    # 7. Remove Helper Tools & App Data Configs
    Write-Info "Cleaning up app data and memory configurations..."
    $claudeMemConfigDir = Join-Path $env:USERPROFILE ".claude-mem"
    if (Test-Path $claudeMemConfigDir) { Remove-Item $claudeMemConfigDir -Recurse -Force -ErrorAction SilentlyContinue }

    if (Test-Path $script:GlobalGateFile) { Remove-Item $script:GlobalGateFile -Force -ErrorAction SilentlyContinue }
    if (Test-Path $script:AppDataDir) { Remove-Item $script:AppDataDir -Recurse -Force -ErrorAction SilentlyContinue }

    Write-Host ""
    Write-Success "Targeted uninstallation complete!"
    Write-Hint "All script-installed plugins, skills, MCP servers, Claude CLI, and configs were removed."
    Write-Hint "Your base runtimes (Node.js, Python, Git) remain intact."
    Stop-Script -Code 0 -Reason "Uninstalled by user request."
}

# ============================================================================
# MAIN ENTRY POINT
# ============================================================================

function Invoke-Main {
    Initialize-Logging
    Initialize-Configuration

    # Launcher-only: a spawned project window is the wrong place to offer
    # ripping out shared global tools (Claude CLI, RTK, etc.) out from under
    # its sibling windows, and there's no reason every one of several
    # concurrently-opened project windows should show this prompt at all.
    if (-not $script:IsChild) {
        Invoke-CompleteUninstaller -TimeoutSeconds 3
    }

    try {
        if ($ProjectPath) {
            Invoke-ProjectMode -Path ($ProjectPath.Trim().Trim('"').TrimEnd('\'))
        } else {
            Invoke-LauncherMode
        }
        exit 0
    } catch {
        Write-Host ""
        Write-Fail "Unexpected error: $_"
        Write-Log "Fatal error: $_" -Level "ERROR"
        Write-Log "Stack: $($_.ScriptStackTrace)" -Level "ERROR"
        Write-Hint $_.ScriptStackTrace
        Stop-Script -Code 99
    } finally {
        Invoke-Cleanup
    }
}

Invoke-Main

