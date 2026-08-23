using TokenOptimizer.Sandbox;

namespace TokenOptimizer.Core.Tests.Sandbox;

public class PreflightGateTests
{
    [Fact]
    public async Task CheckAsync_EverythingUp_IsOkWithNoMissingAndNoSteps()
    {
        var runner = new FakeRunner { ["docker"] = new ProcResult(0, "OK", "") };
        var gate = new PreflightGate(new TestableManager(runner, Settings(), probeUp: true));

        var result = await gate.CheckAsync();

        Assert.True(result.Ok);
        Assert.Empty(result.Missing);
        Assert.Empty(result.Steps);
    }

    [Fact]
    public async Task CheckAsync_DockerDown_MissingHasDockerAndStepsInFixedOrder()
    {
        var runner = new FakeRunner { ["docker"] = new ProcResult(1, "", "Cannot connect to the Docker daemon") };
        var gate = new PreflightGate(new TestableManager(runner, Settings(), probeUp: false));

        var result = await gate.CheckAsync();

        Assert.False(result.Ok);
        Assert.Contains("docker", result.Missing);
        Assert.Equal(new[] { "wsl", "docker", "docker-start", "server" }, result.Steps.Select(s => s.Id));
    }

    [Fact]
    public async Task CheckAsync_DockerUpServerDown_MissingIsOnlyServerAndSingleStep()
    {
        var runner = new FakeRunner { ["docker"] = new ProcResult(0, "OK", "") };
        var gate = new PreflightGate(new TestableManager(runner, Settings(), probeUp: false));

        var result = await gate.CheckAsync();

        Assert.False(result.Ok);
        Assert.Equal(new[] { "opensandbox-server" }, result.Missing);
        Assert.Equal(new[] { "server" }, result.Steps.Select(s => s.Id));
    }

    [Fact]
    public async Task WslStep_ProbeFails_FallsBackToInstallThroughInjectedRunner()
    {
        var mgrRunner = new FakeRunner { ["docker"] = new ProcResult(1, "", "Cannot connect to the Docker daemon") };
        var wslRunner = new StubRunner(args => args.Contains("--status")
            ? new ProcResult(1, "", "")
            : new ProcResult(0, "", ""));
        var gate = new PreflightGate(new TestableManager(mgrRunner, Settings(), probeUp: false), wslRunner);

        var result = await gate.CheckAsync();
        var wsl = result.Steps.Single(s => s.Id == "wsl");
        var ok = await wsl.Execute(CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(2, wslRunner.Calls.Count);
        Assert.Equal(new[] { "--status" }, wslRunner.Calls[0].Args);
        Assert.Equal(new[] { "--install", "--no-distribution" }, wslRunner.Calls[1].Args);
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
            var key = Path.GetFileNameWithoutExtension(exe);
            return Task.FromResult(Results.TryGetValue(key, out var r) ? r : new ProcResult(0, "", ""));
        }
    }

    private sealed class StubRunner : IProcessRunner
    {
        private readonly Func<IReadOnlyList<string>, ProcResult> _respond;

        public StubRunner(Func<IReadOnlyList<string>, ProcResult> respond) => _respond = respond;

        public List<(string Exe, IReadOnlyList<string> Args)> Calls { get; } = new();

        public Task<ProcResult> RunAsync(string exe, IReadOnlyList<string> args,
            IDictionary<string, string>? env = null, CancellationToken ct = default)
        {
            Calls.Add((exe, args));
            return Task.FromResult(_respond(args));
        }
    }

    private sealed class TestableManager : ServerLifecycleManager
    {
        private readonly bool _probeUp;

        public TestableManager(IProcessRunner runner, SandboxSettings settings, bool probeUp)
            : base(runner, settings) => _probeUp = probeUp;

        protected override string ResolveConfigPath()
            => Path.Combine(Path.GetTempPath(), "tokopt-preflight-" + Guid.NewGuid().ToString("N") + ".toml");

        protected override void StartServer(string configPath) { }

        protected override int PollAttempts => 1;
        protected override TimeSpan PollInterval => TimeSpan.FromMilliseconds(1);

        protected override Task<bool> ProbeHealthAsync(Uri healthUri, CancellationToken ct)
            => Task.FromResult(_probeUp);
    }
}
