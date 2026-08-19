using System.Collections.ObjectModel;
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
    private readonly CodexAdapter _codexAdapter;
    private readonly CursorAdapter _cursorAdapter;
    private readonly GroqAdapter _groqAdapter;
    private readonly DeepSeekHarnessAdapter _deepSeekHarnessAdapter;
    private readonly RateLimitTracker _rateLimits;
    private readonly FallbackChainResolver _fallbackResolver;
    private readonly PythonLocator _pythonLocator;
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
        _llamaCppAdapter = new TokenOptimizer.Providers.LlamaCpp.LlamaCppAdapter();
        _antigravityAdapter = new AntigravityAdapter(_credentials);
        _codexAdapter = new CodexAdapter(_credentials);
        _cursorAdapter = new CursorAdapter(_credentials);
        _groqAdapter = new GroqAdapter(_credentials, _claudeLocator);
        _deepSeekHarnessAdapter = new DeepSeekHarnessAdapter();
        _rateLimits = new RateLimitTracker(_configStore);
        _fallbackResolver = new FallbackChainResolver(
            _claudeAdapter, _antigravityAdapter, _codexAdapter, _cursorAdapter, _groqAdapter, _deepSeekHarnessAdapter, _llamaCppAdapter, _rateLimits);
        _wingetInstaller = new WingetInstaller(_availability);
        _companionTooling = new CompanionToolingInstaller(_configStore, _claudeLocator, _availability, _pythonLocator);
        _masterFolderService = new MasterFolderService(_configStore, _projectHistory);
        _uninstaller = new CompanionUninstaller(_availability, _configStore);

        _providers = new IProviderAdapter[]
        {
            _claudeAdapter, _llamaCppAdapter, _antigravityAdapter, _codexAdapter, _cursorAdapter, _groqAdapter, _deepSeekHarnessAdapter,
        };
        ProviderNames = new ObservableCollection<string>(
            new[] { AutoFallbackProviderName, CustomFallbackProviderName }.Concat(_providers.Select(p => p.Name)));
        SelectedProviderName = ProviderNames.FirstOrDefault() ?? string.Empty;

        _ = RefreshAllAsync();
        _ = RefreshDashboardAsync();
        _ = CheckAntigravityLoginAsync();
        _ = CheckCursorLoginAsync();
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
        ["Groq"] = new[] { "llama-3.3-70b-versatile", "deepseek-r1-distill-llama-70b", "llama-3.1-8b-instant", "moonshotai/kimi-k2-instruct", "openai/gpt-oss-120b", "openai/gpt-oss-20b", "qwen/qwen3-32b" },
        ["Codex"] = new[] { "gpt-5-codex", "gpt-5.1-codex", "gpt-5.1-codex-mini" },
        ["Antigravity"] = new[] { "gemini-3-pro", "gemini-3-pro-high" },
        ["Cursor"] = new[] { "auto", "composer-1" },
    };

    /// <summary>Single default/auto model per provider - what Auto/Custom fallback chain shows, so the dropdown isn't every provider's full curated list mashed together (see RefreshModelOverrideOptionsAsync).</summary>
    private static string DefaultModelFor(string providerName) =>
        StaticModelCatalog.TryGetValue(providerName, out var curated) ? curated[0] : providerName;
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

    /// <summary>Selecting the provider IS the category (Avalonia has no built-in grouped-combo control worth the complexity here) - Auto/Custom show the union of everything since the resolved provider decides which entry actually applies.</summary>
    private Task RefreshModelOverrideOptionsAsync(string providerName)
    {
        IEnumerable<string> options;
        if (providerName is AutoFallbackProviderName or CustomFallbackProviderName)
        {
            // Auto/Custom can resolve to any provider, but showing every provider's
            // full curated list at once was an unreadable, uncategorized wall of
            // entries - one default model per provider keeps it scannable and each
            // entry still comes straight from that provider's own model set.
            options = StaticModelCatalog.Keys.Select(DefaultModelFor)
                .Distinct(StringComparer.OrdinalIgnoreCase);
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
        return Task.CompletedTask;
    }

    [ObservableProperty]
    public partial string NewProjectPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ModelOverride { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsolateClaudeConfig { get; set; }

    [ObservableProperty]
    public partial bool RagEnabled { get; set; }

    [ObservableProperty]
    public partial string RagEmbeddingsUrl { get; set; } = string.Empty;

    public ObservableCollection<string> ResumeModeNames { get; } = new(Enum.GetNames<SessionResumeMode>());

    [ObservableProperty]
    public partial string SelectedResumeModeName { get; set; } = nameof(SessionResumeMode.Continue);

    [ObservableProperty]
    public partial string UninstallConfirmationInput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Ready.";

    [ObservableProperty]
    public partial string CodexApiKeyInput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GroqApiKeyInput { get; set; } = string.Empty;

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
        SetupStep2Done = Dependencies.Count > 0 && Dependencies.All(d => d.IsAvailable);
        SetupStep3Done = CompanionToolingProgress >= 1;

        var doneCount = new[] { SetupStep1Done, SetupStep2Done, SetupStep3Done }.Count(d => d);
        SetupStepsSummary = doneCount == 3
            ? "All setup steps done - ready in the Session tab."
            : $"{doneCount} of 3 setup steps done.";
    }

    [RelayCommand]
    private async Task RefreshAllAsync()
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
            Dependencies.Clear();
            foreach (var dep in deps) Dependencies.Add(dep);

            var chain = await _fallbackResolver.DescribeChainAsync();
            FallbackChain.Clear();
            foreach (var step in chain) FallbackChain.Add(step);

            if (CustomChainOrder.Count == 0)
            {
                await SeedCustomChainOrderAsync();
            }

            if (string.IsNullOrWhiteSpace(MasterFolderPath))
            {
                MasterFolderPath = await _masterFolderService.GetMasterFolderAsync() ?? string.Empty;
            }
            if (!string.IsNullOrWhiteSpace(MasterFolderPath))
            {
                await RefreshMasterFolderCandidatesAsync();
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

    /// <summary>Double-click on a subdirectory tree node: launches a session directly against that path, under AutoLaunchProviderName if configured, otherwise whatever's currently selected in the Provider dropdown.</summary>
    [RelayCommand]
    private async Task LaunchAtPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

        var config = await _configStore.LoadAsync();
        var launchProviderName = string.IsNullOrWhiteSpace(config.AutoLaunchProviderName)
            ? SelectedProviderName
            : config.AutoLaunchProviderName;

        IProviderAdapter? provider = launchProviderName switch
        {
            AutoFallbackProviderName => await _fallbackResolver.ResolveAsync(),
            CustomFallbackProviderName => await ResolveCustomChainProviderAsync(),
            _ => _providers.FirstOrDefault(p => p.Name == launchProviderName),
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

        IProviderAdapter? provider = SelectedProviderName switch
        {
            AutoFallbackProviderName => await _fallbackResolver.ResolveAsync(),
            CustomFallbackProviderName => await ResolveCustomChainProviderAsync(),
            _ => _providers.FirstOrDefault(p => p.Name == SelectedProviderName),
        };

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
        ("Codex CLI", _providerCliInstaller.InstallCodexCliAsync),
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
            IProviderAdapter? resolved = SelectedProviderName switch
            {
                AutoFallbackProviderName => await _fallbackResolver.ResolveAsync(),
                CustomFallbackProviderName => await ResolveCustomChainProviderAsync(),
                _ => _providers.FirstOrDefault(p => p.Name == SelectedProviderName),
            };

            ActiveProviderLabel = SelectedProviderName switch
            {
                AutoFallbackProviderName or CustomFallbackProviderName =>
                    resolved is not null ? $"{SelectedProviderName} -> currently resolves to {resolved.Name}" : $"{SelectedProviderName} -> nothing available right now",
                _ => SelectedProviderName,
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
            Log("Enter an OPENAI_API_KEY first.");
            return;
        }

        _credentials.SetCredential(FallbackProvider.Codex, CodexApiKeyInput);
        CodexApiKeyInput = string.Empty;
        Log("Codex credential stored (DPAPI-encrypted, this account only).");
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
            var handoffFile = SessionHandoffExporter.Export(SelectedProject.FullPath);
            Log($"Session handoff exported: {handoffFile}");
        }
        catch (Exception ex)
        {
            Log($"Handoff export failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task LaunchSessionAsync()
    {
        if (SelectedProject is null)
        {
            Log("Select a project first.");
            return;
        }

        IsBusy = true;
        StatusText = $"Launching {SelectedProviderName}...";
        try
        {
            using var instanceLock = InstanceLock.TryAcquire(SelectedProject.FullPath);
            if (instanceLock is null)
            {
                Log("Another setup is already running for this project - launching anyway (setup skipped).");
            }

            IProviderAdapter? provider;
            if (SelectedProviderName == AutoFallbackProviderName)
            {
                provider = await _fallbackResolver.ResolveAsync();
                if (provider is null)
                {
                    Log("No backend in the fallback chain is currently available (Claude, Antigravity, Codex, Cursor, and local model all unavailable).");
                    StatusText = "Ready.";
                    return;
                }
                Log($"Fallback chain resolved to: {provider.Name}");
            }
            else if (SelectedProviderName == CustomFallbackProviderName)
            {
                provider = await ResolveCustomChainProviderAsync();
                if (provider is null)
                {
                    Log("No backend in the custom fallback chain is currently available.");
                    StatusText = "Ready.";
                    return;
                }
                Log($"Custom fallback chain resolved to: {provider.Name}");
            }
            else
            {
                provider = _providers.FirstOrDefault(p => p.Name == SelectedProviderName);
                if (provider is null)
                {
                    Log($"Unknown provider: {SelectedProviderName}");
                    return;
                }

                if (!await provider.IsAvailableAsync())
                {
                    Log($"{provider.Name} is not available on this machine.");
                    StatusText = "Ready.";
                    return;
                }
            }

            if (provider == _claudeAdapter || provider == _llamaCppAdapter || provider == _groqAdapter)
            {
                // Same ~/.claude environment either way (Claude Code direct,
                // or Claude Code pointed at a local llama.cpp model, or at
                // Groq through the local proxy shim) - keep it in sync before
                // every launch so switching between them is zero-friction:
                // same skills, plugins, MCP tools, and claude-mem memory,
                // automatically, every time.
                await _companionTooling.EnsureSharedClaudeEnvironmentAsync(SelectedProject.FullPath);
                await PrepareProjectDirectiveAsync(SelectedProject.FullPath, provider);
            }

            var options = new SessionLaunchOptions(
                SelectedProject.FullPath,
                ResolveEffectiveModel(),
                IsolateClaudeConfig,
                Enum.Parse<SessionResumeMode>(SelectedResumeModeName));

            var handle = await provider.LaunchSessionAsync(options);
            await _projectHistory.AddAsync(SelectedProject.FullPath);
            Log($"Launched {handle.ProviderName} for {handle.ProjectPath} (pid {handle.ProcessId?.ToString() ?? "n/a"}).");
            TrackRateLimitOutcome(handle);
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
