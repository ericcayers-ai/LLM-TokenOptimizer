using System.Diagnostics;
using System.Runtime.Versioning;
using TokenOptimizer.Core.Diagnostics;
using TokenOptimizer.Core.Models;
using TokenOptimizer.Core.Security;
using TokenOptimizer.Providers.Claude;
using TokenOptimizer.Providers.Compat;
using TokenOptimizer.Providers.Manifests;

namespace TokenOptimizer.Providers.Fallback;

/// <summary>
/// Groq - a fast inference API, not its own coding CLI/IDE. Like the LM
/// Studio-local adapter, "using Groq" means launching Claude Code itself
/// pointed at a local proxy, so a Groq-hosted model becomes a drop-in swap
/// for whichever model Claude Code would otherwise talk to - same shared
/// ~/.claude environment, same skills and plugins, zero extra setup.
///
/// Groq's endpoint speaks the OpenAI chat-completions schema, not
/// Anthropic's Messages schema Claude Code CLI expects on
/// ANTHROPIC_BASE_URL - pointing the CLI at Groq directly produces
/// requests Groq can't parse. AnthropicCompatProxy bridges the two: it runs
/// locally, and the CLI talks to it instead of Groq directly.
///
/// Gated on a stored GROQ_API_KEY via ProxyCredentialStore, same pattern as
/// Codex's OPENAI_API_KEY. Manual-only (like Codex/Cursor) - not part of
/// the automatic fallback chain.
/// </summary>
[SupportedOSPlatform("windows")] // ProxyCredentialStore is DPAPI-backed (Windows-only), not a Groq API constraint.
public sealed class GroqAdapter : IProviderAdapter
{
    private static readonly Uri ApiBaseUrl = new("https://api.groq.com/openai/v1");

    private readonly ProxyCredentialStore _credentials;
    private readonly ClaudeExecutableLocator _claudeLocator;

    public GroqAdapter(ProxyCredentialStore credentials, ClaudeExecutableLocator claudeLocator)
    {
        _credentials = credentials;
        _claudeLocator = claudeLocator;
    }

    public string Name => "Groq";

    public async Task<bool> IsAvailableAsync() =>
        _credentials.HasCredential(FallbackProvider.Groq) && await _claudeLocator.FindAsync() is not null;

    /// <summary>Validated model catalog for the UI - see GroqModelCatalog for why free-text model entry isn't safe here.</summary>
    public Task<IReadOnlyList<GroqModel>> ListModelsAsync()
    {
        var apiKey = _credentials.GetCredentialPlainText(FallbackProvider.Groq);
        return apiKey is null
            ? Task.FromResult<IReadOnlyList<GroqModel>>(Array.Empty<GroqModel>())
            : GroqModelCatalog.ListAsync(apiKey);
    }

    public Task<IReadOnlyList<string>> ListInstalledSkillsAsync() =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<IReadOnlyList<string>> ListInstalledPluginsAsync() =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<ProviderResult> InstallSkillAsync(SkillManifest skill) =>
        Task.FromResult(ProviderResult.Fail("Groq is a model backend, not a skill host - install skills against the Claude Code adapter (shared automatically before every launch)."));

    public Task<ProviderResult> InstallPluginAsync(PluginManifest plugin) =>
        Task.FromResult(ProviderResult.Fail("Groq does not host plugins - install against the Claude Code adapter."));

    public Task<ProviderResult> RegisterMcpToolAsync(McpToolManifest tool) =>
        Task.FromResult(ProviderResult.Fail("Groq does not register MCP tools - register against the Claude Code adapter."));

    public async Task<ISessionHandle> LaunchSessionAsync(SessionLaunchOptions options)
    {
        var apiKey = _credentials.GetCredentialPlainText(FallbackProvider.Groq)
                     ?? throw new InvalidOperationException("No Groq credential stored - set GROQ_API_KEY via the proxy credential setup first.");
        if (!GroqModelCatalog.LooksLikeValidKey(apiKey))
            throw new InvalidOperationException("Stored Groq credential doesn't look like a Groq API key (expected a 'gsk_' prefix) - re-check what was saved.");
        var claudeExe = await _claudeLocator.FindAsync()
                         ?? throw new InvalidOperationException("Claude Code executable not found - install it first.");

        await ExternalCommandRunner.RunAsync(claudeExe, "plugin marketplace update", timeoutSeconds: 20);

        var proxy = new AnthropicCompatProxy(ApiBaseUrl, () => apiKey);
        await proxy.StartAsync();

        var args = new List<string>();
        var resumeFlag = options.ResumeMode switch
        {
            SessionResumeMode.Continue => "--continue",
            SessionResumeMode.Pick => "--resume",
            _ => null,
        };
        if (resumeFlag is not null) args.Add(resumeFlag);
        if (!string.IsNullOrWhiteSpace(options.Model)) args.Add($"--model {options.Model}");

        var psi = new ProcessStartInfo
        {
            FileName = claudeExe,
            Arguments = string.Join(' ', args),
            WorkingDirectory = options.ProjectPath,
            UseShellExecute = false,
        };
        psi.EnvironmentVariables["ANTHROPIC_BASE_URL"] = proxy.BaseUrl;
        psi.EnvironmentVariables["ANTHROPIC_AUTH_TOKEN"] = "proxied-locally"; // the proxy injects the real Groq key upstream; the CLI never needs to see it.
        psi.EnvironmentVariables["CLAUDE_MEM_WORKER_PORT"] = CompanionToolingInstaller.IsolatedWorkerPort.ToString();
        psi.EnvironmentVariables["CLAUDE_MEM_DATA_DIR"] = CompanionToolingInstaller.IsolatedDataDir;

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
