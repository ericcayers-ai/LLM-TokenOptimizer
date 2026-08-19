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
    /// Repo ids are best-effort as of this session's research and should be
    /// re-verified against Hugging Face before relying on them - "Qwen3.8
    /// 27B" doesn't match a known exact Unsloth release name, this points at
    /// the closest confirmed match (Unsloth's Qwen3-Coder GGUF line).
    /// </summary>
    public static readonly IReadOnlyList<LlamaCppModelFamily> SupportedFamilies = new[]
    {
        new LlamaCppModelFamily("unsloth/Qwen3-Coder-30B-A3B-Instruct-GGUF", "Qwen3 Coder (Unsloth)", 262_144, "UD-Q4_K_XL"),
        new LlamaCppModelFamily("mudler/KAT-Coder-V2.5-Dev-APEX-GGUF", "KAT-Coder V2.5 Dev APEX (mudler)", 131_072, "UD-Q4_K_XL"),
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Regex QuantTagPattern = new(@"(?:^|[-.])((?:UD-)?(?:IQ|Q)\d[\w]*)\.gguf$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
                if (!match.Success) continue;

                var tag = match.Groups[1].Value.ToUpperInvariant();
                var size = sibling.TryGetProperty("size", out var s) && s.TryGetInt64(out var b) ? b : (long?)null;
                quants[tag] = size;
            }

            return quants.Select(kv => new LlamaCppQuant(kv.Key, kv.Value))
                .OrderBy(q => q.Tag, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<LlamaCppQuant>();
        }
    }
}
