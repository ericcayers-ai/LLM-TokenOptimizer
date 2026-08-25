using System.Runtime.Versioning;
using TokenOptimizer.Core.Models;
using TokenOptimizer.Core.Security;
using TokenOptimizer.Providers.Manifests;
using TokenOptimizer.Sandbox;

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
    private SandboxSessionLauncher? _sandboxLauncher;

    public CursorAdapter(ProxyCredentialStore credentials, SandboxSessionLauncher? sandboxLauncher = null)
    {
        _credentials = credentials;
        _sandboxLauncher = sandboxLauncher;
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

    public async Task<ISessionHandle> LaunchSessionAsync(SessionLaunchOptions options)
    {
        var exe = ExecutableLocators.FindCursor()
                  ?? throw new InvalidOperationException("Cursor CLI (cursor-agent) not found - the desktop app is no longer used as a fallback.");

        var claudeConfigDir = SessionHandoffExporter.GetEffectiveClaudeConfigDir(options.ProjectPath, options.IsolateConfig);
        SessionHandoffExporter.Export(options.ProjectPath, claudeConfigDir);

        // cursor-agent requires a path argument - inside the sandbox the mounted project IS /workspace.
        return await SandboxLauncher().LaunchAsync(Name, SandboxSessionLauncher.ToLinuxCommand(exe, "\"/workspace\""), options);
    }

    /// <summary>Lazily built default launcher (real OpenSandbox runtime + configured settings) when no launcher was injected.</summary>
    private SandboxSessionLauncher SandboxLauncher() =>
        _sandboxLauncher ??= SandboxLauncherFactory.CreateDefault();
}
