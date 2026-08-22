using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.RegularExpressions;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TokenOptimizer.App.Services;
using TokenOptimizer.Core.Concurrency;
using TokenOptimizer.Core.Config;
using TokenOptimizer.Core.Diagnostics;
using TokenOptimizer.Core.Models;
using TokenOptimizer.Core.Projects;
using TokenOptimizer.Core.Security;
using TokenOptimizer.Providers;
using TokenOptimizer.Providers.Claude;
using TokenOptimizer.Providers.Compat;
using TokenOptimizer.Providers.Fallback;

namespace TokenOptimizer.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private const string AutoFallbackProviderName = "Auto (fallback chain)";
    private const string CustomFallbackProviderName = "Custom (fallback chain)";

    private readonly ConfigStore _configStore = new();
    private readonly CommandAvailability _availability = new();
    private readonly ProjectHistoryService _projectHistory;
    private readonly DependencyChecker _dependencyChecker;
    private readonly ClaudeExecutableLocator _claudeLocator;
    private readonly ClaudeCodeAdapter _claudeAdapter;
    private readonly TokenOptimizer.Providers.LlamaCpp.LlamaCppAdapter _llamaCppAdapter;
    private readonly ProxyCredentialStore _credentials = new();
    private readonly AntigravityAdapter _antigravityAdapter;
    private readonly JcodeHarnessAdapter _codexAdapter;
    private readonly CursorAdapter _cursorAdapter;
    private readonly GroqAdapter _groqAdapter;
    private readonly DeepSeekHarnessAdapter _deepSeekHarnessAdapter;
    private readonly OpenCodeAdapter _openCodeAdapter;
    private readonly RateLimitTracker _rateLimits;
    private readonly FallbackChainResolver _fallbackResolver;
    private readonly PythonLocator _pythonLocator;
    private readonly AgencyAgentsInstaller _agencyAgents;
    private readonly WingetInstaller _wingetInstaller;
    private readonly CompanionToolingInstaller _companionTooling;
    private readonly MasterFolderService _masterFolderService;
    private readonly ProjectClaudeMdService _claudeMdService = new();
    private readonly CompanionUninstaller _uninstaller;
    private readonly ProviderCliInstaller _providerCliInstaller = new();
    private readonly IReadOnlyList<IProviderAdapter> _providers;

    public MainViewModel()
    {
        _projectHistory = new ProjectHistoryService(_configStore);
        _pythonLocator = new PythonLocator(_availability);
        _dependencyChecker = new DependencyChecker(_availability, _pythonLocator);
        _claudeLocator = new ClaudeExecutableLocator(_configStore, _availability);
        _claudeAdapter = new ClaudeCodeAdapter(_claudeLocator, _availability);
        _llamaCppAdapter = new TokenOptimizer.Providers.LlamaCpp.LlamaCppAdapter(claudeLocator: _claudeLocator);
        _antigravityAdapter = new AntigravityAdapter(_credentials);
        _codexAdapter = new JcodeHarnessAdapter(_credentials, FallbackProvider.Codex, "openai", "Codex");
        _cursorAdapter = new CursorAdapter(_credentials);
        _groqAdapter = new GroqAdapter(_credentials, _claudeLocator);
        _deepSeekHarnessAdapter = new DeepSeekHarnessAdapter();
        _openCodeAdapter = new OpenCodeAdapter(_credentials, _claudeLocator);
        _rateLimits = new RateLimitTracker(_configStore);
        _fallbackResolver = new FallbackChainResolver(
            _claudeAdapter, _antigravityAdapter, _codexAdapter, _cursorAdapter, _groqAdapter, _deepSeekHarnessAdapter, _openCodeAdapter, _llamaCppAdapter, _rateLimits);
        _wingetInstaller = new WingetInstaller(_availability);
        _agencyAgents = new AgencyAgentsInstaller(_configStore, _availability);
        _companionTooling = new CompanionToolingInstaller(_configStore, _claudeLocator, _availability, _pythonLocator, _agencyAgents);
        _masterFolderService = new MasterFolderService(_configStore, _projectHistory);
        _uninstaller = new CompanionUninstaller(_availability, _configStore);

        _providers = new IProviderAdapter[]
        {
            _claudeAdapter, _antigravityAdapter, _openCodeAdapter, _llamaCppAdapter, _codexAdapter, _cursorAdapter, _groqAdapter, _deepSeekHarnessAdapter,
        };
        ProviderNames = new ObservableCollection<string>(_providers.Select(p => p.Name));
        SelectedProviderName = ProviderNames.FirstOrDefault() ?? string.Empty;

        _ = RefreshAllAsync();
        _ = RefreshDashboardAsync();
        _ = CheckAntigravityLoginAsync();
        _ = CheckCursorLoginAsync();
        _ = AutoDetectRagEndpointAsync();
    }

    public ObservableCollection<ProjectInfo> ProjectHistoryList { get; } = new();
    public ObservableCollection<DependencyStatus> Dependencies { get; } = new();
    public ObservableCollection<string> ProviderNames { get; }
    public ObservableCollection<FallbackChainStep> FallbackChain { get; } = new();
    public ObservableCollection<FallbackChainOrderItemViewModel> CustomChainOrder { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<ProjectCandidateViewModel> MasterFolderCandidates { get; } = new();
    public ObservableCollection<FolderTreeNode> MasterFolderTree { get; } = new();

    [ObservableProperty]
    public partial bool IsMasterFolderTreeOpen { get; set; }

    partial void OnIsMasterFolderTreeOpenChanged(bool value) =>
        MasterFolderTreeToggleLabel = value ? "Hide subfolders" : "Browse subfolders";

    [ObservableProperty]
    public partial string MasterFolderTreeToggleLabel { get; set; } = "Browse subfolders";
    public ObservableCollection<string> ModelOverrideOptions { get; } = new();

    /// <summary>
    /// Curated best-effort model lists per provider - no provider exposes a
    /// real model-catalog enumeration API, so these are static and the
    /// ModelOverride ComboBox stays IsEditable so any string still works.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> StaticModelCatalog = new Dictionary<string, string[]>
    {
        // Index 0 of each array is that provider's default/auto model (see DefaultModelFor) - kept first
        // deliberately, the rest of the array is re-sorted alphabetically wherever it's shown in full.
        ["Claude Code"] = new[] { "claude-sonnet-5", "claude-fable-5", "claude-haiku-4-5-20251001", "claude-opus-5" },
        // Verified 2026-08-20 against GET /openai/v1/models plus a live chat-completions call
        // per model with a real API key - the previous ids (llama-3.3-70b-versatile,
        // deepseek-r1-distill-llama-70b, llama-3.1-8b-instant, moonshotai/kimi-k2-instruct,
        // qwen/qwen3-32b) all 404'd or were decommissioned; these are the account's real models.
        ["Groq"] = new[] { "openai/gpt-oss-120b", "openai/gpt-oss-20b", "qwen/qwen3.6-27b", "groq/compound", "groq/compound-mini" },
        ["Codex"] = new[] { "gpt-5-codex", "gpt-5.1-codex", "gpt-5.1-codex-mini" },
        // Verified 2026-08-21 via live `agy models` - gemini-3-pro/-high do not exist; these do.
        ["Antigravity"] = new[] { "gemini-3.1-pro-high", "gemini-3.1-pro-low" },
        ["Cursor"] = new[] { "auto", "composer-1" },
        ["OpenCode"] = OpenCodeModelCatalog.ModelIds.ToArray(),
        // "Unsloth (local model)" is deliberately absent here - its models come from
        // LlamaCppModelCatalog.SupportedFamilies, live-queried per family against the
        // Hugging Face API (see RefreshModelCatalogAsync/RefreshModelOverrideOptionsAsync)
        // instead of a hand-maintained id list, so every quant that repo actually publishes
        // shows up rather than a handful of guessed ones.
    };

    /// <summary>Single default/auto model per provider - what Auto/Custom fallback chain shows, so the dropdown isn't every provider's full curated list mashed together (see RefreshModelOverrideOptionsAsync).</summary>
    private static string DefaultModelFor(string providerName)
    {
        if (providerName == "Unsloth (local model)")
        {
            var family = TokenOptimizer.Providers.LlamaCpp.LlamaCppModelCatalog.SupportedFamilies[0];
            return $"{family.RepoId}:{family.RecommendedQuant}";
        }
        return StaticModelCatalog.TryGetValue(providerName, out var curated) ? curated[0] : providerName;
    }

    /// <summary>Providers that can share one Claude Code CLI window via UnifiedModelRouter - each speaks (or can be translated to) the Anthropic Messages API. Everything else opens its own separate tool, same as today.</summary>
    private static readonly HashSet<string> BridgeableProviders = new(StringComparer.Ordinal) { "Claude Code", "Groq", "OpenCode" };

    /// <summary>Plain-language row text for the Models card - no raw ids, so a first-time user doesn't need to know what "glm-5.2" or "gpt-5-codex" means.</summary>
    private static string PlainLabelFor(string providerName, string modelId) => providerName switch
    {
        "Claude Code" => modelId switch
        {
            "claude-sonnet-5" => "Claude Sonnet 5 - Anthropic's default, balanced",
            "claude-opus-5" => "Claude Opus 5 - most capable, slower/pricier",
            "claude-haiku-4-5-20251001" => "Claude Haiku 4.5 - fastest, cheapest",
            "claude-fable-5" => "Claude Fable 5 - creative writing focus",
            _ => modelId,
        },
        "Groq" => modelId switch
        {
            "openai/gpt-oss-120b" => "GPT-OSS 120B (Groq) - large open-weight, good default",
            "openai/gpt-oss-20b" => "GPT-OSS 20B (Groq) - small, fast open-weight",
            "qwen/qwen3.6-27b" => "Qwen3.6 27B (Groq) - balanced, strong at code",
            "groq/compound" => "Compound (Groq) - agentic, tool-use built in",
            "groq/compound-mini" => "Compound Mini (Groq) - faster agentic variant",
            _ => $"{modelId} (Groq)",
        },
        "OpenCode" => OpenCodeModelCatalog.Models.FirstOrDefault(m => m.Id == modelId) is { } opencodeModel
            ? $"{modelId} (OpenCode Go) - {opencodeModel.Description}"
            : $"{modelId} (OpenCode Go)",
        // Unsloth entries get their label built directly in BuildUnslothModelGroupsAsync (quant tag + size + family) - not routed through here.
        "Codex" => $"{modelId} (opens separately - OpenAI's own tool)",
        "Cursor" => $"{modelId} (opens separately - Cursor's own tool)",
        "Antigravity" => $"{modelId} (opens separately - Google's own tool)",
        _ => modelId,
    };

    /// <summary>Every model from every provider, tick to make it show up on next launch - see LaunchTickedModelsAsync.</summary>
    public ObservableCollection<ProviderModelOptionViewModel> ModelCatalog { get; } = new();

    /// <summary>Same models as ModelCatalog, grouped by provider with a collapsible header - what the Models card actually binds to.</summary>
    public ObservableCollection<ProviderModelGroupViewModel> ModelCatalogGroups { get; } = new();

    /// <summary>Every agency agent from the agency-agents repo, tick to sync into ~/.claude/agents on next launch.</summary>
    public ObservableCollection<AgencyAgentCatalogEntry> AgencyAgentCatalog { get; } = new();

    private async Task RefreshModelCatalogAsync()
    {
        var config = await _configStore.LoadAsync();
        var ticked = new HashSet<string>(config.TickedModels ?? new List<string>(), StringComparer.Ordinal);

        ModelCatalog.Clear();
        ModelCatalogGroups.Clear();
        foreach (var provider in _providers)
        {
            if (provider == _llamaCppAdapter)
            {
                await AddUnslothModelGroupsAsync(ticked);
                continue;
            }

            if (!StaticModelCatalog.TryGetValue(provider.Name, out var models)) continue;
            var bridgeable = BridgeableProviders.Contains(provider.Name);
            var options = new List<ProviderModelOptionViewModel>();
            foreach (var modelId in models)
            {
                var option = AddModelOption(provider.Name, modelId, PlainLabelFor(provider.Name, modelId), bridgeable, ticked);
                options.Add(option);
            }
            ModelCatalogGroups.Add(new ProviderModelGroupViewModel(provider.Name, options, bridgeable));
        }
    }

    private async Task RefreshAgencyAgentCatalogAsync()
    {
        var config = await _configStore.LoadAsync();
        var ticked = new HashSet<string>(config.TickedAgencyAgents ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var agents = await _agencyAgents.ListAvailableAgentsAsync();
        AgencyAgentCatalog.Clear();
        foreach (var agent in agents)
        {
            var entry = new AgencyAgentCatalogEntry(agent.Division, agent.Slug, agent.Name, agent.Description, ticked.Contains($"{agent.Division}/{agent.Slug}"));
            entry.PropertyChanged += async (_, e) =>
            {
                if (e.PropertyName != nameof(AgencyAgentCatalogEntry.IsTicked)) return;
                await SaveTickedAgencyAgentsAsync();
            };
            AgencyAgentCatalog.Add(entry);
        }
    }

    private async Task SaveTickedAgencyAgentsAsync()
    {
        await _configStore.UpdateAsync(config =>
        {
            config.TickedAgencyAgents = AgencyAgentCatalog.Where(e => e.IsTicked).Select(e => e.Key).ToList();
        });
    }

    private ProviderModelOptionViewModel AddModelOption(string providerName, string modelId, string label, bool bridgeable, HashSet<string> ticked)
    {
        var option = new ProviderModelOptionViewModel(providerName, modelId, label, bridgeable, ticked.Contains($"{providerName}::{modelId}"));
        option.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName != nameof(ProviderModelOptionViewModel.IsTicked)) return;
            OnPropertyChanged(nameof(HasTickedModels));
            await SaveTickedModelsAsync();
        };
        ModelCatalog.Add(option);
        return option;
    }

    /// <summary>
    /// One group per model family (not one flat "Unsloth" group) - each
    /// family's own menu of EVERY quantization Hugging Face actually lists
    /// for it, fetched live via LlamaCppModelCatalog.ListQuantsAsync rather
    /// than a hand-picked shortlist. A family with zero quants found
    /// (offline, or the repo's filenames don't match either naming
    /// convention) is skipped rather than shown as an empty menu.
    /// </summary>
    private async Task AddUnslothModelGroupsAsync(HashSet<string> ticked)
    {
        foreach (var family in _llamaCppAdapter.ListSupportedFamilies())
        {
            var quants = await _llamaCppAdapter.ListQuantsAsync(family.RepoId);
            if (quants.Count == 0) continue;

            var options = new List<ProviderModelOptionViewModel>();
            foreach (var quant in quants)
            {
                var modelId = $"{family.RepoId}:{quant.Tag}";
                var sizeLabel = quant.SizeBytes is { } bytes ? $" - {bytes / 1_073_741_824.0:F1} GB" : "";
                var recommended = string.Equals(quant.Tag, family.RecommendedQuant, StringComparison.OrdinalIgnoreCase) ? " (recommended)" : "";
                options.Add(AddModelOption(_llamaCppAdapter.Name, modelId, $"{quant.Tag}{sizeLabel}{recommended}", false, ticked));
            }
            ModelCatalogGroups.Add(new ProviderModelGroupViewModel($"{family.DisplayName} - every quantization", options, isBridgeable: false));
        }
    }

    /// <summary>Tick every model that can share one Claude Code window (Claude direct, Groq, OpenCode Go) - skips Codex/Cursor/Antigravity, which always open their own separate window regardless.</summary>
    [RelayCommand]
    private async Task TickAllBridgeableModelsAsync()
    {
        foreach (var option in ModelCatalog.Where(m => m.IsBridgeable))
        {
            option.IsTicked = true;
        }
        OnPropertyChanged(nameof(HasTickedModels));
        await SaveTickedModelsAsync();
    }

    private async Task SaveTickedModelsAsync()
    {
        await _configStore.UpdateAsync(config =>
        {
            config.TickedModels = ModelCatalog.Where(m => m.IsTicked).Select(m => m.Key).ToList();
        });
    }
    /// <summary>Provider-level "priority tree" node: how well that provider's default model fits reasoning-heavy planning work vs. fast execution work, and roughly what it costs. Deliberately provider-granularity, not per-model - the preset ranking uses this to both pick a single provider/model and to reorder the fallback chain, and the fallback chain is already provider-granularity.</summary>
    private static readonly IReadOnlyDictionary<string, ProviderFitScore> ProviderFit = new Dictionary<string, ProviderFitScore>
    {
        ["Claude Code"] = new(0.95, 0.55, ModelCostTier.Premium),
        ["Antigravity"] = new(0.85, 0.55, ModelCostTier.Premium),
        ["Codex"] = new(0.85, 0.50, ModelCostTier.Premium),
        ["DeepSeek Harness"] = new(0.80, 0.50, ModelCostTier.Balanced),
        ["Cursor"] = new(0.75, 0.60, ModelCostTier.Balanced),
        ["OpenCode"] = new(0.70, 0.60, ModelCostTier.Balanced),
        ["Unsloth (local model)"] = new(0.50, 0.70, ModelCostTier.Cheap),
        ["Groq"] = new(0.55, 0.95, ModelCostTier.Cheap),
    };

    /// <summary>
    /// Ranks every known provider by fit for the current session preset (read
    /// live from session-preset.json - Planning favors reasoning strength,
    /// Execution favors speed) within the preset's cost tier, then applies the
    /// result two ways: the top pick becomes the default model-override
    /// selection, and the FULL ranked order becomes the custom fallback chain
    /// order - so Auto/Custom both try the best-fit providers first for
    /// whatever kind of session this is. This is the backend the automatic
    /// preset routing (UserPromptSubmit hook + /preset command) feeds; the
    /// manual Session-type card that used to trigger it was removed.
    /// </summary>
    private async Task ApplyIntentPresetAsync()
    {
        var preset = SessionPresetStore.ReadOrDefault(SelectedProject?.FullPath ?? string.Empty);

        var known = ProviderFit.Where(kv => _providers.Any(p => p.Name == kv.Key)).Select(kv => kv.Key).ToList();
        var ranked = SessionPresetRanker.Rank(known, name => ProviderFit[name], preset);
        if (ranked.Count == 0) return;

        SelectedProviderName = ranked[0];
        ModelOverride = DefaultModelFor(ranked[0]);

        var excluded = CustomChainOrder.Where(i => !i.IsIncluded).Select(i => i.ProviderName).ToHashSet(StringComparer.Ordinal);
        CustomChainOrder.Clear();
        var index = 0;
        foreach (var name in ranked)
        {
            CustomChainOrder.Add(new FallbackChainOrderItemViewModel(name, !excluded.Contains(name), index++));
        }
        await SaveCustomFallbackOrderAsync();

        Log($"Preset applied: {SessionPresetStore.IntentName(preset.Intent)}/{SessionPresetStore.TierName(preset.Tier)} -> {ranked[0]} first (fallback chain reordered to match).");
    }

    public ObservableCollection<string> ActiveSkills { get; } = new();
    public ObservableCollection<string> ActivePlugins { get; } = new();
    public ObservableCollection<SkillGuideEntry> SkillGuide { get; } = new();
    public ObservableCollection<SkillGuideEntry> PluginGuide { get; } = new();

    [ObservableProperty]
    public partial string ActiveProviderLabel { get; set; } = "Not resolved yet.";

    [ObservableProperty]
    public partial string TokenUsageSummaryText { get; set; } = "ccusage not installed - run Install Companion Tooling to add it.";

    [ObservableProperty]
    public partial bool IsDashboardRefreshing { get; set; }

    [ObservableProperty]
    public partial ProjectInfo? SelectedProject { get; set; }

    [ObservableProperty]
    public partial string SelectedProviderName { get; set; } = string.Empty;

    partial void OnSelectedProviderNameChanged(string value)
    {
        _ = RefreshModelOverrideOptionsAsync(value);
        _ = RefreshDashboardAsync();
    }

    /// <summary>The two ways a launch can pick its backend - an automatic fallback chain (default) or a user-drag-reordered custom chain.</summary>
    public ObservableCollection<string> LaunchModeNames { get; } = new(new[] { AutoFallbackProviderName, CustomFallbackProviderName });

    [ObservableProperty]
    public partial string SelectedLaunchMode { get; set; } = AutoFallbackProviderName;

    partial void OnSelectedLaunchModeChanged(string value)
    {
        IsCustomChainSelected = value == CustomFallbackProviderName;
        _ = RefreshDashboardAsync();
    }

    /// <summary>Gates the Custom fallback chain card - only relevant, so only shown, when "Custom (fallback chain)" is the selected launch mode.</summary>
    [ObservableProperty]
    public partial bool IsCustomChainSelected { get; set; }

    /// <summary>Single place every launch path resolves its provider from - Auto or Custom fallback chain (single-provider launch was removed; the Provider dropdown now only drives model-override options and the dashboard label).</summary>
    private Task<IProviderAdapter?> ResolveLaunchProviderAsync() => SelectedLaunchMode switch
    {
        AutoFallbackProviderName => _fallbackResolver.ResolveAsync(),
        CustomFallbackProviderName => ResolveCustomChainProviderAsync(),
        _ => _fallbackResolver.ResolveAsync(),
    };

    /// <summary>Selecting the provider IS the category (Avalonia has no built-in grouped-combo control worth the complexity here) - Auto/Custom show the union of everything since the resolved provider decides which entry actually applies.</summary>
    private async Task RefreshModelOverrideOptionsAsync(string providerName)
    {
        IEnumerable<string> options;
        if (providerName is AutoFallbackProviderName or CustomFallbackProviderName)
        {
            // Auto/Custom can resolve to any provider, but showing every provider's
            // full curated list at once was an unreadable, uncategorized wall of
            // entries - one default model per provider keeps it scannable and each
            // entry still comes straight from that provider's own model set.
            options = StaticModelCatalog.Keys.Append("Unsloth (local model)").Select(DefaultModelFor)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }
        else if (providerName == "Unsloth (local model)")
        {
            options = await GetAllUnslothModelIdsAsync();
        }
        else if (StaticModelCatalog.TryGetValue(providerName, out var curated))
        {
            options = curated;
        }
        else
        {
            options = Array.Empty<string>();
        }

        var sorted = options.OrderBy(o => o, StringComparer.OrdinalIgnoreCase).ToList();
        ModelOverrideOptions.Clear();
        foreach (var option in sorted) ModelOverrideOptions.Add(option);
    }

    /// <summary>Every "repoId:quant" id across every supported Unsloth family, live-queried - same source AddUnslothModelGroupsAsync uses, so the Model override field's options always match what the Models card actually offers.</summary>
    private async Task<IReadOnlyList<string>> GetAllUnslothModelIdsAsync()
    {
        var ids = new List<string>();
        foreach (var family in _llamaCppAdapter.ListSupportedFamilies())
        {
            var quants = await _llamaCppAdapter.ListQuantsAsync(family.RepoId);
            ids.AddRange(quants.Select(q => $"{family.RepoId}:{q.Tag}"));
        }
        return ids;
    }

    [ObservableProperty]
    public partial string NewProjectPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ModelOverride { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsolateClaudeConfig { get; set; }

    /// <summary>No manual checkbox any more - AutoDetectRagEndpointAsync probes known local endpoints (Unsloth Studio, Ollama) at startup and sets both of these automatically.</summary>
    [ObservableProperty]
    public partial bool RagEnabled { get; set; }

    [ObservableProperty]
    public partial string RagEmbeddingsUrl { get; set; } = string.Empty;

    private static readonly string[] KnownLocalEmbeddingsEndpoints = { "http://localhost:1234/v1", "http://localhost:11434/v1" };

    /// <summary>Best-effort, short-timeout probe of common local model-server ports - first one that answers wins. Silent no-op if none are running; RAG simply stays off until one is.</summary>
    private async Task AutoDetectRagEndpointAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(600) };
        foreach (var url in KnownLocalEmbeddingsEndpoints)
        {
            try
            {
                var response = await http.GetAsync($"{url}/models");
                if (!response.IsSuccessStatusCode) continue;

                RagEnabled = true;
                RagEmbeddingsUrl = url;
                await _configStore.UpdateAsync(c => c.LastDetectedRagEmbeddingsUrl = url);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { /* not reachable - try next */ }
        }
        RagEnabled = false;
        RagEmbeddingsUrl = string.Empty;
    }

    public ObservableCollection<string> ResumeModeNames { get; } = new(Enum.GetNames<SessionResumeMode>());

    [ObservableProperty]
    public partial string SelectedResumeModeName { get; set; } = nameof(SessionResumeMode.Continue);

    [ObservableProperty]
    public partial string UninstallConfirmationInput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ClearCacheConfirmationInput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CacheSummaryText { get; set; } = "Cache not scanned yet.";

    public ObservableCollection<CacheEntry> CacheProfiles { get; } = new();

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Ready.";

    [ObservableProperty]
    public partial string CodexApiKeyInput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GroqApiKeyInput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OpenCodeApiKeyInput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MasterFolderPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewProjectFolderName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AntigravityLoginStatusText { get; set; } = "Status unknown - click Check.";

    [ObservableProperty]
    public partial string CursorLoginStatusText { get; set; } = "Status unknown - click Check.";

    [ObservableProperty]
    public partial bool SetupStep1Done { get; set; }

    [ObservableProperty]
    public partial bool SetupStep2Done { get; set; }

    [ObservableProperty]
    public partial bool SetupStep3Done { get; set; }

    [ObservableProperty]
    public partial string SetupStepsSummary { get; set; } = "Get started below.";

    /// <summary>Recomputes the Setup tab's numbered-step completion state - called after anything that could change it (project/master-folder added, dependency install, companion tooling install), so the tab reads as a guided flow without being a separate modal wizard.</summary>
    private void RecomputeSetupSteps()
    {
        SetupStep1Done = ProjectHistoryList.Count > 0 || !string.IsNullOrWhiteSpace(MasterFolderPath);
        SetupStep2Done = Dependencies.Count > 0 && Dependencies.Where(d => d.Name != "Unsloth").All(d => d.IsAvailable);
        SetupStep3Done = CompanionToolingProgress >= 1;

        var doneCount = new[] { SetupStep1Done, SetupStep2Done, SetupStep3Done }.Count(d => d);
        SetupStepsSummary = doneCount == 3
            ? "All setup steps done - ready in the Session tab."
            : $"{doneCount} of 3 setup steps done.";
    }

    /// <summary>In-flight cache for RefreshAllAsync - three+ callers (the constructor's fire-and-forget chains plus every user-triggered refresh) can overlap, and each one clearing/repopulating ModelCatalogGroups independently doubled provider groups on cold launch. A semaphore would serialize 3x redundant HF network round-trips; caching the in-flight Task lets every caller share one real execution.</summary>
    private Task? _refreshAllInFlight;

    /// <summary>Guard is a plain synchronous field check/assign - no await happens while holding it, so there is no async-lock deadlock risk.</summary>
    private readonly object _refreshAllGate = new();

    [RelayCommand]
    public Task RefreshAllAsync()
    {
        lock (_refreshAllGate)
        {
            if (_refreshAllInFlight is { IsCompleted: false })
                return _refreshAllInFlight;
            _refreshAllInFlight = RefreshAllCoreAsync();
            return _refreshAllInFlight;
        }
    }

    private async Task RefreshAllCoreAsync()
    {
        IsBusy = true;
        StatusText = "Refreshing...";
        try
        {
            if (LaunchArgs.InitialProjectPath is { } initialPath &&
                ProjectHistoryService.IsValidProjectDirectory(initialPath, out _))
            {
                await _projectHistory.AddAsync(initialPath);
            }

            var history = await _projectHistory.GetHistoryAsync();
            ProjectHistoryList.Clear();
            foreach (var project in history) ProjectHistoryList.Add(project);

            if (LaunchArgs.InitialProjectPath is { } pendingPath)
            {
                SelectedProject = ProjectHistoryList.FirstOrDefault(p =>
                    string.Equals(p.FullPath, Path.GetFullPath(pendingPath), StringComparison.OrdinalIgnoreCase))
                    ?? SelectedProject;
                LaunchArgs.Consume();
            }
            SelectedProject ??= ProjectHistoryList.FirstOrDefault();

            var deps = await _dependencyChecker.CheckAllAsync();
            var unslothExe = TokenOptimizer.Providers.LlamaCpp.LlamaCppLocator.Find();
            deps = deps.Append(new DependencyStatus("Unsloth", unslothExe is not null, unslothExe, null)).ToList();
            Dependencies.Clear();
            foreach (var dep in deps) Dependencies.Add(dep);

            var chain = await _fallbackResolver.DescribeChainAsync();
            FallbackChain.Clear();
            foreach (var step in chain) FallbackChain.Add(step);

            if (CustomChainOrder.Count == 0)
            {
                await SeedCustomChainOrderAsync();
            }

            if (ModelCatalog.Count == 0)
            {
                await RefreshModelCatalogAsync();
            }

            if (AgencyAgentCatalog.Count == 0)
            {
                await RefreshAgencyAgentCatalogAsync();
            }

            if (string.IsNullOrWhiteSpace(MasterFolderPath))
            {
                MasterFolderPath = await _masterFolderService.GetMasterFolderAsync() ?? string.Empty;
            }
            if (!string.IsNullOrWhiteSpace(MasterFolderPath))
            {
                await RefreshMasterFolderCandidatesAsync();
            }

            if (SelectedProject is { FullPath: { } projectPath } && File.Exists(SessionPresetStore.FilePathFor(projectPath)))
            {
                await ApplyIntentPresetAsync();
            }

            StatusText = "Ready.";
        }
        catch (Exception ex)
        {
            StatusText = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RecomputeSetupSteps();
        }
    }

    /// <summary>First run: seed the custom chain from AppConfig.CustomFallbackOrder if saved, otherwise every known provider in its default order, all included.</summary>
    private async Task SeedCustomChainOrderAsync()
    {
        var config = await _configStore.LoadAsync();
        var excluded = new HashSet<string>(config.CustomFallbackExcluded ?? new List<string>(), StringComparer.Ordinal);
        var savedOrder = config.CustomFallbackOrder;
        var names = savedOrder is { Count: > 0 }
            ? savedOrder.Where(n => _providers.Any(p => p.Name == n)).Concat(_providers.Select(p => p.Name).Except(savedOrder)).ToList()
            : _providers.Select(p => p.Name).ToList();

        CustomChainOrder.Clear();
        var index = 0;
        foreach (var name in names)
        {
            CustomChainOrder.Add(new FallbackChainOrderItemViewModel(name, !excluded.Contains(name), index++));
        }
    }

    [RelayCommand]
    private async Task SaveCustomFallbackOrderAsync()
    {
        var order = CustomChainOrder.OrderBy(i => i.SortIndex).Select(i => i.ProviderName).ToList();
        var excluded = CustomChainOrder.Where(i => !i.IsIncluded).Select(i => i.ProviderName).ToList();
        await _configStore.UpdateAsync(config =>
        {
            config.CustomFallbackOrder = order;
            config.CustomFallbackExcluded = excluded;
        });
        Log("Custom fallback chain saved.");
    }

    /// <summary>Called from MainWindow's Closing event - stops claude-mem's shared worker if this was the last open Claude Code window, best-effort.</summary>
    public Task OnWindowClosingAsync() => _companionTooling.StopClaudeMemWorkerIfLastWindowAsync();

    /// <summary>Drag-reorder support: swaps two rows' SortIndex, called by the drag/drop behavior in MainWindow.axaml.cs.</summary>
    public void ReorderCustomChain(int fromIndex, int toIndex)
    {
        var ordered = CustomChainOrder.OrderBy(i => i.SortIndex).ToList();
        if (fromIndex < 0 || fromIndex >= ordered.Count || toIndex < 0 || toIndex >= ordered.Count || fromIndex == toIndex) return;

        var item = ordered[fromIndex];
        ordered.RemoveAt(fromIndex);
        ordered.Insert(toIndex, item);
        for (var i = 0; i < ordered.Count; i++) ordered[i].SortIndex = i;
    }

    [RelayCommand]
    private void MoveCustomChainItemUp(FallbackChainOrderItemViewModel item) => ReorderCustomChain(item.SortIndex, item.SortIndex - 1);

    [RelayCommand]
    private void MoveCustomChainItemDown(FallbackChainOrderItemViewModel item) => ReorderCustomChain(item.SortIndex, item.SortIndex + 1);

    private Task<IProviderAdapter?> ResolveCustomChainProviderAsync() =>
        _fallbackResolver.ResolveCustomAsync(CustomChainOrder.Where(i => i.IsIncluded).OrderBy(i => i.SortIndex).Select(i => i.ProviderName).ToList());

    [RelayCommand]
    private async Task SetMasterFolderAsync()
    {
        if (!MasterFolderService.IsValidMasterFolder(MasterFolderPath, out var error))
        {
            Log($"Cannot use this master folder: {error}");
            return;
        }

        await _masterFolderService.SetMasterFolderAsync(MasterFolderPath);
        await RefreshMasterFolderCandidatesAsync();
        Log($"Master folder set: {MasterFolderPath}");
    }

    [RelayCommand]
    private async Task RefreshMasterFolderCandidatesAsync()
    {
        if (!MasterFolderService.IsValidMasterFolder(MasterFolderPath, out var error))
        {
            Log($"Master folder unavailable: {error}");
            return;
        }

        var candidates = await _masterFolderService.ListCandidatesAsync(MasterFolderPath);
        MasterFolderCandidates.Clear();
        foreach (var candidate in candidates) MasterFolderCandidates.Add(new ProjectCandidateViewModel(candidate));
    }

    /// <summary>Toggles and (re)builds the recursive subdirectory tree shown when the master-folder label is clicked - separate from the flat MasterFolderCandidates list, which only shows immediate subfolders.</summary>
    [RelayCommand]
    private async Task ShowMasterFolderTreeAsync()
    {
        if (IsMasterFolderTreeOpen)
        {
            IsMasterFolderTreeOpen = false;
            return;
        }

        if (!MasterFolderService.IsValidMasterFolder(MasterFolderPath, out var error))
        {
            Log($"Master folder unavailable: {error}");
            return;
        }

        var root = await MasterFolderService.BuildSubdirectoryTreeAsync(MasterFolderPath);
        MasterFolderTree.Clear();
        foreach (var child in root.Children) MasterFolderTree.Add(child);
        IsMasterFolderTreeOpen = true;
    }

    /// <summary>Double-click on a subdirectory tree node: launches a session directly against that path, under AutoLaunchProviderName if configured ("Auto (fallback chain)" or "Custom (fallback chain)"), otherwise the Auto fallback chain.</summary>
    [RelayCommand]
    private async Task LaunchAtPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

        var config = await _configStore.LoadAsync();
        var launchProviderName = string.IsNullOrWhiteSpace(config.AutoLaunchProviderName)
            ? AutoFallbackProviderName
            : config.AutoLaunchProviderName;

        IProviderAdapter? provider = launchProviderName switch
        {
            AutoFallbackProviderName => await _fallbackResolver.ResolveAsync(),
            CustomFallbackProviderName => await ResolveCustomChainProviderAsync(),
            _ => await _fallbackResolver.ResolveAsync(),
        };

        if (provider is null)
        {
            Log($"No provider available to launch {path}.");
            return;
        }

        try
        {
            if (provider == _claudeAdapter || provider == _groqAdapter)
            {
                await _companionTooling.EnsureSharedClaudeEnvironmentAsync(path);
                await PrepareProjectDirectiveAsync(path, provider);
            }

            var options = new SessionLaunchOptions(
                path,
                ResolveEffectiveModel(),
                IsolateClaudeConfig,
                Enum.Parse<SessionResumeMode>(SelectedResumeModeName));
            var handle = await provider.LaunchSessionAsync(options);
            await _projectHistory.AddAsync(path);
            TrackRateLimitOutcome(handle);
            Log($"Launched {handle.ProviderName} for {path} (pid {handle.ProcessId?.ToString() ?? "n/a"}).");
            IsMasterFolderTreeOpen = false;
            await RefreshAllAsync();
        }
        catch (Exception ex)
        {
            Log($"Launch failed for {path}: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task CreateProjectFolderAsync()
    {
        if (!MasterFolderService.IsValidMasterFolder(MasterFolderPath, out var error))
        {
            Log($"Set a master folder first: {error}");
            return;
        }
        if (string.IsNullOrWhiteSpace(NewProjectFolderName))
        {
            Log("Enter a folder name first.");
            return;
        }

        var created = MasterFolderService.CreateProjectFolder(MasterFolderPath, NewProjectFolderName);
        if (created is null)
        {
            Log($"Could not create folder: {NewProjectFolderName}");
            return;
        }

        NewProjectFolderName = string.Empty;
        Log($"Created: {created}");
        await RefreshMasterFolderCandidatesAsync();
    }

    /// <summary>
    /// Opens every checked candidate as its own independent session -
    /// v5.0+'s "several project windows at once" model, ported from the
    /// picker's numbered multi-select / 'a' (open all).
    /// </summary>
    [RelayCommand]
    private async Task LaunchSelectedCandidatesAsync()
    {
        var selected = MasterFolderCandidates.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0)
        {
            Log("Check one or more projects in the master folder list first.");
            return;
        }

        var provider = await ResolveLaunchProviderAsync();

        if (provider is null)
        {
            Log("No provider available to launch with.");
            return;
        }

        foreach (var candidate in selected)
        {
            try
            {
                if (provider == _claudeAdapter || provider == _groqAdapter)
                {
                    await _companionTooling.EnsureSharedClaudeEnvironmentAsync(candidate.FullPath);
                    await PrepareProjectDirectiveAsync(candidate.FullPath, provider);
                }

                var options = new SessionLaunchOptions(
                    candidate.FullPath,
                    ResolveEffectiveModel(),
                    IsolateClaudeConfig,
                    Enum.Parse<SessionResumeMode>(SelectedResumeModeName));
                var handle = await provider.LaunchSessionAsync(options);
                await _projectHistory.AddAsync(candidate.FullPath);
                TrackRateLimitOutcome(handle);
                Log($"Launched {handle.ProviderName} for {candidate.Name} (pid {handle.ProcessId?.ToString() ?? "n/a"}).");
                candidate.IsSelected = false;
            }
            catch (Exception ex)
            {
                Log($"Launch failed for {candidate.Name}: {ex.Message}");
            }
        }

        await RefreshAllAsync();
    }

    [ObservableProperty]
    public partial double CompanionToolingProgress { get; set; }

    [ObservableProperty]
    public partial bool IsCompanionToolingInstalling { get; set; }

    [ObservableProperty]
    public partial double DependencyInstallProgress { get; set; }

    [ObservableProperty]
    public partial bool IsDependencyInstalling { get; set; }

    /// <summary>Named async best-effort installers run in a fixed, numbered sequence so StatusText can show "N/total: name" live instead of only a final summary.</summary>
    private (string Name, Func<Task<bool>> Install)[] CompanionToolingSteps => new (string, Func<Task<bool>>)[]
    {
        ("Graphify", _companionTooling.InstallGraphifyAsync),
        ("claude-mem", _companionTooling.InstallClaudeMemAsync),
        ("headroom", _companionTooling.InstallHeadroomStatuslineAsync),
        ("rtk", _companionTooling.InstallRtkCliAsync),
        ("context7", _companionTooling.RegisterContext7McpAsync),
        ("context-mode", _companionTooling.InstallContextModeMcpAsync),
        ("caveman", _companionTooling.InstallCavemanPluginAsync),
        ("ponytail", _companionTooling.InstallPonytailPluginAsync),
        ("claude-md-management", _companionTooling.InstallClaudeMdManagementPluginAsync),
        ("impeccable", _companionTooling.InstallImpeccableSkillAsync),
        ("task-observer", _companionTooling.InstallTaskObserverSkillAsync),
        ("Unsloth CLI (local model)", _providerCliInstaller.InstallUnslothCliAsync),
        ("OpenCode CLI", _providerCliInstaller.InstallOpenCodeCliAsync),
        ("jcode CLI (Codex)", _providerCliInstaller.InstallJcodeCliAsync),
        ("Antigravity CLI", _providerCliInstaller.InstallAntigravityCliAsync),
        ("Cursor CLI", _providerCliInstaller.InstallCursorCliAsync),
        ("Antigravity plugin parity", async () => { await _providerCliInstaller.SyncClaudePluginsIntoAntigravityAsync(); return true; }),
        ("DeepSeek Harness (dsh)", _providerCliInstaller.InstallDeepSeekHarnessCliAsync),
        ("DeepSeek Harness plugin parity", async () => { await _providerCliInstaller.SyncClaudePluginsIntoDeepSeekHarnessAsync(); return true; }),
        ("ccusage (token/cost tracking)", _providerCliInstaller.InstallCcusageAsync),
    };

    [RelayCommand]
    private async Task InstallCompanionToolingAsync()
    {
        IsBusy = true;
        IsCompanionToolingInstalling = true;
        CompanionToolingProgress = 0;
        try
        {
            var steps = CompanionToolingSteps;
            var total = steps.Length + (SelectedProject is not null ? 1 : 0);
            var stepNumber = 0;

            foreach (var (name, install) in steps)
            {
                stepNumber++;
                StatusText = $"Installing companion tooling... ({stepNumber}/{total}: {name})";
                CompanionToolingProgress = (double)stepNumber / total;
                var ok = await install();
                Log(ok ? $"{name}: OK" : $"{name}: failed");
            }

            if (SelectedProject is not null)
            {
                stepNumber++;
                StatusText = $"Installing companion tooling... ({stepNumber}/{total}: code intelligence)";
                CompanionToolingProgress = (double)stepNumber / total;
                var codeIntelPlugin = await _companionTooling.InstallCodeIntelligencePluginAsync(SelectedProject.FullPath);
                Log(codeIntelPlugin is not null ? $"code intelligence: {codeIntelPlugin}" : "code intelligence: not applicable");
            }

            var active = await _companionTooling.DescribeActiveCompressionAsync();
            foreach (var line in active) Log($"Compression active: {line}");

            CompanionToolingProgress = 1;
            StatusText = "Ready.";
        }
        finally
        {
            IsBusy = false;
            IsCompanionToolingInstalling = false;
            RecomputeSetupSteps();
        }
    }

    [RelayCommand]
    private async Task InstallMissingDependenciesAsync()
    {
        var missing = Dependencies.Where(d => !d.IsAvailable).Select(d => d.Name).ToList();
        if (missing.Count == 0)
        {
            Log("No missing dependencies to install.");
            return;
        }

        IsBusy = true;
        IsDependencyInstalling = true;
        DependencyInstallProgress = 0;
        StatusText = "Installing missing dependencies via winget...";
        try
        {
            var progress = new Progress<InstallStepProgress>(p =>
            {
                StatusText = $"Installing missing dependencies... ({p.StepNumber}/{p.TotalSteps}: {p.Name} - {p.Status})";
                DependencyInstallProgress = p.TotalSteps == 0 ? 0 : (double)p.StepNumber / p.TotalSteps;
                if (p.Status is "done" or "failed") Log($"{p.Name}: {p.Status} (winget)");
            });

            var installed = await _wingetInstaller.InstallMissingAsync(missing, progress);
            if (installed.Count == 0) Log("winget could not install any of the missing dependencies (unavailable or all failed).");
            DependencyInstallProgress = 1;
            await RefreshAllAsync();
        }
        finally
        {
            IsBusy = false;
            IsDependencyInstalling = false;
        }
    }

    /// <summary>
    /// Requires the user to type UNINSTALL first - the GUI's equivalent of
    /// the original console picker's "type rm, then press X to confirm":
    /// deliberate friction so this never fires from a stray click.
    /// </summary>
    [RelayCommand]
    private async Task UninstallEverythingAsync()
    {
        if (!string.Equals(UninstallConfirmationInput.Trim(), "UNINSTALL", StringComparison.Ordinal))
        {
            Log("Type UNINSTALL (exact case) in the confirmation box first.");
            return;
        }

        IsBusy = true;
        StatusText = "Uninstalling everything...";
        try
        {
            var log = await _uninstaller.UninstallAllAsync();
            foreach (var line in log) Log(line);
            UninstallConfirmationInput = string.Empty;
            await RefreshAllAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Sizes %AppData%\TokenOptimizer\claude-profiles - the one cache this app
    /// grows without bound (one full ~/.claude copy per distinct project ever
    /// launched with -IsolateClaudeConfig, never previously cleaned up). Scans
    /// on a background thread since a large profile set means walking many
    /// files, and this runs on tab open, not just on demand.
    /// </summary>
    [RelayCommand]
    private async Task RefreshCacheInfoAsync()
    {
        var entries = await Task.Run(() => CacheManagementService.ListClaudeProfiles());
        CacheProfiles.Clear();
        foreach (var entry in entries) CacheProfiles.Add(entry);

        var totalBytes = entries.Sum(e => e.SizeBytes);
        CacheSummaryText = entries.Count == 0
            ? "No isolated Claude profiles cached."
            : $"{entries.Count} isolated Claude profile(s), {totalBytes / 1_048_576.0:F1} MB total.";
    }

    /// <summary>Removes profiles untouched for 30+ days - safe by construction (age-bounded), so this needs no confirmation unlike the full clear below.</summary>
    [RelayCommand]
    private async Task ClearStaleCacheAsync()
    {
        var removed = await Task.Run(() => CacheManagementService.DeleteStaleProfiles(TimeSpan.FromDays(30)));
        Log(removed == 0 ? "No stale cached profiles (30+ days unused) to remove." : $"Removed {removed} stale cached profile(s).");
        await RefreshCacheInfoAsync();
    }

    /// <summary>Requires the user to type CLEAR first - same deliberate-friction pattern as UninstallEverythingAsync, since this deletes every isolated project's saved Claude settings/history, not just stale ones.</summary>
    [RelayCommand]
    private async Task ClearAllCacheAsync()
    {
        if (!string.Equals(ClearCacheConfirmationInput.Trim(), "CLEAR", StringComparison.Ordinal))
        {
            Log("Type CLEAR (exact case) in the confirmation box first.");
            return;
        }

        await Task.Run(() => CacheManagementService.ClearAllProfiles());
        ClearCacheConfirmationInput = string.Empty;
        Log("Cleared all cached isolated Claude profiles.");
        await RefreshCacheInfoAsync();
    }

    /// <summary>
    /// Any explicit ModelOverride text wins - otherwise null, and the
    /// provider adapter picks its own default (e.g. LlamaCppAdapter falls
    /// back to its first supported model family).
    /// </summary>
    private string? ResolveEffectiveModel() =>
        string.IsNullOrWhiteSpace(ModelOverride) ? null : ModelOverride;

    /// <summary>
    /// Resolves and displays which provider is actually in effect right now
    /// (live-resolved for Auto/Custom, verbatim otherwise) plus what's
    /// installed in the Claude Code terminal every provider ultimately routes
    /// through (see Phase 1: Antigravity/Cursor are CLI-only, Groq/Codex/
    /// llama.cpp point Claude Code at a different backend - the skills/plugins
    /// active are always Claude Code's, regardless of which model is behind it).
    /// </summary>
    [RelayCommand]
    private async Task RefreshDashboardAsync()
    {
        IsDashboardRefreshing = true;
        try
        {
            var resolved = await ResolveLaunchProviderAsync();

            ActiveProviderLabel = SelectedLaunchMode switch
            {
                AutoFallbackProviderName or CustomFallbackProviderName =>
                    resolved is not null ? $"{SelectedLaunchMode} -> currently resolves to {resolved.Name}" : $"{SelectedLaunchMode} -> nothing available right now",
                _ => resolved is not null ? $"Auto (fallback chain) -> currently resolves to {resolved.Name}" : "Auto (fallback chain) -> nothing available right now",
            };

            var skills = await _claudeAdapter.ListInstalledSkillsAsync();
            ActiveSkills.Clear();
            foreach (var skill in skills) ActiveSkills.Add(skill);

            var plugins = await _claudeAdapter.ListInstalledPluginsAsync();
            ActivePlugins.Clear();
            foreach (var plugin in plugins) ActivePlugins.Add(plugin);

            var (skillGuide, pluginGuide) = await Task.Run(() =>
                (SkillCatalogService.ListSkillGuide(), SkillCatalogService.ListPluginGuide()));
            SkillGuide.Clear();
            foreach (var entry in skillGuide) SkillGuide.Add(entry);
            PluginGuide.Clear();
            foreach (var entry in pluginGuide) PluginGuide.Add(entry);

            var usage = await TokenUsageReader.GetSummaryAsync();
            TokenUsageSummaryText = usage is null
                ? "ccusage not installed - run Install Companion Tooling to add it."
                : $"Today: {usage.TodayTokens:N0} tokens, ${usage.TodayCostUsd:F2} - All-time: {usage.AllTimeTokens:N0} tokens, ${usage.AllTimeCostUsd:F2}";
        }
        finally
        {
            IsDashboardRefreshing = false;
        }
    }

    [RelayCommand]
    private void SetCodexCredential()
    {
        if (string.IsNullOrWhiteSpace(CodexApiKeyInput))
        {
            Log("Enter any value to mark Codex as opted-in (jcode manages its own OAuth via `jcode login --provider openai`).");
            return;
        }

        _credentials.SetCredential(FallbackProvider.Codex, CodexApiKeyInput);
        CodexApiKeyInput = string.Empty;
        Log("Codex opt-in marker stored. jcode manages OpenAI auth separately — run `jcode login --provider openai` to complete setup.");
        _ = RefreshAllAsync();
    }

    [RelayCommand]
    private void SetGroqCredential()
    {
        if (string.IsNullOrWhiteSpace(GroqApiKeyInput))
        {
            Log("Enter a GROQ_API_KEY first.");
            return;
        }

        _credentials.SetCredential(FallbackProvider.Groq, GroqApiKeyInput);
        GroqApiKeyInput = string.Empty;
        Log("Groq credential stored (DPAPI-encrypted, this account only).");
        _ = RefreshAllAsync();
    }

    [RelayCommand]
    private void SetOpenCodeCredential()
    {
        if (string.IsNullOrWhiteSpace(OpenCodeApiKeyInput))
        {
            Log("Enter an OpenCode Go API key first (sign in at https://opencode.ai/zen to get one).");
            return;
        }

        _credentials.SetCredential(FallbackProvider.OpenCode, OpenCodeApiKeyInput);
        OpenCodeApiKeyInput = string.Empty;
        Log("OpenCode Go credential stored (DPAPI-encrypted, this account only).");
        _ = RefreshAllAsync();
    }

    /// <summary>Actually opens the Antigravity CLI (no separate "login" subcommand - it prompts sign-in on first interactive run) rather than only flipping an internal opt-in flag with nothing visible happening. Does NOT mark the provider available itself - CheckAntigravityLoginAsync verifies that for real.</summary>
    [RelayCommand]
    private void LoginAntigravity()
    {
        var exe = ExecutableLocators.FindAntigravity();
        if (exe is null)
        {
            Log("Antigravity CLI not found - install it from the Setup tab first.");
            return;
        }

        try
        {
            ProcessLaunchHelper.Start(exe, string.Empty, null);
            Log("Antigravity CLI opened in a new window - complete sign-in there, then click Check.");
            AntigravityLoginStatusText = "Sign-in window opened - complete it, then click Check.";
        }
        catch (Exception ex)
        {
            Log($"Could not launch Antigravity CLI: {ex.Message}");
        }
    }

    [RelayCommand]
    private void LoginCursor()
    {
        var exe = ExecutableLocators.FindCursor();
        if (exe is null)
        {
            Log("Cursor CLI not found - install it from the Setup tab first.");
            return;
        }

        try
        {
            ProcessLaunchHelper.Start(exe, "login", null);
            Log("Cursor login opened in a new window - complete sign-in there, then click Check.");
            CursorLoginStatusText = "Sign-in window opened - complete it, then click Check.";
        }
        catch (Exception ex)
        {
            Log($"Could not launch Cursor login: {ex.Message}");
        }
    }

    /// <summary>Real verification, not a stored click-once flag - see ProviderCliInstaller.IsAntigravityLoggedInAsync. Only sets/clears the fallback-chain credential based on what's ACTUALLY true right now.</summary>
    [RelayCommand]
    private async Task CheckAntigravityLoginAsync()
    {
        var loggedIn = await _providerCliInstaller.IsAntigravityLoggedInAsync();
        if (loggedIn)
        {
            _credentials.SetCredential(FallbackProvider.Antigravity, "verified-login");
            AntigravityLoginStatusText = "✓ Logged in - available in the fallback chain.";
            Log("Antigravity: verified logged in.");
        }
        else
        {
            _credentials.RemoveCredential(FallbackProvider.Antigravity);
            AntigravityLoginStatusText = "Not logged in yet - click Login, sign in, then Check again.";
        }
        await RefreshAllAsync();
    }

    [RelayCommand]
    private async Task CheckCursorLoginAsync()
    {
        var loggedIn = await _providerCliInstaller.IsCursorLoggedInAsync();
        if (loggedIn)
        {
            _credentials.SetCredential(FallbackProvider.Cursor, "verified-login");
            CursorLoginStatusText = "✓ Logged in - available in the fallback chain.";
            Log("Cursor: verified logged in.");
        }
        else
        {
            _credentials.RemoveCredential(FallbackProvider.Cursor);
            CursorLoginStatusText = "Not logged in yet - click Login, sign in, then Check again.";
        }
        await RefreshAllAsync();
    }

    [RelayCommand]
    private async Task BrowseProjectFolderAsync()
    {
        var picked = await FolderPickerService.PickFolderAsync("Select a project folder");
        if (picked is not null) NewProjectPath = picked;
    }

    [RelayCommand]
    private async Task BrowseMasterFolderAsync()
    {
        var picked = await FolderPickerService.PickFolderAsync("Select a master folder");
        if (picked is not null) MasterFolderPath = picked;
    }

    [RelayCommand]
    private async Task AddProjectAsync()
    {
        if (string.IsNullOrWhiteSpace(NewProjectPath))
        {
            Log("Enter a project path first.");
            return;
        }

        if (!ProjectHistoryService.IsValidProjectDirectory(NewProjectPath, out var error))
        {
            Log($"Cannot use this folder: {error}");
            return;
        }

        await _projectHistory.AddAsync(NewProjectPath);
        NewProjectPath = string.Empty;
        await RefreshAllAsync();
    }

    [RelayCommand]
    private async Task ExportHandoffAsync()
    {
        if (SelectedProject is null)
        {
            Log("Select a project first.");
            return;
        }

        try
        {
            var claudeConfigDir = SessionHandoffExporter.GetEffectiveClaudeConfigDir(SelectedProject.FullPath, isolateConfig: false);
            var handoffFile = SessionHandoffExporter.Export(SelectedProject.FullPath, claudeConfigDir);
            Log($"Session handoff exported: {handoffFile}");
        }
        catch (Exception ex)
        {
            Log($"Handoff export failed: {ex.Message}");
        }
    }

    /// <summary>Any model ticked in the Models card - Launch is disabled otherwise.</summary>
    public bool HasTickedModels => ModelCatalog.Any(m => m.IsTicked);

    /// <summary>
    /// One click, everything ticked in the Models card shows up in Claude
    /// Code's own /model picker. Bridgeable models (Claude direct, Groq,
    /// OpenCode Go, Unsloth/local) route to their upstream through
    /// UnifiedModelRouter. Non-bridgeable providers (Codex, Cursor,
    /// Antigravity, DeepSeek Harness) also appear in the same picker for
    /// visibility, and selecting one routes to the auto fallback delegate
    /// (Claude Code -> OpenCode Go) so the Claude Code window stays useful;
    /// those providers additionally open their own separate window, one per
    /// provider, exactly as before.
    /// </summary>
    [RelayCommand]
    private async Task LaunchTickedModelsAsync()
    {
        if (SelectedProject is null) { Log("Select a project first."); return; }

        var ticked = ModelCatalog.Where(m => m.IsTicked).ToList();
        if (ticked.Count == 0) { Log("Tick at least one model above."); return; }

        IsBusy = true;
        StatusText = "Launching...";
        try
        {
            using var instanceLock = InstanceLock.TryAcquire(SelectedProject.FullPath);
            if (instanceLock is null)
            {
                Log("Another setup is already running for this project - launching anyway (setup skipped).");
            }

            var resumeMode = Enum.Parse<SessionResumeMode>(SelectedResumeModeName);
            var bridged = ticked.Where(m => m.IsBridgeable).ToList();
            var standalone = ticked.Where(m => !m.IsBridgeable).GroupBy(m => m.ProviderName).ToList();

            if (bridged.Count > 0)
            {
                await _companionTooling.EnsureSharedClaudeEnvironmentAsync(SelectedProject.FullPath);
                await PrepareProjectDirectiveAsync(SelectedProject.FullPath, _claudeAdapter);

                var claudeExe = await _claudeLocator.FindAsync()
                                 ?? throw new InvalidOperationException("Claude Code executable not found - install it first.");

                await ClaudeCodeAdapter.RefreshPluginMarketplacesAsync(claudeExe);

var preset = SessionPresetStore.ReadOrDefault(SelectedProject.FullPath);
                ProviderFitScore FitOf(string key) =>
                    ModelFitCatalog.ByModelKey.TryGetValue(key, out var modelFit)
                        ? modelFit
                        : ProviderFit.TryGetValue(key.Split("::")[0], out var providerFit) ? providerFit : new ProviderFitScore(0.5, 0.5, ModelCostTier.Balanced);
                var orderedBridged = SessionPresetRanker.RankModels(bridged, m => m.Key, FitOf, preset);

                var routes = BuildModelRoutesForTickedModels(ticked);
                var router = new UnifiedModelRouter(routes, autoFallbackDelegate: ResolveAutoFallbackRouteAsync);
                await router.StartAsync();

                var defaultModelId = orderedBridged[0].ModelId;
                var args = new List<string> { $"--model {defaultModelId}" };
                var resumeFlag = resumeMode switch { SessionResumeMode.Continue => "--continue", SessionResumeMode.Pick => "--resume", _ => null };
                if (resumeFlag is not null) args.Add(resumeFlag);

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = claudeExe,
                    Arguments = string.Join(' ', args),
                    WorkingDirectory = SelectedProject.FullPath,
                    UseShellExecute = false,
                };
                psi.EnvironmentVariables["ANTHROPIC_BASE_URL"] = router.BaseUrl;
                psi.EnvironmentVariables["CLAUDE_MEM_WORKER_PORT"] = CompanionToolingInstaller.IsolatedWorkerPort.ToString();
                psi.EnvironmentVariables["CLAUDE_MEM_DATA_DIR"] = CompanionToolingInstaller.IsolatedDataDir;
                if (IsolateClaudeConfig)
                {
                    psi.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = IsolatedClaudeProfileService.GetOrCreateProfileDir(SelectedProject.FullPath);
                }

                var process = System.Diagnostics.Process.Start(psi);
                var handle = new ProcessSessionHandle("Claude Code", SelectedProject.FullPath, process, watchForRateLimit: true);
                _ = handle.RateLimitOutcome.ContinueWith(async _ => await router.DisposeAsync());
                await _projectHistory.AddAsync(SelectedProject.FullPath);
                Log($"Launched one Claude Code window (pid {handle.ProcessId?.ToString() ?? "n/a"}) with models available: {string.Join(", ", ticked.Select(m => m.ModelId))}");
                TrackRateLimitOutcome(handle);
            }

            foreach (var group in standalone)
            {
                var provider = _providers.FirstOrDefault(p => p.Name == group.Key);
                if (provider is null) continue;
                if (!await provider.IsAvailableAsync())
                {
                    Log($"{provider.Name} is not available - skipped.");
                    continue;
                }

                var options = new SessionLaunchOptions(SelectedProject.FullPath, group.First().ModelId, IsolateClaudeConfig, resumeMode);
                var handle = await provider.LaunchSessionAsync(options);
                await _projectHistory.AddAsync(SelectedProject.FullPath);
                Log($"Launched {handle.ProviderName} separately (pid {handle.ProcessId?.ToString() ?? "n/a"}).");
                TrackRateLimitOutcome(handle);
            }

            StatusText = "Ready.";
        }
        catch (Exception ex)
        {
            Log($"Launch failed: {ex.Message}");
            StatusText = "Ready.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Builds a route for every ticked model so Claude Code's /model picker can show all selected models, not just the bridgeable ones.</summary>
    private Dictionary<string, UnifiedModelRouter.ModelRoute> BuildModelRoutesForTickedModels(IReadOnlyList<ProviderModelOptionViewModel> ticked)
    {
        var routes = new Dictionary<string, UnifiedModelRouter.ModelRoute>(StringComparer.Ordinal);
        foreach (var m in ticked)
        {
            if (TryBuildDirectRoute(m.ProviderName, out var directRoute))
            {
                routes[m.ModelId] = directRoute;
            }
            // Non-bridgeable models intentionally do NOT get a direct route; they fall through
            // to the auto-fallback delegate at request time so they still appear in /model picker
            // but selecting them routes to the next available bridgeable provider.
        }
        return routes;
    }

    private bool TryBuildDirectRoute(string providerName, out UnifiedModelRouter.ModelRoute route)
    {
        switch (providerName)
        {
            case "Claude Code":
                route = new UnifiedModelRouter.ModelRoute(new Uri("https://api.anthropic.com"), RouteKind.AnthropicPassthrough);
                return true;
            case "Groq":
                route = new UnifiedModelRouter.ModelRoute(new Uri("https://api.groq.com/openai/v1"), RouteKind.OpenAiTranslate, () => _credentials.GetCredentialPlainText(FallbackProvider.Groq));
                return true;
            case "OpenCode":
                route = new UnifiedModelRouter.ModelRoute(new Uri("https://opencode.ai/zen/go"), RouteKind.AnthropicPassthrough, () => _credentials.GetCredentialPlainText(FallbackProvider.OpenCode));
                return true;
            default:
                route = null!;
                return false;
        }
    }

    /// <summary>Resolves the "auto" model and non-bridgeable model selections to the next available bridgeable provider in the auto fallback chain (Claude Code, OpenCode Go). Unsloth and Antigravity are skipped because they require runtime server startup that can't happen inside a per-request router delegate. Which of the available bridgeable providers wins is biased live by the current session preset (session-preset.json) - Quality prefers the higher-ReasoningScore provider, Cost-effective the cheaper/faster one, per the same ranking math ApplyIntentPresetAsync uses.</summary>
    private async Task<UnifiedModelRouter.ModelRoute?> ResolveAutoFallbackRouteAsync()
    {
        var candidates = new List<string>();
        if (!await _rateLimits.IsRateLimitedAsync(FallbackProvider.Claude) && await _claudeAdapter.IsAvailableAsync())
        {
            candidates.Add("Claude Code");
        }
        if (!await _rateLimits.IsRateLimitedAsync(FallbackProvider.OpenCode) && await _openCodeAdapter.IsAvailableAsync())
        {
            candidates.Add("OpenCode");
        }
        if (candidates.Count == 0) return null;

        var preset = SessionPresetStore.ReadOrDefault(SelectedProject?.FullPath ?? string.Empty);
        var ranked = SessionPresetRanker.Rank(candidates, name => ProviderFit[name], preset);

        foreach (var name in ranked)
        {
            if (name == "Claude Code")
            {
                return new UnifiedModelRouter.ModelRoute(new Uri("https://api.anthropic.com"), RouteKind.AnthropicPassthrough);
            }
            if (name == "OpenCode")
            {
                return new UnifiedModelRouter.ModelRoute(new Uri("https://opencode.ai/zen/go"), RouteKind.AnthropicPassthrough, () => _credentials.GetCredentialPlainText(FallbackProvider.OpenCode));
            }
        }

        return null;
    }

    /// <summary>Delegates to the shared ProjectSessionPrep so the CLI host (used by the VS Code extension) runs the exact same pre-launch checks as this UI.</summary>
    private Task PrepareProjectDirectiveAsync(string projectDirectory, IProviderAdapter? provider = null)
    {
        Uri? ragEmbeddingsUrl = RagEnabled && Uri.TryCreate(RagEmbeddingsUrl, UriKind.Absolute, out var parsedRagUri)
            ? parsedRagUri
            : null;
        return ProjectSessionPrep.PrepareProjectDirectiveAsync(projectDirectory, _claudeMdService, _availability, Log, provider, ragEmbeddingsUrl);
    }

    private void Log(string message) => LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {message}");

    /// <summary>
    /// Fire-and-forget: waits (possibly hours) for the launched session's
    /// process to exit, then - if the rate-limit watcher caught a usage-limit
    /// banner during that session - persists a cooldown so the fallback
    /// chain's NEXT resolve skips this provider instead of retrying an
    /// already-exhausted backend. Mirrors Save-RateLimitDetectionResult.
    /// </summary>
    private void TrackRateLimitOutcome(ISessionHandle handle)
    {
        FallbackProvider? provider = handle.ProviderName switch
        {
            "Claude Code" => FallbackProvider.Claude,
            "Antigravity" => FallbackProvider.Antigravity,
            "Codex" => FallbackProvider.Codex,
            "Cursor" => FallbackProvider.Cursor,
            "Groq" => FallbackProvider.Groq,
            "OpenCode" => FallbackProvider.OpenCode,
            _ => null,
        };
        if (provider is not { } trackedProvider || handle is not ProcessSessionHandle processHandle) return;

        _ = processHandle.RateLimitOutcome.ContinueWith(async task =>
        {
            var outcome = await task;
            if (!outcome.RateLimitDetected || outcome.ResumeAtUtc is not { } resumeAt) return;
            await _rateLimits.RecordRateLimitAsync(trackedProvider, resumeAt);
            Log($"{handle.ProviderName} hit a usage limit - recorded cooldown until {resumeAt:u} for the fallback chain.");
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
