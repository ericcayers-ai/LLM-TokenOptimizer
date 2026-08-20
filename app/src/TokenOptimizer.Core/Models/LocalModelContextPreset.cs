namespace TokenOptimizer.Core.Models;

/// <summary>
/// Unsloth adapter's context preset. This only maps to
/// --context-length: TokenOptimizer drives local models through `unsloth
/// start`, which doesn't expose separate GPU-layer/batch-size flags of its
/// own to size per preset.
/// </summary>
public enum LocalModelContextPreset
{
    Fast,
    Balanced,
    Max,
}
