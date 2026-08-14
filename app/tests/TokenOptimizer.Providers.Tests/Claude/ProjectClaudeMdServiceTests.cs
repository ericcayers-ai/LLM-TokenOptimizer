using TokenOptimizer.Providers.Claude;

namespace TokenOptimizer.Providers.Tests.Claude;

public class ProjectClaudeMdServiceTests : IDisposable
{
    private readonly string _tempDir;

    public ProjectClaudeMdServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tokopt-claudemd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void CheckClaudeMdBloat_ReturnsNull_WhenNoClaudeMdExists()
    {
        Assert.Null(ProjectClaudeMdService.CheckClaudeMdBloat(_tempDir));
    }

    [Fact]
    public void CheckClaudeMdBloat_ReturnsNull_WhenUnderThreshold()
    {
        File.WriteAllLines(Path.Combine(_tempDir, "CLAUDE.md"), Enumerable.Repeat("line", 50));
        Assert.Null(ProjectClaudeMdService.CheckClaudeMdBloat(_tempDir));
    }

    [Fact]
    public void CheckClaudeMdBloat_WarnsAtOrAboveThreshold()
    {
        File.WriteAllLines(Path.Combine(_tempDir, "CLAUDE.md"), Enumerable.Repeat("line", 300));
        var warning = ProjectClaudeMdService.CheckClaudeMdBloat(_tempDir);
        Assert.NotNull(warning);
        Assert.Contains("300 lines", warning);
    }

    [Fact]
    public void ExceedsGraphifyThreshold_FalseForSmallProject()
    {
        File.WriteAllText(Path.Combine(_tempDir, "a.txt"), "x");
        File.WriteAllText(Path.Combine(_tempDir, "b.txt"), "x");
        Assert.False(ProjectClaudeMdService.ExceedsGraphifyThreshold(_tempDir));
    }

    [Fact]
    public void ExceedsGraphifyThreshold_ExcludesNodeModulesFromCount()
    {
        var nodeModules = Directory.CreateDirectory(Path.Combine(_tempDir, "node_modules"));
        for (var i = 0; i < 200; i++)
        {
            File.WriteAllText(Path.Combine(nodeModules.FullName, $"f{i}.js"), "x");
        }
        File.WriteAllText(Path.Combine(_tempDir, "real.txt"), "x");

        Assert.False(ProjectClaudeMdService.ExceedsGraphifyThreshold(_tempDir));
    }

    [Fact]
    public void EnsureDirective_CreatesClaudeMd_WhenMissing()
    {
        ProjectClaudeMdService.EnsureDirective(_tempDir, useGraphify: false);

        var claudeMdPath = Path.Combine(_tempDir, "CLAUDE.md");
        Assert.True(File.Exists(claudeMdPath));
        var content = File.ReadAllText(claudeMdPath);
        Assert.Contains("# Companion tooling", content);
        Assert.DoesNotContain("# Graphify enforcement", content);
    }

    [Fact]
    public void EnsureDirective_IncludesGraphifySection_WhenRequested()
    {
        ProjectClaudeMdService.EnsureDirective(_tempDir, useGraphify: true);

        var content = File.ReadAllText(Path.Combine(_tempDir, "CLAUDE.md"));
        Assert.Contains("# Graphify enforcement", content);
        Assert.Contains("# Companion tooling", content);
    }

    [Fact]
    public void EnsureDirective_DoesNotDuplicateSections_WhenCalledTwice()
    {
        ProjectClaudeMdService.EnsureDirective(_tempDir, useGraphify: true);
        ProjectClaudeMdService.EnsureDirective(_tempDir, useGraphify: true);

        var content = File.ReadAllText(Path.Combine(_tempDir, "CLAUDE.md"));
        var occurrences = content.Split("# Companion tooling").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void EnsureDirective_PreservesExistingContent_WhenMerging()
    {
        File.WriteAllText(Path.Combine(_tempDir, "CLAUDE.md"), "# My existing project notes\n\nSome content.");

        ProjectClaudeMdService.EnsureDirective(_tempDir, useGraphify: false);

        var content = File.ReadAllText(Path.Combine(_tempDir, "CLAUDE.md"));
        Assert.Contains("My existing project notes", content);
        Assert.Contains("# Companion tooling", content);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
