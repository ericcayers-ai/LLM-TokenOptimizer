using System.Diagnostics;
using System.Runtime.Versioning;
using TokenOptimizer.Core.RateLimit;

namespace TokenOptimizer.Providers;

[SupportedOSPlatform("windows")]
public sealed class ProcessSessionHandle : ISessionHandle
{
    private readonly Process? _process;
    private readonly RateLimitWatcher? _watcher;

    public ProcessSessionHandle(string providerName, string projectPath, Process? process, bool watchForRateLimit = false)
    {
        ProviderName = providerName;
        ProjectPath = projectPath;
        _process = process;
        StartedAt = DateTimeOffset.UtcNow;

        if (watchForRateLimit && process is not null)
        {
            _watcher = new RateLimitWatcher();
            _watcher.Start(process.Id);
            RateLimitOutcome = WaitForExitAndStopWatcherAsync(process, _watcher);
        }
        else
        {
            RateLimitOutcome = Task.FromResult(new RateLimitOutcome(false, null));
        }
    }

    public string ProviderName { get; }
    public string ProjectPath { get; }
    public DateTimeOffset StartedAt { get; }

    /// <summary>
    /// Resolves once the launched session's process exits: whether a
    /// usage-limit banner was detected in its console during the session,
    /// and (if so) the resume time to record for the fallback chain's
    /// cooldown tracking. Never faults - watcher failures resolve to "no
    /// rate limit detected" rather than propagating.
    /// </summary>
    public Task<RateLimitOutcome> RateLimitOutcome { get; }

    public int? ProcessId
    {
        get
        {
            try { return _process is { HasExited: false } ? _process.Id : null; }
            catch { return null; }
        }
    }

    public bool IsRunning
    {
        get
        {
            try { return _process is { HasExited: false }; }
            catch { return false; }
        }
    }

    private static async Task<RateLimitOutcome> WaitForExitAndStopWatcherAsync(Process process, RateLimitWatcher watcher)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch
        {
            // Best effort - fall through to stopping the watcher regardless.
        }

        watcher.Stop();
        return new RateLimitOutcome(watcher.RateLimitDetected, watcher.ResumeAtUtc);
    }
}
