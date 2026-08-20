using System.Runtime.Versioning;
using TokenOptimizer.Core.Security;
using TokenOptimizer.Providers.Fallback;

namespace TokenOptimizer.Providers.Tests.Fallback;

[SupportedOSPlatform("windows")]
public sealed class CodexAdapterTests
{
    [Fact]
    public void BuildArguments_WithModel_ReturnsModelFlag()
    {
        Assert.Equal("-m gpt-5.1-codex", CodexAdapter.BuildArguments("gpt-5.1-codex"));
    }

    [Fact]
    public void BuildArguments_WithNullOrEmptyModel_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, CodexAdapter.BuildArguments(null));
        Assert.Equal(string.Empty, CodexAdapter.BuildArguments(""));
        Assert.Equal(string.Empty, CodexAdapter.BuildArguments("   "));
    }
}
