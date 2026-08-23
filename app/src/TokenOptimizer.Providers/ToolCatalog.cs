using TokenOptimizer.Sandbox;

namespace TokenOptimizer.Providers;

/// <summary>
/// Single source of truth for the companion tools TokenOptimizer installs:
/// one catalog feeds both the host wiring (CompanionToolingInstaller) and,
/// in later tasks, sandbox image baking, so a tool is described exactly once.
/// Entries mirror what the installers actually do today; graft had no host
/// installer and is authored from NanoNets/Graft's documented flow.
/// </summary>
public static class ToolCatalog
{
    public static IReadOnlyList<CompanionTool> Tools { get; } =
    [
        new CompanionTool(
            Id: "rtk",
            HostInstallCommand: """download https://github.com/rtk-ai/rtk/releases/latest/download/rtk-x86_64-pc-windows-msvc.zip into %LOCALAPPDATA%\rtk && rtk init -g""",
            ImageInstallFragment: """RUN curl -fsSL https://raw.githubusercontent.com/rtk-ai/rtk/master/install.sh | bash""",
            ClaudeWiringFragment: """settings.json hooks.PreToolUse += {"type":"command","command":"%LOCALAPPDATA%\rtk\rtk.exe hook claude"} (merged by 'rtk init -g')"""),

        new CompanionTool(
            Id: "graphify",
            HostInstallCommand: """python -m pip install --upgrade graphifyy (falls back to --user on access-denied)""",
            ImageInstallFragment: """RUN pip install --upgrade graphifyy""",
            ClaudeWiringFragment: """~/.claude/skills/graphify/SKILL.md + settings.json PreToolUse hook ('graphify claude install')"""),

        new CompanionTool(
            Id: "headroom",
            HostInstallCommand: """bash -lc "curl -fsSL https://raw.githubusercontent.com/henchmarketing-rgb/headroom/main/install.sh | bash" (run with a python3 PATH shim)""",
            ImageInstallFragment: """RUN curl -fsSL https://raw.githubusercontent.com/henchmarketing-rgb/headroom/main/install.sh | bash""",
            ClaudeWiringFragment: """~/.claude/statusline.sh + settings.json statusLine command 'bash "<config-dir>/statusline.sh"' (python3 refs rewritten to an absolute interpreter)"""),

        new CompanionTool(
            Id: "caveman",
            HostInstallCommand: """plugin marketplace add JuliusBrussee/caveman && plugin install caveman@caveman --scope user""",
            ImageInstallFragment: """RUN claude plugin marketplace add JuliusBrussee/caveman && claude plugin install caveman@caveman --scope user""",
            ClaudeWiringFragment: """registered in ~/.claude/plugins/installed_plugins.json as caveman (scope=user, enabled=true); verified via 'claude plugin list'"""),

        new CompanionTool(
            Id: "claude-mem",
            HostInstallCommand: """CI=true NON_INTERACTIVE=1 npx -y claude-mem@latest install --ide claude-code (after pre-seeding ~/.claude-mem/settings.json)""",
            ImageInstallFragment: """RUN npx -y claude-mem@latest install --ide claude-code""",
            ClaudeWiringFragment: """claude-mem's hooks in ~/.claude/settings.json (written by its installer); app sessions set CLAUDE_MEM_DATA_DIR=~/.claude-mem-tokenoptimizer and CLAUDE_MEM_WORKER_PORT=37778"""),

        new CompanionTool(
            Id: "context7",
            HostInstallCommand: """mcp add --scope user context7 -- npx -y @upstash/context7-mcp""",
            ImageInstallFragment: """RUN claude mcp add --scope user context7 -- npx -y @upstash/context7-mcp""",
            ClaudeWiringFragment: """user-scope MCP server context7 -> npx -y @upstash/context7-mcp ('claude mcp add --scope user')""",
            HostInstallIsExecutable: true),

        new CompanionTool(
            Id: "graft",
            HostInstallCommand: """npm install -g @nanonets/graft && graft init""",
            ImageInstallFragment: """RUN npm install -g @nanonets/graft && graft init --agents claude --yes && graft build  # NanoNets/Graft: init wires .claude/, build generates the gitignored graft/ graph cache (--agents/--yes required without a TTY)""",
            ClaudeWiringFragment: """.claude/skills/graft/SKILL.md + statusline and UserPromptSubmit/PostToolUse/SessionStart/Stop hooks merged into .claude/settings.json + MCP server in .mcp.json (all written by 'graft init')"""),
    ];
}
