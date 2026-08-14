> **Legacy document.** This audits the PowerShell launcher (`LLM-TokenOptimizer.ps1`), which v6.0's C# app (`app/`) superseded as the product. Kept as project history only — see the root [README.md](README.md) for current install/build instructions.

# LLM-TokenOptimizer — Tooling Audit (v5.0 - v5.5)

Written alongside a pass that added quota auto-retry and multi-session
support. This audits what the launcher installs and whether each piece
actually earns its token/complexity cost, instead of assuming an addition
helps just because it shipped.

## Method

- Read the full 4671-line `LLM-TokenOptimizer.ps1` (three parallel focused
  passes: OmniRoute/compression, companion-tooling installers, session/window
  management).
- Verified externally that every installed tool is a real, maintained project
  (not vaporware) via web search: OmniRoute (github.com/diegosouzapw/OmniRoute),
  headroom (github.com/henchmarketing-rgb/headroom), claude-mem
  (docs.claude-mem.ai), task-observer (github.com/iamneilroberts/claude-skills),
  and the two official Anthropic plugins (claude-code-setup,
  claude-md-management).
- Cross-checked findings against this very session's own live skill list and
  CLAUDE.md, since this project runs the tool it's auditing.

## Finding 0 (compliance risk, fixed in v5.2, resolved by removal in v5.5): OmniRoute's Claude routing violates Anthropic's ToS

**v5.5 update**: OmniRoute has been removed from this project entirely - not
just left off by default. See Finding 10 below. Everything below this line
is kept as the historical record of why the v5.2 default-off decision was
made in the first place; the compliance risk it describes no longer applies
to this codebase at all, since the mechanism that created it is gone, not
dormant.


Prompted by a direct question - "is OmniRoute's compression still worth it
alongside claude-mem and headroom, or do they conflict?" - this turned up
something more serious than a redundancy question.

**First, the redundancy question itself, resolved**: they don't overlap.
Neither claude-mem (cross-session memory, via Claude Code's own hooks) nor
the actual `headroom` project this script installs
(`henchmarketing-rgb/headroom`, confirmed by reading its real README
directly) does any token compression - headroom is a passive statusline
monitor, full stop. (Worth flagging precisely because it's an easy mistake:
there's a *different*, similarly-named `headroom-ai` package on npm/PyPI
that does claim compression, and an earlier pass in this same audit process
almost repeated that conflation before being checked against the actual
installed project's source. It's not what this script installs.) OmniRoute
was, and is, the only real compression mechanism in this stack - nothing
else to conflict with it.

**The actual finding**: OmniRoute's Claude Code integration (the `cc/`
provider) authenticates using Claude Code's own **subscription OAuth
token**, routed through OmniRoute's local gateway. Anthropic's Consumer
Terms of Service, updated **2026-02-20** and enforced since **2026-04-04**,
explicitly prohibit this: *"Using OAuth tokens obtained through Claude
Free, Pro, or Max accounts in any other product, tool, or service... is not
permitted."* Confirmed via Anthropic's own policy language (multiple
independent secondary sources quoting it consistently) and via OmniRoute's
own GitHub activity - the maintainer is still actively patching this exact
code path (a v3.8.0 fix for OAuth quota-error classification postdates the
policy change), meaning the capability still exists and still works
mechanically, but using it puts the underlying Claude subscription itself
at risk of restriction. That's a cost no amount of compression savings
offsets.

This script was specifically built around that OAuth pathway
(`Confirm-ClaudeCodeProvider`, `Resolve-OmniRoute1MModel` pinning
`cc/claude-opus-5` / `cc/claude-sonnet-5`), so this isn't a peripheral
detail - it's the center of the whole OmniRoute integration.

**Fixed in v5.2**: `$script:OMNIROUTE_ROUTE_CLAUDE` defaults to `$false`.
Claude Code now launches natively by default - no OmniRoute env wiring, no
onboarding prompts, no background OmniRoute window, no 1M-context model
pinning (that rode on the same OAuth connection). The integration isn't
deleted, just off - re-enabling it is documented as safe only after
switching to a real, metered Anthropic Console API key (the path Anthropic's
policy actually permits) instead of subscription OAuth, which the code has
no way to verify was actually done, so that judgment call is left to whoever
flips the flag back, deliberately, with eyes open.

**Trade-off, stated plainly**: this means the compression savings this whole
project was originally built around are now off by default. That's the
right trade given the alternative is risking the Claude subscription itself
- but it's the single biggest thing this audit changed, and worth
knowing about explicitly rather than discovering silently.

## Finding 1 (bug, fixed in v5.0): fabricated placeholder skills

`Install-ClaudePluginsAndSkills` used to write `SKILL.md` stubs for
`last30days`, `frontend-design`, `bencium-controlled-ux-designer`, `graphify`,
and `impeccable` whose entire body was the literal string
`Active and ready for tool execution.` — no real instructions, ever. These
are not "lightweight" skills; they're empty manifests that still show up in
every session's skill list (confirmed live in the session that audited this:
they render as bare `---`-description entries) for zero functional benefit.
Real `graphify` skill wiring is handled separately and correctly by
`Install-GraphifyPlatform` (`graphify install --platform claude`) — the stub
generator was pure overhead, not a fallback for anything.

**Verdict: net negative, no offsetting benefit. Removed in v5.0** (see the
script's `Install-ClaudePluginsAndSkills`, which now actively deletes any
stub left behind by a prior run so upgrading reclaims the tokens instead of
just capping future growth).

## Finding 2 (design risk, flagged not yet changed): standing-instruction stacking

A project set up by this launcher gets **all of the following** injected as
always-on instructions or hooks, every single session:

1. Graphify's CLAUDE.md block: "CRITICAL... non-negotiable" rule to consult
   the graph before *any* raw file read/Glob/Grep, plus a strict-mode
   `PreToolUse` hook that hard-blocks the first raw read of a session.
2. `task-observer`, whose own trigger description says to invoke it "at the
   start of every task-oriented session."
3. `claude-mem`'s SessionStart/PostToolUse/Stop hooks (memory capture +
   injection).
4. `headroom`'s statusline, refreshed after every tool call.

None of these is individually unreasonable, and none is fake (unlike Finding
1). But a blanket "non-negotiable, always consult the graph first" rule is a
worse fit for a one-line lookup in a small file than for genuine
cross-codebase exploration, and four systems all adding overhead before real
work starts is a real, compounding cost that the script's changelog never
weighs against the Graphify/compression savings it's chasing.

**Recommendation** (not yet implemented — needs a decision, not just a
patch): scope the "non-negotiable" Graphify enforcement to codebases above a
size threshold (e.g. file count or LOC) rather than applying it uniformly,
and consider whether task-observer's every-session trigger is worth it
outside of longer/complex sessions. Left as a follow-up rather than force-
fitting a threshold without the user's input on where to draw it.

**Update (Graphify half only)**: implemented, then extended further in v5.1.
`Test-ProjectExceedsGraphifyThreshold` (`LLM-TokenOptimizer.ps1`, near
`Install-GraphifyHook`) counts source files under the project root
(excluding `node_modules`, `.git`, build output, etc.) against a 150-file
threshold (`$script:GRAPHIFY_STRICT_FILE_THRESHOLD`). The v5.0 version of
this fix only gated *strict enforcement* - Graphify itself was still always
installed and run below the threshold, just with softer CLAUDE.md wording.
**v5.1 goes further, per explicit follow-up request: below the threshold,
Graphify is skipped entirely** - not installed, not run, no CLAUDE.md
section about it at all (`Invoke-ProjectMode`'s "Graphify" step and the
locked "Graphify setup" phase are both now gated on the same threshold
check, computed once as `$useGraphify`). `Set-ProjectClaudeMdDirective`'s
`-Strict` switch was replaced with `-UseGraphify`, which omits the
"# Graphify enforcement" heading entirely rather than writing a softer
variant of it. This caught a real idempotency bug during live testing: the
original merge-detection logic re-checked "does the heading exist yet" on
every launch, which for a project that will never get that heading (below
threshold) evaluated true-for-missing every time and would have
re-appended the companion-tooling section on top of itself on every single
launch. Fixed and verified with an actual create-then-rerun test against
both a below-threshold and an above-threshold scratch directory - both
confirmed idempotent via before/after content diff. The threshold itself
(150 files) is still a rough guess, not a tuned value - no live A/B was
done to pick that number specifically. task-observer's every-session
trigger is unchanged (it's controlled by the skill's own description text,
not by this script) and is still an open follow-up.

## Finding 3 (unresolved, needs live measurement): compression vs. prompt caching

Anthropic's prompt caching discounts cache-hit input tokens by ~90%, but only
when the cached prefix is byte-identical between calls (minimum cacheable
block: 1024 tokens). This script pins OmniRoute's compression to **Stacked**
unconditionally (`$script:OMNIROUTE_COMPRESSION_MODE = "stacked"`, no
opt-out, no per-project tuning) and documents savings of 78-95% of *eligible*
tokens. Both claims are individually true and independently verified as real
(not fabricated), but they weren't tested *together*: if compression
reshapes prompt bytes differently turn-to-turn, it could be forfeiting the
90%-off cache-hit discount in exchange for the 20-40%-off compression
discount — a net loss specifically on long multi-turn sessions, which is
exactly where this launcher is meant to help most.

**Recommendation**: this needs a real before/after measurement (run a
representative multi-turn session with compression on vs. off, compare
actual billed/cached tokens via the OmniRoute dashboard or Claude Code's
`/cost`), not a code change based on a hypothesis. Flagging it here as the
single most consequential open question for anyone deciding whether to keep
Stacked mode pinned on by default.

**Update**: the measurement itself still hasn't been run (needs live
billing/cache data this environment doesn't have — see the v5.0 verification
record below), but the actual gap this finding exposed — "no opt-out, no
per-project tuning" — is fixed. A new `-CompressionMode stacked|ultra|off`
parameter (also exposed as `llmTokenOptimizer.compressionMode` in the VS Code
extension) lets anyone override the pinned default per session:
`off` skips `Set-OmniRouteBestCompression` entirely rather than PUTting a
possibly-unsupported mode string, so a long session can keep its prompt-cache
prefix byte-identical while someone actually runs the A/B described above.
The default behavior (no flag passed) is unchanged — still pinned to Stacked
— since flipping the default without the live measurement would be exactly
the "code change based on a hypothesis" this finding warned against.

**Update 2 (research, per explicit request)**: checked OmniRoute's own
compression-engine documentation directly for a setting that's specifically
designed to be compatible with (not break) provider prompt caching.
**None exists.** No mode is documented as cache-aware. The closest real
mechanism is a per-model/endpoint **exclusion list** (`exclusions` in the
compression settings) that lets an operator name specific model IDs that
must never be compressed at all - a guardrail against byte-modification
breaking a cache-sensitive model's exact payload, but it works by fully
opting a model OUT of compression, not by combining compression and caching
together. There is no "best of both" mode to switch to. Compression setup
itself was already fully automatic before this update (`Set-
OmniRouteBestCompression` runs unconditionally as part of OmniRoute
onboarding, no manual step needed) - what doesn't exist is a smarter
*choice* to automate. The pinned Stacked default was deliberately left
unchanged rather than guessed at, consistent with this finding's standing
objection to a hypothesis-driven change without the live measurement still
described above.

**Update 3 (v5.5)**: this finding was specifically about OmniRoute's
gateway-side Stacked compression, which rewrote prompt bytes on Anthropic's
side of the wire before every call - that's what created the cache-prefix
risk. RTK and Caveman (the tools that replaced OmniRoute, see Finding 10)
don't have this shape of risk: RTK only ever touches Bash tool-call *output*
(command results, not the prompt prefix itself), and Caveman changes the
*model's own generated output*, not the input prompt Claude Code sends.
Neither one rewrites the stable system-prompt/history prefix a cache hit
depends on. This doesn't retroactively validate the old Stacked-vs-caching
question -
that measurement still was never run - it just means the question doesn't
apply to the current tooling, since the mechanism that raised it (gateway-
side prompt rewriting) is no longer in this codebase at all.

## Finding 4 (minor, not yet changed): Graphify install method

Graphify's own documentation recommends `pipx install graphifyy` or
`uv tool install graphifyy` over plain `pip install graphifyy`, specifically
because of Windows PATH problems — which this script has separately (and
repeatedly) patched around after the fact (`Add-PythonUserScriptsToPath`,
`Sync-ProcessPathFromRegistry`). Switching `Install-Graphify` /
`Update-GraphifyIfNeeded` to `pipx`/`uv tool install` would likely eliminate
a whole class of bugs this script currently works around downstream instead
of avoiding upstream. Not changed in v5.0 (would need a `pipx`/`uv`
availability check added to the dependency install flow first) — left as a
scoped follow-up.

## Finding 5: everything else is real

OmniRoute, headroom, claude-mem, claude-code-setup, claude-md-management,
and task-observer are all genuine, actively maintained projects with
plausible, externally-corroborated savings/functionality claims. No other
part of the install pipeline was found to be fake, abandoned, or
non-functional. The self-acknowledged CLAUDE.md redundancy (this script
writes it, the claude-md-management plugin also edits it via
`/revise-claude-md`) is intentional and handled idempotently via marker
headings — documented here as a known coexistence, not a bug.

## Summary

| # | Finding | Status |
|---|---|---|
| 0 | OmniRoute's Claude routing uses subscription OAuth in violation of Anthropic's ToS | **Fixed in v5.2** - off by default (`$script:OMNIROUTE_ROUTE_CLAUDE = $false`) |
| 1 | Fabricated empty placeholder skills | **Fixed in v5.0** |
| 2 | Standing-instruction stacking across 4 systems every session | Graphify half **fully fixed in v5.1** (skipped entirely below threshold, not just softened); task-observer's every-session trigger still open |
| 3 | Compression may fight prompt caching on long sessions | Opt-out shipped in v5.0 (`-CompressionMode off`); v5.1 confirmed no OmniRoute-native "both at once" mode exists; largely moot after v5.2 (OmniRoute compression off by default) |
| 4 | `pip install` vs `pipx`/`uv tool install` for Graphify | Flagged, scoped follow-up |
| 5 | Rest of the stack | Verified real, no action needed |
| 6 | OmniRoute launched in a `cmd.exe` window, inconsistent with every other window this launcher opens | **Fixed in v5.1** - now a real minimized PowerShell window, verified live (correct title + confirmed minimized via Win32 `IsIconic`) |
| 7 | With OmniRoute compression off by default (v5.2), what non-OmniRoute token-saving levers actually exist? | **Added in v5.3** - see below |
| 8 | Broad sweep for more standalone companion tools (starting from prime-agent) | **v5.4** - Context7 added, three candidates declined (two for the SAME OAuth-proxy risk as Finding 0), one stale MCP registration cleaned up - see below |
| 9 | Follow-up "last 30 days" sweep for new plugins/skills/MCPs/agent-optimization | No further script changes warranted - two real mechanisms documented, not defaults-changed; 4 leftover placeholder-skill stubs found and removed from the live machine (Finding 1's fix had never actually run against it) - see below |

## Finding 7 (v5.3): token-saving techniques that don't depend on OmniRoute

With OmniRoute's Claude routing off by default (Finding 0), its compression
is no longer the primary savings lever for most users. Researched Anthropic's
own official Claude Code documentation directly (`code.claude.com/docs/en/
best-practices`, `docs.claude-mem.ai/configuration`) for concrete,
script-implementable levers instead of guessing:

- **Prompt caching is already automatic** - Claude Code caches its own
  system prompt, tool definitions, and CLAUDE.md on every request, no
  configuration needed, and (per Anthropic's own docs) *cannot* be layered
  on top of with more caching. The real risk is invalidating it:
  mid-session model switches, or MCP/plugin changes that trigger
  `/reload-plugins`, blow away the cache and the next turn re-reads the
  whole conversation at full price. This script's own pre-v5.2 behavior
  (registering an OmniRoute MCP server, forcing `--model` mid-session) was
  exactly this kind of cache-invalidating action - the v5.2 OmniRoute
  default-off change incidentally removes one instance of it. Session tips
  (console + CLAUDE.md) now warn about this explicitly.
- **Code intelligence plugins** - Anthropic's own guidance recommends these
  for typed languages (precise symbol navigation + automatic diagnostics
  instead of grep-based search). `Install-CodeIntelligencePlugin` detects
  the project's dominant language and installs the matching official plugin
  (`typescript-lsp`, `pyright-lsp`, `gopls-lsp`, `rust-analyzer-lsp`, etc.
  - exact IDs taken from Anthropic's docs, not guessed) **only** when the
  required language-server binary is already on PATH - this script installs
  the Claude Code plugin, never the underlying compiler/language tooling
  itself. Verified live against real scratch directories for Python-
  dominant, TypeScript-dominant, and no-mapped-language projects - all
  three detected correctly.
- **claude-mem context-injection tuning** - its defaults (50 observations /
  10 sessions / 5 full-detail per session start) are sized for large,
  long-lived codebases. Below the same size threshold Graphify already
  uses, these are now reduced (20/5/2) via process-scoped env vars set
  right before Claude launches - never touches the shared
  `~/.claude-mem/settings.json`, so no effect on any other project.
- **CLAUDE.md bloat warning** - Anthropic's own guidance: a bloated
  CLAUDE.md causes Claude to ignore half of it. `Test-ClaudeMdBloat` warns
  (never edits) once a project's file crosses 300 lines. Verified live
  against a 50-line file (silent), a 350-line file (warns), and a missing
  file (silent no-op).
- **Expanded session tips** - `/clear` between unrelated tasks in the same
  session, and `/clear` + rewrite after two failed corrections on the same
  issue, both taken directly from Anthropic's documented failure patterns
  rather than invented.

## Finding 8 (v5.4): broad sweep from prime-agent, one near-miss caught by live testing

Starting point was `github.com/PrimeIntellect-ai/prime-agent`. Checked what
it actually is before evaluating anything: a **standalone competing agentic
CLI** (its own Recursive-Language-Model / continual-harness architecture,
own subscription/API-key login), not a Claude Code companion tool. Nothing
to integrate here - noted so the research trail is complete rather than
silently skipped.

Widened the search to real, maintained Claude Code companion tools and
evaluated each against the exact bar Finding 0 set: **does it touch Claude
Code's own Anthropic API traffic or subscription OAuth token in any way?**

- **headroom-ai** (`headroomlabs-ai/headroom` - a genuinely different
  project from the statusline `henchmarketing-rgb/headroom` already
  installed, confusingly similar name) looked like the strongest candidate:
  real, 65k+ stars, actively maintained, MCP-server-shaped. Live-tested
  before writing anything into the script - `pip install "headroom-ai[mcp]"`,
  then `headroom mcp --help` - and its own help text says the actual
  compression only happens by routing ALL traffic through a local proxy via
  `ANTHROPIC_BASE_URL`, explicitly documented as "for subscription users who
  don't have API access." That's the identical OAuth-in-a-third-party-tool
  shape Finding 0 flagged in OmniRoute; the `headroom_retrieve` MCP tool
  alone does nothing useful without that proxy running underneath it. This
  is exactly the kind of thing a search-result summary said was safe
  ("no OAuth risk") and live testing caught as wrong. **Declined**; the test
  install was removed from the machine it was verified on.
- **claude-rolling-context** - same shape again on inspection: "configures
  `ANTHROPIC_BASE_URL` to route requests through a local proxy," using
  "existing Claude Code authentication." **Declined** for the same reason,
  without needing to install it to know why.
- **foldback-ai / claude-context / code-review-graph** - declined as
  redundant, not risky: compression overlap with the (declined) headroom-ai
  research, and codebase-graph/semantic-search overlap with Graphify, which
  is already installed and already size-gated (Finding 2 / v5.1). Stacking
  multiple overlapping tools without evidence each adds distinct value is
  the exact anti-pattern this whole audit exists to catch - not repeating it
  just because a new candidate showed up.
- **Context7** (`@upstash/context7-mcp`) - **added**. Injects version-
  specific library/API docs on demand instead of Claude guessing from
  training data or spending turns grepping dependency source for the answer.
  Confirmed via its own `--help` output there's no `ANTHROPIC_BASE_URL` or
  proxy involvement at all - a plain stdio MCP server, architecturally
  identical to any other sanctioned MCP integration (GitHub, Notion, etc.)
  Claude Code's own docs already list as supported. Works without an API key
  (lower rate limit only, no account required). Live-tested end to end on a
  real machine: `claude mcp add --scope user context7 -- npx -y
  @upstash/context7-mcp` succeeded, `claude mcp list` showed
  `context7: ... - ✔ Connected`.
- **MCP Tool Search** (Claude Code's own lazy-loading of MCP tool
  definitions, up to 95% less context overhead per added server) is already
  enabled by default in current Claude Code versions - nothing to configure,
  noted here so it isn't mistaken for a gap this script needs to fill.

**Bonus finding, not from the sweep itself**: while live-testing Context7's
registration on a real machine, `claude mcp list` also surfaced a genuinely
stale leftover - `omniroute: ... - Failed to connect`, a dead registration
from before v5.2 disabled OmniRoute's Claude routing by default. v5.2 never
cleaned up an *existing* registration, only stopped creating new ones. Fixed
with `Remove-StaleOmniRouteMcpServer`, which runs once whenever OmniRoute
routing is off. Live-tested on that exact real registration: `claude mcp
remove omniroute --scope user` succeeded, confirmed gone from `claude mcp
list` afterward.

**Also found and fixed on the live machine this was tested on** (not a
script change - this was leftover state from before the v5.0 fix ever ran
against this machine's real `~/.claude/skills/`): the four fabricated
placeholder skills Finding 1 describes (`last30days`, `frontend-design`,
`bencium-controlled-ux-designer`, `impeccable`) were still present on disk,
each still exactly the empty `Active and ready for tool execution.` stub.
The v5.0 script fix only changes what a *future run* installs/cleans up; it
was never actually re-run against this machine's existing `~/.claude/skills`
until this pass. All four removed directly.

## Finding 9 (v5.4, second pass): broader "last 30 days" sweep - nothing further warranted

A follow-up request asked for a deep, recency-biased sweep (new Claude Code
plugins/skills/MCPs, new agent-optimization techniques) covering roughly the
last 30 days. Searched multiple angles - new MCP servers/plugins, new token-
reduction techniques, new agent-agnostic skill marketplaces (Skills.sh,
agent-skills.cc, ClaudeSkills.info, netresearch/claude-code-marketplace),
new orchestration features (Agent Teams, Dynamic Workflows). Two genuine,
verifiable, official Anthropic mechanisms turned up:

- **`MAX_MCP_OUTPUT_TOKENS`** (default 25,000, per Claude Code's own MCP
  docs) - a real, documented env var, but it's a ceiling you *raise* when
  you're hitting truncation warnings on a verbose MCP server, not a lever
  you lower for savings; the default is already Anthropic's own considered
  choice, not an oversight. Not touched, for the same reason Finding 3
  didn't flip the OmniRoute compression default without evidence: no
  measurement suggests this machine's usage would benefit from a different
  value, and guessing a new default isn't the same as measuring one.
- **Agent Teams** (built-in multi-agent orchestration, currently
  experimental and disabled by default in Claude Code itself) - real, but
  explicitly opt-in and explicitly experimental per Anthropic's own
  framing; this script won't force an experimental feature on by default.
  Worth knowing it exists as a *different* mechanism from this launcher's
  own multi-session support (Agent Teams runs teammates *within* one
  session as coordinated subagents; this launcher's multi-session feature
  runs fully independent `claude` processes/conversations) - not a
  replacement for either, just adjacent.

Everything else the sweep surfaced was either already covered by a prior
finding (compression, semantic caching, memory architecture - all OmniRoute/
claude-mem-shaped ground already covered by Findings 0, 3, 7, 8) or was a
skill/plugin *marketplace* rather than a specific, vetted tool - installing
from those in bulk without individually verifying each one is the exact
anti-pattern Finding 1 exists to warn against, so none were added sight
unseen. **No script changes from this pass** - the honest result of a
broad search is sometimes that the net new ground is thin, and reporting
that is more useful than manufacturing a change to justify the search.

## v5.0 verification record

What was actually run, not just written, before calling this done:

- **Full-script syntax**: `[System.Management.Automation.Language.Parser]::ParseFile`
  over the whole 4700+ line script - zero errors, both after the initial
  change set and again after the fix described below.
- **Embedded C# compile + smoke test**: the `RateLimitWatcher`/`ConsoleIo`
  .NET type (loaded via `Add-Type`) was extracted and compiled in isolation -
  it loads and its methods run without throwing.
- **Cross-process instance-lock test (real, not mocked)**: two genuine
  separate OS processes, both running the exact `Get-PathSlug`/
  `Initialize-InstanceLock`/`Unlock-InstanceLock` code extracted verbatim
  from the shipped script, contending for the same project folder's real
  Win32 named mutex. Confirmed: window B is refused the lock while window A
  holds it, and acquires it correctly once A releases - the exact mechanism
  the multi-session feature depends on.
- **Live rate-limit watcher test, in a genuine attached console window**
  (not this sandboxed tool environment, whose stdio is fully redirected -
  `[Console]::IsOutputRedirected` is `true` here, which the watcher already
  degrades safely under). A detached `powershell.exe` process was spawned
  with its own real console, printed Claude Code's own rate-limit wording
  ("5-hour limit reached - resets 11pm") followed by a "Stop and wait" menu
  line, with the watcher running live alongside it:
  - First run: the console text was captured correctly (confirmed via a raw
    screen dump), but **zero watcher log lines appeared** - a real bug. The
    `Log` callback was a PowerShell scriptblock invoked as an
    `Action<string>` delegate from the watcher's own background `Thread`;
    PowerShell scriptblocks are bound to the runspace of whichever thread
    created them, a raw .NET `Thread` has no runspace, so every invocation
    threw and was silently swallowed by `SafeLog`'s `catch { }`. The
    detection/response logic itself (pure C#, no PowerShell involved) was
    unaffected - only visibility into it was broken.
  - **Fixed**: `RateLimitWatcher.Log` (a PowerShell delegate) was replaced
    with `RateLimitWatcher.LogFilePath` (a plain string), and `SafeLog` now
    writes directly via `File.AppendAllText` under a lock - no PowerShell
    involved from the background thread at all. `Start-RateLimitWatcher`
    points it at the same daily log file `Write-Log` itself writes to.
  - Re-run after the fix: the watcher's own log showed, with real
    timestamps two seconds apart, `Rate-limit text detected in console
    output` followed by `Found 'Stop and wait' option - selecting it` -
    confirming detection, the wait for the menu to render, and the
    `WriteConsoleInput`-injected Enter keypress all fired correctly, live,
    in a real console.
  - **Not tested**: the fallback path (no "Stop and wait" menu ever
    appears, so the watcher parses a reset time out of the matched text and
    waits it out itself before sending "continue") - exercising it for real
    means either waiting out a multi-hour timer or patching the wait
    constant for a test run, and wasn't done. Reviewed by re-reading the
    code, not run.

Still not done, and not doable from here - both require live Claude Code
credentials/billing this environment doesn't have:
- **Two real Claude Code sessions concurrently in one folder**, confirming
  two distinct session JSONLs get created under
  `~/.claude/projects/<slug>/`. The *lock mechanism* that makes this safe
  was verified (above); the actual `claude` process behavior under it was
  not.
- **Compression-vs-prompt-caching token measurement** (Finding 3) - needs a
  real multi-turn session with actual billing/cache-hit data to compare,
  which cannot be produced synthetically. (Finding 3's Update 3 explains why
  this specific risk no longer applies to the current RTK/Caveman tooling,
  but the underlying measurement itself was still never run against
  anything.)

## Finding 10 (v5.5): OmniRoute removed entirely, replaced with the real tools it wrapped

Per direct request: import OmniRoute's documented compression modes
"manually (without OmniRoute)" - i.e. find the actual open-source projects
OmniRoute reimplements, and use them directly instead of going through
OmniRoute's gateway at all.

**Research finding**: OmniRoute's own compression-mode documentation
(Lite/Standard/Aggressive/Ultra/RTK/Stacked, Cache-Aware, Progressive Aging)
describes one real underlying pair of open-source projects plus OmniRoute's
own proprietary regex/heuristic layer on top of them:
- **RTK** ("Rust Token Killer", `github.com/rtk-ai/rtk`, Apache-2.0) - a real,
  actively maintained (~1,464 commits, pushed within the last day as of this
  writing), standalone local binary. Confirmed via its actual GitHub
  Releases API (not marketing pages): ships a genuine
  `rtk-x86_64-pc-windows-msvc.zip` Windows release asset, and wires into
  Claude Code as a real `PreToolUse` hook (`rtk init -g` writes
  `~/.claude/hooks/rtk-rewrite.sh`, confirmed by reading that file directly)
  that rewrites Bash tool calls to filter/compress command output. No API
  key, no network service.
- **Caveman** (`github.com/JuliusBrussee/caveman`, MIT) - a real Claude Code
  plugin, confirmed by fetching its actual `.claude-plugin/marketplace.json`
  and `.claude-plugin/plugin.json` directly (not assumed from its README):
  marketplace name `caveman`, plugin name `caveman`, registers a
  `SessionStart` hook (`src/hooks/caveman-activate.js`) active from message
  one. No API key, zero network calls after install per its own README.
- Everything else OmniRoute names (Lite, Standard, Aggressive, Ultra,
  Stacked, Cache-Aware, Progressive Aging) is OmniRoute's own proprietary
  composite logic layered on RTK + Caveman, not a separate open-source
  project - reimplementing those from scratch would mean guessing at
  behavior nobody's verified, not importing existing code. Per direct
  request, this was explicitly scoped out in favor of just the two real
  underlying tools.

**What changed**:
- Every OmniRoute-specific function, config field (`OmniRouteApiKeyEnc`,
  `OmniRouteProviderVerifiedUtc`, `OmniRouteCompressionConfigured`, etc.),
  and CLI param (`-CompressionMode`, `-ReconfigureOmniRoute`) is gone from
  `LLM-TokenOptimizer.ps1` - not disabled, deleted. This includes the local
  gateway server lifecycle, the headless-dashboard-login/API-key machinery,
  MCP registration, and the 1M-context model-catalog resolution /
  `availableModels` picker restriction.
- Claude Code now launches with its own native model defaults. There is no
  gateway to route through, so there's nothing for `-Model sonnet|opus` to
  conflict with - that flag still works, now applied directly in
  `Start-ClaudeSession` instead of inside the deleted OmniRoute env-setup
  function.
- `Install-CavemanPlugin` and `Install-RtkCli` are new functions in the same
  `Install-CompanionTooling` step as `claude-mem`/`headroom`/etc. - same
  install-once-at-user-scope pattern, same best-effort-never-blocks-launch
  behavior, same config-flag tracking (`CavemanInstalled`, `RtkInstalled`).
  A full uninstall (`rm` + `X` at launcher startup) removes both.
- The VS Code extension (`vscode-extension/`) had its `reconfigureOmniRoute`
  command and `compressionMode` setting removed to match - both were
  OmniRoute-specific and have no equivalent for two always-on, install-once
  companion tools.

**Verification performed**: full-script syntax check
(`[System.Management.Automation.Language.Parser]::ParseFile`) after every
edit - zero errors. Swept the whole file for leftover references to every
deleted function/variable name (`OMNIROUTE_ROUTE_CLAUDE`,
`Set-OmniRouteLaunchEnvironment`, `$script:OmniRouteRouted`, etc.) - the only
matches left are inside the historical changelog comment block documenting
past versions, not live code. TypeScript extension compiled clean
(`tsc -p ./`) and its `package.json` re-validated as parseable JSON.
**Not verified**: an actual live run on a real Windows machine (this
environment can't run PowerShell/winget/a real Claude Code install/actual
`rtk init -g` or `claude plugin install` network calls) - the installers for
RTK and Caveman are new code, written to match this script's existing,
previously-verified patterns (`Install-HeadroomStatusline` for the Git-Bash
pattern, `Install-ClaudeCodeSetupPlugin` for the marketplace-plugin pattern)
and checked against each project's real release assets/plugin manifests
before being written, but neither has been exercised end-to-end on a real
machine. Anyone running this should treat the first real launch as the
actual test, and check `%LOCALAPPDATA%\LLM-TokenOptimizer\logs\` if either
install doesn't confirm success.
