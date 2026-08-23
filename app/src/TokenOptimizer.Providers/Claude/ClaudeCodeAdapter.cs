using System.Diagnostics;
using TokenOptimizer.Core.Diagnostics;
using TokenOptimizer.Providers.Fallback;
using TokenOptimizer.Providers.Manifests;
using TokenOptimizer.Sandbox;

namespace TokenOptimizer.Providers.Claude;

/// <summary>
/// Real, fully-wired adapter for Claude Code - not a stub. Skills are
/// materialized as native SKILL.md + asset files under ~/.claude/skills;
/// plugins go through `claude plugin marketplace add` + `claude plugin
/// install &lt;id&gt;@&lt;marketplace&gt; --scope &lt;scope&gt;`, verified against
/// `claude plugin list` rather than trusting the install command's own exit
/// code (which can report success on a silent no-op) - the same pattern
/// LLM-TokenOptimizer.ps1's Install-ClaudeCodeSetupPlugin/Test-ClaudePluginInstalled used.
/// </summary>
public sealed class ClaudeCodeAdapter : IProviderAdapter
{
    private readonly ClaudeExecutableLocator _locator;
    private readonly CommandAvailability _availability;
    private SandboxSessionLauncher? _sandboxLauncher;

    public ClaudeCodeAdapter(ClaudeExecutableLocator locator, CommandAvailability availability,
        SandboxSessionLauncher? sandboxLauncher = null)
    {
        _locator = locator;
        _availability = availability;
        _sandboxLauncher = sandboxLauncher;
    }

    public string Name => "Claude Code";

    public async Task<bool> IsAvailableAsync() => await _locator.FindAsync() is not null;

    public async Task<IReadOnlyList<string>> ListInstalledSkillsAsync()
    {
        var claudeHome = GetClaudeHome();
        var skillsDir = Path.Combine(claudeHome, "skills");
        if (!Directory.Exists(skillsDir)) return Array.Empty<string>();
        return Directory.EnumerateDirectories(skillsDir)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<string>> ListInstalledPluginsAsync()
    {
        var exe = await _locator.FindAsync();
        if (exe is null) return Array.Empty<string>();

        var result = await ExternalCommandRunner.RunAsync(exe, "plugin list", timeoutSeconds: 15);
        if (!result.Success) return Array.Empty<string>();

        return result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    public Task<ProviderResult> InstallSkillAsync(SkillManifest skill)
    {
        var skillsDir = Path.Combine(GetClaudeHome(), "skills", skill.Id);
        Directory.CreateDirectory(skillsDir);

        var header = $"---\nname: {skill.Id}\ndescription: {skill.Description}\n---\n\n# {skill.DisplayName}\n\n{skill.TriggerHint}\n\n{skill.BodyMarkdown}\n";
        File.WriteAllText(Path.Combine(skillsDir, "SKILL.md"), header);

        foreach (var asset in skill.Assets)
        {
            var assetPath = Path.Combine(skillsDir, asset.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
            File.WriteAllText(assetPath, asset.Content);
        }

        return Task.FromResult(ProviderResult.Ok($"Skill '{skill.Id}' installed to {skillsDir}"));
    }

    public async Task<ProviderResult> InstallPluginAsync(PluginManifest plugin)
    {
        var exe = await _locator.FindAsync();
        if (exe is null) return ProviderResult.Fail("Claude Code executable not found");

        if (plugin.Source == PluginSource.Marketplace)
        {
            var marketplace = plugin.SourceLocator;
            await ExternalCommandRunner.RunAsync(exe, $"plugin marketplace add {marketplace}", timeoutSeconds: 30);

            var installResult = await ExternalCommandRunner.RunAsync(
                exe, $"plugin install {plugin.Id}@{ExtractMarketplaceName(marketplace)} --scope {plugin.Scope}", timeoutSeconds: 60);

            var reportedSuccess = installResult.Success || installResult.Output.Contains("already installed", StringComparison.OrdinalIgnoreCase);
            var confirmed = await VerifyPluginInstalledAsync(exe, plugin.Id, reportedSuccess);
            return confirmed
                ? ProviderResult.Ok($"Plugin '{plugin.Id}' installed")
                : ProviderResult.Fail($"Plugin '{plugin.Id}' install did not confirm success: {installResult.Output}");
        }

        return ProviderResult.Fail($"Plugin source '{plugin.Source}' is not yet supported for Claude Code");
    }

    public async Task<ProviderResult> RegisterMcpToolAsync(McpToolManifest tool)
    {
        var exe = await _locator.FindAsync();
        if (exe is null) return ProviderResult.Fail("Claude Code executable not found");

        var envArgs = string.Join(' ', tool.Environment.Select(kv => $"--env {kv.Key}={kv.Value}"));
        var argList = string.Join(' ', tool.Arguments);
        var args = $"mcp add {tool.Id} --scope {tool.Scope} {envArgs} -- {tool.Command} {argList}".Trim();

        var result = await ExternalCommandRunner.RunAsync(exe, args, timeoutSeconds: 30);
        return result.Success
            ? ProviderResult.Ok($"MCP tool '{tool.Id}' registered")
            : ProviderResult.Fail($"MCP tool '{tool.Id}' registration failed: {result.Output}");
    }

    public ClaudeLaunchEnvironment BuildLaunchEnvironment(SessionLaunchOptions options)
    {
        var builder = new ClaudeLaunchEnvironmentBuilder()
            .WithModel(options.Model)
            .WithResumeMode(options.ResumeMode)
            .WithClaudeMemIsolation();
        if (options.IsolateConfig)
        {
            builder.WithIsolatedConfig(options.ProjectPath);
        }
        return builder.Build();
    }

    public async Task<ISessionHandle> LaunchSessionAsync(SessionLaunchOptions options)
    {
        var exe = await _locator.FindAsync()
                  ?? throw new InvalidOperationException("Claude Code executable not found - install it first.");

        await RefreshPluginMarketplacesAsync(exe);

        var launchEnv = BuildLaunchEnvironment(options);
        return await SandboxLauncher().LaunchAsync(Name, SandboxSessionLauncher.ToLinuxCommand(exe, launchEnv.Arguments), options);
    }

    /// <summary>Lazily built default launcher (real OpenSandbox runtime + configured settings) when no launcher was injected.</summary>
    private SandboxSessionLauncher SandboxLauncher() =>
        _sandboxLauncher ??= new SandboxSessionLauncher(new OpenSandboxSdkRuntime(new SandboxSettings()), new SandboxSettings());

    private static string GetClaudeHome() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>
    /// Runs `claude plugin marketplace update` before every launch instead of
    /// relying on the user to notice a "needs attention" badge and type
    /// /reload-plugins by hand - skills live inside plugin manifests, so
    /// refreshing marketplaces is also how a skill's own content gets picked
    /// up after it changes. Best-effort and silent on failure/timeout.
    /// Skips the call when claudeExe is a node.exe wrapper because the wrapper
    /// does not implement the plugin marketplace subcommand consistently.
    /// </summary>
    public static async Task RefreshPluginMarketplacesAsync(string claudeExe)
    {
        await RefreshPluginMarketplacesAsync(claudeExe, ExternalCommandRunner.RunAsync);
    }

    internal static async Task RefreshPluginMarketplacesAsync(
        string claudeExe,
        Func<string, string, string?, int, IReadOnlyDictionary<string, string>?, CancellationToken, Task<CommandResult>> runner)
    {
        if (claudeExe.EndsWith("node.exe", StringComparison.OrdinalIgnoreCase)) return;
        await runner(claudeExe, "plugin marketplace update", null, 20, null, default);
    }

    internal static string ExtractMarketplaceName(string marketplaceLocator)
    {
        var slashIndex = marketplaceLocator.LastIndexOf('/');
        return slashIndex >= 0 ? marketplaceLocator[(slashIndex + 1)..] : marketplaceLocator;
    }

    private static async Task<bool> VerifyPluginInstalledAsync(string exe, string pluginId, bool installReportedSuccess)
    {
        var result = await ExternalCommandRunner.RunAsync(exe, "plugin list", timeoutSeconds: 15);
        if (!result.Success) return installReportedSuccess;
        return result.Output.Contains(pluginId, StringComparison.OrdinalIgnoreCase);
    }
}
