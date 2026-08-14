using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TokenOptimizer.Core.Benchmarking;
using TokenOptimizer.Core.Concurrency;
using TokenOptimizer.Core.Config;
using TokenOptimizer.Core.Diagnostics;
using TokenOptimizer.Core.Models;
using TokenOptimizer.Core.Projects;
using TokenOptimizer.Core.Security;
using TokenOptimizer.Providers;
using TokenOptimizer.Providers.Claude;
using TokenOptimizer.Providers.Fallback;
using TokenOptimizer.Providers.LmStudio;

namespace TokenOptimizer.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private const string AutoFallbackProviderName = "Auto (fallback chain)";

    private readonly ConfigStore _configStore = new();
    private readonly CommandAvailability _availability = new();
    private readonly ProjectHistoryService _projectHistory;
    private readonly DependencyChecker _dependencyChecker;
    private readonly ClaudeExecutableLocator _claudeLocator;
    private readonly ClaudeCodeAdapter _claudeAdapter;
    private readonly LmStudioAdapter _lmStudioAdapter;
    private readonly ProxyCredentialStore _credentials = new();
    private readonly AntigravityAdapter _antigravityAdapter;
    private readonly CodexAdapter _codexAdapter;
    private readonly CursorAdapter _cursorAdapter;
    private readonly RateLimitTracker _rateLimits;
    private readonly FallbackChainResolver _fallbackResolver;
    private readonly BenchmarkSummaryReader _benchmarkReader;
    private readonly PythonLocator _pythonLocator;
    private readonly WingetInstaller _wingetInstaller;
    private readonly CompanionToolingInstaller _companionTooling;
    private readonly MasterFolderService _masterFolderService;
    private readonly BenchmarkRunner _benchmarkRunner;
    private readonly ProjectClaudeMdService _claudeMdService = new();
    private readonly CompanionUninstaller _uninstaller;
    private readonly IReadOnlyList<IProviderAdapter> _providers;

    public MainViewModel()
    {
        _projectHistory = new ProjectHistoryService(_configStore);
        _pythonLocator = new PythonLocator(_availability);
        _dependencyChecker = new DependencyChecker(_availability, _pythonLocator);
        _claudeLocator = new ClaudeExecutableLocator(_configStore, _availability);
        _claudeAdapter = new ClaudeCodeAdapter(_claudeLocator, _availability);
        _lmStudioAdapter = new LmStudioAdapter(_claudeLocator);
        _antigravityAdapter = new AntigravityAdapter(_credentials);
        _codexAdapter = new CodexAdapter(_credentials);
        _cursorAdapter = new CursorAdapter(_credentials);
        _rateLimits = new RateLimitTracker(_configStore);
        _fallbackResolver = new FallbackChainResolver(
            _claudeAdapter, _antigravityAdapter, _codexAdapter, _cursorAdapter, _lmStudioAdapter, _rateLimits);
        _benchmarkReader = new BenchmarkSummaryReader(_configStore);
        _wingetInstaller = new WingetInstaller(_availability);
        _companionTooling = new CompanionToolingInstaller(_configStore, _claudeLocator, _availability, _pythonLocator);
        _masterFolderService = new MasterFolderService(_configStore, _projectHistory);
        _benchmarkRunner = new BenchmarkRunner(_availability, _pythonLocator);
        _uninstaller = new CompanionUninstaller(_availability, _configStore);

        _providers = new IProviderAdapter[]
        {
            _claudeAdapter, _lmStudioAdapter, _antigravityAdapter, _codexAdapter, _cursorAdapter,
        };
        ProviderNames = new ObservableCollection<string>(
            new[] { AutoFallbackProviderName }.Concat(_providers.Select(p => p.Name)));
        SelectedProviderName = ProviderNames.FirstOrDefault() ?? string.Empty;

        QualityTierNames = new ObservableCollection<string>(Enum.GetNames<BenchmarkQualityTier>());
        SelectedQualityTierName = QualityTierNames.FirstOrDefault() ?? string.Empty;

        _ = RefreshAllAsync();
        _ = LoadBenchmarkCatalogAsync();
    }

    public ObservableCollection<ProjectInfo> ProjectHistoryList { get; } = new();
    public ObservableCollection<DependencyStatus> Dependencies { get; } = new();
    public ObservableCollection<string> ProviderNames { get; }
    public ObservableCollection<FallbackChainStep> FallbackChain { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<ProjectCandidateViewModel> MasterFolderCandidates { get; } = new();
    public ObservableCollection<string> CatalogModels { get; } = new();
    public ObservableCollection<string> QualityTierNames { get; }
    public ObservableCollection<BenchmarkRow> Leaderboard { get; } = new();

    [ObservableProperty]
    public partial ProjectInfo? SelectedProject { get; set; }

    [ObservableProperty]
    public partial string SelectedProviderName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewProjectPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ModelOverride { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsolateClaudeConfig { get; set; }

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
    public partial string BestLocalModelText { get; set; } = "No benchmark results yet.";

    [ObservableProperty]
    public partial string CodexApiKeyInput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MasterFolderPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewProjectFolderName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? SelectedCatalogModel { get; set; }

    [ObservableProperty]
    public partial string SelectedQualityTierName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BenchmarkStatusText { get; set; } = "Idle.";

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

            await RefreshBestLocalModelAsync();

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
        }
    }

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

        var provider = SelectedProviderName == AutoFallbackProviderName
            ? await _fallbackResolver.ResolveAsync()
            : _providers.FirstOrDefault(p => p.Name == SelectedProviderName);

        if (provider is null)
        {
            Log("No provider available to launch with.");
            return;
        }

        foreach (var candidate in selected)
        {
            try
            {
                if (provider == _claudeAdapter || provider == _lmStudioAdapter)
                {
                    await _companionTooling.EnsureSharedClaudeEnvironmentAsync(candidate.FullPath);
                    await PrepareProjectDirectiveAsync(candidate.FullPath);
                }

                var options = new SessionLaunchOptions(
                    candidate.FullPath,
                    string.IsNullOrWhiteSpace(ModelOverride) ? null : ModelOverride,
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

    [RelayCommand]
    private async Task InstallCompanionToolingAsync()
    {
        IsBusy = true;
        StatusText = "Installing companion tooling...";
        try
        {
            Log((await _companionTooling.InstallGraphifyAsync()) ? "Graphify: OK" : "Graphify: failed");
            Log((await _companionTooling.InstallClaudeMemAsync()) ? "claude-mem: OK" : "claude-mem: failed");
            Log((await _companionTooling.InstallHeadroomStatuslineAsync()) ? "headroom: OK" : "headroom: failed");
            Log((await _companionTooling.InstallRtkCliAsync()) ? "rtk: OK" : "rtk: failed");
            Log((await _companionTooling.RegisterContext7McpAsync()) ? "context7: OK" : "context7: failed");
            Log((await _companionTooling.InstallContextModeMcpAsync()) ? "context-mode: OK" : "context-mode: failed");
            Log((await _companionTooling.InstallCavemanPluginAsync()) ? "caveman: OK" : "caveman: failed");
            Log((await _companionTooling.InstallClaudeMdManagementPluginAsync()) ? "claude-md-management: OK" : "claude-md-management: failed");
            Log((await _companionTooling.InstallTaskObserverSkillAsync()) ? "task-observer: OK" : "task-observer: failed");
            Log((await _companionTooling.InstallLMStudioSupportAsync()) ? "LM Studio support: OK" : "LM Studio support: not detected");

            if (SelectedProject is not null)
            {
                var codeIntelPlugin = await _companionTooling.InstallCodeIntelligencePluginAsync(SelectedProject.FullPath);
                Log(codeIntelPlugin is not null ? $"code intelligence: {codeIntelPlugin}" : "code intelligence: not applicable");
            }

            var active = await _companionTooling.DescribeActiveCompressionAsync();
            foreach (var line in active) Log($"Compression active: {line}");

            StatusText = "Ready.";
        }
        finally
        {
            IsBusy = false;
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
        StatusText = "Installing missing dependencies via winget...";
        try
        {
            var installed = await _wingetInstaller.InstallMissingAsync(missing);
            foreach (var name in installed) Log($"Installed via winget: {name}");
            if (installed.Count == 0) Log("winget could not install any of the missing dependencies (unavailable or all failed).");
            await RefreshAllAsync();
        }
        finally
        {
            IsBusy = false;
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

    [RelayCommand]
    private async Task RefreshBestLocalModelAsync()
    {
        var summaryPath = FindBenchmarkSummaryPath();
        if (summaryPath is null)
        {
            BestLocalModelText = "No benchmark_summary.json found near the repo.";
            return;
        }

        var best = await _benchmarkReader.RefreshBestLocalModelAsync(summaryPath);
        BestLocalModelText = best is null
            ? "benchmark_summary.json has no successful benchmark rows."
            : $"Best local model: {best.Model} ({best.AvgTokensPerSecond:F1} tok/s" +
              (best.CompositeScore is { } cs ? $", composite={cs:F3}" : "") + ")";

        RefreshLeaderboard(summaryPath);
    }

    private void RefreshLeaderboard(string? summaryPath)
    {
        Leaderboard.Clear();
        if (summaryPath is null) return;

        var ranked = BenchmarkSummaryReader.ReadRows(summaryPath)
            .Where(r => r.Stage == "benchmark" && (r.Status == "ok" || r.Status == "partial"))
            .OrderByDescending(r => r.CompositeScore ?? 0)
            .ThenByDescending(r => r.AvgTokensPerSecond ?? 0);
        foreach (var row in ranked) Leaderboard.Add(row);
    }

    [RelayCommand]
    private async Task LoadBenchmarkCatalogAsync()
    {
        var repoRoot = BenchmarkRunner.FindRepoRoot();
        if (repoRoot is null)
        {
            BenchmarkStatusText = "run_benchmarks.py not found near the app - benchmark mode unavailable.";
            return;
        }

        var models = await Task.Run(() => BenchmarkRunner.ListCatalogModels(repoRoot));
        CatalogModels.Clear();
        foreach (var model in models) CatalogModels.Add(model);
        BenchmarkStatusText = $"{models.Count} models in catalog. Idle.";
    }

    /// <summary>
    /// Fires the run in the background rather than blocking IsBusy - a real
    /// benchmark sweep (downloads + generation) can run for hours, and the
    /// rest of the app (launching sessions, managing projects) should stay
    /// usable the whole time.
    /// </summary>
    [RelayCommand]
    private void RunBenchmark()
    {
        var repoRoot = BenchmarkRunner.FindRepoRoot();
        if (repoRoot is null)
        {
            Log("run_benchmarks.py not found near the app.");
            return;
        }

        if (!Enum.TryParse<BenchmarkQualityTier>(SelectedQualityTierName, out var tier))
        {
            tier = BenchmarkQualityTier.MaxQuality;
        }

        var selectedModel = SelectedCatalogModel;
        var models = selectedModel is not null ? new[] { selectedModel } : null;
        var modelsLabel = selectedModel ?? "all models";
        BenchmarkStatusText = $"Running ({tier}, {modelsLabel})...";
        Log($"Benchmark run started: tier={tier}, models={modelsLabel}");

        _ = Task.Run(async () =>
        {
            var result = await _benchmarkRunner.RunAsync(repoRoot, models, tier);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                BenchmarkStatusText = result.Success ? "Run complete." : $"Run failed: {Truncate(result.Output, 200)}";
                Log(result.Success ? "Benchmark run complete." : $"Benchmark run failed: {Truncate(result.Output, 200)}");
                await RefreshBestLocalModelAsync();
            });
        });
    }

    private static string Truncate(string text, int maxLength) => text.Length <= maxLength ? text : text[..maxLength] + "...";

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
    private void OptInAntigravity()
    {
        _credentials.SetCredential(FallbackProvider.Antigravity, "opted-in");
        Log("Antigravity opted into the fallback chain (sign-in happens inside the app).");
        _ = RefreshAllAsync();
    }

    [RelayCommand]
    private void OptInCursor()
    {
        _credentials.SetCredential(FallbackProvider.Cursor, "opted-in");
        Log("Cursor opted into the fallback chain (sign-in happens inside the app).");
        _ = RefreshAllAsync();
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

            if (provider == _claudeAdapter || provider == _lmStudioAdapter)
            {
                // Same ~/.claude environment either way (Claude Code direct or
                // Claude Code pointed at a local LM Studio model) - keep it in
                // sync before every launch so switching between them is
                // zero-friction: same skills, plugins, MCP tools, and
                // claude-mem memory, automatically, every time.
                await _companionTooling.EnsureSharedClaudeEnvironmentAsync(SelectedProject.FullPath);
                await PrepareProjectDirectiveAsync(SelectedProject.FullPath);
            }

            var options = new SessionLaunchOptions(
                SelectedProject.FullPath,
                string.IsNullOrWhiteSpace(ModelOverride) ? null : ModelOverride,
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

    private static string? FindBenchmarkSummaryPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "benchmark_summary.json");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Keeps CLAUDE.md's graph-first + companion-tooling directives current,
    /// warns on a bloated CLAUDE.md, and wires up Graphify strict mode for
    /// projects big enough to warrant it - the same "runs every launch,
    /// idempotent, marker-gated" checks Invoke-ProjectMode ran before
    /// starting Claude Code.
    /// </summary>
    private async Task PrepareProjectDirectiveAsync(string projectDirectory)
    {
        if (ProjectClaudeMdService.CheckClaudeMdBloat(projectDirectory) is { } bloatWarning)
        {
            Log(bloatWarning);
        }

        var useGraphify = ProjectClaudeMdService.ExceedsGraphifyThreshold(projectDirectory);
        if (useGraphify && _availability.IsOnPath("graphify", useCache: true))
        {
            await _claudeMdService.InstallGraphifyHookAsync(projectDirectory);
            await _claudeMdService.InstallGraphifyStrictModeAsync(projectDirectory);
        }

        ProjectClaudeMdService.EnsureDirective(projectDirectory, useGraphify);

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
