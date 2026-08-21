using System.Text.Json;
using TokenOptimizer.Providers.Claude;

namespace TokenOptimizer.Providers.Tests.Claude;

public sealed class AgencyAgentsInstallerTests : IDisposable
{
    private readonly string _tempDir;

    public AgencyAgentsInstallerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"agency-agents-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private (string repoDir, string claudeConfigDir, string agentsDir) CreateFakeRepo(IReadOnlyList<(string division, string slug, string name, string desc)> agents)
    {
        var repoDir = Path.Combine(_tempDir, "repo");
        var divisions = new Dictionary<string, List<string>>();

        foreach (var (division, slug, name, desc) in agents)
        {
            var divDir = Path.Combine(repoDir, division);
            Directory.CreateDirectory(divDir);
            File.WriteAllText(Path.Combine(divDir, $"{slug}.md"), $"---\nname: {name}\ndescription: {desc}\n---\n\n# {name}\n\nContent here.");

            if (!divisions.ContainsKey(division))
                divisions[division] = [];
            divisions[division].Add(slug);
        }

        File.WriteAllText(Path.Combine(repoDir, "divisions.json"), JsonSerializer.Serialize(divisions, new JsonSerializerOptions { WriteIndented = true }));

        var claudeConfigDir = Path.Combine(_tempDir, "claude-config");
        var agentsDir = Path.Combine(claudeConfigDir, "agents");
        Directory.CreateDirectory(agentsDir);

        return (repoDir, claudeConfigDir, agentsDir);
    }

    private AgencyAgentsInstaller CreateInstaller(Core.Config.ConfigStore configStore, string repoDir, string claudeConfigDir)
    {
        return new AgencyAgentsInstaller(configStore, new Core.Diagnostics.CommandAvailability())
        {
            RepoDirOverride = repoDir,
            AgentsDirOverride = claudeConfigDir,
        };
    }

    [Fact]
    public void ParseFrontmatter_ExtractsNameAndDescription()
    {
        var dir = Path.Combine(_tempDir, "div", "slug");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "slug.md"), "---\nname: My Agent\ndescription: Does things\n---\n\nBody");

        var (name, description) = AgencyAgentsInstaller.ParseFrontmatter(Path.Combine(dir, "slug.md"));

        Assert.Equal("My Agent", name);
        Assert.Equal("Does things", description);
    }

    [Fact]
    public void ParseFrontmatter_MissingFrontmatter_ReturnsNulls()
    {
        var file = Path.Combine(_tempDir, "plain.md");
        File.WriteAllText(file, "# Just a heading\n\nNo frontmatter here.");

        var (name, description) = AgencyAgentsInstaller.ParseFrontmatter(file);

        Assert.Null(name);
        Assert.Null(description);
    }

    [Fact]
    public void ParseFrontmatter_QuotedValues_UnquotesCorrectly()
    {
        var dir = Path.Combine(_tempDir, "div", "slug");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "slug.md"), "---\nname: \"Quoted Agent\"\ndescription: 'Single quoted desc'\n---\n\nBody");

        var (name, description) = AgencyAgentsInstaller.ParseFrontmatter(Path.Combine(dir, "slug.md"));

        Assert.Equal("Quoted Agent", name);
        Assert.Equal("Single quoted desc", description);
    }

    [Fact]
    public async Task SyncTickedAgentsAsync_CopiesTickedAndRemovesUnticked()
    {
        var (repoDir, claudeConfigDir, agentsDir) = CreateFakeRepo([
            ("research", "analyst", "Analyst", "Analyzes stuff"),
            ("ops", "runner", "Runner", "Runs things"),
        ]);

        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        var configStore = new Core.Config.ConfigStore(configDir);

        var installer = CreateInstaller(configStore, repoDir, claudeConfigDir);

        var synced = await installer.SyncTickedAgentsAsync(["analyst"]);

        Assert.Equal(1, synced);
        Assert.True(File.Exists(Path.Combine(agentsDir, "analyst.md")));
        Assert.False(File.Exists(Path.Combine(agentsDir, "runner.md")));

        var manifestRaw = await File.ReadAllTextAsync(Path.Combine(agentsDir, ".agency-agents-synced.json"));
        var manifest = JsonSerializer.Deserialize<List<string>>(manifestRaw)!;
        Assert.Single(manifest);
        Assert.Equal("analyst", manifest[0]);
    }

    [Fact]
    public async Task SyncTickedAgentsAsync_RemovesUntickedThatWerePreviouslySynced()
    {
        var (repoDir, claudeConfigDir, agentsDir) = CreateFakeRepo([
            ("research", "analyst", "Analyst", "Analyzes stuff"),
            ("ops", "runner", "Runner", "Runs things"),
        ]);

        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        var configStore = new Core.Config.ConfigStore(configDir);

        var installer = CreateInstaller(configStore, repoDir, claudeConfigDir);

        await installer.SyncTickedAgentsAsync(["analyst", "runner"]);
        Assert.True(File.Exists(Path.Combine(agentsDir, "analyst.md")));
        Assert.True(File.Exists(Path.Combine(agentsDir, "runner.md")));

        var synced2 = await installer.SyncTickedAgentsAsync(["analyst"]);
        Assert.Equal(1, synced2);
        Assert.False(File.Exists(Path.Combine(agentsDir, "runner.md")));
    }

    [Fact]
    public async Task SyncTickedAgentsAsync_EmptyTicked_RemovesAllPreviouslySynced()
    {
        var (repoDir, claudeConfigDir, agentsDir) = CreateFakeRepo([
            ("research", "analyst", "Analyst", "Analyzes stuff"),
        ]);

        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        var configStore = new Core.Config.ConfigStore(configDir);

        var installer = CreateInstaller(configStore, repoDir, claudeConfigDir);

        await installer.SyncTickedAgentsAsync(["analyst"]);
        Assert.True(File.Exists(Path.Combine(agentsDir, "analyst.md")));

        var synced = await installer.SyncTickedAgentsAsync([]);
        Assert.Equal(0, synced);
        Assert.False(File.Exists(Path.Combine(agentsDir, "analyst.md")));
    }

    [Fact]
    public async Task ListAvailableAgentsAsync_MissingRepo_ReturnsEmpty()
    {
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        var configStore = new Core.Config.ConfigStore(configDir);

        var installer = CreateInstaller(configStore, Path.Combine(_tempDir, "nonexistent"), _tempDir);

        var agents = await installer.ListAvailableAgentsAsync();

        Assert.Empty(agents);
    }

    [Fact]
    public async Task ListAvailableAgentsAsync_MissingDivisionsJson_ReturnsEmpty()
    {
        var repoDir = Path.Combine(_tempDir, "empty-repo");
        Directory.CreateDirectory(repoDir);

        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        var configStore = new Core.Config.ConfigStore(configDir);

        var installer = CreateInstaller(configStore, repoDir, _tempDir);

        var agents = await installer.ListAvailableAgentsAsync();

        Assert.Empty(agents);
    }

    [Fact]
    public async Task SyncTickedAgentsAsync_MissingRepo_ReturnsZero()
    {
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        var configStore = new Core.Config.ConfigStore(configDir);

        var installer = CreateInstaller(configStore, Path.Combine(_tempDir, "nonexistent"), _tempDir);

        var synced = await installer.SyncTickedAgentsAsync(["anything"]);

        Assert.Equal(0, synced);
    }
}
