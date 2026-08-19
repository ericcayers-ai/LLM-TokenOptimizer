namespace TokenOptimizer.Core.Models;

/// <summary>
/// Every flag `unsloth start` actually documents (unsloth.ai/docs/integrations/unsloth-start,
/// unsloth.ai/docs/basics/claude-code) - TokenOptimizer drives local models
/// through that CLI rather than managing llama-server itself: `unsloth
/// start` already runs the OpenAI-compatible server, resolves the GGUF, and
/// launches Claude Code pointed at it without touching Claude's own config
/// files. Deep inference-level tuning beyond the sampler params below (KV
/// cache dtype, flash-attention, batch sizes, RoPE/YaRN, MoE tensor
/// overrides) is Unsloth Studio's surface, not a documented `unsloth start`
/// flag - not modeled here since there's no confirmed way for
/// TokenOptimizer to set it from outside Studio. One record so the flags a
/// user picks can be saved/loaded as a per-model preset (LlamaCppPresetStore).
/// </summary>
public sealed record LlamaCppLaunchOptions
{
    /// <summary>--context-length (alias --max-seq-length). Always-200k-context requirement: default target for both supported model families.</summary>
    public int ContextLength { get; init; } = 200_000;

    /// <summary>--gguf-variant override. Normally left null - the quant is embedded directly in --model as repo:quant, matching unsloth's own generated-command convention.</summary>
    public string? GgufVariant { get; init; }

    /// <summary>--load-in-4bit/--no-load-in-4bit. Only meaningful for non-GGUF Hugging Face models; null leaves it at unsloth's own default.</summary>
    public bool? LoadIn4Bit { get; init; }

    /// <summary>--tensor-parallel/--no-tensor-parallel - multi-GPU toggle. Null leaves it at unsloth's own default.</summary>
    public bool? TensorParallel { get; init; }

    /// <summary>--persist/--no-persist - keep Unsloth-managed agent storage between runs. Null leaves it at unsloth's own default.</summary>
    public bool? Persist { get; init; }

    /// <summary>--as-subagent - keep Claude Code on its current model and register this local model as a subagent instead of replacing the main model.</summary>
    public bool AsSubagent { get; init; }

    /// <summary>--yolo - skips approval prompts. Security-sensitive; defaults off, surface as an explicit opt-in only.</summary>
    public bool Yolo { get; init; }

    /// <summary>--api-key, or null to rely on the UNSLOTH_API_KEY environment variable / a locally running Studio needing no key.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Points at a remote Studio instead of a local one (sets UNSLOTH_STUDIO_URL) - see "Connect to a remote Studio" in the unsloth start docs.</summary>
    public string? StudioUrl { get; init; }

    /// <summary>--temp - sampling temperature. Null leaves it at unsloth's own default.</summary>
    public double? Temperature { get; init; }

    /// <summary>--top-p. Null leaves it at unsloth's own default.</summary>
    public double? TopP { get; init; }

    /// <summary>--top-k. Null leaves it at unsloth's own default.</summary>
    public int? TopK { get; init; }

    /// <summary>--min-p. Null leaves it at unsloth's own default.</summary>
    public double? MinP { get; init; }

    /// <summary>--chat-template-kwargs - raw JSON object string forwarded verbatim (e.g. thinking-mode toggles some chat templates expose). Null omits the flag.</summary>
    public string? ChatTemplateKwargs { get; init; }

    /// <summary>--launch/--no-launch - whether unsloth should open/attach the agent itself. Null leaves it at unsloth's own default (launch).</summary>
    public bool? Launch { get; init; }

    /// <summary>--serve/--no-serve - whether unsloth should start its own local server vs. only printing the command/environment. Null leaves it at unsloth's own default (serve).</summary>
    public bool? Serve { get; init; }

    /// <summary>Raw passthrough appended after unsloth's own flags - anything unsloth doesn't recognize forwards straight to Claude Code (its own documented behavior), so this is also how a manual --continue/--resume-style Claude flag would be supplied if not already covered by SessionLaunchOptions.ResumeMode.</summary>
    public string ExtraArguments { get; init; } = "";
}
