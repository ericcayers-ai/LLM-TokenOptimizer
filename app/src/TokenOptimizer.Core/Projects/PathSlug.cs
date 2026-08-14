using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TokenOptimizer.Core.Projects;

/// <summary>Stable, filesystem-safe, collision-resistant identifier for a directory - used for per-project mutex names and CLAUDE_CONFIG_DIR names.</summary>
public static class PathSlug
{
    public static string For(string path)
    {
        var normalized = path.TrimEnd('\\', '/').ToLowerInvariant();
        var segments = normalized.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var leaf = segments.Length > 0 ? segments[^1] : "root";
        leaf = Regex.Replace(leaf, "[^a-z0-9]", "-").Trim('-');
        if (string.IsNullOrEmpty(leaf)) leaf = "project";

        var hashBytes = MD5.HashData(Encoding.UTF8.GetBytes(normalized));
        var hash = Convert.ToHexStringLower(hashBytes)[..8];

        return $"{leaf}-{hash}";
    }
}
