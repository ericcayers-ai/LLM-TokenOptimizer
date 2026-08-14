using System.Text.Json;

namespace TokenOptimizer.Providers.Fallback;

/// <summary>
/// Manual "Transfer Session to Codex/Cursor" - bundles the current Claude
/// Code session's text context and this project's skill instructions into a
/// handoff file, then references it from AGENTS.md so the receiving tool
/// sees it as soon as it launches. Lossy by design: only text content
/// blocks survive (no images/tool results/thinking blocks) - a full-fidelity
/// port isn't possible since Codex/Cursor have no concept of Claude Code's
/// session format. This is a best-effort context bridge, not a migration.
/// Ported from Export-SessionHandoff / ConvertTo-SessionHandoffText /
/// Get-AvailableSkillsDigest.
/// </summary>
public static class SessionHandoffExporter
{
    private const int MaxTranscriptChars = 60_000;

    public static string? FindLatestTranscript(string projectDirectory, string? claudeConfigDir = null)
    {
        var claudeHome = claudeConfigDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

        var slug = System.Text.RegularExpressions.Regex.Replace(
            projectDirectory.TrimEnd('\\', '/'), @"[:\\/]", "-");
        var projectDir = Path.Combine(claudeHome, "projects", slug);
        if (!Directory.Exists(projectDir)) return null;

        return Directory.EnumerateFiles(projectDir, "*.jsonl")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
    }

    public static string ConvertTranscriptToHandoffText(string transcriptPath, int maxChars = MaxTranscriptChars)
    {
        var turns = new List<string>();

        foreach (var line in File.ReadLines(transcriptPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) continue;
                var type = typeProp.GetString();
                if (type != "user" && type != "assistant") continue;

                if (!root.TryGetProperty("message", out var message) ||
                    !message.TryGetProperty("content", out var content))
                {
                    continue;
                }

                var textParts = new List<string>();
                if (content.ValueKind == JsonValueKind.String)
                {
                    textParts.Add(content.GetString() ?? string.Empty);
                }
                else if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in content.EnumerateArray())
                    {
                        if (block.TryGetProperty("type", out var blockType) && blockType.GetString() == "text" &&
                            block.TryGetProperty("text", out var textProp))
                        {
                            var text = textProp.GetString();
                            if (!string.IsNullOrEmpty(text)) textParts.Add(text);
                        }
                    }
                }

                if (textParts.Count > 0)
                {
                    turns.Add($"**{type}**: {string.Join('\n', textParts)}");
                }
            }
        }

        var full = string.Join("\n\n", turns);
        if (full.Length > maxChars)
        {
            full = "...(earlier context truncated)...\n\n" + full[^maxChars..];
        }

        return full;
    }

    public static string GetAvailableSkillsDigest(string projectDirectory)
    {
        var dirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills"),
            Path.Combine(projectDirectory, ".claude", "skills"),
        }.Where(Directory.Exists);

        var chunks = new List<string>();
        foreach (var dir in dirs)
        {
            foreach (var skillFile in Directory.EnumerateFiles(dir, "SKILL.md", SearchOption.AllDirectories))
            {
                try
                {
                    var body = File.ReadAllText(skillFile);
                    var skillName = Directory.GetParent(skillFile)?.Name ?? "skill";
                    chunks.Add($"### {skillName}\n\n{body}");
                }
                catch (IOException) { /* skip unreadable */ }
            }
        }

        return string.Join("\n\n---\n\n", chunks);
    }

    public static string Export(string projectDirectory, string? claudeConfigDir = null)
    {
        var handoffDir = Path.Combine(projectDirectory, ".claude-handoff");
        Directory.CreateDirectory(handoffDir);
        var handoffFile = Path.Combine(handoffDir, "session-handoff.md");

        var sections = new List<string>
        {
            $"# Session handoff from Claude Code\n\nGenerated {DateTime.Now:yyyy-MM-dd HH:mm:ss}. " +
            "Best-effort context bridge, not a full session migration - text only (no images/tool results), " +
            "and the skills below are reference material only (this tool has no Claude Code-style skill trigger system).",
        };

        var transcript = FindLatestTranscript(projectDirectory, claudeConfigDir);
        if (transcript is not null)
        {
            var convo = ConvertTranscriptToHandoffText(transcript);
            if (!string.IsNullOrWhiteSpace(convo))
            {
                sections.Add($"## Conversation so far (Claude Code session)\n\n{convo}");
            }
        }

        var skills = GetAvailableSkillsDigest(projectDirectory);
        if (!string.IsNullOrWhiteSpace(skills))
        {
            sections.Add($"## Skills available in the source Claude Code environment (reference only)\n\n{skills}");
        }

        File.WriteAllText(handoffFile, string.Join("\n\n---\n\n", sections));

        AgentsMdSync.SyncFromClaudeMd(projectDirectory);
        var agentsMd = Path.Combine(projectDirectory, "AGENTS.md");
        const string marker = ".claude-handoff/session-handoff.md";
        var reference = $"\n\n<!-- tokenoptimizer session handoff -->\nSee {marker} for the Claude Code session this project was transferred from.\n";

        try
        {
            if (File.Exists(agentsMd))
            {
                var existing = File.ReadAllText(agentsMd);
                if (!existing.Contains(marker))
                {
                    File.WriteAllText(agentsMd, existing.TrimEnd() + reference);
                }
            }
            else
            {
                File.WriteAllText(agentsMd, reference);
            }
        }
        catch (IOException) { /* best effort */ }

        return handoffFile;
    }
}
