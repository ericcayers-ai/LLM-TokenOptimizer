using System.Diagnostics;

namespace TokenOptimizer.Providers.Fallback;

/// <summary>
/// Several CLI installers (npm's Codex package, Cursor's official installer)
/// ship their entry point as a .cmd wrapper script, not a native .exe.
/// Windows' CreateProcess - what Process.Start uses under UseShellExecute
/// = false - cannot launch a .cmd/.bat directly (ERROR_BAD_EXE_FORMAT); it
/// needs cmd.exe /c to interpret it. Every adapter routes its launch
/// through here so this quirk is handled in exactly one place.
/// </summary>
public static class ProcessLaunchHelper
{
    public static Process? Start(string exePath, string arguments, string? workingDirectory, IReadOnlyDictionary<string, string>? environment = null)
    {
        var isScript = exePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                       || exePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

        var psi = isScript
            ? new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{exePath}\"{(string.IsNullOrEmpty(arguments) ? "" : " " + arguments)}\"",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
            }
            : new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
            };

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                psi.EnvironmentVariables[key] = value;
            }
        }

        return Process.Start(psi);
    }
}
