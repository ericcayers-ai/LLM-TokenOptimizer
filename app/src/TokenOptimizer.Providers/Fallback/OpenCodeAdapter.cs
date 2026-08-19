using System.Diagnostics;
using System.Runtime.Versioning;
using TokenOptimizer.Core.Diagnostics;
using TokenOptimizer.Core.Models;
using TokenOptimizer.Core.Security;
using TokenOptimizer.Providers.Claude;
using TokenOptimizer.Providers.Manifests;

namespace TokenOptimizer.Providers.Fallback;

/// <summary>
/// OpenCode Go - the OpenCode team's low-cost subscription gateway to
/// popular open coding models (opencode.ai/docs/providers#opencode-go).
/// Sign in once at https://opencode.ai/zen to get an API key; that's the
/// only setup this adapter needs - no base URL to configure, no local
/// proxy to run. Unlike Groq (OpenAI chat-completions schema, needs
/// AnthropicCompatProxy to translate), the Go gateway already speaks the
/// Anthropic Messages API, so Claude Code can point ANTHROPIC_BASE_URL at
/// it directly, same shape as pointing at Anthropic's own api.anthropic.com.
///
/// Part of the automatic fallback chain (unlike Codex/Cursor/Groq), slotted
/// right before the local llama.cpp model: see FallbackChainResolver.
/// </summary>
[SupportedOSPlatform("windows")] // ProxyCredentialStore is DPAPI-backed (Windows-only), not an OpenCode API constraint.
public sealed class OpenCodeAdapter : IProviderAdapter
{
    private static readonly Uri ApiBaseUrl = new("https://opencode.ai/zen/go");

    private readonly ProxyCredentialStore _credentials;
    private readonly ClaudeExecutableLocator _claudeLocator;

    public OpenCodeAdapter(ProxyCredentialStore credentials, ClaudeExecutableLocator claudeLocator)
    {
        _credentials = credentials;
        _claudeLocator = claudeLocator;
    }

    public string Name => "OpenCode";

    public async Task<bool> IsAvailableAsync() =>
        _credentials.HasCredential(FallbackProvider.OpenCode) && await _claudeLocator.FindAsync() is not null;

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
        var apiKey = _credentials.GetCredentialPlainText(FallbackProvider.OpenCode)
                     ?? throw new InvalidOperationException("No OpenCode Go credential stored - sign in at https://opencode.ai/zen and save the API key in Fallback credentials first.");
        var claudeExe = await _claudeLocator.FindAsync()
                         ?? throw new InvalidOperationException("Claude Code executable not found - install it first.");

        await ExternalCommandRunner.RunAsync(claudeExe, "plugin marketplace update", timeoutSeconds: 20);

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
        psi.EnvironmentVariables["ANTHROPIC_BASE_URL"] = ApiBaseUrl.ToString();
        psi.EnvironmentVariables["ANTHROPIC_AUTH_TOKEN"] = apiKey;

        if (options.IsolateConfig)
        {
            var profileDir = IsolatedClaudeProfileService.GetOrCreateProfileDir(options.ProjectPath);
            psi.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = profileDir;
        }

        var process = Process.Start(psi);
        return new ProcessSessionHandle(Name, options.ProjectPath, process, watchForRateLimit: true);
    }
}
