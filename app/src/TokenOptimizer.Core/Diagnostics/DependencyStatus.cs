namespace TokenOptimizer.Core.Diagnostics;

public sealed record DependencyStatus(string Name, bool IsAvailable, string? ResolvedPath, string? Version);
