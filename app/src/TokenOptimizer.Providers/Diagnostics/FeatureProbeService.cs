using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using TokenOptimizer.Core.Diagnostics;
using TokenOptimizer.Core.Models;
using TokenOptimizer.Core.Security;
using TokenOptimizer.Providers.Claude;
using TokenOptimizer.Providers.Compat;
using TokenOptimizer.Providers.Fallback;
using TokenOptimizer.Providers.LlamaCpp;

namespace TokenOptimizer.Providers.Diagnostics;

[SupportedOSPlatform("windows")]
public sealed class FeatureProbeService
{
    private const int CommandTimeoutSeconds = 120;
    private const int PluginListTimeoutSeconds = 15;
    private const int UnslothBootTimeoutSeconds = 90;

    private readonly ClaudeExecutableLocator _claudeLocator;
    private readonly ProxyCredentialStore _credentials;
    private readonly Func<string, string, string?, int, IReadOnlyDictionary<string, string>?, CancellationToken, Task<CommandResult>> _runCommand;
    private readonly Func<string?> _findUnsloth;
    private readonly Func<string> _getClaudeHome;

    public FeatureProbeService(
        ClaudeExecutableLocator claudeLocator,
        ProxyCredentialStore credentials,
        Func<string, string, string?, int, IReadOnlyDictionary<string, string>?, CancellationToken, Task<CommandResult>>? runCommand = null,
        Func<string?>? findUnsloth = null,
        Func<string>? getClaudeHome = null)
    {
        _claudeLocator = claudeLocator;
        _credentials = credentials;
        _runCommand = runCommand ?? ExternalCommandRunner.RunAsync;
        _findUnsloth = findUnsloth ?? LlamaCppLocator.Find;
        _getClaudeHome = getClaudeHome ?? DefaultClaudeHome;
    }

    public async Task<SessionContinuityResult> ProbeSessionContinuityAsync(string providerName, string? model, string? projectPath, CancellationToken ct)
    {
        var claudeExe = await _claudeLocator.FindAsync();
        if (claudeExe is null)
        {
            return new SessionContinuityResult(false, providerName, string.Empty, "Claude Code executable not found.");
        }

        var codephrase = Guid.NewGuid().ToString("N");
        ClaudeLaunchEnvironment firstEnv;
        ClaudeLaunchEnvironment secondEnv;
        AnthropicCompatProxy? proxy = null;

        try
        {
            (firstEnv, secondEnv, proxy) = await BuildContinuityEnvironmentsAsync(providerName, model, projectPath, ct);
        }
        catch (Exception ex)
        {
            proxy?.DisposeAsync().AsTask().Wait(ct);
            return new SessionContinuityResult(false, providerName, codephrase, ModelProbeService.Redact(ex.Message));
        }

        try
        {
            var firstPrompt = $"Remember the codephrase {codephrase}. Reply OK";
            var firstArgs = CombineArguments(firstEnv.Arguments, $"-p \"{firstPrompt}\"");
            var first = await _runCommand(claudeExe, firstArgs, projectPath, CommandTimeoutSeconds, firstEnv.Env, ct);
            if (!first.Success)
            {
                return new SessionContinuityResult(false, providerName, codephrase, ModelProbeService.Redact(first.Output));
            }

            var secondPrompt = "What was the codephrase? Reply with it only.";
            var secondArgs = CombineArguments(secondEnv.Arguments, $"-p \"{secondPrompt}\"");
            var second = await _runCommand(claudeExe, secondArgs, projectPath, CommandTimeoutSeconds, secondEnv.Env, ct);
            if (!second.Success)
            {
                return new SessionContinuityResult(false, providerName, codephrase, ModelProbeService.Redact(second.Output));
            }

            var text = second.Output.Trim();
            if (!text.Contains(codephrase, StringComparison.Ordinal))
            {
                return new SessionContinuityResult(false, providerName, codephrase, $"Codephrase did not round-trip. Response: {ModelProbeService.Redact(text)}");
            }

            return new SessionContinuityResult(true, providerName, codephrase, null);
        }
        finally
        {
            if (proxy is not null)
            {
                await proxy.DisposeAsync();
            }
        }
    }

    public async Task<SharedSkillsPluginsResult> ProbeSharedSkillsPluginsAsync(string? projectPath, CancellationToken ct)
    {
        var claudeExe = await _claudeLocator.FindAsync();
        if (claudeExe is null)
        {
            return new SharedSkillsPluginsResult(false, "Claude Code executable not found.", new Dictionary<string, SkillsPluginsSnapshot>(StringComparer.OrdinalIgnoreCase));
        }

        var snapshots = new Dictionary<string, SkillsPluginsSnapshot>(StringComparer.OrdinalIgnoreCase);
        var skillIds = ListSkillIds();

        try
        {
            var claudeEnv = BuildPluginListEnv("Claude Code", null, projectPath);
            snapshots["Claude Code"] = new SkillsPluginsSnapshot(
                await RunPluginListAsync(claudeExe, claudeEnv, projectPath, ct),
                skillIds);

            var (groqEnv, groqProxy) = await BuildPluginListEnvWithProxyAsync("Groq", projectPath, ct);
            try
            {
                snapshots["Groq"] = new SkillsPluginsSnapshot(
                    await RunPluginListAsync(claudeExe, groqEnv, projectPath, ct),
                    skillIds);
            }
            finally
            {
                if (groqProxy is not null) await groqProxy.DisposeAsync();
            }

            var openCodeEnv = await BuildPluginListEnvAsync("OpenCode", null, projectPath, ct);
            snapshots["OpenCode"] = new SkillsPluginsSnapshot(
                await RunPluginListAsync(claudeExe, openCodeEnv, projectPath, ct),
                skillIds);

            var unslothEnv = await BuildPluginListEnvAsync("Unsloth (local model)", null, projectPath, ct);
            snapshots["Unsloth (local model)"] = new SkillsPluginsSnapshot(
                await RunPluginListAsync(claudeExe, unslothEnv, projectPath, ct),
                skillIds);
        }
        catch (Exception ex)
        {
            return new SharedSkillsPluginsResult(false, ModelProbeService.Redact(ex.Message), snapshots);
        }

        var first = snapshots.First().Value;
        var pluginsMatch = snapshots.All(s => s.Value.Plugins.SequenceEqual(first.Plugins, StringComparer.OrdinalIgnoreCase));
        var skillsMatch = snapshots.All(s => s.Value.SkillIds.SequenceEqual(first.SkillIds, StringComparer.OrdinalIgnoreCase));

        if (pluginsMatch && skillsMatch)
        {
            return new SharedSkillsPluginsResult(true, null, snapshots);
        }

        var errors = new List<string>();
        if (!pluginsMatch) errors.Add("Plugin sets differ across providers.");
        if (!skillsMatch) errors.Add("Skill id sets differ across providers.");
        return new SharedSkillsPluginsResult(false, string.Join(" ", errors), snapshots);
    }

    public Task<HandoffProbeResult> ProbeExportHandoffAsync(string projectPath, CancellationToken ct)
    {
        return ProbeExportHandoffAsync(projectPath, null, ct);
    }

    public Task<HandoffProbeResult> ProbeExportHandoffAsync(string projectPath, string? claudeConfigDir, CancellationToken ct)
    {
        try
        {
            var handoffFile = SessionHandoffExporter.Export(projectPath, claudeConfigDir);
            if (!File.Exists(handoffFile))
            {
                return Task.FromResult(new HandoffProbeResult(false, handoffFile, "Handoff file was not created."));
            }

            if (new FileInfo(handoffFile).Length == 0)
            {
                return Task.FromResult(new HandoffProbeResult(false, handoffFile, "Handoff file is empty."));
            }

            var agentsMd = Path.Combine(projectPath, "AGENTS.md");
            if (!File.Exists(agentsMd) || !File.ReadAllText(agentsMd).Contains(".claude-handoff/session-handoff.md"))
            {
                return Task.FromResult(new HandoffProbeResult(false, handoffFile, "AGENTS.md marker reference missing."));
            }

            return Task.FromResult(new HandoffProbeResult(true, handoffFile, null));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new HandoffProbeResult(false, string.Empty, ModelProbeService.Redact(ex.Message)));
        }
    }

    private async Task<(ClaudeLaunchEnvironment First, ClaudeLaunchEnvironment Second, AnthropicCompatProxy? Proxy)> BuildContinuityEnvironmentsAsync(string providerName, string? model, string? projectPath, CancellationToken ct)
    {
        var baseOptions = new SessionLaunchOptions(projectPath ?? Environment.CurrentDirectory, model, IsolateConfig: false);
        var normalized = providerName.ToLowerInvariant();

        if (normalized is "claude code" or "claude")
        {
            var adapter = new ClaudeCodeAdapter(_claudeLocator, new CommandAvailability());
            var first = adapter.BuildLaunchEnvironment(baseOptions with { ResumeMode = SessionResumeMode.New });
            var second = adapter.BuildLaunchEnvironment(baseOptions with { ResumeMode = SessionResumeMode.Continue });
            return (first, second, null);
        }

        if (normalized is "groq")
        {
            var apiKey = _credentials.GetCredentialPlainText(FallbackProvider.Groq)
                         ?? throw new InvalidOperationException("No Groq credential stored.");
            var proxy = new AnthropicCompatProxy(GroqAdapter.ApiBaseUrl, () => apiKey);
            await proxy.StartAsync();
            var adapter = new GroqAdapter(_credentials, _claudeLocator);
            var first = adapter.BuildLaunchEnvironment(baseOptions with { ResumeMode = SessionResumeMode.New }, proxy.BaseUrl);
            var second = adapter.BuildLaunchEnvironment(baseOptions with { ResumeMode = SessionResumeMode.Continue }, proxy.BaseUrl);
            return (first, second, proxy);
        }

        throw new InvalidOperationException($"Unknown provider: {providerName}");
    }

    private ClaudeLaunchEnvironment BuildPluginListEnv(string providerName, string? model, string? projectPath)
    {
        var options = new SessionLaunchOptions(projectPath ?? Environment.CurrentDirectory, model, IsolateConfig: false, SessionResumeMode.Continue);
        var normalized = providerName.ToLowerInvariant();

        if (normalized is "claude code" or "claude")
        {
            return new ClaudeCodeAdapter(_claudeLocator, new CommandAvailability()).BuildLaunchEnvironment(options);
        }

        throw new InvalidOperationException($"Unknown provider: {providerName}");
    }

    private async Task<(ClaudeLaunchEnvironment Env, AnthropicCompatProxy? Proxy)> BuildPluginListEnvWithProxyAsync(string providerName, string? projectPath, CancellationToken ct)
    {
        var options = new SessionLaunchOptions(projectPath ?? Environment.CurrentDirectory, null, IsolateConfig: false, SessionResumeMode.Continue);
        var normalized = providerName.ToLowerInvariant();

        if (normalized is "groq")
        {
            var apiKey = _credentials.GetCredentialPlainText(FallbackProvider.Groq)
                         ?? throw new InvalidOperationException("No Groq credential stored.");
            var proxy = new AnthropicCompatProxy(GroqAdapter.ApiBaseUrl, () => apiKey);
            await proxy.StartAsync();
            var env = new GroqAdapter(_credentials, _claudeLocator).BuildLaunchEnvironment(options, proxy.BaseUrl);
            return (env, proxy);
        }

        throw new InvalidOperationException($"Unknown provider: {providerName}");
    }

    private async Task<ClaudeLaunchEnvironment> BuildPluginListEnvAsync(string providerName, string? model, string? projectPath, CancellationToken ct)
    {
        var options = new SessionLaunchOptions(projectPath ?? Environment.CurrentDirectory, model, IsolateConfig: false, SessionResumeMode.Continue);
        var normalized = providerName.ToLowerInvariant();

        if (normalized is "opencode" or "open code")
        {
            var apiKey = _credentials.GetCredentialPlainText(FallbackProvider.OpenCode)
                         ?? throw new InvalidOperationException("No OpenCode Go credential stored.");
            return new OpenCodeAdapter(_credentials, _claudeLocator).BuildLaunchEnvironment(options, apiKey);
        }

        if (normalized is "unsloth (local model)" or "unsloth")
        {
            var unsloth = _findUnsloth();
            if (string.IsNullOrWhiteSpace(unsloth))
            {
                throw new InvalidOperationException("unsloth CLI not found.");
            }

            var resolvedModel = string.IsNullOrWhiteSpace(model)
                ? $"{LlamaCppModelCatalog.SupportedFamilies[0].RepoId}:{LlamaCppModelCatalog.SupportedFamilies[0].RecommendedQuant}"
                : model;
            var bootArgs = $"start claude --model {resolvedModel} --max-seq-length 131072 --no-launch --serve";
            CommandResult boot;
            using (var bootCts = new CancellationTokenSource(TimeSpan.FromSeconds(UnslothBootTimeoutSeconds)))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, bootCts.Token))
            {
                boot = await _runCommand(unsloth, bootArgs, projectPath, 0, null, linked.Token);
            }

            if (!boot.Success)
            {
                throw new InvalidOperationException($"unsloth boot failed: {ModelProbeService.Redact(boot.Output)}");
            }

            var generated = LlamaCppAdapter.ParseGeneratedEnvironment(boot.Output);
            if (generated is null)
            {
                throw new InvalidOperationException("Could not parse ANTHROPIC_BASE_URL from unsloth boot output.");
            }

            return new ClaudeLaunchEnvironmentBuilder()
                .WithAnthropicBaseUrl(generated.Value.BaseUrl.ToString())
                .WithAnthropicAuthToken(generated.Value.ApiKey)
                .Build();
        }

        throw new InvalidOperationException($"Unknown provider: {providerName}");
    }

    private async Task<IReadOnlyList<string>> RunPluginListAsync(string claudeExe, ClaudeLaunchEnvironment env, string? projectPath, CancellationToken ct)
    {
        var result = await _runCommand(claudeExe, "plugin list", projectPath, PluginListTimeoutSeconds, env.Env, ct);
        if (!result.Success)
        {
            throw new InvalidOperationException($"claude plugin list failed: {ModelProbeService.Redact(result.Output)}");
        }

        return result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderBy(line => line, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<string> ListSkillIds()
    {
        var skillsDir = Path.Combine(_getClaudeHome(), "skills");
        if (!Directory.Exists(skillsDir)) return Array.Empty<string>();

        return Directory.EnumerateDirectories(skillsDir)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    private static string CombineArguments(string? envArguments, string promptArgument)
    {
        return string.IsNullOrWhiteSpace(envArguments)
            ? promptArgument
            : $"{envArguments} {promptArgument}";
    }

    private static string DefaultClaudeHome() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
}

public sealed record SessionContinuityResult(bool Ok, string Provider, string Codephrase, string? Error);
public sealed record SkillsPluginsSnapshot(IReadOnlyList<string> Plugins, IReadOnlyList<string> SkillIds);
public sealed record SharedSkillsPluginsResult(bool Ok, string? Error, IReadOnlyDictionary<string, SkillsPluginsSnapshot> PerProvider);
public sealed record HandoffProbeResult(bool Ok, string HandoffFile, string? Error);
