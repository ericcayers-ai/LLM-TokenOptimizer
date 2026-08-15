using System.Text.RegularExpressions;
using TokenOptimizer.Core.Diagnostics;

namespace TokenOptimizer.Core.Benchmarking;

/// <summary>
/// Drives run_benchmarks.py from inside the app: locate it, list its model
/// catalog (parsed from its own MODEL_LIST source rather than duplicated
/// here, so the app never drifts out of sync with the script), and launch a
/// run at a chosen quality tier against a chosen model set. The scoring
/// pipeline itself stays entirely in Python, untouched - this is a launcher
/// and a results reader (see BenchmarkSummaryReader), not a reimplementation.
///
/// Resource gating (VRAM/RAM vs. each model's size_gb) also lives entirely
/// in the script (filter_models_by_resources, unconditional - applies even
/// to an explicit --models list) rather than being duplicated here, for the
/// same single-source-of-truth reason. A model too big for this machine is
/// excluded and logged, never silently skipped or force-run.
/// </summary>
public sealed class BenchmarkRunner
{
    private static readonly Regex ModelIdPattern = new("\"id\":\\s*\"([^\"]+)\"", RegexOptions.Compiled);

    // Matches a MODEL_CONFIG entry's own "max_tokens": N to identify models
    // the script itself configured with a big reasoning budget (10000-14000)
    // - these are the reasoning/"thinking" models whose comments document,
    // live, that a smaller flat budget produces ZERO completion tokens.
    private static readonly Regex ModelConfigEntryPattern = new(
        "\"([^\"]+)\":\\s*\\{[^}]*\"max_tokens\":\\s*(\\d+)", RegexOptions.Compiled);

    private const int HeavyModelMaxTokensThreshold = 9000;

    private readonly CommandAvailability _availability;
    private readonly PythonLocator _pythonLocator;

    public BenchmarkRunner(CommandAvailability availability, PythonLocator pythonLocator)
    {
        _availability = availability;
        _pythonLocator = pythonLocator;
    }

    /// <summary>Walks up from the app's own base directory looking for run_benchmarks.py, mirroring how the app locates benchmark_summary.json.</summary>
    public static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "run_benchmarks.py"))) return dir.FullName;
        }
        return null;
    }

    /// <summary>The full catalog of models run_benchmarks.py knows how to fetch/benchmark, for a manual "pick from all models" picker.</summary>
    public static IReadOnlyList<string> ListCatalogModels(string repoRoot)
    {
        var source = ReadScriptSource(repoRoot);
        if (source is null) return Array.Empty<string>();
        return ModelIdPattern.Matches(source).Select(m => m.Groups[1].Value).Distinct().ToList();
    }

    /// <summary>
    /// Models the script's own MODEL_CONFIG configured with a big reasoning
    /// budget (&gt;= 9000 max_tokens) - these need that much room or they
    /// produce zero completion tokens (confirmed live, see the script's own
    /// comments on qwen3.6-35b-a3b). Quick/Balanced tiers skip these rather
    /// than forcing a smaller budget onto them, since a flat override would
    /// corrupt their results instead of just taking longer.
    /// </summary>
    public static IReadOnlyList<string> ListHeavyModelIds(string repoRoot)
    {
        var source = ReadScriptSource(repoRoot);
        if (source is null) return Array.Empty<string>();

        return ModelConfigEntryPattern.Matches(source)
            .Where(m => int.Parse(m.Groups[2].Value) >= HeavyModelMaxTokensThreshold)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
    }

    private static string? ReadScriptSource(string repoRoot)
    {
        var scriptPath = Path.Combine(repoRoot, "run_benchmarks.py");
        return File.Exists(scriptPath) ? File.ReadAllText(scriptPath) : null;
    }

    public Task<CommandResult> RunAsync(
        string repoRoot,
        IReadOnlyList<string>? modelIds,
        BenchmarkQualityTier tier,
        CancellationToken cancellationToken = default) =>
        RunAsync(repoRoot, modelIds, tier, onLine: null, cancellationToken);

    /// <summary>Same as RunAsync, but streams each stdout/stderr line to onLine as the run produces it - for a live log view instead of only a final result after the whole (possibly hours-long) run completes.</summary>
    public async Task<CommandResult> RunAsync(
        string repoRoot,
        IReadOnlyList<string>? modelIds,
        BenchmarkQualityTier tier,
        Action<string>? onLine,
        CancellationToken cancellationToken = default)
    {
        var scriptPath = Path.Combine(repoRoot, "run_benchmarks.py");
        var args = new List<string> { $"\"{scriptPath}\"" };

        var effectiveModels = modelIds;
        if (tier != BenchmarkQualityTier.MaxQuality && (modelIds is null || modelIds.Count == 0))
        {
            // "All models" at a faster tier means "all models minus the ones
            // that need a big reasoning budget to produce any output at
            // all" - never a flat --max-tokens override, which the script's
            // own per-model MODEL_CONFIG would otherwise have clobbered for
            // every reasoning model in the run, producing empty/zero-score
            // results for all of them instead of a genuine speed/reliability
            // trade-off.
            var heavy = new HashSet<string>(ListHeavyModelIds(repoRoot));
            var lightModels = ListCatalogModels(repoRoot).Where(id => !heavy.Contains(id)).ToList();
            if (lightModels.Count > 0) effectiveModels = lightModels;
        }

        if (effectiveModels is { Count: > 0 })
        {
            args.Add($"--models \"{string.Join(',', effectiveModels)}\"");
        }

        if (tier == BenchmarkQualityTier.Quick)
        {
            // Skip the (often multi-GB, multi-minute) download step -
            // assumes the selected models are already on disk. This is the
            // tier's real speed lever; it never touches per-model token
            // budgets, so whatever runs still produces reliable scores.
            args.Add("--skip-download");
        }

        // Prefer `uv run` (the documented invocation - see Write-Hint in the
        // original launcher) since it manages the script's own venv/deps;
        // fall back to a plain verified Python interpreter if uv isn't installed.
        if (_availability.IsOnPath("uv", useCache: true))
        {
            return onLine is null
                ? await ExternalCommandRunner.RunAsync(
                    "uv", $"run {string.Join(' ', args)}", repoRoot, timeoutSeconds: 0, cancellationToken: cancellationToken)
                : await ExternalCommandRunner.RunStreamingAsync(
                    "uv", $"run {string.Join(' ', args)}", repoRoot, onLine, timeoutSeconds: 0, cancellationToken: cancellationToken);
        }

        var pythonExe = await _pythonLocator.FindWorkingPythonAsync();
        if (pythonExe is null)
        {
            return new CommandResult { Success = false, Output = "No working Python interpreter and uv is not installed - cannot run benchmarks." };
        }

        return onLine is null
            ? await ExternalCommandRunner.RunAsync(
                pythonExe, string.Join(' ', args), repoRoot, timeoutSeconds: 0, cancellationToken: cancellationToken)
            : await ExternalCommandRunner.RunStreamingAsync(
                pythonExe, string.Join(' ', args), repoRoot, onLine, timeoutSeconds: 0, cancellationToken: cancellationToken);
    }
}
