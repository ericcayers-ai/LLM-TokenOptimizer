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

    [Fact]
    public async Task Exec_DeliberatelyFailingCommand_ReportsNonZeroExit()
    {
        if (!IsEnabled) return;
        var runtime = new OpenSandboxSdkRuntime(LocalSettings());
        var handle = await runtime.CreateAsync(NewSpec());

        try
        {
            // Exit-code contract: a command that fails on purpose must surface
            // as a non-zero terminal ExecExit, never a silent 0 - callers gate
            // failover and health decisions on this.
            var exit = await RunToExitAsync(runtime, handle.Id, new[] { "sh", "-c", "exit 3" });
            Assert.Equal(3, exit);
        }
        finally
        {
            await runtime.KillAsync(handle.Id);
        }
    }

    [Fact]
    public async Task Container_ReachesHostListenerViaHostDockerInternal()
    {
        if (!IsEnabled) return;
        // Host proxies bind 127.0.0.1:<port>; containers must reach them via
        // host.docker.internal instead. This pins that the rewritten env
        // values actually round-trip from inside the sandbox.
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = RespondWithMarkerAsync(listener);

        var runtime = new OpenSandboxSdkRuntime(LocalSettings());
        var handle = await runtime.CreateAsync(new SandboxSpec(
            Image: "opensandbox/aio:latest",
            Mounts: Array.Empty<SandboxMount>(),
            Env: new Dictionary<string, string>
            {
                ["TOKOPT_HOST_TARGET"] = $"http://host.docker.internal:{port}/ping",
            }));

        try
        {
            var events = new List<ExecEvent>();
            await foreach (var e in runtime.ExecAsync(handle.Id, new[]
                           {
                               "sh", "-c",
                               "curl -s --max-time 10 \"$TOKOPT_HOST_TARGET\" || wget -qO- -T 10 \"$TOKOPT_HOST_TARGET\"",
                           }))
                events.Add(e);

            Assert.Equal(0, Assert.IsType<ExecExit>(events[^1]).Code);
            Assert.Contains(events, e => e is ExecOutput o && o.Stream == "stdout" && o.Text.Contains("tokopt-host-reachable"));
        }
        finally
        {
            await runtime.KillAsync(handle.Id);
        }

        await serverTask;
    }

    private static async Task<int> RunToExitAsync(OpenSandboxSdkRuntime runtime, string id, IReadOnlyList<string> argv)
    {
        ExecEvent last = new ExecOutput("stdout", string.Empty);
        await foreach (var e in runtime.ExecAsync(id, argv))
            last = e;
        return Assert.IsType<ExecExit>(last).Code;
    }

    private static async Task RespondWithMarkerAsync(System.Net.Sockets.TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var buffer = new byte[8192];
        _ = await stream.ReadAsync(buffer); // request headers arrive in the first segment for curl/wget
        var body = "tokopt-host-reachable"u8.ToArray();
        var head = System.Text.Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(body);
    }

    private static SandboxSpec NewSpec() => new(
        Image: "opensandbox/aio:latest",
        Mounts: Array.Empty<SandboxMount>());

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
