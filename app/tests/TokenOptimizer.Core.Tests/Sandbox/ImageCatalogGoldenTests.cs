using TokenOptimizer.Providers;
using TokenOptimizer.Sandbox;

namespace TokenOptimizer.Core.Tests.Sandbox;

public class ImageCatalogGoldenTests
{
    [Fact]
    public void GenerateDockerfile_AgentBase_MatchesGolden()
    {
        var catalog = new ImageCatalog(ToolCatalog.Tools);

        Assert.Equal(GoldenAgentBaseDockerfile, catalog.GenerateDockerfile(AgentImageKind.AgentBase));
    }

    [Fact]
    public void GenerateDockerfile_AgentCompanion_MatchesGolden()
    {
        var catalog = new ImageCatalog(ToolCatalog.Tools);

        Assert.Equal(GoldenAgentCompanionDockerfile, catalog.GenerateDockerfile(AgentImageKind.AgentCompanion));
    }

    [Theory]
    [InlineData(AgentImageKind.AgentBase)]
    [InlineData(AgentImageKind.AgentCompanion)]
    public void GenerateDockerfile_IsDeterministicAndLfOnly(AgentImageKind kind)
    {
        var first = new ImageCatalog(ToolCatalog.Tools).GenerateDockerfile(kind);
        var second = new ImageCatalog(ToolCatalog.Tools).GenerateDockerfile(kind);

        Assert.Equal(first, second);
        Assert.False(first.Contains('\r'));
    }

    [Fact]
    public void GenerateDockerfile_PinsBaseImageAndNodeSourceVersion_ForBothKinds()
    {
        var catalog = new ImageCatalog(ToolCatalog.Tools);

        foreach (var kind in (AgentImageKind[])Enum.GetValues(typeof(AgentImageKind)))
        {
            var output = catalog.GenerateDockerfile(kind);
            Assert.Contains("FROM opensandbox/code-interpreter:v1.1.0", output);
            Assert.Contains("setup_22.x", output);
            Assert.DoesNotContain("setup_lts.x", output);
        }
    }

    [Fact]
    public void GenerateDockerfile_AgentBase_ReturnsOnlyTheBaseStage()
    {
        var output = new ImageCatalog(ToolCatalog.Tools).GenerateDockerfile(AgentImageKind.AgentBase);

        foreach (var tool in ToolCatalog.Tools)
            Assert.DoesNotContain(tool.ImageInstallFragment, output);

        Assert.DoesNotContain("WIRING", output);
        Assert.DoesNotContain("entrypoint", output);
        Assert.DoesNotContain("ENTRYPOINT", output);
        Assert.DoesNotContain("CMD", output);
    }

    [Fact]
    public void GenerateDockerfile_AgentCompanion_BakesEveryNonEmptyToolFragmentVerbatimInCatalogOrder()
    {
        var output = new ImageCatalog(ToolCatalog.Tools).GenerateDockerfile(AgentImageKind.AgentCompanion);

        var offset = 0;
        foreach (var tool in ToolCatalog.Tools.Where(t => !string.IsNullOrWhiteSpace(t.ImageInstallFragment)))
        {
            var at = output.IndexOf(tool.ImageInstallFragment, offset, StringComparison.Ordinal);
            Assert.True(at >= 0, $"fragment for '{tool.Id}' missing or out of order");
            offset = at + tool.ImageInstallFragment.Length;
        }

        Assert.Contains("NanoNets/Graft", output);
    }

    [Fact]
    public void GenerateDockerfile_AgentCompanion_ReferencesEntrypointScriptPath()
    {
        var output = new ImageCatalog(ToolCatalog.Tools).GenerateDockerfile(AgentImageKind.AgentCompanion);

        Assert.Contains("/usr/local/bin/tokenoptimizer-entrypoint.sh", output);
        Assert.Contains("COPY entrypoint.sh /usr/local/bin/tokenoptimizer-entrypoint.sh", output);
        Assert.Contains("ENTRYPOINT [\"/usr/local/bin/tokenoptimizer-entrypoint.sh\"]", output);
    }

    [Fact]
    public void GenerateEntrypointScript_MatchesGolden()
    {
        var script = new ImageCatalog(ToolCatalog.Tools).GenerateEntrypointScript();

        Assert.Equal(GoldenEntrypointScript, script);
        Assert.False(script.Contains('\r'));
    }

    [Fact]
    public void Constructor_ReflectsInjectedToolsInsteadOfHardcodedCatalog()
    {
        var solo = new CompanionTool(
            Id: "only",
            HostInstallCommand: "host",
            ImageInstallFragment: "RUN echo only",
            ClaudeWiringFragment: "wire only");

        var output = new ImageCatalog([solo]).GenerateDockerfile(AgentImageKind.AgentCompanion);

        Assert.DoesNotContain("rtk", output);
        Assert.Contains("# [only]", output);
        Assert.Contains("RUN echo only", output);
        Assert.Contains("[only] wire only", output);
    }

    [Fact]
    public void Constructor_SkipsToolsWithEmptyFragments()
    {
        var silent = new CompanionTool(
            Id: "silent",
            HostInstallCommand: "host",
            ImageInstallFragment: "",
            ClaudeWiringFragment: "");

        var output = new ImageCatalog([silent]).GenerateDockerfile(AgentImageKind.AgentCompanion);

        Assert.DoesNotContain("# [silent]", output);
        Assert.DoesNotContain("[silent]", output);
        Assert.Contains("COPY <<'EOF_WIRING' /opt/tokenoptimizer/WIRING.txt", output);
    }

    [Fact]
    public void Constructor_RejectsNullOrEmptyTools()
    {
        Assert.Throws<ArgumentNullException>(() => new ImageCatalog(null!));
        Assert.Throws<ArgumentException>(() => new ImageCatalog([]));
    }

    private const string GoldenAgentBaseDockerfile = """
# syntax=docker/dockerfile:1
# Generated by TokenOptimizer.Sandbox.ImageCatalog - DO NOT EDIT BY HAND.
# Single source of truth: TokenOptimizer.Providers.ToolCatalog (feeds host wiring AND image baking).

FROM opensandbox/code-interpreter:v1.1.0

# node LTS + Claude Code CLI
RUN curl -fsSL https://deb.nodesource.com/setup_22.x | bash - && \
    apt-get install -y nodejs && \
    npm install -g @anthropic-ai/claude-code
""";

    private const string GoldenAgentCompanionDockerfile = """
# syntax=docker/dockerfile:1
# Generated by TokenOptimizer.Sandbox.ImageCatalog - DO NOT EDIT BY HAND.
# Single source of truth: TokenOptimizer.Providers.ToolCatalog (feeds host wiring AND image baking).

FROM opensandbox/code-interpreter:v1.1.0

# node LTS + Claude Code CLI
RUN curl -fsSL https://deb.nodesource.com/setup_22.x | bash - && \
    apt-get install -y nodejs && \
    npm install -g @anthropic-ai/claude-code

# --- companion tools (ToolCatalog order) ---

# [rtk]
RUN curl -fsSL https://raw.githubusercontent.com/rtk-ai/rtk/master/install.sh | bash

# [graphify]
RUN pip install --upgrade graphifyy

# [headroom]
RUN curl -fsSL https://raw.githubusercontent.com/henchmarketing-rgb/headroom/main/install.sh | bash

# [caveman]
RUN claude plugin marketplace add JuliusBrussee/caveman && claude plugin install caveman@caveman --scope user

# [claude-mem]
RUN npx -y claude-mem@latest install --ide claude-code

# [context7]
RUN claude mcp add --scope user context7 -- npx -y @upstash/context7-mcp

# [graft]
RUN npm install -g @nanonets/graft && graft init --agents claude --yes && graft build  # NanoNets/Graft: init wires .claude/, build generates the gitignored graft/ graph cache (--agents/--yes required without a TTY)

# --- .claude wiring notes (from ToolCatalog.ClaudeWiringFragment; baked as inert documentation) ---
COPY <<'EOF_WIRING' /opt/tokenoptimizer/WIRING.txt
[rtk] settings.json hooks.PreToolUse += {"type":"command","command":"%LOCALAPPDATA%\rtk\rtk.exe hook claude"} (merged by 'rtk init -g')
[graphify] ~/.claude/skills/graphify/SKILL.md + settings.json PreToolUse hook ('graphify claude install')
[headroom] ~/.claude/statusline.sh + settings.json statusLine command 'bash "<config-dir>/statusline.sh"' (python3 refs rewritten to an absolute interpreter)
[caveman] registered in ~/.claude/plugins/installed_plugins.json as caveman (scope=user, enabled=true); verified via 'claude plugin list'
[claude-mem] claude-mem's hooks in ~/.claude/settings.json (written by its installer); app sessions set CLAUDE_MEM_DATA_DIR=~/.claude-mem-tokenoptimizer and CLAUDE_MEM_WORKER_PORT=37778
[context7] user-scope MCP server context7 -> npx -y @upstash/context7-mcp ('claude mcp add --scope user')
[graft] .claude/skills/graft/SKILL.md + statusline and UserPromptSubmit/PostToolUse/SessionStart/Stop hooks merged into .claude/settings.json + MCP server in .mcp.json (all written by 'graft init')
EOF_WIRING

# --- entrypoint: per-workspace graft bootstrap, then exec the container command ---
COPY entrypoint.sh /usr/local/bin/tokenoptimizer-entrypoint.sh
RUN chmod +x /usr/local/bin/tokenoptimizer-entrypoint.sh

WORKDIR /workspace
ENTRYPOINT ["/usr/local/bin/tokenoptimizer-entrypoint.sh"]
CMD ["sleep", "infinity"]
""";

    private const string GoldenEntrypointScript = """
#!/usr/bin/env bash
set -e
if [ -d /workspace ] && [ ! -f /workspace/.graft-ready ]; then
  cd /workspace
  graft init || true
  graft build || true
  touch /workspace/.graft-ready
fi
exec "$@"
""";
}
