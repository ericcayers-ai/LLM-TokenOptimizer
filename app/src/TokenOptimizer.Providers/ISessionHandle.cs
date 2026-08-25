using TokenOptimizer.Core.RateLimit;

namespace TokenOptimizer.Providers;

public interface ISessionHandle
{
    string ProviderName { get; }
    string ProjectPath { get; }
    int? ProcessId { get; }
    bool IsRunning { get; }
    DateTimeOffset StartedAt { get; }

    /// <summary>Resolves once the session exits: whether a usage-limit banner was observed and (if so) when to resume. Never faults.</summary>
    Task<RateLimitOutcome> RateLimitOutcome { get; }
}
