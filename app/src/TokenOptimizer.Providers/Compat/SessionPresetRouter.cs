using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TokenOptimizer.Providers.Claude;

namespace TokenOptimizer.Providers.Compat;

/// <summary>Cost tier for the preset ranking - the same Cheap/Balanced/Premium scale MainViewModel's priority tree uses, relocated here so the ranking math is shared (and testable) rather than duplicated between the app's ApplyIntentPresetAsync and the router's per-request bias.</summary>
public enum ModelCostTier { Cheap, Balanced, Premium }

/// <summary>How the session is being used - drives whether reasoning strength or speed wins in the ranking.</summary>
public enum SessionPresetIntent { Planning, Execution }

/// <summary>Cost preset - filters the pool before ranking (Cost-effective keeps only Cheap, Quality excludes Cheap, Balanced keeps all).</summary>
public enum SessionPresetTier { CostEffective, Balanced, Quality }

/// <summary>The live session-routing decision: intent + preset, written by the UserPromptSubmit hook and read by the router before every __auto__ resolution.</summary>
public sealed record SessionPreset(SessionPresetIntent Intent, SessionPresetTier Tier)
{
    /// <summary>Default for profiles predating session-preset.json (and for any prompt that matches no keyword).</summary>
    public static SessionPreset Default => new(SessionPresetIntent.Execution, SessionPresetTier.Balanced);
}

/// <summary>One provider's fit scores - the value type of the ranking table, shared so MainViewModel.ProviderFit and the router bias feed the same Rank function.</summary>
public sealed record ProviderFitScore(double ReasoningScore, double SpeedScore, ModelCostTier CostTier);

/// <summary>
/// Per-project session-preset state, persisted as session-preset.json inside the
/// same per-project isolated Claude profile directory IsolatedClaudeProfileService
/// already maintains (the one IPC surface in the codebase proven to sync reliably
/// per-project/per-launch). Written by the UserPromptSubmit hook (keyword inference)
/// and by the /preset command; read by the router before each __auto__ resolution.
/// </summary>
public static class SessionPresetStore
{
    public const string FileName = "session-preset.json";

    public static string FilePathFor(string projectDirectory) =>
        Path.Combine(IsolatedClaudeProfileService.GetProfileDirPath(projectDirectory), FileName);

    /// <summary>
    /// Keyword-inference table, defaulted as documented in the hook script's
    /// header (the four rows map directly to the four session categories the
    /// user named: architectural roadmap planning, research, long-horizon
    /// agentic workflow, bug fixes/debugging). Balanced for long-horizon work
    /// is deliberate - mimo-v2.5 is catalogued as Balanced-tier, so a Balanced
    /// preset naturally favors it for exactly that workload.
    /// </summary>
    public static SessionPreset InferFromPrompt(string prompt)
    {
        var text = prompt ?? string.Empty;
        var lower = text.Trim().ToLowerInvariant();

        if (lower.StartsWith("/plan", StringComparison.Ordinal)) return new SessionPreset(SessionPresetIntent.Planning, SessionPresetTier.Quality);
        if (lower.StartsWith("/build", StringComparison.Ordinal)) return new SessionPreset(SessionPresetIntent.Execution, SessionPresetTier.Balanced);

        if (lower.Contains("architecture") || lower.Contains("roadmap")) return new SessionPreset(SessionPresetIntent.Planning, SessionPresetTier.Quality);
        if (lower.Contains("research")) return new SessionPreset(SessionPresetIntent.Planning, SessionPresetTier.Quality);
        if (lower.Contains("long-horizon") || lower.Contains("agentic workflow")) return new SessionPreset(SessionPresetIntent.Execution, SessionPresetTier.Balanced);
        if (lower.Contains("bug") || lower.Contains("fix") || lower.Contains("debug")) return new SessionPreset(SessionPresetIntent.Execution, SessionPresetTier.CostEffective);

        return SessionPreset.Default;
    }

    public static SessionPreset ReadOrDefault(string projectDirectory) => ReadFrom(FilePathFor(projectDirectory));

    public static void Write(string projectDirectory, SessionPreset preset) => WriteTo(FilePathFor(projectDirectory), preset);

    internal static SessionPreset ReadFrom(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return SessionPreset.Default;
            using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
            var root = doc.RootElement;

            var intent = SessionPresetIntent.Execution;
            if (root.TryGetProperty("intent", out var intentProp) && intentProp.ValueKind == JsonValueKind.String)
            {
                intent = ParseIntent(intentProp.GetString());
            }

            var tier = SessionPresetTier.Balanced;
            if (root.TryGetProperty("preset", out var tierProp) && tierProp.ValueKind == JsonValueKind.String)
            {
                tier = ParseTier(tierProp.GetString());
            }

            return new SessionPreset(intent, tier);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return SessionPreset.Default;
        }
    }

    internal static void WriteTo(string filePath, SessionPreset preset)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = new JsonObject
        {
            ["intent"] = IntentName(preset.Intent),
            ["preset"] = TierName(preset.Tier),
        };
        File.WriteAllText(filePath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string IntentName(SessionPresetIntent intent) => intent == SessionPresetIntent.Planning ? "Planning" : "Execution";

    public static string TierName(SessionPresetTier tier) => tier switch
    {
        SessionPresetTier.CostEffective => "Cost-effective",
        SessionPresetTier.Quality => "Quality",
        _ => "Balanced",
    };

    private static SessionPresetIntent ParseIntent(string? value) => Normalize(value) switch
    {
        "planning" => SessionPresetIntent.Planning,
        _ => SessionPresetIntent.Execution,
    };

    private static SessionPresetTier ParseTier(string? value) => Normalize(value) switch
    {
        "costeffective" => SessionPresetTier.CostEffective,
        "quality" => SessionPresetTier.Quality,
        _ => SessionPresetTier.Balanced,
    };

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

/// <summary>
/// The single ranking algorithm behind both ApplyIntentPresetAsync (the app's
/// priority tree) and the router's live per-request bias: filter the known
/// providers by the preset's cost tier, then rank by reasoning strength for
/// Planning or speed for Execution; if the preset filtered out everything,
/// fall back to ranking all of it rather than picking nothing.
/// </summary>
public static class SessionPresetRanker
{
    public static IReadOnlyList<string> Rank(IEnumerable<string> known, Func<string, ProviderFitScore> fitOf, SessionPreset preset)
    {
        bool CostAllowed(ProviderFitScore fit) => preset.Tier switch
        {
            SessionPresetTier.CostEffective => fit.CostTier == ModelCostTier.Cheap,
            SessionPresetTier.Quality => fit.CostTier != ModelCostTier.Cheap,
            _ => true,
        };

        var knownList = known.Where(k => fitOf(k) is not null).ToList();
        var pool = knownList.Where(k => CostAllowed(fitOf(k))).ToList();
        if (pool.Count == 0) pool = knownList;

        return pool
            .OrderByDescending(k => preset.Intent == SessionPresetIntent.Planning ? fitOf(k).ReasoningScore : fitOf(k).SpeedScore)
            .Concat(knownList.Except(pool))
            .ToList();
    }

    /// <summary>
    /// Per-model variant of Rank for a set of ticked models within one provider:
    /// keys are "{providerName}::{modelId}" (the same Key shape
    /// ProviderModelOptionViewModel exposes). Uses the per-model fit when the
    /// catalog has one, otherwise falls back to that model's provider-level fit.
    /// </summary>
    public static IReadOnlyList<T> RankModels<T>(
        IEnumerable<T> models,
        Func<T, string> keyOf,
        Func<string, ProviderFitScore> fitOf,
        SessionPreset preset)
    {
        var modelsList = models.ToList();
        if (modelsList.Count == 0) return modelsList;
        var rankedKeys = Rank(modelsList.Select(keyOf), fitOf, preset);
        return rankedKeys
            .Select(key => modelsList.First(m => keyOf(m) == key))
            .ToList();
    }
}

/// <summary>
/// Per-model fit scores keyed by "{providerName}::{modelId}" - the pattern to
/// generalize from OpenCodeModelCatalog's per-model CostTier, now applied to
/// Groq's and Claude's models so that when multiple models are ticked within one
/// provider the launch order follows the resolved preset (via RankModels) instead
/// of fixed catalog declaration order. Models without a curated entry fall back
/// to their provider-level ProviderFit at ranking time.
/// </summary>
public static class ModelFitCatalog
{
    public static readonly IReadOnlyDictionary<string, ProviderFitScore> ByModelKey = new Dictionary<string, ProviderFitScore>(StringComparer.Ordinal)
    {
        ["Claude Code::claude-sonnet-5"] = new(0.95, 0.55, ModelCostTier.Premium),
        ["Claude Code::claude-opus-5"] = new(0.95, 0.45, ModelCostTier.Premium),
        ["Claude Code::claude-fable-5"] = new(0.85, 0.60, ModelCostTier.Premium),
        ["Claude Code::claude-haiku-4-5-20251001"] = new(0.70, 0.90, ModelCostTier.Balanced),
        ["Groq::openai/gpt-oss-120b"] = new(0.70, 0.80, ModelCostTier.Balanced),
        ["Groq::openai/gpt-oss-20b"] = new(0.55, 0.90, ModelCostTier.Cheap),
        ["Groq::qwen/qwen3.6-27b"] = new(0.75, 0.85, ModelCostTier.Balanced),
        ["Groq::groq/compound"] = new(0.80, 0.95, ModelCostTier.Balanced),
        ["Groq::groq/compound-mini"] = new(0.60, 0.95, ModelCostTier.Cheap),
    };
}