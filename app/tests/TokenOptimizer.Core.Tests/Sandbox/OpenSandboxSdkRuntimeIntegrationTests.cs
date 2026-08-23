using TokenOptimizer.Sandbox;

namespace TokenOptimizer.Core.Tests.Sandbox;

public class OpenSandboxSdkRuntimeIntegrationTests
{
    [Fact]
    public async Task CreateExecFileRoundTripAndKill_AgainstLiveServer()
    {
        if (!IsEnabled) return;
        var runtime = new OpenSandboxSdkRuntime(LocalSettings());

        var handle = await runtime.CreateAsync(new SandboxSpec(
            Image: "opensandbox/aio:latest",
            Mounts: Array.Empty<SandboxMount>()));

        try
        {
            var events = new List<ExecEvent>();
            await foreach (var e in runtime.ExecAsync(handle.Id, new[] { "echo", "tokenoptimizer" }))
                events.Add(e);

            var exit = Assert.IsType<ExecExit>(events[^1]);
            Assert.Equal(0, exit.Code);
            Assert.Contains(events, e => e is ExecOutput o && o.Stream == "stdout" && o.Text.Contains("tokenoptimizer"));

            await runtime.WriteFileAsync(handle.Id, "/tmp/to.txt", "roundtrip");
            Assert.Equal("roundtrip", await runtime.ReadFileAsync(handle.Id, "/tmp/to.txt"));

            await runtime.KillAsync(handle.Id);
        }
        catch
        {
            await runtime.KillAsync(handle.Id);
            throw;
        }

        var deadEx = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in runtime.ExecAsync(handle.Id, new[] { "true" })) { }
        });
        Assert.Contains("is dead", deadEx.Message);
    }

    private static bool IsEnabled =>
        Environment.GetEnvironmentVariable("TOKENOPTIMIZER_DOCKER_TESTS") == "1"
        && DockerAvailable();

    private static bool DockerAvailable()
    {
        try
        {
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "info",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            proc.WaitForExit(15000);
            return proc.HasExited && proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static SandboxSettings LocalSettings() =>
        new() { Domain = "localhost:8080", Protocol = "http" };
}
