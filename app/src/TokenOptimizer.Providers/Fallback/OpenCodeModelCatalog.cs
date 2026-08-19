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
/// rather than fetched. Index 0 (mimo2.5) is the adapter's default: best
/// balance of quality/cost for everyday use. Order matters for the UI
/// dropdown, not for routing. The ModelOverride ComboBox stays IsEditable
/// (see MainViewModel.StaticModelCatalog), so any model id the operator's
/// OpenCode server actually routes to still works even if it isn't listed
/// here - this is a curated shortlist, not a hard allowlist.
/// </summary>
public static class OpenCodeModelCatalog
{
    public static readonly IReadOnlyList<OpenCodeModel> Models = new[]
    {
        new OpenCodeModel("mimo2.5", OpenCodeModelTier.Balanced, "Default - best balance of quality and cost"),
        new OpenCodeModel("v4-flash", OpenCodeModelTier.Cheap, "Cheapest option"),
        new OpenCodeModel("minimax2.7", OpenCodeModelTier.Balanced, "Supported alternative"),
        new OpenCodeModel("kimik2.5", OpenCodeModelTier.Balanced, "Supported alternative"),
        new OpenCodeModel("qwen3.8", OpenCodeModelTier.Balanced, "Supported alternative"),
        new OpenCodeModel("glm-5.2", OpenCodeModelTier.Expensive, "Optional - highest cost"),
    };

    public static string DefaultModel => Models[0].Id;

    public static IReadOnlyList<string> ModelIds => Models.Select(m => m.Id).ToList();
}
