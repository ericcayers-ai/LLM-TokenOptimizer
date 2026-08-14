namespace TokenOptimizer.Providers.Fallback;

/// <summary>
/// Codex and Cursor don't read .claude/skills or plugin manifests - those
/// are Claude Code-specific and genuinely can't transfer to a different
/// vendor's agent. What DOES transfer is the project's own instructions:
/// both Codex CLI and Cursor natively read an AGENTS.md at the project root
/// (an emerging cross-tool convention), the same role CLAUDE.md plays for
/// Claude Code. If the project only has a CLAUDE.md, mirror it to AGENTS.md
/// so whichever fallback backend launches sees the same guidance instead of
/// starting cold. Never overwrites an AGENTS.md the project already
/// maintains on its own.
/// </summary>
public static class AgentsMdSync
{
    public static void SyncFromClaudeMd(string projectDirectory)
    {
        var claudeMd = Path.Combine(projectDirectory, "CLAUDE.md");
        var agentsMd = Path.Combine(projectDirectory, "AGENTS.md");

        if (File.Exists(claudeMd) && !File.Exists(agentsMd))
        {
            try
            {
                File.Copy(claudeMd, agentsMd);
            }
            catch (IOException)
            {
                // Best effort - the fallback backend just starts without it.
            }
        }
    }
}
