using TokenOptimizer.Core.Config;
using TokenOptimizer.Core.Models;

namespace TokenOptimizer.Core.Projects;

/// <summary>
/// Tracks recently-opened projects, most-recent-first, capped so the list
/// stays useful rather than growing forever.
/// </summary>
public sealed class ProjectHistoryService
{
    private const int MaxHistory = 25;
    private readonly ConfigStore _configStore;

    public ProjectHistoryService(ConfigStore configStore)
    {
        _configStore = configStore;
    }

    public async Task<IReadOnlyList<ProjectInfo>> GetHistoryAsync()
    {
        var config = await _configStore.LoadAsync();
        return config.ProjectHistory
            .Where(Directory.Exists)
            .Select(p => new ProjectInfo(p))
            .ToList();
    }

    public Task AddAsync(string projectPath)
    {
        var normalized = Path.GetFullPath(projectPath);
        return _configStore.UpdateAsync(config =>
        {
            config.ProjectHistory.RemoveAll(p => string.Equals(
                Path.GetFullPath(p), normalized, StringComparison.OrdinalIgnoreCase));
            config.ProjectHistory.Insert(0, normalized);
            if (config.ProjectHistory.Count > MaxHistory)
            {
                config.ProjectHistory = config.ProjectHistory.Take(MaxHistory).ToList();
            }
        });
    }

    /// <summary>
    /// Empty folders are valid projects - a brand-new folder or fresh clone
    /// target has nothing in it yet, and that's fine.
    /// </summary>
    public static bool IsValidProjectDirectory(string path, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(path)) { error = "Path cannot be blank"; return false; }
        if (!Directory.Exists(path)) { error = $"Not a directory: {path}"; return false; }
        if (System.Text.RegularExpressions.Regex.IsMatch(path, @"^[A-Za-z]:\\$"))
        {
            error = "Cannot use a drive root as a project";
            return false;
        }

        try
        {
            var testFile = Path.Combine(path, $".tokenoptimizer_perm_test_{Guid.NewGuid():N}".Substring(0, 32));
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
        }
        catch
        {
            error = "Missing write permissions";
            return false;
        }

        return true;
    }
}
