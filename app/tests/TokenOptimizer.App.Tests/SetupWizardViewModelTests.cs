using TokenOptimizer.App.ViewModels;
using TokenOptimizer.Sandbox;

namespace TokenOptimizer.App.Tests;

public class SetupWizardViewModelTests
{
    [Fact]
    public async Task RunAsync_HealthFlipsAfterFirstEnsure_RaisesCompletedOnceAndReturnsTrue()
    {
        var runner = new FakeRunner { ["docker"] = new ProcResult(0, "OK", "") };
        var mgr = new FlippingManager(runner, Settings());
        var vm = new SetupWizardViewModel(new PreflightGate(mgr));
        var completed = 0;
        vm.Completed += (_, _) => completed++;

        var ok = await vm.RunAsync();

        Assert.True(ok);
        Assert.Equal(1, completed);
        Assert.NotEmpty(vm.Log);
        Assert.Contains(vm.Log, line => line.Contains("server"));
    }

    [Fact]
    public async Task RunAsync_AlreadyHealthy_CompletesImmediatelyWithoutSteps()
    {
        var runner = new FakeRunner { ["docker"] = new ProcResult(0, "OK", "") };
        var mgr = new FlippingManager(runner, Settings()) { AlwaysUp = true };
        var vm = new SetupWizardViewModel(new PreflightGate(mgr));
        var completed = 0;
        vm.Completed += (_, _) => completed++;

        var ok = await vm.RunAsync();

        Assert.True(ok);
        Assert.Equal(1, completed);
        Assert.DoesNotContain(vm.Log, line => line.Contains("["));
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

    /// <summary>
    /// Reports unhealthy until the first EnsureRunningAsync call flips health,
    /// mirroring "gate starts unhealthy; running the returned steps fixes it".
    /// </summary>
    private sealed class FlippingManager : ServerLifecycleManager
    {
        private int _probes;

        public FlippingManager(IProcessRunner runner, SandboxSettings settings)
            : base(runner, settings) { }

        public bool AlwaysUp { get; set; }
        public bool EnsureRunningCalled { get; private set; }

        protected override string ResolveConfigPath()
            => Path.Combine(Path.GetTempPath(), "tokopt-wizard-" + Guid.NewGuid().ToString("N") + ".toml");

        protected override void StartServer(string configPath) { }

        protected override int PollAttempts => 1;
        protected override TimeSpan PollInterval => TimeSpan.FromMilliseconds(1);

        protected override Task<bool> ProbeHealthAsync(Uri healthUri, CancellationToken ct)
        {
            _probes++;
            return Task.FromResult(AlwaysUp || _probes >= 2);
        }
    }
}
