using TokenOptimizer.Providers;
using TokenOptimizer.Sandbox;

namespace TokenOptimizer.Core.Tests.Providers;

public class SandboxSessionHandleTests
{
    [Fact]
    public async Task Pump_ConsumesScriptedEvents_IsRunningFlipsOnExecExit()
    {
        var runtime = new FakeSandboxRuntime();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var handle = new SandboxSessionHandle(
            "claude",
            @"C:\proj",
            runtime,
            "sbx-test",
            GatedStream(gate.Task, new ExecOutput("stdout", "hello")));

        Assert.True(handle.IsRunning);
        Assert.Null(handle.ProcessId);

        gate.TrySetResult();

        await handle.RateLimitOutcome;
        Assert.False(handle.IsRunning);
    }

    [Fact]
    public async Task RateLimitBanner_InOutputChunk_FlipsOutcomeToDetected()
    {
        var runtime = new FakeSandboxRuntime();

        using var handle = new SandboxSessionHandle(
            "claude",
            @"C:\proj",
            runtime,
            "sbx-test",
            Stream(
                new ExecOutput("stdout", "5-hour limit reached \u2225 resets 3pm"),
                new ExecExit(0)));

        var outcome = await handle.RateLimitOutcome;

        Assert.True(outcome.RateLimitDetected);
        Assert.NotNull(outcome.ResumeAtUtc);
        Assert.InRange(
            outcome.ResumeAtUtc!.Value,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(25));
    }

    [Fact]
    public async Task WatchDisabled_OutcomeResolvesImmediately_NoDetection()
    {
        var runtime = new FakeSandboxRuntime();

        using var handle = new SandboxSessionHandle(
            "claude",
            @"C:\proj",
            runtime,
            "sbx-test",
            NeverEndingStream(),
            watchForRateLimit: false);

        Assert.True(handle.RateLimitOutcome.IsCompleted);

        var outcome = await handle.RateLimitOutcome;
        Assert.False(outcome.RateLimitDetected);
        Assert.Null(outcome.ResumeAtUtc);
    }

    [Fact]
    public async Task ThrowingMidStream_NeverFaults_ResolvesNoDetection()
    {
        var runtime = new FakeSandboxRuntime();

        using var handle = new SandboxSessionHandle(
            "claude",
            @"C:\proj",
            runtime,
            "sbx-test",
            ThrowingStream());

        var outcome = await handle.RateLimitOutcome;

        Assert.False(outcome.RateLimitDetected);
        Assert.Null(outcome.ResumeAtUtc);
        Assert.False(handle.IsRunning);
    }

    [Fact]
    public async Task Dispose_KillsRuntimeSandbox()
    {
        var runtime = new FakeSandboxRuntime();
        var sandbox = await runtime.CreateAsync(Spec());

        var handle = new SandboxSessionHandle(
            "claude",
            @"C:\proj",
            runtime,
            sandbox.Id,
            GatedStream(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task));

        handle.Dispose();

        await WaitUntilAsync(() => runtime.IsDead(sandbox.Id));
        Assert.True(runtime.IsDead(sandbox.Id));
    }

    private static SandboxSpec Spec() => new(
        Image: "opensandbox/aio:latest",
        Mounts: new[] { new SandboxMount("/workspace", @"C:\proj") });

    private static async IAsyncEnumerable<ExecEvent> Stream(params ExecEvent[] events)
    {
        foreach (var e in events)
            yield return e;
    }

    private static async IAsyncEnumerable<ExecEvent> GatedStream(Task gate, params ExecEvent[] beforeExit)
    {
        foreach (var e in beforeExit)
            yield return e;
        await gate;
        yield return new ExecExit(0);
    }

    private static async IAsyncEnumerable<ExecEvent> NeverEndingStream()
    {
        while (true)
        {
            await Task.Delay(1000);
            yield return new ExecOutput("stdout", "tick");
        }
    }

    private static async IAsyncEnumerable<ExecEvent> ThrowingStream()
    {
        yield return new ExecOutput("stdout", "working");
        throw new InvalidOperationException("sandbox exploded mid-stream");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var start = DateTime.UtcNow;
        while (!condition())
        {
            if ((DateTime.UtcNow - start).TotalMilliseconds > timeoutMs)
                throw new TimeoutException($"Condition not met within {timeoutMs}ms.");
            await Task.Delay(25);
        }
    }
}
