using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
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
///
/// Umbrella redesign: every other "separate tool" provider (Antigravity,
/// Codex, Cursor) calls SessionHandoffExporter.Export() once before
/// launching - that was a gap here, now closed. This adapter goes one step
/// further than those: rather than leaving the project's Claude skills as a
/// passive reference file inside the handoff markdown, LaunchSessionAsync
/// also packages them as a local pnpm package and adds it into the "web"
/// profile dsh actually launches, so they're picked up by dsh's own package
/// tooling instead of sitting as inert reference text. Bundling this into
/// the launch means there is no separate manual "export" step for this
/// provider any more; it always happens as part of switching to it.
///
/// `dsh plugin` verified against the real installed CLI (v0.1.0-rc.7): it is
/// not a bespoke plugin-manifest installer - `dsh plugin --profile &lt;name&gt;
/// &lt;args...&gt;` forwards &lt;args...&gt; straight to `pnpm` running inside that
/// profile's directory (confirmed live: `dsh plugin --profile web --help`
/// prints pnpm's own --help verbatim). So "installing a plugin" here means
/// `pnpm add &lt;local-path-with-a-package.json&gt;` against the "web" profile,
/// not a `plugin.json` manifest as an earlier version of this file assumed.
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

        var result = await ExternalCommandRunner.RunAsync(exe, "plugin --profile web list", timeoutSeconds: 15);
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

        // dsh forwards this straight to `pnpm add <path>` in the "web" profile dir - the path needs its own package.json.
        var result = await ExternalCommandRunner.RunAsync(exe, $"plugin --profile web add \"{plugin.SourceLocator}\"", timeoutSeconds: 30);
        return result.Success
            ? ProviderResult.Ok($"Plugin '{plugin.Id}' installed into deepseek-harness")
            : ProviderResult.Fail($"deepseek-harness plugin install failed (dev preview - its CLI surface may have changed): {result.Output}");
    }

    public Task<ProviderResult> RegisterMcpToolAsync(McpToolManifest tool) =>
        Task.FromResult(ProviderResult.Fail("deepseek-harness MCP registration is not wired up here - use its own web UI/config."));

    public async Task<ISessionHandle> LaunchSessionAsync(SessionLaunchOptions options)
    {
        var exe = ExecutableLocators.FindDeepSeekHarness()
                  ?? throw new InvalidOperationException("deepseek-harness (dsh) not found - install with `npm i -g @deepseek-ai/dsh` first.");

        SessionHandoffExporter.Export(options.ProjectPath);
        await TryInstallSkillsAsNativePluginAsync(exe, options.ProjectPath);

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

        return new ProcessSessionHandle(Name, options.ProjectPath, process, watchForRateLimit: false);
    }

    /// <summary>
    /// Packages the project's Claude skills as a minimal local pnpm package
    /// (a real package.json - confirmed required, see class summary) and
    /// adds it into the "web" profile dsh actually launches, via `dsh plugin
    /// --profile web add &lt;path&gt;` (== `pnpm add &lt;path&gt;` in that profile
    /// dir). Best-effort: wrapped so a pnpm/dsh failure (missing profile,
    /// CLI surface changed, etc.) never blocks the launch - ExternalCommandRunner
    /// already reports failure via CommandResult rather than throwing, and this
    /// only guards the local file-write step around it.
    /// </summary>
    private async Task TryInstallSkillsAsNativePluginAsync(string exe, string projectDirectory)
    {
        try
        {
            var skillsDigest = SessionHandoffExporter.GetAvailableSkillsDigest(projectDirectory);
            if (string.IsNullOrWhiteSpace(skillsDigest)) return;

            var pluginDir = Path.Combine(projectDirectory, ".claude-handoff", "deepseek-harness-skills");
            Directory.CreateDirectory(pluginDir);

            var packageJson = new JsonObject
            {
                ["name"] = "claude-code-skills",
                ["version"] = "1.0.0",
                ["private"] = true,
                ["description"] = "Claude Code skills available for this project.",
            };
            await File.WriteAllTextAsync(Path.Combine(pluginDir, "package.json"), packageJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            await File.WriteAllTextAsync(Path.Combine(pluginDir, "SKILLS.md"), skillsDigest);

            await ExternalCommandRunner.RunAsync(exe, $"plugin --profile web add \"{pluginDir}\"", timeoutSeconds: 30);
        }
        catch (IOException)
        {
            // Best effort - dev preview, never block the launch on this.
        }
    }
}
