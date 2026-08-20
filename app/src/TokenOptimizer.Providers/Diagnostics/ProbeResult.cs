namespace TokenOptimizer.Providers.Diagnostics;

/// <summary>
/// Outcome of a single live model probe. <see cref="Ok"/> is true only when
/// the model returned a non-empty text response to the cheap probe prompt.
/// </summary>
public sealed record ProbeResult(
    bool Ok,
    string Provider,
    string Model,
    string ResponseText,
    int LatencyMs,
    string? Error,
    bool Skipped = false,
    string? SkipReason = null);
