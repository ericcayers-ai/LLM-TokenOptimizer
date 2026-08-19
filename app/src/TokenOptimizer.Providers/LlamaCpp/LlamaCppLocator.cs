using TokenOptimizer.Core.Diagnostics;

namespace TokenOptimizer.Providers.LlamaCpp;

/// <summary>Finds the `unsloth` CLI (unsloth.ai/docs/integrations/unsloth-start) - TokenOptimizer drives local GGUF models through it rather than managing llama-server directly.</summary>
public static class LlamaCppLocator
{
    public static string? Find()
    {
        var onPath = new CommandAvailability().ResolveOnPath(OperatingSystem.IsWindows() ? "unsloth.exe" : "unsloth");
        if (onPath is not null) return onPath;

        var exeName = OperatingSystem.IsWindows() ? "unsloth.cmd" : "unsloth";
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var npmCandidate = Path.Combine(roaming, "npm", exeName);
        return File.Exists(npmCandidate) ? npmCandidate : null;
    }
}
