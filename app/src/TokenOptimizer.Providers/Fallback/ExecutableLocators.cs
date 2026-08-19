using TokenOptimizer.Core.Diagnostics;

namespace TokenOptimizer.Providers.Fallback;

/// <summary>
/// Confirmed install locations for the fallback-chain tools' CLI binaries
/// only. Every provider routes through a single terminal session (Claude
/// Code, or its own CLI when it isn't API-redirectable) - GUI/IDE
/// executables are deliberately not resolved here anymore, so a
/// GUI-only install correctly reports as unavailable instead of popping a
/// separate app window.
/// </summary>
public static class ExecutableLocators
{
    public static string? FindAntigravity()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidate = Path.Combine(localAppData, "agy", "bin", "agy.exe");
        if (File.Exists(candidate)) return candidate;

        return new CommandAvailability().ResolveOnPath("agy");
    }

    public static string? FindCodex()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var candidate in new[] { Path.Combine(roaming, "npm", "codex.cmd"), Path.Combine(roaming, "npm", "codex") })
        {
            if (File.Exists(candidate)) return candidate;
        }

        return new CommandAvailability().ResolveOnPath("codex");
    }

    public static string? FindCursor()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidate = Path.Combine(localAppData, "cursor-agent", "cursor-agent.cmd");
        if (File.Exists(candidate)) return candidate;

        return new CommandAvailability().ResolveOnPath("cursor-agent");
    }

    /// <summary>deepseek-ai/deepseek-harness ("dsh") - published as @deepseek-ai/dsh on npm, dev preview.</summary>
    public static string? FindDeepSeekHarness()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var candidate in new[] { Path.Combine(roaming, "npm", "dsh.cmd"), Path.Combine(roaming, "npm", "dsh") })
        {
            if (File.Exists(candidate)) return candidate;
        }

        return new CommandAvailability().ResolveOnPath("dsh");
    }
}
