using TokenOptimizer.Core.Config;
using TokenOptimizer.Core.Models;

namespace TokenOptimizer.Core.Tests.Config;

public class ConfigStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ConfigStore _store;

    public ConfigStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tokopt-tests-" + Guid.NewGuid().ToString("N"));
        _store = new ConfigStore(_tempDir);
    }

    [Fact]
    public async Task LoadAsync_WhenNoFileExists_ReturnsDefaultConfig()
    {
        var config = await _store.LoadAsync();
        Assert.NotNull(config);
        Assert.Empty(config.ProjectHistory);
        Assert.Null(config.ClaudePath);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsAllFields()
    {
        var config = new AppConfig
        {
            MasterFolder = @"C:\projects",
            ClaudePath = @"C:\claude\claude.exe",
            HeadroomInstalled = true,
            PreferredModel = "sonnet",
        };
        config.ProjectHistory.Add(@"C:\projects\one");
        config.ProjectHistory.Add(@"C:\projects\two");

        await _store.SaveAsync(config);
        var reloaded = await _store.LoadAsync();

        Assert.Equal(config.MasterFolder, reloaded.MasterFolder);
        Assert.Equal(config.ClaudePath, reloaded.ClaudePath);
        Assert.True(reloaded.HeadroomInstalled);
        Assert.Equal("sonnet", reloaded.PreferredModel);
        Assert.Equal(config.ProjectHistory, reloaded.ProjectHistory);
    }

    [Fact]
    public async Task SaveAsync_OverwritesPreviousContentAtomically()
    {
        await _store.SaveAsync(new AppConfig { ClaudePath = "first" });
        await _store.SaveAsync(new AppConfig { ClaudePath = "second" });

        var reloaded = await _store.LoadAsync();
        Assert.Equal("second", reloaded.ClaudePath);
        Assert.False(File.Exists(_store.ConfigPath + ".tmp"));
    }

    [Fact]
    public async Task UpdateAsync_AppliesMutationAndPersistsIt()
    {
        await _store.UpdateAsync(c => c.ClaudePath = "initial");
        await _store.UpdateAsync(c => c.PreferredModel = "opus");

        var reloaded = await _store.LoadAsync();
        Assert.Equal("initial", reloaded.ClaudePath);
        Assert.Equal("opus", reloaded.PreferredModel);
    }

    [Fact]
    public async Task UpdateAsync_ManyConcurrentCallers_LoseNoUpdates()
    {
        // Simulates several near-simultaneous callers each appending their
        // own project to history - a plain Load-then-Save race would let
        // later writers clobber earlier ones. UpdateAsync must not.
        var tasks = Enumerable.Range(0, 20)
            .Select(i => _store.UpdateAsync(c => c.ProjectHistory.Add($"project-{i}")));
        await Task.WhenAll(tasks);

        var reloaded = await _store.LoadAsync();
        Assert.Equal(20, reloaded.ProjectHistory.Count);
        Assert.Equal(20, reloaded.ProjectHistory.Distinct().Count());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
