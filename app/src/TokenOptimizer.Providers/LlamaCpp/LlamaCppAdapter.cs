using System.Globalization;
using System.Text.RegularExpressions;
using TokenOptimizer.Core.Config;
using TokenOptimizer.Core.Diagnostics;
using TokenOptimizer.Core.Models;
using TokenOptimizer.Providers.Claude;
using TokenOptimizer.Providers.Compat;
using TokenOptimizer.Providers.Fallback;
using TokenOptimizer.Providers.Manifests;
using TokenOptimizer.Sandbox;

namespace TokenOptimizer.Providers.LlamaCpp;

/// <summary>
/// Wraps `unsloth start` (unsloth.ai/docs/integrations/unsloth-start) so a
/// local GGUF model becomes a drop-in swap for the Claude Code proxy model.
/// `unsloth start claude --model repo:quant` already runs an OpenAI-compatible
/// server, resolves/loads the GGUF, and launches Claude Code pointed at it,
/// "never touching your agent's config files" per its own docs.
///
/// Known Unsloth backend gaps: auto-compaction/rolling-context-window is absent
/// from the server/CLI at the API level. That's a real backend gap, not
/// something faked here - but LaunchWithRollingContextAsync below adds a genuine
/// client-side rolling window in front of Unsloth's server (same idea
/// AnthropicCompatProxy already uses for Groq, just without a schema
/// translation this time - see RollingContextProxy). Two gaps remain that a
/// client-side wrapper genuinely cannot manufacture: (1) no documented
/// model-swap/unload/TTL-eviction endpoints for juggling multiple loaded models
/// mid-session; (2) no documented concurrent-request/parallel-slot control.
/// </summary>
public sealed class LlamaCppAdapter : IProviderAdapter
{
    private readonly LlamaCppPresetStore _presets;
    private readonly ClaudeExecutableLocator? _claudeLocator;
    private SandboxSessionLauncher? _sandboxLauncher;
    private string? _unslothPath;

    public LlamaCppAdapter(LlamaCppPresetStore? presets = null, ClaudeExecutableLocator? claudeLocator = null,
        SandboxSessionLauncher? sandboxLauncher = null)
    {
        _presets = presets ?? new LlamaCppPresetStore();
        _claudeLocator = claudeLocator;
        _sandboxLauncher = sandboxLauncher;
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
        var args = new List<string> { "start", "claude", "--model", modelSpec, "--max-seq-length", options.ContextLength.ToString() };

        if (options.GgufVariant is { } variant) args.AddRange(["--gguf-variant", variant]);
        if (options.LoadIn4Bit is { } l4b) args.Add(l4b ? "--load-in-4bit" : "--no-load-in-4bit");
        if (options.TensorParallel is { } tp) args.Add(tp ? "--tensor-parallel" : "--no-tensor-parallel");
        if (options.Persist is { } persist) args.Add(persist ? "--persist" : "--no-persist");
        if (options.AsSubagent) args.Add("--as-subagent");
        if (options.Yolo) args.Add("--yolo");
        if (options.ApiKey is { } apiKey) args.AddRange(["--api-key", apiKey]);
        if (options.Temperature is { } temp) args.AddRange(["--temperature", temp.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (options.TopP is { } topP) args.AddRange(["--top-p", topP.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (options.TopK is { } topK) args.AddRange(["--top-k", topK.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (options.MinP is { } minP) args.AddRange(["--min-p", minP.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (!string.IsNullOrWhiteSpace(options.ChatTemplateKwargs)) args.AddRange(["--chat-template-kwargs", options.ChatTemplateKwargs]);
        if (options.Launch is { } launch) args.Add(launch ? "--launch" : "--no-launch");
        if (options.Serve is { } serve) args.Add(serve ? "--serve" : "--no-serve");

        // Claude Code's own flag (not unsloth's) - unsloth forwards it through since it doesn't recognize it.
        if (!string.IsNullOrWhiteSpace(options.SystemPromptAppend)) args.AddRange(["--append-system-prompt", options.SystemPromptAppend]);

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

        // A saved per-model preset (LlamaCppPresetStore) always wins if the user set one.
        // Otherwise fall back to a fully auto-configured, tier-calibrated default
        // (LlamaCppDefaultPresets) rather than a bare LlamaCppLaunchOptions() - so a model
        // never needs a manual trip through Unsloth Studio before it's usable here.
        var launchOptions = await _presets.GetAsync(family.RepoId, quant) ?? LlamaCppDefaultPresets.Build(family, quant);

        if (launchOptions.RollingContextWindowEnabled)
        {
            return await LaunchWithRollingContextAsync(unsloth, modelSpec, launchOptions, options, claudeArgs);
        }

        var arguments = BuildArguments(modelSpec, launchOptions, claudeArgs);

        // The whole `unsloth start claude` CLI session (server + agent) runs
        // in the sandbox against the /workspace mount. Note: the host-side
        // env vars this path used to set (UNSLOTH_STUDIO_URL, and a host
        // CLAUDE_CONFIG_DIR profile path under IsolateConfig) cannot cross
        // the SandboxSessionLauncher boundary yet - env plumbing is pending
        // upstream work, see task report.
        return await SandboxLauncher().LaunchAsync(Name, SandboxSessionLauncher.ToLinuxCommand(unsloth, arguments), options);
    }

    /// <summary>Lazily built default launcher (real OpenSandbox runtime + configured settings) when no launcher was injected.</summary>
    private SandboxSessionLauncher SandboxLauncher() =>
        _sandboxLauncher ??= new SandboxSessionLauncher(new OpenSandboxSdkRuntime(new SandboxSettings()), new SandboxSettings());

    /// <summary>
    /// The rolling-context-window path (default): boot Unsloth's local
    /// server without launching Claude Code itself (--no-launch --serve),
    /// parse the Anthropic-shaped endpoint/credentials it generated from
    /// that boot output, then launch Claude Code ourselves pointed at
    /// TokenOptimizer's own RollingContextProxy instead of straight at
    /// Unsloth - the proxy forwards to the real endpoint, trimming the
    /// conversation first when it's over budget. Falls back with a clear
    /// error (never a silent wrong result) if Unsloth's --no-launch output
    /// doesn't contain what this expects to parse.
    /// </summary>
    private async Task<ISessionHandle> LaunchWithRollingContextAsync(
        string unsloth, string modelSpec, LlamaCppLaunchOptions launchOptions, SessionLaunchOptions options, string claudeArgs)
    {
        var bootArguments = BuildBootArguments(modelSpec, launchOptions);
        var boot = await ExternalCommandRunner.RunAsync(unsloth, bootArguments, options.ProjectPath, timeoutSeconds: 90);
        if (!boot.Success)
        {
            throw new InvalidOperationException(
                $"Rolling context window setup failed - 'unsloth start --no-launch' could not boot the local server: {boot.Output}");
        }

        var generated = ParseGeneratedEnvironment(boot.Output)
            ?? throw new InvalidOperationException(
                "Rolling context window setup failed - could not find ANTHROPIC_BASE_URL in unsloth's --no-launch output " +
                "(its printed format may have changed since this was written). Save a per-model preset with " +
                "RollingContextWindowEnabled=false to launch directly through unsloth instead.");

        var claudeExe = (_claudeLocator is not null ? await _claudeLocator.FindAsync() : null)
            ?? throw new InvalidOperationException("Claude Code executable not found - install it first.");

        await ClaudeCodeAdapter.RefreshPluginMarketplacesAsync(claudeExe);

        var proxy = new RollingContextProxy(generated.BaseUrl, () => generated.ApiKey, launchOptions.ContextLength);
        await proxy.StartAsync();

        var claudeArgList = new List<string>();
        if (!string.IsNullOrWhiteSpace(claudeArgs)) claudeArgList.Add(claudeArgs);
        if (!string.IsNullOrWhiteSpace(launchOptions.SystemPromptAppend))
        {
            claudeArgList.Add("--append-system-prompt");
            claudeArgList.Add(launchOptions.SystemPromptAppend.Contains(' ') ? $"\"{launchOptions.SystemPromptAppend}\"" : launchOptions.SystemPromptAppend);
        }

        // The unsloth server boot above stays on the host (local server
        // spawn); the Claude Code session it feeds runs in the sandbox.
        // Note: the host env vars this path used to set (ANTHROPIC_BASE_URL
        // pointing at the host loopback proxy, ANTHROPIC_AUTH_TOKEN, and a
        // host CLAUDE_CONFIG_DIR profile under IsolateConfig) cannot cross
        // the SandboxSessionLauncher boundary yet - env plumbing is pending
        // upstream work, see task report.
        var handle = (SandboxSessionHandle)await SandboxLauncher().LaunchAsync(
            Name, SandboxSessionLauncher.ToLinuxCommand(claudeExe, string.Join(' ', claudeArgList)), options);
        _ = handle.RateLimitOutcome.ContinueWith(async _ => await proxy.DisposeAsync());
        return handle;
    }

    /// <summary>Same model/config-loading flags as BuildArguments, minus everything that only makes sense when unsloth is also launching the agent itself (Claude-facing args, --yolo, ExtraArguments) - this boots and configures the server only.</summary>
    internal static string BuildBootArguments(string modelSpec, LlamaCppLaunchOptions options)
    {
        var args = new List<string> { "start", "claude", "--model", modelSpec, "--max-seq-length", options.ContextLength.ToString(), "--no-launch", "--serve" };

        if (options.GgufVariant is { } variant) args.AddRange(["--gguf-variant", variant]);
        if (options.LoadIn4Bit is { } l4b) args.Add(l4b ? "--load-in-4bit" : "--no-load-in-4bit");
        if (options.TensorParallel is { } tp) args.Add(tp ? "--tensor-parallel" : "--no-tensor-parallel");
        if (options.Persist is { } persist) args.Add(persist ? "--persist" : "--no-persist");
        if (options.ApiKey is { } apiKey) args.AddRange(["--api-key", apiKey]);
        if (options.Temperature is { } temp) args.AddRange(["--temperature", temp.ToString(CultureInfo.InvariantCulture)]);
        if (options.TopP is { } topP) args.AddRange(["--top-p", topP.ToString(CultureInfo.InvariantCulture)]);
        if (options.TopK is { } topK) args.AddRange(["--top-k", topK.ToString(CultureInfo.InvariantCulture)]);
        if (options.MinP is { } minP) args.AddRange(["--min-p", minP.ToString(CultureInfo.InvariantCulture)]);
        if (!string.IsNullOrWhiteSpace(options.ChatTemplateKwargs)) args.AddRange(["--chat-template-kwargs", options.ChatTemplateKwargs]);

        return string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
    }

    /// <summary>
    /// Anthropic's own env-var contract for a custom endpoint is fixed
    /// (ANTHROPIC_BASE_URL, plus ANTHROPIC_API_KEY/ANTHROPIC_AUTH_TOKEN) -
    /// that's the only mechanism Claude Code exposes for this, so it's what
    /// Unsloth's own "automatically configures the endpoint, API key,
    /// provider, model" claim (its docs' words) must be setting for the
    /// `claude` agent. The exact --no-launch print format isn't documented,
    /// so this scans loosely (tolerant of "export ", quotes, ":" or "=") -
    /// returns null rather than a wrong guess if nothing matches.
    /// </summary>
    internal static (Uri BaseUrl, string? ApiKey)? ParseGeneratedEnvironment(string output)
    {
        var baseUrlMatch = Regex.Match(output, @"ANTHROPIC_BASE_URL\s*[=:]\s*""?([^\s""]+)""?", RegexOptions.IgnoreCase);
        if (!baseUrlMatch.Success || !Uri.TryCreate(baseUrlMatch.Groups[1].Value, UriKind.Absolute, out var baseUrl)) return null;

        var keyMatch = Regex.Match(output, @"ANTHROPIC_(?:API_KEY|AUTH_TOKEN)\s*[=:]\s*""?([^\s""]+)""?", RegexOptions.IgnoreCase);
        return (baseUrl, keyMatch.Success ? keyMatch.Groups[1].Value : null);
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
