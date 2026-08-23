using System.Runtime.CompilerServices;
using TokenOptimizer.Providers;
using TokenOptimizer.Providers.Fallback;
using TokenOptimizer.Sandbox;

namespace TokenOptimizer.Core.Tests.Providers;

public class SandboxSessionLauncherTests
{
    private static SandboxSettings Settings() => new()
    {
        AgentImage = "tokenoptimizer/agent-test:latest",
        IdleTimeoutMinutes = 42,
    };

    private static SessionLaunchOptions Options(bool isolateConfig = false) =>
        new(@"C:\code\demo", IsolateConfig: isolateConfig);

    [Fact]
    public async Task Launch_UsesAgentImage_IdleTimeout_AndWorkspaceMount()
    {
        var runtime = new RecordingRuntime();
        var launcher = new SandboxSessionLauncher(runtime, Settings());

        await launcher.LaunchAsync("Claude Code", "claude --continue", Options());

        var spec = runtime.SpecOf("sbx-000001");
        Assert.Equal("tokenoptimizer/agent-test:latest", spec.Image);
        Assert.Equal(TimeSpan.FromMinutes(42), spec.Timeout);
        var workspace = Assert.Single(spec.Mounts, m => m.Target == "/workspace");
        Assert.Equal(@"C:\code\demo", workspace.Source);
        Assert.False(workspace.ReadOnly);
    }

    [Fact]
    public async Task Launch_IsolateConfigFalse_MountsHostClaudeConfigReadOnly()
    {
        var runtime = new RecordingRuntime();
        var launcher = new SandboxSessionLauncher(runtime, Settings());

        await launcher.LaunchAsync("Claude Code", "claude", Options(isolateConfig: false));

        var spec = runtime.SpecOf("sbx-000001");
        var expectedHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        var credentialMount = Assert.Single(spec.Mounts, m => m.Target == "/root/.claude");
        Assert.Equal(expectedHome, credentialMount.Source);
        // Credential material must never be writable from inside the container.
        Assert.True(credentialMount.ReadOnly);
    }

    [Fact]
    public async Task Launch_IsolateConfigTrue_NoHostClaudeConfigMount()
    {
        var runtime = new RecordingRuntime();
        var launcher = new SandboxSessionLauncher(runtime, Settings());

        await launcher.LaunchAsync("Claude Code", "claude", Options(isolateConfig: true));

        var spec = runtime.SpecOf("sbx-000001");
        Assert.DoesNotContain(spec.Mounts, m => m.Target == "/root/.claude");
        Assert.Contains(spec.Mounts, m => m.Target == "/workspace");
    }

    [Fact]
    public async Task Launch_MergesProvidedEnvironmentIntoSpecEnv()
    {
        var runtime = new RecordingRuntime();
        var launcher = new SandboxSessionLauncher(runtime, Settings());

        await launcher.LaunchAsync("Groq", "claude --continue", Options(),
            new Dictionary<string, string>
            {
                ["ANTHROPIC_BASE_URL"] = "http://127.0.0.1:8080/",
                ["ANTHROPIC_AUTH_TOKEN"] = "proxied-locally",
            });

        var spec = runtime.SpecOf("sbx-000001");
        Assert.Equal("http://127.0.0.1:8080/", spec.Env!["ANTHROPIC_BASE_URL"]);
        Assert.Equal("proxied-locally", spec.Env["ANTHROPIC_AUTH_TOKEN"]);
    }

    [Fact]
    public async Task Launch_NoEnvironmentProvided_SpecEnvStaysEmpty()
    {
        var runtime = new RecordingRuntime();
        var launcher = new SandboxSessionLauncher(runtime, Settings());

        await launcher.LaunchAsync("Claude Code", "claude", Options());

        var spec = runtime.SpecOf("sbx-000001");
        Assert.True(spec.Env is null || spec.Env.Count == 0);
    }

    [Fact]
    public async Task Launch_ExecutesBashLoginShell_WithCommandVerbatim()
    {
        var runtime = new RecordingRuntime();
        var launcher = new SandboxSessionLauncher(runtime, Settings());

        const string command = "antigravity /workspace --continue";
        await launcher.LaunchAsync("Antigravity", command, Options());

        var call = Assert.Single(runtime.ExecCalls);
        Assert.Equal(new[] { "bash", "-lc", command }, call.Argv);
    }

    [Fact]
    public async Task Launch_ReturnsHandleWiredToRuntimeStream_BecomesNotRunningOnExit()
    {
        var runtime = new RecordingRuntime();
        runtime.QueueOutput("sbx-000001", new ExecOutput("stdout", "session-echo"));
        runtime.HoldAfterScript();

        var launcher = new SandboxSessionLauncher(runtime, Settings());
        var handle = Assert.IsType<SandboxSessionHandle>(await launcher.LaunchAsync("Claude Code", "claude", Options()));
        using var _ = handle;

        Assert.True(handle.IsRunning);

        runtime.Release();

        var outcome = await handle.RateLimitOutcome;

        Assert.False(outcome.RateLimitDetected);
        Assert.False(handle.IsRunning);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Launch_NullOrEmptyProjectPath_ThrowsInvalidOperation(string? projectPath)
    {
        var runtime = new RecordingRuntime();
        var launcher = new SandboxSessionLauncher(runtime, Settings());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            launcher.LaunchAsync("Claude Code", "claude", new SessionLaunchOptions(projectPath!)));
    }

    /// <summary>
    /// Test double: records Exec argv (FakeSandboxRuntime discards it) while
    /// delegating everything else - including spec storage and scripted
    /// output queues keyed by deterministic sbx-N ids - to the real fake.
    /// When armed via HoldAfterScript() before the launch, the exec stream is
    /// held open once the fake's script drains and only terminates (ExecExit)
    /// after Release() - so a test can observe IsRunning mid-session without
    /// racing the handle's background pump.
    /// </summary>
    private sealed class RecordingRuntime : ISandboxRuntime
    {
        private readonly FakeSandboxRuntime _inner = new();
        private TaskCompletionSource? _release;

        public List<(string Id, IReadOnlyList<string> Argv)> ExecCalls { get; } = new();

        public async Task<SandboxHandle> CreateAsync(SandboxSpec spec, CancellationToken ct = default) =>
            await _inner.CreateAsync(spec, ct);

        public async IAsyncEnumerable<ExecEvent> ExecAsync(
            string id, IReadOnlyList<string> argv, [EnumeratorCancellation] CancellationToken ct = default)
        {
            ExecCalls.Add((id, argv));
            await foreach (var e in _inner.ExecAsync(id, argv, ct))
                yield return e;

            if (_release is null)
                yield break;

            await _release.Task;
            yield return new ExecExit(0);
        }

        public void HoldAfterScript() =>
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release?.TrySetResult();

        public Task<string> ReadFileAsync(string id, string path, CancellationToken ct = default) =>
            _inner.ReadFileAsync(id, path, ct);

        public Task WriteFileAsync(string id, string path, string content, CancellationToken ct = default) =>
            _inner.WriteFileAsync(id, path, content, ct);

        public Task KillAsync(string id, CancellationToken ct = default) => _inner.KillAsync(id, ct);

        public SandboxSpec SpecOf(string id) => _inner.SpecOf(id);

        public void QueueOutput(string id, params ExecEvent[] events) => _inner.QueueOutput(id, events);
    }
}
