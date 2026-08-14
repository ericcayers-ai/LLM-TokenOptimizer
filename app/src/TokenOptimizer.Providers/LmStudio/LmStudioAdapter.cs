using System.Diagnostics;
using System.Text.Json;
using TokenOptimizer.Core.Diagnostics;
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
                if (key is not null) models.Add(new LmStudioModel(key, type));
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

    public async Task<ProviderResult> LoadModelAsync(string modelId, int contextLength = 8192)
    {
        var lms = ResolveLmsPath();
        if (lms is null) return ProviderResult.Fail("lms CLI not found");

        var result = await ExternalCommandRunner.RunAsync(
            lms, $"load {modelId} --gpu max --context-length {contextLength} -y", timeoutSeconds: 600);

        return result.Success
            ? ProviderResult.Ok($"Model '{modelId}' loaded")
            : ProviderResult.Fail($"Load failed: {Truncate(result.Output, 500)}");
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
            var loadResult = await LoadModelAsync(options.Model);
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
