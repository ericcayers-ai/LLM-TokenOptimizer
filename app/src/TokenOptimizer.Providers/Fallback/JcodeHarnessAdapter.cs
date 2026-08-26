using System.Runtime.Versioning;
using TokenOptimizer.Core.Models;
using TokenOptimizer.Core.Security;
using TokenOptimizer.Providers.Manifests;
using TokenOptimizer.Sandbox;

namespace TokenOptimizer.Providers.Fallback;

/// <summary>
/// Generic jcode-routed provider adapter. Replaces the per-provider
/// CodexAdapter by routing through jcode instead, which manages its own
/// auth and provider connections. Each instance is constructed with a
/// specific jcode provider ID (e.g. "openai") and display name, so the
/// same adapter class can cover multiple upstream providers as more are
/// verified against it. Gated on jcode being installed AND a credential
/// stored via ProxyCredentialStore (opt-in, same pattern as the adapters
/// it replaces).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class JcodeHarnessAdapter : IProviderAdapter
{
    private readonly ProxyCredentialStore _credentials;
    private readonly FallbackProvider _gatingKey;
    private readonly string _jcodeProviderId;
    private readonly string _displayName;
    private SandboxSessionLauncher? _sandboxLauncher;

    public JcodeHarnessAdapter(ProxyCredentialStore credentials, FallbackProvider gatingKey, string jcodeProviderId, string displayName,
        SandboxSessionLauncher? sandboxLauncher = null)
    {
        _credentials = credentials;
        _gatingKey = gatingKey;
        _jcodeProviderId = jcodeProviderId;
        _displayName = displayName;
        _sandboxLauncher = sandboxLauncher;
    }

    public string Name => _displayName;

    public Task<bool> IsAvailableAsync() =>
        Task.FromResult(ExecutableLocators.FindJcode() is not null && _credentials.HasCredential(_gatingKey));

    public Task<IReadOnlyList<string>> ListInstalledSkillsAsync() => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    public Task<IReadOnlyList<string>> ListInstalledPluginsAsync() => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<ProviderResult> InstallSkillAsync(SkillManifest skill) =>
        Task.FromResult(ProviderResult.Fail($"{_displayName} routes through jcode, which manages its own skills - not wired up here."));

    public Task<ProviderResult> InstallPluginAsync(PluginManifest plugin) =>
        Task.FromResult(ProviderResult.Fail($"{_displayName} does not host plugins via this adapter."));

    public Task<ProviderResult> RegisterMcpToolAsync(McpToolManifest tool) =>
        Task.FromResult(ProviderResult.Fail($"{_displayName} MCP registration is not wired up here - jcode reads Claude Code's live MCP config directly."));

    internal static string BuildArguments(string jcodeProviderId, string? model, SessionResumeMode resumeMode)
    {
        var args = $"--provider {jcodeProviderId}";
        if (!string.IsNullOrWhiteSpace(model))
            args += $" --model {model}";

        // Continue/Pick are not yet mapped by jcode itself (see
        // docs/plans/jcode-integration-plan.md) - they degrade to a fresh
        // session. Deliberately NOT Console.WriteLine'd: CliHost promises one
        // JSON object on stdout, and a stray line breaks every consumer's parse.
        // The degradation is documented here and in the plan instead.
        return args;
    }

    public async Task<ISessionHandle> LaunchSessionAsync(SessionLaunchOptions options)
    {
        var exe = ExecutableLocators.FindJcode()
                  ?? throw new InvalidOperationException("jcode executable not found - install with `irm https://jcode.sh/install.ps1 | iex`.");

        var claudeConfigDir = SessionHandoffExporter.GetEffectiveClaudeConfigDir(options.ProjectPath, options.IsolateConfig);
        SessionHandoffExporter.Export(options.ProjectPath, claudeConfigDir);

        // No path argument: jcode operates on the working directory, which is the /workspace mount inside the container.
        var arguments = BuildArguments(_jcodeProviderId, options.Model, options.ResumeMode);
        return await SandboxLauncher().LaunchAsync(Name, SandboxSessionLauncher.ToLinuxCommand(exe, arguments), options);
    }

    /// <summary>Lazily built default launcher (real OpenSandbox runtime + configured settings) when no launcher was injected.</summary>
    private SandboxSessionLauncher SandboxLauncher() =>
        _sandboxLauncher ??= SandboxLauncherFactory.CreateDefault();
}
