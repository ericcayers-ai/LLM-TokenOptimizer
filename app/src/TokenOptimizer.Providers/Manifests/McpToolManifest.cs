namespace TokenOptimizer.Providers.Manifests;

/// <summary>
/// Provider-neutral MCP server registration - a command + args + env, which
/// is the lowest common denominator every provider's MCP config accepts.
/// </summary>
public sealed record McpToolManifest(
    string Id,
    string Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string Scope = "user");
