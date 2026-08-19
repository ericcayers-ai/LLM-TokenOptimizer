using TokenOptimizer.Core.Diagnostics;

namespace TokenOptimizer.Providers.LlamaCpp;

/// <summary>
/// Finds the `unsloth` CLI (unsloth.ai/docs/integrations/unsloth-start) -
/// TokenOptimizer drives local GGUF models through it rather than managing
/// llama-server directly. Unsloth ships as a pip package (`pip install
/// unsloth`), not npm - its console-script entry point lands in the active
/// Python installation's Scripts/ folder, which isn't always on PATH, so
/// this probes the same install locations PythonLocator already knows
/// about (site-installer and `pip install --user`) after the PATH check.
/// </summary>
public static class LlamaCppLocator
{
    public static string? Find()
    {
        var onPath = new CommandAvailability().ResolveOnPath(OperatingSystem.IsWindows() ? "unsloth.exe" : "unsloth");
        if (onPath is not null) return onPath;

        if (!OperatingSystem.IsWindows()) return null;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        var scriptsRoots = new List<string>();
        foreach (var root in new[] { Path.Combine(localAppData, "Programs", "Python"), programFiles })
        {
            if (!Directory.Exists(root)) continue;
            scriptsRoots.AddRange(Directory.EnumerateDirectories(root, "Python3*")
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(dir => Path.Combine(dir, "Scripts")));
        }

        // `pip install --user` on Windows lands under %APPDATA%\Python\PythonXY\Scripts.
        var userPythonRoot = Path.Combine(roaming, "Python");
        if (Directory.Exists(userPythonRoot))
        {
            scriptsRoots.AddRange(Directory.EnumerateDirectories(userPythonRoot, "Python3*")
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(dir => Path.Combine(dir, "Scripts")));
        }

        return scriptsRoots
            .Select(scripts => Path.Combine(scripts, "unsloth.exe"))
            .FirstOrDefault(File.Exists);
    }
}
