using TokenOptimizer.Core.Diagnostics;

namespace TokenOptimizer.Providers.Fallback;

/// <summary>
/// Confirmed install locations for the three fallback-chain tools, ported
/// from Find-AntigravityExecutable / Find-CodexExecutable / Find-CursorExecutable.
/// </summary>
public static class ExecutableLocators
{
    public static string? FindAntigravity()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new[]
        {
            Path.Combine(localAppData, "Programs", "Antigravity IDE", "Antigravity IDE.exe"),
            Path.Combine(localAppData, "Programs", "Antigravity", "Antigravity.exe"),
            Path.Combine(programFiles, "Google", "Antigravity", "Antigravity IDE.exe"),
            Path.Combine(programFiles, "Google", "Antigravity", "Antigravity.exe"),
            Path.Combine(localAppData, "agy", "bin", "agy.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        return new CommandAvailability().ResolveOnPath("agy");
    }

    public static string? FindCodex() => new CommandAvailability().ResolveOnPath("codex");

    public static string? FindCursor()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new[]
        {
            Path.Combine(localAppData, "Programs", "cursor", "Cursor.exe"),
            Path.Combine(programFiles, "Cursor", "Cursor.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        return new CommandAvailability().ResolveOnPath("cursor");
    }
}
