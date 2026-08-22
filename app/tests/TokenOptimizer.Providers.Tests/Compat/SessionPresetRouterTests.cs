using TokenOptimizer.Providers.Compat;

namespace TokenOptimizer.Providers.Tests.Compat;

/// <summary>
/// Session preset routing is the automatic replacement for the removed manual
/// "Session type" card: the UserPromptSubmit hook (and /preset command) write
/// session-preset.json, and the router's ResolveAutoFallbackRouteAsync reads it
/// before every __auto__ resolution to bias the ProviderFit ranking. These
/// tests pin the two halves - keyword inference + state-file read (what the
/// hook writes) and the ranking bias (what the router consumes).
/// </summary>
public class SessionPresetRouterTests
{
    [Theory]
    [InlineData("/plan", SessionPresetIntent.Planning, SessionPresetTier.Quality)]
    [InlineData("/plan design the architecture", SessionPresetIntent.Planning, SessionPresetTier.Quality)]
    [InlineData("/build", SessionPresetIntent.Execution, SessionPresetTier.Balanced)]
    [InlineData("write the roadmap for this system", SessionPresetIntent.Planning, SessionPresetTier.Quality)]
    [InlineData("research the latest paper", SessionPresetIntent.Planning, SessionPresetTier.Quality)]
    [InlineData("long-horizon agentic workflow", SessionPresetIntent.Execution, SessionPresetTier.Balanced)]
    [InlineData("fix this bug in the debugger", SessionPresetIntent.Execution, SessionPresetTier.CostEffective)]
    [InlineData("ordinary prompt", SessionPresetIntent.Execution, SessionPresetTier.Balanced)]
    public void InferFromPrompt_MapsKeywordDefaults(string prompt, SessionPresetIntent expectedIntent, SessionPresetTier expectedTier)
    {
        var preset = SessionPresetStore.InferFromPrompt(prompt);
        Assert.Equal(expectedIntent, preset.Intent);
        Assert.Equal(expectedTier, preset.Tier);
    }

    [Fact]
    public void ReadOrDefault_MissingFile_ReturnsBalancedExecutionDefault()
    {
        var dir = CreateTempDir();
        try
        {
            var preset = SessionPresetStore.ReadFrom(Path.Combine(dir, "session-preset.json"));
            Assert.Equal(SessionPresetIntent.Execution, preset.Intent);
            Assert.Equal(SessionPresetTier.Balanced, preset.Tier);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadFrom_QualityFile_ParsesIntentAndPreset()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "session-preset.json");
            SessionPresetStore.WriteTo(path, new SessionPreset(SessionPresetIntent.Planning, SessionPresetTier.Quality));

            var preset = SessionPresetStore.ReadFrom(path);
            Assert.Equal(SessionPresetIntent.Planning, preset.Intent);
            Assert.Equal(SessionPresetTier.Quality, preset.Tier);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Preset=Quality must prefer the higher-ReasoningScore provider - the core of ResolveAutoFallbackRouteAsync's live bias.</summary>
    [Fact]
    public void Rank_QualityPreset_PrefersHigherReasoningScoreProvider()
    {
        var candidates = new[] { "Fast Cheap Provider", "Smart Premium Provider" };
        var fit = new Dictionary<string, ProviderFitScore>
        {
            ["Fast Cheap Provider"] = new(0.30, 0.95, ModelCostTier.Cheap),
            ["Smart Premium Provider"] = new(0.95, 0.40, ModelCostTier.Premium),
        };

        var ranked = SessionPresetRanker.Rank(candidates, name => fit[name], new SessionPreset(SessionPresetIntent.Planning, SessionPresetTier.Quality));

        Assert.Equal("Smart Premium Provider", ranked[0]);
    }

    /// <summary>Preset=Cost-effective must prefer the cheaper/faster provider - the reverse of Quality.</summary>
    [Fact]
    public void Rank_CostEffectivePreset_PrefersCheaperProvider()
    {
        var candidates = new[] { "Fast Cheap Provider", "Smart Premium Provider" };
        var fit = new Dictionary<string, ProviderFitScore>
        {
            ["Fast Cheap Provider"] = new(0.30, 0.95, ModelCostTier.Cheap),
            ["Smart Premium Provider"] = new(0.95, 0.40, ModelCostTier.Premium),
        };

        var ranked = SessionPresetRanker.Rank(candidates, name => fit[name], new SessionPreset(SessionPresetIntent.Execution, SessionPresetTier.CostEffective));

        Assert.Equal("Fast Cheap Provider", ranked[0]);
    }

    /// <summary>Quality excludes Cheap-tier providers entirely (they fall to the end of the order, after every allowed one).</summary>
    [Fact]
    public void Rank_QualityPreset_FiltersCheapProvidersBehindAllowedOnes()
    {
        var candidates = new[] { "Cheap Fast", "Balanced Mid" };
        var fit = new Dictionary<string, ProviderFitScore>
        {
            ["Cheap Fast"] = new(0.20, 0.90, ModelCostTier.Cheap),
            ["Balanced Mid"] = new(0.60, 0.60, ModelCostTier.Balanced),
        };

        var ranked = SessionPresetRanker.Rank(candidates, name => fit[name], new SessionPreset(SessionPresetIntent.Execution, SessionPresetTier.Quality));

        Assert.Equal("Balanced Mid", ranked[0]);
        Assert.Equal("Cheap Fast", ranked[^1]);
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "session-preset-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        return dir;
    }
}