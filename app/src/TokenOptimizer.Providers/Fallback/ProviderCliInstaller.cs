using TokenOptimizer.Core.Diagnostics;
using TokenOptimizer.Providers.LlamaCpp;

namespace TokenOptimizer.Providers.Fallback;

/// <summary>
/// Auto-installs the fallback-chain providers' CLI binaries from inside the
/// app, mirroring CompanionToolingInstaller's pattern (idempotent - checks
/// ExecutableLocators first, installs only if missing). Codex goes through
/// npm (its documented install path); Antigravity and Cursor each ship
/// their own official PowerShell/curl-style installer script, run
/// non-interactively here exactly as their vendors document.
/// </summary>
public sealed class ProviderCliInstaller
{
    public async Task<bool> InstallJcodeCliAsync()
    {
        if (ExecutableLocators.FindJcode() is not null) return true;
        await ExternalCommandRunner.RunAsync(
            "powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"irm https://jcode.sh/install.ps1 | iex\"",
            timeoutSeconds: 180);
        return ExecutableLocators.FindJcode() is not null;
    }

    public async Task<bool> InstallAntigravityCliAsync()
    {
        if (ExecutableLocators.FindAntigravity() is not null) return true;
        await ExternalCommandRunner.RunAsync(
            "powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"irm https://antigravity.google/cli/install.ps1 | iex\"",
            timeoutSeconds: 180);
        return ExecutableLocators.FindAntigravity() is not null;
    }

    /// <summary>ccusage: local, offline CLI reporting token/cost usage across Claude Code, Codex, Gemini CLI, Copilot, and more - no proxy, no MCP server, reads the same session logs already on disk. github.com/ryoppippi/ccusage.</summary>
    public async Task<bool> InstallCcusageAsync()
    {
        if (new CommandAvailability().ResolveOnPath("ccusage") is not null) return true;
        await ExternalCommandRunner.RunAsync("npm", "install -g ccusage", timeoutSeconds: 180);
        return new CommandAvailability().ResolveOnPath("ccusage") is not null;
    }

    /// <summary>@deepseek-ai/dsh is dev preview - install is best-effort; a failure here just means "still manual only", not a crash.</summary>
    public async Task<bool> InstallDeepSeekHarnessCliAsync()
    {
        if (ExecutableLocators.FindDeepSeekHarness() is not null) return true;
        await ExternalCommandRunner.RunAsync("npm", "install -g @deepseek-ai/dsh", timeoutSeconds: 180);
        return ExecutableLocators.FindDeepSeekHarness() is not null;
    }

    /// <summary>Unsloth ships as a pip package, not npm - `unsloth start` is what powers the always-available local-model fallback (see LlamaCppAdapter), so this is offered as a first-class install step, not an afterthought.</summary>
    public async Task<bool> InstallUnslothCliAsync()
    {
        if (LlamaCppLocator.Find() is not null) return true;
        await ExternalCommandRunner.RunAsync("pip", "install --upgrade unsloth", timeoutSeconds: 600);
        return LlamaCppLocator.Find() is not null;
    }

    /// <summary>OpenCode's own standalone TUI/agent CLI (opencode-ai on npm) - not required by OpenCodeAdapter (which talks to the OpenCode Go gateway directly through Claude Code), but installable here so users who also want the native OpenCode agent don't have to leave the app.</summary>
    public async Task<bool> InstallOpenCodeCliAsync()
    {
        if (ExecutableLocators.FindOpenCode() is not null) return true;
        await ExternalCommandRunner.RunAsync("npm", "install -g opencode-ai", timeoutSeconds: 180);
        return ExecutableLocators.FindOpenCode() is not null;
    }

    public async Task<bool> InstallCursorCliAsync()
    {
        if (ExecutableLocators.FindCursor() is not null) return true;
        await ExternalCommandRunner.RunAsync(
            "powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"irm 'https://cursor.com/install?win32=true' | iex\"",
            timeoutSeconds: 180);
        return ExecutableLocators.FindCursor() is not null;
    }

    /// <summary>
    /// Actual login verification, not a stored flag the user set by clicking
    /// a button once - agy has no dedicated whoami/status subcommand, so
    /// `agy models` (which requires auth to succeed) is used as a live proxy:
    /// exit 0 means a signed-in session is actually working right now.
    /// </summary>
    public async Task<bool> IsAntigravityLoggedInAsync()
    {
        var exe = ExecutableLocators.FindAntigravity();
        if (exe is null) return false;

        var result = await RunCheckAsync(exe, "models");
        return result.Success;
    }

    /// <summary>Real login verification via `cursor-agent status`, which prints "Logged in as &lt;email&gt;" only when actually authenticated.</summary>
    public async Task<bool> IsCursorLoggedInAsync()
    {
        var exe = ExecutableLocators.FindCursor();
        if (exe is null) return false;

        var result = await RunCheckAsync(exe, "status");
        return result.Success && result.Output.Contains("Logged in", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Same .cmd-wrapper handling as ProcessLaunchHelper, but for a captured-output check instead of an interactive launch.</summary>
    private static Task<CommandResult> RunCheckAsync(string exePath, string arguments)
    {
        var isScript = exePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        return isScript
            ? ExternalCommandRunner.RunAsync("cmd.exe", $"/c \"\"{exePath}\" {arguments}\"", timeoutSeconds: 20)
            : ExternalCommandRunner.RunAsync(exePath, arguments, timeoutSeconds: 20);
    }

    /// <summary>
    /// Plugin parity for Antigravity: `agy plugin install &lt;local-directory&gt;`
    /// natively understands Claude Code's plugin folder format (verified live -
    /// it tags the import "source": "claude-code" and pulls in skills/agents/
    /// hooks), so every plugin `claude plugin install` has already cached
    /// locally under ~/.claude/plugins/cache/&lt;marketplace&gt;/&lt;plugin&gt; gets handed
    /// to Antigravity's own native plugin system the same way - no hardcoded
    /// plugin list to keep in sync, whatever Claude has installed gets mirrored.
    /// Idempotent: re-installing an already-imported plugin just re-syncs it.
    /// </summary>
    public async Task<int> SyncClaudePluginsIntoAntigravityAsync()
    {
        var agyExe = ExecutableLocators.FindAntigravity();
        if (agyExe is null) return 0;

        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "plugins", "cache");
        if (!Directory.Exists(cacheDir)) return 0;

        var installed = 0;

        // ~/.claude/plugins/cache/<marketplace>/<plugin>/<commit-hash>/.claude-plugin/plugin.json
        // - depth varies (some marketplaces add a commit-hash layer, some
        // don't), so search for plugin.json directly rather than assume a
        // fixed depth; the plugin root is always two levels up from it.
        foreach (var manifestPath in Directory.EnumerateFiles(cacheDir, "plugin.json", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(Path.GetDirectoryName(manifestPath)) != ".claude-plugin") continue;
            var pluginRoot = Path.GetDirectoryName(Path.GetDirectoryName(manifestPath));
            if (pluginRoot is null) continue;

            var result = await ExternalCommandRunner.RunAsync(agyExe, $"plugin install \"{pluginRoot}\"", timeoutSeconds: 30);
            if (result.Success) installed++;
        }

        // The impeccable skill is git-cloned separately (its own repo IS a
        // Claude-format plugin), not routed through the marketplace cache.
        var impeccableDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills", "impeccable", "plugin");
        if (File.Exists(Path.Combine(impeccableDir, ".claude-plugin", "plugin.json")))
        {
            var result = await ExternalCommandRunner.RunAsync(agyExe, $"plugin install \"{impeccableDir}\"", timeoutSeconds: 30);
            if (result.Success) installed++;
        }

        return installed;
    }

    /// <summary>
    /// deepseek-harness ("Everything is a Plugin") is itself plugin-based,
    /// same one-directional Claude-&gt;target mirroring as Antigravity above.
    /// Dev preview - `dsh plugin install &lt;path&gt;` is this session's best
    /// understanding of its CLI surface, not a documented stable contract;
    /// each install call fails soft (ExternalCommandRunner never throws, it
    /// reports Success=false) so a breaking CLI change degrades to "0
    /// synced" rather than crashing the app.
    /// </summary>
    public async Task<int> SyncClaudePluginsIntoDeepSeekHarnessAsync()
    {
        var dshExe = ExecutableLocators.FindDeepSeekHarness();
        if (dshExe is null) return 0;

        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "plugins", "cache");
        if (!Directory.Exists(cacheDir)) return 0;

        var installed = 0;
        foreach (var manifestPath in Directory.EnumerateFiles(cacheDir, "plugin.json", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(Path.GetDirectoryName(manifestPath)) != ".claude-plugin") continue;
            var pluginRoot = Path.GetDirectoryName(Path.GetDirectoryName(manifestPath));
            if (pluginRoot is null) continue;

            var result = await ExternalCommandRunner.RunAsync(dshExe, $"plugin install \"{pluginRoot}\"", timeoutSeconds: 30);
            if (result.Success) installed++;
        }

        return installed;
    }
}
