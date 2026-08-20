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
    /// <summary>--max-seq-length (confirmed via `unsloth start claude -h` against the actually-installed CLI, v2026.8.18 - the docs page's "--context-length" alias isn't in this version's real --help). Always-128k-context requirement: default target for every supported model family/quant.</summary>
    public int ContextLength { get; init; } = 131_072;

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

    /// <summary>--temperature (confirmed via `unsloth start claude -h`, v2026.8.18 - not "--temp"). Range 0.0-2.0. Null leaves it at the model's own recommended default.</summary>
    public double? Temperature { get; init; }

    /// <summary>--top-p. Null leaves it at unsloth's own default.</summary>
    public double? TopP { get; init; }

    /// <summary>--top-k. Null leaves it at unsloth's own default.</summary>
    public int? TopK { get; init; }

    /// <summary>--min-p. Null leaves it at unsloth's own default.</summary>
    public double? MinP { get; init; }

    /// <summary>--chat-template-kwargs - NOT present in `unsloth start claude -h` on the actually-installed v2026.8.18 (may be from an older/different CLI version, or forwarded without being a declared option). Left in place since it's opt-in and null by default; treat as unconfirmed on current Unsloth versions rather than relied-upon.</summary>
    public string? ChatTemplateKwargs { get; init; }

    /// <summary>--launch/--no-launch - whether unsloth should open/attach the agent itself. Null leaves it at unsloth's own default (launch).</summary>
    public bool? Launch { get; init; }

    /// <summary>--serve/--no-serve - whether unsloth should start its own local server vs. only printing the command/environment. Null leaves it at unsloth's own default (serve).</summary>
    public bool? Serve { get; init; }

    /// <summary>Raw passthrough appended after unsloth's own flags - anything unsloth doesn't recognize forwards straight to Claude Code (its own documented behavior), so this is also how a manual --continue/--resume-style Claude flag would be supplied if not already covered by SessionLaunchOptions.ResumeMode.</summary>
    public string ExtraArguments { get; init; } = "";

    /// <summary>
    /// Text for Claude Code's own --append-system-prompt flag (docs.claude.com/en/docs/claude-code/cli-reference
    /// - real, documented Claude Code flag, not an Unsloth one; unsloth forwards it straight through since it
    /// doesn't recognize it itself). Kept as its own field rather than folded into ExtraArguments so it gets
    /// quoted as one argv token instead of BuildArguments re-quoting an already-multi-token string.
    /// See LlamaCppSystemPromptCatalog for what goes here and why it's tier-specific.
    /// </summary>
    public string? SystemPromptAppend { get; init; }

    /// <summary>
    /// Unsloth's own server has no rolling-context-window/auto-compact
    /// feature (verified against its docs - not a documented flag or
    /// endpoint). When true (the default), LlamaCppAdapter boots Unsloth's
    /// server with --no-launch, routes Claude Code through TokenOptimizer's
    /// own RollingContextProxy instead of straight at Unsloth, and that
    /// proxy does the trimming client-side. Set false to launch directly
    /// through `unsloth start claude` instead (skips the extra local hop).
    /// </summary>
    public bool RollingContextWindowEnabled { get; init; } = true;
}
