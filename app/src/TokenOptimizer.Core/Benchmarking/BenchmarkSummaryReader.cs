using System.Text.Json;
using TokenOptimizer.Core.Config;

namespace TokenOptimizer.Core.Benchmarking;

/// <summary>
/// Reads benchmark_summary.json (written by run_benchmarks.py - that
/// pipeline is out of scope here, this only reads its output) and records
/// whichever model scored highest on composite_score: a SWE-bench-style
/// capability score weighted 70/30 against raw speed, not pure tokens/second
/// - a fast model that writes broken code is a bad coding agent regardless
/// of throughput. Falls back to avg_tokens_per_second alone against an older
/// summary file that predates composite_score. Ported from
/// Update-BestLocalModelFromBenchmarks. Always re-reads rather than trusting
/// a cached "best" - re-running the benchmark suite can change the winner.
/// </summary>
public sealed class BenchmarkSummaryReader
{
    private readonly ConfigStore _configStore;

    public BenchmarkSummaryReader(ConfigStore configStore)
    {
        _configStore = configStore;
    }

    public static IReadOnlyList<BenchmarkRow> ReadRows(string benchmarkSummaryPath)
    {
        if (!File.Exists(benchmarkSummaryPath)) return Array.Empty<BenchmarkRow>();

        using var doc = JsonDocument.Parse(File.ReadAllText(benchmarkSummaryPath));
        var rows = new List<BenchmarkRow>();

        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            var model = entry.TryGetProperty("model", out var m) ? m.GetString() : null;
            var stage = entry.TryGetProperty("stage", out var s) ? s.GetString() : null;
            var status = entry.TryGetProperty("status", out var st) ? st.GetString() : null;
            if (model is null || stage is null || status is null) continue;

            double? tps = entry.TryGetProperty("avg_tokens_per_second", out var t) && t.ValueKind == JsonValueKind.Number
                ? t.GetDouble() : null;
            double? resolveRate = entry.TryGetProperty("resolve_rate", out var r) && r.ValueKind == JsonValueKind.Number
                ? r.GetDouble() : null;
            double? composite = entry.TryGetProperty("composite_score", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetDouble() : null;

            rows.Add(new BenchmarkRow(model, stage, status, tps, resolveRate, composite));
        }

        return rows;
    }

    /// <summary>
    /// Picks the best local coding-agent model from the summary and persists
    /// it into AppConfig so the fallback chain / LM Studio adapter can use it
    /// as the local-model swap without re-reading the file each time.
    /// Returns null if there's no summary file or no successful benchmark rows.
    /// </summary>
    public async Task<BenchmarkRow?> RefreshBestLocalModelAsync(string benchmarkSummaryPath)
    {
        var rows = ReadRows(benchmarkSummaryPath);
        var candidates = rows
            .Where(r => r.Stage == "benchmark" && (r.Status == "ok" || r.Status == "partial") && r.AvgTokensPerSecond is not null)
            .ToList();

        if (candidates.Count == 0) return null;

        var hasCompositeScore = candidates[0].CompositeScore is not null;
        var best = hasCompositeScore
            ? candidates.OrderByDescending(r => r.CompositeScore).First()
            : candidates.OrderByDescending(r => r.AvgTokensPerSecond).First();

        var config = await _configStore.LoadAsync();
        // composite_score is a syntax+keyword heuristic, not a correctness
        // check - it has been observed to disagree sharply with human review
        // (e.g. picking a model with runtime-breaking bugs over one that
        // reliably ships working code). Once a human has deliberately picked
        // a model, don't let this automatic re-scan silently overwrite it.
        if (!config.BestLocalModelIsManualOverride)
        {
            config.BestLocalModelId = best.Model;
            config.BestLocalModelTokensPerSecond = best.AvgTokensPerSecond;
            config.BestLocalModelUpdatedUtc = DateTimeOffset.UtcNow.ToString("o");
            await _configStore.SaveAsync(config);
        }

        return best;
    }
}
