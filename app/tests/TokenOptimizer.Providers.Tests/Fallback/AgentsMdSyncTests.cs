using TokenOptimizer.Providers.Fallback;

namespace TokenOptimizer.Providers.Tests.Fallback;

public class AgentsMdSyncTests : IDisposable
{
    private readonly string _tempDir;

    public AgentsMdSyncTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tokopt-agentsmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void SyncFromClaudeMd_CopiesClaudeMdToAgentsMd_WhenAgentsMdMissing()
    {
        File.WriteAllText(Path.Combine(_tempDir, "CLAUDE.md"), "project instructions");

        AgentsMdSync.SyncFromClaudeMd(_tempDir);

        var agentsMdPath = Path.Combine(_tempDir, "AGENTS.md");
        Assert.True(File.Exists(agentsMdPath));
        Assert.Equal("project instructions", File.ReadAllText(agentsMdPath));
    }

    [Fact]
    public void SyncFromClaudeMd_NeverOverwritesExistingAgentsMd()
    {
        File.WriteAllText(Path.Combine(_tempDir, "CLAUDE.md"), "claude instructions");
        File.WriteAllText(Path.Combine(_tempDir, "AGENTS.md"), "project's own agents.md");

        AgentsMdSync.SyncFromClaudeMd(_tempDir);

        Assert.Equal("project's own agents.md", File.ReadAllText(Path.Combine(_tempDir, "AGENTS.md")));
    }

    [Fact]
    public void SyncFromClaudeMd_DoesNothing_WhenNoClaudeMdExists()
    {
        AgentsMdSync.SyncFromClaudeMd(_tempDir);
        Assert.False(File.Exists(Path.Combine(_tempDir, "AGENTS.md")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
