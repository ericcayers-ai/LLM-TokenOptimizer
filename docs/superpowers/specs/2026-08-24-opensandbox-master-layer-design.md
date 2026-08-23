# OpenSandbox Master Layer Design

**Date:** 2026-08-24
**Status:** Approved
**Scope:** TokenOptimizer gains a mandatory sandbox substrate built on Alibaba's OpenSandbox (github.com/alibaba/OpenSandbox, Apache-2.0), adopted upstream-as-engine, with NanoNets/Graft integrated as the token-saving context layer.

## Decisions (user-approved)

1. **Integration mode:** Adopt upstream as engine. The real `opensandbox-server` (pip/uvx), `osb` CLI, and official `Alibaba.OpenSandbox` C# SDK are installed/managed by the app; no protocol reimplementation.
2. **graft = NanoNets/Graft**: code-graph context injector (hooks + statusline into `.claude/`, ~42% token-cut claims).
3. **Orchestration scope: Full substrate** — agent sessions (Claude Code + all fallback backends), companion tooling (RTK, graphify, headroom, caveman, claude-mem, context7, graft) baked into sandbox images, project mounted at `/workspace`, benchmarks per-sandbox.
4. **Docker posture: Mandatory sandbox.** App requires Docker Desktop + running opensandbox-server; direct-host launches are removed behind the gate. Accepted as a breaking change; first-run setup wizard mitigates onboarding.
5. **Architecture: Dedicated orchestrator project** — new `TokenOptimizer.Sandbox` csproj is the master layer; `MainViewModel` delegates to it.

## Architecture

```
Avalonia App ──┐
VS Code ext ───┤→ SandboxOrchestrator  (NEW · app/src/TokenOptimizer.Sandbox)
               │    ├─ PreflightGate        mandatory: Docker + server up, else SetupWizard → exit
               │    ├─ ServerLifecycleManager  uvx opensandbox-server, ~/.sandbox.toml (docker runtime), health probe, watchdog
               │    ├─ SandboxFactory          official Alibaba.OpenSandbox SDK: create/exec/files/kill
               │    ├─ VolumeMapper            project ↔ /workspace mount
               │    └─ ImageCatalog            tokenoptimizer images layered on upstream bases
Provider adapters (Claude/Antigravity/Codex/Cursor/Groq/LmStudio) → all execute INSIDE sandboxes
FallbackChainResolver → backend failover within sandbox substrate (unchanged semantics)
```

## Components

- **`TokenOptimizer.Sandbox`** (new): `ISandboxRuntime` contract + `FakeSandboxRuntime` (unit tests) + `OpenSandboxSdkRuntime` (official SDK adapter); `SandboxSettings` in ConfigStore; `ServerLifecycleManager` (install/init/start/health/watchdog via `IProcessRunner`); `PreflightGate` + setup steps; `ImageCatalog` generating Dockerfiles from `ToolCatalog`; `SandboxSessionHandle` streaming sibling of `ProcessSessionHandle`.
- **`ToolCatalog`** (extracted in Providers): single source of truth for companion tools — host install command + image install fragment + `.claude` wiring fragment — feeding both host wiring and image baking. Includes graft (`NanoNets/Graft`).
- **Providers:** adapters switch from local `Process` to in-sandbox exec; `FallbackChainResolver` semantics unchanged; `RateLimitWatcher` consumes the same stream shape.
- **App UI:** startup preflight gate; guided `SetupWizardViewModel`; dashboard shows server status + sandbox list.
- **VS Code extension:** dashboard "Sandbox" panel.
- **Legacy PS1 launcher:** untouched, documented as superseded.

## Session data flow

preflight → ensure server → pick project → create sandbox (`tokenoptimizer/agent-companion`, project mounted, idle timeout) → exec backend CLI inside (pre-wired `.claude` with companions + graft; entrypoint runs `graft init && graft build /workspace`) → stream output → rate-limit/failover logic unchanged → kill/snapshot.

## Upstream adoption map

**Wired:** server (Docker runtime), C# SDK, osb CLI diagnostics, code-interpreter/aio image bases, execd+ingress implicitly.
**Unwired (Linux/multi-tenant concerns, documented future work):** Kubernetes runtime, gVisor/Kata/Firecracker isolation, ingress routing strategies, egress policy UI, credential vault (host `ProxyCredentialStore` remains).

## Error handling

Docker down → wizard + retry · server crash → watchdog restart with backoff, reattach by sandbox ID where possible · image build failure → surfaced logs · sandbox OOM/timeout → kill/recreate, session preserved via existing handoff exporter · backend failover ≠ sandbox failover (all backends run in-sandbox).

## Verification strategy

Unit tests with `FakeSandboxRuntime`; golden Dockerfile tests; Docker-gated integration tests (`TOKENOPTIMIZER_DOCKER_TESTS=1`): create/exec/files/kill against `opensandbox/aio`; E2E smoke script (`claude --version` in-sandbox); full suite green; MSI builds.

## Global constraints

- Windows-only product; .NET 10; WiX pinned **5.0.2**
- Sandbox mode mandatory; no direct-host launch survives the gate
- `LLM-TokenOptimizer.ps1` not modified
- No secrets in images/config samples
- One `ToolCatalog` feeds both host wiring and image baking
