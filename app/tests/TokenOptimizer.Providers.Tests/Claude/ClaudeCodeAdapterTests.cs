using System.Runtime.Versioning;
using TokenOptimizer.Core.Diagnostics;
using TokenOptimizer.Providers.Claude;

namespace TokenOptimizer.Providers.Tests.Claude;

[SupportedOSPlatform("windows")]
public sealed class ClaudeCodeAdapterTests
{
    [Theory]
    [InlineData("anthropics/claude-plugins-official", "claude-plugins-official")]
    [InlineData("claude-plugins-official", "claude-plugins-official")]
    [InlineData("some-org/some-marketplace", "some-marketplace")]
    public void ExtractMarketplaceName_ReturnsSegmentAfterLastSlash(string locator, string expected)
    {
        Assert.Equal(expected, ClaudeCodeAdapter.ExtractMarketplaceName(locator));
    }

    [Fact]
    public async Task RefreshPluginMarketplacesAsync_NodeExeWrapper_SkipsUpdate()
    {
        var called = false;
        Func<string, string, string?, int, IReadOnlyDictionary<string, string>?, CancellationToken, Task<CommandResult>> runner =
            (_, _, _, _, _, _) => { called = true; return Task.FromResult(new CommandResult { Success = true }); };

        await ClaudeCodeAdapter.RefreshPluginMarketplacesAsync("C:\\path\\node.exe", runner);

        Assert.False(called);
    }

    [Fact]
    public async Task RefreshPluginMarketplacesAsync_RealExe_RunsMarketplaceUpdate()
    {
        string? capturedArgs = null;
        Func<string, string, string?, int, IReadOnlyDictionary<string, string>?, CancellationToken, Task<CommandResult>> runner =
            (_, args, _, _, _, _) => { capturedArgs = args; return Task.FromResult(new CommandResult { Success = true }); };

        await ClaudeCodeAdapter.RefreshPluginMarketplacesAsync("C:\\path\\claude.exe", runner);

        Assert.Equal("plugin marketplace update", capturedArgs);
    }
}
