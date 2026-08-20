using TokenOptimizer.Providers.Claude;

namespace TokenOptimizer.Providers.Tests.Claude;

public sealed class SkillCatalogServiceTests : IDisposable
{
    private readonly string _tempDir;

    public SkillCatalogServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void ListSkillGuide_WithCustomClaudeConfigDir_ListsSkillsFromCustomDir()
    {
        var skillsDir = Path.Combine(_tempDir, "skills", "test-skill");
        Directory.CreateDirectory(skillsDir);
        File.WriteAllText(Path.Combine(skillsDir, "SKILL.md"), "---\nname: test-skill\ndescription: A test skill\n---\n\n# Test");

        var entries = SkillCatalogService.ListSkillGuide(_tempDir);

        Assert.Contains(entries, e => e.Name == "test-skill");
    }

    [Fact]
    public void ListPluginGuide_WithCustomClaudeConfigDir_ListsPluginsFromCustomDir()
    {
        var pluginDir = Path.Combine(_tempDir, "plugins", "cache", "market", "plugin", ".claude-plugin");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), "{\"name\":\"test-plugin\",\"description\":\"A test plugin\"}");

        var entries = SkillCatalogService.ListPluginGuide(_tempDir);

        Assert.Contains(entries, e => e.Name == "test-plugin");
    }
}
