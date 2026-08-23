using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using TokenOptimizer.App.Cli;
using TokenOptimizer.Sandbox;

namespace TokenOptimizer.App.Tests;

[Collection("CliHost")]
[SupportedOSPlatform("windows")]
public sealed class CliHostArgParsingTests : IDisposable
{
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();

    public CliHostArgParsingTests()
    {
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
    }

    public void Dispose()
    {
        Console.SetOut(Console.Out);
        Console.SetError(Console.Error);
        _stdout.Dispose();
        _stderr.Dispose();
    }

    [Fact]
    public async Task NoCommand_ReturnsFail()
    {
        var exit = await CliHost.RunAsync([]);
        Assert.Equal(1, exit);
        var json = ParseStdout();
        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.Contains("No command given", json["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnknownCommand_ReturnsFail()
    {
        var exit = await CliHost.RunAsync(["not-a-command"]);
        Assert.Equal(1, exit);
        var json = ParseStdout();
        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.Contains("not-a-command", json["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("launch", "--project")]
    [InlineData("uninstall", "--confirm")]
    [InlineData("master-folder-set", "--path")]
    [InlineData("create-project", "--path")]
    [InlineData("add-project", "--path")]
    [InlineData("set-credential", "--provider")]
    [InlineData("opt-in", "--provider")]
    [InlineData("export-handoff", "--project")]
    [InlineData("image-export", "--out")]
    public async Task MissingRequiredArg_NamesTheArg(string command, string argName)
    {
        var exit = await CliHost.RunAsync([command]);
        Assert.Equal(1, exit);
        var json = ParseStdout();
        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.Contains(argName, json["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResetConfig_ValidInvoke_ReturnsOkShape()
    {
        var exit = await CliHost.RunAsync(["reset-config"]);
        var json = ParseStdout();
        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.True(json["data"]!["reset"]!.GetValue<bool>());
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task History_ValidInvoke_ReturnsOkShape()
    {
        var exit = await CliHost.RunAsync(["history"]);
        var json = ParseStdout();
        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.NotNull(json["data"]!["history"]);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task SetCredential_ValidInvoke_ReturnsOkShape()
    {
        var exit = await CliHost.RunAsync(["set-credential", "--provider", "groq", "--key", "test-key"]);
        var json = ParseStdout();
        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.Equal("Groq", json["data"]!["stored"]!.GetValue<string>());
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task OptIn_ValidInvoke_ReturnsOkShape()
    {
        var exit = await CliHost.RunAsync(["opt-in", "--provider", "antigravity"]);
        var json = ParseStdout();
        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.Equal("Antigravity", json["data"]!["optedIn"]!.GetValue<string>());
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Providers_ValidInvoke_ReturnsOkShape()
    {
        var exit = await CliHost.RunAsync(["providers"]);
        var json = ParseStdout();
        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.NotNull(json["data"]!["providers"]);
        Assert.NotNull(json["data"]!["auto"]);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Status_ValidInvoke_ReturnsOkShape()
    {
        var exit = await CliHost.RunAsync(["status"]);
        var json = ParseStdout();
        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.NotNull(json["data"]!["dependencies"]);
        Assert.NotNull(json["data"]!["fallbackChain"]);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task MasterFolderSet_InvalidPath_ReturnsFail()
    {
        var exit = await CliHost.RunAsync(["master-folder-set", "--path", "not-a-real-folder"]);
        Assert.Equal(1, exit);
        var json = ParseStdout();
        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.Contains("Invalid master folder", json["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateProject_MissingName_ReturnsFailNamingName()
    {
        var exit = await CliHost.RunAsync(["create-project", "--path", Environment.CurrentDirectory]);
        Assert.Equal(1, exit);
        var json = ParseStdout();
        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.Contains("--name", json["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetCredential_MissingKey_ReturnsFailNamingKey()
    {
        var exit = await CliHost.RunAsync(["set-credential", "--provider", "groq"]);
        Assert.Equal(1, exit);
        var json = ParseStdout();
        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.Contains("--key", json["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Uninstall_WrongConfirm_ReturnsFail()
    {
        var exit = await CliHost.RunAsync(["uninstall", "--confirm", "no"]);
        Assert.Equal(1, exit);
        var json = ParseStdout();
        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.Contains("UNINSTALL", json["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task SandboxStatus_ValidInvoke_PrintsAllSixKeysInStableOrder()
    {
        var settings = new SandboxSettings();
        var manager = new StubServerManager(DockerUpRunner(), settings) { Healthy = false };

        var exit = await CliHost.RunAsync(["sandbox-status"], sandboxManager: manager);

        Assert.Equal(0, exit);
        var json = ParseStdout();
        Assert.True(json["dockerUp"]!.GetValue<bool>());
        Assert.False(json["serverUp"]!.GetValue<bool>());
        Assert.Equal(settings.Domain, json["domain"]!.GetValue<string>());
        Assert.Equal(settings.AgentImage, json["agentImage"]!.GetValue<string>());
        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.Contains("opensandbox-server", json["missing"]!.AsArray().Select(n => n!.GetValue<string>()));

        var raw = _stdout.ToString().Trim();
        var indexes = new[] { "dockerUp", "serverUp", "domain", "agentImage", "ok", "missing" }
            .Select(key => raw.IndexOf($"\"{key}\"", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, indexes);
        Assert.True(indexes.Zip(indexes.Skip(1)).All(pair => pair.First < pair.Second),
            $"Expected stable field order dockerUp,serverUp,domain,agentImage,ok,missing - got: {raw}");
    }

    [Fact]
    public async Task SandboxStatus_AllUp_ReportsReadyWithNoMissing()
    {
        var manager = new StubServerManager(DockerUpRunner(), new SandboxSettings()) { Healthy = true };

        var exit = await CliHost.RunAsync(["sandbox-status"], sandboxManager: manager);

        Assert.Equal(0, exit);
        var json = ParseStdout();
        Assert.True(json["dockerUp"]!.GetValue<bool>());
        Assert.True(json["serverUp"]!.GetValue<bool>());
        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.Empty(json["missing"]!.AsArray());
    }

    private static FakeRunner DockerUpRunner() => new() { ["docker"] = new ProcResult(0, "OK", "") };

    [Fact]
    public async Task ImageExport_ValidInvoke_WritesCompanionFilesAndReturnsOkShape()
    {
        var outDir = Path.Combine(Path.GetTempPath(), "tokenoptimizer-e2e-" + Guid.NewGuid().ToString("N"));
        try
        {
            var exit = await CliHost.RunAsync(["image-export", "--out", outDir]);

            Assert.Equal(0, exit);
            var json = ParseStdout();
            Assert.True(json["ok"]!.GetValue<bool>());
            Assert.Equal(Path.GetFullPath(outDir), json["data"]!["dir"]!.GetValue<string>());

            var dockerfile = File.ReadAllText(Path.Combine(outDir, "Dockerfile"));
            Assert.Contains("FROM opensandbox/code-interpreter:v1.1.0", dockerfile, StringComparison.Ordinal);
            Assert.Contains("/opt/tokenoptimizer/WIRING.txt", dockerfile, StringComparison.Ordinal);
            Assert.Contains("ENTRYPOINT", dockerfile, StringComparison.Ordinal);

            var entrypoint = File.ReadAllText(Path.Combine(outDir, "entrypoint.sh"));
            Assert.StartsWith("#!/usr/bin/env bash", entrypoint, StringComparison.Ordinal);
            Assert.Contains("graft init", entrypoint, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task SmokeRun_ClaudeVersionOutput_PassesAndKillsSandbox()
    {
        var runtime = new StubSandboxRuntime { StdoutText = "claude 1.2.3 (Claude Code)", ExitCode = 0 };

        var exit = await CliHost.RunAsync(["smoke-run"], sandboxRuntime: runtime);

        Assert.Equal(0, exit);
        var json = ParseStdout();
        Assert.True(json["pass"]!.GetValue<bool>());
        Assert.Contains("sb-stub-1", json["detail"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Single(runtime.CreatedImages);
        Assert.Equal("tokenoptimizer/agent-companion:latest", runtime.CreatedImages[0]);
        Assert.Equal(["sb-stub-1"], runtime.KilledIds);
    }

    [Fact]
    public async Task SmokeRun_ImageOption_OverridesDefaultImage()
    {
        var runtime = new StubSandboxRuntime { StdoutText = "claude 1.2.3", ExitCode = 0 };

        var exit = await CliHost.RunAsync(["smoke-run", "--image", "custom/image:e2e"], sandboxRuntime: runtime);

        Assert.Equal(0, exit);
        Assert.Equal("custom/image:e2e", runtime.CreatedImages[0]);
    }

    [Fact]
    public async Task SmokeRun_VersionLikeOnlyOutput_Passes()
    {
        var runtime = new StubSandboxRuntime { StdoutText = "2.1.0\n", ExitCode = 0 };

        var exit = await CliHost.RunAsync(["smoke-run"], sandboxRuntime: runtime);

        Assert.Equal(0, exit);
        Assert.True(ParseStdout()["pass"]!.GetValue<bool>());
    }

    [Fact]
    public async Task SmokeRun_NoClaudeOrVersionOutput_FailsAndStillKills()
    {
        var runtime = new StubSandboxRuntime { StdoutText = "hello world", ExitCode = 127 };

        var exit = await CliHost.RunAsync(["smoke-run"], sandboxRuntime: runtime);

        Assert.Equal(1, exit);
        var json = ParseStdout();
        Assert.False(json["pass"]!.GetValue<bool>());
        Assert.NotEmpty(json["detail"]!.GetValue<string>());
        Assert.Single(runtime.KilledIds);
    }

    [Fact]
    public async Task SmokeRun_CreateFailure_ReportsPassFalseWithoutKill()
    {
        var runtime = new StubSandboxRuntime { CreateError = new InvalidOperationException("registry unreachable") };

        var exit = await CliHost.RunAsync(["smoke-run"], sandboxRuntime: runtime);

        Assert.Equal(1, exit);
        var json = ParseStdout();
        Assert.False(json["pass"]!.GetValue<bool>());
        Assert.Contains("registry unreachable", json["detail"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runtime.KilledIds);
    }

    /// <summary>Same seam pattern as SetupWizardViewModelTests.FlippingManager: real GetStatusAsync flow, health probe stubbed so no network/docker is touched.</summary>
    private sealed class StubServerManager(IProcessRunner runner, SandboxSettings settings)
        : ServerLifecycleManager(runner, settings)
    {
        public bool Healthy { get; init; }

        protected override Task<bool> ProbeHealthAsync(Uri healthUri, CancellationToken ct) => Task.FromResult(Healthy);
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public Dictionary<string, ProcResult> Results { get; } = new();
        public ProcResult this[string exe] { get => Results[exe]; init => Results[exe] = value; }

        public Task<ProcResult> RunAsync(string exe, IReadOnlyList<string> args,
            IDictionary<string, string>? env = null, CancellationToken ct = default)
        {
            var key = Path.GetFileNameWithoutExtension(exe);
            return Task.FromResult(Results.TryGetValue(key, out var r) ? r : new ProcResult(1, "", "not stubbed"));
        }
    }

    /// <summary>In-memory ISandboxRuntime: records create/kill calls, replays a canned claude --version exec stream. No network.</summary>
    private sealed class StubSandboxRuntime : ISandboxRuntime
    {
        public List<string> CreatedImages { get; } = [];
        public List<string> KilledIds { get; } = [];
        public string StdoutText { get; init; } = "";
        public int ExitCode { get; init; }
        public Exception? CreateError { get; init; }

        public Task<SandboxHandle> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            if (CreateError is not null) throw CreateError;
            CreatedImages.Add(spec.Image);
            return Task.FromResult(new SandboxHandle("sb-stub-1"));
        }

        public async IAsyncEnumerable<ExecEvent> ExecAsync(string id, IReadOnlyList<string> argv,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Assert.Equal(["claude", "--version"], argv.ToArray());
            yield return new ExecOutput("stdout", StdoutText);
            await Task.Yield();
            yield return new ExecExit(ExitCode);
        }

        public Task<string> ReadFileAsync(string id, string path, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task WriteFileAsync(string id, string path, string content, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task KillAsync(string id, CancellationToken ct = default)
        {
            KilledIds.Add(id);
            return Task.CompletedTask;
        }
    }

    private JsonNode ParseStdout()
    {
        var text = _stdout.ToString().Trim();
        Assert.False(string.IsNullOrWhiteSpace(text), "Expected JSON on stdout");
        return JsonNode.Parse(text)!;
    }
}
