using TokenOptimizer.Core.Config;
using TokenOptimizer.Core.Models;
using TokenOptimizer.Providers;
using TokenOptimizer.Providers.LlamaCpp;

namespace TokenOptimizer.Providers.Tests;

public sealed class LlamaCppInContainerGateTests
{
    [Fact]
    public async Task LaunchSessionAsync_NonRollingPreset_WithoutOptInEnvVar_ThrowsClearError()
    {
        using var _ = UnsetEnvVar(LlamaCppAdapter.AllowInContainerModelLoadEnvVar);
        var configDir = CreateTempDir();
        var family = LlamaCppModelCatalog.SupportedFamilies[0];
        var presets = new LlamaCppPresetStore(configDir);
        await presets.SaveAsync(family.RepoId, family.RecommendedQuant,
            new LlamaCppLaunchOptions { RollingContextWindowEnabled = false });
        var adapter = new LlamaCppAdapter(presets);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.LaunchSessionAsync(new SessionLaunchOptions(
                CreateProjectDir(), $"{family.RepoId}:{family.RecommendedQuant}")));

        Assert.Contains(LlamaCppAdapter.AllowInContainerModelLoadEnvVar, ex.Message);
    }

    [Fact]
    public void InContainerModelLoadAllowed_FollowsOptInEnvVar()
    {
        Environment.SetEnvironmentVariable(LlamaCppAdapter.AllowInContainerModelLoadEnvVar, "1");
        try
        {
            Assert.True(LlamaCppAdapter.InContainerModelLoadAllowed);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LlamaCppAdapter.AllowInContainerModelLoadEnvVar, null);
        }

        Assert.False(LlamaCppAdapter.InContainerModelLoadAllowed);
    }

    private static IDisposable UnsetEnvVar(string name)
    {
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, null);
        return new RestoreEnvVar(name, previous);
    }

    private sealed class RestoreEnvVar(string name, string? previous) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable(name, previous);
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateProjectDir() => CreateTempDir();
}
