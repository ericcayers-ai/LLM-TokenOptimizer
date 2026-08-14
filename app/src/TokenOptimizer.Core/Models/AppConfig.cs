namespace TokenOptimizer.Core.Models;

public sealed class AppConfig
{
    public string? MasterFolder { get; set; }
    public List<string> ProjectHistory { get; set; } = new();
    public string? ClaudePath { get; set; }
    public string? LastGraphifyVersion { get; set; }
    public bool HeadroomInstalled { get; set; }
    public bool ClaudeCodeSetupPluginInstalled { get; set; }
    public bool ClaudeMdManagementPluginInstalled { get; set; }
    public bool CodeIntelligencePluginInstalled { get; set; }
    public bool CavemanPluginInstalled { get; set; }
    public bool ClaudeMemInstalled { get; set; }
    public bool ContextModeMcpInstalled { get; set; }
    public bool Context7McpInstalled { get; set; }
    public bool TaskObserverSkillInstalled { get; set; }
    public bool AutoSkillsCliInstalled { get; set; }
    public bool RtkCliInstalled { get; set; }
    public bool LMStudioSupportInstalled { get; set; }
    public string? PreferredModel { get; set; }
    public bool IsolateClaudeConfig { get; set; }

    public bool AntigravityProxyInstalled { get; set; }
    public string? AntigravityRateLimitedUntilUtc { get; set; }
    public string? CodexRateLimitedUntilUtc { get; set; }
    public string? CursorRateLimitedUntilUtc { get; set; }
    public string? ClaudeRateLimitedUntilUtc { get; set; }

    public string? BestLocalModelId { get; set; }
    public double? BestLocalModelTokensPerSecond { get; set; }
    public string? BestLocalModelUpdatedUtc { get; set; }
}
