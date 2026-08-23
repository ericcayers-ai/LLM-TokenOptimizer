using TokenOptimizer.Sandbox;

namespace TokenOptimizer.Core.Tests.Sandbox;

public class ServerLifecycleManagerTests
{
    [Fact]
    public async Task GetStatus_DockerDown_ReturnsNotUpWithError()
    {
        var runner = new FakeRunner { ["docker"] = new ProcResult(1, "", "Cannot connect to the Docker daemon") };
        var mgr = new TestableManager(runner, Settings(), probeUp: false);

        var status = await mgr.GetStatusAsync();

        Assert.False(status.DockerUp);
        Assert.False(status.ServerUp);
        Assert.False(string.IsNullOrWhiteSpace(status.Error));
    }

    [Fact]
    public async Task GetStatus_HealthyServer_ReturnsUpWithDomain()
    {
        var runner = new FakeRunner { ["docker"] = new ProcResult(0, "OK", "") };
        var mgr = new TestableManager(runner, Settings(), probeUp: true);

        var status = await mgr.GetStatusAsync();

        Assert.True(status.DockerUp);
        Assert.True(status.ServerUp);
        Assert.Equal("localhost", status.Domain!.Host);
        Assert.Equal(8080, status.Domain.Port);
        Assert.Null(status.Error);
    }

    [Fact]
    public async Task EnsureRunning_ServerAlreadyHealthy_DoesNothingElse()
    {
        var runner = new FakeRunner { ["docker"] = new ProcResult(0, "OK", "") };
        var mgr = new TestableManager(runner, Settings(), probeUp: true);

        var status = await mgr.EnsureRunningAsync(CancellationToken.None);

        Assert.True(status.ServerUp);
        Assert.False(mgr.StartServerCalled);
        Assert.DoesNotContain(runner.Calls, c => c.Args.Contains("init-config"));
    }

    [Fact]
    public async Task EnsureRunning_ColdPath_WritesConfigStartsServerAndWaitsForHealth()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "tokopt-sbxcfg-" + Guid.NewGuid().ToString("N") + ".toml");
        try
        {
            var runner = new FakeRunner { ["docker"] = new ProcResult(0, "OK", "") };
            var settings = Settings();
            var mgr = new TestableManager(runner, settings, probeUp: false)
            {
                ConfigPathOverride = configPath,
                ProbeUpAfterStart = true,
            };

            var status = await mgr.EnsureRunningAsync(CancellationToken.None);

            Assert.True(mgr.StartServerCalled);
            Assert.Contains(runner.Calls, c => c.Args.Contains("init-config"));
            Assert.True(File.Exists(configPath));
            Assert.True(status.ServerUp);
        }
        finally
        {
            if (File.Exists(configPath)) File.Delete(configPath);
        }
    }

    [Fact]
    public async Task EnsureRunning_ConfigAlreadyExists_SkipsInitConfig()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "tokopt-sbxcfg-" + Guid.NewGuid().ToString("N") + ".toml");
        File.WriteAllText(configPath, "[runtime]\ntype = \"docker\"\n");
        try
        {
            var runner = new FakeRunner { ["docker"] = new ProcResult(0, "OK", "") };
            var mgr = new TestableManager(runner, Settings(), probeUp: false)
            {
                ConfigPathOverride = configPath,
                ProbeUpAfterStart = true,
            };

            await mgr.EnsureRunningAsync(CancellationToken.None);

            Assert.DoesNotContain(runner.Calls, c => c.Args.Contains("init-config"));
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public async Task EnsureRunning_ServerNeverHealthy_TimesOutWithError()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "tokopt-sbxcfg-" + Guid.NewGuid().ToString("N") + ".toml");
        try
        {
            var runner = new FakeRunner { ["docker"] = new ProcResult(0, "OK", "") };
            var mgr = new TestableManager(runner, Settings(), probeUp: false)
            {
                ConfigPathOverride = configPath,
                ProbeUpAfterStart = false,
                PollAttemptsOverride = 2,
                PollIntervalOverride = TimeSpan.FromMilliseconds(10),
            };

            var status = await mgr.EnsureRunningAsync(CancellationToken.None);

            Assert.False(status.ServerUp);
            Assert.False(string.IsNullOrWhiteSpace(status.Error));
        }
        finally
        {
            if (File.Exists(configPath)) File.Delete(configPath);
        }
    }

    private static SandboxSettings Settings() => new() { Domain = "localhost:8080", Protocol = "http" };

    private sealed class FakeRunner : IProcessRunner
    {
        public Dictionary<string, ProcResult> Results { get; } = new();
        public List<(string Exe, IReadOnlyList<string> Args)> Calls { get; } = new();

        public ProcResult this[string exe] { get => Results[exe]; init => Results[exe] = value; }

        public Task<ProcResult> RunAsync(string exe, IReadOnlyList<string> args,
            IDictionary<string, string>? env = null, CancellationToken ct = default)
        {
            Calls.Add((exe, args));
            if (args.Contains("init-config"))
                File.WriteAllText(args[2], "# simulated opensandbox-server config\n");
            var key = Path.GetFileNameWithoutExtension(exe);
            return Task.FromResult(Results.TryGetValue(key, out var r) ? r : new ProcResult(0, "", ""));
        }
    }

    private sealed class TestableManager : ServerLifecycleManager
    {
        private readonly bool _probeUp;

        public TestableManager(IProcessRunner runner, SandboxSettings settings, bool probeUp)
            : base(runner, settings) => _probeUp = probeUp;

        public string? ConfigPathOverride { get; set; }
        public bool ProbeUpAfterStart { get; set; }
        public int PollAttemptsOverride { get; set; }
        public TimeSpan PollIntervalOverride { get; set; }
        public bool StartServerCalled { get; private set; }

        protected override string ResolveConfigPath()
            => ConfigPathOverride ?? base.ResolveConfigPath();

        protected override void StartServer(string configPath) => StartServerCalled = true;

        protected override int PollAttempts => PollAttemptsOverride > 0 ? PollAttemptsOverride : base.PollAttempts;
        protected override TimeSpan PollInterval => PollIntervalOverride > TimeSpan.Zero ? PollIntervalOverride : base.PollInterval;

        protected override Task<bool> ProbeHealthAsync(Uri healthUri, CancellationToken ct)
            => Task.FromResult(_probeUp || (StartServerCalled && ProbeUpAfterStart));
    }
}
