using System.Net.Http.Headers;
using System.Text.Json;

namespace TokenOptimizer.Providers.Fallback;

public sealed record GroqModel(string Id, long? ContextWindow, string? OwnedBy);

/// <summary>
/// Groq's model catalog changes often (new/deprecated model ids) and its
/// namespace (openai/gpt-oss-120b, groq/compound, ...) shares
/// nothing with Anthropic's - free-text model entry means users have to
/// know/guess a valid id. Queries Groq's own /models endpoint (OpenAI-
/// compatible: GET {base}/models, bearer-auth) so the UI can offer a
/// validated dropdown instead, mirroring LmStudioAdapter.ListInstalledModelsAsync's
/// role for the local backend.
/// </summary>
public static class GroqModelCatalog
{
    private const string ModelsUrl = "https://api.groq.com/openai/v1/models";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<IReadOnlyList<GroqModel>> ListAsync(string apiKey, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ModelsUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return Array.Empty<GroqModel>();

        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var models = new List<GroqModel>();
            foreach (var entry in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = entry.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                if (id is null) continue;
                var ctxWindow = entry.TryGetProperty("context_window", out var cw) && cw.TryGetInt64(out var c) ? c : (long?)null;
                var ownedBy = entry.TryGetProperty("owned_by", out var ob) ? ob.GetString() : null;
                models.Add(new GroqModel(id, ctxWindow, ownedBy));
            }
            return models.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<GroqModel>();
        }
    }

    /// <summary>Groq API keys are always issued with this prefix - catches a pasted-wrong-value mistake before it burns a network round trip into a silent 401.</summary>
    public static bool LooksLikeValidKey(string? apiKey) =>
        !string.IsNullOrWhiteSpace(apiKey) && apiKey.StartsWith("gsk_", StringComparison.Ordinal);
}
