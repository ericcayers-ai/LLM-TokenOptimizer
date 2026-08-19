using TokenOptimizer.Core.Concurrency;
using TokenOptimizer.Core.Models;

namespace TokenOptimizer.Providers.Fallback;

public sealed record FallbackChainStep(string ProviderName, bool IsAvailable, string? UnavailableReason);

/// <summary>
/// Resolves which backend a session should actually launch against when the
/// user picks "Auto". Mirrors Resolve-SessionBackend: ONLY Claude Code -&gt;
/// Antigravity -&gt; local model is automatic. Codex and Cursor are
/// deliberately manual-only (v5.9 reverted auto-routing them, by explicit
/// request) - reachable exclusively by picking them directly in the
/// provider dropdown or via "Export Handoff", never auto-selected here.
/// </summary>
public sealed class FallbackChainResolver
{
    private readonly IProviderAdapter _claudeAdapter;
    private readonly AntigravityAdapter _antigravity;
    private readonly CodexAdapter _codex;
    private readonly CursorAdapter _cursor;
    private readonly GroqAdapter _groq;
    private readonly DeepSeekHarnessAdapter _deepSeekHarness;
    private readonly IProviderAdapter _localModel;
    private readonly RateLimitTracker _rateLimits;

    public FallbackChainResolver(
        IProviderAdapter claudeAdapter,
        AntigravityAdapter antigravity,
        CodexAdapter codex,
        CursorAdapter cursor,
        GroqAdapter groq,
        DeepSeekHarnessAdapter deepSeekHarness,
        IProviderAdapter localModel,
        RateLimitTracker rateLimits)
    {
        _claudeAdapter = claudeAdapter;
        _antigravity = antigravity;
        _codex = codex;
        _cursor = cursor;
        _groq = groq;
        _deepSeekHarness = deepSeekHarness;
        _localModel = localModel;
        _rateLimits = rateLimits;
    }

    /// <summary>All adapters this resolver knows about, keyed by Name, for custom-order resolution.</summary>
    private IReadOnlyDictionary<string, (IProviderAdapter Adapter, FallbackProvider? RateLimitKey)> AdaptersByName => new Dictionary<string, (IProviderAdapter, FallbackProvider?)>
    {
        [_claudeAdapter.Name] = (_claudeAdapter, FallbackProvider.Claude),
        [_antigravity.Name] = (_antigravity, FallbackProvider.Antigravity),
        [_codex.Name] = (_codex, FallbackProvider.Codex),
        [_cursor.Name] = (_cursor, FallbackProvider.Cursor),
        [_groq.Name] = (_groq, FallbackProvider.Groq),
        [_deepSeekHarness.Name] = (_deepSeekHarness, FallbackProvider.DeepSeekHarness),
        [_localModel.Name] = (_localModel, null),
    };

    /// <summary>Walks a user-defined, drag-reordered provider order (see AppConfig.CustomFallbackOrder), same availability/rate-limit gating as the auto chain, skipping any name not present in this adapter set.</summary>
    public async Task<IProviderAdapter?> ResolveCustomAsync(IReadOnlyList<string> orderedProviderNames)
    {
        var byName = AdaptersByName;
        foreach (var name in orderedProviderNames)
        {
            if (!byName.TryGetValue(name, out var entry)) continue;
            if (entry.RateLimitKey is { } key && await _rateLimits.IsRateLimitedAsync(key)) continue;
            if (await entry.Adapter.IsAvailableAsync()) return entry.Adapter;
        }

        return null;
    }

    public async Task<IProviderAdapter?> ResolveAsync()
    {
        if (!await _rateLimits.IsRateLimitedAsync(FallbackProvider.Claude) && await _claudeAdapter.IsAvailableAsync())
        {
            return _claudeAdapter;
        }

        if (!await _rateLimits.IsRateLimitedAsync(FallbackProvider.Antigravity) && await _antigravity.IsAvailableAsync())
        {
            return _antigravity;
        }

        if (await _localModel.IsAvailableAsync()) return _localModel;

        return null;
    }

    public async Task<IReadOnlyList<FallbackChainStep>> DescribeChainAsync()
    {
        var steps = new List<FallbackChainStep>
        {
            await DescribeAsync(_claudeAdapter, FallbackProvider.Claude),
            await DescribeAsync(_antigravity, FallbackProvider.Antigravity),
            await DescribeAsync(_localModel, null),
            await DescribeManualOnlyAsync(_codex, FallbackProvider.Codex),
            await DescribeManualOnlyAsync(_cursor, FallbackProvider.Cursor),
            await DescribeManualOnlyAsync(_groq, FallbackProvider.Groq),
            await DescribeManualOnlyAsync(_deepSeekHarness, FallbackProvider.DeepSeekHarness),
        };

        return steps;
    }

    private async Task<FallbackChainStep> DescribeAsync(IProviderAdapter adapter, FallbackProvider? rateLimitKey)
    {
        if (rateLimitKey is { } key && await _rateLimits.IsRateLimitedAsync(key))
        {
            return new FallbackChainStep(adapter.Name, false, "Rate-limited until cooldown expires");
        }

        var available = await adapter.IsAvailableAsync();
        return new FallbackChainStep(adapter.Name, available, available ? null : "Not installed or credential missing");
    }

    private async Task<FallbackChainStep> DescribeManualOnlyAsync(IProviderAdapter adapter, FallbackProvider rateLimitKey)
    {
        var step = await DescribeAsync(adapter, rateLimitKey);
        return step with { UnavailableReason = step.IsAvailable ? "Manual only - not auto-routed" : step.UnavailableReason };
    }
}
