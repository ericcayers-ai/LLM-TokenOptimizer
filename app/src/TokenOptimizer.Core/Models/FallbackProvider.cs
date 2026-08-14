namespace TokenOptimizer.Core.Models;

/// <summary>
/// Priority order of the fallback chain when Claude Code itself is
/// unavailable or rate-limited: Antigravity -> Codex -> Cursor -> local
/// model, in that fixed order.
/// </summary>
public enum FallbackProvider
{
    Claude,
    Antigravity,
    Codex,
    Cursor,
}
