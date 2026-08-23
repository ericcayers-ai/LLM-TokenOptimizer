using TokenOptimizer.Providers.Fallback;

namespace TokenOptimizer.Core.Tests.Providers;

/// <summary>
/// Host-side proxies bind 127.0.0.1:&lt;port&gt;, but env values forwarded into a
/// sandbox resolve that loopback INSIDE the container (where nothing listens).
/// SandboxEnvTranslator is the one seam every launch flows through; these pin
/// its rewrite rules before adapters ever see the container-shaped values.
/// </summary>
public class SandboxEnvTranslatorTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8399/v1", "http://host.docker.internal:8399/v1")]
    [InlineData("http://localhost:8080/", "http://host.docker.internal:8080/")]
    [InlineData("http://127.0.0.1/p?a=b&c=d", "http://host.docker.internal/p?a=b&c=d")]
    public void Translate_LoopbackUrl_RewritesHostPreservingSchemePortPathAndQuery(string value, string expected)
    {
        var translated = SandboxEnvTranslator.Translate(
            new Dictionary<string, string> { ["ANTHROPIC_BASE_URL"] = value });

        Assert.Equal(expected, translated!["ANTHROPIC_BASE_URL"]);
    }

    [Fact]
    public void Translate_NonUrlValues_PassThroughUntouched()
    {
        var translated = SandboxEnvTranslator.Translate(new Dictionary<string, string>
        {
            ["ANTHROPIC_AUTH_TOKEN"] = "proxied-locally",
            ["CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY"] = "1",
            ["CLAUDE_MEM_DATA_DIR"] = @"C:\Users\x\.claude-mem-tokenoptimizer",
        });

        Assert.Equal("proxied-locally", translated!["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Equal("1", translated["CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY"]);
        Assert.Equal(@"C:\Users\x\.claude-mem-tokenoptimizer", translated["CLAUDE_MEM_DATA_DIR"]);
    }

    [Fact]
    public void Translate_PortOnlyValue_LeftAsIs()
    {
        var translated = SandboxEnvTranslator.Translate(
            new Dictionary<string, string> { ["CLAUDE_MEM_WORKER_PORT"] = "37778" });

        Assert.Equal("37778", translated!["CLAUDE_MEM_WORKER_PORT"]);
    }

    [Fact]
    public void Translate_ConfigDirKey_IsDropped()
    {
        var translated = SandboxEnvTranslator.Translate(new Dictionary<string, string>
        {
            ["CLAUDE_CONFIG_DIR"] = @"C:\proj\.claude-profiles\abc",
            ["ANTHROPIC_AUTH_TOKEN"] = "keep-me",
        });

        Assert.False(translated!.ContainsKey("CLAUDE_CONFIG_DIR"));
        Assert.Equal("keep-me", translated["ANTHROPIC_AUTH_TOKEN"]);
    }

    [Fact]
    public void Translate_NullOrEmpty_ReturnsNullishInput()
    {
        Assert.Null(SandboxEnvTranslator.Translate(null));
        Assert.True(SandboxEnvTranslator.Translate(new Dictionary<string, string>()) is null or { Count: 0 });
    }
}
