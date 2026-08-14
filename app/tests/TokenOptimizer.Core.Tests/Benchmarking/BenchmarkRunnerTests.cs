using TokenOptimizer.Core.Benchmarking;

namespace TokenOptimizer.Core.Tests.Benchmarking;

public class BenchmarkRunnerTests : IDisposable
{
    private readonly string _tempDir;

    public BenchmarkRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tokopt-benchrunner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void ListCatalogModels_ReturnsEmpty_WhenScriptMissing()
    {
        Assert.Empty(BenchmarkRunner.ListCatalogModels(_tempDir));
    }

    [Fact]
    public void ListCatalogModels_ExtractsIdsFromModelListArray_DedupedAndOrdered()
    {
        File.WriteAllText(Path.Combine(_tempDir, "run_benchmarks.py"), """
        MODEL_LIST = [
            {"id": "qwen/qwen3.6-35b-a3b", "size_gb": 22},
            {"id": "qwen/qwen3-coder-30b", "size_gb": 19},
            {"id": "qwen/qwen3.6-35b-a3b", "size_gb": 22},
        ]
        """);

        var models = BenchmarkRunner.ListCatalogModels(_tempDir);

        Assert.Equal(2, models.Count);
        Assert.Contains("qwen/qwen3.6-35b-a3b", models);
        Assert.Contains("qwen/qwen3-coder-30b", models);
    }

    [Fact]
    public void ListHeavyModelIds_FindsReasoningModelsNeedingBigBudget()
    {
        File.WriteAllText(Path.Combine(_tempDir, "run_benchmarks.py"), """
        MODEL_CONFIG = {
            "qwen/qwen3.6-35b-a3b": {"temperature": 0.6, "top_p": 0.95, "max_tokens": 12000, "context_length": 32768, "disable_thinking": True},
            "qwen/qwen3-coder-30b": {"temperature": 0.7, "top_p": 0.8, "max_tokens": 6000, "context_length": 16384, "disable_thinking": False},
            "openai/gpt-oss-20b": {"temperature": 0.7, "top_p": None, "max_tokens": 6000, "context_length": 16384, "disable_thinking": False},
        }
        """);

        var heavy = BenchmarkRunner.ListHeavyModelIds(_tempDir);

        Assert.Contains("qwen/qwen3.6-35b-a3b", heavy);
        Assert.DoesNotContain("qwen/qwen3-coder-30b", heavy);
        Assert.DoesNotContain("openai/gpt-oss-20b", heavy);
    }

    [Fact]
    public void HeavyModelExclusion_LeavesOnlyLightModels_ForFasterTiers()
    {
        // The logic RunAsync applies when the user picks "all models" at
        // Balanced/Quick: catalog minus heavy models. Verified directly
        // here since spying on the launched process's argv needs a process
        // fake RunAsync doesn't expose.
        File.WriteAllText(Path.Combine(_tempDir, "run_benchmarks.py"), """
        MODEL_LIST = [
            {"id": "qwen/qwen3.6-35b-a3b", "size_gb": 22},
            {"id": "qwen/qwen3-coder-30b", "size_gb": 19},
        ]
        MODEL_CONFIG = {
            "qwen/qwen3.6-35b-a3b": {"temperature": 0.6, "top_p": 0.95, "max_tokens": 12000, "context_length": 32768, "disable_thinking": True},
        }
        """);

        var heavy = BenchmarkRunner.ListHeavyModelIds(_tempDir);
        var all = BenchmarkRunner.ListCatalogModels(_tempDir);
        var light = all.Where(id => !heavy.Contains(id)).ToList();

        Assert.Single(light);
        Assert.Equal("qwen/qwen3-coder-30b", light[0]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
