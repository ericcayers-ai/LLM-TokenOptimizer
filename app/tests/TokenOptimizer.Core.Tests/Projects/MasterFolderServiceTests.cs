using TokenOptimizer.Core.Config;
using TokenOptimizer.Core.Projects;

namespace TokenOptimizer.Core.Tests.Projects;

public class MasterFolderServiceTests : IDisposable
{
    private readonly string _configDir;
    private readonly string _masterFolder;
    private readonly MasterFolderService _service;
    private readonly ProjectHistoryService _projectHistory;

    public MasterFolderServiceTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "tokopt-mf-cfg-" + Guid.NewGuid().ToString("N"));
        _masterFolder = Path.Combine(Path.GetTempPath(), "tokopt-mf-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_masterFolder);
        var configStore = new ConfigStore(_configDir);
        _projectHistory = new ProjectHistoryService(configStore);
        _service = new MasterFolderService(configStore, _projectHistory);
    }

    [Fact]
    public async Task SetThenGetMasterFolder_RoundTrips()
    {
        await _service.SetMasterFolderAsync(_masterFolder);
        var result = await _service.GetMasterFolderAsync();
        Assert.Equal(Path.GetFullPath(_masterFolder), result);
    }

    [Fact]
    public async Task ListCandidatesAsync_ReturnsImmediateSubfoldersOnly()
    {
        var projectA = Directory.CreateDirectory(Path.Combine(_masterFolder, "project-a"));
        Directory.CreateDirectory(Path.Combine(projectA.FullName, "nested"));
        Directory.CreateDirectory(Path.Combine(_masterFolder, "project-b"));

        var candidates = await _service.ListCandidatesAsync(_masterFolder);

        Assert.Equal(2, candidates.Count);
        Assert.Contains(candidates, c => c.Name == "project-a");
        Assert.Contains(candidates, c => c.Name == "project-b");
    }

    [Fact]
    public async Task ListCandidatesAsync_MarksProjectsAlreadyInHistoryAsSeenBefore()
    {
        var projectA = Directory.CreateDirectory(Path.Combine(_masterFolder, "project-a"));
        Directory.CreateDirectory(Path.Combine(_masterFolder, "project-b"));
        await _projectHistory.AddAsync(projectA.FullName);

        var candidates = await _service.ListCandidatesAsync(_masterFolder);

        Assert.True(candidates.Single(c => c.Name == "project-a").SeenBefore);
        Assert.False(candidates.Single(c => c.Name == "project-b").SeenBefore);
    }

    [Fact]
    public void CreateProjectFolder_RejectsInvalidFileNameCharacters()
    {
        var result = MasterFolderService.CreateProjectFolder(_masterFolder, "bad:name");
        Assert.Null(result);
    }

    [Fact]
    public void CreateProjectFolder_CreatesNewEmptyFolder()
    {
        var result = MasterFolderService.CreateProjectFolder(_masterFolder, "new-project");
        Assert.NotNull(result);
        Assert.True(Directory.Exists(result));
    }

    [Fact]
    public void CreateProjectFolder_ReturnsExistingPath_WhenAlreadyExists()
    {
        var existing = Directory.CreateDirectory(Path.Combine(_masterFolder, "already-there"));
        var result = MasterFolderService.CreateProjectFolder(_masterFolder, "already-there");
        Assert.Equal(existing.FullName, result);
    }

    [Fact]
    public void IsValidMasterFolder_RejectsNonExistentPath()
    {
        var isValid = MasterFolderService.IsValidMasterFolder(Path.Combine(_masterFolder, "nope"), out var error);
        Assert.False(isValid);
        Assert.NotNull(error);
    }

    public void Dispose()
    {
        try { Directory.Delete(_configDir, recursive: true); } catch { }
        try { Directory.Delete(_masterFolder, recursive: true); } catch { }
    }
}
