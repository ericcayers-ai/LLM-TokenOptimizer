namespace TokenOptimizer.Core.Diagnostics;

/// <summary>One isolated Claude profile directory (see IsolatedClaudeProfileService) - a full copy of ~/.claude's settings/history/skills, seeded once per distinct project path and never previously cleaned up.</summary>
public sealed record CacheEntry(string Name, string Path, long SizeBytes, DateTime LastWriteUtc);

/// <summary>
/// Reports on and clears the one cache this app grows without bound:
/// %AppData%\TokenOptimizer\claude-profiles\&lt;project-slug&gt;, one full
/// directory per distinct project ever launched with -IsolateClaudeConfig
/// (see IsolatedClaudeProfileService). Nothing previously read, sized, or
/// pruned this directory - a user who isolates many projects over time had
/// no way to see or reclaim the space short of deleting it by hand.
///
/// Every method takes an optional profilesRoot override (defaulting to the
/// real %AppData% path) rather than reading Environment.SpecialFolder
/// directly - .NET's GetFolderPath resolves that once via the Windows API
/// and does not honor a runtime-changed APPDATA env var, so a hardcoded path
/// here would be untestable without touching the real user profile.
/// </summary>
public static class CacheManagementService
{
    public static string DefaultProfilesRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenOptimizer", "claude-profiles");

    public static IReadOnlyList<CacheEntry> ListClaudeProfiles(string? profilesRoot = null)
    {
        var root = profilesRoot ?? DefaultProfilesRoot;
        if (!Directory.Exists(root)) return Array.Empty<CacheEntry>();

        var entries = new List<CacheEntry>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();
            var size = SumSizes(files);
            var lastWrite = files.Count > 0
                ? files.Max(f => File.GetLastWriteTimeUtc(f))
                : Directory.GetLastWriteTimeUtc(dir);
            entries.Add(new CacheEntry(Path.GetFileName(dir), dir, size, lastWrite));
        }

        return entries.OrderByDescending(e => e.SizeBytes).ToList();
    }

    public static long TotalClaudeProfilesSizeBytes(string? profilesRoot = null) =>
        ListClaudeProfiles(profilesRoot).Sum(e => e.SizeBytes);

    public static void DeleteProfile(string name, string? profilesRoot = null)
    {
        var dir = Path.Combine(profilesRoot ?? DefaultProfilesRoot, name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>Deletes profiles whose newest file is older than maxAge. Returns how many were removed.</summary>
    public static int DeleteStaleProfiles(TimeSpan maxAge, string? profilesRoot = null)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var removed = 0;
        foreach (var entry in ListClaudeProfiles(profilesRoot))
        {
            if (entry.LastWriteUtc >= cutoff) continue;
            DeleteProfile(entry.Name, profilesRoot);
            removed++;
        }
        return removed;
    }

    public static void ClearAllProfiles(string? profilesRoot = null)
    {
        var root = profilesRoot ?? DefaultProfilesRoot;
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static long SumSizes(IEnumerable<string> files)
    {
        long total = 0;
        foreach (var f in files)
        {
            try { total += new FileInfo(f).Length; }
            catch (IOException) { /* file removed/locked mid-scan - skip it */ }
        }
        return total;
    }
}
