using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using TokenOptimizer.Core.Config;
using TokenOptimizer.Core.Diagnostics;
using TokenOptimizer.Core.Models;
using TokenOptimizer.Core.Security;
using TokenOptimizer.Providers.Claude;
using TokenOptimizer.Providers.Diagnostics;

namespace TokenOptimizer.Providers.Tests.Diagnostics;

[SupportedOSPlatform("windows")]
public sealed class FeatureProbeServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _claudeHome;
    private readonly ProxyCredentialStore _credentials;
    private readonly ClaudeExecutableLocator _locator;
    private readonly List<CommandInvocation> _invocations = new();

    public FeatureProbeServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        _claudeHome = Path.Combine(_tempDir, "claude-home");
        Directory.CreateDirectory(Path.Combine(_claudeHome, "skills"));
        _credentials = new ProxyCredentialStore(Path.Combine(_tempDir, "creds"));
        _locator = CreateLocator(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task ProbeSessionContinuityAsync_Claude_RoundTripsCodephrase()
    {
        var service = new FeatureProbeService(
            _locator,
            _credentials,
            FakeRunner(
                _ => new CommandResult { Success = true, Output = "OK" },
                _ => new CommandResult { Success = true, Output = ParseCodephrase(_invocations[0].Arguments) }),
            getClaudeHome: () => _claudeHome);

        var result = await service.ProbeSessionContinuityAsync("Claude Code", "claude-sonnet-5", _tempDir, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("Claude Code", result.Provider);
        Assert.Null(result.Error);
        Assert.Equal(2, _invocations.Count);
        Assert.DoesNotContain("--continue", _invocations[0].Arguments);
        Assert.Contains("Remember the codephrase", _invocations[0].Arguments);
        Assert.Contains("--continue", _invocations[1].Arguments);
        Assert.Contains("What was the codephrase", _invocations[1].Arguments);
    }

    [Fact]
    public async Task ProbeSessionContinuityAsync_Groq_RoundTripsCodephrase()
    {
        _credentials.SetCredential(FallbackProvider.Groq, "gsk_test_key");
        var service = new FeatureProbeService(
            _locator,
            _credentials,
            FakeRunner(
                _ => new CommandResult { Success = true, Output = "OK" },
                _ => new CommandResult { Success = true, Output = ParseCodephrase(_invocations[0].Arguments) }),
            getClaudeHome: () => _claudeHome);

        var result = await service.ProbeSessionContinuityAsync("Groq", "openai/gpt-oss-120b", _tempDir, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("Groq", result.Provider);
        Assert.Null(result.Error);
        Assert.Equal(2, _invocations.Count);
        Assert.Contains("ANTHROPIC_BASE_URL", _invocations[0].Environment?.Keys ?? Array.Empty<string>());
        Assert.Equal("proxied-locally", _invocations[0].Environment!["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Contains("--continue", _invocations[1].Arguments);
    }

    [Fact]
    public async Task ProbeSessionContinuityAsync_UnknownProvider_ReturnsFail()
    {
        var service = new FeatureProbeService(_locator, _credentials, FakeRunner(), getClaudeHome: () => _claudeHome);

        var result = await service.ProbeSessionContinuityAsync("not-real", "model", _tempDir, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("Unknown provider", result.Error);
        Assert.Empty(_invocations);
    }

    [Fact]
    public async Task ProbeSharedSkillsPluginsAsync_AllProviders_Match()
    {
        Directory.CreateDirectory(Path.Combine(_claudeHome, "skills", "skill-a"));
        File.WriteAllText(Path.Combine(_claudeHome, "skills", "skill-a", "SKILL.md"), "---\nname: skill-a\n---\n");
        Directory.CreateDirectory(Path.Combine(_claudeHome, "skills", "skill-b"));
        File.WriteAllText(Path.Combine(_claudeHome, "skills", "skill-b", "SKILL.md"), "---\nname: skill-b\n---\n");

        _credentials.SetCredential(FallbackProvider.Groq, "gsk_test_key");
        _credentials.SetCredential(FallbackProvider.OpenCode, "oc_test_key");
        var bootOutput = "ANTHROPIC_BASE_URL=http://127.0.0.1:9999/v1\nANTHROPIC_AUTH_TOKEN=local-token\n";
        var pluginList = "plugin-one\nplugin-two\n";
            var service = new FeatureProbeService(
                _locator,
                _credentials,
                FakeRunner(
                    _ => new CommandResult { Success = true, Output = pluginList },
                    _ => new CommandResult { Success = true, Output = pluginList },
                    _ => new CommandResult { Success = true, Output = pluginList },
                    inv => inv.Arguments.StartsWith("start claude", StringComparison.Ordinal)
                        ? new CommandResult { Success = true, Output = bootOutput }
                        : new CommandResult { Success = true, Output = pluginList },
                    _ => new CommandResult { Success = true, Output = pluginList }),
                findUnsloth: () => "unsloth.exe",
                getClaudeHome: () => _claudeHome);


        var result = await service.ProbeSharedSkillsPluginsAsync(_tempDir, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Null(result.Error);
        Assert.Equal(4, result.PerProvider.Count);
        var first = result.PerProvider["Claude Code"];
        Assert.Equal(new[] { "plugin-one", "plugin-two" }, first.Plugins);
        Assert.Equal(new[] { "skill-a", "skill-b" }, first.SkillIds);
        Assert.All(result.PerProvider.Values, snapshot =>
        {
            Assert.Equal(first.Plugins, snapshot.Plugins);
            Assert.Equal(first.SkillIds, snapshot.SkillIds);
        });
    }

    [Fact]
    public async Task ProbeSharedSkillsPluginsAsync_PluginSetsDiffer_ReturnsFail()
    {
        Directory.CreateDirectory(Path.Combine(_claudeHome, "skills", "skill-a"));
        File.WriteAllText(Path.Combine(_claudeHome, "skills", "skill-a", "SKILL.md"), "---\nname: skill-a\n---\n");

        _credentials.SetCredential(FallbackProvider.Groq, "gsk_test_key");
        _credentials.SetCredential(FallbackProvider.OpenCode, "oc_test_key");
        var bootOutput = "ANTHROPIC_BASE_URL=http://127.0.0.1:9999/v1\nANTHROPIC_AUTH_TOKEN=local-token\n";
        var service = new FeatureProbeService(
            _locator,
            _credentials,
            FakeRunner(
                _ => new CommandResult { Success = true, Output = "plugin-one\n" },
                _ => new CommandResult { Success = true, Output = "plugin-one\nplugin-two\n" },
                _ => new CommandResult { Success = true, Output = "plugin-one\n" },
                inv => inv.Arguments.StartsWith("start claude", StringComparison.Ordinal)
                    ? new CommandResult { Success = true, Output = bootOutput }
                    : new CommandResult { Success = true, Output = "plugin-one\n" }),
            findUnsloth: () => "unsloth.exe",
            getClaudeHome: () => _claudeHome);

        var result = await service.ProbeSharedSkillsPluginsAsync(_tempDir, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("Plugin sets differ", result.Error);
    }

    [Fact]
    public async Task ProbeExportHandoffAsync_WithTranscriptAndSkills_WritesHandoffAndReferencesAgentsMd()
    {
        var projectDir = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(projectDir);
        var claudeConfigDir = Path.Combine(_tempDir, "claude-config");
        var slug = Regex.Replace(projectDir.TrimEnd('\\', '/'), @"[:\\/]", "-");
        var projectClaudeDir = Path.Combine(claudeConfigDir, "projects", slug);
        Directory.CreateDirectory(projectClaudeDir);
        File.WriteAllLines(Path.Combine(projectClaudeDir, "session.jsonl"), new[]
        {
            """{"type": "user", "message": {"content": "hello there"}}""",
            """{"type": "assistant", "message": {"content": [{"type": "text", "text": "hi back"}]}}""",
        });
        var projectSkillsDir = Path.Combine(projectDir, ".claude", "skills", "proj-skill");
        Directory.CreateDirectory(projectSkillsDir);
        File.WriteAllText(Path.Combine(projectSkillsDir, "SKILL.md"), "---\nname: proj-skill\n---\nbody\n");

        var service = new FeatureProbeService(_locator, _credentials, FakeRunner(), getClaudeHome: () => _claudeHome);

        var result = await service.ProbeExportHandoffAsync(projectDir, claudeConfigDir, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.True(File.Exists(result.HandoffFile));
        var content = File.ReadAllText(result.HandoffFile);
        Assert.Contains("hello there", content);
        Assert.Contains("hi back", content);
        Assert.Contains("proj-skill", content);
        var agentsMd = Path.Combine(projectDir, "AGENTS.md");
        Assert.True(File.Exists(agentsMd));
        Assert.Contains(".claude-handoff/session-handoff.md", File.ReadAllText(agentsMd));
    }

    private Func<string, string, string?, int, IReadOnlyDictionary<string, string>?, CancellationToken, Task<CommandResult>> FakeRunner(params Func<CommandInvocation, CommandResult>[] handlers)
    {
        var queue = new Queue<Func<CommandInvocation, CommandResult>>(handlers);
        return (file, args, wd, timeout, env, ct) =>
        {
            var inv = new CommandInvocation(file, args, wd, timeout, env);
            _invocations.Add(inv);
            var result = queue.Count > 0 ? queue.Dequeue()(inv) : new CommandResult { Success = true, Output = "" };
            return Task.FromResult(result);
        };
    }

    private static string ParseCodephrase(string arguments)
    {
        var match = Regex.Match(arguments, @"Remember the codephrase ([0-9a-fA-F]+)\. Reply OK");
        return match.Success ? match.Groups[1].Value : "missing";
    }

    private static ClaudeExecutableLocator CreateLocator(string tempDir)
    {
        var configDir = Path.Combine(tempDir, "config");
        Directory.CreateDirectory(configDir);
        var fakeExe = Path.Combine(tempDir, "claude.exe");
        File.WriteAllText(fakeExe, "");
        var store = new ConfigStore(configDir);
        store.SaveAsync(new AppConfig { ClaudePath = fakeExe }).GetAwaiter().GetResult();
        return new ClaudeExecutableLocator(store, new CommandAvailability());
    }

    private sealed record CommandInvocation(string File, string Arguments, string? WorkingDirectory, int Timeout, IReadOnlyDictionary<string, string>? Environment);
}
