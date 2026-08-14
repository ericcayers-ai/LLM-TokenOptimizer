using TokenOptimizer.Core.Config;
using TokenOptimizer.Core.Projects;

namespace TokenOptimizer.Core.Tests.Projects;

public class ProjectHistoryServiceTests : IDisposable
{
    private readonly string _configDir;
    private readonly string _projectsRoot;
    private readonly ProjectHistoryService _service;

    public ProjectHistoryServiceTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "tokopt-cfg-" + Guid.NewGuid().ToString("N"));
        _projectsRoot = Path.Combine(Path.GetTempPath(), "tokopt-projects-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectsRoot);
        _service = new ProjectHistoryService(new ConfigStore(_configDir));
    }

    [Fact]
    public async Task AddAsync_MostRecentProjectAppearsFirst()
    {
        var a = CreateProjectDir("a");
        var b = CreateProjectDir("b");

        await _service.AddAsync(a);
        await _service.AddAsync(b);

        var history = await _service.GetHistoryAsync();
        Assert.Equal(b, history[0].FullPath);
        Assert.Equal(a, history[1].FullPath);
    }

    [Fact]
    public async Task AddAsync_ReAddingExistingProject_MovesItToFront_NoDuplicate()
    {
        var a = CreateProjectDir("a");
        var b = CreateProjectDir("b");

        await _service.AddAsync(a);
        await _service.AddAsync(b);
        await _service.AddAsync(a);

        var history = await _service.GetHistoryAsync();
        Assert.Equal(2, history.Count);
        Assert.Equal(a, history[0].FullPath);
    }

    [Fact]
    public async Task GetHistoryAsync_SkipsProjectsThatNoLongerExistOnDisk()
    {
        var a = CreateProjectDir("a");
        await _service.AddAsync(a);
        Directory.Delete(a);

        var history = await _service.GetHistoryAsync();
        Assert.Empty(history);
    }

    [Fact]
    public void IsValidProjectDirectory_RejectsDriveRoot()
    {
        var isValid = ProjectHistoryService.IsValidProjectDirectory(@"C:\", out var error);
        Assert.False(isValid);
        Assert.NotNull(error);
    }

    [Fact]
    public void IsValidProjectDirectory_RejectsNonExistentPath()
    {
        var isValid = ProjectHistoryService.IsValidProjectDirectory(
            Path.Combine(_projectsRoot, "does-not-exist"), out var error);
        Assert.False(isValid);
        Assert.NotNull(error);
    }

    [Fact]
    public void IsValidProjectDirectory_AcceptsEmptyWritableFolder()
    {
        var dir = CreateProjectDir("empty");
        var isValid = ProjectHistoryService.IsValidProjectDirectory(dir, out var error);
        Assert.True(isValid);
        Assert.Null(error);
    }

    private string CreateProjectDir(string name)
    {
        var path = Path.Combine(_projectsRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_configDir, recursive: true); } catch { }
        try { Directory.Delete(_projectsRoot, recursive: true); } catch { }
    }
}
