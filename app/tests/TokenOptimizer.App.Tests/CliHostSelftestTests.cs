using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using TokenOptimizer.App.Cli;
using TokenOptimizer.Core.Config;
using TokenOptimizer.Core.Diagnostics;
using TokenOptimizer.Core.Models;
using TokenOptimizer.Core.Security;
using TokenOptimizer.Providers.Claude;
using TokenOptimizer.Providers.Diagnostics;

namespace TokenOptimizer.App.Tests;

[Collection("CliHost")]
[SupportedOSPlatform("windows")]
public sealed class CliHostSelftestTests : IDisposable
{
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();
    private readonly string _tempDir;

    public CliHostSelftestTests()
    {
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        Console.SetOut(Console.Out);
        Console.SetError(Console.Error);
        _stdout.Dispose();
        _stderr.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task TestModel_MissingProvider_ReturnsFailNamingArg()
    {
        var exit = await CliHost.RunAsync(["test-model", "--model", "claude-sonnet-5"]);
        Assert.Equal(1, exit);
        var json = ParseStdout();
        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.Contains("--provider", json["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestModel_MissingModel_ReturnsFailNamingArg()
    {
        var exit = await CliHost.RunAsync(["test-model", "--provider", "Claude Code"]);
        Assert.Equal(1, exit);
        var json = ParseStdout();
        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.Contains("--model", json["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestModel_UnknownProvider_ReturnsFail()
    {
        var probe = CreateProbeService(_ => new CommandResult { Success = false, Output = "nope" });
        var exit = await CliHost.RunAsync(["test-model", "--provider", "not-real", "--model", "x"], probe);
        Assert.Equal(1, exit);
        var json = ParseStdout();
        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.Contains("Unknown provider", json["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task TestModel_Success_ReturnsOkWithProbeResult()
    {
        var probe = CreateProbeService(_ => new CommandResult { Success = true, Output = "PONG" });
        var exit = await CliHost.RunAsync(["test-model", "--provider", "Claude Code", "--model", "claude-sonnet-5"], probe);
        Assert.Equal(0, exit);
        var json = ParseStdout();
        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.True(json["data"]!["result"]!["ok"]!.GetValue<bool>());
        Assert.Equal("PONG", json["data"]!["result"]!["responseText"]!.GetValue<string>());
    }

    [Fact]
    public async Task Selftest_FullMatrix_ReturnsReportShape()
    {
        var probe = CreateProbeService(_ => new CommandResult { Success = true, Output = "PONG" });
        var exit = await CliHost.RunAsync(["selftest"], probe);
        var json = ParseStdout();
        Assert.NotNull(json["data"]!["results"]);
        Assert.Equal(SelftestMatrix.Entries.Count, json["data"]!["results"]!.AsArray().Count);
        Assert.NotNull(json["data"]!["summary"]);
        Assert.Equal(SelftestMatrix.Entries.Count, json["data"]!["summary"]!["total"]!.GetValue<int>());
        Assert.True(json["data"]!["summary"]!["passed"]!.GetValue<int>() >= 0);
        Assert.True(json["data"]!["summary"]!["failed"]!.GetValue<int>() >= 0);
        Assert.True(json["data"]!["summary"]!["skipped"]!.GetValue<int>() >= 0);
    }

    [Fact]
    public async Task Selftest_OneFailure_ReturnsOkFalse()
    {
        var probe = CreateProbeService(inv =>
        {
            var isFirstClaude = inv.Arguments.Contains("claude-sonnet-5", StringComparison.Ordinal);
            return new CommandResult { Success = !isFirstClaude, Output = isFirstClaude ? "fail" : "PONG" };
        });
        var exit = await CliHost.RunAsync(["selftest"], probe);
        var json = ParseStdout();
        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.True(json["data"]!["summary"]!["failed"]!.GetValue<int>() > 0);
    }

    private ModelProbeService CreateProbeService(Func<CommandInvocation, CommandResult> handler)
    {
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        var fakeExe = Path.Combine(_tempDir, "claude.exe");
        File.WriteAllText(fakeExe, "");
        var store = new ConfigStore(configDir);
        store.SaveAsync(new AppConfig { ClaudePath = fakeExe }).GetAwaiter().GetResult();
        var locator = new ClaudeExecutableLocator(store, new CommandAvailability());
        var credentials = new ProxyCredentialStore(Path.Combine(_tempDir, "creds"));
        var invocations = new List<CommandInvocation>();
        return new ModelProbeService(locator, credentials, (file, args, wd, timeout, env, ct) =>
        {
            var inv = new CommandInvocation(file, args, wd, timeout, env);
            invocations.Add(inv);
            return Task.FromResult(handler(inv));
        });
    }

    private JsonNode ParseStdout()
    {
        var text = _stdout.ToString().Trim();
        Assert.False(string.IsNullOrWhiteSpace(text), "Expected JSON on stdout");
        return JsonNode.Parse(text)!;
    }

    private sealed record CommandInvocation(string File, string Arguments, string? WorkingDirectory, int Timeout, IReadOnlyDictionary<string, string>? Environment);
}
