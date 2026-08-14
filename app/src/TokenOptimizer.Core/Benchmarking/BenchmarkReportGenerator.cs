using System.Text;
using System.Text.Json;

namespace TokenOptimizer.Core.Benchmarking;

/// <summary>
/// Generates a markdown scoring matrix + per-model averages directly from
/// benchmark_&lt;model&gt;.json files - the same data run_benchmarks.py's
/// generate_report.py reports, minus the human-written quality-review prose
/// (that half of the report is deliberately produced by an AI reviewing the
/// BenchmarkExporter zip separately, not generated here - this class only
/// covers what's mechanically derivable from the raw result files).
/// </summary>
public static class BenchmarkReportGenerator
{
    private sealed record TestResult(
        string TestName, string Status, double? TtftSeconds, int? ReasoningTokens,
        int? CompletionTokens, double? TokensPerSecond, int? TotalTokens,
        double? CapabilityScore, bool? SyntaxValid, int? RubricHits, int? RubricTotal, bool? Delivered);

    /// <summary>Reads every benchmark_&lt;model&gt;.json in repoRoot, writes the markdown report to outputPath, and returns the number of models included (0 if none found - no file is written in that case).</summary>
    public static int Generate(string repoRoot, string outputPath)
    {
        var resultFiles = Directory.EnumerateFiles(repoRoot, "benchmark_*.json")
            .Where(f => !Path.GetFileName(f).Equals("benchmark_summary.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (resultFiles.Count == 0) return 0;

        var byModel = new Dictionary<string, List<TestResult>>();
        foreach (var file in resultFiles)
        {
            List<TestResult>? tests;
            try
            {
                tests = ParseFile(file);
            }
            catch (JsonException)
            {
                continue; // malformed/partial file from an interrupted run - skip rather than crash the whole report
            }
            if (tests is not { Count: > 0 }) continue;

            var key = ExtractModelId(file) ?? Path.GetFileNameWithoutExtension(file);
            byModel[key] = tests;
        }
        if (byModel.Count == 0) return 0;

        var sb = new StringBuilder();
        sb.AppendLine("# Benchmark Scoring Matrix");
        sb.AppendLine();
        sb.AppendLine("Generated directly from `benchmark_<model>.json` files - averages and per-test metrics only. " +
                       "`capability_score` is an automated syntax+keyword heuristic, not a verified correctness check; " +
                       "for a human/AI-verified quality review, use \"Export for AI Review\" separately.");
        sb.AppendLine();

        var ranked = byModel
            .Select(kv => (Model: kv.Key, Tests: kv.Value, Avg: Averages(kv.Value)))
            .OrderByDescending(x => x.Avg?.CapabilityScore ?? -1)
            .ToList();

        sb.AppendLine("## Ranked Summary (by average automated capability_score)");
        sb.AppendLine();
        sb.AppendLine("| Rank | Model | Avg Capability | Avg TTFT (s) | Avg Tok/s | Tests OK |");
        sb.AppendLine("|---|---|---|---|---|---|");
        var rank = 1;
        foreach (var (model, tests, avg) in ranked)
        {
            if (avg is null)
            {
                sb.AppendLine($"| {rank++} | {model} | - | - | - | 0/{tests.Count} |");
                continue;
            }
            sb.AppendLine($"| {rank++} | {model} | {avg.CapabilityScore:F3} | {avg.TtftSeconds:F3} | " +
                           $"{avg.TokensPerSecond:F3} | {avg.TestsOk}/{tests.Count} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Per-Model Detail");
        sb.AppendLine();
        foreach (var (model, tests, avg) in ranked)
        {
            sb.AppendLine($"### {model}");
            sb.AppendLine();
            sb.AppendLine("| Test | Status | TTFT (s) | Reasoning tok | Completion tok | Tok/s | Total tok | Capability | Syntax valid | Rubric | Delivered |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var t in tests)
            {
                if (t.Status != "ok")
                {
                    sb.AppendLine($"| {t.TestName} | {t.Status} | - | - | - | - | - | - | - | - | - |");
                    continue;
                }
                sb.AppendLine($"| {t.TestName} | ok | {t.TtftSeconds} | {t.ReasoningTokens} | {t.CompletionTokens} | " +
                               $"{t.TokensPerSecond} | {t.TotalTokens} | {t.CapabilityScore} | {t.SyntaxValid} | " +
                               $"{t.RubricHits}/{t.RubricTotal} | {t.Delivered} |");
            }
            sb.AppendLine();
            if (avg is not null)
            {
                sb.AppendLine($"**Averages across {avg.TestsOk}/{tests.Count} successful tests:** " +
                               $"TTFT={avg.TtftSeconds:F3}s, reasoning_tokens={avg.ReasoningTokens:F1}, " +
                               $"completion_tokens={avg.CompletionTokens:F1}, tokens_per_second={avg.TokensPerSecond:F3}, " +
                               $"total_tokens={avg.TotalTokens:F1}, capability_score={avg.CapabilityScore:F3}, " +
                               $"rubric={avg.RubricHits:F2}/{avg.RubricTotal:F2}, syntax_valid={avg.SyntaxValidCount}/{avg.TestsOk}, " +
                               $"delivered={avg.DeliveredCount}/{avg.TestsOk}.");
                sb.AppendLine();
            }
        }

        File.WriteAllText(outputPath, sb.ToString());
        return byModel.Count;
    }

    private sealed record ModelAverages(
        double TtftSeconds, double ReasoningTokens, double CompletionTokens, double TokensPerSecond,
        double TotalTokens, double CapabilityScore, double RubricHits, double RubricTotal,
        int SyntaxValidCount, int DeliveredCount, int TestsOk);

    private static ModelAverages? Averages(List<TestResult> tests)
    {
        var ok = tests.Where(t => t.Status == "ok").ToList();
        if (ok.Count == 0) return null;
        double Avg(Func<TestResult, double?> sel) => ok.Average(t => sel(t) ?? 0);
        return new ModelAverages(
            TtftSeconds: Avg(t => t.TtftSeconds),
            ReasoningTokens: Avg(t => t.ReasoningTokens),
            CompletionTokens: Avg(t => t.CompletionTokens),
            TokensPerSecond: Avg(t => t.TokensPerSecond),
            TotalTokens: Avg(t => t.TotalTokens),
            CapabilityScore: Avg(t => t.CapabilityScore),
            RubricHits: Avg(t => t.RubricHits),
            RubricTotal: Avg(t => t.RubricTotal),
            SyntaxValidCount: ok.Count(t => t.SyntaxValid == true),
            DeliveredCount: ok.Count(t => t.Delivered != false),
            TestsOk: ok.Count);
    }

    private static string? ExtractModelId(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return null;
        var first = root[0];
        return first.TryGetProperty("model", out var m) ? m.GetString() : null;
    }

    private static List<TestResult> ParseFile(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var results = new List<TestResult>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return results;

        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            var testName = entry.TryGetProperty("test_name", out var tn) ? tn.GetString() ?? "?" : "?";
            var status = entry.TryGetProperty("status", out var st) ? st.GetString() ?? "?" : "?";

            double? GetDouble(string name) => entry.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            int? GetInt(string name) => entry.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

            bool? syntaxValid = null;
            int? rubricHits = null, rubricTotal = null;
            bool? delivered = null;
            if (entry.TryGetProperty("capability_detail", out var detail) && detail.ValueKind == JsonValueKind.Object)
            {
                if (detail.TryGetProperty("syntax_valid", out var sv) && sv.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    syntaxValid = sv.GetBoolean();
                if (detail.TryGetProperty("rubric_hits", out var rh) && rh.ValueKind == JsonValueKind.Number)
                    rubricHits = rh.GetInt32();
                if (detail.TryGetProperty("rubric_total", out var rt) && rt.ValueKind == JsonValueKind.Number)
                    rubricTotal = rt.GetInt32();
                if (detail.TryGetProperty("delivered", out var dl) && dl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    delivered = dl.GetBoolean();
            }

            results.Add(new TestResult(
                testName, status, GetDouble("ttft_seconds"), GetInt("reasoning_tokens"), GetInt("completion_tokens"),
                GetDouble("tokens_per_second"), GetInt("total_tokens"), GetDouble("capability_score"),
                syntaxValid, rubricHits, rubricTotal, delivered));
        }
        return results;
    }
}
