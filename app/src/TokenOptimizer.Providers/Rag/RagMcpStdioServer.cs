using System.Text.Json;
using System.Text.Json.Nodes;
using TokenOptimizer.Providers.LlamaCpp;

namespace TokenOptimizer.Providers.Rag;

/// <summary>
/// Minimal MCP server over stdio (newline-delimited JSON-RPC 2.0, no
/// Content-Length framing) exposing LlamaCppRagService as two tools:
/// rag_retrieve and rag_ingest. Claude Code is the MCP client here - this
/// process is only ever spawned by it (via `claude mcp add ... -- <this exe>
/// --cli mcp-rag-server ...`, wired up by RagMcpRegistrar), so there is no
/// need for us to build a generic MCP *client*. Auto-ingests the project's
/// text/code files on startup so retrieval works without a manual step.
/// </summary>
public static class RagMcpStdioServer
{
    private static readonly string[] IngestedExtensions =
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".md", ".mdx", ".json", ".yaml", ".yml",
        ".cshtml", ".razor", ".axaml", ".xaml", ".go", ".rs", ".java", ".rb", ".cpp", ".c",
        ".h", ".hpp", ".sql", ".sh", ".ps1", ".toml", ".txt",
    };

    private static readonly string[] SkippedDirectoryNames =
    {
        ".git", ".graphify", "graphify-out", "node_modules", "bin", "obj", "dist", "build",
        ".venv", "venv", "__pycache__", ".claude", ".vs", ".idea",
    };

    private const int MaxIngestFiles = 200;
    private const long MaxIngestFileBytes = 200 * 1024;

    public static async Task<int> RunAsync(string projectPath, Uri embeddingsBaseUrl, CancellationToken ct = default)
    {
        var rag = new LlamaCppRagService(embeddingsBaseUrl);

        // Auto-ingest walks up to MaxIngestFiles sequentially, one embeddings
        // call each; LlamaCppRagService's HttpClient has a 30s timeout per
        // call, so an unreachable-but-not-actively-refusing endpoint (e.g.
        // firewalled, or hung) could otherwise stall startup for a very long
        // time. A single short-timeout probe first keeps that failure mode
        // to a couple seconds instead.
        if (await IsEmbeddingsEndpointReachableAsync(embeddingsBaseUrl, ct))
        {
            await AutoIngestProjectAsync(rag, projectPath, ct);
        }
        else
        {
            await Console.Error.WriteLineAsync($"[rag-mcp] embeddings endpoint {embeddingsBaseUrl} unreachable - skipping auto-ingest (rag_ingest/rag_retrieve will still be exposed).");
        }

        await Console.Error.WriteLineAsync($"[rag-mcp] ready - {rag.ChunkCount} chunks indexed from {projectPath}");

        while (!ct.IsCancellationRequested)
        {
            var line = await Console.In.ReadLineAsync(ct);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonNode? request;
            try
            {
                request = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }
            if (request is null) continue;

            var method = request["method"]?.GetValue<string>();
            var id = request["id"]?.DeepClone();
            if (method is null || id is null) continue; // notifications need no response

            var (isError, payload) = method switch
            {
                "initialize" => (false, BuildInitializeResult()),
                "tools/list" => (false, BuildToolsListResult()),
                "tools/call" => await HandleToolCallAsync(rag, projectPath, request["params"], ct),
                _ => (true, BuildError(-32601, $"Method not found: {method}")),
            };

            var envelope = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id };
            envelope[isError ? "error" : "result"] = payload;

            Console.Out.WriteLine(envelope.ToJsonString());
            await Console.Out.FlushAsync(ct);
        }

        return 0;
    }

    private static JsonNode BuildInitializeResult() => new JsonObject
    {
        ["protocolVersion"] = "2024-11-05",
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject { ["name"] = "token-optimizer-rag", ["version"] = "1.0.0" },
    };

    private static JsonNode BuildToolsListResult() => new JsonObject
    {
        ["tools"] = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "rag_retrieve",
                ["description"] = "Retrieve the most relevant chunks from this project's auto-ingested RAG index for a query.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string" },
                        ["top_k"] = new JsonObject { ["type"] = "integer", ["description"] = "Max chunks to return (default 3)." },
                        ["min_score"] = new JsonObject { ["type"] = "number", ["description"] = "Minimum cosine-similarity affinity, 0-1 (default 0.5)." },
                    },
                    ["required"] = new JsonArray { "query" },
                },
            },
            new JsonObject
            {
                ["name"] = "rag_ingest",
                ["description"] = "Ingest or re-ingest one file's text into the RAG index. Path may be relative to the project root or absolute.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } },
                    ["required"] = new JsonArray { "path" },
                },
            },
        },
    };

    private static async Task<(bool IsError, JsonNode Payload)> HandleToolCallAsync(
        LlamaCppRagService rag, string projectPath, JsonNode? callParams, CancellationToken ct)
    {
        var name = callParams?["name"]?.GetValue<string>();
        var arguments = callParams?["arguments"];

        try
        {
            switch (name)
            {
                case "rag_retrieve":
                {
                    var query = arguments?["query"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(query)) return (true, BuildError(-32602, "'query' is required."));

                    var topK = arguments?["top_k"]?.GetValue<int>() ?? 3;
                    var minScore = arguments?["min_score"]?.GetValue<double>() ?? 0.5;
                    var results = await rag.RetrieveAsync(query, topK, minScore, ct);

                    var text = results.Count == 0
                        ? "No relevant chunks found above the affinity threshold."
                        : string.Join("\n\n", results.Select(r => $"[{r.SourcePath}] (score {r.Score:F2})\n{r.Text}"));

                    return (false, ToolTextResult(text));
                }

                case "rag_ingest":
                {
                    var path = arguments?["path"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(path)) return (true, BuildError(-32602, "'path' is required."));

                    var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(projectPath, path);
                    if (!File.Exists(fullPath)) return (true, BuildError(-32602, $"File not found: {fullPath}"));

                    var text = await File.ReadAllTextAsync(fullPath, ct);
                    await rag.IngestAsync(fullPath, text, ct: ct);
                    return (false, ToolTextResult($"Ingested {fullPath} ({rag.ChunkCount} total chunks indexed)."));
                }

                default:
                    return (true, BuildError(-32601, $"Unknown tool: {name}"));
            }
        }
        catch (Exception ex)
        {
            return (true, BuildError(-32000, ex.Message));
        }
    }

    private static JsonNode ToolTextResult(string text) => new JsonObject
    {
        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
    };

    private static JsonNode BuildError(int code, string message) => new JsonObject
    {
        ["code"] = code,
        ["message"] = message,
    };

    private static async Task<bool> IsEmbeddingsEndpointReachableAsync(Uri embeddingsBaseUrl, CancellationToken ct)
    {
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await probe.GetAsync(embeddingsBaseUrl, ct);
            return true; // any HTTP response (even 404/405) means something is listening
        }
        catch
        {
            return false;
        }
    }

    private static async Task AutoIngestProjectAsync(LlamaCppRagService rag, string projectPath, CancellationToken ct)
    {
        if (!Directory.Exists(projectPath)) return;

        var ingested = 0;
        foreach (var file in EnumerateCandidateFiles(projectPath))
        {
            if (ct.IsCancellationRequested || ingested >= MaxIngestFiles) break;

            try
            {
                var info = new FileInfo(file);
                if (info.Length == 0 || info.Length > MaxIngestFileBytes) continue;

                var text = await File.ReadAllTextAsync(file, ct);
                await rag.IngestAsync(file, text, ct: ct);
                ingested++;
            }
            catch
            {
                // Best-effort auto-ingest - unreadable/binary-looking files are skipped, never fatal to server startup.
            }
        }
    }

    private static IEnumerable<string> EnumerateCandidateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> subDirs;
            IEnumerable<string> files;
            try
            {
                subDirs = Directory.EnumerateDirectories(dir);
                files = Directory.EnumerateFiles(dir);
            }
            catch
            {
                continue;
            }

            foreach (var sub in subDirs)
            {
                var name = Path.GetFileName(sub);
                if (SkippedDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                stack.Push(sub);
            }

            foreach (var file in files)
            {
                if (IngestedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }
    }
}
