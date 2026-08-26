namespace TokenOptimizer.Core.Models;

/// <summary>
/// Priority order of the fallback chain when Claude Code itself is
/// unavailable or rate-limited: Antigravity -> OpenCode -> local
/// model, in that fixed order. Codex/Cursor/Groq/DeepSeekHarness are
/// manual-only (see FallbackChainResolver).
/// </summary>
public enum FallbackProvider
{
    Claude,
    Antigravity,
    Codex,
    Cursor,
    Groq,
    DeepSeekHarness,
    OpenCode,
    /// <summary>OpenCode Zen - a separate gateway/account from OpenCode Go: different sign-in, different API key, different base URL (opencode.ai/zen vs opencode.ai/zen/go).</summary>
    OpenCodeZen,
    /// <summary>Hermes Agent - an encompassing peer agent platform with its own provider config and fallback chain; manual-only like Codex/Cursor (a separate product, not a swappable backend).</summary>
    HermesAgent,
}
