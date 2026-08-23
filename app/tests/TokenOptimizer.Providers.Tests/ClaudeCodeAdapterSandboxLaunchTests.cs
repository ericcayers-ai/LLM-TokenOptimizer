using System.Runtime.CompilerServices;
using TokenOptimizer.Core.Config;
using TokenOptimizer.Core.Diagnostics;
using TokenOptimizer.Providers.Claude;
using TokenOptimizer.Providers.Fallback;
using TokenOptimizer.Sandbox;

namespace TokenOptimizer.Providers.Tests;

/// <summary>
/// Pins that LaunchSessionAsync forwards launchEnv.Env into the sandbox spec:
/// dropping it silently strands isolated sessions without their config and
/// bridged sessions without their proxy endpoint.
/// </summary>
public sealed class ClaudeCodeAdapterSandboxLaunchTests
{
    [Fact]
    public async Task LaunchSession_ForwardsLaunchEnvIntoSandboxSpec()
    {
        var runtime = new RecordingRuntime();
        var project = CreateTempDir();
        var adapter = new ClaudeCodeAdapter(await CreateNodeWrapperLocator(), new CommandAvailability(),
            new SandboxSessionLauncher(runtime, Settings()));

        await adapter.LaunchSessionAsync(new SessionLaunchOptions(project, IsolateConfig: true));

        var spec = runtime.LastSpec!;
        Assert.NotNull(spec.Env);
        Assert.Equal(CompanionToolingInstaller.IsolatedWorkerPort.ToString(), spec.Env["CLAUDE_MEM_WORKER_PORT"]);
        Assert.Equal(CompanionToolingInstaller.IsolatedDataDir, spec.Env["CLAUDE_MEM_DATA_DIR"]);
    }

    [Fact]
    public async Task LaunchSession_IsolateConfig_WindowsConfigDirKeyDoesNotReachContainer()
    {
        var runtime = new RecordingRuntime();
        var project = CreateTempDir();
        var adapter = new ClaudeCodeAdapter(await CreateNodeWrapperLocator(), new CommandAvailability(),
            new SandboxSessionLauncher(runtime, Settings()));

        await adapter.LaunchSessionAsync(new SessionLaunchOptions(project, IsolateConfig: true));

        // BuildLaunchEnvironment deliberately sets CLAUDE_CONFIG_DIR (a Windows
        // path); the sandbox seam strips it because it can never resolve in the
        // Linux container - isolation comes from having no config mount at all.
        Assert.False(runtime.LastSpec!.Env!.ContainsKey("CLAUDE_CONFIG_DIR"));
    }

    private static SandboxSettings Settings() => new()
    {
        AgentImage = "tokenoptimizer/agent-test:latest",
        IdleTimeoutMinutes = 5,
    };

    /// <summary>A claude.exe stand-in whose name ends with node.exe so the adapter's marketplace refresh (a real process spawn) is skipped.</summary>
    private static async Task<ClaudeExecutableLocator> CreateNodeWrapperLocator()
    {
        var dir = CreateTempDir();
        var exe = Path.Combine(dir, "claude-node.exe");
        await File.WriteAllTextAsync(exe, string.Empty);
        var store = new ConfigStore(dir);
        await store.UpdateAsync(c => c.ClaudePath = exe);
        return new ClaudeExecutableLocator(store, new CommandAvailability());
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "tokopt-adaptersbxcfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RecordingRuntime : ISandboxRuntime
    {
        public SandboxSpec? LastSpec { get; private set; }

        public Task<SandboxHandle> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            LastSpec = spec;
            return Task.FromResult(new SandboxHandle("sbx-000001"));
        }

        public async IAsyncEnumerable<ExecEvent> ExecAsync(
            string id, IReadOnlyList<string> argv, [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new ExecExit(0);
            await Task.CompletedTask;
        }

        public Task<string> ReadFileAsync(string id, string path, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task WriteFileAsync(string id, string path, string content, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task KillAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }
}
