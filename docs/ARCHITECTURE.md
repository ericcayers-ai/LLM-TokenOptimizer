# TokenOptimizer System Architecture

**Status:** current as of August 2026. This document is the design record for how every piece fits together — the "encompassing layer" story. Component-level READMEs cover usage; this covers structure and the decisions behind it.

## 1. The one-sentence model

TokenOptimizer sits **between a human and every coding-agent backend they use**: it owns provider discovery, credential storage, session launching, model routing, fallback, sandboxing, and token accounting, so any supported agent harness (Claude Code, Codex/jcode, Antigravity, OpenCode Go/Zen, Groq, local engines — Unsloth/llama.cpp, FreeToken MoE, Hermes Agent) is just another adapter behind one consistent surface (Avalonia UI + headless JSON CLI + VS Code extension).

## 2. Layer map

```
+--------------------------------------------------------------------------+
|  SURFACES (thin)                                                          |
|    Avalonia UI (MainViewModel)   CliHost --cli JSON   VS Code extension   |
|         |                              |                     |            |
|         +----------------- both front-ends call --------+----+            |
|                                                         |                 |
+---------------------------------------------------------|-----------------+
|  ORCHESTRATION                                           |                 |
|    FallbackChainResolver   UnifiedModelRouter   AnthropicCompatProxy       |
|    RateLimitTracker        SessionPresetRanker  RollingContextProxy      |
|    ProjectSessionPrep      SessionHandoffExporter                          |
+--------------------------------------------------------------------------+
|  PROVIDER ADAPTERS (IProviderAdapter - one class per harness/backend)     |
|    ClaudeCode  Antigravity  Jcode(Codex)  Cursor  OpenCode(Go/Zen)         |
|    Groq  DeepSeekHarness  LlamaCpp/Unsloth  FreeToken(local MoE)          |
|    HermesAgent   <- each: IsAvailable / Launch / manifests / skills       |
+--------------------------------------------------------------------------+
|  SUBSTRATE                                                                |
|    TokenOptimizer.Sandbox: PreflightGate -> opensandbox-server lifecycle  |
|    -> per-session container (project at /workspace, companion tools baked)|
|    Core: ConfigStore, ProxyCredentialStore (DPAPI), ProcessSessionHandle  |
+--------------------------------------------------------------------------+
|  LOCAL MODEL ENGINES (upstreams, launched/probed, not replaced)           |
|    FreeToken Desktop :1919 (Anthropic + OpenAI API)   unsloth/llama-server|
+--------------------------------------------------------------------------+
```

## 3. The three routing tiers (how requests actually flow)

1. **Direct env-var launch** (`ANTHROPIC_BASE_URL`): adapters that need no translation point Claude Code straight at an upstream. FreeToken serves Anthropic `/v1/messages` natively on `127.0.0.1:1919`, so `FreeTokenAdapter` is pure env-var wiring — zero moving parts.
2. **Per-session bridge** (`AnthropicCompatProxy`): one OS-assigned loopback port per session; terminates Anthropic-shaped requests, re-emits OpenAI chat-completions upstream (Groq, llama-server), translates responses back including streaming SSE deltas and tool-call blocks. Passthrough mode skips translation for Anthropic-native upstreams (OpenCode Go).
3. **Model-multiplexing gateway** (`UnifiedModelRouter`): one endpoint, many models. Routes are keyed by the request's `model` field; `GET /v1/models` advertises every ticked model plus an `__auto__` meta-model into Claude Code's own `/model` picker (via `CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY=1`). Non-bridgeable selections fall through to the auto-fallback delegate. This is what makes one CLI window able to switch between cloud, gateway, and local models mid-session.

**Decision rule for new backends:** speaks Anthropic natively → tier 1 or passthrough route; speaks only OpenAI → tier 2 translate route; neither → its own adapter launches its own harness process (tier 0 — the harness itself is the runtime, like Codex/Antigravity/Hermes).

## 4. Fallback semantics (deliberate, two-axis)

- **Automatic chain** (resolver order): Claude Code → Antigravity → OpenCode Go → local model. Gated per-provider by `RateLimitTracker` cooldowns recorded from real session rate-limit banners.
- **Manual-only**: Codex, Cursor, Groq, DeepSeek Harness, **Hermes Agent**, **FreeToken**. Rationale: these are either separate products with their own auth/session models (not swappable backends) or local engines whose availability depends on GUI state (a loaded model) that auto-routing must never silently work around. They remain fully launchable and appear in `DescribeChainAsync` with their manual-only status.
- **Custom chain**: user drag-reorders any subset (`AppConfig.CustomFallbackOrder`) — same gating, resolved by name against the same adapter set.

## 5. The encompassing layer: Hermes Agent integration

Hermes Agent (Nous Research) is itself an agent *platform* — CLI, desktop, TUI, gateway, proxy — with its own provider config, fallback chain, and skills system. It is integrated as **an encompassing peer layer**, not flattened into a model endpoint:

- **As a provider adapter** (`HermesAgentAdapter`): launches `hermes chat` sessions in a project directory (host-side — Hermes has its own container story; double-sandboxing would break its tool access), gated on locator + probe of the Hermes home dir. Model selection maps to `--model`; Continue resumes the folder's most recent session via `-c`; Pick fails fast because `hermes chat --resume` requires an explicit session id (no interactive picker exists).
- **As a router citizen** (`TryBuildDirectRoute`): ticked Hermes-provider models get a passthrough route so they appear in Claude Code's `/model` picker alongside cloud and local options.
- **As an auto-fallback candidate**: `ResolveAutoFallbackRouteAsync` prefers FreeToken (local, free, Anthropic-native) ahead of paid cloud routes before giving up.
- **Config contract (verified against Hermes source, Aug 2026)**: Hermes consumes custom endpoints via `model.provider: custom` + `model.base_url` + optional `model.api_mode: anthropic_messages` in `config.yaml`, key stored as `HERMES_CUSTOM_<identity>_API_KEY`. A bare host:port URL defaults to OpenAI transport, so Anthropic-native upstreams (FreeToken) require the explicit `api_mode`. TokenOptimizer's `Setup-HermesIntegration.ps1` writes exactly this shape via `hermes config set`, pointed at whatever local engine is serving — which is how Hermes sessions ride the same local MoE engine through TokenOptimizer-managed infrastructure. Per-profile isolation uses `HERMES_HOME`.
- **Why adapter-and-config rather than proxy-through**: Hermes already ships its own OAuth proxy (`hermes proxy start`), fallback chain, and multi-provider routing. Re-exporting those through our loopback proxies would duplicate working machinery; pointing Hermes' native custom-endpoint support at TokenOptimizer-managed engines composes instead.

## 6. Local MoE engines: FreeToken vs llama.cpp/Unsloth

Both are tier-1-style upstreams; they differ in lifecycle ownership. FreeToken Desktop owns model loading in its GUI (no documented headless load endpoint) — the adapter probes `/v1/models`, reports unavailable until a model is loaded, launches the app when installed-but-idle, and waits honestly rather than faking success. llama.cpp/Unsloth is driven headlessly (`unsloth start claude ... --serve`) and its generated env is parsed from boot output. `freetoken_local/` (Python, stdlib-only) is the scripting counterpart used by CI/self-tests and by users automating outside the app.

## 7. Verification spine

- **Unit/integration**: xUnit per project; adapters tested with injected locators/runners (no real processes in tests).
- **Rate-limit cooldowns** are recorded only from sandboxed sessions, whose handles arm the console watcher; host-side launches (login/GUI flows, Hermes sessions, local servers) report no rate-limit outcome by construction — a local engine has no usage-limit banner and remote harnesses surface limits in their own UIs. The tracker's provider mapping is kept complete anyway so future sandbox-routed providers can't corrupt the bookkeeping.
- **Live matrix**: `SelftestMatrix` entries run real end-to-end probes through the exact production launch path (`ModelProbeService`); unreachable components **skip with a stated reason** — a skip is honest, a fabricated pass is not.
- **Selftest CLI**: `TokenOptimizer.App.exe --cli selftest` emits one JSON verdict consumed by the VS Code extension and CI.
- **Python side**: `python -m freetoken_local selftest` performs a real round-trip against :1919 (no mocks); non-zero exit when the server isn't serving.

## 8. Invariants (do not break these when extending)

1. Every new provider implements `IProviderAdapter` and appears in BOTH front-ends' adapter arrays (MainViewModel + CliHost) — the two must never drift.
2. New rate-limitable providers get a `FallbackProvider` member + `AppConfig` field + tracker mapping, or they corrupt cooldown bookkeeping.
3. Loopback proxies bind OS-assigned ports, one per session; nothing hardcodes ports except documented upstream defaults (:1919).
4. Credentials only ever live in `ProxyCredentialStore` (DPAPI) or Hermes' `.env` — never in `config.json`, never logged (`ModelProbeService.Redact`).
5. Availability probing tells the truth: installed-but-not-serving is *unavailable*, with the reason surfaced, never silently worked around.
6. Sandbox preflight gates interactive launches; host-side exceptions (login/GUI flows, Hermes sessions, local servers) stay host-side deliberately and say so in comments.
