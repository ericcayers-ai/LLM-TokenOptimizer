using System.Diagnostics;
using System.Runtime.Versioning;
using TokenOptimizer.Core.Diagnostics;
using TokenOptimizer.Providers.Manifests;

namespace TokenOptimizer.Providers.Fallback;

/// <summary>
/// deepseek-ai/deepseek-harness ("dsh") - a dev-preview, plugin-based agent
/// runtime with its own web UI (default 127.0.0.1:3080), not a
/// stdin/stdout coding CLI like the other fallback providers. "Launching a
/// session" here means starting its web server as a background process and
/// opening the browser to it, rather than the app driving a terminal
/// session directly - ISessionHandle still wraps the underlying Process
/// (the web server), so the rest of the app's process-lifetime/rate-limit
/// plumbing keeps working unchanged, but there's no stdio conversation to
/// pipe through it. Manual-only (like Codex/Cursor) - not part of the
/// automatic fallback chain. Dev preview: its CLI surface may change
/// without notice, so failures here should read as "harness unavailable",
/// not crash the app.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DeepSeekHarnessAdapter : IProviderAdapter
{
    private const int DefaultPort = 3080;

    public string Name => "DeepSeek Harness";

    public Task<bool> IsAvailableAsync() =>
        Task.FromResult(ExecutableLocators.FindDeepSeekHarness() is not null);

    public async Task<IReadOnlyList<string>> ListInstalledPluginsAsync()
    {
        var exe = ExecutableLocators.FindDeepSeekHarness();
        if (exe is null) return Array.Empty<string>();

        var result = await ExternalCommandRunner.RunAsync(exe, "plugin list", timeoutSeconds: 15);
        if (!result.Success) return Array.Empty<string>();

        return result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    public Task<IReadOnlyList<string>> ListInstalledSkillsAsync() =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<ProviderResult> InstallSkillAsync(SkillManifest skill) =>
        Task.FromResult(ProviderResult.Fail("deepseek-harness has no separate skill concept - everything is a plugin; install against the Claude Code adapter and sync from there."));

    public async Task<ProviderResult> InstallPluginAsync(PluginManifest plugin)
    {
        var exe = ExecutableLocators.FindDeepSeekHarness();
        if (exe is null) return ProviderResult.Fail("deepseek-harness (dsh) not found - install with `npm i -g @deepseek-ai/dsh` first.");

        if (plugin.Source != PluginSource.LocalPath)
            return ProviderResult.Fail("deepseek-harness plugin install only supports local paths in this adapter - marketplace/git sources aren't wired up.");

        var result = await ExternalCommandRunner.RunAsync(exe, $"plugin install \"{plugin.SourceLocator}\"", timeoutSeconds: 30);
        return result.Success
            ? ProviderResult.Ok($"Plugin '{plugin.Id}' installed into deepseek-harness")
            : ProviderResult.Fail($"deepseek-harness plugin install failed (dev preview - its CLI surface may have changed): {result.Output}");
    }

    public Task<ProviderResult> RegisterMcpToolAsync(McpToolManifest tool) =>
        Task.FromResult(ProviderResult.Fail("deepseek-harness MCP registration is not wired up here - use its own web UI/config."));

    public Task<ISessionHandle> LaunchSessionAsync(SessionLaunchOptions options)
    {
        var exe = ExecutableLocators.FindDeepSeekHarness()
                  ?? throw new InvalidOperationException("deepseek-harness (dsh) not found - install with `npm i -g @deepseek-ai/dsh` first.");

        var process = ProcessLaunchHelper.Start(exe, $"web --port {DefaultPort}", options.ProjectPath);
        if (process is null)
            throw new InvalidOperationException("Failed to start deepseek-harness web server.");

        try
        {
            Process.Start(new ProcessStartInfo($"http://127.0.0.1:{DefaultPort}") { UseShellExecute = true });
        }
        catch
        {
            // Best effort - the server is still up even if opening a browser tab failed.
        }

        return Task.FromResult<ISessionHandle>(new ProcessSessionHandle(Name, options.ProjectPath, process, watchForRateLimit: false));
    }
}
