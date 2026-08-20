using TokenOptimizer.Providers.Fallback;

namespace TokenOptimizer.Providers.Tests.Fallback;

public class SessionHandoffExporterTests : IDisposable
{
    private readonly string _tempDir;

    public SessionHandoffExporterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tokopt-handoff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void ConvertTranscriptToHandoffText_ExtractsOnlyTextBlocks_FromUserAndAssistantTurns()
    {
        var transcriptPath = Path.Combine(_tempDir, "session.jsonl");
        File.WriteAllLines(transcriptPath, new[]
        {
            """{"type": "user", "message": {"content": "hello there"}}""",
            """{"type": "assistant", "message": {"content": [{"type": "text", "text": "hi back"}, {"type": "tool_use", "id": "x"}]}}""",
            """{"type": "system", "message": {"content": "should be skipped"}}""",
            "",
        });

        var text = SessionHandoffExporter.ConvertTranscriptToHandoffText(transcriptPath);

        Assert.Contains("**user**: hello there", text);
        Assert.Contains("**assistant**: hi back", text);
        Assert.DoesNotContain("should be skipped", text);
    }

    [Fact]
    public void ConvertTranscriptToHandoffText_TruncatesToMaxChars_FromTheEnd()
    {
        var transcriptPath = Path.Combine(_tempDir, "session.jsonl");
        var longText = new string('x', 200);
        var line = "{\"type\": \"user\", \"message\": {\"content\": \"" + longText + "\"}}";
        File.WriteAllLines(transcriptPath, new[] { line });

        var text = SessionHandoffExporter.ConvertTranscriptToHandoffText(transcriptPath, maxChars: 50);

        Assert.StartsWith("...(earlier context truncated)...", text);
        Assert.True(text.Length < 200);
    }

    [Fact]
    public void Export_WritesHandoffFile_AndReferencesItFromAgentsMd()
    {
        var handoffFile = SessionHandoffExporter.Export(_tempDir, claudeConfigDir: Path.Combine(_tempDir, "no-such-claude-home"));

        Assert.True(File.Exists(handoffFile));
        var agentsMd = Path.Combine(_tempDir, "AGENTS.md");
        Assert.True(File.Exists(agentsMd));
        Assert.Contains(".claude-handoff/session-handoff.md", File.ReadAllText(agentsMd));
    }

    [Fact]
    public void Export_CalledTwice_DoesNotDuplicateAgentsMdReference()
    {
        SessionHandoffExporter.Export(_tempDir, claudeConfigDir: Path.Combine(_tempDir, "no-such-claude-home"));
        SessionHandoffExporter.Export(_tempDir, claudeConfigDir: Path.Combine(_tempDir, "no-such-claude-home"));

        var agentsMd = File.ReadAllText(Path.Combine(_tempDir, "AGENTS.md"));
        var occurrences = agentsMd.Split(".claude-handoff/session-handoff.md").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void GetEffectiveClaudeConfigDir_IsolateTrue_ReturnsProfileDir()
    {
        var dir = SessionHandoffExporter.GetEffectiveClaudeConfigDir(_tempDir, isolateConfig: true);
        Assert.NotNull(dir);
        Assert.Contains("claude-profiles", dir);
    }

    [Fact]
    public void GetEffectiveClaudeConfigDir_ExistingProfile_ReturnsProfileDir()
    {
        var profileDir = SessionHandoffExporter.GetEffectiveClaudeConfigDir(_tempDir, isolateConfig: true);
        Assert.NotNull(profileDir);
        // Re-query without isolate should find the existing profile.
        var found = SessionHandoffExporter.GetEffectiveClaudeConfigDir(_tempDir, isolateConfig: false);
        Assert.Equal(profileDir, found);
    }

    [Fact]
    public void GetEffectiveClaudeConfigDir_NoProfileNoIsolate_ReturnsNull()
    {
        var freshDir = Path.Combine(_tempDir, "fresh");
        Directory.CreateDirectory(freshDir);
        var found = SessionHandoffExporter.GetEffectiveClaudeConfigDir(freshDir, isolateConfig: false);
        Assert.Null(found);
    }

    [Fact]
    public void FindLatestTranscript_UsesProvidedClaudeConfigDir()
    {
        var claudeHome = Path.Combine(_tempDir, "custom-claude");
        var slug = System.Text.RegularExpressions.Regex.Replace(
            _tempDir.TrimEnd('\\', '/'), @"[:\\/]", "-");
        var projectDir = Path.Combine(claudeHome, "projects", slug);
        Directory.CreateDirectory(projectDir);
        var transcript = Path.Combine(projectDir, "session.jsonl");
        File.WriteAllText(transcript, "");

        var found = SessionHandoffExporter.FindLatestTranscript(_tempDir, claudeHome);
        Assert.Equal(transcript, found);
    }

    [Fact]
    public void GetAvailableSkillsDigest_WithCustomClaudeConfigDir_IncludesCustomSkills()
    {
        var customClaudeHome = Path.Combine(_tempDir, "custom-claude");
        var skillsDir = Path.Combine(customClaudeHome, "skills", "custom-skill");
        Directory.CreateDirectory(skillsDir);
        File.WriteAllText(Path.Combine(skillsDir, "SKILL.md"), "# Custom Skill");

        var digest = SessionHandoffExporter.GetAvailableSkillsDigest(_tempDir, customClaudeHome);

        Assert.Contains("Custom Skill", digest);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
