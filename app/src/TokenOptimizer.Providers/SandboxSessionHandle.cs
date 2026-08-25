using TokenOptimizer.Core.RateLimit;
using TokenOptimizer.Sandbox;

namespace TokenOptimizer.Providers;

/// <summary>
/// Session handle for a provider session running inside a sandbox. Consumes
/// the sandbox runtime's ExecAsync event stream on a background pump, feeding
/// output chunks into a RateLimitScanner (the stream-fed twin of
/// RateLimitWatcher - in-sandbox sessions have no host process or console to
/// watch by PID). On ExecExit the rate-limit outcome task completes with what
/// was observed; it never faults - stream failures resolve to "no rate limit
/// detected". Lives in Providers because Core already references Sandbox, so
/// a Sandbox-side implementation could not reach Core.RateLimit or this
/// namespace without a reference cycle.
/// </summary>
public sealed class SandboxSessionHandle : ISessionHandle, IDisposable
{
    private readonly ISandboxRuntime _runtime;
    private readonly string _sandboxId;
    private readonly RateLimitScanner? _scanner;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<RateLimitOutcome> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _finished;
    private bool _disposed;

    public SandboxSessionHandle(
        string providerName,
        string projectPath,
        ISandboxRuntime runtime,
        string sandboxId,
        IAsyncEnumerable<ExecEvent> events,
        bool watchForRateLimit = true)
    {
        ProviderName = providerName;
        ProjectPath = projectPath;
        _runtime = runtime;
        _sandboxId = sandboxId;
        StartedAt = DateTimeOffset.UtcNow;

        if (watchForRateLimit)
        {
            _scanner = new RateLimitScanner();
            _ = PumpAsync(events, _cts.Token);
        }
        else
        {
            Complete(); // immediately-resolved default outcome, like ProcessSessionHandle
        }
    }

    public string ProviderName { get; }
    public string ProjectPath { get; }
    public DateTimeOffset StartedAt { get; }

    /// <summary>Resolves once the sandboxed session exits: whether a usage-limit banner was seen in its output and (if so) when to resume. Never faults.</summary>
    public Task<RateLimitOutcome> RateLimitOutcome => _completion.Task;

    /// <summary>No host process exists for an in-sandbox session.</summary>
    public int? ProcessId => null;

    /// <summary>True until a terminal ExecExit is observed (or disposal).</summary>
    public bool IsRunning => !_finished;

    /// <summary>Best-effort kill of the underlying sandbox; fire-and-forget.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        Complete();
        _ = KillSandboxAsync();
    }

    private async Task KillSandboxAsync()
    {
        try { await _runtime.KillAsync(_sandboxId).ConfigureAwait(false); }
        catch
        {
            // Best effort - disposal must never throw.
        }
    }

    private async Task PumpAsync(IAsyncEnumerable<ExecEvent> events, CancellationToken token)
    {
        try
        {
            await foreach (var e in events.WithCancellation(token).ConfigureAwait(false))
            {
                switch (e)
                {
                    case ExecOutput output when !string.IsNullOrEmpty(output.Text):
                        _scanner!.Scan(output.Text);
                        break;
                    case ExecExit:
                        Complete();
                        return;
                }
            }

            Complete(); // stream ended without an explicit exit event
        }
        catch (OperationCanceledException)
        {
            Complete(); // disposed mid-session - resolve with whatever was observed
        }
        catch
        {
            Complete(); // never fault - failures resolve to the observed outcome
        }
    }

    private void Complete()
    {
        _finished = true;
        _completion.TrySetResult(new RateLimitOutcome(
            _scanner?.RateLimitDetected ?? false,
            _scanner?.ResumeAtUtc));
    }
}
