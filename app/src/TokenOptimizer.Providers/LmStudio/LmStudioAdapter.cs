using System.Diagnostics;
using System.Text.Json;
using TokenOptimizer.Core.Diagnostics;
using TokenOptimizer.Core.Models;
using TokenOptimizer.Providers.Claude;
using TokenOptimizer.Providers.Manifests;

namespace TokenOptimizer.Providers.LmStudio;

/// <summary>
/// Wraps LM Studio's `lms` CLI so a local model becomes a drop-in swap for
/// the Claude Code proxy model: ensures the local API server is up, loads
/// the requested model, then launches Claude Code pointed at that local
/// OpenAI-compatible endpoint via ANTHROPIC_BASE_URL - the same proxy-swap
/// mechanism the fallback chain (Claude -> Antigravity -> local LM Studio)
/// already relies on. LM Studio itself hosts models, not skills/plugins, so
/// those members honestly report "not supported" rather than faking success.
/// </summary>
public sealed class LmStudioAdapter : IProviderAdapter
{
    private const int ServerPort = 1234;
    private readonly ClaudeExecutableLocator _claudeLocator;
    private string? _lmsPath;

    public LmStudioAdapter(ClaudeExecutableLocator claudeLocator)
    {
        _claudeLocator = claudeLocator;
    }

    public string Name => "LM Studio (local)";

    public Task<bool> IsAvailableAsync() => Task.FromResult(ResolveLmsPath() is not null);

    public async Task<IReadOnlyList<LmStudioModel>> ListInstalledModelsAsync()
    {
        var lms = ResolveLmsPath();
        if (lms is null) return Array.Empty<LmStudioModel>();

        var result = await ExternalCommandRunner.RunAsync(lms, "ls --json", timeoutSeconds: 20);
        if (!result.Success) return Array.Empty<LmStudioModel>();

        try
        {
            using var doc = JsonDocument.Parse(result.Output);
            var models = new List<LmStudioModel>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var type = entry.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
                if (type != "llm") continue;
                var key = entry.TryGetProperty("modelKey", out var keyProp) ? keyProp.GetString() : null;
                var sizeBytes = entry.TryGetProperty("sizeBytes", out var sizeProp) && sizeProp.TryGetInt64(out var size) ? size : (long?)null;
                var maxContext = entry.TryGetProperty("maxContextLength", out var ctxProp) && ctxProp.TryGetInt32(out var ctx) ? ctx : (int?)null;
                if (key is not null) models.Add(new LmStudioModel(key, type, sizeBytes, maxContext));
            }
            return models;
        }
        catch (JsonException)
        {
            return Array.Empty<LmStudioModel>();
        }
    }

    public async Task<bool> EnsureServerRunningAsync()
    {
        var lms = ResolveLmsPath();
        if (lms is null) return false;

        var status = await ExternalCommandRunner.RunAsync(lms, "server status", timeoutSeconds: 10);
        if (IsServerUp(status.Success, status.Output)) return true;

        var start = await ExternalCommandRunner.RunAsync(lms, $"server start --port {ServerPort}", timeoutSeconds: 30);
        if (!start.Success) return false;

        for (var i = 0; i < 10; i++)
        {
            var recheck = await ExternalCommandRunner.RunAsync(lms, "server status", timeoutSeconds: 10);
            if (IsServerUp(recheck.Success, recheck.Output)) return true;
            await Task.Delay(1000);
        }

        return false;
    }

    /// <summary>ttlSeconds null = never auto-unload (lms's own default when --ttl is omitted); a value = unload after that many idle seconds. --parallel is fixed at 1: one interactive coding session, no concurrent-request need.</summary>
    public async Task<ProviderResult> LoadModelAsync(string modelId, int contextLength = 8192, int? ttlSeconds = null)
    {
        var lms = ResolveLmsPath();
        if (lms is null) return ProviderResult.Fail("lms CLI not found");

        var args = $"load {modelId} --gpu max --context-length {contextLength} --parallel 1 -y";
        if (ttlSeconds is { } ttl) args += $" --ttl {ttl}";

        var result = await ExternalCommandRunner.RunAsync(lms, args, timeoutSeconds: 600);

        return result.Success
            ? ProviderResult.Ok($"Model '{modelId}' loaded")
            : ProviderResult.Fail($"Load failed: {Truncate(result.Output, 500)}");
    }

    private const int AbsoluteContextFloor = 2048;
    private const int DefaultMaxContextGuess = 32768; // used only if LM Studio doesn't report the model's own maxContextLength

    /// <summary>
    /// Sizes context length off the ACTUAL model (its own reported
    /// maxContextLength/sizeBytes from `lms ls --json`) and this ACTUAL
    /// machine's detected VRAM/RAM (HardwareInfo), not fixed constants -
    /// a 4B model and a 70B model, or an 8GB-VRAM laptop and a 24GB-VRAM
    /// desktop, land on different numbers by design:
    ///   - Fast:     min(4096, model's max) - smallest useful window, favors load/inference speed.
    ///   - Balanced: min(model's max, half of it) - most single-file work fits without Max's cost.
    ///   - Max:      the model's own reported ceiling, scaled down first if the
    ///               detected inference pool (VRAM else system RAM) looks tight
    ///               relative to the model's on-disk size, THEN halved further
    ///               and retried on any real load failure - lms load fails
    ///               outright rather than silently truncating when a context
    ///               doesn't fit, so the floor is discovered empirically, not guessed.
    /// GPU offload is always "max" (best available), parallel requests fixed
    /// at 1 (single interactive coding session, no concurrent-request need),
    /// and TTL scales with the preset: Fast unloads quickly when idle to free
    /// resources, Balanced is more lenient, Max never auto-unloads.
    /// </summary>
    public async Task<ProviderResult> LoadModelWithPresetAsync(string modelId, LmStudioContextPreset preset)
    {
        var modelMaxContext = await GetModelMaxContextAsync(modelId) ?? DefaultMaxContextGuess;
        var modelSizeBytes = await GetModelSizeBytesAsync(modelId);

        if (preset == LmStudioContextPreset.Fast)
        {
            return await LoadModelAsync(modelId, Math.Min(4096, modelMaxContext), ttlSeconds: 300);
        }

        if (preset == LmStudioContextPreset.Balanced)
        {
            var balanced = Math.Max(AbsoluteContextFloor, Math.Min(modelMaxContext, modelMaxContext / 2));
            return await LoadModelAsync(modelId, balanced, ttlSeconds: 1800);
        }

        // Max: start from the model's own ceiling, pre-scaled down if the
        // detected pool looks tight relative to the model's size on disk, so
        // the first attempt is realistic instead of guaranteed to fail on
        // small hardware.
        var pool = await HardwareInfo.GetInferencePoolGbAsync();
        var modelSizeGb = modelSizeBytes.HasValue ? modelSizeBytes.Value / 1024.0 / 1024.0 / 1024.0 : (double?)null;
        var attempt = modelMaxContext;
        if (modelSizeGb is { } sizeGb && sizeGb > 0)
        {
            if (pool < sizeGb * 1.2) attempt = Math.Max(AbsoluteContextFloor, modelMaxContext / 4);
            else if (pool < sizeGb * 2) attempt = Math.Max(AbsoluteContextFloor, modelMaxContext / 2);
            // pool >= 2x model size: plenty of headroom, try the model's full native context.
        }

        ProviderResult last = ProviderResult.Fail("No load attempted");
        while (attempt >= AbsoluteContextFloor)
        {
            last = await LoadModelAsync(modelId, attempt, ttlSeconds: null);
            if (last.Success) return ProviderResult.Ok($"Model '{modelId}' loaded at {attempt} tokens of context (Max preset).");
            attempt /= 2;
        }

        return ProviderResult.Fail($"Max preset could not load '{modelId}' even at the floor context length ({AbsoluteContextFloor}): {last.Message}");
    }

    private async Task<int?> GetModelMaxContextAsync(string modelId)
    {
        var models = await ListInstalledModelsAsync();
        return FindMatchingModel(models, modelId)?.MaxContextLength;
    }

    private async Task<long?> GetModelSizeBytesAsync(string modelId)
    {
        var models = await ListInstalledModelsAsync();
        return FindMatchingModel(models, modelId)?.SizeBytes;
    }

    private static LmStudioModel? FindMatchingModel(IReadOnlyList<LmStudioModel> models, string modelId) =>
        models.FirstOrDefault(m => string.Equals(m.ModelKey, modelId, StringComparison.OrdinalIgnoreCase))
        ?? models.FirstOrDefault(m => m.ModelKey.Contains(modelId, StringComparison.OrdinalIgnoreCase) || modelId.Contains(m.ModelKey, StringComparison.OrdinalIgnoreCase));

    public async Task<bool> UnloadAllModelsAsync()
    {
        var lms = ResolveLmsPath();
        if (lms is null) return false;

        var result = await ExternalCommandRunner.RunAsync(lms, "unload --all", timeoutSeconds: 30);
        return result.Success;
    }

    /// <summary>Restart = unload whatever's currently loaded, then reload the same model under a (possibly new) context preset - the UI's "Restart Local Model" action after changing Fast/Balanced/Max.</summary>
    public async Task<ProviderResult> RestartModelAsync(string modelId, LmStudioContextPreset preset)
    {
        await UnloadAllModelsAsync();
        return await LoadModelWithPresetAsync(modelId, preset);
    }

    public Task<IReadOnlyList<string>> ListInstalledSkillsAsync() =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<IReadOnlyList<string>> ListInstalledPluginsAsync() =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<ProviderResult> InstallSkillAsync(SkillManifest skill) =>
        Task.FromResult(ProviderResult.Fail("LM Studio is a model backend, not a skill host - install skills against a coding-agent adapter (e.g. Claude Code)."));

    public Task<ProviderResult> InstallPluginAsync(PluginManifest plugin) =>
        Task.FromResult(ProviderResult.Fail("LM Studio does not host plugins - install against a coding-agent adapter (e.g. Claude Code)."));

    public Task<ProviderResult> RegisterMcpToolAsync(McpToolManifest tool) =>
        Task.FromResult(ProviderResult.Fail("LM Studio does not register MCP tools - register against a coding-agent adapter (e.g. Claude Code)."));

    /// <summary>
    /// Launching a "session" against LM Studio means: bring the local server
    /// up, then hand off to Claude Code with its API base pointed at that
    /// local OpenAI-compatible endpoint, so the local model is a drop-in
    /// swap for whichever model Claude Code would otherwise talk to.
    /// </summary>
    public async Task<ISessionHandle> LaunchSessionAsync(SessionLaunchOptions options)
    {
        if (!await EnsureServerRunningAsync())
        {
            throw new InvalidOperationException("Could not bring up the LM Studio local server.");
        }

        if (!string.IsNullOrWhiteSpace(options.Model))
        {
            var loadResult = await LoadModelWithPresetAsync(options.Model, options.ContextPreset ?? LmStudioContextPreset.Balanced);
            if (!loadResult.Success)
            {
                throw new InvalidOperationException(loadResult.Message);
            }
        }

        var claudeExe = await _claudeLocator.FindAsync()
                         ?? throw new InvalidOperationException("Claude Code executable not found - install it first.");

        await ExternalCommandRunner.RunAsync(claudeExe, "plugin marketplace update", timeoutSeconds: 20);

        var args = new List<string>();
        var resumeFlag = options.ResumeMode switch
        {
            SessionResumeMode.Continue => "--continue",
            SessionResumeMode.Pick => "--resume",
            _ => null,
        };
        if (resumeFlag is not null) args.Add(resumeFlag);

        var psi = new ProcessStartInfo
        {
            FileName = claudeExe,
            Arguments = string.Join(' ', args),
            WorkingDirectory = options.ProjectPath,
            UseShellExecute = false,
        };
        psi.EnvironmentVariables["ANTHROPIC_BASE_URL"] = $"http://localhost:{ServerPort}/v1";

        if (options.IsolateConfig)
        {
            var profileDir = IsolatedClaudeProfileService.GetOrCreateProfileDir(options.ProjectPath);
            psi.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = profileDir;
        }

        var process = Process.Start(psi);
        return new ProcessSessionHandle(Name, options.ProjectPath, process, watchForRateLimit: true);
    }

    private string? ResolveLmsPath() => _lmsPath ??= LmsCliLocator.Find();

    internal static bool IsServerUp(bool statusOk, string statusOutput)
    {
        var text = statusOutput.ToLowerInvariant();
        return statusOk && text.Contains("running") && !text.Contains("not running");
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[^maxLength..];
}
