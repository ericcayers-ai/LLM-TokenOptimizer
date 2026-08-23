using TokenOptimizer.Sandbox;

namespace TokenOptimizer.Core.Tests.Sandbox;

public class FakeSandboxRuntimeTests
{
    [Fact]
    public async Task CreateAsync_ReturnsUniqueIds_AcrossCalls()
    {
        var runtime = new FakeSandboxRuntime();

        var first = await runtime.CreateAsync(Spec());
        var second = await runtime.CreateAsync(Spec());

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task ExecAsync_ReplaysScriptedEvents_InOrder()
    {
        var runtime = new FakeSandboxRuntime();
        var sandbox = await runtime.CreateAsync(Spec());
        runtime.QueueOutput(sandbox.Id,
            new ExecOutput("stdout", "tokenoptimizer"),
            new ExecExit(0));

        var events = new List<ExecEvent>();
        await foreach (var e in runtime.ExecAsync(sandbox.Id, new[] { "echo", "tokenoptimizer" }))
            events.Add(e);

        Assert.Equal(2, events.Count);
        var output = Assert.IsType<ExecOutput>(events[0]);
        Assert.Equal("stdout", output.Stream);
        Assert.Equal("tokenoptimizer", output.Text);
        Assert.IsType<ExecExit>(events[1]);
    }

    [Fact]
    public async Task WriteThenRead_RoundTripsExactContent()
    {
        var runtime = new FakeSandboxRuntime();
        var sandbox = await runtime.CreateAsync(Spec());

        await runtime.WriteFileAsync(sandbox.Id, "/tmp/hello.txt", "Hello World");
        var content = await runtime.ReadFileAsync(sandbox.Id, "/tmp/hello.txt");

        Assert.Equal("Hello World", content);
    }

    [Fact]
    public async Task Kill_MarksDead_AndLaterOpsThrow()
    {
        var runtime = new FakeSandboxRuntime();
        var sandbox = await runtime.CreateAsync(Spec());

        await runtime.KillAsync(sandbox.Id);

        Assert.True(runtime.IsDead(sandbox.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ReadFileAsync(sandbox.Id, "/tmp/x"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.WriteFileAsync(sandbox.Id, "/tmp/x", "y"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in runtime.ExecAsync(sandbox.Id, new[] { "true" })) { }
        });
    }

    [Fact]
    public async Task Kill_IsIdempotent()
    {
        var runtime = new FakeSandboxRuntime();
        var sandbox = await runtime.CreateAsync(Spec());

        await runtime.KillAsync(sandbox.Id);
        await runtime.KillAsync(sandbox.Id);

        Assert.True(runtime.IsDead(sandbox.Id));
    }

    private static SandboxSpec Spec() => new(
        Image: "opensandbox/aio:latest",
        Mounts: new Dictionary<string, string> { ["/workspace"] = @"C:\proj" });
}
