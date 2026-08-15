using System.Text.RegularExpressions;

namespace TokenOptimizer.Providers.Claude;

/// <summary>One skill or plugin entry for the Dashboard's "what's available, when to use it" guide.</summary>
public sealed record SkillGuideEntry(string Name, string Description, string Source);

/// <summary>
/// Powers the Dashboard's skills/plugins guide: unlike ClaudeCodeAdapter.
/// ListInstalledSkillsAsync (folder names only, and only user-scope
/// ~/.claude/skills - it misses skills bundled inside plugins entirely),
/// this walks every SKILL.md this session can actually reach - user-scope
/// AND every installed plugin's bundled skills under
/// ~/.claude/plugins/cache/&lt;marketplace&gt;/&lt;plugin&gt;/.../skills/ - and pulls
/// each one's own frontmatter description, which is exactly the "when to
/// use this" text Claude itself reads to decide when to trigger a skill.
/// Plugin-level descriptions come from each plugin's own plugin.json.
/// Plain regex frontmatter parsing - SKILL.md's YAML frontmatter is simple
/// key: value pairs, not worth a YAML dependency for two fields.
/// </summary>
public static class SkillCatalogService
{
    private static readonly Regex FrontmatterField = new(@"^\s*(name|description)\s*:\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    public static IReadOnlyList<SkillGuideEntry> ListSkillGuide()
    {
        var claudeHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        var entries = new List<SkillGuideEntry>();

        var userSkillsDir = Path.Combine(claudeHome, "skills");
        if (Directory.Exists(userSkillsDir))
        {
            foreach (var skillMd in SafeEnumerateFiles(userSkillsDir, "SKILL.md"))
            {
                if (TryParseSkillMd(skillMd, "skill", out var entry)) entries.Add(entry);
            }
        }

        var pluginsCacheDir = Path.Combine(claudeHome, "plugins", "cache");
        if (Directory.Exists(pluginsCacheDir))
        {
            foreach (var skillMd in SafeEnumerateFiles(pluginsCacheDir, "SKILL.md"))
            {
                var pluginName = PluginNameFromCachePath(skillMd, pluginsCacheDir) ?? "plugin";
                if (TryParseSkillMd(skillMd, pluginName, out var entry)) entries.Add(entry);
            }
        }

        return entries
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<SkillGuideEntry> ListPluginGuide()
    {
        var claudeHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        var pluginsCacheDir = Path.Combine(claudeHome, "plugins", "cache");
        var entries = new List<SkillGuideEntry>();
        if (!Directory.Exists(pluginsCacheDir)) return entries;

        foreach (var manifestPath in SafeEnumerateFiles(pluginsCacheDir, "plugin.json"))
        {
            if (Path.GetFileName(Path.GetDirectoryName(manifestPath)) != ".claude-plugin") continue;
            if (TryParsePluginJson(manifestPath, out var entry)) entries.Add(entry);
        }

        return entries
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryParseSkillMd(string path, string source, out SkillGuideEntry entry)
    {
        entry = default!;
        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException) { return false; }

        var frontmatterEnd = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (!text.StartsWith("---", StringComparison.Ordinal) || frontmatterEnd < 0) return false;
        var lines = text[3..frontmatterEnd].Split('\n');

        string? name = null, description = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var m = FrontmatterField.Match(lines[i]);
            if (!m.Success) continue;

            var value = m.Groups[2].Value.Trim('"', '\'');
            // YAML block-scalar description (">"/"|", optional chomping suffix like
            // ">-") - the real text is on the following, more-indented lines, not
            // on this one; fold them into a single line the same way YAML's ">" does.
            if (m.Groups[1].Value == "description" && value.Length > 0 && (value[0] == '>' || value[0] == '|'))
            {
                var folded = new List<string>();
                var j = i + 1;
                while (j < lines.Length && lines[j].Length > 0 && char.IsWhiteSpace(lines[j][0]))
                {
                    folded.Add(lines[j].Trim());
                    j++;
                }
                value = string.Join(' ', folded);
                i = j - 1;
            }

            if (m.Groups[1].Value == "name") name = value;
            else if (m.Groups[1].Value == "description") description = value;
        }

        name ??= Directory.GetParent(path)?.Name ?? Path.GetFileNameWithoutExtension(path);
        entry = new SkillGuideEntry(name, string.IsNullOrWhiteSpace(description) ? "(no description)" : description, source);
        return true;
    }

    private static bool TryParsePluginJson(string manifestPath, out SkillGuideEntry entry)
    {
        entry = default!;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = doc.RootElement;
            var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (name is null) return false;
            var description = root.TryGetProperty("description", out var d) ? d.GetString() : null;
            entry = new SkillGuideEntry(name, description ?? "(no description)", "plugin");
            return true;
        }
        catch (System.Text.Json.JsonException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>~/.claude/plugins/cache/&lt;marketplace&gt;/&lt;plugin&gt;/... - the plugin folder name is two levels below the cache root.</summary>
    private static string? PluginNameFromCachePath(string filePath, string cacheDir)
    {
        var relative = Path.GetRelativePath(cacheDir, filePath);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Length > 1 ? parts[1] : null;
    }

    /// <summary>Directory.EnumerateFiles throws on the first unreadable subdirectory (permissions, a mid-scan delete) - skip broken branches instead of failing the whole scan.</summary>
    private static IEnumerable<string> SafeEnumerateFiles(string root, string pattern)
    {
        IEnumerator<string>? enumerator = null;
        try { enumerator = Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).GetEnumerator(); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        while (true)
        {
            bool moved;
            try { moved = enumerator.MoveNext(); }
            catch (IOException) { yield break; }
            catch (UnauthorizedAccessException) { yield break; }
            if (!moved) yield break;
            yield return enumerator.Current;
        }
    }
}
