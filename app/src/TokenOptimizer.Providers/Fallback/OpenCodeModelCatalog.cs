namespace TokenOptimizer.Providers.Fallback;

public enum OpenCodeModelTier
{
    Cheap,
    Balanced,
    Expensive,
}

public sealed record OpenCodeModel(string Id, OpenCodeModelTier Tier, string Description);

/// <summary>
/// OpenCode's self-hosted Go API server routes to whatever models the
/// operator has wired up behind it - there's no discovery endpoint to
/// query, so (like Groq's key-prefix check) the catalog here is curated
/// rather than fetched. Index 0 (mimo-v2.5) is the adapter's default: best
/// balance of quality/cost for everyday use. Order matters for the UI
/// dropdown, not for routing. The ModelOverride ComboBox stays IsEditable
/// (see MainViewModel.StaticModelCatalog), so any model id the operator's
/// OpenCode server actually routes to still works even if it isn't listed
/// here - this is a curated shortlist, not a hard allowlist.
///
/// IDs verified 2026-08-20 against GET /zen/go/v1/models and a live
/// /v1/messages call per model with a real API key - the original ids
/// (mimo2.5, v4-flash, minimax2.7, kimik2.5, qwen3.8) all 404'd
/// ("Model X is not supported"); these are the account's real routed
/// names. deepseek-v4-flash exists in the model list but 400s with
/// RegionError ("only available hosted in China, requires explicit
/// opt-in") - swapped for glm-5 (verified working) as the Cheap tier
/// entry instead.
/// </summary>
public static class OpenCodeModelCatalog
{
    public static readonly IReadOnlyList<OpenCodeModel> Models = new[]
    {
        new OpenCodeModel("mimo-v2.5", OpenCodeModelTier.Balanced, "Default - best balance of quality and cost"),
        new OpenCodeModel("glm-5", OpenCodeModelTier.Cheap, "Cheapest option"),
        new OpenCodeModel("minimax-m2.7", OpenCodeModelTier.Balanced, "Supported alternative"),
        new OpenCodeModel("kimi-k2.5", OpenCodeModelTier.Balanced, "Supported alternative"),
        new OpenCodeModel("qwen3.8-max", OpenCodeModelTier.Balanced, "Supported alternative"),
        new OpenCodeModel("glm-5.2", OpenCodeModelTier.Expensive, "Optional - highest cost"),
    };

    public static string DefaultModel => Models[0].Id;

    public static IReadOnlyList<string> ModelIds => Models.Select(m => m.Id).ToList();
}
