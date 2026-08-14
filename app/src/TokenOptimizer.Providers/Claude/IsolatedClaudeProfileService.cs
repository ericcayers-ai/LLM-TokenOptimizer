using TokenOptimizer.Core.Projects;

namespace TokenOptimizer.Providers.Claude;

/// <summary>
/// -IsolateClaudeConfig support: gives a project window its own
/// CLAUDE_CONFIG_DIR (separate settings, credentials, history, cache) so
/// concurrent windows never write the same Claude Code state file at once.
/// Seeded once from the real ~/.claude so MCP servers and personal settings
/// carry over instead of starting from nothing. Ported from
/// Initialize-IsolatedClaudeProfile.
/// </summary>
public sealed class IsolatedClaudeProfileService
{
    private static readonly string[] SeedLeaves = ["settings.json", "CLAUDE.md", "commands", "agents", "skills"];

    /// <summary>Creates (if needed) and returns the isolated CLAUDE_CONFIG_DIR path for a project.</summary>
    public static string GetOrCreateProfileDir(string projectDirectory)
    {
        var slug = PathSlug.For(projectDirectory);
        var profileRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenOptimizer", "claude-profiles");
        var profileDir = Path.Combine(profileRoot, slug);

        if (!Directory.Exists(profileDir))
        {
            Directory.CreateDirectory(profileDir);
            var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            if (Directory.Exists(source))
            {
                foreach (var leaf in SeedLeaves)
                {
                    var src = Path.Combine(source, leaf);
                    var dst = Path.Combine(profileDir, leaf);
                    try
                    {
                        if (File.Exists(src)) File.Copy(src, dst, overwrite: true);
                        else if (Directory.Exists(src)) CopyDirectory(src, dst);
                    }
                    catch (IOException) { /* best effort seed */ }
                }
            }
        }

        return profileDir;
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var destPath = Path.Combine(destDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(file, destPath, overwrite: true);
        }
    }
}
