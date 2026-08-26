using TokenOptimizer.Core.Diagnostics;

namespace TokenOptimizer.Providers.FreeToken;

/// <summary>
/// Locates the FreeToken desktop engine (github.com/FlashML-org/FreeToken,
/// Windows build from flashml.ai). The PyPI `freetoken[accel]` package only
/// ships Linux wheels (triton has no win_amd64 build), so on Windows the
/// desktop installer is the only runtime - it lands in
/// %LOCALAPPDATA%\FreeToken Desktop\freetoken-desktop.exe (per-user NSIS
/// silent install), with Program Files locations probed for machine-wide
/// installs before falling back to PATH.
/// </summary>
public static class FreeTokenLocator
{
    /// <summary>Default serve address from FreeToken's own docs (docs/cli.md).</summary>
    public const string DefaultBaseUrl = "http://127.0.0.1:1919";

    public static string? FindDesktopApp()
    {
        var onPath = new CommandAvailability().ResolveOnPath(
            OperatingSystem.IsWindows() ? "freetoken-desktop.exe" : "freetoken-desktop");
        if (onPath is not null) return onPath;

        if (!OperatingSystem.IsWindows()) return null;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var candidateDirs = new[]
        {
            Path.Combine(localAppData, "FreeToken Desktop"),
            Path.Combine(localAppData, "Programs", "FreeToken"),
            Path.Combine(localAppData, "Programs", "freetoken"),
            Path.Combine(programFiles, "FreeToken"),
            Path.Combine(programFilesX86, "FreeToken"),
        };

        var candidateExes = new[] { "freetoken-desktop.exe", "FreeToken.exe", "freetoken.exe" };

        foreach (var dir in candidateDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var exe in candidateExes)
            {
                var full = Path.Combine(dir, exe);
                if (File.Exists(full)) return full;
            }
        }

        return null;
    }
}
