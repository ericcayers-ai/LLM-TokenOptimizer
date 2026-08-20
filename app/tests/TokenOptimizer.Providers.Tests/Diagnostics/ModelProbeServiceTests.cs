using System.Runtime.Versioning;
using TokenOptimizer.Core.Config;
using TokenOptimizer.Core.Diagnostics;
using TokenOptimizer.Core.Models;
using TokenOptimizer.Core.Security;
using TokenOptimizer.Providers.Claude;
using TokenOptimizer.Providers.Diagnostics;

namespace TokenOptimizer.Providers.Tests.Diagnostics;

[SupportedOSPlatform("windows")]
public sealed class ModelProbeServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ProxyCredentialStore _credentials;
    private readonly ClaudeExecutableLocator _locator;
    private readonly List<CommandInvocation> _invocations = new();

    public ModelProbeServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        _credentials = new ProxyCredentialStore(Path.Combine(_tempDir, "creds"));
        _locator = CreateLocator(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task ProbeAsync_UnknownProvider_ReturnsFail()
    {
        var service = new ModelProbeService(_locator, _credentials, FakeRunner());
        var result = await service.ProbeAsync("not-real", "model", null, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("Unknown provider", result.Error);
    }

    [Fact]
    public async Task ProbeAsync_ClaudeSuccess_ReturnsPongResponse()
    {
        var service = new ModelProbeService(_locator, _credentials, FakeRunner(_ => new CommandResult { Success = true, Output = "PONG" }));
        var result = await service.ProbeAsync("Claude Code", "claude-sonnet-5", null, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Equal("PONG", result.ResponseText);
        Assert.Equal("Claude Code", result.Provider);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ProbeAsync_TimedOut_ReturnsFailWithTimeoutMessage()
    {
        var service = new ModelProbeService(_locator, _credentials, FakeRunner(_ => new CommandResult { Success = false, TimedOut = true, Output = "too slow" }));
        var result = await service.ProbeAsync("Claude Code", "claude-sonnet-5", null, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("timed out", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeAsync_EmptyResponse_ReturnsFail()
    {
        var service = new ModelProbeService(_locator, _credentials, FakeRunner(_ => new CommandResult { Success = true, Output = "   " }));
        var result = await service.ProbeAsync("Claude Code", "claude-sonnet-5", null, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("empty", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeAsync_ErrorContainsAuthToken_RedactsValue()
    {
        var service = new ModelProbeService(_locator, _credentials, FakeRunner(_ => new CommandResult
        {
            Success = false,
            Output = "Error: ANTHROPIC_AUTH_TOKEN=super-secret-key and ANTHROPIC_API_KEY=another-secret",
        }));
        var result = await service.ProbeAsync("Claude Code", "claude-sonnet-5", null, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.DoesNotContain("super-secret-key", result.Error);
        Assert.DoesNotContain("another-secret", result.Error);
        Assert.Contains("ANTHROPIC_AUTH_TOKEN=***", result.Error);
        Assert.Contains("ANTHROPIC_API_KEY=***", result.Error);
    }

    [Fact]
    public async Task ProbeAsync_AntigravityMissingCli_Skipped()
    {
        var service = new ModelProbeService(_locator, _credentials, FakeRunner(_ => new CommandResult { Success = true, Output = "" }), () => null);
        var result = await service.ProbeAsync("Antigravity", "gemini-3-pro", null, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.True(result.Skipped);
        Assert.Contains("not found", result.SkipReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeAsync_AntigravityNoCredential_Skipped()
    {
        var service = new ModelProbeService(_locator, _credentials, FakeRunner(_ => new CommandResult { Success = true, Output = "" }), () => "agy.exe");
        var result = await service.ProbeAsync("Antigravity", "gemini-3-pro", null, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.True(result.Skipped);
        Assert.Contains("opted", result.SkipReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeAllAsync_RunsMatrixSequentially()
    {
        _credentials.SetCredential(FallbackProvider.Groq, "gsk_test_key");
        var callCount = 0;
        var service = new ModelProbeService(_locator, _credentials, (file, args, wd, timeout, env, ct) =>
        {
            callCount++;
            return Task.FromResult(new CommandResult { Success = true, Output = $"PONG {callCount}" });
        });
        var matrix = new[] { ("Claude Code", "m1"), ("Groq", "m2") };
        var results = await service.ProbeAllAsync(matrix, CancellationToken.None);
        Assert.Equal(2, results.Count);
        Assert.Equal("PONG 1", results[0].ResponseText);
        Assert.Equal("PONG 2", results[1].ResponseText);
    }

    [Fact]
    public void Redact_LeavesNonAuthTextUnchanged()
    {
        var text = "Some error without auth";
        Assert.Equal(text, ModelProbeService.Redact(text));
    }

    private Func<string, string, string?, int, IReadOnlyDictionary<string, string>?, CancellationToken, Task<CommandResult>> FakeRunner(params Func<CommandInvocation, CommandResult>[] handlers)
    {
        var queue = new Queue<Func<CommandInvocation, CommandResult>>(handlers);
        return (file, args, wd, timeout, env, ct) =>
        {
            var inv = new CommandInvocation(file, args, wd, timeout, env);
            _invocations.Add(inv);
            var result = queue.Count > 0 ? queue.Dequeue()(inv) : new CommandResult { Success = true, Output = "PONG" };
            return Task.FromResult(result);
        };
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
