using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace TokenOptimizer.Providers.Compat;

/// <summary>How a route's upstream needs to be talked to.</summary>
public enum RouteKind
{
    /// <summary>Speaks the Anthropic Messages API already (Claude direct, OpenCode Go) - request/response bytes pass through untranslated.</summary>
    AnthropicPassthrough,

    /// <summary>Speaks OpenAI chat-completions (Groq, and any other OpenAI-shaped backend) - translated via AnthropicCompatProxy's existing schema bridge.</summary>
    OpenAiTranslate,
}

/// <summary>
/// One local endpoint, many models. AnthropicCompatProxy bridges exactly one
/// upstream per instance (what GroqAdapter/LlamaCpp each spin up for their
/// own single-provider session). This router does the same job but keyed
/// per-request by the "model" field in the incoming Anthropic-shaped
/// request, so a single Claude Code CLI window - one ANTHROPIC_BASE_URL, one
/// process - can have its own /model picker list models from several
/// different providers at once, each dispatched to its own upstream.
///
/// Claude-native models use AnthropicPassthrough with a null AuthToken -
/// Claude Code CLI already attaches whatever auth it normally uses (API key
/// or subscription session) to the outgoing request headers before it ever
/// reaches this router, so passthrough forwards those headers verbatim
/// rather than injecting our own; the router never needs to know how the
/// CLI is authenticated for its own models.
/// </summary>
public sealed class UnifiedModelRouter : IAsyncDisposable
{
    public sealed record ModelRoute(Uri UpstreamBaseUrl, RouteKind Kind, Func<string?>? AuthToken = null);

    private readonly IReadOnlyDictionary<string, ModelRoute> _routes;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public UnifiedModelRouter(IReadOnlyDictionary<string, ModelRoute> routes)
    {
        _routes = routes;
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
            if (ctx.Request.Url?.AbsolutePath != "/v1/messages" || ctx.Request.HttpMethod != "POST")
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);
            var anthropicRequest = JsonNode.Parse(body)!.AsObject();
            var model = anthropicRequest["model"]?.GetValue<string>() ?? "";

            if (!_routes.TryGetValue(model, out var route))
            {
                ctx.Response.StatusCode = 400;
                var errorBody = new JsonObject
                {
                    ["type"] = "error",
                    ["error"] = new JsonObject { ["type"] = "invalid_request_error", ["message"] = $"Model '{model}' is not one of the ticked models for this session." },
                };
                await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(errorBody.ToJsonString()), ct);
                return;
            }

            if (route.Kind == RouteKind.AnthropicPassthrough)
            {
                await PassthroughAsync(ctx, route, body, ct);
            }
            else
            {
                var stream = anthropicRequest.TryGetPropertyValue("stream", out var s) && s?.GetValue<bool>() == true;
                var openAiRequest = AnthropicCompatProxy.AnthropicToOpenAiRequest(anthropicRequest);

                using var upstreamReq = new HttpRequestMessage(HttpMethod.Post, $"{route.UpstreamBaseUrl}/chat/completions")
                {
                    Content = new StringContent(openAiRequest.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                var token = route.AuthToken?.Invoke();
                if (!string.IsNullOrEmpty(token))
                    upstreamReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var upstreamResp = await _http.SendAsync(upstreamReq, HttpCompletionOption.ResponseHeadersRead, ct);

                if (stream)
                    await AnthropicCompatProxy.RelayStreamingAsync(ctx, upstreamResp, ct);
                else
                    await AnthropicCompatProxy.RelayNonStreamingAsync(ctx, upstreamResp, ct);
            }
        }
        catch (Exception ex)
        {
            try
            {
                ctx.Response.StatusCode = 502;
                var errorBody = new JsonObject
                {
                    ["type"] = "error",
                    ["error"] = new JsonObject { ["type"] = "api_error", ["message"] = ex.Message },
                };
                await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(errorBody.ToJsonString()), ct);
            }
            catch { /* client already gone */ }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { /* already closed */ }
        }
    }

    /// <summary>
    /// Untranslated forward: the upstream already speaks the same schema the
    /// CLI sent. AuthToken null (Claude-native models) forwards the client's
    /// original auth headers unchanged - the CLI's own login/API-key/session
    /// auth travels through as-is. A non-null AuthToken (OpenCode Go) instead
    /// injects that provider's own stored key, matching what OpenCodeAdapter
    /// does today via ANTHROPIC_AUTH_TOKEN for a direct (non-routed) launch.
    /// </summary>
    private async Task PassthroughAsync(HttpListenerContext ctx, ModelRoute route, string body, CancellationToken ct)
    {
        using var upstreamReq = new HttpRequestMessage(HttpMethod.Post, $"{route.UpstreamBaseUrl}/v1/messages")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        var injectedToken = route.AuthToken?.Invoke();
        foreach (var header in ctx.Request.Headers.AllKeys)
        {
            if (header is null) continue;
            if (header.Equals("Host", StringComparison.OrdinalIgnoreCase) || header.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || header.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            if (injectedToken is not null && (header.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || header.Equals("x-api-key", StringComparison.OrdinalIgnoreCase))) continue;
            upstreamReq.Headers.TryAddWithoutValidation(header, ctx.Request.Headers[header]);
        }
        if (injectedToken is not null)
        {
            upstreamReq.Headers.Remove("x-api-key");
            upstreamReq.Headers.TryAddWithoutValidation("x-api-key", injectedToken);
        }

        using var upstreamResp = await _http.SendAsync(upstreamReq, HttpCompletionOption.ResponseHeadersRead, ct);
        ctx.Response.StatusCode = (int)upstreamResp.StatusCode;
        if (upstreamResp.Content.Headers.ContentType is { } contentType) ctx.Response.ContentType = contentType.ToString();

        await using var upstreamStream = await upstreamResp.Content.ReadAsStreamAsync(ct);
        await upstreamStream.CopyToAsync(ctx.Response.OutputStream, ct);
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
