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
///
/// GET /v1/models returns every routed model plus, when an auto-fallback
/// delegate is supplied, a "__auto__" meta-model - that's what lets Claude
/// Code's own /model picker list every ticked model (labeled "From gateway").
/// Three things the caller (whoever launches the CLI pointed at this router)
/// MUST also do or the picker still won't show anything beyond the account
/// default:
/// (1) set a non-empty ANTHROPIC_AUTH_TOKEN alongside ANTHROPIC_BASE_URL -
/// Claude Code's internal discovery gate (minified name Vna()) requires both,
/// plus getAPIProvider() resolving to "firstParty". Do NOT set
/// CLAUDE_CODE_USE_GATEWAY=1 - that switches getAPIProvider() to the
/// unrelated real-enterprise-SSO "gateway" mode, which makes Vna() bail
/// (it explicitly requires "firstParty") so the discovery request never
/// fires at all. Confirmed live via claude.exe's own debug log
/// (--debug-file/-d): unset, it logs "[gatewayDiscovery] cached N models";
/// set, discovery is skipped and only an unrelated internal bucket the
/// picker never reads gets populated - this was the actual root cause of an
/// earlier "picker still shows only Default" regression, not anything
/// server-side;
/// (2) also set CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY=1 - the endpoint
/// is separately opt-in; and
/// (3) never pass a non-Claude-native id via --model/ANTHROPIC_MODEL at
/// launch - Claude Code validates that value against the account's real
/// entitlements before any request reaches this proxy, and rejects it with
/// "restricted by your organization's settings", falling back to the
/// account default. Only real Claude ids (or omitting --model entirely)
/// are safe there; the rest of the ticked set is still reachable live via
/// /model once discovery is on.
/// </summary>
public sealed class UnifiedModelRouter : IAsyncDisposable
{
    public sealed record ModelRoute(Uri UpstreamBaseUrl, RouteKind Kind, Func<string?>? AuthToken = null);

    /// <summary>Special model id - selecting it in Claude Code's /model picker routes each request through whatever the auto-fallback delegate returns (typically the next live provider in the Claude Code -> Antigravity -> OpenCode -> local chain).</summary>
    public const string AutoModelId = "__auto__";

    /// <summary>
    /// Claude Code's own gateway-model-discovery feature (opt-in via
    /// CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY=1 - see
    /// ClaudeLaunchEnvironmentBuilder) silently drops any GET /v1/models
    /// entry whose id doesn't contain "claude" or "anthropic" (case-
    /// insensitive) anywhere - confirmed against Anthropic's own
    /// model-config docs. Real ids like "groq/compound" or "big-pickle"
    /// would be filtered out before they ever reach the picker, discovery
    /// on or not. Every non-Claude-native id gets this prefix purely for
    /// advertising/picker-display; Unadvertise reverses it on the way back
    /// in so route lookups still use the real id.
    /// </summary>
    private const string GatewayIdPrefix = "claude-gateway-";

    private static bool LooksClaudeNative(string id) =>
        id.Contains("claude", StringComparison.OrdinalIgnoreCase) || id.Contains("anthropic", StringComparison.OrdinalIgnoreCase);

    private static string Advertise(string realId) => LooksClaudeNative(realId) ? realId : $"{GatewayIdPrefix}{realId}";

    private static string Unadvertise(string shownId) =>
        shownId.StartsWith(GatewayIdPrefix, StringComparison.Ordinal) ? shownId[GatewayIdPrefix.Length..] : shownId;

    private readonly IReadOnlyDictionary<string, ModelRoute> _routes;
    private readonly Func<Task<ModelRoute?>>? _autoFallbackDelegate;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public UnifiedModelRouter(IReadOnlyDictionary<string, ModelRoute> routes, Func<Task<ModelRoute?>>? autoFallbackDelegate = null)
    {
        _routes = routes;
        _autoFallbackDelegate = autoFallbackDelegate;
    }

    /// <summary>Every model id the router exposes via GET /v1/models - static routes plus "__auto__" when an auto-fallback delegate is set.</summary>
    public IReadOnlyList<string> AdvertisedModelIds
    {
        get
        {
            var ids = _routes.Keys.Select(Advertise).ToList();
            if (_autoFallbackDelegate is not null) ids.Add(Advertise(AutoModelId));
            return ids;
        }
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
            var path = ctx.Request.Url?.AbsolutePath ?? string.Empty;
            var method = ctx.Request.HttpMethod;

            // Claude Code CLI's /model picker populates from GET /v1/models. Without this handler the picker
            // silently falls back to whatever the upstream /v1/models endpoint returns (often just the user's
            // account-default model), hiding every other ticked model. Returning every routed model + the
            // "__auto__" meta-model here is what makes the in-CLI picker actually list all ticked options.
            if (method == "GET" && path == "/v1/models")
            {
                await RespondWithModelsAsync(ctx, ct);
                return;
            }

            // Some Claude Code CLI versions probe additional endpoints; respond 204 to silence them cleanly
            // instead of letting them bubble up as 404 in the log and confuse users.
            if (method == "GET" && (path == "/" || path == "/health" || path == "/v1/messages"))
            {
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }

            if (path != "/v1/messages" || method != "POST")
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);
            var anthropicRequest = JsonNode.Parse(body)!.AsObject();
            var model = Unadvertise(anthropicRequest["model"]?.GetValue<string>() ?? "");

            ModelRoute route;
            if (_routes.TryGetValue(model, out var directRoute))
            {
                route = directRoute;
            }
            else if (model == AutoModelId && _autoFallbackDelegate is not null)
            {
                var fallback = await _autoFallbackDelegate();
                if (fallback is null)
                {
                    ctx.Response.StatusCode = 503;
                    var errorBody = new JsonObject
                    {
                        ["type"] = "error",
                        ["error"] = new JsonObject { ["type"] = "api_error", ["message"] = "No provider in the auto fallback chain is currently available." },
                    };
                    await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(errorBody.ToJsonString()), ct);
                    return;
                }
                route = fallback;
            }
            else
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

    private async Task RespondWithModelsAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var data = new JsonArray();
        foreach (var realId in _routes.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            data.Add(new JsonObject
            {
                ["id"] = Advertise(realId),
                ["type"] = "model",
                ["display_name"] = realId,
                ["created_at"] = "2024-01-01T00:00:00Z",
            });
        }
        if (_autoFallbackDelegate is not null)
        {
            data.Add(new JsonObject
            {
                ["id"] = Advertise(AutoModelId),
                ["type"] = "model",
                ["display_name"] = "Auto (fallback chain) - picks the next available provider per request",
                ["created_at"] = "2024-01-01T00:00:00Z",
            });
        }
        var body = new JsonObject
        {
            ["data"] = data,
            ["first_id"] = data.FirstOrDefault()?["id"]?.GetValue<string>(),
            ["last_id"] = data.LastOrDefault()?["id"]?.GetValue<string>(),
            ["has_more"] = false,
        };
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), ct);
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
