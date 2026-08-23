using System.Runtime.Versioning;
using TokenOptimizer.Core.Models;
using TokenOptimizer.Core.Security;
using TokenOptimizer.Providers.Manifests;
using TokenOptimizer.Sandbox;

namespace TokenOptimizer.Providers.Fallback;

/// <summary>
/// Google Antigravity's terminal CLI (agy) - first in the fallback chain
/// after Claude Code. CLI-only: the desktop IDE app is deliberately not
/// launched here, so every provider opens a single terminal-style session
/// rather than a separate GUI window. Gated on a stored opt-in credential
/// (see ProxyCredentialStore), not just "is it installed": a user with
/// Antigravity on disk but no intention of using it as a fallback shouldn't
/// have this silently launch it. Real login happens interactively inside
/// the CLI itself (OAuth) - nothing here automates that.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AntigravityAdapter : IProviderAdapter
{
    private readonly ProxyCredentialStore _credentials;
    private SandboxSessionLauncher? _sandboxLauncher;

    public AntigravityAdapter(ProxyCredentialStore credentials, SandboxSessionLauncher? sandboxLauncher = null)
    {
        _credentials = credentials;
        _sandboxLauncher = sandboxLauncher;
    }

    public string Name => "Antigravity";

    public Task<bool> IsAvailableAsync() =>
        Task.FromResult(ExecutableLocators.FindAntigravity() is not null && _credentials.HasCredential(FallbackProvider.Antigravity));

    public Task<IReadOnlyList<string>> ListInstalledSkillsAsync() => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    public Task<IReadOnlyList<string>> ListInstalledPluginsAsync() => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<ProviderResult> InstallSkillAsync(SkillManifest skill) =>
        Task.FromResult(ProviderResult.Fail("Antigravity is a fallback IDE, not a skill host - AGENTS.md is synced instead of skills."));

    public Task<ProviderResult> InstallPluginAsync(PluginManifest plugin) =>
        Task.FromResult(ProviderResult.Fail("Antigravity does not host plugins."));

    public Task<ProviderResult> RegisterMcpToolAsync(McpToolManifest tool) =>
        Task.FromResult(ProviderResult.Fail("Antigravity MCP registration is not exposed via CLI - configure inside the app."));

    public async Task<ISessionHandle> LaunchSessionAsync(SessionLaunchOptions options)
    {
        var exe = ExecutableLocators.FindAntigravity()
                  ?? throw new InvalidOperationException("Antigravity CLI (agy) not found - the desktop IDE is no longer used as a fallback.");

        var claudeConfigDir = SessionHandoffExporter.GetEffectiveClaudeConfigDir(options.ProjectPath, options.IsolateConfig);
        SessionHandoffExporter.Export(options.ProjectPath, claudeConfigDir);

        // agy requires a path argument - inside the sandbox the mounted project IS /workspace.
        return await SandboxLauncher().LaunchAsync(Name, SandboxSessionLauncher.ToLinuxCommand(exe, "\"/workspace\""), options);
    }

    /// <summary>Lazily built default launcher (real OpenSandbox runtime + configured settings) when no launcher was injected.</summary>
    private SandboxSessionLauncher SandboxLauncher() =>
        _sandboxLauncher ??= SandboxLauncherFactory.CreateDefault();
}
