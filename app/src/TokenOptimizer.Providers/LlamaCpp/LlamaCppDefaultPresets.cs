using TokenOptimizer.Core.Models;

namespace TokenOptimizer.Providers.LlamaCpp;

/// <summary>How hard a quant compresses the model - drives both sampler defaults and which system prompt it gets. Derived from the quant tag string, not stored per-model, since the same five bands apply to any family's naming scheme (Unsloth's IQ/Q-prefixed tags and mudler's APEX profile names alike).</summary>
public enum LlamaCppQuantTier
{
    /// <summary>~1-2 bit (IQ1_*, I-MINI/MINI) - smallest, fastest, most error-prone.</summary>
    UltraCompact,
    /// <summary>~2-3 bit (IQ2_*, Q2_*, I-COMPACT/COMPACT).</summary>
    Compact,
    /// <summary>~3-4 bit (IQ3_*, Q3_*, IQ4_*, Q4_*, I-BALANCED/BALANCED) - the everyday middle ground.</summary>
    Standard,
    /// <summary>~5-6 bit (Q5_*, Q6_*).</summary>
    High,
    /// <summary>~8 bit and up (Q8_*, BF16, I-QUALITY/QUALITY) - closest to the unquantized model's real behavior.</summary>
    FullPrecision,
}

/// <summary>
/// Auto-configuration so a local model never needs manual Studio setup:
/// context length, sampler params, and a tier-calibrated system prompt are
/// all derived from the quant tag alone. LlamaCppAdapter only falls back to
/// this when the user hasn't saved their own preset via LlamaCppPresetStore
/// (an explicit user preset always wins).
/// </summary>
public static class LlamaCppDefaultPresets
{
    public static LlamaCppQuantTier ClassifyTier(string quantTag)
    {
        var tag = quantTag.ToUpperInvariant();
        if (tag.Contains("IQ1") || tag.Contains("MINI")) return LlamaCppQuantTier.UltraCompact;
        if (tag.Contains("IQ2") || tag.StartsWith("Q2") || tag.Contains("_Q2") || tag.Contains("COMPACT")) return LlamaCppQuantTier.Compact;
        if (tag.Contains("Q8") || tag.Contains("BF16") || tag.Contains("QUALITY")) return LlamaCppQuantTier.FullPrecision;
        if (tag.Contains("Q5") || tag.Contains("Q6")) return LlamaCppQuantTier.High;
        return LlamaCppQuantTier.Standard; // IQ3/Q3/IQ4/Q4, BALANCED, and anything unrecognized
    }

    /// <summary>
    /// Sampler baseline is Qwen's own published recommended non-thinking
    /// inference settings (temp 0.7 / top-p 0.8 / top-k 20 / min-p 0) - both
    /// supported families are Qwen3.5-class architectures (Qwen3.8-27B
    /// directly; KAT-Coder-V2.5-Dev is Qwen3_5MoeForConditionalGeneration
    /// per its own model card). Temperature is nudged down at the low-bit
    /// end: this is an engineering heuristic (heavier quantization =
    /// narrower safe sampling range before incoherence), not a documented
    /// Qwen/Unsloth recommendation - flagged here so it reads as a judgment
    /// call, not a cited fact.
    /// </summary>
    public static LlamaCppLaunchOptions Build(LlamaCppModelFamily family, string quantTag)
    {
        var tier = ClassifyTier(quantTag);
        var temperature = tier switch
        {
            LlamaCppQuantTier.UltraCompact => 0.45,
            LlamaCppQuantTier.Compact => 0.55,
            LlamaCppQuantTier.Standard => 0.70,
            LlamaCppQuantTier.High => 0.70,
            LlamaCppQuantTier.FullPrecision => 0.70,
            _ => 0.70,
        };

        return new LlamaCppLaunchOptions
        {
            ContextLength = 131_072, // always-128k requirement, uniform across every model/quant
            Persist = true,
            Temperature = temperature,
            TopP = 0.8,
            TopK = 20,
            MinP = 0,
            SystemPromptAppend = LlamaCppSystemPromptCatalog.GetSystemPrompt(tier, family.DisplayName),
        };
    }
}

/// <summary>
/// One coding-agent system-prompt append per quant tier (not per literal
/// quant tag - ~30 near-duplicate prompts would add no real behavioral
/// difference over 5 tiers, since what actually needs compensating for is
/// how hard the weights are compressed, which is exactly what tier already
/// encodes). Structure follows the same shape public coding-agent system
/// prompts use (Claude Code's own, and widely-discussed leaked prompts from
/// Cursor/Cline/Windsurf): identity, an explicit plan-before-acting and
/// verify-before-claiming-done discipline, tool-use ground rules, and scope
/// discipline - written fresh for this app, not reproduced from any of them.
/// Appended via --append-system-prompt, so it layers on top of Claude
/// Code's own default system prompt rather than replacing it.
/// </summary>
public static class LlamaCppSystemPromptCatalog
{
    public static string GetSystemPrompt(LlamaCppQuantTier tier, string familyDisplayName)
    {
        var calibration = tier switch
        {
            LlamaCppQuantTier.UltraCompact =>
                "You are running a heavily compressed (ultra-low-bit) quantization of this model. Assume your reasoning is noisier and your recall of exact syntax is less reliable than an unquantized model. Work in the smallest steps that still make progress: one file, one function, or one command at a time. Re-read a file immediately before editing it rather than trusting memory of its contents from earlier in the conversation. Prefer the most literal, conventional solution over a clever one - cleverness is where compression-induced errors show up first. After any multi-step change, re-verify each step individually instead of assuming the chain held together.",
            LlamaCppQuantTier.Compact =>
                "You are running a compact low-bit quantization of this model. Keep changes scoped to what was actually asked, and check your own output for small mistakes (off-by-one errors, mismatched names, dropped edge cases) before presenting it as done. Favor straightforward, well-known patterns over multi-layered abstractions - they are easier for you to get right and easier for a human to catch if you didn't. When a task has more than two or three moving parts, break it into an explicit short plan first.",
            LlamaCppQuantTier.Standard =>
                "You are running this model at a standard mid-range quantization - a reasonable balance of speed and fidelity. Plan briefly before acting on anything non-trivial, make the change, then verify the result actually satisfies the request before calling it done. Normal engineering judgment applies; you don't need to over-simplify, but don't skip verification either.",
            LlamaCppQuantTier.High =>
                "You are running this model at a high-fidelity quantization, close to full precision. You can reliably handle multi-file changes and longer chains of reasoning within a single turn. Still plan before large changes and verify before claiming completion - fidelity reduces the rate of quantization-induced mistakes, it does not eliminate the value of checking your own work.",
            LlamaCppQuantTier.FullPrecision =>
                "You are running this model at full or near-full precision - its most capable configuration. Use that headroom for harder judgment calls: weighing trade-offs, spotting subtler edge cases, and handling larger multi-file or multi-step tasks in one pass. Verification before claiming completion still applies regardless of precision.",
            _ => "",
        };

        return $"You are acting as a local coding agent ({familyDisplayName}) inside Claude Code, working directly in the user's project. Stay focused on the specific task requested - do not refactor, add features, or change scope beyond what was asked. Use the available tools (read, edit, search, run) rather than guessing at file contents or command output. {calibration}";
    }
}
