namespace TokenOptimizer.Providers.Fallback;

public sealed record OpenCodeZenModel(string Id, string Description);

/// <summary>
/// OpenCode Zen - a separate gateway from OpenCode Go, not just a live view
/// of the same catalog: different account/sign-in (opencode.ai/auth),
/// different API key, different base URL (opencode.ai/zen vs Go's
/// opencode.ai/zen/go), and its own OpenAI-chat-completions-compatible
/// endpoint (verified live: GET https://opencode.ai/zen/v1/models needs no
/// auth to list). Zen's full catalog mostly re-hosts models already
/// available elsewhere in this app (GPT-5.x, Claude, Gemini) at markup
/// pricing, so only the free tier is curated here - these ids verified live
/// 2026-08-22 against that endpoint. OpenCode's own docs mark them
/// "available for a limited time"/"limited time" promotional models, so
/// they rotate; re-check that endpoint if one ever 404s.
/// </summary>
public static class OpenCodeZenModelCatalog
{
    public static readonly IReadOnlyList<OpenCodeZenModel> Models = new[]
    {
        new OpenCodeZenModel("big-pickle", "Free - stealth model"),
        new OpenCodeZenModel("mimo-v2.5-free", "Free"),
        new OpenCodeZenModel("hy3-free", "Free"),
        new OpenCodeZenModel("nemotron-3-ultra-free", "Free - NVIDIA"),
        new OpenCodeZenModel("nemotron-3.5-lightning-free", "Free - NVIDIA"),
        new OpenCodeZenModel("muse-spark-1.2-contributor-free", "Free"),
        new OpenCodeZenModel("deepseek-v4-flash-free", "Free"),
        new OpenCodeZenModel("x-preview-f-free", "Free - stealth model"),
        new OpenCodeZenModel("laguna-s-2.1-free", "Free"),
    };

    public static string DefaultModel => Models[0].Id;

    public static IReadOnlyList<string> ModelIds => Models.Select(m => m.Id).ToList();
}
