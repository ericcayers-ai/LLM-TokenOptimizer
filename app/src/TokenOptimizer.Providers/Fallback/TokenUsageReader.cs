using System.Text.Json;
using TokenOptimizer.Core.Diagnostics;

namespace TokenOptimizer.Providers.Fallback;

public sealed record TokenUsageSummary(double TodayCostUsd, long TodayTokens, double AllTimeCostUsd, long AllTimeTokens, string? MostRecentDay);

/// <summary>
/// Reads token/cost usage via ccusage (see ProviderCliInstaller.InstallCcusageAsync) -
/// a local, offline CLI that parses the same Claude Code/Codex/etc session
/// logs already on disk. No network call, no proxy, nothing this app has to
/// compute itself - just shells out and reads its JSON.
/// </summary>
public static class TokenUsageReader
{
    public static async Task<TokenUsageSummary?> GetSummaryAsync()
    {
        var ccusage = new CommandAvailability().ResolveOnPath("ccusage");
        if (ccusage is null) return null;

        var result = await ExternalCommandRunner.RunAsync(ccusage, "daily --json", timeoutSeconds: 20);
        if (!result.Success) return null;

        try
        {
            using var doc = JsonDocument.Parse(result.Output);
            if (!doc.RootElement.TryGetProperty("daily", out var daily) || daily.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            double allTimeCost = 0, todayCost = 0;
            long allTimeTokens = 0, todayTokens = 0;
            string? mostRecentDay = null;
            var today = DateTime.Now.ToString("yyyy-MM-dd");

            foreach (var day in daily.EnumerateArray())
            {
                var period = day.TryGetProperty("period", out var p) ? p.GetString() : null;
                var cost = day.TryGetProperty("totalCost", out var c) ? c.GetDouble() : 0;
                var tokens = SumTokens(day);

                allTimeCost += cost;
                allTimeTokens += tokens;
                if (period is not null && (mostRecentDay is null || string.CompareOrdinal(period, mostRecentDay) > 0)) mostRecentDay = period;
                if (period == today)
                {
                    todayCost += cost;
                    todayTokens += tokens;
                }
            }

            return new TokenUsageSummary(todayCost, todayTokens, allTimeCost, allTimeTokens, mostRecentDay);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long SumTokens(JsonElement day)
    {
        long total = 0;
        foreach (var field in new[] { "inputTokens", "outputTokens", "cacheCreationTokens", "cacheReadTokens" })
        {
            if (day.TryGetProperty(field, out var v) && v.TryGetInt64(out var n)) total += n;
        }
        return total;
    }
}
