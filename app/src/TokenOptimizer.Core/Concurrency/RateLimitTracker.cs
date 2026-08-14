using TokenOptimizer.Core.Config;
using TokenOptimizer.Core.Models;

namespace TokenOptimizer.Core.Concurrency;

/// <summary>
/// Records when a provider hit its usage limit and skips it until that
/// cooldown expires, instead of retrying an already-exhausted backend and
/// burning another attempt. Mirrors Test/Save-RateLimit* in the PowerShell
/// launcher.
/// </summary>
public sealed class RateLimitTracker
{
    private readonly ConfigStore _configStore;

    public RateLimitTracker(ConfigStore configStore)
    {
        _configStore = configStore;
    }

    public async Task<bool> IsRateLimitedAsync(FallbackProvider provider)
    {
        var config = await _configStore.LoadAsync();
        var raw = GetField(config, provider);
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (!DateTimeOffset.TryParse(raw, out var until)) return false;
        return DateTimeOffset.UtcNow < until;
    }

    public Task RecordRateLimitAsync(FallbackProvider provider, DateTimeOffset resumeAtUtc) =>
        _configStore.UpdateAsync(config => SetField(config, provider, resumeAtUtc.ToString("o")));

    private static string? GetField(AppConfig config, FallbackProvider provider) => provider switch
    {
        FallbackProvider.Claude => config.ClaudeRateLimitedUntilUtc,
        FallbackProvider.Antigravity => config.AntigravityRateLimitedUntilUtc,
        FallbackProvider.Codex => config.CodexRateLimitedUntilUtc,
        FallbackProvider.Cursor => config.CursorRateLimitedUntilUtc,
        _ => null,
    };

    private static void SetField(AppConfig config, FallbackProvider provider, string value)
    {
        switch (provider)
        {
            case FallbackProvider.Claude: config.ClaudeRateLimitedUntilUtc = value; break;
            case FallbackProvider.Antigravity: config.AntigravityRateLimitedUntilUtc = value; break;
            case FallbackProvider.Codex: config.CodexRateLimitedUntilUtc = value; break;
            case FallbackProvider.Cursor: config.CursorRateLimitedUntilUtc = value; break;
        }
    }
}
