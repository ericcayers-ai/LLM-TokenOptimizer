using TokenOptimizer.Core.Diagnostics;

namespace TokenOptimizer.Core.Tests.Diagnostics;

public class CacheManagementServiceTests : IDisposable
{
    private readonly string _profilesRoot;

    public CacheManagementServiceTests()
    {
        _profilesRoot = Path.Combine(Path.GetTempPath(), "tokopt-cache-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_profilesRoot)) Directory.Delete(_profilesRoot, recursive: true);
    }

    private string ProfileDir(string name)
    {
        var dir = Path.Combine(_profilesRoot, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ListClaudeProfiles_ReturnsEmpty_WhenProfilesRootMissing()
    {
        Assert.Empty(CacheManagementService.ListClaudeProfiles(_profilesRoot));
    }

    [Fact]
    public void ListClaudeProfiles_SizesEachProfileDirectory()
    {
        var dirA = ProfileDir("project-a");
        File.WriteAllText(Path.Combine(dirA, "settings.json"), new string('x', 100));

        var entries = CacheManagementService.ListClaudeProfiles(_profilesRoot);

        var entry = Assert.Single(entries);
        Assert.Equal("project-a", entry.Name);
        Assert.Equal(100, entry.SizeBytes);
    }

    [Fact]
    public void DeleteStaleProfiles_RemovesOnlyProfilesOlderThanMaxAge()
    {
        var freshDir = ProfileDir("fresh");
        File.WriteAllText(Path.Combine(freshDir, "settings.json"), "fresh");

        var staleDir = ProfileDir("stale");
        var staleFile = Path.Combine(staleDir, "settings.json");
        File.WriteAllText(staleFile, "stale");
        File.SetLastWriteTimeUtc(staleFile, DateTime.UtcNow.AddDays(-40));

        var removed = CacheManagementService.DeleteStaleProfiles(TimeSpan.FromDays(30), _profilesRoot);

        Assert.Equal(1, removed);
        var remaining = CacheManagementService.ListClaudeProfiles(_profilesRoot);
        var remainingEntry = Assert.Single(remaining);
        Assert.Equal("fresh", remainingEntry.Name);
    }

    [Fact]
    public void ClearAllProfiles_RemovesEveryProfile()
    {
        ProfileDir("a");
        ProfileDir("b");

        CacheManagementService.ClearAllProfiles(_profilesRoot);

        Assert.Empty(CacheManagementService.ListClaudeProfiles(_profilesRoot));
    }
}
