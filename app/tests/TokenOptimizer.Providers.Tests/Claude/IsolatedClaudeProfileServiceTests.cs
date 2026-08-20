using TokenOptimizer.Providers.Claude;

namespace TokenOptimizer.Providers.Tests.Claude;

public sealed class IsolatedClaudeProfileServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sourceClaude;
    private readonly string _projectDir;

    public IsolatedClaudeProfileServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        _sourceClaude = Path.Combine(_tempDir, "source-claude");
        _projectDir = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(_sourceClaude);
        Directory.CreateDirectory(_projectDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void GetOrCreateProfileDir_NewProfile_SeedsFromSource()
    {
        var skillsDir = Path.Combine(_sourceClaude, "skills", "seed-skill");
        Directory.CreateDirectory(skillsDir);
        File.WriteAllText(Path.Combine(skillsDir, "SKILL.md"), "# Seed");

        var profileDir = IsolatedClaudeProfileService.GetOrCreateProfileDir(_projectDir, _sourceClaude);

        Assert.True(Directory.Exists(profileDir));
        Assert.True(File.Exists(Path.Combine(profileDir, "skills", "seed-skill", "SKILL.md")));
    }

    [Fact]
    public void GetOrCreateProfileDir_ExistingProfile_ReSyncsNewSkills()
    {
        var seedSkillsDir = Path.Combine(_sourceClaude, "skills", "seed-skill");
        Directory.CreateDirectory(seedSkillsDir);
        File.WriteAllText(Path.Combine(seedSkillsDir, "SKILL.md"), "# Seed");

        IsolatedClaudeProfileService.GetOrCreateProfileDir(_projectDir, _sourceClaude);

        var newSkillsDir = Path.Combine(_sourceClaude, "skills", "new-skill");
        Directory.CreateDirectory(newSkillsDir);
        File.WriteAllText(Path.Combine(newSkillsDir, "SKILL.md"), "# New");

        var profileDir = IsolatedClaudeProfileService.GetOrCreateProfileDir(_projectDir, _sourceClaude);

        Assert.True(File.Exists(Path.Combine(profileDir, "skills", "new-skill", "SKILL.md")));
    }

    [Fact]
    public void GetOrCreateProfileDir_ExistingProfile_ReSyncsInstalledPluginsJson()
    {
        IsolatedClaudeProfileService.GetOrCreateProfileDir(_projectDir, _sourceClaude);

        Directory.CreateDirectory(Path.Combine(_sourceClaude, "plugins"));
        File.WriteAllText(Path.Combine(_sourceClaude, "plugins", "installed_plugins.json"), "{\"version\":1}");

        var profileDir = IsolatedClaudeProfileService.GetOrCreateProfileDir(_projectDir, _sourceClaude);

        Assert.Equal("{\"version\":1}", File.ReadAllText(Path.Combine(profileDir, "plugins", "installed_plugins.json")));
    }
}
