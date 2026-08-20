using System.Diagnostics.CodeAnalysis;
using TokenOptimizer.Core.Models;

namespace TokenOptimizer.Providers.Claude;

/// <summary>
/// Immutable result of building the process environment and arguments used to
/// launch Claude Code. Probes and adapters must share this exact construction
/// so a passing probe proves the real launch path works.
/// </summary>
public sealed record ClaudeLaunchEnvironment(IReadOnlyDictionary<string, string> Env, string Arguments);

/// <summary>
/// Builds <see cref="ClaudeLaunchEnvironment"/> instances for the real
/// Claude Code launch path. Preserves insertion order of arguments so callers
/// can match their historical CLI shape while still reusing the shared
/// environment wiring.
/// </summary>
public sealed class ClaudeLaunchEnvironmentBuilder
{
    private readonly List<string> _arguments = new();
    private readonly Dictionary<string, string> _environment = new(StringComparer.OrdinalIgnoreCase);

    public ClaudeLaunchEnvironmentBuilder WithModel(string? model)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            _arguments.Add($"--model {model}");
        }
        return this;
    }

    public ClaudeLaunchEnvironmentBuilder WithResumeMode(SessionResumeMode mode)
    {
        var flag = mode switch
        {
            SessionResumeMode.Continue => "--continue",
            SessionResumeMode.Pick => "--resume",
            _ => null,
        };
        if (flag is not null)
        {
            _arguments.Add(flag);
        }
        return this;
    }

    public ClaudeLaunchEnvironmentBuilder WithAnthropicBaseUrl(string? baseUrl)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            _environment["ANTHROPIC_BASE_URL"] = baseUrl;
        }
        return this;
    }

    public ClaudeLaunchEnvironmentBuilder WithAnthropicAuthToken(string? token)
    {
        if (!string.IsNullOrWhiteSpace(token))
        {
            _environment["ANTHROPIC_AUTH_TOKEN"] = token;
        }
        return this;
    }

    public ClaudeLaunchEnvironmentBuilder WithClaudeMemIsolation()
    {
        _environment["CLAUDE_MEM_WORKER_PORT"] = CompanionToolingInstaller.IsolatedWorkerPort.ToString();
        _environment["CLAUDE_MEM_DATA_DIR"] = CompanionToolingInstaller.IsolatedDataDir;
        return this;
    }

    public ClaudeLaunchEnvironmentBuilder WithIsolatedConfig(string projectPath)
    {
        _environment["CLAUDE_CONFIG_DIR"] = IsolatedClaudeProfileService.GetOrCreateProfileDir(projectPath);
        return this;
    }

    public ClaudeLaunchEnvironment Build() =>
        new(new Dictionary<string, string>(_environment, StringComparer.OrdinalIgnoreCase), string.Join(' ', _arguments));
}
