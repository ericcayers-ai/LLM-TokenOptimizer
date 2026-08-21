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
/// OpenCode Go - the OpenCode team's low-cost subscription gateway to
/// popular open coding models (opencode.ai/docs/providers#opencode-go).
/// Sign in once at https://opencode.ai/zen to get an API key; that's the
/// only setup this adapter needs. The Go gateway already speaks the
/// Anthropic Messages API, so no schema translation is needed - but a
/// local AnthropicCompatProxy (in anthropicPassthrough mode) still sits in
/// front, because Claude Code CLI 2.1.237 silently rewrites any --model
/// value it doesn't recognize as one of the account's allowed Anthropic
/// models back to the account default before the request ever leaves the
/// CLI; the proxy re-injects the real model id the caller asked for.
///
/// Part of the automatic fallback chain (unlike Codex/Cursor/Groq), slotted
/// right before the local llama.cpp model: see FallbackChainResolver.
/// </summary>
[SupportedOSPlatform("windows")] // ProxyCredentialStore is DPAPI-backed (Windows-only), not an OpenCode API constraint.
public sealed class OpenCodeAdapter : IProviderAdapter
{
    internal static readonly Uri ApiBaseUrl = new("https://opencode.ai/zen/go");

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

    public ClaudeLaunchEnvironment BuildLaunchEnvironment(SessionLaunchOptions options, string proxyBaseUrl)
    {
        var model = string.IsNullOrWhiteSpace(options.Model) ? OpenCodeModelCatalog.DefaultModel : options.Model;
        var builder = new ClaudeLaunchEnvironmentBuilder()
            .WithResumeMode(options.ResumeMode)
            .WithModel(model)
            .WithAnthropicBaseUrl(proxyBaseUrl)
            .WithAnthropicAuthToken("proxied-locally")
            .WithClaudeMemIsolation();
        if (options.IsolateConfig)
        {
            builder.WithIsolatedConfig(options.ProjectPath);
        }
        return builder.Build();
    }

    public async Task<ISessionHandle> LaunchSessionAsync(SessionLaunchOptions options)
    {
        var apiKey = _credentials.GetCredentialPlainText(FallbackProvider.OpenCode)
                     ?? throw new InvalidOperationException("No OpenCode Go credential stored - sign in at https://opencode.ai/zen and save the API key in Fallback credentials first.");
        var claudeExe = await _claudeLocator.FindAsync()
                         ?? throw new InvalidOperationException("Claude Code executable not found - install it first.");

        await ExternalCommandRunner.RunAsync(claudeExe, "plugin marketplace update", timeoutSeconds: 20);

        var model = string.IsNullOrWhiteSpace(options.Model) ? OpenCodeModelCatalog.DefaultModel : options.Model;
        var proxy = new AnthropicCompatProxy(ApiBaseUrl, () => apiKey, forceModel: model, anthropicPassthrough: true);
        await proxy.StartAsync();

        var launchEnv = BuildLaunchEnvironment(options, proxy.BaseUrl);
        var psi = new ProcessStartInfo
        {
            FileName = claudeExe,
            Arguments = launchEnv.Arguments,
            WorkingDirectory = options.ProjectPath,
            UseShellExecute = false,
        };
        foreach (var kv in launchEnv.Env)
        {
            psi.EnvironmentVariables[kv.Key] = kv.Value;
        }

        var process = Process.Start(psi);
        var handle = new ProcessSessionHandle(Name, options.ProjectPath, process, watchForRateLimit: true);
        _ = handle.RateLimitOutcome.ContinueWith(async _ => await proxy.DisposeAsync());
        return handle;
    }
}
