using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TokenOptimizer.Providers.Compat;

namespace TokenOptimizer.Providers.Tests.Compat;

/// <summary>
/// UnifiedModelRouter is the bridge between Claude Code CLI's /model picker
/// and N upstream model APIs. The bug being fixed: previously only POST
/// /v1/messages was handled, so Claude Code's GET /v1/models discovery fell
/// back to the upstream's own list (often just one account-default model),
/// hiding every other ticked model from the in-CLI picker.
/// </summary>
public class UnifiedModelRouterTests : IDisposable
{
    private readonly List<FakeUpstream> _upstreams = new();

    public void Dispose()
    {
        foreach (var u in _upstreams) u.Dispose();
    }

    private FakeUpstream StartFakeUpstream(string? modelIdToReturn = null, string? bearerTokenToExpect = null)
    {
        var upstream = new FakeUpstream(modelIdToReturn, bearerTokenToExpect);
        _upstreams.Add(upstream);
        return upstream;
    }

    [Fact]
    public async Task GetV1Models_ReturnsEveryRoutedModel_SoClaudeCodeModelPickerShowsAllTickedModels()
    {
        var anthropic = StartFakeUpstream("claude-sonnet-5");
        var groq = StartFakeUpstream("groq/compound", bearerTokenToExpect: "groq-key");
        var opencode = StartFakeUpstream("mimo-v2.5", bearerTokenToExpect: "opencode-key");

        await using var router = new UnifiedModelRouter(new Dictionary<string, UnifiedModelRouter.ModelRoute>(StringComparer.Ordinal)
        {
            ["claude-sonnet-5"] = new(new Uri(anthropic.BaseUrl), RouteKind.AnthropicPassthrough),
            ["groq/compound"] = new(new Uri(groq.BaseUrl), RouteKind.OpenAiTranslate, () => "groq-key"),
            ["mimo-v2.5"] = new(new Uri(opencode.BaseUrl), RouteKind.AnthropicPassthrough, () => "opencode-key"),
        });
        await router.StartAsync();

        using var http = new HttpClient { BaseAddress = new Uri(router.BaseUrl) };
        var response = await http.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(body);
        var data = body!["data"] as JsonArray;
        Assert.NotNull(data);
        var ids = data!.Select(n => n?["id"]?.GetValue<string>()).ToHashSet();
        Assert.Contains("claude-sonnet-5", ids);
        Assert.Contains("groq/compound", ids);
        Assert.Contains("mimo-v2.5", ids);
    }

    [Fact]
    public async Task GetV1Models_IncludesAutoMetaModel_WhenAutoFallbackDelegateProvided()
    {
        var claude = StartFakeUpstream("claude-sonnet-5");
        await using var router = new UnifiedModelRouter(
            new Dictionary<string, UnifiedModelRouter.ModelRoute>(StringComparer.Ordinal)
            {
                ["claude-sonnet-5"] = new(new Uri(claude.BaseUrl), RouteKind.AnthropicPassthrough),
            },
            autoFallbackDelegate: () => Task.FromResult<UnifiedModelRouter.ModelRoute?>(new UnifiedModelRouter.ModelRoute(new Uri(claude.BaseUrl), RouteKind.AnthropicPassthrough)));
        await router.StartAsync();

        using var http = new HttpClient { BaseAddress = new Uri(router.BaseUrl) };
        var response = await http.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        var data = body!["data"] as JsonArray;
        var ids = data!.Select(n => n?["id"]?.GetValue<string>()).ToHashSet();
        Assert.Contains("claude-sonnet-5", ids);
        Assert.Contains("__auto__", ids);
    }

    [Fact]
    public async Task GetV1Models_OmitsAutoMetaModel_WhenNoAutoFallbackDelegateProvided()
    {
        var claude = StartFakeUpstream("claude-sonnet-5");
        await using var router = new UnifiedModelRouter(new Dictionary<string, UnifiedModelRouter.ModelRoute>(StringComparer.Ordinal)
        {
            ["claude-sonnet-5"] = new(new Uri(claude.BaseUrl), RouteKind.AnthropicPassthrough),
        });
        await router.StartAsync();

        using var http = new HttpClient { BaseAddress = new Uri(router.BaseUrl) };
        var response = await http.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        var data = body!["data"] as JsonArray;
        var ids = data!.Select(n => n?["id"]?.GetValue<string>()).ToHashSet();
        Assert.DoesNotContain("__auto__", ids);
    }

    [Fact]
    public async Task PostMessages_WithRoutedModel_PassesThroughToThatUpstream()
    {
        var claude = StartFakeUpstream("claude-sonnet-5");
        await using var router = new UnifiedModelRouter(new Dictionary<string, UnifiedModelRouter.ModelRoute>(StringComparer.Ordinal)
        {
            ["claude-sonnet-5"] = new(new Uri(claude.BaseUrl), RouteKind.AnthropicPassthrough),
        });
        await router.StartAsync();

        using var http = new HttpClient { BaseAddress = new Uri(router.BaseUrl) };
        var req = new JsonObject
        {
            ["model"] = "claude-sonnet-5",
            ["max_tokens"] = 16,
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "hi" } },
        };
        var response = await http.PostAsync("/v1/messages",
            new StringContent(req.ToJsonString(), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"model\":\"claude-sonnet-5\"", body);
        Assert.Equal(1, claude.TotalRequestCount);
    }

    [Fact]
    public async Task PostMessages_WithAutoModel_RoutesViaFallbackDelegate()
    {
        var claude = StartFakeUpstream("claude-sonnet-5");
        await using var router = new UnifiedModelRouter(
            new Dictionary<string, UnifiedModelRouter.ModelRoute>(StringComparer.Ordinal),
            autoFallbackDelegate: () => Task.FromResult<UnifiedModelRouter.ModelRoute?>(new UnifiedModelRouter.ModelRoute(new Uri(claude.BaseUrl), RouteKind.AnthropicPassthrough)));
        await router.StartAsync();

        using var http = new HttpClient { BaseAddress = new Uri(router.BaseUrl) };
        var req = new JsonObject
        {
            ["model"] = "__auto__",
            ["max_tokens"] = 16,
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "hi" } },
        };
        var response = await http.PostAsync("/v1/messages",
            new StringContent(req.ToJsonString(), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"model\":\"claude-sonnet-5\"", body);
        Assert.Equal(1, claude.TotalRequestCount);
    }

    [Fact]
    public async Task PostMessages_WithUnknownModel_Returns400()
    {
        var claude = StartFakeUpstream("claude-sonnet-5");
        await using var router = new UnifiedModelRouter(new Dictionary<string, UnifiedModelRouter.ModelRoute>(StringComparer.Ordinal)
        {
            ["claude-sonnet-5"] = new(new Uri(claude.BaseUrl), RouteKind.AnthropicPassthrough),
        });
        await router.StartAsync();

        using var http = new HttpClient { BaseAddress = new Uri(router.BaseUrl) };
        var req = new JsonObject
        {
            ["model"] = "totally-not-routed",
            ["max_tokens"] = 16,
            ["messages"] = new JsonArray(),
        };
        var response = await http.PostAsync("/v1/messages",
            new StringContent(req.ToJsonString(), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Tiny stub upstream that answers any path with a minimal Anthropic Messages response - enough to verify routing and bearer-token forwarding.</summary>
    private sealed class FakeUpstream : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        public string BaseUrl { get; }
        public int MessagesRequestCount { get; private set; }
        public int TotalRequestCount { get; private set; }
        public string? LastRequestPath { get; private set; }
        public string? LastRequestMethod { get; private set; }
        public string? LastAuthorizationHeader { get; private set; }
        public string? ExpectedBearerToken { get; }
        private readonly string _modelIdToReturn;

        public FakeUpstream(string? modelIdToReturn, string? bearerTokenToExpect)
        {
            _modelIdToReturn = modelIdToReturn ?? "fake-model";
            ExpectedBearerToken = bearerTokenToExpect;
            var port = GetFreePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add($"{BaseUrl}/");
            _listener.Start();
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }

                _ = HandleAsync(ctx);
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            try
            {
                TotalRequestCount++;
                LastRequestMethod = ctx.Request.HttpMethod;
                LastRequestPath = ctx.Request.Url?.AbsolutePath;
                var auth = ctx.Request.Headers["Authorization"];
                LastAuthorizationHeader = auth;
                if (ctx.Request.HttpMethod == "POST" && ctx.Request.Url?.AbsolutePath == "/v1/messages")
                {
                    MessagesRequestCount++;
                }
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                var body = new JsonObject
                {
                    ["id"] = "msg_test",
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["model"] = _modelIdToReturn,
                    ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "ok" } },
                    ["stop_reason"] = "end_turn",
                    ["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 1 },
                };
                var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            catch { /* test infra only */ }
            finally { try { ctx.Response.Close(); } catch { } }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }

        private static int GetFreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
    }
}
