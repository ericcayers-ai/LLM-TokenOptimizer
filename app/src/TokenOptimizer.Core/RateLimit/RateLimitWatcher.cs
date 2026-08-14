using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace TokenOptimizer.Core.RateLimit;

/// <summary>
/// Watches a launched provider's console for usage-limit text and, when
/// found, either selects Claude Code's own "Stop and wait" retry option or
/// waits out a parsed reset time itself before sending "continue". Ported
/// from the embedded RateLimitWatcher .NET type in LLM-TokenOptimizer.ps1 -
/// same regex patterns, same 5-hour default window, same 2-minute cooldown
/// between detections so a persistent banner doesn't re-trigger every poll.
/// One instance watches one launched session; call Start with that
/// session's OS process id, Stop when it exits or the app is closing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RateLimitWatcher
{
    private static readonly Regex RateLimitPattern = new(
        @"(5-hour limit reached|weekly limit|session limit|You've hit your (weekly|session) limit|" +
        @"rate limit reached|rate.?limit exceeded|usage limit|quota exceeded|quota reached|" +
        @"too many requests|HTTP 429|\b429\b)",
        RegexOptions.IgnoreCase);

    private static readonly Regex ResetTimePattern = new(
        @"resets?\s+(?<time>\d{1,2}(:\d{2})?\s*(am|pm)?)", RegexOptions.IgnoreCase);

    private static readonly Regex StopAndWaitPattern = new(@"Stop and wait", RegexOptions.IgnoreCase);

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;

    public bool RateLimitDetected { get; private set; }
    public DateTimeOffset? ResumeAtUtc { get; private set; }

    public void Start(int consoleOwnerProcessId, int pollIntervalMs = 3000)
    {
        if (_loopTask is not null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loopTask = Task.Run(() => Loop(consoleOwnerProcessId, pollIntervalMs, token), token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loopTask?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts = null;
        _loopTask = null;
    }

    private void Loop(int consoleOwnerProcessId, int pollIntervalMs, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= _cooldownUntil)
                {
                    var tail = ConsoleWatcherInterop.ReadVisibleScreen(consoleOwnerProcessId);
                    if (!string.IsNullOrEmpty(tail) && RateLimitPattern.IsMatch(tail))
                    {
                        Handle(consoleOwnerProcessId, tail, token);
                        _cooldownUntil = DateTimeOffset.UtcNow.AddMinutes(2);
                    }
                }
            }
            catch
            {
                // Best-effort - a watcher failure must never take down the launched session.
            }

            try { Task.Delay(pollIntervalMs, token).Wait(token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Handle(int consoleOwnerProcessId, string tail, CancellationToken token)
    {
        RateLimitDetected = true;

        // Prefer the CLI's own built-in "Stop and wait" retry flow over
        // reimplementing wait/retry - give it a few seconds to render.
        for (var i = 0; i < 5 && !token.IsCancellationRequested; i++)
        {
            Thread.Sleep(1000);
            var screen = ConsoleWatcherInterop.ReadVisibleScreen(consoleOwnerProcessId);
            if (screen is not null && StopAndWaitPattern.IsMatch(screen))
            {
                // Unknown exact resume time in this path - assume the
                // documented 5-hour window as a conservative estimate.
                ResumeAtUtc = DateTimeOffset.UtcNow.AddHours(5);
                ConsoleWatcherInterop.SendEnter(consoleOwnerProcessId);
                return;
            }
        }

        // Fallback: no menu appeared. Parse a reset time out of the matched
        // text and wait it out ourselves, then send "continue".
        var wait = TimeSpan.FromHours(5); // documented default window
        var match = ResetTimePattern.Match(tail);
        if (match.Success && DateTime.TryParse(match.Groups["time"].Value.Trim(), out var parsed))
        {
            var target = DateTime.Today.Add(parsed.TimeOfDay);
            if (target <= DateTime.Now) target = target.AddDays(1);
            var candidate = target - DateTime.Now;
            if (candidate > TimeSpan.Zero && candidate < TimeSpan.FromHours(24)) wait = candidate;
        }

        wait = wait.Add(TimeSpan.FromSeconds(60)); // safety margin past reset
        var resumeAt = DateTimeOffset.UtcNow.Add(wait);
        ResumeAtUtc = resumeAt;

        while (!token.IsCancellationRequested && DateTimeOffset.UtcNow < resumeAt)
        {
            var remaining = resumeAt - DateTimeOffset.UtcNow;
            var sleepMs = (int)Math.Min(30_000, Math.Max(1000, remaining.TotalMilliseconds));
            try { Task.Delay(sleepMs, token).Wait(token); }
            catch (OperationCanceledException) { return; }
        }

        if (token.IsCancellationRequested) return;
        ConsoleWatcherInterop.SendStringWithEnter(consoleOwnerProcessId, "continue");
    }
}
