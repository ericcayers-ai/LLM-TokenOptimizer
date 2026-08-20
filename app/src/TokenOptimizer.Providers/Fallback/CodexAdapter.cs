using System.Diagnostics;
using System.Runtime.Versioning;
using TokenOptimizer.Core.Models;
using TokenOptimizer.Core.Security;
using TokenOptimizer.Providers.Manifests;

namespace TokenOptimizer.Providers.Fallback;

/// <summary>
/// OpenAI Codex CLI, installed via `npm i -g @openai/codex`. Second in the
/// fallback chain. Uses the real OPENAI_API_KEY stored via
/// ProxyCredentialStore - Codex's own documented auth mechanism, unlike
/// Antigravity/Cursor's OAuth-in-app pattern.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CodexAdapter : IProviderAdapter
{
    private readonly ProxyCredentialStore _credentials;

    public CodexAdapter(ProxyCredentialStore credentials)
    {
        _credentials = credentials;
    }

    public string Name => "Codex";

    public Task<bool> IsAvailableAsync() =>
        Task.FromResult(ExecutableLocators.FindCodex() is not null && _credentials.HasCredential(FallbackProvider.Codex));

    public Task<IReadOnlyList<string>> ListInstalledSkillsAsync() => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    public Task<IReadOnlyList<string>> ListInstalledPluginsAsync() => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<ProviderResult> InstallSkillAsync(SkillManifest skill) =>
        Task.FromResult(ProviderResult.Fail("Codex is a fallback CLI, not a skill host - AGENTS.md is synced instead of skills."));

    public Task<ProviderResult> InstallPluginAsync(PluginManifest plugin) =>
        Task.FromResult(ProviderResult.Fail("Codex does not host plugins."));

    public Task<ProviderResult> RegisterMcpToolAsync(McpToolManifest tool) =>
        Task.FromResult(ProviderResult.Fail("Codex MCP registration is not wired up here - use its own config.toml."));

    internal static string BuildArguments(string? model) =>
        string.IsNullOrWhiteSpace(model) ? string.Empty : $"-m {model}";

    public Task<ISessionHandle> LaunchSessionAsync(SessionLaunchOptions options)
    {
        var exe = ExecutableLocators.FindCodex()
                  ?? throw new InvalidOperationException("Codex executable not found - install with `npm i -g @openai/codex`.");
        var apiKey = _credentials.GetCredentialPlainText(FallbackProvider.Codex)
                     ?? throw new InvalidOperationException("No Codex credential stored - set OPENAI_API_KEY via the proxy credential setup first.");

        var claudeConfigDir = SessionHandoffExporter.GetEffectiveClaudeConfigDir(options.ProjectPath, options.IsolateConfig);
        SessionHandoffExporter.Export(options.ProjectPath, claudeConfigDir);

        var arguments = BuildArguments(options.Model);
        var process = ProcessLaunchHelper.Start(exe, arguments, options.ProjectPath,
            new Dictionary<string, string> { ["OPENAI_API_KEY"] = apiKey });
        return Task.FromResult<ISessionHandle>(new ProcessSessionHandle(Name, options.ProjectPath, process, watchForRateLimit: true));
    }
}
