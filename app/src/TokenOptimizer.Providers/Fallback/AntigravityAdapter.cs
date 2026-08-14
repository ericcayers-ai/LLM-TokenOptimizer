using System.Diagnostics;
using System.Runtime.Versioning;
using TokenOptimizer.Core.Models;
using TokenOptimizer.Core.Security;
using TokenOptimizer.Providers.Manifests;

namespace TokenOptimizer.Providers.Fallback;

/// <summary>
/// Google Antigravity IDE - first in the fallback chain after Claude Code.
/// Gated on a stored opt-in credential (see ProxyCredentialStore), not just
/// "is it installed": a user with Antigravity on disk but no intention of
/// using it as a fallback shouldn't have this silently open it. Real login
/// happens interactively inside the app itself (OAuth) - nothing here
/// automates that.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AntigravityAdapter : IProviderAdapter
{
    private readonly ProxyCredentialStore _credentials;

    public AntigravityAdapter(ProxyCredentialStore credentials)
    {
        _credentials = credentials;
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

    public Task<ISessionHandle> LaunchSessionAsync(SessionLaunchOptions options)
    {
        var exe = ExecutableLocators.FindAntigravity()
                  ?? throw new InvalidOperationException("Antigravity executable not found.");

        SessionHandoffExporter.Export(options.ProjectPath);

        Process? process;
        var isTerminalCli = exe.EndsWith("agy.exe", StringComparison.OrdinalIgnoreCase);
        if (isTerminalCli)
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"\"{options.ProjectPath}\"",
                WorkingDirectory = options.ProjectPath,
                UseShellExecute = false,
            });
        }
        else
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"\"{options.ProjectPath}\"",
                UseShellExecute = true,
            });
        }

        // Only the terminal CLI variant (agy.exe) has a console to watch -
        // the desktop IDE is a GUI window with no console text to scan.
        return Task.FromResult<ISessionHandle>(new ProcessSessionHandle(Name, options.ProjectPath, process, watchForRateLimit: isTerminalCli));
    }
}
