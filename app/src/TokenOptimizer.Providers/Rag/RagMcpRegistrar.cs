using TokenOptimizer.Providers.Manifests;

namespace TokenOptimizer.Providers.Rag;

/// <summary>
/// Registers this app's own executable as a per-project MCP server
/// (`--cli mcp-rag-server`) against whichever provider adapter is about to
/// launch, using the same McpToolManifest/RegisterMcpToolAsync contract
/// CompanionToolingInstaller uses for context7. Scope "local" because the
/// RAG index is per-project - unlike context7, a single "user"-scope
/// registration would keep pointing retrieval at whichever project
/// registered first. Best-effort: adapters that don't host MCP tools
/// (everything except Claude Code today) fail this silently, matching how
/// RegisterMcpToolAsync already behaves for them.
/// </summary>
public static class RagMcpRegistrar
{
    public const string ToolId = "token-optimizer-rag";

    public static async Task EnsureRegisteredAsync(
        IProviderAdapter provider, string projectPath, Uri embeddingsBaseUrl, Action<string>? log = null)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            log?.Invoke("RAG MCP registration skipped: could not resolve this app's own executable path.");
            return;
        }

        var manifest = new McpToolManifest(
            Id: ToolId,
            Command: Quote(exePath),
            Arguments: new[] { "--cli", "mcp-rag-server", "--project", Quote(projectPath), "--embeddings-url", embeddingsBaseUrl.ToString() },
            Environment: new Dictionary<string, string>(),
            Scope: "local");

        var result = await provider.RegisterMcpToolAsync(manifest);
        if (!result.Success && !result.Message.Contains("already", StringComparison.OrdinalIgnoreCase))
        {
            log?.Invoke($"RAG MCP registration skipped: {result.Message}");
        }
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
}
