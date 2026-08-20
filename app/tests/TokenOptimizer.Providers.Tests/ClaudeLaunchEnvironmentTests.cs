using TokenOptimizer.Core.Diagnostics;
using TokenOptimizer.Core.Models;
using TokenOptimizer.Core.Security;
using TokenOptimizer.Providers.Claude;
using TokenOptimizer.Providers.Fallback;

namespace TokenOptimizer.Providers.Tests;

public sealed class ClaudeLaunchEnvironmentTests
{
    private const string ProjectPath = "C:\\tmp\\project";

    [Fact]
    public void Builder_PreservesInsertionOrder()
    {
        var env = new ClaudeLaunchEnvironmentBuilder()
            .WithModel("claude-sonnet-5")
            .WithResumeMode(SessionResumeMode.Continue)
            .WithClaudeMemIsolation()
            .Build();

        Assert.Equal("--model claude-sonnet-5 --continue", env.Arguments);
    }

    [Fact]
    public void Builder_WithIsolatedConfig_SetsClaudeConfigDir()
    {
        var env = new ClaudeLaunchEnvironmentBuilder()
            .WithClaudeMemIsolation()
            .WithIsolatedConfig(ProjectPath)
            .Build();

        Assert.True(env.Env.ContainsKey("CLAUDE_CONFIG_DIR"));
        Assert.Contains("claude-profiles", env.Env["CLAUDE_CONFIG_DIR"]!);
    }

    [Fact]
    public void ClaudeCodeAdapter_BuildLaunchEnvironment_MatchesExpectedEnvAndArgs()
    {
        var adapter = new ClaudeCodeAdapter(CreateLocator(), new CommandAvailability());
        var options = new SessionLaunchOptions(ProjectPath, "claude-sonnet-5", IsolateConfig: false, SessionResumeMode.Continue);

        var env = adapter.BuildLaunchEnvironment(options);

        Assert.Equal("--model claude-sonnet-5 --continue", env.Arguments);
        Assert.Equal(CompanionToolingInstaller.IsolatedWorkerPort.ToString(), env.Env["CLAUDE_MEM_WORKER_PORT"]);
        Assert.Equal(CompanionToolingInstaller.IsolatedDataDir, env.Env["CLAUDE_MEM_DATA_DIR"]);
        Assert.False(env.Env.ContainsKey("CLAUDE_CONFIG_DIR"));
        Assert.False(env.Env.ContainsKey("ANTHROPIC_BASE_URL"));
        Assert.False(env.Env.ContainsKey("ANTHROPIC_AUTH_TOKEN"));
    }

    [Fact]
    public void ClaudeCodeAdapter_IsolatedConfig_SetsClaudeConfigDir()
    {
        var adapter = new ClaudeCodeAdapter(CreateLocator(), new CommandAvailability());
        var options = new SessionLaunchOptions(ProjectPath, null, IsolateConfig: true, SessionResumeMode.Pick);

        var env = adapter.BuildLaunchEnvironment(options);

        Assert.Equal("--resume", env.Arguments);
        Assert.True(env.Env.ContainsKey("CLAUDE_CONFIG_DIR"));
    }

    [Fact]
    public void GroqAdapter_BuildLaunchEnvironment_MatchesExpectedEnvAndArgs()
    {
        var adapter = new GroqAdapter(new ProxyCredentialStore(CreateTempDir()), CreateLocator());
        var options = new SessionLaunchOptions(ProjectPath, "openai/gpt-oss-120b", IsolateConfig: false, SessionResumeMode.Continue);

        var env = adapter.BuildLaunchEnvironment(options, "http://127.0.0.1:12345/");

        Assert.Equal("--continue --model openai/gpt-oss-120b", env.Arguments);
        Assert.Equal("http://127.0.0.1:12345/", env.Env["ANTHROPIC_BASE_URL"]);
        Assert.Equal("proxied-locally", env.Env["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Equal(CompanionToolingInstaller.IsolatedWorkerPort.ToString(), env.Env["CLAUDE_MEM_WORKER_PORT"]);
        Assert.Equal(CompanionToolingInstaller.IsolatedDataDir, env.Env["CLAUDE_MEM_DATA_DIR"]);
    }

    [Fact]
    public void OpenCodeAdapter_BuildLaunchEnvironment_MatchesExpectedEnvAndArgs()
    {
        var adapter = new OpenCodeAdapter(new ProxyCredentialStore(CreateTempDir()), CreateLocator());
        var options = new SessionLaunchOptions(ProjectPath, null, IsolateConfig: false, SessionResumeMode.Continue);

        var env = adapter.BuildLaunchEnvironment(options, "oc_test_key");

        Assert.Equal($"--continue --model {OpenCodeModelCatalog.DefaultModel}", env.Arguments);
        Assert.Equal("https://opencode.ai/zen/go", env.Env["ANTHROPIC_BASE_URL"]);
        Assert.Equal("oc_test_key", env.Env["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Equal(CompanionToolingInstaller.IsolatedWorkerPort.ToString(), env.Env["CLAUDE_MEM_WORKER_PORT"]);
        Assert.Equal(CompanionToolingInstaller.IsolatedDataDir, env.Env["CLAUDE_MEM_DATA_DIR"]);
    }

    [Fact]
    public void OpenCodeAdapter_WithCustomModel_UsesCustomModel()
    {
        var adapter = new OpenCodeAdapter(new ProxyCredentialStore(CreateTempDir()), CreateLocator());
        var options = new SessionLaunchOptions(ProjectPath, "custom-model", IsolateConfig: false, SessionResumeMode.New);

        var env = adapter.BuildLaunchEnvironment(options, "key");

        Assert.Equal("--model custom-model", env.Arguments);
    }

    private static ClaudeExecutableLocator CreateLocator()
    {
        var configDir = CreateTempDir();
        return new ClaudeExecutableLocator(new TokenOptimizer.Core.Config.ConfigStore(configDir), new CommandAvailability());
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(path);
        return path;
    }
}
