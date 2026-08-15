using TokenOptimizer.Core.Diagnostics;

namespace TokenOptimizer.Providers.Claude;

/// <summary>
/// Keeps CLAUDE.md's graph-first + companion-tooling directives current,
/// warns on a bloated CLAUDE.md, wires up Graphify strict mode for projects
/// big enough to warrant it, and self-heals claude-mem - the same
/// "runs every launch, idempotent, marker-gated" checks Invoke-ProjectMode
/// ran before starting Claude Code. Shared by every UI surface that can
/// launch a Claude Code-backed session (the desktop app's MainViewModel and
/// the CLI host both call this - one implementation, no drift between them).
/// </summary>
public static class ProjectSessionPrep
{
    public static async Task PrepareProjectDirectiveAsync(
        string projectDirectory,
        ProjectClaudeMdService claudeMdService,
        CommandAvailability availability,
        Action<string>? log = null)
    {
        if (ProjectClaudeMdService.CheckClaudeMdBloat(projectDirectory) is { } bloatWarning)
        {
            log?.Invoke(bloatWarning);
        }

        var useGraphify = ProjectClaudeMdService.ExceedsGraphifyThreshold(projectDirectory);
        if (useGraphify && availability.IsOnPath("graphify", useCache: true))
        {
            await claudeMdService.InstallGraphifyHookAsync(projectDirectory);
            await claudeMdService.InstallGraphifyStrictModeAsync(projectDirectory);
        }

        ProjectClaudeMdService.EnsureDirective(projectDirectory, useGraphify);
        ProjectClaudeMdService.EnsureHandoffReference(projectDirectory);

        // claude-mem's context-injection defaults (50 observations / 10
        // sessions / 5 full-detail) are tuned for larger, longer-lived
        // codebases. Process-scoped only - never touches the shared
        // ~/.claude-mem/settings.json, so it has no effect on any other
        // project; the launched claude.exe inherits it since it's a child
        // of this process either way (UseShellExecute true or false).
        if (!useGraphify)
        {
            Environment.SetEnvironmentVariable("CLAUDE_MEM_CONTEXT_OBSERVATIONS", "20");
            Environment.SetEnvironmentVariable("CLAUDE_MEM_CONTEXT_SESSION_COUNT", "5");
            Environment.SetEnvironmentVariable("CLAUDE_MEM_CONTEXT_FULL_COUNT", "2");
        }
        else
        {
            Environment.SetEnvironmentVariable("CLAUDE_MEM_CONTEXT_OBSERVATIONS", null);
            Environment.SetEnvironmentVariable("CLAUDE_MEM_CONTEXT_SESSION_COUNT", null);
            Environment.SetEnvironmentVariable("CLAUDE_MEM_CONTEXT_FULL_COUNT", null);
        }

        await ClaudeMemRepair.RepairAsync();
    }
}
