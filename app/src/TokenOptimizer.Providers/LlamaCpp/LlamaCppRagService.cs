using System.Text.Json;
using System.Text.Json.Nodes;

namespace TokenOptimizer.Providers.LlamaCpp;

public sealed record RagChunk(string SourcePath, string Text, float[] Embedding);
public sealed record RagRetrievalResult(string SourcePath, string Text, double Score);

/// <summary>
/// "Chat with documents" (plan §5d) - neither `unsloth start` nor
/// llama-server underneath it does ingestion, chunking, or citation on
/// their own; this builds it in TokenOptimizer using an OpenAI-compatible
/// /v1/embeddings endpoint as the sole primitive. LlamaCppAdapter no longer
/// owns a known server URL directly (unsloth manages that internally), so
/// the caller must supply one - e.g. Unsloth Studio's own local endpoint,
/// if/when its address is exposed. Mirrors LM Studio's dual-mode behavior
/// loosely: callers decide whether a short doc is small enough to inject
/// whole (skip this service) or should go through chunk+embed+retrieve.
/// In-memory only - no vector DB dependency for what's a handful of
/// documents in a single coding session.
/// </summary>
public sealed class LlamaCppRagService
{
    private readonly Uri _embeddingsBaseUrl;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly List<RagChunk> _chunks = new();

    public LlamaCppRagService(Uri serverBaseUrl)
    {
        _embeddingsBaseUrl = serverBaseUrl;
    }

    public int ChunkCount => _chunks.Count;

    /// <summary>Chunks by a fixed token-ish character window (no tokenizer dependency here) with overlap, embeds each chunk, and adds it to the in-memory index.</summary>
    public async Task IngestAsync(string sourcePath, string text, int chunkChars = 2000, int overlapChars = 200, CancellationToken ct = default)
    {
        for (var start = 0; start < text.Length; start += chunkChars - overlapChars)
        {
            var length = Math.Min(chunkChars, text.Length - start);
            var chunkText = text.Substring(start, length);
            var embedding = await EmbedAsync(chunkText, ct);
            if (embedding is not null) _chunks.Add(new RagChunk(sourcePath, chunkText, embedding));
            if (start + chunkChars >= text.Length) break;
        }
    }

    /// <summary>Top-k retrieval by cosine similarity, filtered to a minimum affinity threshold - the two knobs from the plan's RAG plugin panel screenshot.</summary>
    public async Task<IReadOnlyList<RagRetrievalResult>> RetrieveAsync(string query, int retrievalLimit = 3, double affinityThreshold = 0.5, CancellationToken ct = default)
    {
        var queryEmbedding = await EmbedAsync(query, ct);
        if (queryEmbedding is null) return Array.Empty<RagRetrievalResult>();

        return _chunks
            .Select(c => new RagRetrievalResult(c.SourcePath, c.Text, CosineSimilarity(queryEmbedding, c.Embedding)))
            .Where(r => r.Score >= affinityThreshold)
            .OrderByDescending(r => r.Score)
            .Take(retrievalLimit)
            .ToList();
    }

    private async Task<float[]?> EmbedAsync(string text, CancellationToken ct)
    {
        var body = new JsonObject { ["input"] = text };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_embeddingsBaseUrl}/embeddings")
        {
            Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var embeddingArray = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
            return embeddingArray.EnumerateArray().Select(e => e.GetSingle()).ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, magA = 0, magB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        return magA == 0 || magB == 0 ? 0 : dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }
}
