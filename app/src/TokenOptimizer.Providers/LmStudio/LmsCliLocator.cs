namespace TokenOptimizer.Providers.LmStudio;

/// <summary>
/// LM Studio's `lms` CLI isn't always on PATH even when installed - it ships
/// at a fixed per-user location once LM Studio has been launched at least
/// once. Ported from find_lms() in run_benchmarks.py.
/// </summary>
public static class LmsCliLocator
{
    public static string? Find()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, "lms.exe");
            if (File.Exists(candidate)) return candidate;
        }

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "bin", "lms.exe");
        return File.Exists(fallback) ? fallback : null;
    }
}
