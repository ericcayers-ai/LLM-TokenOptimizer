using TokenOptimizer.Core.Config;
using TokenOptimizer.Sandbox;

namespace TokenOptimizer.Core.Tests.Config;

public class SandboxSettingsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ConfigStore _store;

    public SandboxSettingsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tokopt-tests-" + Guid.NewGuid().ToString("N"));
        _store = new ConfigStore(_tempDir);
    }

    [Fact]
    public void Defaults_MatchSpecifiedValues()
    {
        var s = new SandboxSettings();

        Assert.Equal("localhost:8080", s.Domain);
        Assert.Equal("http", s.Protocol);
        Assert.Null(s.ApiKeySecretRef);
        Assert.Equal("tokenoptimizer/agent-companion:latest", s.AgentImage);
        Assert.Equal(60, s.IdleTimeoutMinutes);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsSandboxSection()
    {
        var config = new Models.AppConfig
        {
            Sandbox = new SandboxSettings
            {
                Domain = "sandbox.local:9000",
                Protocol = "https",
                ApiKeySecretRef = "proxy:opensandbox",
                AgentImage = "tokenoptimizer/agent-companion:v9",
                IdleTimeoutMinutes = 15,
            },
        };

        await _store.SaveAsync(config);
        var reloaded = await _store.LoadAsync();

        Assert.Equal("sandbox.local:9000", reloaded.Sandbox.Domain);
        Assert.Equal("https", reloaded.Sandbox.Protocol);
        Assert.Equal("proxy:opensandbox", reloaded.Sandbox.ApiKeySecretRef);
        Assert.Equal("tokenoptimizer/agent-companion:v9", reloaded.Sandbox.AgentImage);
        Assert.Equal(15, reloaded.Sandbox.IdleTimeoutMinutes);
    }

    [Fact]
    public async Task LoadAsync_OldConfigWithoutSandboxSection_GetsDefaults()
    {
        await _store.SaveAsync(new Models.AppConfig { ClaudePath = @"C:\claude\claude.exe" });
        // Simulate a pre-Sandbox config file by stripping the section from disk.
        var path = _store.ConfigPath;
        var json = File.ReadAllText(path);
        File.WriteAllText(path, json.Replace("\"Sandbox\"", "\"__removed\""));

        var reloaded = await _store.LoadAsync();

        Assert.NotNull(reloaded.Sandbox);
        Assert.Equal("localhost:8080", reloaded.Sandbox.Domain);
        Assert.Equal(@"C:\claude\claude.exe", reloaded.ClaudePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }
}
