using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace TokenOptimizer.Providers.Compat;

/// <summary>
/// Rolling context window + auto-compact for Unsloth-served local models -
/// a feature genuinely absent from Unsloth's own server/CLI (confirmed
/// against its live docs; see LlamaCppDefaultPresets' summary of that
/// research). Unsloth's own backend doesn't have this; nothing stops
/// TokenOptimizer from adding it in front, the same way AnthropicCompatProxy
/// already sits in front of Groq to bridge a schema mismatch.
///
/// No schema translation is needed here (both sides already speak the
/// Anthropic Messages API - see LlamaCppAdapter.LaunchWithRollingContextAsync
/// for how the real upstream endpoint is obtained), only request-body
/// mutation before forwarding, plus verbatim passthrough of everything else
/// (headers, streaming bytes, non-streaming bytes) either direction.
///
/// Token counting is a character-based heuristic (~4 chars/token), not an
/// exact tokenizer - deliberately conservative (trims a little earlier than
/// strictly necessary) rather than exact, since undershooting costs one
/// extra trimmed turn while overshooting fails the whole request.
/// </summary>
public sealed class RollingContextProxy : IAsyncDisposable
{
    private const int CharsPerTokenEstimate = 4;
    private const int ReservedForOutputTokens = 4096;

    private readonly Uri _upstreamBaseUrl;
    private readonly Func<string?> _upstreamAuthToken;
    private readonly int _contextLength;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public RollingContextProxy(Uri upstreamBaseUrl, Func<string?> upstreamAuthToken, int contextLength)
    {
        _upstreamBaseUrl = upstreamBaseUrl;
        _upstreamAuthToken = upstreamAuthToken;
        _contextLength = contextLength;
    }

    public int Port { get; private set; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public Task StartAsync()
    {
        Port = GetFreeLoopbackPort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();

        _cts = new CancellationTokenSource();
        _acceptLoop = AcceptLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { /* already stopping */ }
        try { _listener.Stop(); } catch { /* already stopped */ }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; } catch { /* accept loop faults are expected on cancel */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _listener.Close();
        _http.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }

            _ = HandleAsync(ctx, ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            string? body = null;
            if (ctx.Request.HttpMethod == "POST")
            {
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                body = await reader.ReadToEndAsync(ct);
            }

            if (path == "/v1/messages" && !string.IsNullOrEmpty(body))
            {
                var request = JsonNode.Parse(body)!.AsObject();
                ApplyRollingWindow(request);
                body = request.ToJsonString();
            }

            // Uri.ToString() on a bare-authority URI (e.g. "http://127.0.0.1:8888") canonicalizes
            // to include a trailing "/" - naively interpolating it before a leading-"/" path
            // produces "http://127.0.0.1:8888//v1/messages", which the real unsloth server 404s
            // on (confirmed against a live server, not a guess). GetLeftPart(Authority) strips
            // that trailing slash so exactly one separates authority from path.
            var upstreamAuthority = _upstreamBaseUrl.GetLeftPart(UriPartial.Authority);
            using var upstreamReq = new HttpRequestMessage(new HttpMethod(ctx.Request.HttpMethod), $"{upstreamAuthority}{path}{ctx.Request.Url?.Query}");
            if (!string.IsNullOrEmpty(body))
                upstreamReq.Content = new StringContent(body, Encoding.UTF8, "application/json");

            var token = _upstreamAuthToken();
            if (!string.IsNullOrEmpty(token))
            {
                // Confirmed against a live unsloth server: it's ANTHROPIC_AUTH_TOKEN it prints
                // (not ANTHROPIC_API_KEY), which per the Anthropic SDK's own convention means
                // "Authorization: Bearer <token>", not "x-api-key" - the latter 401s
                // ("Not authenticated") against the real server even though the token is valid.
                upstreamReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
                upstreamReq.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            }

            using var upstreamResp = await _http.SendAsync(upstreamReq, HttpCompletionOption.ResponseHeadersRead, ct);
            ctx.Response.StatusCode = (int)upstreamResp.StatusCode;
            ctx.Response.ContentType = upstreamResp.Content.Headers.ContentType?.ToString() ?? "application/json";

            var upstreamStream = await upstreamResp.Content.ReadAsStreamAsync(ct);
            await using (upstreamStream.ConfigureAwait(false))
            {
                await upstreamStream.CopyToAsync(ctx.Response.OutputStream, ct);
            }
        }
        catch (Exception ex)
        {
            try
            {
                ctx.Response.StatusCode = 502;
                var payload = Encoding.UTF8.GetBytes(
                    $"{{\"type\":\"error\",\"error\":{{\"type\":\"api_error\",\"message\":{JsonValue.Create(ex.Message)!.ToJsonString()}}}}}");
                await ctx.Response.OutputStream.WriteAsync(payload, ct);
            }
            catch { /* client already gone */ }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { /* already closed */ }
        }
    }

    /// <summary>
    /// The actual rolling-context-window/auto-compact logic: estimates the
    /// request's total token count, and if it's over budget, drops the
    /// oldest messages (the system prompt is never touched) and replaces
    /// them with one compaction marker, keeping as much of the recent
    /// conversation as fits. Mutates `request` in place.
    /// </summary>
    internal void ApplyRollingWindow(JsonObject request)
    {
        if (request["messages"] is not JsonArray messages || messages.Count == 0) return;

        var systemTokens = EstimateTokens(request["system"]);
        var budget = _contextLength - ReservedForOutputTokens - systemTokens;
        if (budget <= 0) return; // system prompt alone already exceeds the window - nothing safe to trim

        var kept = new List<JsonNode?>();
        var used = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var tokens = EstimateTokens(messages[i]);
            if (used + tokens > budget && kept.Count > 0) break; // always keep at least the single most recent message
            used += tokens;
            kept.Insert(0, messages[i]?.DeepClone());
        }

        if (kept.Count == messages.Count) return; // everything fit - no trimming needed

        // Best-effort, not a full guarantee: if trimming happened to land right after a tool_use
        // whose tool_result is the first kept message, pull the tool_use turn back in too, since
        // Claude's Messages API expects every tool_result to be preceded by its matching tool_use.
        var firstKeptOriginalIndex = messages.Count - kept.Count;
        if (firstKeptOriginalIndex > 0 && ContainsToolResult(kept[0]))
        {
            kept.Insert(0, messages[firstKeptOriginalIndex - 1]?.DeepClone());
        }

        var droppedCount = messages.Count - kept.Count;
        var marker = new JsonObject
        {
            ["role"] = "user",
            ["content"] = $"[TokenOptimizer rolling context window: {droppedCount} earlier turn(s) trimmed to stay within this model's context. Continue naturally from the conversation below.]",
        };

        var trimmed = new JsonArray { marker };
        foreach (var m in kept) trimmed.Add(m);
        request["messages"] = trimmed;
    }

    private static bool ContainsToolResult(JsonNode? message) =>
        message?["content"] is JsonArray blocks && blocks.Any(b => b?["type"]?.GetValue<string>() == "tool_result");

    private static int EstimateTokens(JsonNode? content)
    {
        if (content is null) return 0;
        var text = content is JsonValue v && v.TryGetValue(out string? s) ? s : content.ToJsonString();
        return (text?.Length ?? 0) / CharsPerTokenEstimate;
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
