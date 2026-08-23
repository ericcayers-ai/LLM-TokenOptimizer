# OpenSandbox Master Orchestrator Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Alibaba OpenSandbox the mandatory execution substrate for TokenOptimizer — every agent session, companion tool, and benchmark runs inside managed sandboxes orchestrated by a new master layer.

**Architecture:** New `TokenOptimizer.Sandbox` project owns server lifecycle, preflight gating, sandbox creation via the official `Alibaba.OpenSandbox` SDK, image building (companions + graft baked in), and session streaming. Existing provider adapters and fallback chain keep their semantics; their launch path moves inside sandboxes.

**Tech Stack:** .NET 10 · Avalonia · `Alibaba.OpenSandbox` NuGet · upstream `opensandbox-server` via uvx (Docker runtime) · Docker Desktop/WSL2 · NanoNets/Graft · existing xUnit suites.

## Global Constraints

- Windows-only product; .NET 10; WiX stays pinned **5.0.2**
- Sandbox mode **mandatory**: no direct-host launch path survives behind the gate
- `LLM-TokenOptimizer.ps1` is legacy — **do not modify**
- `dotnet test TokenOptimizer.slnx` must stay green; Docker-gated integration tests skip unless `TOKENOPTIMIZER_DOCKER_TESTS=1`
- No secrets in images/config samples; API keys stay in host `ProxyCredentialStore`/`ConfigStore`
- Single source of truth for companion tools: one `ToolCatalog` feeds both host wiring and image baking
- After code changes land: `graphify update .`

---

### Task 0: Commit design doc + this plan

- [ ] Write spec to `docs/superpowers/specs/2026-08-24-opensandbox-master-layer-design.md`
- [ ] Write plan to `docs/superpowers/plans/2026-08-24-opensandbox-master-layer.md`
- [ ] `git add docs/superpowers && git commit -m "docs: opensandbox master layer spec + plan"`

### Task 1: Extract `ToolCatalog` (single source of truth)

**Files:**
- Create: `app/src/TokenOptimizer.Providers/ToolCatalog.cs`
- Modify: `app/src/TokenOptimizer.Providers/Claude/CompanionToolingInstaller.cs`
- Test: `app/tests/TokenOptimizer.Core.Tests/Providers/ToolCatalogTests.cs`

**Interfaces:**
- Produces: `record CompanionTool(string Id, string HostInstallCommand, string ImageInstallFragment, string ClaudeWiringFragment)` and `static class ToolCatalog { IReadOnlyList<CompanionTool> Tools { get; } }` covering rtk, graphify, headroom, caveman, claude-mem, context7, graft.

Steps: failing test (catalog contains ids `{rtk,graphify,headroom,caveman,claude-mem,context7,graft}`, fragments non-empty, graft fragment contains `NanoNets/Graft`) → move installer tool constants into catalog → installer reads from catalog → pass → commit `refactor: extract ToolCatalog`.

### Task 2: Sandbox contracts + fake runtime

**Files:**
- Create: `app/src/TokenOptimizer.Sandbox/ISandboxRuntime.cs`, `FakeSandboxRuntime.cs`, `TokenOptimizer.Sandbox.csproj` (net10.0)
- Test: `app/tests/TokenOptimizer.Core.Tests/Sandbox/FakeSandboxRuntimeTests.cs`

```csharp
public interface ISandboxRuntime {
    Task<SandboxHandle> CreateAsync(SandboxSpec spec, CancellationToken ct = default);
    IAsyncEnumerable<ExecEvent> ExecAsync(string id, IReadOnlyList<string> argv, CancellationToken ct = default);
    Task<string> ReadFileAsync(string id, string path, CancellationToken ct = default);
    Task WriteFileAsync(string id, string path, string content, CancellationToken ct = default);
    Task KillAsync(string id, CancellationToken ct = default);
}
public sealed record SandboxSpec(string Image, IReadOnlyDictionary<string,string> Mounts,
    TimeSpan? Timeout = null, IReadOnlyDictionary<string,string>? Env = null);
public sealed record SandboxHandle(string Id);
public abstract record ExecEvent(string Text);
public sealed record ExecOutput(string Stream, string Text) : ExecEvent(Text);
public sealed record ExecExit(int Code) : ExecEvent(Code.ToString());
```

Tests: unique ids on create; exec replays scripted events; write→read roundtrip; kill marks dead (later ops throw `InvalidOperationException`). Commit `feat(sandbox): contracts + fake runtime`.

### Task 3: `SandboxSettings` + ConfigStore wiring

**Files:**
- Create: `app/src/TokenOptimizer.Sandbox/SandboxSettings.cs`
- Modify: `app/src/TokenOptimizer.Core/Config/ConfigStore.cs`
- Test: `app/tests/TokenOptimizer.Core.Tests/Config/SandboxSettingsTests.cs`

Shape: `Domain=localhost:8080`, `Protocol=http`, `ApiKeySecretRef` (no plaintext key), `AgentImage=tokenoptimizer/agent-companion:latest`, `IdleTimeoutMinutes=60`. Roundtrip serialize/deserialize tests; defaults stable. Commit `feat(sandbox): settings section`.

### Task 4: `ServerLifecycleManager`

**Files:**
- Create: `app/src/TokenOptimizer.Sandbox/ServerLifecycleManager.cs`, `ProcessRunner.cs`
- Test: `app/tests/TokenOptimizer.Core.Tests/Sandbox/ServerLifecycleManagerTests.cs`

```csharp
public interface IProcessRunner { Task<ProcResult> RunAsync(string exe, IReadOnlyList<string> args,
    IDictionary<string,string>? env = null, CancellationToken ct = default); }
public sealed record ProcResult(int ExitCode, string StdOut, string StdErr);
public sealed record ServerStatus(bool DockerUp, bool ServerUp, Uri? Domain, string? Error);
public sealed class ServerLifecycleManager(IProcessRunner runner, SandboxSettings s) {
    public Task<ServerStatus> GetStatusAsync();                        // docker info ; GET /health
    public Task<ServerStatus> EnsureRunningAsync(CancellationToken ct); // init-config --example docker → uvx opensandbox-server → poll health ≤30s
}
```

Unit tests with fake runner scripts (`docker info` fail → `DockerUp=false`; healthy poll sequence → `ServerUp=true`; config written once). Commit `feat(sandbox): server lifecycle`.

### Task 5: `PreflightGate` + SetupWizard VM

**Files:**
- Create: `app/src/TokenOptimizer.Sandbox/PreflightGate.cs`
- Create: `app/src/TokenOptimizer.App/ViewModels/SetupWizardViewModel.cs`
- Test: `app/tests/TokenOptimizer.Core.Tests/Sandbox/PreflightGateTests.cs`

```csharp
public sealed record PreflightResult(bool Ok, IReadOnlyList<string> Missing, IReadOnlyList<SetupStep> Steps);
public sealed record SetupStep(string Id, string Description, Func<CancellationToken, Task<bool>> Execute);
```

Steps: WSL enable → `winget install Docker.DockerDesktop` → start service → ensure server (Task 4). Gate = `Ok` only when both probes pass. Tests: missing-docker path lists correct steps; ok-path empty. Commit `feat(sandbox): mandatory preflight + wizard`.

### Task 6: Official SDK adapter (discovery + `OpenSandboxSdkRuntime`)

**Files:**
- Create: `app/src/TokenOptimizer.Sandbox/OpenSandboxSdkRuntime.cs`
- Modify: `app/src/TokenOptimizer.Sandbox/TokenOptimizer.Sandbox.csproj` (+ `Alibaba.OpenSandbox`)
- Test: `app/tests/TokenOptimizer.Core.Tests/Sandbox/OpenSandboxSdkRuntimeIntegrationTests.cs` (gated by `TOKENOPTIMIZER_DOCKER_TESTS=1`)

Steps: spike — inspect upstream C# SDK source (`sdks/sandbox/csharp`) for exact lifecycle/exec/file method names; map to `ISandboxRuntime` (Python reference: `Sandbox.create / commands.run / files.read_file / files.write_files / kill`). Gated integration tests: create `opensandbox/aio` sandbox → exec `echo tokenoptimizer` stdout assert → file roundtrip → kill. Commit `feat(sandbox): official SDK runtime adapter`.

### Task 7: `ImageCatalog` Dockerfile generation

**Files:**
- Create: `app/src/TokenOptimizer.Sandbox/ImageCatalog.cs`
- Test: `app/tests/TokenOptimizer.Core.Tests/Sandbox/ImageCatalogGoldenTests.cs`

Generates two stages from `ToolCatalog`: `agent-base` (node LTS + Claude Code CLI on `opensandbox/code-interpreter` base) and `agent-companion` (+ each tool's `ImageInstallFragment`, `.claude` wiring fragments, entrypoint running `graft init && graft build /workspace`). Golden-file assertions on full output text (stable ordering). Build smoke gated test: `docker build` succeeds when Docker present. Commit `feat(sandbox): image catalog + graft/companion layer`.

### Task 8: `SandboxSessionHandle` + provider refactor

**Files:**
- Create: `app/src/TokenOptimizer.Providers/SandboxSessionHandle.cs`
- Modify: provider adapters + `FallbackChainResolver` call sites; `app/src/TokenOptimizer.App/ViewModels/MainViewModel.cs` (`LaunchSessionAsync` ~L696) delegates through orchestrator; startup calls `PreflightGate`
- Test: `app/tests/TokenOptimizer.Core.Tests/Providers/SandboxSessionHandleTests.cs`

Same surface as `ProcessSessionHandle` (`IDisposable`, exit awaitable, output stream feeding `RateLimitWatcher` unchanged). Adapters switch from `Process` to `ISandboxRuntime.ExecAsync(argv)` with backend-specific argv/env; resolver order untouched. Tests: handle aggregates events until `ExecExit`; watcher receives stream. Commit `feat!: route all backends through sandbox substrate`.

### Task 9: App UI + VS Code dashboard

Modify `MainViewModel` (sandbox list/status section, wizard binding); `vscode-extension/src/dashboard.ts` adds "Sandbox" panel. Verify `dotnet build`, extension compile, manual Avalonia smoke. Commit `feat(ui): sandbox dashboard + setup wizard`.

### Task 10: E2E smoke + docs + MSI

Create `scripts/e2e-sandbox-smoke.ps1`: preflight → ensure server → build image → create sandbox → `claude --version` in-sandbox → kill → exit non-zero on any failure. Update README ("How the pieces fit together" gains Sandbox row; requirements gain Docker). `dotnet test` green; `build-installer.ps1` produces MSI. Commit `test+docs: e2e smoke, docs, msi verify`.
