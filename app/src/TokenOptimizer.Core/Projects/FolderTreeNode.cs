namespace TokenOptimizer.Core.Projects;

/// <summary>One node of a master folder's recursive subdirectory tree (see MasterFolderService.BuildSubdirectoryTreeAsync).</summary>
public sealed record FolderTreeNode(string Name, string FullPath, IReadOnlyList<FolderTreeNode> Children);
