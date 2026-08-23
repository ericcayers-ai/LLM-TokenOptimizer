using TokenOptimizer.Sandbox;

namespace TokenOptimizer.Providers.Fallback;

/// <summary>
/// Launches interactive coding-agent CLI sessions inside an OpenSandbox
/// sandbox instead of as host processes: creates a sandbox with the project
/// mounted at /workspace and runs a Linux command line inside it, returning
/// a SandboxSessionHandle that streams the session's output (and feeds the
/// rate-limit scanner).
///
/// Interim credential story (ratified until upstream Credential Vault wiring
/// lands): when SessionLaunchOptions.IsolateConfig is false the launcher
/// mounts the host Claude config dir (%USERPROFILE%\.claude) into the
/// container at /root/.claude so stored auth/skills/plugins carry over;
/// when IsolateConfig is true no such mount is made and the CLI uses an
/// isolated profile inside the container.
/// </summary>
public sealed class SandboxSessionLauncher
{
    private const string WorkspaceMountPoint = "/workspace";
    private const string ClaudeConfigMountPoint = "/root/.claude";

    private readonly ISandboxRuntime _runtime;
    private readonly SandboxSettings _settings;

    public SandboxSessionLauncher(ISandboxRuntime runtime, SandboxSettings settings)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>Creates a sandbox with the project mounted at /workspace and runs linuxCommand inside it. Returns a SandboxSessionHandle streaming its output.</summary>
    public async Task<ISessionHandle> LaunchAsync(
        string providerName,
        string linuxCommand,
        SessionLaunchOptions options,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        if (string.IsNullOrWhiteSpace(options?.ProjectPath))
            throw new InvalidOperationException("Sandbox launch requires a ProjectPath - there is nothing to mount at /workspace.");

        var spec = new SandboxSpec(
            Image: _settings.AgentImage,
            Mounts: BuildMounts(options.ProjectPath, options.IsolateConfig),
            Timeout: TimeSpan.FromMinutes(_settings.IdleTimeoutMinutes),
            Env: MergeEnvironment(environment));

        var sandbox = await _runtime.CreateAsync(spec);

        var events = _runtime.ExecAsync(sandbox.Id, ["bash", "-lc", linuxCommand]);
        return new SandboxSessionHandle(providerName, options.ProjectPath, _runtime, sandbox.Id, events, watchForRateLimit: true);
    }

    private static IReadOnlyDictionary<string, string>? MergeEnvironment(IReadOnlyDictionary<string, string>? environment) =>
        environment is null || environment.Count == 0 ? null : environment;

    /// <summary>
    /// Maps a host executable path (+ optional arguments) to the in-container
    /// command line used for sandbox sessions: the CLI's file name without
    /// extension (container images install the CLIs on PATH - .exe/.cmd
    /// wrappers don't exist there) plus the arguments verbatim.
    /// </summary>
    internal static string ToLinuxCommand(string hostExecutablePath, string? arguments = null)
    {
        var cliName = Path.GetFileNameWithoutExtension(hostExecutablePath);
        return string.IsNullOrWhiteSpace(arguments) ? cliName : $"{cliName} {arguments}";
    }

    private static IReadOnlyList<SandboxMount> BuildMounts(string projectPath, bool isolateConfig)
    {
        var mounts = new List<SandboxMount>
        {
            new(WorkspaceMountPoint, projectPath),
        };

        if (!isolateConfig)
        {
            var claudeHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            // Stored auth/skills/plugins are credential material: mounted read-only
            // so the container can read but never modify the host's Claude config.
            mounts.Add(new SandboxMount(ClaudeConfigMountPoint, claudeHome, ReadOnly: true));
        }

        return mounts;
    }
}
