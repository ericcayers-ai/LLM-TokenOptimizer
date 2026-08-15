namespace TokenOptimizer.Core.Models;

/// <summary>How aggressively LM Studio should size the local model's context window - see LmStudioAdapter.LoadModelWithPresetAsync.</summary>
public enum LmStudioContextPreset
{
    /// <summary>Smallest context, fastest load and inference - favors speed over how much history/code fits in context.</summary>
    Fast,

    /// <summary>Middle ground - enough context for most single-file work without the load time/memory cost of Max.</summary>
    Balanced,

    /// <summary>Starts at the model's largest practical context and halves down (retrying the load) until it actually fits this machine's VRAM/RAM - the most context this PC can support, not a fixed guess.</summary>
    Max,
}
