# jcode Harness Merge + agency-agents Layer — Full Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Status of this document:** third revision. v1 proposed jcode as one more manual-only adapter. v2, after the user clarified they wanted a deeper merge, proposed treating jcode as a possible full replacement but recommended against it pending verification. v3 (this document) is the result of: (a) a dedicated harness-comparison research pass across the real open-source alternatives, and (b) reading every relevant adapter file in this codebase in full to determine, per-provider, whether merging into jcode is actually safe. **The scope below is narrower than "everything merges" — three of seven candidate providers genuinely merge cleanly; four do not, for specific, evidenced reasons given below.** This is not hedging — it's what the code actually supports once read in full. Where a provider doesn't merge, the reason is stated concretely, not asserted.

---

## Part A — Harness research (why jcode, not something else)

A dedicated research pass compared jcode against every real open-source coding-agent CLI harness discoverable on GitHub, not just the ones jcode's own README benchmarks itself against.

| Harness | License | Windows | Activity | Headless contract | Provider breadth | Verdict |
|---|---|---|---|---|---|---|
| **jcode** (1jehuang/jcode) | MIT | **Native Win32**, no bash required, signed x64/ARM64 installers | Active, daily commits | `docs/WRAPPERS.md` documents `jcode run --json` / `--ndjson` (typed events: `start`, `text_delta`, `tool_start`, `tool_exec`, `done`, `error`), `model/provider/auth list --json`. **No documented exit-code contract** — this is the one open risk, verified empirically in Phase 0 below. | 20+ built-in + arbitrary OpenAI-compatible | **Chosen** |
| **pi** (earendil-works/pi) | MIT | Requires a bash shell (Git Bash/MSYS2/WSL) — not bash-free | Very active, 94.8k★, weekly releases | Best-documented of anything surveyed — explicit `--mode rpc` with strict JSONL framing built for process integration | 30+ providers | Real alternative, independently validated (cited Databricks internal benchmark: highest pass rate on Opus 4.8 among harnesses tested). **Rejected only because of the bash-shell dependency** — our launcher spawns raw Win32 child processes today with zero shell dependency, and adding "requires Git Bash" as a hard requirement is a real cost this plan avoids paying. Documented here as Plan B if jcode's Phase 0 spike fails. |
| **goose** (block/goose) | Apache-2.0 | Native Rust binary | Active | `goose run` with `text`/`json`/`stream-json` formats, `goose session --resume -n <name>` | Broad, extensible | Credible fallback, not chosen (no reason to prefer over jcode once jcode's Windows-native + headless claims check out) |
| **crush** (charmbracelet/crush) | FSL-1.1-MIT (source-available, not OSI-approved) | Cross-platform | Active | Documented `crush run --format json` | Broad | Disqualified — not genuinely open source under a 2-year embargo per release |
| **OpenHands** (All-Hands-AI/OpenHands) | MIT | **WSL required, no native Windows** | Active | Has a headless mode but moot | Broad (LiteLLM) | Disqualified — violates the hard Windows-native requirement |
| **SWE-agent** | MIT | N/A | Moderately active | Batch/benchmark-oriented, no session-resume concept | N/A | Wrong shape — not an interactive daily-driver harness |
| **Plandex** | MIT | Unverified | **~10.5 months stale** | N/A | N/A | Deprioritized as effectively unmaintained |
| **Continue** | Apache-2.0 | N/A | Active | N/A | N/A | IDE-extension platform, not a standalone terminal harness |
| **Codex CLI / OpenCode** | Apache-2.0 / MIT | Native | Active | N/A | N/A | Already integrated today — used as baselines, not migration targets themselves |

**Conclusion: jcode remains the right choice** — the only broadly-capable, actively-maintained, genuinely open-source harness with a bash-free native Windows binary and a written scripting guide. Its self-reported RAM/speed numbers are not independently verified and are irrelevant to this decision either way — what matters is Windows-nativeness and the headless contract, both of which check out from jcode's own docs (`docs/WINDOWS.md`, `docs/WRAPPERS.md`) independent of its marketing claims. The one real gap — no documented exit-code contract — is the first thing Phase 0 verifies empirically before any other code is written.

---

## Part B — Why only 3 of 7 candidate providers actually merge into jcode

The user's original framing was "route everything into jcode... everything else merges." Reading every adapter in full surfaced that most of them are not thin process-spawns — several carry out real, often deliberately-built, sometimes very-recently-shipped logic that jcode cannot replicate, and moving them would be a silent regression, not a merge. Per-provider verdict:

### Merge into jcode (genuinely safe, no feature loss — often a feature *gain*)

- **Antigravity** — `AntigravityAdapter.LaunchSessionAsync` today just resolves `agy.exe` and runs `ProcessLaunchHelper.Start(exe, "\"{projectPath}\"", projectPath)` — a single quoted-path argument, nothing else. It does not honor `SessionResumeMode` or model selection at all (confirmed by reading the file in full). jcode has a native `antigravity`/`google` provider integration. Migrating loses nothing (the current adapter already ignores resume/model) and *gains* both, once Phase 0 confirms jcode's actual flags for them.
- **Cursor** — identical shape to Antigravity: `CursorAdapter.LaunchSessionAsync` runs `cursor-agent "{path}"` with no other flags, no resume/model handling. jcode has a native `cursor` provider integration. Same reasoning, same gain.
- **Codex** — `CodexAdapter.LaunchSessionAsync` is thin (`codex -m {model}`) but currently injects a real stored `OPENAI_API_KEY` from `ProxyCredentialStore` rather than using OAuth-in-app like Antigravity/Cursor. Migrating to jcode's `jcode login --provider openai` converges Codex onto the same opt-in-marker credential pattern the other two already use — a deliberate behavior change (stored-key injection → jcode-native OAuth session), called out explicitly in Task 6 below, not hidden.

### Stay on their dedicated adapter (merging would be a regression)

- **Claude Code** — `ClaudeCodeAdapter` is the deepest integration in the codebase: it *writes* `SKILL.md` files directly (`InstallSkillAsync`), drives `claude plugin marketplace add`/`plugin install --scope`/`plugin list` (verified against actual output, not trusted exit codes), and registers MCP servers via `claude mcp add`. This is what every companion tool in `CompanionToolingInstaller` depends on — claude-mem, caveman, ponytail, context7, task-observer, impeccable, per-language code-intelligence plugins, the whole `EnsureSharedClaudeEnvironmentAsync` apparatus. jcode reading Claude Code's config *files* live (confirmed from its README) is not the same as jcode *executing* Claude Code's plugin/hook runtime — there is no evidence jcode replicates that runtime, and no way to verify it without treating "does jcode run a `.claude/plugins` hook the same way `claude` does" as its own multi-day verification project. Given the size of what's riding on that runtime, this plan does not gamble it. **Claude Code stays exactly as-is.**
- **Groq** — `GroqAdapter` is not a thin spawn. It stands up a local `AnthropicCompatProxy` that translates Groq's OpenAI-compatible API into Anthropic's Messages shape *specifically so it can still launch the real `claude` binary* — the entire point is "get Groq's speed while staying inside genuine Claude Code, with all its plugins/skills, active." Routing Groq through jcode instead would mean leaving the `claude` binary entirely for Groq sessions — the opposite of what this adapter was built to do. **Stays dedicated.**
- **OpenCode** — same shape and same reasoning as Groq: its `AnthropicCompatProxy` (with `anthropicPassthrough: true`) exists specifically to keep sessions inside real `claude`, and additionally works around a Claude Code CLI 2.1.237 model-ID-rewriting bug (fixed this session, per this repo's own recent commit history — `fix: OpenCode passthrough uses x-api-key, not Authorization: Bearer` and the model-force proxy work). This is deliberate, very recently-hardened code. **Stays dedicated.**
- **DeepSeekHarness** — `DeepSeekHarnessAdapter` launches `dsh web --port 3080`, a *browser-based* UI, not a terminal session — it also best-effort packages the project's Claude skills into a pnpm plugin package (`TryInstallSkillsAsNativePluginAsync`) before launch. jcode is a TUI harness; it has no browser-UI-server mode to receive this. There is no jcode equivalent of "spawn a web server and open a browser tab," so there is nothing to merge this into without dropping the feature entirely. **Stays dedicated.**
- **Local Model (Unsloth/llama.cpp)** — `LlamaCppAdapter` is the most sophisticated adapter in the codebase: hardware-aware preset tiers (`LlamaCppDefaultPresets`, built and tested *this session*), a custom `RollingContextProxy` for long-session context management, and two distinct launch paths depending on `RollingContextWindowEnabled`. jcode does support OpenAI-compatible local endpoints (Ollama/LM Studio/vLLM-style), which could in principle receive Unsloth's already-OpenAI-compatible boot output — but that would mean dropping the rolling-context-window feature (or trusting jcode's own unverified context management to replace it) and re-verifying the entire hardware-tier calibration against a different client. That is a real, separate project, not a drop-in swap, and it would discard work shipped in this same session. **Stays dedicated for this plan.** (Flagged as a legitimate future simplification once jcode's local-model behavior is separately, deliberately verified — not attempted here.)

**Net effect:** `FallbackProvider` enum, `FallbackChainResolver`'s structure, the auto/manual chain split, and the `_providers` array size are **unchanged**. Only the concrete class backing three of the eight existing provider slots changes — from `AntigravityAdapter`/`CodexAdapter`/`CursorAdapter` to three differently-configured instances of one new `JcodeHarnessAdapter` class. This is the honest, minimal-blast-radius version of "merge into jcode" once every adapter has actually been read.

---

## Global Constraints

- Windows-only code paths (`[SupportedOSPlatform("windows")]`) throughout `TokenOptimizer.Providers`/`TokenOptimizer.App`, matching every existing adapter.
- No new NuGet packages.
- `dotnet build app/TokenOptimizer.slnx` then `dotnet test app/TokenOptimizer.slnx` green before every commit.
- **No task in Phase 2+ that touches `AntigravityAdapter`/`CodexAdapter`/`CursorAdapter` deletion runs until Phase 0's empirical spike passes for that specific provider.** If jcode's exit-code/login/resume behavior for a given provider doesn't check out, that provider simply stays on its existing dedicated adapter — this plan degrades gracefully per-provider, it does not require an all-or-nothing bet.
- Two wiring sites for every provider-graph change: `app/src/TokenOptimizer.App/ViewModels/MainViewModel.cs` (WPF app) and `app/src/TokenOptimizer.App/Cli/CliHost.cs` (VS Code extension backend) construct the adapter graph independently and must be updated together.
- Reuse existing idioms exactly — do not invent new patterns where one already exists: `ProxyCredentialStore` opt-in-marker gating (`CursorAdapter`'s exact shape), `ProcessLaunchHelper.Start` for spawning (handles the `.cmd`/`.bat` CreateProcess quirk), `SessionHandoffExporter.Export` before every launch, `ProcessSessionHandle(..., watchForRateLimit: true)` for rate-limit detection, sticky `AppConfig` boolean flags for one-time installs, the `TickedModels`-style `List<string>?` + "ticked" pattern for user multi-select.
- `BenchmarkRunner`/`TokenOptimizer.Core.Benchmarking` **does not exist in this codebase** (confirmed via full-repo search — the `graphify` graph's references to it are stale). No task in this plan modifies or depends on it.
- `RateLimitOutcome` is declared at `app/src/TokenOptimizer.Providers/RateLimitOutcome.cs`: `public sealed record RateLimitOutcome(bool RateLimitDetected, DateTimeOffset? ResumeAtUtc);`.

---

## Phase 0: Empirical verification spike (gates everything else)

This phase produces no application code. It produces a findings note (`docs/superpowers/plans/findings/2026-08-21-jcode-spike-findings.md`) that Phase 2+ tasks are read against before they proceed. Do this by hand, on this machine, with a real jcode install — not by re-reading documentation.

### Task 0.1: Install jcode and confirm the Windows-native claim

Steps:
- [ ] 0.1.1 `irm https://jcode.sh/install.ps1 | iex`. Confirm `jcode --version` works from a fresh PowerShell window with no Git Bash / WSL invoked.
- [ ] 0.1.2 Record the installed path (`(Get-Command jcode).Source`) — confirm `ExecutableLocators`-style `ResolveOnPath("jcode")` would find it.

### Task 0.2: Verify the headless JSON contract and exit codes (the one documented-but-unverified risk)

Steps:
- [ ] 0.2.1 Run `jcode --quiet run --json "Reply with exactly: PONG"` against a provider you can already authenticate (pick whichever of Claude/OpenAI/Gemini you have credentials for). Record: exact stdout shape, whether stderr stayed empty, and the **process exit code** on success.
- [ ] 0.2.2 Force a failure case (invalid/no provider configured) and record the exit code and stdout/stderr shape on failure. This is the fact `ModelProbeService`-style pass/fail logic (see Task 0.5) needs and that no documentation currently states.
- [ ] 0.2.3 Force an auth-missing case (`jcode auth status --json` against a provider never logged in) — record shape.
- [ ] 0.2.4 Write all three findings into the findings note verbatim (real command, real output, real exit code — no paraphrasing).

### Task 0.3: Verify Antigravity, Cursor, and Codex specifically through jcode

For each of the three candidate providers:

Steps:
- [ ] 0.3.1 `jcode login --provider antigravity` (or the exact provider id jcode actually uses — confirm via `jcode login` with no args, which the README says lists all providers interactively) using the same account TokenOptimizer's `AntigravityAdapter` currently authenticates against. Confirm it succeeds and confirm `jcode auth status --json` reflects it afterward.
- [ ] 0.3.2 Repeat for Cursor and Codex/OpenAI.
- [ ] 0.3.3 For each, run a real prompt through `jcode run "..."` (interactive bare launch is fine here, doesn't need `--json`) and confirm you actually get a real model response from the expected backend, not a fallback/default provider.
- [ ] 0.3.4 Record, per provider: does `jcode login --provider X` reuse an existing OS-level session (e.g. if Cursor's own desktop app is already signed in) or does it require a fully separate login? This affects whether opting a provider into jcode is a no-op for already-authenticated users or a real extra step — needs to be stated accurately in the UI copy later (Task 6).

### Task 0.4: Verify resume/model flag behavior

Steps:
- [ ] 0.4.1 `jcode --resume <name>` — confirm the exact invocation (README shows `jcode --resume fox`; confirm whether the "name" is something jcode assigns automatically or something you choose, since `SessionResumeMode.Pick` in this codebase means "let the user choose from a picker," not "supply a name up front"). Record whether jcode has an interactive resume-picker reachable non-interactively, or whether `Pick`/`Continue` simply have no clean jcode equivalent.
- [ ] 0.4.2 `jcode --model <id>` or `jcode --provider X --model <id>` — confirm this is accepted for at least one of Antigravity/Cursor/Codex's routed model set.
- [ ] 0.4.3 If Continue/Pick don't map cleanly, that's a real finding, not a blocker — Task 1's `JcodeHarnessAdapter` design already accounts for this (New is fully supported; Continue/Pick degrade to New with a logged note, see Task 1's Interfaces section).

### Task 0.5: Decide the pass/fail gate

Steps:
- [ ] 0.5.1 For each of Antigravity/Cursor/Codex independently: mark PASS (proceed with Tasks 2-7 for that provider) or FAIL (that provider stays on its current dedicated adapter permanently — remove it from Phase 2+ task scope, no further action needed for it). PASS requires: real auth confirmed (0.3), a real response through the expected backend (0.3.3), and a known, non-guessed exit-code convention (0.2) good enough to replace `ProcessSessionHandle`'s existing "did the process start" check.
- [ ] 0.5.2 Commit the findings note. This is the actual deliverable of Phase 0 — do not proceed to Phase 1 without it existing and being read.

---

## Phase 1: `JcodeHarnessAdapter` (generic, provider-parameterized)

### Task 1: Locate the jcode binary

**Files:** Modify `app/src/TokenOptimizer.Providers/Fallback/ExecutableLocators.cs`

Steps:
- [ ] 1.1 Add `FindJcode()`: `new CommandAvailability().ResolveOnPath("jcode")` — PATH-only, matching Task 0.1.2's finding (jcode's installer has no fixed install directory documented, unlike Cursor/Antigravity's known LocalAppData paths).
- [ ] 1.2 Build.

### Task 2: `JcodeHarnessAdapter`

**Files:**
- Create `app/src/TokenOptimizer.Providers/Fallback/JcodeHarnessAdapter.cs`
- Create `app/tests/TokenOptimizer.Providers.Tests/Fallback/JcodeHarnessAdapterTests.cs`

**Interfaces (fill in the exact flag names from Phase 0's findings note before writing code — every flag below is a placeholder pending 0.2-0.4 confirmation, marked `[VERIFY]`):**

```csharp
public sealed class JcodeHarnessAdapter : IProviderAdapter
{
    public JcodeHarnessAdapter(ProxyCredentialStore credentials, FallbackProvider gatingKey, string jcodeProviderId, string displayName);

    public string Name { get; } // displayName, e.g. "Antigravity", "Cursor", "Codex" - UNCHANGED from today's provider names, so no UI/config string changes ripple anywhere else

    public Task<bool> IsAvailableAsync();
    // ExecutableLocators.FindJcode() is not null && credentials.HasCredential(gatingKey)
    // Same opt-in-marker shape CursorAdapter already uses today - the "credential" gates
    // whether the provider is offered, real auth lives entirely inside `jcode login`.

    public Task<IReadOnlyList<string>> ListInstalledSkillsAsync(); // empty array - unchanged from today's Antigravity/Cursor/Codex behavior
    public Task<IReadOnlyList<string>> ListInstalledPluginsAsync(); // empty array - unchanged

    public Task<ProviderResult> InstallSkillAsync(SkillManifest skill);
    // ProviderResult.Fail($"{Name} routes through jcode, which manages its own skills - not wired up here.")
    public Task<ProviderResult> InstallPluginAsync(PluginManifest plugin);
    // ProviderResult.Fail($"{Name} does not host plugins via this adapter.")
    public Task<ProviderResult> RegisterMcpToolAsync(McpToolManifest tool);
    // ProviderResult.Fail($"{Name} MCP registration is not wired up here - jcode reads Claude Code's live MCP config directly.")

    internal static string BuildArguments(string jcodeProviderId, string? model, SessionResumeMode resumeMode);
    // [VERIFY against Phase 0.4] Something in the shape:
    //   $"--provider {jcodeProviderId}" + (model is blank ? "" : $" --model {model}")
    //     + resumeMode switch {
    //         SessionResumeMode.New => "",
    //         SessionResumeMode.Continue or SessionResumeMode.Pick =>
    //           <exact flag from 0.4.1, or "" with a Console log line "jcode: Continue/Pick not
    //           yet mapped, launching New - see docs/superpowers/plans/findings/2026-08-21-jcode-spike-findings.md">
    //       }
    // Unit-testable without a process, exactly like CodexAdapter.BuildArguments today.

    public Task<ISessionHandle> LaunchSessionAsync(SessionLaunchOptions options);
    // Resolve exe via ExecutableLocators.FindJcode() (throw InvalidOperationException if null,
    // same message shape as every existing adapter: "jcode executable not found - install with
    // `irm https://jcode.sh/install.ps1 | iex`.").
    // SessionHandoffExporter.GetEffectiveClaudeConfigDir(options.ProjectPath, options.IsolateConfig)
    // then SessionHandoffExporter.Export(...) - unchanged, every existing adapter does this before
    // launch and jcode reads the resulting CLAUDE.md/AGENTS.md live per its own docs.
    // ProcessLaunchHelper.Start(exe, BuildArguments(jcodeProviderId, options.Model, options.ResumeMode), options.ProjectPath)
    // Wrap: new ProcessSessionHandle(Name, options.ProjectPath, process, watchForRateLimit: true)
}
```

**On `watchForRateLimit: true` and `RateLimitWatcher`:** no changes needed to `RateLimitWatcher.cs`. Its regex (`app/src/TokenOptimizer.Core/RateLimit/RateLimitWatcher.cs`) already includes harness-agnostic phrases — `rate limit reached`, `rate.?limit exceeded`, `usage limit`, `quota exceeded`, `quota reached`, `too many requests`, `HTTP 429`, `\b429\b` — that will still fire regardless of which TUI displays them. Only the Claude-Code-specific phrasing (`5-hour limit reached`, `weekly limit`, `You've hit your (weekly|session) limit`, and the `Stop and wait` menu-detection special case) won't match inside jcode's TUI, meaning a detected-but-unmatched rate limit falls back to the existing generic `ResetTimePattern` parse or the existing 5-hour default — both of which are already the current fallback behavior for any unmatched banner. This is confirmed adequate by reading the regex in full, not assumed — no task needed here.

Steps:
- [ ] 2.1 Fill in every `[VERIFY]` marker above from the Phase 0 findings note.
- [ ] 2.2 Implement `JcodeHarnessAdapter.cs` per the finalized Interfaces.
- [ ] 2.3 `JcodeHarnessAdapterTests.cs`: `BuildArguments_WithModel_IncludesModelFlag`, `BuildArguments_WithNullOrEmptyModel_OmitsModelFlag`, `BuildArguments_NewSession_OmitsResumeFlag`, `BuildArguments_ContinueOrPickSession_<WhateverPhase0Found>` (name depends on 0.4.1's outcome — if Continue/Pick don't map, this test asserts the logged-fallback-to-New behavior instead).
- [ ] 2.4 `dotnet test app/TokenOptimizer.slnx` green.
- [ ] 2.5 Commit: `feat: add JcodeHarnessAdapter (generic jcode-routed provider)`

---

## Phase 2: Wire the three merging providers

Only proceed with a given provider's sub-tasks below if Phase 0.5.1 marked it PASS.

### Task 3: `FallbackChainResolver`

**Files:** Modify `app/src/TokenOptimizer.Providers/Fallback/FallbackChainResolver.cs`

Steps:
- [ ] 3.1 Change the field/constructor-parameter **types** for whichever of `_antigravity`, `_codex`, `_cursor` passed Phase 0 — from `AntigravityAdapter`/`CodexAdapter`/`CursorAdapter` to `JcodeHarnessAdapter`. Field **names** stay identical (`_antigravity`, `_codex`, `_cursor`) — nothing else in this file changes: `AdaptersByName`, `ResolveAsync`, `ResolveCustomAsync`, and `DescribeChainAsync`'s auto/manual-only split all already operate purely through `.Name`/`.IsAvailableAsync()`, both on the `IProviderAdapter` interface both old and new classes implement identically. Antigravity keeps its position in the **auto** chain (`ResolveAsync`'s second check); Codex/Cursor keep their position in the **manual-only** `DescribeChainAsync` block. No chain-ordering or auto/manual-classification logic changes at all.
- [ ] 3.2 Build (breaks `MainViewModel.cs`/`CliHost.cs` construction for whichever providers changed type — expected, fixed in Task 4).

### Task 4: Wire into both app entry points

**Files:**
- Modify `app/src/TokenOptimizer.App/ViewModels/MainViewModel.cs`
- Modify `app/src/TokenOptimizer.App/Cli/CliHost.cs`

Steps:
- [ ] 4.1 For each PASSing provider, replace e.g. `_antigravityAdapter = new AntigravityAdapter(_credentials);` with `_antigravityAdapter = new JcodeHarnessAdapter(_credentials, FallbackProvider.Antigravity, jcodeProviderId: "antigravity" /* [VERIFY exact id from 0.3.1] */, displayName: "Antigravity");` — field name (`_antigravityAdapter`), `_providers` array position, and `ProviderNames` display string all stay byte-identical, so nothing downstream (UI bindings, `LaunchSelectedCandidatesAsync`, the CLI's `providers` command) needs to change.
- [ ] 4.2 Repeat in `CliHost.cs` for the same providers, same variable names.
- [ ] 4.3 `dotnet build app/TokenOptimizer.slnx` — both projects compile.
- [ ] 4.4 Commit: `feat: route <passing providers> through JcodeHarnessAdapter`

### Task 5: Manual verification per merged provider

Steps (repeat per PASSing provider):
- [ ] 5.1 Opt in (existing credential-marker mechanism, unchanged — see Task 6 for Codex's semantic note).
- [ ] 5.2 Launch a session against a real project folder. Confirm jcode's TUI opens in that directory with AGENTS.md/CLAUDE.md handoff context present, and confirm it's actually talking to the expected backend account (not a default/wrong provider).
- [ ] 5.3 Confirm `FallbackChain`/`ProviderNames` in the UI still shows the same provider name, same position, same auto/manual classification as before this change.
- [ ] 5.4 For Antigravity specifically (still in the auto chain): confirm `FallbackChainResolver.ResolveAsync()` still correctly falls through to it when Claude Code is unavailable/rate-limited, exactly as before.

### Task 6: Codex's credential-semantics note (no code change required, but must be documented)

Codex today stores a **real `OPENAI_API_KEY`** via `ProxyCredentialStore.SetCredential(FallbackProvider.Codex, apiKey)`, read back via `GetCredentialPlainText` and injected as an env var. After migration, `JcodeHarnessAdapter` only calls `HasCredential(FallbackProvider.Codex)` — it never reads the stored value, because jcode manages its own OpenAI OAuth session via `jcode login --provider openai`. This is **backward compatible with zero migration step**: an existing stored Codex API key still makes `HasCredential` return true (it only checks file existence), so nothing breaks for users who already opted in — TokenOptimizer just stops reading the value. New users opt in the same way Antigravity/Cursor already do (Task 6 in the earlier additive-only draft of this plan identified where that opt-in UI/CLI path lives — locate it via `graphify query "how does the Cursor opt-in credential get set"` before touching it, not yet confirmed which file owns this).

Steps:
- [ ] 6.1 Update the opt-in UI/CLI copy for Codex (wherever Task 6's `graphify query` above points) to say "requires `jcode login --provider openai`" instead of "requires an OpenAI API key," if that copy exists and names the old requirement.
- [ ] 6.2 Commit: `docs: clarify Codex opt-in now runs through jcode's own OAuth`

### Task 7: Retire the fully-replaced adapters (only for providers that passed AND were manually verified)

**Files (delete only what Task 5 fully verified):**
- `app/src/TokenOptimizer.Providers/Fallback/AntigravityAdapter.cs` (+ any dedicated test file, if one exists — none was found in this pass, only `CodexAdapterTests.cs` exists among the three)
- `app/src/TokenOptimizer.Providers/Fallback/CodexAdapter.cs` + `app/tests/TokenOptimizer.Providers.Tests/Fallback/CodexAdapterTests.cs`
- `app/src/TokenOptimizer.Providers/Fallback/CursorAdapter.cs`
- Remove the now-dead `ExecutableLocators.FindAntigravity()`/`FindCodex()`/`FindCursor()` methods for whichever adapters were fully retired — availability is now `FindJcode()` + credential marker, the old binary-discovery methods have no remaining caller.

Steps:
- [ ] 7.1 Delete only the files for providers that passed Phase 0 AND completed Task 5 manual verification. A provider that passed Phase 0 but hasn't been manually verified yet keeps its old adapter file until it has.
- [ ] 7.2 Remove now-orphaned `ExecutableLocators` methods; build; confirm no remaining references (a stray `agy`/`cursor-agent`/`codex.cmd` path-resolution call left dangling would be exactly the kind of stub this plan is required to avoid).
- [ ] 7.3 `dotnet test app/TokenOptimizer.slnx` green.
- [ ] 7.4 Commit: `refactor: retire AntigravityAdapter/CodexAdapter/CursorAdapter, fully replaced by JcodeHarnessAdapter`

This task is deliberately last and deliberately gated — deleting working code before its replacement is proven in Task 5 is exactly the kind of mistake the "no mistakes" requirement rules out.

---

## Phase 3: agency-agents (companion-tooling layer on Claude Code — unaffected by the jcode work above)

agency-agents (github.com/msitarzewski/agency-agents, MIT) is a library of ~250 Claude Code subagent `.md` files organized into ~24 divisions (engineering, design, marketing, finance, ...). Since Claude Code stays on its dedicated adapter (Part B above), this phase is entirely independent of Phases 0-2 and can be built in parallel.

**Research summary:** Agent files are plain `.md`: YAML frontmatter (`name`, `description`, `color`, `emoji`, `vibe`) + a prose system prompt — Claude Code's native subagent file format, confirmed by reading `engineering/engineering-code-reviewer.md` in full, nothing to transform. `divisions.json` is the source of truth for the division set. The repo's own `scripts/install.sh`'s `install_claude_code()` function does exactly this, confirmed by reading it in full: for each selected division directory, copy every `*.md` file flat into `resolve_dest claude-code "$HOME/.claude/agents"`, where `resolve_dest` honors a `CLAUDE_CONFIG_DIR` override — the **exact same environment variable** `CompanionToolingInstaller.GetClaudeConfigDir()` already reads. Zero path-convention friction.

### Task 8: `AgencyAgentsInstaller` — fetch + catalog

**Files:**
- Create `app/src/TokenOptimizer.Providers/Claude/AgencyAgentsInstaller.cs`
- Modify `app/src/TokenOptimizer.Core/Models/AppConfig.cs`

**Interfaces:**
- `AppConfig`: add `public bool AgencyAgentsCloned { get; set; }` (sticky flag, matches every other one-time-setup flag already in this file — `ClaudeMemInstalled`, `HeadroomInstalled`, etc.) and `public List<string>? TickedAgencyAgents { get; set; }` (division-qualified slugs, e.g. `"engineering/engineering-code-reviewer"` — matches `TickedModels`' `"Provider::Model"` key shape exactly).
- `AgencyAgentsInstaller(ConfigStore configStore, CommandAvailability availability)`.
- `EnsureClonedAsync()` → shallow-clones `https://github.com/msitarzewski/agency-agents.git` into `%USERPROFILE%\.tokenoptimizer\agency-agents` if missing (`git pull --quiet --ff-only` if present; on pull failure, delete and re-clone rather than reconcile a partial checkout — this is `CompanionToolingInstaller.InstallImpeccableSkillAsync`'s exact clone/repair logic, read in full and reused verbatim, not reinvented). Sets `AgencyAgentsCloned`. Best-effort: missing `git` or clone failure returns false, never blocks a launch (same contract every other `CompanionToolingInstaller` method already follows).
- `ListAvailableAgentsAsync()` → parses `divisions.json` (division → label/icon/color) plus every `*.md` file with YAML frontmatter under each division directory, returns `IReadOnlyList<AgencyAgentInfo>` where `AgencyAgentInfo` is `public sealed record AgencyAgentInfo(string Division, string Slug, string Name, string Description)`. Parsing: split on the `---` frontmatter fences, minimal hand-rolled scalar-key extraction for `name:`/`description:` — the frontmatter is flat scalars only (confirmed directly from the real `engineering-code-reviewer.md` frontmatter read during research), no YAML library needed.
- `SyncTickedAgentsAsync(IReadOnlyList<string> tickedSlugs)` → resolves the Claude config dir exactly as `CompanionToolingInstaller.GetClaudeConfigDir()` does (`CLAUDE_CONFIG_DIR` env var, else `~/.claude`), copies each ticked agent's `.md` file into `{claudeConfigDir}/agents/`, and **removes any previously-synced agency-agents file that is no longer ticked** — track synced filenames in `agents/.agency-agents-synced.json` alongside the agents dir so unticking an agent actually uninstalls it rather than leaving an orphaned file with no way to remove it.

Steps:
- [ ] 8.1 Read `CompanionToolingInstaller.InstallImpeccableSkillAsync` (clone/repair pattern) and `GetClaudeConfigDir` in full before writing anything — reuse both exactly.
- [ ] 8.2 Add the two `AppConfig` fields.
- [ ] 8.3 Implement `EnsureClonedAsync`, `ListAvailableAgentsAsync`, `SyncTickedAgentsAsync` per the Interfaces above.
- [ ] 8.4 Unit tests in `app/tests/TokenOptimizer.Providers.Tests/Claude/AgencyAgentsInstallerTests.cs`: frontmatter parser against fixture strings including the real `engineering-code-reviewer.md` frontmatter captured during research; sync/un-sync manifest bookkeeping against a temp directory (inject the clone directory path so tests point at a fixture folder — no real git/network calls in unit tests).
- [ ] 8.5 `dotnet test app/TokenOptimizer.slnx` green. Commit: `feat: add AgencyAgentsInstaller for syncing agency-agents subagents into Claude Code`

### Task 9: Wire into the shared Claude environment + UI selection

**Files:**
- Modify `app/src/TokenOptimizer.Providers/Claude/CompanionToolingInstaller.cs`
- Modify `app/src/TokenOptimizer.App/ViewModels/MainViewModel.cs`

Steps:
- [ ] 9.1 Read `CompanionToolingInstaller`'s current constructor in full before editing, so the new `AgencyAgentsInstaller` dependency lands alongside the existing `ConfigStore`/`ClaudeExecutableLocator`/`CommandAvailability`/`PythonLocator` dependencies rather than being bolted on awkwardly. In `EnsureSharedClaudeEnvironmentAsync`, after the existing calls, add: `await _agencyAgents.EnsureClonedAsync(); await _agencyAgents.SyncTickedAgentsAsync(tickedSlugs);` — this is the single call site both `MainViewModel` and `CliHost` already route through before any Claude-binary launch, so both app entry points pick this up automatically, no second wiring site needed (unlike the jcode changes in Phase 2, which need both sites touched explicitly because `FallbackChainResolver` construction is duplicated between them).
- [ ] 9.2 `MainViewModel.cs`: add an `ObservableCollection<AgencyAgentCatalogEntry>` (division, name, description, `IsTicked`) populated from `ListAvailableAgentsAsync()` on startup — mirror `ModelCatalog`'s exact shape (`IsTicked` property, toggle handler persisting to `config.TickedAgencyAgents` the same way `SaveTickedModelsAsync` persists `config.TickedModels`) so this is recognizably the same selection model already in the app, not a new one.
- [ ] 9.3 Build + test green. Commit: `feat: surface agency-agents division/agent picker using the existing ticked-selection pattern`

### Task 10: Manual verification

Steps:
- [ ] 10.1 Launch the app, tick agents across 2+ divisions (e.g. `engineering-code-reviewer`, `design-ui-designer`).
- [ ] 10.2 Launch a Claude Code session; confirm `~/.claude/agents/` (or `%CLAUDE_CONFIG_DIR%/agents/` if set) contains exactly the ticked files.
- [ ] 10.3 Untick one, relaunch, confirm that file is removed and the rest remain.
- [ ] 10.4 Confirm a completely fresh checkout (no prior `AgencyAgentsCloned` flag, nothing ticked) clones the catalog correctly on first launch and installs zero agent files — populating the picker must not require anything to already be ticked.

---

## Explicitly out of scope (and why)

- **Claude Code, Groq, OpenCode, DeepSeekHarness, and Local Model (Unsloth) merging into jcode** — see Part B. Each carries real, evidenced functionality jcode cannot currently replicate without its own separate, deliberate verification project.
- **jcode's Swarm / multi-agent mode, semantic memory graph** — internal to jcode's own process, no launcher-level hook exists to call into.
- **agency-agents for non-Claude-Code tools** (Cursor, Copilot, Gemini CLI, etc. — the repo's own `install.sh` supports these too) — out of scope because none of TokenOptimizer's other adapters sync any content into their targets today (Codex/Cursor's `InstallSkillAsync` already refuse, for the same reason); adding this would be new capability those adapters don't have, not "layering onto the existing Claude Code CLI" as asked.
- **MCP config sync (`~/.jcode/mcp.json`)** — jcode already reads Claude Code's live MCP config directly per its own docs; nothing to build.
- **pi as the chosen harness** — documented in Part A as the honest runner-up, not pursued because of its bash-shell dependency on Windows. Revisit only if Phase 0 fails for jcode.
