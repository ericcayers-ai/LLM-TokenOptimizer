using System.Diagnostics;
using System.Runtime.Versioning;
using TokenOptimizer.Core.Config;
using TokenOptimizer.Core.Diagnostics;
using TokenOptimizer.Core.Models;
using TokenOptimizer.Core.Security;
using TokenOptimizer.Providers.Claude;
using TokenOptimizer.Providers.Compat;
using TokenOptimizer.Providers.Manifests;

namespace TokenOptimizer.Providers.Fallback;

/// <summary>
/// OpenCode's self-hosted Go API server - like GroqAdapter, "using OpenCode"
/// means launching Claude Code itself pointed at a local proxy, so a model
/// served behind the OpenCode server becomes a drop-in swap for whichever
/// model Claude Code would otherwise talk to.
///
/// Unlike Groq (fixed cloud endpoint), OpenCode is self-hosted: the base
/// URL is whatever the user's Go server is bound to (AppConfig.OpenCodeBaseUrl),
/// stored plaintext since it's not a secret, same as a hostname. The API
/// key is optional - most local OpenCode deployments run without auth -
/// but is sent as a Bearer token when present for operators who front
/// their server with one.
///
/// Part of the automatic fallback chain (unlike Codex/Cursor/Groq), slotted
/// right before the local llama.cpp model: see FallbackChainResolver.
/// </summary>
[SupportedOSPlatform("windows")] // ProxyCredentialStore is DPAPI-backed (Windows-only), not an OpenCode API constraint.
public sealed class OpenCodeAdapter : IProviderAdapter
{
    private readonly ProxyCredentialStore _credentials;
    private readonly ClaudeExecutableLocator _claudeLocator;
    private readonly ConfigStore _configStore;

    public OpenCodeAdapter(ProxyCredentialStore credentials, ClaudeExecutableLocator claudeLocator, ConfigStore configStore)
    {
        _credentials = credentials;
        _claudeLocator = claudeLocator;
        _configStore = configStore;
    }

    public string Name => "OpenCode";

    public async Task<bool> IsAvailableAsync()
    {
        var config = await _configStore.LoadAsync();
        if (string.IsNullOrWhiteSpace(config.OpenCodeBaseUrl) || !Uri.TryCreate(config.OpenCodeBaseUrl, UriKind.Absolute, out _))
            return false;

        return await _claudeLocator.FindAsync() is not null;
    }

    public Task<IReadOnlyList<string>> ListModelsAsync() => Task.FromResult(OpenCodeModelCatalog.ModelIds);

    public Task<IReadOnlyList<string>> ListInstalledSkillsAsync() =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<IReadOnlyList<string>> ListInstalledPluginsAsync() =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<ProviderResult> InstallSkillAsync(SkillManifest skill) =>
        Task.FromResult(ProviderResult.Fail("OpenCode is a model backend, not a skill host - install skills against the Claude Code adapter (shared automatically before every launch)."));

    public Task<ProviderResult> InstallPluginAsync(PluginManifest plugin) =>
        Task.FromResult(ProviderResult.Fail("OpenCode does not host plugins - install against the Claude Code adapter."));

    public Task<ProviderResult> RegisterMcpToolAsync(McpToolManifest tool) =>
        Task.FromResult(ProviderResult.Fail("OpenCode does not register MCP tools - register against the Claude Code adapter."));

    public async Task<ISessionHandle> LaunchSessionAsync(SessionLaunchOptions options)
    {
        var config = await _configStore.LoadAsync();
        if (string.IsNullOrWhiteSpace(config.OpenCodeBaseUrl) || !Uri.TryCreate(config.OpenCodeBaseUrl, UriKind.Absolute, out var baseUrl))
            throw new InvalidOperationException("No OpenCode base URL configured - set it in Fallback credentials first (e.g. http://localhost:4096/v1).");
        var claudeExe = await _claudeLocator.FindAsync()
                         ?? throw new InvalidOperationException("Claude Code executable not found - install it first.");

        await ExternalCommandRunner.RunAsync(claudeExe, "plugin marketplace update", timeoutSeconds: 20);

        var apiKey = _credentials.GetCredentialPlainText(FallbackProvider.OpenCode);
        var proxy = new AnthropicCompatProxy(baseUrl, () => apiKey);
        await proxy.StartAsync();

        var args = new List<string>();
        var resumeFlag = options.ResumeMode switch
        {
            SessionResumeMode.Continue => "--continue",
            SessionResumeMode.Pick => "--resume",
            _ => null,
        };
        if (resumeFlag is not null) args.Add(resumeFlag);
        var model = string.IsNullOrWhiteSpace(options.Model) ? OpenCodeModelCatalog.DefaultModel : options.Model;
        args.Add($"--model {model}");

        var psi = new ProcessStartInfo
        {
            FileName = claudeExe,
            Arguments = string.Join(' ', args),
            WorkingDirectory = options.ProjectPath,
            UseShellExecute = false,
        };
        psi.EnvironmentVariables["ANTHROPIC_BASE_URL"] = proxy.BaseUrl;
        psi.EnvironmentVariables["ANTHROPIC_AUTH_TOKEN"] = "proxied-locally"; // the proxy injects the real OpenCode key (if any) upstream; the CLI never needs to see it.

        if (options.IsolateConfig)
        {
            var profileDir = IsolatedClaudeProfileService.GetOrCreateProfileDir(options.ProjectPath);
            psi.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = profileDir;
        }

        var process = Process.Start(psi);
        var handle = new ProcessSessionHandle(Name, options.ProjectPath, process, watchForRateLimit: true);
        _ = handle.RateLimitOutcome.ContinueWith(async _ => await proxy.DisposeAsync());
        return handle;
    }
}
