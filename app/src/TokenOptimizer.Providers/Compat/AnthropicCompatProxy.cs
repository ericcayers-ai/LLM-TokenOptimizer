using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TokenOptimizer.Providers.Compat;

/// <summary>
/// Claude Code CLI only speaks the Anthropic Messages API on
/// ANTHROPIC_BASE_URL (POST /v1/messages - system/messages/content-blocks,
/// SSE event types like content_block_delta). Groq and llama-server both
/// expose the OpenAI chat-completions schema instead (POST
/// /chat/completions - messages with plain/array content, SSE "delta"
/// chunks). Pointing ANTHROPIC_BASE_URL directly at either produces
/// well-formed-looking but schema-mismatched requests that fail silently
/// or error deep in the CLI. This proxy sits in between: it terminates
/// Anthropic-shaped requests locally and re-emits OpenAI-shaped ones
/// upstream, translating the response (streaming or not, including tool
/// calls) back into Anthropic's event shape.
///
/// One instance per launched session, bound to an OS-assigned loopback
/// port so multiple sessions (e.g. Groq and llama.cpp at once) don't
/// collide. Caller starts it before launching the CLI process and stops
/// it once that process exits.
/// </summary>
public sealed class AnthropicCompatProxy : IAsyncDisposable
{
    private readonly Uri _upstreamBaseUrl;
    private readonly Func<string?> _upstreamBearerToken;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    /// <param name="upstreamBaseUrl">OpenAI-compatible base, e.g. https://api.groq.com/openai/v1 or http://127.0.0.1:8085/v1</param>
    /// <param name="upstreamBearerToken">Resolves the Authorization: Bearer token per request; return null for backends (llama.cpp) that need none.</param>
    public AnthropicCompatProxy(Uri upstreamBaseUrl, Func<string?> upstreamBearerToken)
    {
        _upstreamBaseUrl = upstreamBaseUrl;
        _upstreamBearerToken = upstreamBearerToken;
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

            var stream = anthropicRequest.TryGetPropertyValue("stream", out var s) && s?.GetValue<bool>() == true;
            var openAiRequest = AnthropicToOpenAiRequest(anthropicRequest);

            using var upstreamReq = new HttpRequestMessage(HttpMethod.Post, $"{_upstreamBaseUrl}/chat/completions")
            {
                Content = new StringContent(openAiRequest.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            var token = _upstreamBearerToken();
            if (!string.IsNullOrEmpty(token))
                upstreamReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var upstreamResp = await _http.SendAsync(
                upstreamReq, HttpCompletionOption.ResponseHeadersRead, ct);

            if (stream)
                await RelayStreamingAsync(ctx, upstreamResp, ct);
            else
                await RelayNonStreamingAsync(ctx, upstreamResp, ct);
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
                var payload = Encoding.UTF8.GetBytes(errorBody.ToJsonString());
                await ctx.Response.OutputStream.WriteAsync(payload, ct);
            }
            catch { /* client already gone */ }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { /* already closed */ }
        }
    }

    // ---- Anthropic request -> OpenAI request ----

    // internal (not private): UnifiedModelRouter reuses this translation logic for its own OpenAI-shaped routes rather than duplicating it.
    internal static JsonObject AnthropicToOpenAiRequest(JsonObject anthropic)
    {
        var openAi = new JsonObject
        {
            ["model"] = anthropic["model"]?.DeepClone() ?? "unknown",
            ["stream"] = anthropic.TryGetPropertyValue("stream", out var s) ? s?.DeepClone() : false,
        };
        if (anthropic.TryGetPropertyValue("max_tokens", out var maxTokens)) openAi["max_tokens"] = maxTokens?.DeepClone();
        if (anthropic.TryGetPropertyValue("temperature", out var temp)) openAi["temperature"] = temp?.DeepClone();
        if (anthropic.TryGetPropertyValue("top_p", out var topP)) openAi["top_p"] = topP?.DeepClone();
        if (anthropic.TryGetPropertyValue("stop_sequences", out var stop)) openAi["stop"] = stop?.DeepClone();

        var messages = new JsonArray();
        if (anthropic.TryGetPropertyValue("system", out var system) && system is not null)
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = FlattenContent(system) });
        }
        if (anthropic.TryGetPropertyValue("messages", out var anthropicMessages) && anthropicMessages is JsonArray arr)
        {
            foreach (var msg in arr)
            {
                if (msg is not JsonObject mo) continue;
                var role = mo["role"]?.GetValue<string>() ?? "user";
                var content = mo["content"];

                // A content array holding tool_result blocks maps to OpenAI's
                // separate "tool" role messages, one per result - the two
                // schemas structure tool responses differently in-line vs
                // as sibling messages.
                if (content is JsonArray blocks && blocks.Any(b => b?["type"]?.GetValue<string>() == "tool_result"))
                {
                    foreach (var block in blocks)
                    {
                        if (block?["type"]?.GetValue<string>() != "tool_result") continue;
                        messages.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = block["tool_use_id"]?.DeepClone(),
                            ["content"] = FlattenContent(block["content"]),
                        });
                    }
                    continue;
                }

                // An assistant content array holding tool_use blocks maps to
                // OpenAI's assistant "tool_calls" array instead of inline content.
                if (role == "assistant" && content is JsonArray abs && abs.Any(b => b?["type"]?.GetValue<string>() == "tool_use"))
                {
                    var toolCalls = new JsonArray();
                    var text = new StringBuilder();
                    foreach (var block in abs)
                    {
                        var type = block?["type"]?.GetValue<string>();
                        if (type == "tool_use")
                        {
                            toolCalls.Add(new JsonObject
                            {
                                ["id"] = block!["id"]?.DeepClone(),
                                ["type"] = "function",
                                ["function"] = new JsonObject
                                {
                                    ["name"] = block["name"]?.DeepClone(),
                                    ["arguments"] = (block["input"]?.ToJsonString()) ?? "{}",
                                },
                            });
                        }
                        else if (type == "text")
                        {
                            text.Append(block!["text"]?.GetValue<string>());
                        }
                    }
                    var assistantMsg = new JsonObject { ["role"] = "assistant" };
                    assistantMsg["content"] = text.Length > 0 ? text.ToString() : null;
                    assistantMsg["tool_calls"] = toolCalls;
                    messages.Add(assistantMsg);
                    continue;
                }

                messages.Add(new JsonObject { ["role"] = role, ["content"] = FlattenContent(content) });
            }
        }
        openAi["messages"] = messages;

        if (anthropic.TryGetPropertyValue("tools", out var tools) && tools is JsonArray toolsArr)
        {
            var openAiTools = new JsonArray();
            foreach (var t in toolsArr)
            {
                if (t is not JsonObject to) continue;
                openAiTools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = to["name"]?.DeepClone(),
                        ["description"] = to["description"]?.DeepClone(),
                        ["parameters"] = to["input_schema"]?.DeepClone() ?? new JsonObject(),
                    },
                });
            }
            openAi["tools"] = openAiTools;
        }

        return openAi;
    }

    /// <summary>Anthropic content is either a plain string or an array of typed blocks (text/tool_result); OpenAI's simple-message form wants a plain string.</summary>
    private static JsonNode? FlattenContent(JsonNode? content)
    {
        if (content is null) return null;
        if (content is JsonValue) return content.DeepClone();
        if (content is JsonArray blocks)
        {
            var sb = new StringBuilder();
            foreach (var block in blocks)
            {
                if (block is JsonValue) { sb.Append(block.GetValue<string>()); continue; }
                var type = block?["type"]?.GetValue<string>();
                if (type == "text") sb.Append(block!["text"]?.GetValue<string>());
                else if (block?["content"] is not null) sb.Append(FlattenContent(block["content"])?.GetValue<string>());
            }
            return sb.ToString();
        }
        return content.DeepClone();
    }

    // ---- OpenAI response -> Anthropic response ----

    internal static async Task RelayNonStreamingAsync(HttpListenerContext ctx, HttpResponseMessage upstream, CancellationToken ct)
    {
        var body = await upstream.Content.ReadAsStringAsync(ct);
        if (!upstream.IsSuccessStatusCode)
        {
            ctx.Response.StatusCode = (int)upstream.StatusCode;
            var errorBody = new JsonObject
            {
                ["type"] = "error",
                ["error"] = new JsonObject { ["type"] = "api_error", ["message"] = body },
            };
            var errPayload = Encoding.UTF8.GetBytes(errorBody.ToJsonString());
            await ctx.Response.OutputStream.WriteAsync(errPayload, ct);
            return;
        }

        var openAi = JsonNode.Parse(body)!.AsObject();
        var choice = openAi["choices"]?[0]?.AsObject();
        var message = choice?["message"]?.AsObject();

        var contentBlocks = new JsonArray();
        var textContent = message?["content"]?.GetValue<string?>();
        if (!string.IsNullOrEmpty(textContent))
            contentBlocks.Add(new JsonObject { ["type"] = "text", ["text"] = textContent });

        if (message?["tool_calls"] is JsonArray toolCalls)
        {
            foreach (var tc in toolCalls)
            {
                var fn = tc?["function"];
                var argsRaw = fn?["arguments"]?.GetValue<string>() ?? "{}";
                JsonNode? argsNode;
                try { argsNode = JsonNode.Parse(argsRaw); } catch { argsNode = new JsonObject(); }
                contentBlocks.Add(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = tc?["id"]?.DeepClone(),
                    ["name"] = fn?["name"]?.DeepClone(),
                    ["input"] = argsNode,
                });
            }
        }

        var finishReason = choice?["finish_reason"]?.GetValue<string>();
        var usage = openAi["usage"];

        var anthropicResp = new JsonObject
        {
            ["id"] = openAi["id"]?.DeepClone() ?? "msg_proxy",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = openAi["model"]?.DeepClone(),
            ["content"] = contentBlocks,
            ["stop_reason"] = MapFinishReason(finishReason),
            ["stop_sequence"] = null,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = usage?["prompt_tokens"]?.DeepClone() ?? 0,
                ["output_tokens"] = usage?["completion_tokens"]?.DeepClone() ?? 0,
            },
        };

        ctx.Response.ContentType = "application/json";
        var payload = Encoding.UTF8.GetBytes(anthropicResp.ToJsonString());
        await ctx.Response.OutputStream.WriteAsync(payload, ct);
    }

    /// <summary>
    /// Translates an OpenAI SSE stream to Anthropic's event sequence
    /// (message_start -> content_block_start/delta/stop* -> message_delta ->
    /// message_stop) as chunks arrive, so the CLI's streaming UI keeps
    /// working rather than degrading to a blocking wait.
    /// </summary>
    internal static async Task RelayStreamingAsync(HttpListenerContext ctx, HttpResponseMessage upstream, CancellationToken ct)
    {
        ctx.Response.ContentType = "text/event-stream";
        var output = ctx.Response.OutputStream;

        async Task WriteEvent(string eventName, JsonObject data)
        {
            var line = $"event: {eventName}\ndata: {data.ToJsonString()}\n\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            await output.WriteAsync(bytes, ct);
            await output.FlushAsync(ct);
        }

        var msgId = $"msg_{Guid.NewGuid():N}";
        await WriteEvent("message_start", new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject { ["id"] = msgId, ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray(), ["model"] = "proxy" },
        });

        var textBlockOpen = false;
        var openToolBlocks = new Dictionary<int, (string id, string name, StringBuilder args)>();
        string? finishReason = null;

        using var reader = new StreamReader(await upstream.Content.ReadAsStreamAsync(ct));
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            JsonObject chunk;
            try { chunk = JsonNode.Parse(data)!.AsObject(); } catch { continue; }
            var delta = chunk["choices"]?[0]?["delta"];
            finishReason ??= chunk["choices"]?[0]?["finish_reason"]?.GetValue<string?>();

            var deltaText = delta?["content"]?.GetValue<string?>();
            if (!string.IsNullOrEmpty(deltaText))
            {
                if (!textBlockOpen)
                {
                    textBlockOpen = true;
                    await WriteEvent("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" } });
                }
                await WriteEvent("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = deltaText } });
            }

            if (delta?["tool_calls"] is JsonArray toolDeltas)
            {
                foreach (var td in toolDeltas)
                {
                    var idx = td?["index"]?.GetValue<int>() ?? 0;
                    var blockIndex = idx + 1; // index 0 is reserved for the text block above
                    if (!openToolBlocks.ContainsKey(idx))
                    {
                        var id = td?["id"]?.GetValue<string>() ?? $"toolu_{Guid.NewGuid():N}";
                        var name = td?["function"]?["name"]?.GetValue<string>() ?? "";
                        openToolBlocks[idx] = (id, name, new StringBuilder());
                        await WriteEvent("content_block_start", new JsonObject
                        {
                            ["type"] = "content_block_start",
                            ["index"] = blockIndex,
                            ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = new JsonObject() },
                        });
                    }
                    var argsDelta = td?["function"]?["arguments"]?.GetValue<string?>();
                    if (!string.IsNullOrEmpty(argsDelta))
                    {
                        openToolBlocks[idx].args.Append(argsDelta);
                        await WriteEvent("content_block_delta", new JsonObject
                        {
                            ["type"] = "content_block_delta",
                            ["index"] = blockIndex,
                            ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = argsDelta },
                        });
                    }
                }
            }
        }

        if (textBlockOpen)
            await WriteEvent("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = 0 });
        foreach (var idx in openToolBlocks.Keys)
            await WriteEvent("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = idx + 1 });

        await WriteEvent("message_delta", new JsonObject
        {
            ["type"] = "message_delta",
            ["delta"] = new JsonObject { ["stop_reason"] = MapFinishReason(finishReason) },
        });
        await WriteEvent("message_stop", new JsonObject { ["type"] = "message_stop" });
    }

    private static string MapFinishReason(string? openAiReason) => openAiReason switch
    {
        "tool_calls" => "tool_use",
        "length" => "max_tokens",
        "stop" or null => "end_turn",
        _ => "end_turn",
    };

    private static int GetFreeLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
