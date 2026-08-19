using System.Diagnostics;
using TokenOptimizer.Core.Config;
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
///
/// Feature-parity check against LM Studio (the backend this replaced), done
/// against each product's actual server/API docs, not their marketing:
/// auto-compaction/rolling-context-window is absent from BOTH backends at
/// the API level (LM Studio's own bug tracker confirms no built-in
/// conversation summarization, and its `rollingWindow` field is an
/// undocumented internal one not reliably settable outside its own UI/SDK)
/// - so this is not a regression, and nothing is stubbed in here to fake
/// having it. Two real, confirmed-by-docs gaps that remain open because
/// they're genuinely unsloth-side, not something a client-side wrapper can
/// manufacture: (1) no documented model-swap/unload/TTL-eviction endpoints
/// for juggling multiple loaded models mid-session (LM Studio has
/// /api/v1/models/load|unload); (2) no documented concurrent-request/
/// parallel-slot control (LM Studio exposes "Max Concurrent Predictions" in
/// its UI, though not yet via its own REST API either).
/// </summary>
public sealed class LlamaCppAdapter : IProviderAdapter
{
    private readonly LlamaCppPresetStore _presets;
    private string? _unslothPath;

    public LlamaCppAdapter(LlamaCppPresetStore? presets = null)
    {
        _presets = presets ?? new LlamaCppPresetStore();
    }

    public string Name => "Unsloth (local model)";

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
        if (options.Temperature is { } temp) args.AddRange(["--temp", temp.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (options.TopP is { } topP) args.AddRange(["--top-p", topP.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (options.TopK is { } topK) args.AddRange(["--top-k", topK.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (options.MinP is { } minP) args.AddRange(["--min-p", minP.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (!string.IsNullOrWhiteSpace(options.ChatTemplateKwargs)) args.AddRange(["--chat-template-kwargs", options.ChatTemplateKwargs]);
        if (options.Launch is { } launch) args.Add(launch ? "--launch" : "--no-launch");
        if (options.Serve is { } serve) args.Add(serve ? "--serve" : "--no-serve");

        if (!string.IsNullOrWhiteSpace(options.ExtraArguments)) args.Add(options.ExtraArguments);
        if (!string.IsNullOrWhiteSpace(claudeArgs)) args.Add(claudeArgs);

        return string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
    }

    public Task<IReadOnlyList<string>> ListInstalledSkillsAsync() =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<IReadOnlyList<string>> ListInstalledPluginsAsync() =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<ProviderResult> InstallSkillAsync(SkillManifest skill) =>
        Task.FromResult(ProviderResult.Fail("Unsloth is a model backend, not a skill host - install skills against a coding-agent adapter (e.g. Claude Code)."));

    public Task<ProviderResult> InstallPluginAsync(PluginManifest plugin) =>
        Task.FromResult(ProviderResult.Fail("Unsloth does not host plugins - install against a coding-agent adapter (e.g. Claude Code)."));

    public Task<ProviderResult> RegisterMcpToolAsync(McpToolManifest tool) =>
        Task.FromResult(ProviderResult.Fail("Unsloth does not register MCP tools - register against a coding-agent adapter (e.g. Claude Code)."));

    /// <summary>
    /// Launches Claude Code through `unsloth start claude --model
    /// repo:quant`, defaulting to the first supported family's recommended
    /// quant if the caller didn't request a specific one via
    /// options.Model (in "repoId:quant" form).
    /// </summary>
    public async Task<ISessionHandle> LaunchSessionAsync(SessionLaunchOptions options)
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

        // Previously always a bare default - a saved per-model preset (context
        // length, sampler params, subagent/persist toggles, etc.) was written
        // via LlamaCppPresetStore but never actually read back on launch.
        var launchOptions = await _presets.GetAsync(family.RepoId, quant) ?? new LlamaCppLaunchOptions();
        var arguments = BuildArguments(modelSpec, launchOptions, claudeArgs);

        var psi = new ProcessStartInfo
        {
            FileName = unsloth,
            Arguments = arguments,
            WorkingDirectory = options.ProjectPath,
            UseShellExecute = false,
        };
        if (!string.IsNullOrWhiteSpace(launchOptions.StudioUrl))
        {
            psi.EnvironmentVariables["UNSLOTH_STUDIO_URL"] = launchOptions.StudioUrl;
        }
        if (options.IsolateConfig)
        {
            var profileDir = IsolatedClaudeProfileService.GetOrCreateProfileDir(options.ProjectPath);
            psi.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = profileDir;
        }

        var process = Process.Start(psi)
                      ?? throw new InvalidOperationException("Failed to start unsloth.");
        return new ProcessSessionHandle(Name, options.ProjectPath, process, watchForRateLimit: true);
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
