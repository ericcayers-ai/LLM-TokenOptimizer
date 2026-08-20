using System.Text.Json;
using System.Text.RegularExpressions;

namespace TokenOptimizer.Providers.LlamaCpp;

/// <summary>One of the two supported model families - see LlamaCppModelCatalog.SupportedFamilies.</summary>
public sealed record LlamaCppModelFamily(string RepoId, string DisplayName, int NativeContextLength, string RecommendedQuant);

/// <summary>One GGUF quantization variant found in a family's HF repo.</summary>
public sealed record LlamaCppQuant(string Tag, long? SizeBytes);

/// <summary>
/// llama.cpp can only pull a named repo:quant (-hf user/repo:QUANT), it has
/// no browse/search of its own - this queries the Hugging Face API
/// (GET /api/models/{repo}) for the repo's file listing and parses
/// Unsloth's UD-&lt;QUANT&gt; naming convention so the UI can offer a real
/// quant picker instead of a hardcoded guess. Two hardcoded "supported/
/// recommended" families per the plan; HF search beyond them is a separate,
/// optional browse panel (plan §5d), not modeled here.
/// </summary>
public static class LlamaCppModelCatalog
{
    /// <summary>
    /// Both repo ids confirmed against their live Hugging Face model pages.
    /// Qwen3.8-27B supersedes the previous Qwen3-Coder-30B entry (Qwen3.8 is
    /// the newer, more capable generation per Qwen's own release notes) -
    /// unsloth/Qwen3.8-27B-GGUF, Unsloth's Dynamic v3.0 quant of Qwen3.8-27B.
    /// Context length is best-effort (not yet independently confirmed for
    /// this specific release) - wrong here just means a suboptimal
    /// --context-length default, not a launch failure.
    /// </summary>
    /// <summary>NativeContextLength is pinned to 131_072 (128K) for both - the always-128k-context requirement applies uniformly regardless of what a given model's absolute architectural max might be.</summary>
    public static readonly IReadOnlyList<LlamaCppModelFamily> SupportedFamilies = new[]
    {
        new LlamaCppModelFamily("unsloth/Qwen3.8-27B-GGUF", "Qwen3.8-27B (Unsloth)", 131_072, "UD-IQ4_XS"),
        new LlamaCppModelFamily("mudler/KAT-Coder-V2.5-Dev-APEX-GGUF", "KAT-Coder V2.5 Dev APEX (mudler)", 131_072, "I-QUALITY"),
    };

    /// <summary>
    /// Qwen3.8-27B-GGUF's repo lists many more quants than are worth offering -
    /// restricted here to the four the user actually wants surfaced: Q4_K_M and
    /// Q3_K_XL as size/quality alternatives either side of the default, IQ3_XXS
    /// as the smallest fallback, and IQ4_XS as the default (see RecommendedQuant
    /// above). Every file in this repo is Unsloth's Dynamic quant, so the tag
    /// QuantTagPattern actually parses out always carries a "UD-" prefix (e.g.
    /// "Qwen3.8-27B-UD-Q4_K_M.gguf" -&gt; "UD-Q4_K_M") - confirmed 2026-08-20 against
    /// the live HF API listing, not just the bare names as written above.
    /// Family-scoped (not applied to KAT-Coder) since KAT-Coder's APEX profile
    /// names don't share this convention.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> QuantAllowlistByRepo =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["unsloth/Qwen3.8-27B-GGUF"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "UD-Q4_K_M", "UD-IQ4_XS", "UD-Q3_K_XL", "UD-IQ3_XXS",
            },
        };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Regex QuantTagPattern = new(@"(?:^|[-.])((?:UD-)?(?:IQ|Q)\d[\w]*)\.gguf$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Falls back for repos that don't use the IQ/Q&lt;n&gt; naming above - mudler's APEX quants (e.g. KAT-Coder-V2.5-Dev-APEX-I-Balanced.gguf) name the precision profile directly instead.</summary>
    private static readonly Regex ApexProfilePattern = new(@"APEX-([A-Za-z][\w-]*)\.gguf$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<IReadOnlyList<LlamaCppQuant>> ListQuantsAsync(string repoId, CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync($"https://huggingface.co/api/models/{repoId}", ct);
        if (!resp.IsSuccessStatusCode) return Array.Empty<LlamaCppQuant>();

        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("siblings", out var siblings)) return Array.Empty<LlamaCppQuant>();

            var quants = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
            foreach (var sibling in siblings.EnumerateArray())
            {
                var filename = sibling.TryGetProperty("rfilename", out var f) ? f.GetString() : null;
                if (filename is null || !filename.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) continue;

                var match = QuantTagPattern.Match(filename);
                if (!match.Success) match = ApexProfilePattern.Match(filename);
                if (!match.Success) continue;

                var tag = match.Groups[1].Value.ToUpperInvariant();
                var size = sibling.TryGetProperty("size", out var s) && s.TryGetInt64(out var b) ? b : (long?)null;
                quants[tag] = size;
            }

            var filtered = QuantAllowlistByRepo.TryGetValue(repoId, out var allowed)
                ? quants.Where(kv => allowed.Contains(kv.Key))
                : quants.AsEnumerable();

            return filtered.Select(kv => new LlamaCppQuant(kv.Key, kv.Value))
                .OrderBy(q => q.Tag, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<LlamaCppQuant>();
        }
    }
}
