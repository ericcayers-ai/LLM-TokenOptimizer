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
    public bool ImpeccableSkillInstalled { get; set; }
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
    public string? GroqRateLimitedUntilUtc { get; set; }

    public string? BestLocalModelId { get; set; }
    public double? BestLocalModelTokensPerSecond { get; set; }
    public string? BestLocalModelUpdatedUtc { get; set; }

    /// <summary>
    /// When true, RefreshBestLocalModelAsync's automatic composite_score pick
    /// won't overwrite BestLocalModelId - set after a human deliberately
    /// picks a model (e.g. from BENCHMARK_REPORT.md's human quality review,
    /// which the composite_score heuristic can disagree with).
    /// </summary>
    public bool BestLocalModelIsManualOverride { get; set; }

    /// <summary>Provider names in user-chosen priority order for the "Custom (fallback chain)" option, drag-reordered in the Session tab.</summary>
    public List<string>? CustomFallbackOrder { get; set; }

    /// <summary>Provider names excluded from the custom chain (unchecked in the drag-reorder list) - everything else in CustomFallbackOrder is used.</summary>
    public List<string>? CustomFallbackExcluded { get; set; }

    /// <summary>Provider (or "Auto (fallback chain)" / "Custom (fallback chain)") to launch under when a master-folder subdirectory is double-clicked in the tree browser. Null = use whatever SelectedProviderName currently is.</summary>
    public string? AutoLaunchProviderName { get; set; }

    /// <summary>Fast/Balanced/Max - see LmStudioContextPreset. Stored by name so old configs missing this default cleanly to Balanced.</summary>
    public string LmStudioContextPresetName { get; set; } = nameof(LmStudioContextPreset.Balanced);
}
