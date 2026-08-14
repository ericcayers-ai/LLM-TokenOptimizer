using TokenOptimizer.Providers.Claude;

namespace TokenOptimizer.Providers.Tests.Claude;

public class ClaudeCodeAdapterTests
{
    [Theory]
    [InlineData("anthropics/claude-plugins-official", "claude-plugins-official")]
    [InlineData("claude-plugins-official", "claude-plugins-official")]
    [InlineData("some-org/some-marketplace", "some-marketplace")]
    public void ExtractMarketplaceName_ReturnsSegmentAfterLastSlash(string locator, string expected)
    {
        Assert.Equal(expected, ClaudeCodeAdapter.ExtractMarketplaceName(locator));
    }
}
