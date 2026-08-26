namespace TokenOptimizer.Core.Models;

using TokenOptimizer.Sandbox;

public sealed class AppConfig
{
    /// <summary>OpenSandbox substrate connection/image settings - see TokenOptimizer.Sandbox.SandboxSettings.</summary>
    public SandboxSettings Sandbox { get; set; } = new();

    public string? MasterFolder { get; set; }
    public List<string> ProjectHistory { get; set; } = new();
    public string? ClaudePath { get; set; }
    public string? LastGraphifyVersion { get; set; }
    public bool HeadroomInstalled { get; set; }
    public bool ClaudeCodeSetupPluginInstalled { get; set; }
    public bool ClaudeMdManagementPluginInstalled { get; set; }
    public bool CodeIntelligencePluginInstalled { get; set; }
    public bool CavemanPluginInstalled { get; set; }
    public bool PonytailPluginInstalled { get; set; }
    public bool ClaudeMemInstalled { get; set; }
    public bool ContextModeMcpInstalled { get; set; }
    public bool Context7McpInstalled { get; set; }
    public bool TaskObserverSkillInstalled { get; set; }
    public bool ImpeccableSkillInstalled { get; set; }
    public bool AutoSkillsCliInstalled { get; set; }
    public bool RtkCliInstalled { get; set; }
    public bool ClaudePluginsAndSkillsInstalled { get; set; }
    public string? PreferredModel { get; set; }
    public bool IsolateClaudeConfig { get; set; }

    public bool AntigravityProxyInstalled { get; set; }
    public string? AntigravityRateLimitedUntilUtc { get; set; }
    public string? CodexRateLimitedUntilUtc { get; set; }
    public string? CursorRateLimitedUntilUtc { get; set; }
    public string? ClaudeRateLimitedUntilUtc { get; set; }
    public string? GroqRateLimitedUntilUtc { get; set; }
    public string? OpenCodeRateLimitedUntilUtc { get; set; }
    public string? HermesAgentRateLimitedUntilUtc { get; set; }

    /// <summary>Provider names in user-chosen priority order for the "Custom (fallback chain)" option, drag-reordered in the Session tab.</summary>
    public List<string>? CustomFallbackOrder { get; set; }

    /// <summary>Provider names excluded from the custom chain (unchecked in the drag-reorder list) - everything else in CustomFallbackOrder is used.</summary>
    public List<string>? CustomFallbackExcluded { get; set; }

    /// <summary>"ProviderName::ModelId" keys ticked in the Models card - what shows up in Claude Code's own /model list on next launch (see MainViewModel.LaunchTickedModelsAsync / UnifiedModelRouter).</summary>
    public List<string>? TickedModels { get; set; }

    /// <summary>Last resolved local embeddings endpoint for auto-wired RAG retrieval (see MainViewModel.AutoDetectRagEndpointAsync) - probed automatically, no manual toggle.</summary>
    public string? LastDetectedRagEmbeddingsUrl { get; set; }

    /// <summary>Whether the agency-agents repo has been shallow-cloned into ~/.tokenoptimizer/agency-agents.</summary>
    public bool AgencyAgentsCloned { get; set; }

    /// <summary>Agency-agent slug names ticked for sync into Claude Code's agents directory.</summary>
    public List<string>? TickedAgencyAgents { get; set; }

    /// <summary>Local date (yyyy-MM-dd) the daily companion-tooling auto-update last ran - see MainViewModel.RunDailyAutoUpdateIfNeededAsync. Null/stale means it hasn't run today yet.</summary>
    public string? LastAutoUpdateCheckDate { get; set; }

    /// <summary>Companion-tool names ticked in the Setup tab's picker - null means "everything" (pre-existing installs before this list existed keep their all-tools behavior).</summary>
    public List<string>? TickedCompanionTools { get; set; }
}
