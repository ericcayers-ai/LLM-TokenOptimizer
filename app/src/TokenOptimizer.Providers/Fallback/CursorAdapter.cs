using System.Diagnostics;
using System.Runtime.Versioning;
using TokenOptimizer.Core.Models;
using TokenOptimizer.Core.Security;
using TokenOptimizer.Providers.Manifests;

namespace TokenOptimizer.Providers.Fallback;

/// <summary>
/// Cursor IDE - last of the three IDE/CLI fallbacks before the local model.
/// Same opt-in-credential gating as Antigravity; real auth happens via
/// interactive sign-in inside Cursor itself.
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
                  ?? throw new InvalidOperationException("Cursor executable not found.");

        SessionHandoffExporter.Export(options.ProjectPath);

        var isExe = Path.GetExtension(exe).Equals(".exe", StringComparison.OrdinalIgnoreCase);
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"\"{options.ProjectPath}\"",
            WorkingDirectory = isExe ? null : options.ProjectPath,
            UseShellExecute = isExe,
        });

        return Task.FromResult<ISessionHandle>(new ProcessSessionHandle(Name, options.ProjectPath, process));
    }
}
