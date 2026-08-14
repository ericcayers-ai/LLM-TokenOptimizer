namespace TokenOptimizer.Core.Diagnostics;

/// <summary>
/// Several companion tools (headroom, RTK) ship as bash installers/hooks
/// with no native Windows path. Git for Windows - already a required
/// dependency - provides the bash.exe needed to run them.
/// </summary>
public static class GitBashLocator
{
    public static string? Find()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidates = new[]
        {
            Path.Combine(programFiles, "Git", "bin", "bash.exe"),
            Path.Combine(programFilesX86, "Git", "bin", "bash.exe"),
            Path.Combine(programFiles, "Git", "usr", "bin", "bash.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        return new CommandAvailability().ResolveOnPath("bash");
    }
}
