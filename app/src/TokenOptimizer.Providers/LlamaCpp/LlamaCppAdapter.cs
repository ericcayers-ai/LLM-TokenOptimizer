using System.Diagnostics;
using TokenOptimizer.Core.Models;
using TokenOptimizer.Providers.Claude;
using TokenOptimizer.Providers.Manifests;

namespace TokenOptimizer.Providers.LlamaCpp;

/// <summary>
/// Wraps `unsloth start` (unsloth.ai/docs/integrations/unsloth-start) so a
/// local GGUF model becomes a drop-in swap for the Claude Code proxy model,
/// replacing LmStudioAdapter. `unsloth start claude --model repo:quant`
/// already runs an OpenAI-compatible server, resolves/loads the GGUF, and
/// launches Claude Code pointed at it, "never touching your agent's config
/// files" per its own docs - so unlike GroqAdapter this adapter does NOT
/// need TokenOptimizer's own AnthropicCompatProxy shim or direct
/// llama-server process ownership; unsloth handles that bridging itself.
/// </summary>
public sealed class LlamaCppAdapter : IProviderAdapter
{
    private string? _unslothPath;

    public string Name => "llama.cpp (local, via unsloth)";

    public Task<bool> IsAvailableAsync() => Task.FromResult(ResolveUnslothPath() is not null);

    public IReadOnlyList<LlamaCppModelFamily> ListSupportedFamilies() => LlamaCppModelCatalog.SupportedFamilies;

    public Task<IReadOnlyList<LlamaCppQuant>> ListQuantsAsync(string repoId) => LlamaCppModelCatalog.ListQuantsAsync(repoId);

    /// <summary>
    /// Builds `unsloth start claude` arguments per its documented flag
    /// surface only - no fabricated flags. modelSpec is "repo:quant" (the
    /// same convention `unsloth start`'s own generated-command examples
    /// use for --model). claudeArgs are forwarded verbatim after unsloth's
    /// own flags, per "Passing agent arguments" in the docs (e.g. --continue).
    /// </summary>
    internal static string BuildArguments(string modelSpec, LlamaCppLaunchOptions options, string claudeArgs)
    {
        var args = new List<string> { "start", "claude", "--model", modelSpec, "--context-length", options.ContextLength.ToString() };

        if (options.GgufVariant is { } variant) args.AddRange(["--gguf-variant", variant]);
        if (options.LoadIn4Bit is { } l4b) args.Add(l4b ? "--load-in-4bit" : "--no-load-in-4bit");
        if (options.TensorParallel is { } tp) args.Add(tp ? "--tensor-parallel" : "--no-tensor-parallel");
        if (options.Persist is { } persist) args.Add(persist ? "--persist" : "--no-persist");
        if (options.AsSubagent) args.Add("--as-subagent");
        if (options.Yolo) args.Add("--yolo");
        if (options.ApiKey is { } apiKey) args.AddRange(["--api-key", apiKey]);

        if (!string.IsNullOrWhiteSpace(options.ExtraArguments)) args.Add(options.ExtraArguments);
        if (!string.IsNullOrWhiteSpace(claudeArgs)) args.Add(claudeArgs);

        return string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
    }

    public Task<IReadOnlyList<string>> ListInstalledSkillsAsync() =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<IReadOnlyList<string>> ListInstalledPluginsAsync() =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<ProviderResult> InstallSkillAsync(SkillManifest skill) =>
        Task.FromResult(ProviderResult.Fail("llama.cpp is a model backend, not a skill host - install skills against a coding-agent adapter (e.g. Claude Code)."));

    public Task<ProviderResult> InstallPluginAsync(PluginManifest plugin) =>
        Task.FromResult(ProviderResult.Fail("llama.cpp does not host plugins - install against a coding-agent adapter (e.g. Claude Code)."));

    public Task<ProviderResult> RegisterMcpToolAsync(McpToolManifest tool) =>
        Task.FromResult(ProviderResult.Fail("llama.cpp does not register MCP tools - register against a coding-agent adapter (e.g. Claude Code)."));

    /// <summary>
    /// Launches Claude Code through `unsloth start claude --model
    /// repo:quant`, defaulting to the first supported family's recommended
    /// quant if the caller didn't request a specific one via
    /// options.Model (in "repoId:quant" form).
    /// </summary>
    public Task<ISessionHandle> LaunchSessionAsync(SessionLaunchOptions options)
    {
        var unsloth = ResolveUnslothPath()
                      ?? throw new InvalidOperationException("unsloth CLI not found - install it (see unsloth.ai/docs/integrations/unsloth-start) and ensure it's on PATH.");

        var (family, quant) = ResolveRequestedModel(options.Model);
        var modelSpec = $"{family.RepoId}:{quant}";

        var claudeArgs = options.ResumeMode switch
        {
            SessionResumeMode.Continue => "--continue",
            SessionResumeMode.Pick => "--resume",
            _ => "",
        };

        var launchOptions = new LlamaCppLaunchOptions();
        var arguments = BuildArguments(modelSpec, launchOptions, claudeArgs);

        var psi = new ProcessStartInfo
        {
            FileName = unsloth,
            Arguments = arguments,
            WorkingDirectory = options.ProjectPath,
            UseShellExecute = false,
        };
        if (options.IsolateConfig)
        {
            var profileDir = IsolatedClaudeProfileService.GetOrCreateProfileDir(options.ProjectPath);
            psi.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = profileDir;
        }

        var process = Process.Start(psi)
                      ?? throw new InvalidOperationException("Failed to start unsloth.");
        return Task.FromResult<ISessionHandle>(new ProcessSessionHandle(Name, options.ProjectPath, process, watchForRateLimit: true));
    }

    private static (LlamaCppModelFamily Family, string Quant) ResolveRequestedModel(string? modelSpec)
    {
        var families = LlamaCppModelCatalog.SupportedFamilies;
        if (string.IsNullOrWhiteSpace(modelSpec))
            return (families[0], families[0].RecommendedQuant);

        var parts = modelSpec.Split(':', 2);
        var repoId = parts[0];
        var quant = parts.Length > 1 ? parts[1] : null;

        var family = families.FirstOrDefault(f => string.Equals(f.RepoId, repoId, StringComparison.OrdinalIgnoreCase))
                     ?? families[0];
        return (family, quant ?? family.RecommendedQuant);
    }

    private string? ResolveUnslothPath() => _unslothPath ??= LlamaCppLocator.Find();
}
