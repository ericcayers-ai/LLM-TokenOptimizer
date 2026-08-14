using System.Collections.ObjectModel;
using Avalonia.Input.Platform;
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
    private readonly GroqAdapter _groqAdapter;
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
        _groqAdapter = new GroqAdapter(_credentials, _claudeLocator);
        _rateLimits = new RateLimitTracker(_configStore);
        _fallbackResolver = new FallbackChainResolver(
            _claudeAdapter, _antigravityAdapter, _codexAdapter, _cursorAdapter, _groqAdapter, _lmStudioAdapter, _rateLimits);
        _benchmarkReader = new BenchmarkSummaryReader(_configStore);
        _wingetInstaller = new WingetInstaller(_availability);
        _companionTooling = new CompanionToolingInstaller(_configStore, _claudeLocator, _availability, _pythonLocator);
        _masterFolderService = new MasterFolderService(_configStore, _projectHistory);
        _benchmarkRunner = new BenchmarkRunner(_availability, _pythonLocator);
        _uninstaller = new CompanionUninstaller(_availability, _configStore);

        _providers = new IProviderAdapter[]
        {
            _claudeAdapter, _lmStudioAdapter, _antigravityAdapter, _codexAdapter, _cursorAdapter, _groqAdapter,
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
    public partial string GroqApiKeyInput { get; set; } = string.Empty;

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
                    await ResolveEffectiveModelAsync(provider),
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
        ("claude-md-management", _companionTooling.InstallClaudeMdManagementPluginAsync),
        ("impeccable", _companionTooling.InstallImpeccableSkillAsync),
        ("task-observer", _companionTooling.InstallTaskObserverSkillAsync),
        ("LM Studio support", _companionTooling.InstallLMStudioSupportAsync),
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
        var config = await _configStore.LoadAsync();
        var overrideNote = config.BestLocalModelIsManualOverride
            ? $" (manual pick in effect: {config.BestLocalModelId} - composite_score auto-pick shown below is informational only)"
            : "";
        BestLocalModelText = best is null
            ? "benchmark_summary.json has no successful benchmark rows."
            : $"Best local model: {best.Model} ({best.AvgTokensPerSecond:F1} tok/s" +
              (best.CompositeScore is { } cs ? $", composite={cs:F3}" : "") + ")" + overrideNote;

        RefreshLeaderboard(summaryPath);
    }

    /// <summary>
    /// Any explicit ModelOverride text wins. Otherwise, for the local LM
    /// Studio adapter, fall back to the configured local-model pick
    /// (manual override or the auto composite_score winner) so selecting
    /// "LM Studio (local)" in the provider dropdown actually loads a model
    /// instead of silently starting the server with nothing loaded.
    /// </summary>
    private async Task<string?> ResolveEffectiveModelAsync(IProviderAdapter provider)
    {
        if (!string.IsNullOrWhiteSpace(ModelOverride)) return ModelOverride;
        if (provider != _lmStudioAdapter) return null;

        var config = await _configStore.LoadAsync();
        return config.BestLocalModelId;
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

    /// <summary>
    /// One-click "give this to an AI" export: zips every benchmark_&lt;model&gt;.json
    /// plus a ready-to-paste review prompt (see BenchmarkExporter), reveals the
    /// zip in Explorer so it's immediately at hand to attach/upload, and copies
    /// the prompt text to the clipboard so the only remaining step is pasting it
    /// alongside the zip into whatever AI the user wants to run the review.
    /// </summary>
    [RelayCommand]
    private async Task ExportBenchmarksForAiReviewAsync()
    {
        var repoRoot = BenchmarkRunner.FindRepoRoot();
        if (repoRoot is null)
        {
            BenchmarkStatusText = "run_benchmarks.py not found near the app - nothing to export.";
            return;
        }

        var zipPath = Path.Combine(repoRoot, $"benchmark_results_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
        var count = await Task.Run(() => BenchmarkExporter.Export(repoRoot, zipPath));
        if (count == 0)
        {
            BenchmarkStatusText = "No benchmark_<model>.json files found - run benchmarks first.";
            return;
        }

        var clipboard = GetTopLevelClipboard();
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(BenchmarkExporter.BuildPrompt(count));
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{zipPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log($"Could not open Explorer to reveal the export: {ex.Message}");
        }

        BenchmarkStatusText = $"Exported {count} model result(s) to {Path.GetFileName(zipPath)}" +
                               (clipboard is not null ? " - review prompt copied to clipboard." : " - clipboard unavailable, prompt is inside the zip as " + BenchmarkExporter.PromptFileName + ".");
        Log(BenchmarkStatusText);
    }

    /// <summary>
    /// Generates the mechanical scoring-matrix/averages report (BenchmarkReportGenerator)
    /// and opens it - deliberately separate from ExportBenchmarksForAiReviewAsync, which
    /// handles the human/AI-written quality-review half of the picture via the zip+prompt.
    /// </summary>
    [RelayCommand]
    private async Task GenerateBenchmarkReportAsync()
    {
        var repoRoot = BenchmarkRunner.FindRepoRoot();
        if (repoRoot is null)
        {
            BenchmarkStatusText = "run_benchmarks.py not found near the app - nothing to report.";
            return;
        }

        var reportPath = Path.Combine(repoRoot, "BENCHMARK_REPORT.md");
        var count = await Task.Run(() => BenchmarkReportGenerator.Generate(repoRoot, reportPath));
        if (count == 0)
        {
            BenchmarkStatusText = "No benchmark_<model>.json files found - run benchmarks first.";
            return;
        }

        BenchmarkStatusText = $"Wrote BENCHMARK_REPORT.md ({count} model(s)).";
        Log(BenchmarkStatusText);

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = reportPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log($"Could not open the report: {ex.Message}");
        }
    }

    private static IClipboard? GetTopLevelClipboard()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } window)
        {
            return Avalonia.Controls.TopLevel.GetTopLevel(window)?.Clipboard;
        }
        return null;
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
                await ResolveEffectiveModelAsync(provider),
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

    /// <summary>Delegates to the shared ProjectSessionPrep so the CLI host (used by the VS Code extension) runs the exact same pre-launch checks as this UI.</summary>
    private Task PrepareProjectDirectiveAsync(string projectDirectory) =>
        ProjectSessionPrep.PrepareProjectDirectiveAsync(projectDirectory, _claudeMdService, _availability, Log);

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
