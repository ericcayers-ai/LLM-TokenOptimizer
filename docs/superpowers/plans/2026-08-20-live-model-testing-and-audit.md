# Live Model E2E Testing & Full System Audit — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every model selectable in the app UI returns text end-to-end; cross-provider features (handoff, autocompact, skills/plugins, session continuity) verified consistent; every file in `app/` and `vscode-extension/` audited; everything fixable fixed with tests.

**Architecture:** A permanent headless probe capability built into the app (`ModelProbeService` + `--cli test-model` / `--cli selftest`) reusing the exact env/arg construction the launch adapters use, so a passing probe proves the real launch path works. Live matrix run through it, failures fixed, audit findings cleaned, all guarded by a new `TokenOptimizer.App.Tests` project.

**Tech Stack:** .NET 10 SDK (10.0.200 installed), xUnit, Avalonia 12.1.1 app, claude CLI (`~/.local/bin/claude.exe`), unsloth CLI (`~/.unsloth/studio/bin/unsloth.exe`), groq/opencode credentials in `ProxyCredentialStore`, TypeScript/mocha (vscode-extension).

## Global Constraints

- Windows-only code paths (`[SupportedOSPlatform("windows")]` pattern used throughout); PowerShell 5.1 semantics for shell commands (no `&&`).
- Never commit secrets: `ProxyCredentialStore` uses DPAPI; probes must not print tokens — redact auth env values in probe output.
- Live probes spend real API credits (Groq x5 models, OpenCode zen x1, Anthropic x1) — keep prompts 1-token-cheap (`"Reply with exactly: PONG"`), single attempt per model, 120s timeout each.
- Local Unsloth probes boot real GGUF servers (13 GB + 21 GB models) — boot timeout 90s (matches LlamaCppAdapter), one model at a time.
- Every task: `dotnet build app/TokenOptimizer.slnx` then `dotnet test app/TokenOptimizer.slnx` green before commit. No emoji in files. No comment additions unless asked.
- Tests that hit the network are marked `[Trait("Category","Live")]` and excluded from default `dotnet test` runs via `--filter` (default run must stay offline-deterministic).

---

## Phase 1 — Headless test infrastructure

### Task 1: Create `TokenOptimizer.App.Tests` + first CliHost tests

**Files:**
- Create: `app/tests/TokenOptimizer.App.Tests/TokenOptimizer.App.Tests.csproj`
- Create: `app/tests/TokenOptimizer.App.Tests/CliHostArgParsingTests.cs`
- Modify: `app/TokenOptimizer.slnx` (add project)
- Reference: `app/src/TokenOptimizer.App/Cli/CliHost.cs` (371 lines, currently 0% covered — read fully before writing tests)

**Interfaces:**
- Produces: test project all later App-level tasks add tests into; `dotnet test app/TokenOptimizer.slnx` runs it.

Steps:
- [ ] 1.1 Create csproj mirroring `TokenOptimizer.Core.Tests.csproj` (read it first): net10.0, xUnit, `ProjectReference` to `TokenOptimizer.App.csproj`.
- [ ] 1.2 Add to `TokenOptimizer.slnx` following the existing `<Project ...>` entries' pattern.
- [ ] 1.3 Read `CliHost.cs` in full; write arg-parsing tests for every existing command (`status, providers, launch, install-dependencies, install-companion-tooling, reset-config, uninstall, master-folder-set, master-folder-list, create-project, history, add-project, set-credential, opt-in, export-handoff, mcp-rag-server`): valid invoke -> exit code + JSON `{ok,...}` shape on stdout; unknown command -> `{ok:false}` + exit 1; missing required arg -> error names the arg. Tests that would spawn processes/CLIs mock via the seam identified in 1.3 (if `CliHost` constructs processes directly, first extract an injectable `Func<string,(int,string)>` runner — keep the extraction minimal and covered by these same tests).
- [ ] 1.4 `dotnet test app/TokenOptimizer.slnx` — all green.
- [ ] 1.5 Commit: `test: add TokenOptimizer.App.Tests with CliHost arg-parsing coverage`

### Task 2: Extract shared `ClaudeLaunchEnvironment` builder

**Files:**
- Create: `app/src/TokenOptimizer.Providers/Claude/ClaudeLaunchEnvironment.cs`
- Modify: `app/src/TokenOptimizer.Providers/Claude/ClaudeCodeAdapter.cs` (L113-150)
- Modify: `app/src/TokenOptimizer.Providers/Fallback/GroqAdapter.cs` (L81-112)
- Modify: `app/src/TokenOptimizer.Providers/Fallback/OpenCodeAdapter.cs` (L67-92)
- Test: `app/tests/TokenOptimizer.Providers.Tests/ClaudeLaunchEnvironmentTests.cs`

**Rationale:** Probes must use byte-identical env construction to the real launch path, so extract it once instead of duplicating it a 5th time.

**Interfaces:**
- Produces: `public sealed record ClaudeLaunchEnvironment(IReadOnlyDictionary<string,string> Env, string Arguments)`; `public static ClaudeLaunchEnvironmentBuilder` with the same options each adapter passes today (`--model`, `--continue/--resume`, `ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN`, `CLAUDE_MEM_WORKER_PORT`, `CLAUDE_MEM_DATA_DIR`, `CLAUDE_CONFIG_DIR` when isolating).

Steps:
- [ ] 2.1 Read all four adapter launch methods in full; write failing tests first asserting the exact env/arg maps each adapter currently produces (golden values copied from current code: claude-mem port 37778 wiring from CompanionToolingInstaller.cs:42-44, base URLs from GroqAdapter.cs:96-104 / OpenCodeAdapter.cs:80-88, isolation from ClaudeCodeAdapter.cs:142-146).
- [ ] 2.2 Implement the builder; rewire the four adapters to call it. No behavior change — golden tests prove it.
- [ ] 2.3 Full build + tests green; commit `refactor: extract shared Claude launch env construction`

### Task 3: `ModelProbeService`

**Files:**
- Create: `app/src/TokenOptimizer.Core/Diagnostics/ModelProbeService.cs`
- Create: `app/src/TokenOptimizer.Core/Diagnostics/ProbeResult.cs`
- Test: `app/tests/TokenOptimizer.Core.Tests/ModelProbeServiceTests.cs`

**Interfaces:**
- Consumes: `ClaudeLaunchEnvironment` (Task 2), `ClaudeExecutableLocator`, `ExecutableLocators`, `ProxyCredentialStore`.
- Produces:
```csharp
public sealed record ProbeResult(bool Ok, string Provider, string Model, string ResponseText,
    int LatencyMs, string? Error, bool Skipped = false, string? SkipReason = null);

public sealed class ModelProbeService
{
    public Task<ProbeResult> ProbeAsync(string providerName, string model, string? projectPath, CancellationToken ct);
    public Task<IReadOnlyList<ProbeResult>> ProbeAllAsync(IEnumerable<(string provider, string model)> matrix, CancellationToken ct);
}
```
- Probe mechanics per provider (all non-interactive, 120s timeout, stdout/stderr captured, auth values redacted in `Error`):
  - **Claude Code**: `claude -p "<prompt>" --model <id>` with claude-mem env from Task 2, no `--continue`.
  - **Groq**: start `AnthropicCompatProxy` (same instantiation as GroqAdapter.cs:96-104), probe `claude -p --model <id>` through it, dispose proxy after.
  - **OpenCode**: env `ANTHROPIC_BASE_URL=https://opencode.ai/zen/go` + stored credential token exactly as OpenCodeAdapter.cs:80-90 does; probe `claude -p --model opencode-go` (exact id read from `OpenCodeModelCatalog.ModelIds`).
  - **Unsloth (local)**: replicate `LlamaCppAdapter.LaunchWithRollingContextAsync` boot (LlamaCppAdapter.cs:174-224): `unsloth start claude --model <repo:quant> --max-seq-length 131072 --no-launch --serve`, 90s boot timeout, regex-parse `ANTHROPIC_BASE_URL`+auth from boot output (L255-262 pattern), probe `claude -p` directly against that base URL, then kill server process. One at a time.
  - **Antigravity**: locate via `ExecutableLocators.FindAntigravity()`; if null or no stored opt-in credential -> `Skipped=true`. If present: run `agy --version` (smoke), then attempt `agy -p "<prompt>"` only if `agy --help` shows a print/non-interactive flag.
- [ ] 3.1 Failing tests first: fake process runner asserting timeout handling, empty-response failure, redaction of `ANTHROPIC_AUTH_TOKEN` values in error strings, skip logic for Antigravity-missing.
- [ ] 3.2 Implement; tests green; commit `feat: headless ModelProbeService for live model verification`

### Task 4: CLI commands `test-model` + `selftest`

**Files:**
- Modify: `app/src/TokenOptimizer.App/Cli/CliHost.cs`
- Create: `app/src/TokenOptimizer.Core/Diagnostics/SelftestMatrix.cs`
- Test: `app/tests/TokenOptimizer.App.Tests/CliHostSelftestTests.cs`

**Interfaces:**
- `--cli test-model --provider <name> --model <id> [--project <path>]` -> `{ok, data:{...ProbeResult}}`, exit 0/1.
- `--cli selftest [--project <path>]` -> runs the full matrix, prints one JSON report + summary table, exit 0 iff all non-skipped probes pass.
- Matrix (hardcoded in `SelftestMatrix.cs`, single source of truth):
```
Claude Code: claude-sonnet-5
Groq: openai/gpt-oss-120b, openai/gpt-oss-20b, qwen/qwen3.6-27b, groq/compound, groq/compound-mini
OpenCode: opencode-go (catalog spelling)
Unsloth (local model): unsloth/Qwen3.8-27B-GGUF:UD-IQ4_XS, mudler/KAT-Coder-V2.5-Dev-APEX-GGUF:I-QUALITY
Antigravity: gemini-3-pro, gemini-3-pro-high (probe = agy smoke per Task 3 rules)
```
- [ ] 4.1 Failing tests: command parsing, unknown provider error, report JSON shape.
- [ ] 4.2 Implement; green; commit `feat: add test-model and selftest CLI commands`

### Task 5: Feature-consistency probes

**Files:**
- Create: `app/src/TokenOptimizer.Core/Diagnostics/FeatureProbeService.cs`
- Test: `app/tests/TokenOptimizer.Core.Tests/FeatureProbeServiceTests.cs`, `app/tests/TokenOptimizer.Providers.Tests/RollingContextProxyTrimTests.cs`

- [ ] 5.1 **Session continuity probe**: `claude -p "Remember the codephrase <random-uuid>. Reply OK"` then `claude --continue -p "What was the codephrase? Reply with it only."` -> assert uuid round-trips. Run for Claude native and one bridged provider (Groq default).
- [ ] 5.2 **Shared skills/plugins probe**: for each of Claude/Groq/OpenCode/Unsloth envs, run `claude plugin list` (subprocess) + enumerate skills dir; assert all four produce identical plugin sets and identical skill id sets.
- [ ] 5.3 **Export handoff probe**: `--cli export-handoff --project <tmp project with a prior transcript>` -> assert `.claude-handoff/session-handoff.md` exists, non-empty, contains transcript tail + skills digest; assert `AGENTS.md` gains the marker reference.
- [ ] 5.4 **Autocompact**: unit-test `RollingContextProxy.ApplyRollingWindow` with synthetic >budget body: oldest messages dropped, compaction marker inserted, orphaned tool_result re-paired, newest message retained. Golden cases: under-budget passthrough, exactly-at-budget, tool_use/tool_result spanning the cut. Live smoke (Live trait): single Unsloth probe with context-bloating preamble through rolling proxy -> response still arrives.
- [ ] 5.5 All offline tests green; commit `feat: feature-consistency probes (session, skills/plugins, handoff, autocompact)`

---

## Phase 2 — Live run

### Task 6: Run the matrix, Antigravity full attempt, record report

- [ ] 6.1 `dotnet build app/TokenOptimizer.slnx -c Release`; rebuild `app/publish/app/TokenOptimizer.App.exe` using the existing publish procedure.
- [ ] 6.2 **Antigravity full attempt**: run `ExecutableLocators.FindAntigravity()` logic manually (`where.exe agy`, standard install dirs). If missing: search for winget/npm install source; ask before installing. If install requires interactive OAuth: install, prompt user to run `agy` login, store opt-in credential via `--cli set-credential`. If no non-interactive mode: record launch-path smoke + `manual-verify`.
- [ ] 6.3 Run `TokenOptimizer.App.exe --cli selftest`. Capture full JSON.
- [ ] 6.4 Write results to `docs/testing/selftest-2026-08-20.md`: matrix table (ok/latency/error per model), feature-probe results, Antigravity outcome.
- [ ] 6.5 Commit: `docs: record live selftest results`

---

## Phase 3 — Fixes (everything fixable)

### Task 7: vscode-extension provider-id drift (live bug)

**Files:** Modify `vscode-extension/src/extension.ts:125,252,375` (`"LM Studio (local)"` -> `"Unsloth (local model)"`); Extend `vscode-extension/src/test/suite/extension.test.ts`; Modify `vscode-extension/readme.md`.

- [ ] 7.1 Replace all three occurrences (grep to confirm exactly 3 first).
- [ ] 7.2 Add a mocha test that greps `extension.ts` for provider-id literals and asserts each exists in the app's provider registry (`--cli providers` output).
- [ ] 7.3 `cd vscode-extension; npm install; npm test` green. Commit `fix: extension used removed 'LM Studio (local)' provider id`.

### Task 8: CodexAdapter ignores selected model

**Files:** Modify `app/src/TokenOptimizer.Providers/Fallback/CodexAdapter.cs:42-54`; Test `app/tests/TokenOptimizer.Providers.Tests/CodexAdapterTests.cs`.
- [ ] 8.1 Read the file; failing test: `LaunchSessionAsync` with `Model="gpt-5.1-codex"` must pass `-m gpt-5.1-codex` (verify against `codex --help`).
- [ ] 8.2 Implement, green, commit `fix: CodexAdapter now passes selected model to codex CLI`.

### Task 9: Handoff exporter misses isolated-profile transcripts

**Files:** Modify `SessionHandoffExporter.cs` call sites: `AntigravityAdapter.cs:51`, `CodexAdapter.cs:49`, `CursorAdapter.cs:48`, `DeepSeekHarnessAdapter.cs:95`, `MainViewModel.ExportHandoffAsync` (MainViewModel.cs:1282-1299), `CliHost.cs:326-335`.
- [ ] 9.1 Each caller passes its effective claude config dir. Tests per adapter shape in `TokenOptimizer.Providers.Tests`.
- [ ] 9.2 Green; commit `fix: handoff export reads isolated-profile transcripts`.

### Task 10: Skill catalog / skills digest ignore `CLAUDE_CONFIG_DIR`

**Files:** Modify `SkillCatalogService.cs:27`, `SessionHandoffExporter.GetAvailableSkillsDigest` (SessionHandoffExporter.cs:99).
- [ ] 10.1 Failing tests: with `CLAUDE_CONFIG_DIR` pointing at temp profile with skill, both services must see it (falling back to `~/.claude` when unset). Implement, green, commit `fix: skill catalog and handoff digest honor CLAUDE_CONFIG_DIR`.

### Task 11: Model catalog corrections from live results

**Files:** Modify `MainViewModel.StaticModelCatalog` (MainViewModel.cs:112-131), `OpenCodeModelCatalog`, `GroqModelCatalog` if implicated.
- [ ] 11.1 For every failing model id: verify correct current id at provider (Groq GET /openai/v1/models, Anthropic docs, zen catalog). Only replace confirmed-live ids with verification date comments.
- [ ] 11.2 Re-run affected probes -> green. Commit `fix: refresh model catalog ids from live verification`.

### Task 12: `plugin marketplace update` consistency

**Files:** Modify `MainViewModel.cs` launch paths — `LaunchTickedModelsAsync` unified-router branch (L1454-1489) and `LlamaCppAdapter` rolling path (LlamaCppAdapter.cs:174-224).
- [ ] 12.1 Read current paths; make plugin marketplace update consistent (either all paths skip-when-node-exe or all paths run — pick one, document choice). Test via fake runner.
- [ ] 12.2 Green; commit `fix: consistent plugin marketplace refresh across all launch paths`.

### Task 13: Isolated-profile skill/plugin drift

**Files:** Modify `IsolatedClaudeProfileService.cs` (L18-46); Test in `TokenOptimizer.Providers.Tests`.
- [ ] 13.1 Failing test: seed profile, add new skill to `~/.claude/skills`, launch again -> new skill present in isolated profile. Implement re-sync (copy skills/ + plugin config that currently drift). Keep change minimal.
- [ ] 13.2 Green; commit `fix: isolated profiles re-sync shared skills on launch`.

### Task 14: Stale-reference cleanup across app + extension

**Files:** `MainWindow.axaml:273`, `HardwareInfo.cs:6-9`, `LocalModelContextPreset.cs:4-7`, `CompanionToolingInstaller.cs:428,503`, `GroqModelCatalog.cs:14`, `LlamaCppAdapter.cs:16,21,24`, `LlamaCppRagService.cs:16`, `LlamaCppPresetStore.cs:9`, `MainViewModel.cs:472`, `dashboard.ts:19`, `readme.md:8,53,56,68`, `package.json` model-setting description, root `README.md` (L67-74).
- [ ] 14.1 For each: read context, update text to v6 reality (Unsloth replaces LM Studio; no benchmark subsystem; no .ps1 product). Keep `CompanionUninstaller.cs` OmniRoute references (intentional cleanup).
- [ ] 14.2 Root README table rewritten to v6 reality.
- [ ] 14.3 Build + extension compile + tests green; commit `docs: purge stale LM Studio/benchmark/PowerShell references`.

### Task 15: Junk-file full sweep

**Files:** Create `docs/benchmarks/README.md`; Move 22x `benchmark_*.json`, `benchmark_summary.{json,csv}`, `BENCHMARK_REPORT.md`, `generate_report.py`, `merge_quality.py`, 9x `run_benchmarks*.log` -> `docs/benchmarks/`; Delete `vscode-extension/llm-token-optimizer-5.9.2.vsix`; Modify `.gitignore`.
- [ ] 15.1 Full sweep: `git ls-files` + disk listing; flag anything ambiguous -> ask.
- [ ] 15.2 Execute moves (`git mv`), deletions (`git rm`), `.gitignore` entries (`__pycache__/`, `*.pdb` under publish, `.vscode-test/`, `*.vsix` except `app/publish/`), `docs/benchmarks/README.md` provenance note.
- [ ] 15.3 Verify nothing references moved paths. Commit `chore: archive legacy benchmarks, remove stale build artifacts`.

---

## Phase 4 — Verification & closure

### Task 16: End-to-end verification

- [ ] 16.1 `dotnet test app/TokenOptimizer.slnx` — all 3 test projects green, offline.
- [ ] 16.2 `cd vscode-extension; npm test` green.
- [ ] 16.3 Rebuild publish exe; `--cli selftest` re-run -> every previously-failing model now green (or explicitly Skipped with reason); feature probes green.
- [ ] 16.4 Frontend sanity: launch Avalonia app, Models card shows corrected catalog, tick-list launches one bridged provider end-to-end (manual checklist in `docs/testing/selftest-2026-08-20.md` appendix).
- [ ] 16.5 Update `docs/testing/selftest-2026-08-20.md` with final results; run `/graphify . --update`.
- [ ] 16.6 Final commit `docs: final selftest verification report`; surface task-observer observations.
