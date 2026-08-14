namespace TokenOptimizer.Core.Projects;

/// <summary>A subfolder of the master folder eligible to be opened as a project, with whether it's been opened before.</summary>
public sealed record ProjectCandidate(string FullPath, string Name, bool SeenBefore);
