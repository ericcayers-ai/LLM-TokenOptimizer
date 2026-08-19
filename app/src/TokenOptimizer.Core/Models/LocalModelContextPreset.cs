namespace TokenOptimizer.Core.Models;

/// <summary>
/// llama.cpp adapter's equivalent of LmStudioContextPreset - kept as a
/// separate enum rather than renaming the LM Studio one in place, since
/// LmStudioAdapter itself was only being removed (not touched) until the
/// dev-branch split landed. Unlike LM Studio's version, this only maps to
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
