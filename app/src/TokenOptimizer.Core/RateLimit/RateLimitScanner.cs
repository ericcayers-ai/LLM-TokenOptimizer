using System.Text.RegularExpressions;

namespace TokenOptimizer.Core.RateLimit;

/// <summary>
/// Stream-fed twin of RateLimitWatcher: scans session output text for
/// usage-limit banners and parses the reset time to wait out. No console
/// interop - safe for in-sandbox sessions where no host process or console
/// exists to watch. This is the authoritative home for the shared banner
/// regexes; RateLimitWatcher keeps its own private copies for the
/// PID-based console path.
/// </summary>
public sealed class RateLimitScanner
{
    // Same patterns as RateLimitWatcher - keep both in sync.
    private static readonly Regex RateLimitPattern = new(
        @"(5-hour limit reached|weekly limit|session limit|You've hit your (weekly|session) limit|" +
        @"rate limit reached|rate.?limit exceeded|usage limit|quota exceeded|quota reached|" +
        @"too many requests|HTTP 429|\b429\b)",
        RegexOptions.IgnoreCase);

    private static readonly Regex ResetTimePattern = new(
        @"resets?\s+(?<time>\d{1,2}(:\d{2})?\s*(am|pm)?)", RegexOptions.IgnoreCase);

    private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;

    public bool RateLimitDetected { get; private set; }
    public DateTimeOffset? ResumeAtUtc { get; private set; }

    /// <summary>
    /// Scans one chunk of session output for a usage-limit banner. Mirrors
    /// RateLimitWatcher semantics: on match record detection, parse the
    /// reset time when present (today + time-of-day, rolled to tomorrow if
    /// already past, capped under 24h; otherwise the documented 5-hour
    /// default window), always adding a 60-second safety margin past the
    /// reset. Further matches are ignored for a 2-minute cooldown so a
    /// persistent banner does not re-trigger on every chunk.
    /// </summary>
    public void Scan(string outputChunk)
    {
        if (string.IsNullOrEmpty(outputChunk)) return;
        if (DateTimeOffset.UtcNow < _cooldownUntil) return;
        if (!RateLimitPattern.IsMatch(outputChunk)) return;

        RateLimitDetected = true;
        _cooldownUntil = DateTimeOffset.UtcNow.AddMinutes(2);

        var wait = TimeSpan.FromHours(5); // documented default window
        var match = ResetTimePattern.Match(outputChunk);
        if (match.Success && DateTime.TryParse(match.Groups["time"].Value.Trim(), out var parsed))
        {
            var target = DateTime.Today.Add(parsed.TimeOfDay);
            if (target <= DateTime.Now) target = target.AddDays(1);
            var candidate = target - DateTime.Now;
            if (candidate > TimeSpan.Zero && candidate < TimeSpan.FromHours(24)) wait = candidate;
        }

        wait = wait.Add(TimeSpan.FromSeconds(60)); // safety margin past reset
        ResumeAtUtc = DateTimeOffset.UtcNow.Add(wait);
    }
}
