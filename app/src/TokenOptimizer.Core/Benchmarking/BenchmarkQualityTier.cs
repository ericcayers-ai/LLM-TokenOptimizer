namespace TokenOptimizer.Core.Benchmarking;

/// <summary>
/// run_benchmarks.py itself has no built-in "quality tier" flag - these are
/// presets of its existing real CLI flags (--models, --skip-download), not
/// a reimplementation of its scoring. Deliberately never overrides
/// --max-tokens: the script's own MODEL_CONFIG tunes that per model
/// (reasoning models need 10000-14000 tokens just to stop "thinking" and
/// emit an answer - a flat override zeroes out their results rather than
/// speeding them up). Speed instead comes from which models run and
/// whether downloads are skipped, never from starving a model's budget.
/// </summary>
public enum BenchmarkQualityTier
{
    /// <summary>Every catalog model (or the one explicitly picked), each at its own researched config - most reliable, slowest.</summary>
    MaxQuality,

    /// <summary>When running "all models," skips the ones needing a big reasoning budget (&gt;=9000 tokens) - fewer, faster models, every result still reliable.</summary>
    Balanced,

    /// <summary>Same model exclusion as Balanced, plus --skip-download (assumes models are already on disk) - fastest full sweep.</summary>
    Quick,
}
