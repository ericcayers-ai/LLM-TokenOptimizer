# Plan: jcode integration into TokenOptimizer

**Status:** plan only — not implemented. Written 2026-08-26 per the standing request
("research http://github.com/1jehuang/jcode and implement jcode into the tokenoptimizer or
things similar — wide scope, make the plan").

## What jcode is

[jcode](https://github.com/1jehuang/jcode) (MIT, Rust, Windows installer at
`https://jcode.sh/install.ps1`) is a RAM-efficient coding-agent CLI harness. Relevant
capabilities, from its README:

- **Provider breadth via one binary**: subscription-backed OAuth logins (`jcode login
  --provider claude|openai|gemini|copilot|azure|...`) plus aggregator providers
  (`openrouter`, `openai-compatible`) and local servers (lmstudio, ollama) — multi-account
  switching with `/account`.
- **Headless auth**: `--print-auth-url --json` + `--callback-url` / `--auth-code` and
  `login --provider <p> --no-browser` for SSH/headless flows.
- **Cross-harness resume**: resume sessions originally created by claude code, codex,
  opencode, or pi.
- **Swarm**: multiple agents in one repo managed by a shared server; agents are notified
  when files shift under each other; works headed or headless.
- **Server/client mode**: `jcode serve` + `jcode connect` for persistent sessions.
- **Built-in browser tool**, mermaid rendering, agent memory graph.
- **Claude Code config compatibility**: reads `~/.claude.json` `mcpServers`,
  per-project `.mcp.json`, `.claude/mcp.json`.

## What TokenOptimizer already has

- `JcodeHarnessAdapter` (`app/src/TokenOptimizer.Providers/Fallback/JcodeHarnessAdapter.cs`)
  already routes Codex and Cursor through jcode (`--provider <id> --model <id>` args,
  gated on jcode on PATH + a stored credential). The adapter pattern to extend exists.
- `ExecutableLocators.FindJcode()` locates the binary.
- UnifiedModelRouter + the gateway env-var contract verified in
  `docs/investigations/model-picker-gateway-discovery.md` §9.
- FreeToken provider (this repo, uncommitted at plan time): local MoE serving Anthropic API
  on loopback :1919.

## Integration opportunities, ranked

### P1 — Promote jcode from "fallback harness" to first-class provider family
Today only Codex/Cursor ride through `JcodeHarnessAdapter`. jcode's provider list is much
wider (claude/openai/gemini/copilot/azure/alibaba-coding-plan/fireworks/minimax/meta-muse/
lmstudio/openrouter/orcarouter/openai-compatible/chutes/cerebras/cursor/antigravity/google).
Plan:
1. Add a `JcodeProviders` catalog (id + display name + credential gating key) covering the
   providers TokenOptimizer wants to expose.
2. One `JcodeHarnessAdapter` instance per exposed provider, appended in MainViewModel +
   CliHost provider arrays exactly like the FreeToken wiring did.
3. Credential flow stays opt-in via ProxyCredentialStore, but add a `jcode login --provider
   <id> --no-browser` guided path in the app's provider card so users don't touch a terminal.

### P2 — Cross-harness session resume as an app feature
"Resume a Claude Code session from another tool" is a user-visible feature jcode gives us
for free. Plan: a "Resume with…" action in the Session tab that lists jcode-resumable
sessions for the selected project and launches `jcode --resume <name>` (or opens jcode TUI)
with the project cwd. Zero new protocol work — it shells out.

### P3 — Local models through jcode's OpenAI-compatible profile system
FreeToken serves OpenAI `/v1/chat/completions` on the same port as its Anthropic endpoint.
jcode consumes self-hosted OpenAI-compatible endpoints via named profiles in
`~/.jcode/config.toml`. Plan: on FreeToken provider setup, offer to write/update a
`freetoken-local` profile in `~/.jcode/config.toml` pointing at `http://127.0.0.1:1919/v1`
so jcode sessions can also use the local MoE models. Same pattern generalizes to any local
llama.cpp server the app manages.

### P4 — Swarm panel
A read-only dashboard card that runs `jcode serve` for a project and renders swarm state
(agents, channels, completions). Defer until P1–P3 land; needs UI work in MainViewModel and
the VS Code extension panel.

### Explicitly out of scope
- Bundling/embedding jcode into the MSI: keep it an external dependency discovered on PATH
  (consistent with every other adapter).
- Replacing UnifiedModelRouter with jcode routing: different job — the router is the
  Claude-Code-facing proxy; jcode would be a *consumer* of it, not a replacement.

## Suggested commit sequence when implemented

1. `feat(providers): jcode provider catalog + adapters for new providers` (P1 steps 1–2)
2. `feat(ui): guided jcode login flow` (P1 step 3)
3. `feat(app): cross-harness resume via jcode` (P2)
4. `feat(providers): freetoken/jcode local-model bridge profiles` (P3)
5. Tests mirroring FreeToken's live-loopback test style for any new HTTP-touching code.
