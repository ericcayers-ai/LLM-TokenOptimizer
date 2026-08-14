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

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
