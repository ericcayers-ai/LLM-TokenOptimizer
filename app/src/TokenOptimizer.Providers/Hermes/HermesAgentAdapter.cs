using System.Runtime.Versioning;
using TokenOptimizer.Core.Models;
using TokenOptimizer.Providers.Fallback;
using TokenOptimizer.Providers.Manifests;

namespace TokenOptimizer.Providers.Hermes;

/// <summary>
/// Hermes Agent (github.com/NousResearch/hermes-agent) integrated as an
/// encompassing peer layer, not flattened into a model endpoint: it is itself
/// an agent platform (CLI/desktop/TUI/gateway/proxy) with its own provider
/// config, fallback chain, and skills system. This adapter launches real
/// `hermes chat` sessions in the project directory - host-side by design,
/// because Hermes owns its own container story and double-sandboxing would
/// break its tool access (same reasoning that keeps login/GUI flows host-side).
///
/// Model selection maps to `--model`, resume modes to `-c` / `--resume`.
/// To point Hermes at TokenOptimizer-managed local engines, run
/// scripts/Setup-HermesIntegration.ps1 - it writes Hermes' native custom-endpoint
/// contract (`model.provider: custom` + `base_url` + `api_mode`) via
/// `hermes config set`; this adapter deliberately does not mutate Hermes config.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HermesAgentAdapter : IProviderAdapter
{
    /// <summary>Display name - referenced verbatim in CliHost/TrackRateLimitOutcome-style mappings and docs.</summary>
    public const string ProviderName = "Hermes Agent";

    private readonly Func<string?>? _findExecutable;
    private readonly Func<bool>? _probeHome;

    public HermesAgentAdapter(Func<string?>? findExecutable = null, Func<bool>? probeHome = null)
    {
        _findExecutable = findExecutable;
        _probeHome = probeHome;
    }

    public string Name => ProviderName;

    /// <summary>
    /// Available when a hermes executable is found AND its profile home exists.
    /// The venv layout puts the CLI inside the home dir, so for that shape the
    /// second check is implied; an on-PATH install without any ~/.hermes has no
    /// credentials/config and would only fail later - report unavailable instead.
    /// </summary>
    public Task<bool> IsAvailableAsync()
    {
        var exe = (_findExecutable ?? HermesLocator.Find)();
        if (exe is null) return Task.FromResult(false);
        return Task.FromResult((_probeHome ?? ProbeDefaultHome)());
    }

    internal static bool ProbeDefaultHome()
    {
        // Hermes resolves its home from $HERMES_HOME when set (its own
        // documented rule - profiles live under $HERMES_HOME/profiles), falling
        // back to ~/.hermes on stock installs. Check in that exact order or a
        // relocated install reads as absent.
        var envHome = Environment.GetEnvironmentVariable("HERMES_HOME");
        if (!string.IsNullOrWhiteSpace(envHome))
        {
            return Directory.Exists(envHome);
        }
        return Directory.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hermes"));
    }

    public Task<IReadOnlyList<string>> ListInstalledSkillsAsync() =>
        // Skills live under the active profile's skills/ directory; surfacing
        // them here would duplicate Hermes' own `hermes skills list`. Not wired.
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlyList<string>> ListInstalledPluginsAsync() =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<ProviderResult> InstallSkillAsync(SkillManifest skill) =>
        Task.FromResult(ProviderResult.Fail(
            $"{ProviderName} manages its own skills via `hermes skills` - install against Claude Code or sync manually."));

    public Task<ProviderResult> InstallPluginAsync(PluginManifest plugin) =>
        Task.FromResult(ProviderResult.Fail($"{ProviderName} does not host plugins via this adapter."));

    public Task<ProviderResult> RegisterMcpToolAsync(McpToolManifest tool) =>
        Task.FromResult(ProviderResult.Fail($"{ProviderName} MCP registration is not wired up here - manage via `hermes mcp`."));

    public Task<ISessionHandle> LaunchSessionAsync(SessionLaunchOptions options)
    {
        var exe = (_findExecutable ?? HermesLocator.Find)()
            ?? throw new InvalidOperationException(
                "Hermes Agent CLI not found - install with: curl -fsSL https://hermes-agent.nousresearch.com/install.sh | bash");

        var process = ProcessLaunchHelper.Start(exe, BuildArguments(options.ProjectPath, options.Model, options.ResumeMode), options.ProjectPath)
            ?? throw new InvalidOperationException($"Failed to start Hermes Agent at '{exe}'.");

        return Task.FromResult<ISessionHandle>(new ProcessSessionHandle(Name, options.ProjectPath, process));
    }

    /// <summary>
    /// `--in DIR` pins both the workspace and the session there (verified
    /// against cmd_chat in hermes source); `-c` resumes the folder's most
    /// recent session. SessionResumeMode.Pick has NO Hermes equivalent -
    /// `chat --resume` requires an explicit session id (verified live:
    /// "error: argument --resume/-r: expected one argument"), so Pick fails
    /// fast here rather than degrading silently to some other behavior.
    /// </summary>
    internal static string BuildArguments(string projectPath, string? model, SessionResumeMode resumeMode)
    {
        if (resumeMode == SessionResumeMode.Pick)
        {
            throw new NotSupportedException(
                "Hermes Agent has no interactive session picker on launch - " +
                "`hermes chat --resume` requires an explicit session id. Use " +
                "Continue (most recent) or New, then switch sessions inside Hermes.");
        }

        var args = $"chat --in \"{projectPath}\"";
        if (!string.IsNullOrWhiteSpace(model))
            args += $" --model {model}";
        args += resumeMode switch
        {
            SessionResumeMode.Continue => " -c",
            _ => "",
        };
        return args;
    }
}
