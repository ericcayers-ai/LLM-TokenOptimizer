using System.Diagnostics;
using System.Runtime.Versioning;
using TokenOptimizer.Core.Models;
using TokenOptimizer.Core.Security;
using TokenOptimizer.Providers.Manifests;

namespace TokenOptimizer.Providers.Fallback;

/// <summary>
/// Cursor's terminal CLI (cursor-agent) - manual-only, CLI-only. The Cursor
/// desktop IDE app is deliberately not launched here, so every provider
/// opens a single terminal-style session rather than a separate GUI window.
/// Same opt-in-credential gating as Antigravity; real auth happens via
/// interactive sign-in inside the CLI itself.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CursorAdapter : IProviderAdapter
{
    private readonly ProxyCredentialStore _credentials;

    public CursorAdapter(ProxyCredentialStore credentials)
    {
        _credentials = credentials;
    }

    public string Name => "Cursor";

    public Task<bool> IsAvailableAsync() =>
        Task.FromResult(ExecutableLocators.FindCursor() is not null && _credentials.HasCredential(FallbackProvider.Cursor));

    public Task<IReadOnlyList<string>> ListInstalledSkillsAsync() => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    public Task<IReadOnlyList<string>> ListInstalledPluginsAsync() => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<ProviderResult> InstallSkillAsync(SkillManifest skill) =>
        Task.FromResult(ProviderResult.Fail("Cursor is a fallback IDE, not a skill host - AGENTS.md is synced instead of skills."));

    public Task<ProviderResult> InstallPluginAsync(PluginManifest plugin) =>
        Task.FromResult(ProviderResult.Fail("Cursor does not host plugins via this adapter."));

    public Task<ProviderResult> RegisterMcpToolAsync(McpToolManifest tool) =>
        Task.FromResult(ProviderResult.Fail("Cursor MCP registration is not wired up here - configure inside the app."));

    public Task<ISessionHandle> LaunchSessionAsync(SessionLaunchOptions options)
    {
        var exe = ExecutableLocators.FindCursor()
                  ?? throw new InvalidOperationException("Cursor CLI (cursor-agent) not found - the desktop app is no longer used as a fallback.");

        var claudeConfigDir = SessionHandoffExporter.GetEffectiveClaudeConfigDir(options.ProjectPath, options.IsolateConfig);
        SessionHandoffExporter.Export(options.ProjectPath, claudeConfigDir);

        var process = ProcessLaunchHelper.Start(exe, $"\"{options.ProjectPath}\"", options.ProjectPath);

        return Task.FromResult<ISessionHandle>(new ProcessSessionHandle(Name, options.ProjectPath, process, watchForRateLimit: true));
    }
}
