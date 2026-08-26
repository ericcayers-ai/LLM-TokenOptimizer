using TokenOptimizer.Core.Diagnostics;

namespace TokenOptimizer.Providers.Hermes;

/// <summary>
/// Locates the Hermes Agent CLI (github.com/NousResearch/hermes-agent). The
/// reference install lives at %LOCALAPPDATA%\hermes\hermes-agent\venv\Scripts\
/// (shell-installer venv layout); a machine-wide or pipx-style install shows up
/// as plain "hermes" on PATH. Like every other harness CLI here, we launch the
/// real executable - never a copy - so skills/config/memory stay the user's own.
/// </summary>
public static class HermesLocator
{
    // Display name lives on HermesAgentAdapter.ProviderName ("Hermes Agent") -
    // single source of truth for the string the UI/CLI/docs all match on.

    public static string? Find()
    {
        var onPath = new CommandAvailability().ResolveOnPath(
            OperatingSystem.IsWindows() ? "hermes.exe" : "hermes");
        if (onPath is not null) return onPath;

        if (!OperatingSystem.IsWindows()) return null;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidateExes = new[]
        {
            Path.Combine(localAppData, "hermes", "hermes-agent", "venv", "Scripts", "hermes.exe"),
            Path.Combine(localAppData, "hermes", "venv", "Scripts", "hermes.exe"),
        };
        foreach (var candidate in candidateExes)
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
