using System.Diagnostics;
using System.Runtime.Versioning;
using TokenOptimizer.Core.Models;
using TokenOptimizer.Core.Security;
using TokenOptimizer.Providers.Manifests;

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

    public JcodeHarnessAdapter(ProxyCredentialStore credentials, FallbackProvider gatingKey, string jcodeProviderId, string displayName)
    {
        _credentials = credentials;
        _gatingKey = gatingKey;
        _jcodeProviderId = jcodeProviderId;
        _displayName = displayName;
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

        switch (resumeMode)
        {
            case SessionResumeMode.Continue:
            case SessionResumeMode.Pick:
                Console.WriteLine($"jcode: Continue/Pick not yet mapped, launching New - see docs/superpowers/plans/findings/2026-08-21-jcode-spike-findings.md");
                break;
        }

        return args;
    }

    public Task<ISessionHandle> LaunchSessionAsync(SessionLaunchOptions options)
    {
        var exe = ExecutableLocators.FindJcode()
                  ?? throw new InvalidOperationException("jcode executable not found - install with `irm https://jcode.sh/install.ps1 | iex`.");

        var claudeConfigDir = SessionHandoffExporter.GetEffectiveClaudeConfigDir(options.ProjectPath, options.IsolateConfig);
        SessionHandoffExporter.Export(options.ProjectPath, claudeConfigDir);

        var arguments = BuildArguments(_jcodeProviderId, options.Model, options.ResumeMode);
        var process = ProcessLaunchHelper.Start(exe, arguments, options.ProjectPath);

        return Task.FromResult<ISessionHandle>(new ProcessSessionHandle(Name, options.ProjectPath, process, watchForRateLimit: true));
    }
}
