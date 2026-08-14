using System.Runtime.Versioning;
using Microsoft.Win32;

namespace TokenOptimizer.Core.Diagnostics;

/// <summary>
/// Re-syncs this process's in-memory PATH from the registry (User + Machine
/// Environment keys) and appends a handful of well-known install
/// directories that commonly aren't on PATH yet immediately after an
/// installer runs (npm global, Python user Scripts, python.org install
/// dirs). Ported from Sync-ProcessPathFromRegistry/Add-StandardPaths/
/// Add-PythonUserScriptsToPath - without this, a tool installed moments ago
/// (by this app's own winget/pip install calls, or by hand) can report
/// "not found" until the process restarts, since a launched child process
/// only ever inherits PATH as it was when THIS process started.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PathRefresher
{
    public static void Refresh()
    {
        try
        {
            var userPath = Registry.GetValue(@"HKEY_CURRENT_USER\Environment", "Path", null) as string ?? string.Empty;
            var machinePath = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment", "Path", null) as string ?? string.Empty;

            var current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var merged = string.Join(Path.PathSeparator, new[] { machinePath, userPath, current }
                .SelectMany(p => p.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase));

            Environment.SetEnvironmentVariable("PATH", merged);
        }
        catch
        {
            // Best effort - a registry read failure must never block startup.
        }

        AppendIfMissing(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        AppendIfMissing(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming", "Python", "Scripts"));
        AppendIfMissing(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python"));
    }

    private static void AppendIfMissing(string dir)
    {
        if (!Directory.Exists(dir)) return;
        var current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (current.Contains(dir, StringComparison.OrdinalIgnoreCase)) return;
        Environment.SetEnvironmentVariable("PATH", current + Path.PathSeparator + dir);
    }
}
