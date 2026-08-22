# Work Report — Tab Cleanup, Auto Model Routing, Companion Tooling Fixes

**Prepared for:** external AI review of an autonomous execution session.
**Repo:** `LLM-TokenOptimizer` · **Branch:** `feat/ui-routing-overhaul` (NOT merged to main) · **Base HEAD:** `63d1639`
**Date:** 2026-08-22 · **Executor:** opencode session following a written handoff plan.

This report documents every change made, why it was made, how it was verified, where the executing agent deviated from the plan (and why), and what remains undone. A reviewer should be able to re-run every verification command below and reproduce the evidence.

---

## 1. Final state at a glance

| Item | Baseline (63d1639) | After this work |
|---|---|---|
| Build | succeeds | succeeds (same 2 pre-existing NU1510 warnings; zero new) |
| Tests | 152 pass (46 Core + 26 App + 80 Providers) | **169 pass** (46 + 27 + 96) — +17 new |
| Commits on branch | — | 5 (one per stage, Conventional Commits) |

```
7f311f3 fix: live re-verify companion tools; document all of them in generated CLAUDE.md   (Stage 6 in-repo parts)
50a5275 feat: order ticked models by preset within each provider                           (Stage 5)
2058b70 feat: replace manual Session-type card with automatic live preset routing          (Stage 4)
724ba61 refactor: remove vestigial Session Launch card; relocate Resume/Isolate/Export Handoff (Stage 3)
5aa9017 fix: guard RefreshAllAsync against concurrent cold-launch duplication              (Stage 1)
```

Verification commands:
```
dotnet build app/TokenOptimizer.slnx
dotnet test  app/TokenOptimizer.slnx
git diff 63d1639..HEAD --stat
```

---

## 2. Stage-by-stage detail

### Stage 0 — Baseline (no commit)

Confirmed HEAD `63d1639`, clean tree except untracked `docs/testing/screenshots/captures/` (left alone). Captured baseline build + test results above so later failures could not be blamed on pre-existing state.

### Stage 1 — Cold-launch duplicate-groups race (commit `5aa9017`)

**Root cause (traced in source):** `MainViewModel`'s constructor fires three independent fire-and-forget chains (`MainViewModel.cs:82-86`): a direct `RefreshAllAsync()`, plus `CheckAntigravityLoginAsync()` and `CheckCursorLoginAsync()` which each end in their own `await RefreshAllAsync()` after a CLI-login probe. None were synchronized. `RefreshAllAsync` guarded model-catalog population with a check-then-act race (`if (ModelCatalog.Count == 0)`); two overlapping calls could both observe `Count == 0`, both enter `RefreshModelCatalogAsync`, which does `Clear()` → builds cloud groups synchronously → `await AddUnslothModelGroupsAsync(ticked)` (a real HuggingFace network suspension point). While one call was suspended mid-population, the other's `Clear()` wiped its partial results; neither cleared again afterward, so both independently re-`Add()`ed every group built after the interleave — the doubled "Qwen3.8-27B … every quantization" expander seen in the user's screenshot.

**Fix:** an in-flight-`Task` cache on `RefreshAllAsync` itself (`MainViewModel.cs:549-563`) — not a semaphore, because serializing three callers would mean 3× redundant HF round-trips; caching the in-flight Task lets every caller share one real execution. Body moved verbatim to `RefreshAllCoreAsync` (`MainViewModel.cs:566`). No call sites changed. The lock only guards a synchronous field check/assign (no async deadlock risk).

**Deliberate deviation from plan text:** the plan's snippet kept the method private; it had to become `public` so the regression test can call it directly.

**Regression test** (`app/tests/TokenOptimizer.App.Tests/RefreshAllConcurrencyTests.cs`): constructs a fresh `MainViewModel` (whose constructor itself starts a refresh), then calls `RefreshAllAsync()` 3× concurrently via `Task.WhenAll`, awaits with a 120 s bound, and asserts `ModelCatalogGroups` contains no duplicate group names and is non-empty. This passes regardless of network state (offline still yields the six static provider groups), and fails if two executions ever interleave.

### Stage 2 — KAT-Coder catalog regression-verify (no code change)

Live HF API call confirmed `mudler/KAT-Coder-V2.5-Dev-APEX-GGUF` publishes exactly the 7 `.gguf` files the plan listed; each matches `ApexProfilePattern` (`LlamaCppModel.cs:64`). `QuantAllowlistByRepo` has no curated entry for this repo, so all 7 surface unfiltered (matches the user's explicit "every quantization"). `RecommendedQuant = "I-QUALITY"` pre-ticks correctly. Nothing to change.

### Stage 3 — Remove vestigial Session Launch card (commit `724ba61`)

**Why:** the card duplicated Groq (and every provider) a third time across the Session tab, and its own code comment already said single-provider launch was disconnected from the real path — `LaunchSessionAsync` never read `SelectedProviderName`; it only branched on `SelectedLaunchMode` (Auto/Custom), which is the fallback-chain card's job. Removing it leaves the Models ticklist as the single canonical picker.

Changes:
- Deleted the Session Launch card XAML in full.
- Relocated live inputs rather than deleting them: Resume-mode ComboBox was already present in the Models card (`MainWindow.axaml` launch row); moved the `IsolateClaudeConfig` checkbox into that same row; moved Export Handoff into a new "Session handoff" card on the Dashboard tab (it exports the current project's handoff regardless of launch context).
- Deleted `[RelayCommand] LaunchSessionAsync` entirely.
- Deleted card-only members: `SelectedProviderDescription` property, its assignment inside `OnSelectedProviderNameChanged`, and the now-orphaned `ProviderDescriptions` dictionary.
- Kept everything with surviving consumers (grep-verified before deciding): `_providers` (catalog loop), `ProviderNames`/`SelectedProviderName` (constructor seed + `ApplyIntentPresetAsync` writer + dashboard-refresh hook), `ModelOverride`/`ResolveEffectiveModel()`/`ModelOverrideOptions` (used by master-folder launch ~line 827 and candidates launch ~904).
- Also corrected three strings of stale UI copy that referenced deleted controls ("single provider above", "Provider picker", "as the Provider above").

**Verification:** compiled-binding build passed (Avalonia fails the build on orphaned binding paths — load-bearing here); grep sweep shows zero remaining references to every deleted member; full suite green.

### Stage 4 — Automatic live preset routing replaces the manual card (commit `2058b70`)

The old "Session type" card (Decision-type + Preset dropdowns + Re-apply button) re-ranked a fallback list once per click — not tied to what the user was doing mid-session. Replaced the manual trigger with an automatic one.

**New Providers file `app/src/TokenOptimizer.Providers/Compat/SessionPresetRouter.cs`:**
- `ModelCostTier` enum, `ProviderFitScore` record, `SessionPresetIntent`/`SessionPresetTier`, `SessionPreset` record with `Default = Execution/Balanced` (lines 9-25).
- `SessionPresetStore` (lines 34+): `InferFromPrompt` keyword table (documented verbatim in header), `FilePathFor` reusing `IsolatedClaudeProfileService.GetProfileDirPath` — i.e. the state file lives in the same per-project isolated profile dir the codebase already syncs reliably per-project/per-launch; no new IPC surface invented.
- `SessionPresetRanker.Rank` (line 145): the single ranking algorithm — cost-tier filter by preset (Cost-effective ⇒ Cheap only; Quality ⇒ excludes Cheap; Balanced ⇒ all), sort by ReasoningScore for Planning / SpeedScore for Execution, empty-pool falls back to ranking everything rather than picking nothing.

**MainViewModel wiring:**
- `ProviderFit` table retained as-is but retyped to the shared `ProviderFitScore` (line 301).
- Deleted the four card-fed properties (`SessionIntentNames`, `PresetNames`, `SelectedSessionIntent`, `SelectedPreset`) and their two change handlers — grep-confirmed no other caller first.
- `ApplyIntentPresetAsync` KEPT as the backend (`MainViewModel.cs:324`) per the plan; refactored to read the current preset from `session-preset.json` instead of the deleted combos and to route through `SessionPresetRanker.Rank`. Its manual `[RelayCommand]` trigger removed (the button no longer exists). It runs automatically at the end of refresh **only when a preset file exists** for the selected project — so a decided preset propagates into the custom chain/dashboard, while a user who never engaged routing keeps their hand-dragged chain untouched.
- `ResolveAutoFallbackRouteAsync` (`MainViewModel.cs:1458`) rewritten: builds the available bridgeable candidate list under the existing rate-limit/availability checks, reads `session-preset.json` **per request** (this is what makes routing genuinely live — the `__auto__` id is already re-resolved per request by `UnifiedModelRouter`), ranks via the shared ranker, returns the top-ranked available route. With Claude Code (Reasoning 0.95/Premium) vs OpenCode (0.70/Balanced) as candidates: Quality+Planning → Claude first; Cost-effective+Execution → cost filter empties the pool → fallback ranks by speed → OpenCode first.

**Machine-local files (repo `.claude/` is gitignored — consistent with how the existing graphify hooks are configured here):**
- `.claude/hooks/session-preset-router.ps1` — project-scoped `UserPromptSubmit` hook. Hook mode parses stdin JSON (`prompt`, `cwd`); direct mode takes `-Preset <name>` for the `/preset` command. Header comment documents the keyword table verbatim. Replicates `PathSlug.For` (leaf slug + MD5-8) to land the file exactly where the C# side reads it.
- `.claude/settings.json` — added the `UserPromptSubmit` entry alongside existing graphify `PreToolUse` guards.
- `.claude/commands/preset.md` — `/preset quality|balanced|cost-effective`, bypasses inference.

**Dashboard note added** ("Auto preset routing" card) describing the mechanism and the `/preset` override, per the plan's "mention this in the app".

**Deviations from plan text, disclosed:**
1. Plan said "Keep ModelCostTier enum" *in MainViewModel*. It was relocated to Providers (public) so the ranking math could be single-sourced and tested in Providers.Tests — the alternative was duplicating the algorithm, which the plan explicitly forbade ("reuse … rather than writing a second algorithm"). Same values, same semantics, one home.
2. The state file lives at `%APPDATA%\TokenOptimizer\claude-profiles\{slug}\session-preset.json`. Because `PathSlug.For` hashes the exact normalized path string, forward-slash and backslash inputs hash differently. Verified live: the PowerShell hook fed a backslash cwd writes `llm-tokenoptimizer-e25e93d2`, byte-identical to C# `PathSlug.For` output for the same path; the app always passes `SelectedProject.FullPath` (backslash form on Windows), and Claude Code delivers `cwd` in the same form. The earlier apparent mismatch during testing was a test-input artifact (forward slashes), not a product bug.

**Tests (+13, `SessionPresetRouterTests.cs`):** full keyword table incl. `/plan`, `/build`, architecture/roadmap, research, long-horizon/agentic-workflow, bug/fix/debug, no-match default; missing-file default; write→read round-trip; Quality prefers higher-ReasoningScore; Cost-effective prefers the reverse; Quality filters Cheap behind allowed; plus Stage-5 model tests below.

### Stage 5 — Per-model priority within the ticked set (commit `50a5275`)

Previously `LaunchTickedModelsAsync` picked `bridged[0].ModelId` (the CLI's initial `--model`) by fixed catalog declaration order — unrelated to ticking order or any score.

- Added `ModelFitCatalog.ByModelKey` (`SessionPresetRouter.cs:193`): per-model `ProviderFitScore` keyed `"{provider}::{modelId}"` (same shape as `ProviderModelOptionViewModel.Key`), covering Claude Code's four models and Groq's five, generalizing the pattern already present in `OpenCodeModelCatalog`'s per-model tiers. OpenCode models deliberately have no curated entries — they fall back to provider-level fit, so nothing is dropped.
- Added `SessionPresetRanker.RankModels<T>` generic overload (line 170).
- `LaunchTickedModelsAsync` (`MainViewModel.cs:1352-1363`): resolves the preset from `session-preset.json`, orders the bridged set via `RankModels` with a fit resolver that prefers per-model entries and falls back to `ProviderFit`, and uses `orderedBridged[0].ModelId` as the CLI default. Route construction itself unchanged (`BuildModelRoutesForTickedModels`) — this is pure re-ordering, as the plan specified.

**Nullable-flow note for the reviewer:** the first cut used `SelectedProject?.FullPath` inside the launch body; that null-conditional read reset flow analysis after the method's entry guard (`if (SelectedProject is null) return;`), surfacing CS8602 on a later `SelectedProject.FullPath`. Fixed by using the direct access (the guard makes it provably non-null); build returned to exactly the two pre-existing NU1510 warnings.

**Tests (+3):** Quality orders the higher-reasoning Groq model first within the provider; Cost-effective orders the cheaper one first; unknown model keys fall back to provider fit and are not dropped.

### Stage 6 — Companion tooling fixes

Findings that **contradicted the handoff's premises**, established by direct inspection before touching anything:

| Handoff claimed | Actually true on this machine |
|---|---|
| RTK "hook was never wired", contract "genuinely unconfirmed by name" | RTK binary IS installed (`%LOCALAPPDATA%\rtk\rtk.exe`, v0.45.0); `RtkCliInstalled=True`; but no RTK hook anywhere in global settings.json or its .bak — the installer's `rtk init -g` step evidently didn't persist |
| The four sticky flags (`CavemanPluginInstalled`, `PonytailPluginInstalled`, `ContextModeMcpInstalled`, `RtkCliInstalled`) are all `false` | All four are `true` in `%APPDATA%\TokenOptimizer\config.json` and match reality |
| headroom Windows path bug | Confirmed exactly as described — plus a second latent bug underneath it (see below) |
| claude-mem missing `CLAUDE_CODE_PATH` | Confirmed; real CLI at `C:\Users\ericc\.local\bin\claude.exe`; key name and fallback search paths confirmed against claude-mem's own worker source |

Human confirmation was obtained in-chat for each gated edit (6.1 run `rtk init -g`; 6.2 fix headroom; 6.3 add CLAUDE_CODE_PATH; 6.7 do live re-verification anyway despite flags being correct).

**6.1 RTK (global `~/.claude/settings.json`).** Ran `rtk init -g` — non-interactive mode registered `RTK.md` + the `@RTK.md` include in global CLAUDE.md but defaulted the settings patch to N. Added the documented PreToolUse entry manually: matcher `Bash`, command `C:\Users\ericc\AppData\Local\rtk\rtk.exe hook claude` (full path required — bare `rtk` is not on PATH, so even RTK's own canonical registration would fail at runtime here). Live-verified the hook contract end-to-end: piping PreToolUse JSON for `git status`, `cargo test`, `ls -la`, `dotnet build` returns proper `hookSpecificOutput` rewrites (`rtk git status`, etc.). Known cosmetic wart: rtk's self-check greps settings.json for the literal string `rtk hook claude` and prints a stderr "[rtk] No hook installed" banner because ours uses the full path; the rewrite itself works — functional behavior chosen over matching a self-check heuristic.

**6.2 headroom (`~/.claude/hooks/context-counter.py`).** Root cause as described: Unix `/Users/…` → sanitize → `-Users-…` → `lstrip('-')` → prepend `-` ⇒ `-Users-…` (correct single-dash convention). Windows `C:\Users\…` → `C--Users-…` → lstrip no-op → prepend `-` ⇒ `-C--Users-…` (never exists). Fix branches on drive-letter detection (`^[A-Za-z]:`) and skips the dash prefix. **Second latent bug found and fixed once the path fix exposed real files:** the JSONL read used Python's platform-default encoding (cp1252 here) and threw `UnicodeDecodeError` on UTF-8 transcript content; added `encoding="utf-8"`. Live-verified with properly-escaped JSON payloads: script now finds the real project dir (132 transcripts), reads usage, and wrote `context-tokens-{sid}.json` containing a real percentage (`{"tokens":425737,"pct":42.6}`) — the statusline has data for the first time on this machine.

**6.3 claude-mem (`~/.claude-mem/settings.json`).** Added `"CLAUDE_CODE_PATH": "C:\\Users\\ericc\\.local\\bin\\claude.exe"` (file verified to exist). Key name, tilde-expansion, capability check, and the `~/.local/bin/claude` fallback order were all confirmed against claude-mem's worker source before writing, so this matches what the consumer actually reads.

**6.4 / 6.5** — verified-only, no action, exactly as the plan predicted (context-mode/caveman/ponytail/context7 registered and firing; task-observer/claude-md-management/claude-code-setup correctly manual).

**6.6 CLAUDE.md generator (commit `7f311f3`).** `Set-ProjectClaudeMdDirective`'s heredoc listed only seven tools and omitted RTK, context-mode, caveman and ponytail entirely. Rewritten at the generator (so future projects inherit the correction) with an explicit split: **Hook-automatic** (claude-mem, headroom + session-hygiene guidance, RTK, context-mode, caveman, ponytail, context7) vs **Manual-skill-only** (claude-code-setup, task-observer, claude-md-management), Prompt-cache paragraph retained. The same corrected text hand-applied to this repo's own `CLAUDE.md`.

**6.7 Live re-verification (user chose the preferred option despite flags being correct).** `CompanionToolingInstaller.DescribeActiveCompressionAsync` previously gated every check behind the sticky flags — meaning flag drift in either direction (false hides a real install; true claims a removed one) corrupted reporting, and the flags drift precisely when tools are installed outside the app's own flow. Rewritten to trust nothing sticky: one `claude plugin list` invocation checked against caveman/context-mode/**ponytail (newly reported)**, rtk checked against either its hook script or the installed binary, context7 via one `claude mcp list`. Sticky flags remain solely for the installers' skip-if-done fast path, which is their legitimate use.

### Stage 7 — Live testing pass

Verified headlessly (all reproducible):

| Check | Method | Result |
|---|---|---|
| 4 target models catalogued | source inspection | `claude-sonnet-5` MainViewModel.cs:117; `groq/compound` :122; `mimo-v2.5` OpenCodeModelCatalog.cs:36; KAT-Coder `I-QUALITY` LlamaCppModel.cs:36 |
| Keyword inference, all four named categories + default | executed the actual hook with real payloads through PowerShell | `/plan…`→Planning/Quality; architectural roadmap→Planning/Quality; research→Planning/Quality; long-horizon agentic workflow→Execution/Balanced; fix bug→Execution/Cost-effective; plain→Execution/Balanced |
| `/preset quality\|cost-effective\|balanced` | executed hook direct mode | writes exact tier, bypassing inference |
| State file ↔ app agreement | hook wrote to `llm-tokenoptimizer-e25e93d2`; C# `PathSlug.For` computed identical slug | match |
| RTK rewrite | piped PreToolUse JSON for 4 command families | all rewritten correctly |
| App boots + live connectivity | `dotnet run -- --cli selftest` | claude-sonnet-5 **PONG OK**; groq/* **OK** (with a claude.ai-connectors auth warning); Antigravity PONG OK |

Environment-dependent failures observed in selftest, assessed as **pre-existing conditions, not regressions**: `mimo-v2.5` → HTTP 400 from the Console Go upstream (auth/config issue on the account side; the identical connector warning appears on the passing Groq calls); Unsloth local models → "No running Unsloth server found at :8888" (nothing is listening locally in this environment).

**Explicitly NOT done (cannot be driven headlessly):** interactive GUI launches ticking each model and exercising sessions end-to-end, and flipping `/preset` inside a running CLI window to watch routing flip mid-session. The decision logic those flows depend on is fully unit-tested (Quality vs Cost-effective ordering at both provider and per-model granularity), and the state-file plumbing they depend on is verified live end-to-end — but the final human-in-the-loop pass remains open for the user.

### Stage 8 — MSI release swap (built; upload HELD OFF per your instruction)

- Fresh `app/installer/TokenOptimizer.msi` built via `build-installer.ps1` (publish → vsix → wix): 39,288,832 bytes, timestamped today. `Product.wxs` `Version="1.10.0"` untouched, per plan.
- **Handoff discrepancy found:** the plan said to overwrite asset on release `app-v1.10.0` — that tag does not exist. The real release is tagged **`v1.10.0`** (asset `TokenOptimizer.msi`, 39,276,544 bytes, published 2026-08-21).
- You answered **"No, hold off"** on the upload. Nothing public changed. When you want it: `gh release upload v1.10.0 app/installer/TokenOptimizer.msi --clobber`.

---

## 3. Inventory of every file touched

**In-repo, committed (reviewable via `git diff 63d1639..HEAD`):**
- `app/src/TokenOptimizer.App/ViewModels/MainViewModel.cs`
- `app/src/TokenOptimizer.App/Views/MainWindow.axaml`
- `app/src/TokenOptimizer.Providers/Compat/SessionPresetRouter.cs` *(new)*
- `app/src/TokenOptimizer.Providers/Claude/CompanionToolingInstaller.cs`
- `app/tests/TokenOptimizer.App.Tests/RefreshAllConcurrencyTests.cs` *(new)*
- `app/tests/TokenOptimizer.Providers.Tests/Compat/SessionPresetRouterTests.cs` *(new)*
- `LLM-TokenOptimizer.ps1` (companion-section heredoc only)
- `CLAUDE.md` (companion-tooling section only)

**In-repo, machine-local (`.claude/` is gitignored — mirrors the existing graphify-hooks convention):**
- `.claude/hooks/session-preset-router.ps1` *(new)*
- `.claude/commands/preset.md` *(new)*
- `.claude/settings.json` (UserPromptSubmit entry added alongside graphify PreToolUse)

**Global, outside this repo (each edited only after explicit in-chat confirmation):**
- `~/.claude/settings.json` (RTK PreToolUse Bash hook added)
- `~/.claude/hooks/context-counter.py` (drive-letter branch + utf-8 read)
- `~/.claude-mem/settings.json` (CLAUDE_CODE_PATH added)
- `rtk init -g` side effects: `~/.claude/RTK.md` refreshed, `@RTK.md` include added to `~/.claude/CLAUDE.md`

**Not touched:** `docs/testing/screenshots/captures/` (pre-existing untracked, left alone); `Product.wxs` version; no new tags/releases; nothing pushed; `main` untouched.

---

## 4. Honest limitations & residual risks for the reviewer

1. **Branch not merged.** All work sits on `feat/ui-routing-overhaul`; merging/deleting is left to you.
2. **GUI-only verifications outstanding** (listed in Stage 7): visual confirmation of exactly one expander per group across repeated cold launches; tick-and-launch of each of the four models; mid-session `/preset` flip observed in a live window.
3. **Slug sensitivity:** `PathSlug.For` hashes the raw path string, so a project reached via different separator styles or casing could produce two profile dirs. This predates this work (handoffs and isolated profiles already used it); the router hook inherits the property. Both sides were verified to agree for the canonical Windows backslash form the app actually passes.
4. **Hook scope:** the auto-router hook is project-scoped to this repo by design (per plan), so live keyword routing activates for sessions rooted here, not arbitrary projects launched by the app. The app-side per-request bias still applies everywhere; only the automatic *writing* of the state file is repo-scoped.
5. **RTK cosmetic stderr** (self-check doesn't recognize the full-path registration) — functionally harmless, noted in §Stage 6.
6. **Two pre-existing environment issues** surfaced by selftest (OpenCode mimo-v2.5 upstream 400; no local Unsloth server) were diagnosed and deliberately not "fixed", since they are credential/runtime conditions outside this codebase.
7. **`ApplyIntentPresetAsync` chain-rewrite behavior** now triggers automatically whenever a preset file exists at refresh time. If a user both engages preset routing AND hand-drags the custom chain, the next refresh will reorder the chain to the preset ranking. Judged acceptable (it mirrors what the old Re-apply button did), but flagged here since it's a behavioral change a reviewer may weigh differently.
