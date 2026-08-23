using TokenOptimizer.Providers;

namespace TokenOptimizer.Core.Tests.Providers;

public class ToolCatalogTests
{
    private static readonly string[] ExpectedIds =
        ["rtk", "graphify", "headroom", "caveman", "claude-mem", "context7", "graft"];

    [Fact]
    public void Tools_ContainsExactlyTheSevenCompanionToolIds()
    {
        Assert.Equal(ExpectedIds.Length, ToolCatalog.Tools.Count);
        Assert.Equal(new HashSet<string>(ExpectedIds), new HashSet<string>(ToolCatalog.Tools.Select(t => t.Id)));
    }

    [Fact]
    public void EveryTool_HasAllFieldsNonEmpty()
    {
        Assert.All(ToolCatalog.Tools, tool =>
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Id), $"{nameof(tool.Id)} is empty");
            Assert.False(string.IsNullOrWhiteSpace(tool.HostInstallCommand), $"{tool.Id}: {nameof(tool.HostInstallCommand)} is empty");
            Assert.False(string.IsNullOrWhiteSpace(tool.ImageInstallFragment), $"{tool.Id}: {nameof(tool.ImageInstallFragment)} is empty");
            Assert.False(string.IsNullOrWhiteSpace(tool.ClaudeWiringFragment), $"{tool.Id}: {nameof(tool.ClaudeWiringFragment)} is empty");
        });
    }

    [Fact]
    public void Graft_ImageInstallFragment_ReferencesNanoNetsGraft()
    {
        var graft = Assert.Single(ToolCatalog.Tools, t => t.Id == "graft");
        Assert.Contains("NanoNets/Graft", graft.ImageInstallFragment);
    }
}
