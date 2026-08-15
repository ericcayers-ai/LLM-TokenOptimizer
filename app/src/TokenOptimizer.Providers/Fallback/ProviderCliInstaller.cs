using TokenOptimizer.Core.Diagnostics;

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
    public async Task<bool> InstallCodexCliAsync()
    {
        if (ExecutableLocators.FindCodex() is not null) return true;
        await ExternalCommandRunner.RunAsync("npm", "install -g @openai/codex", timeoutSeconds: 180);
        return ExecutableLocators.FindCodex() is not null;
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
}
