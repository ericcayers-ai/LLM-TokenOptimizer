namespace TokenOptimizer.Core.Models;

public sealed record ProjectInfo(string FullPath)
{
    public string Name => Path.GetFileName(FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}
