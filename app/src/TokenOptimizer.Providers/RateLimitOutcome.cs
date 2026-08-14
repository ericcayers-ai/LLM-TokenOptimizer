namespace TokenOptimizer.Providers;

public sealed record RateLimitOutcome(bool RateLimitDetected, DateTimeOffset? ResumeAtUtc);
