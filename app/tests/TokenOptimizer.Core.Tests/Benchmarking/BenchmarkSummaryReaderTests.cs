using TokenOptimizer.Core.Benchmarking;
using TokenOptimizer.Core.Config;

namespace TokenOptimizer.Core.Tests.Benchmarking;

public class BenchmarkSummaryReaderTests : IDisposable
{
    private readonly string _tempDir;

    public BenchmarkSummaryReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tokopt-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void ReadRows_ReturnsEmpty_WhenFileMissing()
    {
        var rows = BenchmarkSummaryReader.ReadRows(Path.Combine(_tempDir, "missing.json"));
        Assert.Empty(rows);
    }

    [Fact]
    public async Task RefreshBestLocalModelAsync_PicksHighestCompositeScore_NotHighestSpeed()
    {
        // A faster model with worse composite score should lose - composite
        // is weighted 70% capability / 30% speed, not pure throughput.
        var summaryPath = Path.Combine(_tempDir, "benchmark_summary.json");
        File.WriteAllText(summaryPath, """
        [
            {"model": "fast-but-broken", "stage": "benchmark", "status": "ok", "avg_tokens_per_second": 90.0, "resolve_rate": 0.2, "composite_score": 0.35},
            {"model": "slower-but-correct", "stage": "benchmark", "status": "ok", "avg_tokens_per_second": 40.0, "resolve_rate": 0.9, "composite_score": 0.75},
            {"model": "irrelevant-download-row", "stage": "download", "status": "ok"}
        ]
        """);

        var reader = new BenchmarkSummaryReader(new ConfigStore(_tempDir));
        var best = await reader.RefreshBestLocalModelAsync(summaryPath);

        Assert.NotNull(best);
        Assert.Equal("slower-but-correct", best!.Model);
    }

    [Fact]
    public async Task RefreshBestLocalModelAsync_FallsBackToSpeed_WhenCompositeScoreMissing()
    {
        var summaryPath = Path.Combine(_tempDir, "benchmark_summary.json");
        File.WriteAllText(summaryPath, """
        [
            {"model": "model-a", "stage": "benchmark", "status": "ok", "avg_tokens_per_second": 20.0},
            {"model": "model-b", "stage": "benchmark", "status": "ok", "avg_tokens_per_second": 55.0}
        ]
        """);

        var reader = new BenchmarkSummaryReader(new ConfigStore(_tempDir));
        var best = await reader.RefreshBestLocalModelAsync(summaryPath);

        Assert.NotNull(best);
        Assert.Equal("model-b", best!.Model);
    }

    [Fact]
    public async Task RefreshBestLocalModelAsync_ReturnsNull_WhenNoSuccessfulBenchmarkRows()
    {
        var summaryPath = Path.Combine(_tempDir, "benchmark_summary.json");
        File.WriteAllText(summaryPath, """
        [
            {"model": "model-a", "stage": "benchmark", "status": "failed", "detail": "timeout"}
        ]
        """);

        var reader = new BenchmarkSummaryReader(new ConfigStore(_tempDir));
        var best = await reader.RefreshBestLocalModelAsync(summaryPath);

        Assert.Null(best);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
