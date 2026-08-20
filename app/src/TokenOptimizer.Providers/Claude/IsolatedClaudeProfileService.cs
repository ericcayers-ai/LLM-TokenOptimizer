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

    /// <summary>Returns the isolated CLAUDE_CONFIG_DIR path for a project without creating it.</summary>
    public static string GetProfileDirPath(string projectDirectory)
    {
        var slug = PathSlug.For(projectDirectory);
        var profileRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenOptimizer", "claude-profiles");
        return Path.Combine(profileRoot, slug);
    }

    /// <summary>Creates (if needed) and returns the isolated CLAUDE_CONFIG_DIR path for a project.</summary>
    public static string GetOrCreateProfileDir(string projectDirectory)
    {
        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return GetOrCreateProfileDir(projectDirectory, source);
    }

    internal static string GetOrCreateProfileDir(string projectDirectory, string sourceClaudeConfigDir)
    {
        var profileDir = GetProfileDirPath(projectDirectory);
        var created = !Directory.Exists(profileDir);
        if (created)
        {
            Directory.CreateDirectory(profileDir);
        }

        if (Directory.Exists(sourceClaudeConfigDir))
        {
            if (created)
            {
                foreach (var leaf in SeedLeaves)
                {
                    var src = Path.Combine(sourceClaudeConfigDir, leaf);
                    var dst = Path.Combine(profileDir, leaf);
                    try
                    {
                        if (File.Exists(src)) File.Copy(src, dst, overwrite: true);
                        else if (Directory.Exists(src)) CopyDirectory(src, dst);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort seed */ }
                }
            }

            // Re-sync mutable shared state on every launch so skills and plugin
            // config added after the profile was first created are not stranded
            // in the real ~/.claude while the isolated profile drifts behind.
            ReSyncDirectory(Path.Combine(sourceClaudeConfigDir, "skills"), Path.Combine(profileDir, "skills"));
            ReSyncFile(
                Path.Combine(sourceClaudeConfigDir, "plugins", "installed_plugins.json"),
                Path.Combine(profileDir, "plugins", "installed_plugins.json"));
        }

        return profileDir;
    }

    private static void ReSyncFile(string sourceFile, string destFile)
    {
        try
        {
            if (File.Exists(sourceFile))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                File.Copy(sourceFile, destFile, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private static void ReSyncDirectory(string sourceDir, string destDir)
    {
        if (!Directory.Exists(sourceDir)) return;
        try
        {
            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, file);
                if (IsGitInternal(relative)) continue;
                var destPath = Path.Combine(destDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                File.Copy(file, destPath, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private static bool IsGitInternal(string relativePath) =>
        relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase));

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            if (IsGitInternal(relative)) continue;
            var destPath = Path.Combine(destDir, relative);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                File.Copy(file, destPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort per file */ }
        }
    }
}
