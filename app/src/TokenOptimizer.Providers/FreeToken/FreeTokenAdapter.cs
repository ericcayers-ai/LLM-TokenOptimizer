using System.Diagnostics;
using System.Text.Json.Nodes;
using TokenOptimizer.Core.Diagnostics;
using TokenOptimizer.Core.RateLimit;
using TokenOptimizer.Providers.Claude;
using TokenOptimizer.Providers.Compat;
using TokenOptimizer.Providers.Fallback;
using TokenOptimizer.Providers.Manifests;

namespace TokenOptimizer.Providers.FreeToken;

/// <summary>
/// FreeToken (github.com/FlashML-org/FreeToken) is an edge-native MoE serving
/// engine: frontier open-weight models (Qwen3.6-35B-A3B, GLM-5.2,
/// DeepSeek-V4-Flash, ...) served from consumer GPUs via bandwidth-adaptive
/// CPU-GPU co-execution. On Windows it ships only as the desktop app (the
/// PyPI engine is Linux-only), which serves BOTH the OpenAI API
/// (/v1/chat/completions) and the Anthropic API (/v1/messages) on
/// http://127.0.0.1:1919 per its own quickstart docs.
///
/// That native Anthropic endpoint is what makes this adapter simple and real:
/// Claude Code points straight at the FreeToken server through
/// ANTHROPIC_BASE_URL (same env-var contract ClaudeExecutableLocator-based
/// adapters already use) with no schema translation, no proxy, and no
/// sandbox round-trip - the server binds loopback on the host and the CLI
/// session runs host-side beside it. The server only listens once a model
/// is loaded in the desktop GUI; IsAvailableAsync probes /v1/models so a
/// not-yet-loaded app reads as unavailable rather than launching a session
/// that would immediately fail.
///
/// Honest gaps (mirrors LlamaCppAdapter's disclosure style): model
/// load/unload happens in the FreeToken desktop GUI, not via this adapter -
/// there is no documented headless load endpoint to drive. Rate-limit
/// watching is not wired (a local engine has no usage-limit banner).
/// </summary>
public sealed class FreeTokenAdapter : IProviderAdapter
{
    private readonly ClaudeExecutableLocator? _claudeLocator;

    public FreeTokenAdapter(ClaudeExecutableLocator? claudeLocator = null)
    {
        _claudeLocator = claudeLocator;
    }

    public string Name => "FreeToken (local MoE)";

    /// <summary>
    /// Available when the desktop app is installed AND its API is actually
    /// serving (i.e. a model is loaded). A plain install without a loaded
    /// model reports unavailable instead of half-working.
    /// </summary>
    public async Task<bool> IsAvailableAsync()
    {
        if (FreeTokenLocator.FindDesktopApp() is null) return false;
        return await ProbeServerAsync();
    }

    /// <summary>GET {base}/v1/models with a short timeout - true iff HTTP 200.</summary>
    internal static async Task<bool> ProbeServerAsync(string baseUrl = FreeTokenLocator.DefaultBaseUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var resp = await http.GetAsync($"{baseUrl.TrimEnd('/')}/v1/models");
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Model ids the running FreeToken server reports, for callers that surface them (empty when unreachable).</summary>
    public static async Task<IReadOnlyList<string>> ListServedModelsAsync(string baseUrl = FreeTokenLocator.DefaultBaseUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var body = await http.GetStringAsync($"{baseUrl.TrimEnd('/')}/v1/models");
            var node = JsonNode.Parse(body);
            return node?["data"]?.AsArray()
                .Where(m => m?["id"] is not null)
                .Select(m => m!["id"]!.GetValue<string>())
                .ToList()
                ?? [];
        }
        catch
        {
            return [];
        }
    }

    public Task<IReadOnlyList<string>> ListInstalledSkillsAsync() =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlyList<string>> ListInstalledPluginsAsync() =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<ProviderResult> InstallSkillAsync(SkillManifest skill) =>
        Task.FromResult(ProviderResult.Fail("FreeToken is a model backend, not a skill host - install skills against a coding-agent adapter (e.g. Claude Code)."));

    public Task<ProviderResult> InstallPluginAsync(PluginManifest plugin) =>
        Task.FromResult(ProviderResult.Fail("FreeToken does not host plugins - install against a coding-agent adapter (e.g. Claude Code)."));

    public Task<ProviderResult> RegisterMcpToolAsync(McpToolManifest tool) =>
        Task.FromResult(ProviderResult.Fail("FreeToken does not register MCP tools - register against a coding-agent adapter (e.g. Claude Code)."));

    /// <summary>
    /// Ensures the FreeToken server is up (launching the desktop app and
    /// waiting for its API when it isn't), then launches Claude Code on the
    /// host pointed at it via ANTHROPIC_BASE_URL/ANTHROPIC_AUTH_TOKEN -
    /// FreeToken serves Anthropic-shaped /v1/messages natively, so no proxy
    /// or translation layer sits in between.
    /// </summary>
    public async Task<ISessionHandle> LaunchSessionAsync(SessionLaunchOptions options)
    {
        var appPath = FreeTokenLocator.FindDesktopApp()
            ?? throw new InvalidOperationException(
                "FreeToken desktop app not found - install it from https://www.flashml.ai/ (FreeToken-Setup-win-x64.exe).");

        if (!await ProbeServerAsync())
        {
            LaunchDesktopApp(appPath);
            await WaitForServerAsync(TimeSpan.FromSeconds(ServerStartTimeoutSeconds));
        }

        var models = await ListServedModelsAsync();
        if (models.Count == 0)
        {
            throw new InvalidOperationException(
                "FreeToken's API is reachable but reports no loaded models. " +
                "Load a model in the FreeToken desktop window, then relaunch.");
        }

        var claudeExe = (_claudeLocator is not null ? await _claudeLocator.FindAsync() : null)
            ?? throw new InvalidOperationException("Claude Code executable not found - install it first.");

        await ClaudeCodeAdapter.RefreshPluginMarketplacesAsync(claudeExe);

        // FreeToken accepts any bearer token; the value here only satisfies
        // Claude Code's own non-empty-auth gate (see UnifiedModelRouter docs).
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ANTHROPIC_BASE_URL"] = FreeTokenLocator.DefaultBaseUrl,
            ["ANTHROPIC_AUTH_TOKEN"] = "freetoken-local",
            ["ANTHROPIC_MODEL"] = options.Model ?? models[0],
        };

        var resumeArgs = options.ResumeMode switch
        {
            SessionResumeMode.Continue => "--continue",
            SessionResumeMode.Pick => "--resume",
            _ => "",
        };

        var process = ProcessLaunchHelper.Start(claudeExe, resumeArgs, options.ProjectPath, environment)
            ?? throw new InvalidOperationException($"Failed to start Claude Code at '{claudeExe}'.");

        return new ProcessSessionHandle(Name, options.ProjectPath, process);
    }

    private static void LaunchDesktopApp(string appPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = appPath,
            UseShellExecute = true, // GUI app: let the shell place it
        });
    }

    /// <summary>Polls /v1/models until the server answers or the budget runs out. The desktop app needs its GUI opened by a human before it will bind the port; this waits patiently but never fakes success.</summary>
    private static async Task WaitForServerAsync(TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            if (await ProbeServerAsync()) return;
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new TimeoutException(
            $"FreeToken did not open its API on {FreeTokenLocator.DefaultBaseUrl} within {budget.TotalSeconds:0}s. " +
            "The desktop app must be running with a model loaded (its GUI opens on launch - pick a model there), then retry.");
    }

    /// <summary>How long LaunchSessionAsync waits for a just-launched desktop app to bind :1919 - generous for first-run model downloads.</summary>
    internal const int ServerStartTimeoutSeconds = 90;
}
