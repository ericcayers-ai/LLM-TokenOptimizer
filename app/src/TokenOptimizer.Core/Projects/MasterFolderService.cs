using TokenOptimizer.Core.Config;

namespace TokenOptimizer.Core.Projects;

/// <summary>
/// The multi-project launcher: a master folder whose immediate subfolders
/// are each a candidate project, opened one independent session per folder.
/// Ports Read-MasterFolder/Test-MasterFolder/Show-ProjectMenu/Select-Projects/
/// New-ProjectFolder's data layer (selection/menu rendering is the GUI's
/// own concern now, not a console picker).
/// </summary>
public sealed class MasterFolderService
{
    private readonly ConfigStore _configStore;
    private readonly ProjectHistoryService _projectHistory;

    public MasterFolderService(ConfigStore configStore, ProjectHistoryService projectHistory)
    {
        _configStore = configStore;
        _projectHistory = projectHistory;
    }

    public async Task<string?> GetMasterFolderAsync()
    {
        var config = await _configStore.LoadAsync();
        return config.MasterFolder;
    }

    public async Task SetMasterFolderAsync(string path)
    {
        var config = await _configStore.LoadAsync();
        config.MasterFolder = Path.GetFullPath(path);
        await _configStore.SaveAsync(config);
    }

    public static bool IsValidMasterFolder(string path, out string? error)
    {
        if (string.IsNullOrWhiteSpace(path)) { error = "Path cannot be blank"; return false; }
        if (!Directory.Exists(path)) { error = $"Not a directory: {path}"; return false; }
        error = null;
        return true;
    }

    /// <summary>Immediate subfolders of the master folder that pass the same write-permission check as any other project.</summary>
    public async Task<IReadOnlyList<ProjectCandidate>> ListCandidatesAsync(string masterFolder)
    {
        var history = await _projectHistory.GetHistoryAsync();
        var knownPaths = history.Select(p => p.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = new List<ProjectCandidate>();
        foreach (var dir in Directory.EnumerateDirectories(masterFolder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            if (!ProjectHistoryService.IsValidProjectDirectory(dir, out _)) continue;
            var fullPath = Path.GetFullPath(dir);
            candidates.Add(new ProjectCandidate(fullPath, Path.GetFileName(dir), knownPaths.Contains(fullPath)));
        }

        return candidates;
    }

    /// <summary>
    /// Recursive subdirectory tree of the master folder, for browsing (rather
    /// than just the flat "projects" list ListCandidatesAsync produces) -
    /// depth-capped and skipping hidden/reparse-point directories so a huge
    /// or symlink-looped tree can't hang the UI.
    /// </summary>
    public static Task<FolderTreeNode> BuildSubdirectoryTreeAsync(string masterFolder, int maxDepth = 4) =>
        Task.Run(() => BuildNode(masterFolder, Path.GetFileName(masterFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), maxDepth));

    private static FolderTreeNode BuildNode(string path, string name, int depthRemaining)
    {
        if (depthRemaining <= 0) return new FolderTreeNode(name, path, Array.Empty<FolderTreeNode>());

        var children = new List<FolderTreeNode>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(path).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var info = new DirectoryInfo(dir);
                if (info.Attributes.HasFlag(FileAttributes.Hidden) || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                children.Add(BuildNode(dir, info.Name, depthRemaining - 1));
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        return new FolderTreeNode(name, path, children);
    }

    /// <summary>Creates a new empty subfolder directly inside the master folder, ready to be opened as a project.</summary>
    public static string? CreateProjectFolder(string masterFolder, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var invalidChars = Path.GetInvalidFileNameChars();
        if (name.Any(c => invalidChars.Contains(c)) || name is "." or "..") return null;

        var newPath = Path.Combine(masterFolder, name);
        if (Directory.Exists(newPath)) return newPath;

        try
        {
            Directory.CreateDirectory(newPath);
            return newPath;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
