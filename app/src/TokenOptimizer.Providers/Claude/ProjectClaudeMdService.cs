using TokenOptimizer.Core.Diagnostics;

namespace TokenOptimizer.Providers.Claude;

/// <summary>
/// Per-project CLAUDE.md/Graphify enforcement: whether a project is big
/// enough to warrant Graphify strict mode, whether its CLAUDE.md has grown
/// bloated, and keeping the graph-first + companion-tooling directive
/// sections present in CLAUDE.md. Ported from Test-ProjectExceedsGraphifyThreshold /
/// Test-ClaudeMdBloat / Set-ProjectClaudeMdDirective / Install-GraphifyHook /
/// Install-GraphifyStrictMode.
/// </summary>
public sealed class ProjectClaudeMdService
{
    private const int GraphifyStrictFileThreshold = 150;
    private const int ClaudeMdBloatLineThreshold = 300;

    private static readonly string[] ExcludeDirs =
        ["node_modules", ".git", ".graphify", "graphify-out", "dist", "build", "out", "bin", "obj", "__pycache__", ".venv", "venv", ".next", "target"];

    private const string GraphifyMarkerHeading = "# Graphify enforcement";
    private const string CompanionMarkerHeading = "# Companion tooling";

    /// <summary>A project below this file count skips Graphify entirely - not worth the setup/token overhead.</summary>
    public static bool ExceedsGraphifyThreshold(string projectDirectory)
    {
        try
        {
            var count = Directory.EnumerateFiles(projectDirectory, "*", SearchOption.AllDirectories)
                .Count(f => !ExcludeDirs.Any(dir => f.Contains($"{Path.DirectorySeparatorChar}{dir}{Path.DirectorySeparatorChar}")));
            return count >= GraphifyStrictFileThreshold;
        }
        catch (IOException)
        {
            return true; // default to strict mode on a failed count, matching the original
        }
    }

    /// <summary>Anthropic's own guidance: a bloated CLAUDE.md causes Claude to ignore half of it. Returns a warning message, or null if under threshold.</summary>
    public static string? CheckClaudeMdBloat(string projectDirectory)
    {
        var claudeMdPath = Path.Combine(projectDirectory, "CLAUDE.md");
        if (!File.Exists(claudeMdPath)) return null;

        try
        {
            var lineCount = File.ReadLines(claudeMdPath).Count();
            if (lineCount < ClaudeMdBloatLineThreshold) return null;
            return $"CLAUDE.md is {lineCount} lines (threshold {ClaudeMdBloatLineThreshold}) - a bloated CLAUDE.md causes Claude to ignore half of it. " +
                   "Prune what Claude can already infer from the code; move sometimes-relevant context into a skill instead.";
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task<bool> InstallGraphifyHookAsync(string projectDirectory)
    {
        var marker = Path.Combine(projectDirectory, ".graphify_hook_installed");
        if (File.Exists(marker)) return true;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0) await Task.Delay(2000);
            var result = await ExternalCommandRunner.RunAsync("graphify", "hook install", projectDirectory, timeoutSeconds: 30);
            if (result.Success)
            {
                File.WriteAllText(marker, string.Empty);
                return true;
            }
        }

        return false;
    }

    public async Task<bool> InstallGraphifyStrictModeAsync(string projectDirectory)
    {
        var strictMarker = Path.Combine(projectDirectory, ".graphify_strict_installed");
        var strictOk = File.Exists(strictMarker);
        if (!strictOk)
        {
            var result = await ExternalCommandRunner.RunAsync("graphify", "install --project --strict", projectDirectory, timeoutSeconds: 30);
            strictOk = result.Success;
            if (strictOk) File.WriteAllText(strictMarker, string.Empty);
        }

        Environment.SetEnvironmentVariable("GRAPHIFY_HOOK_STRICT", "1");

        var claudeHookMarker = Path.Combine(projectDirectory, ".graphify_claude_hook_installed");
        var hookOk = File.Exists(claudeHookMarker);
        if (!hookOk)
        {
            var result = await ExternalCommandRunner.RunAsync("graphify", "claude install", projectDirectory, timeoutSeconds: 30);
            hookOk = result.Success;
            if (hookOk) File.WriteAllText(claudeHookMarker, string.Empty);
        }

        return strictOk && hookOk;
    }

    /// <summary>
    /// Ensures CLAUDE.md has the graph-first directive (only if the project
    /// is big enough to use Graphify at all) and the companion-tooling
    /// awareness section, merging into an existing file without duplicating
    /// sections it already has.
    /// </summary>
    public static void EnsureDirective(string projectDirectory, bool useGraphify)
    {
        var claudeMdPath = Path.Combine(projectDirectory, "CLAUDE.md");
        var graphifySection = useGraphify ? BuildGraphifySection() : null;
        var companionSection = BuildCompanionSection();
        var directiveBlock = graphifySection is not null ? $"{graphifySection}\n\n{companionSection}" : companionSection;

        try
        {
            if (!File.Exists(claudeMdPath))
            {
                File.WriteAllText(claudeMdPath, directiveBlock);
                return;
            }

            var existing = File.ReadAllText(claudeMdPath);
            var hasGraphify = existing.Contains(GraphifyMarkerHeading);
            var hasCompanion = existing.Contains(CompanionMarkerHeading);
            var graphifySatisfied = hasGraphify || graphifySection is null;

            if (graphifySatisfied && hasCompanion) return;

            string? toAppend = (!graphifySatisfied, hasCompanion) switch
            {
                (true, false) => directiveBlock,
                (_, false) => directiveBlock[directiveBlock.IndexOf(CompanionMarkerHeading, StringComparison.Ordinal)..],
                (true, true) => directiveBlock[..directiveBlock.IndexOf(CompanionMarkerHeading, StringComparison.Ordinal)].TrimEnd(),
                _ => null,
            };

            if (toAppend is null) return;

            File.WriteAllText(claudeMdPath, existing.TrimEnd() + "\r\n\r\n" + toAppend);
        }
        catch (IOException)
        {
            // Best effort - a write failure here must never block launch.
        }
    }

    private static string BuildGraphifySection() =>
        "CRITICAL: You must run `graphify query` or read `graphify-out/GRAPH_REPORT.md` (or `.graphify/graph.json` / " +
        "`.graphify/studio/studio.html` on newer Graphify builds) before any raw file read, Glob, or Grep. This is non-negotiable.\n\n" +
        $"{GraphifyMarkerHeading}\n\n" +
        "- Treat `graphify` as mandatory for understanding this codebase. `grep`/`Grep` and raw file reads are a fallback only, to be used after consulting the graph, never before it.\n" +
        "- Any subagent spawned inside this project must follow the same rule: query the graph first, fall back to grep only if the graph doesn't have the answer.\n" +
        "- At the start of a session: use `graphify-out/GRAPH_REPORT.md` (or the current project's `.graphify/graph.json`) before searching files. Do not use raw grep first.\n" +
        "- Strict-mode enforcement is active for this project (`graphify install --project --strict`, `GRAPHIFY_HOOK_STRICT=1`, and a `PreToolUse` hook installed via `graphify claude install` in `.claude/settings.json`). The first raw source read of a session is hard-blocked and redirected to the graph; file search and bash commands are intercepted by the hook.";

    private static string BuildCompanionSection() =>
        $"{CompanionMarkerHeading}\n\n" +
        "The following are installed once at user scope (`~/.claude/`) and are active in every session in this project, not just this one:\n\n" +
        "- **claude-mem** - persistent cross-session memory, runs on Claude Code's own lifecycle hooks.\n" +
        "- **headroom** - context-window usage bar in the statusline.\n" +
        "- **claude-code-setup** - recommends tailored MCP servers/skills/hooks for this project.\n" +
        "- **task-observer** - logs workflow friction for later review.\n" +
        "- **claude-md-management** - audits and maintains this file (`/revise-claude-md`, the in-session `#` shortcut).\n" +
        "- **context7** (MCP) - version-specific library/API docs on demand.\n";
}
