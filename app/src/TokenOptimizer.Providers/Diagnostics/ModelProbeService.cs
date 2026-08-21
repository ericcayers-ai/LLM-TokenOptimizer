using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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

/// <summary>
/// Headless, non-interactive probe that launches each selectable model
/// through the exact same path the real adapters use. A passing probe
/// therefore proves the real launch path works end-to-end.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ModelProbeService
{
    internal const string ProbePrompt = "Reply with exactly: PONG";
    internal const int ProbeTimeoutSeconds = 120;
    internal const int UnslothBootTimeoutSeconds = 90;

    private readonly ClaudeExecutableLocator _claudeLocator;
    private readonly ProxyCredentialStore _credentials;
    private readonly Func<string, string, string?, int, IReadOnlyDictionary<string, string>?, CancellationToken, Task<CommandResult>> _runCommand;
    private readonly Func<string?> _findAntigravity;

    public ModelProbeService(
        ClaudeExecutableLocator claudeLocator,
        ProxyCredentialStore credentials,
        Func<string, string, string?, int, IReadOnlyDictionary<string, string>?, CancellationToken, Task<CommandResult>>? runCommand = null,
        Func<string?>? findAntigravity = null)
    {
        _claudeLocator = claudeLocator;
        _credentials = credentials;
        _runCommand = runCommand ?? DefaultRunCommand;
        _findAntigravity = findAntigravity ?? ExecutableLocators.FindAntigravity;
    }

    private static Task<CommandResult> DefaultRunCommand(
        string fileName,
        string arguments,
        string? workingDirectory,
        int timeoutSeconds,
        IReadOnlyDictionary<string, string>? extraEnvironment,
        CancellationToken cancellationToken)
    {
        return ExternalCommandRunner.RunAsync(
            fileName,
            arguments,
            workingDirectory,
            timeoutSeconds,
            extraEnvironment,
            cancellationToken);
    }

    public async Task<ProbeResult> ProbeAsync(string providerName, string model, string? projectPath, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return providerName.ToLowerInvariant() switch
            {
                "claude code" or "claude" => await ProbeClaudeAsync(model, projectPath, ct),
                "groq" => await ProbeGroqAsync(model, projectPath, ct),
                "opcode" or "opencode" => await ProbeOpenCodeAsync(model, projectPath, ct),
                "unsloth (local model)" or "unsloth" => await ProbeUnslothAsync(model, projectPath, ct),
                "antigravity" => await ProbeAntigravityAsync(model, ct),
                _ => new ProbeResult(false, providerName, model, "", (int)sw.ElapsedMilliseconds, $"Unknown provider: {providerName}"),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProbeResult(false, providerName, model, "", (int)sw.ElapsedMilliseconds, Redact(ex.Message));
        }
    }

    public async Task<IReadOnlyList<ProbeResult>> ProbeAllAsync(IEnumerable<(string provider, string model)> matrix, CancellationToken ct)
    {
        var results = new List<ProbeResult>();
        foreach (var (provider, model) in matrix)
        {
            results.Add(await ProbeAsync(provider, model, null, ct));
        }
        return results;
    }

    private async Task<ProbeResult> ProbeClaudeAsync(string model, string? projectPath, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var claudeExe = await _claudeLocator.FindAsync();
        if (claudeExe is null)
        {
            return Fail("Claude Code", model, sw, "Claude Code executable not found.");
        }

        var options = new SessionLaunchOptions(projectPath ?? Environment.CurrentDirectory, model, IsolateConfig: false, SessionResumeMode.New);
        var env = new ClaudeCodeAdapter(_claudeLocator, new CommandAvailability()).BuildLaunchEnvironment(options);
        var result = await RunClaudeProbeAsync(claudeExe, env, projectPath, ProbeTimeoutSeconds, ct);
        return ToProbeResult("Claude Code", model, result, sw);
    }

    private async Task<ProbeResult> ProbeGroqAsync(string model, string? projectPath, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var apiKey = _credentials.GetCredentialPlainText(FallbackProvider.Groq);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Fail("Groq", model, sw, "No Groq credential stored.");
        }

        var claudeExe = await _claudeLocator.FindAsync();
        if (claudeExe is null)
        {
            return Fail("Groq", model, sw, "Claude Code executable not found.");
        }

        var proxy = new AnthropicCompatProxy(GroqAdapter.ApiBaseUrl, () => apiKey, forceModel: model);
        try
        {
            await proxy.StartAsync();
            var options = new SessionLaunchOptions(projectPath ?? Environment.CurrentDirectory, model, IsolateConfig: false, SessionResumeMode.New);
            var env = new GroqAdapter(_credentials, _claudeLocator).BuildLaunchEnvironment(options, proxy.BaseUrl);
            var result = await RunClaudeProbeAsync(claudeExe, env, projectPath, ProbeTimeoutSeconds, ct);
            return ToProbeResult("Groq", model, result, sw);
        }
        finally
        {
            await proxy.DisposeAsync();
        }
    }

    private async Task<ProbeResult> ProbeOpenCodeAsync(string model, string? projectPath, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var apiKey = _credentials.GetCredentialPlainText(FallbackProvider.OpenCode);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Fail("OpenCode", model, sw, "No OpenCode Go credential stored.");
        }

        var claudeExe = await _claudeLocator.FindAsync();
        if (claudeExe is null)
        {
            return Fail("OpenCode", model, sw, "Claude Code executable not found.");
        }

        var resolvedModel = string.IsNullOrWhiteSpace(model) ? OpenCodeModelCatalog.DefaultModel : model;
        var options = new SessionLaunchOptions(projectPath ?? Environment.CurrentDirectory, resolvedModel, IsolateConfig: false, SessionResumeMode.New);

        var proxy = new AnthropicCompatProxy(OpenCodeAdapter.ApiBaseUrl, () => apiKey, forceModel: resolvedModel, anthropicPassthrough: true);
        try
        {
            await proxy.StartAsync();
            var env = new OpenCodeAdapter(_credentials, _claudeLocator).BuildLaunchEnvironment(options, proxy.BaseUrl);
            var result = await RunClaudeProbeAsync(claudeExe, env, projectPath, ProbeTimeoutSeconds, ct);
            return ToProbeResult("OpenCode", resolvedModel, result, sw);
        }
        finally
        {
            await proxy.DisposeAsync();
        }
    }

    private async Task<ProbeResult> ProbeUnslothAsync(string model, string? projectPath, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var unsloth = LlamaCppLocator.Find();
        if (string.IsNullOrWhiteSpace(unsloth))
        {
            return Fail("Unsloth (local model)", model, sw, "unsloth CLI not found.");
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
            return Fail("Unsloth (local model)", model, sw, $"unsloth boot failed: {Redact(boot.Output)}");
        }

        var generated = LlamaCppAdapter.ParseGeneratedEnvironment(boot.Output);
        if (generated is null)
        {
            return Fail("Unsloth (local model)", model, sw, "Could not parse ANTHROPIC_BASE_URL from unsloth boot output.");
        }

        var claudeExe = await _claudeLocator.FindAsync();
        if (claudeExe is null)
        {
            return Fail("Unsloth (local model)", model, sw, "Claude Code executable not found.");
        }

        var env = new ClaudeLaunchEnvironmentBuilder()
            .WithAnthropicBaseUrl(generated.Value.BaseUrl.ToString())
            .WithAnthropicAuthToken(generated.Value.ApiKey)
            .Build();
        var result = await RunClaudeProbeAsync(claudeExe, env, projectPath, ProbeTimeoutSeconds, ct);
        return ToProbeResult("Unsloth (local model)", model, result, sw);
    }

    private async Task<ProbeResult> ProbeAntigravityAsync(string model, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var agy = _findAntigravity();
        if (agy is null)
        {
            return new ProbeResult(false, "Antigravity", model, "", (int)sw.ElapsedMilliseconds, null, Skipped: true, SkipReason: "Antigravity CLI (agy) not found.");
        }
        if (!_credentials.HasCredential(FallbackProvider.Antigravity))
        {
            return new ProbeResult(false, "Antigravity", model, "", (int)sw.ElapsedMilliseconds, null, Skipped: true, SkipReason: "User has not opted in to Antigravity.");
        }

        var version = await _runCommand(agy, "--version", null, 30, null, ct);
        if (!version.Success)
        {
            return Fail("Antigravity", model, sw, $"agy --version failed: {Redact(version.Output)}");
        }

        var help = await _runCommand(agy, "--help", null, 30, null, ct);
        if (!help.Success || !help.Output.Contains("-p", StringComparison.Ordinal) && !help.Output.Contains("--print", StringComparison.Ordinal))
        {
            return new ProbeResult(false, "Antigravity", model, "", (int)sw.ElapsedMilliseconds, null, Skipped: true, SkipReason: "agy has no non-interactive print flag; manual verify required.");
        }

        var result = await _runCommand(agy, $"-p \"{ProbePrompt}\" --model {model}", null, ProbeTimeoutSeconds, null, ct);
        return ToProbeResult("Antigravity", model, result, sw);
    }

    private async Task<CommandResult> RunClaudeProbeAsync(string claudeExe, ClaudeLaunchEnvironment env, string? projectPath, int timeoutSeconds, CancellationToken ct)
    {
        var args = $"-p \"{ProbePrompt}\"";
        if (!string.IsNullOrWhiteSpace(env.Arguments))
        {
            args = $"{env.Arguments} {args}";
        }
        return await _runCommand(claudeExe, args, projectPath, timeoutSeconds, env.Env, ct);
    }

    private ProbeResult ToProbeResult(string provider, string model, CommandResult result, Stopwatch sw)
    {
        if (result.TimedOut)
        {
            return Fail(provider, model, sw, "Probe timed out.");
        }
        if (!result.Success)
        {
            return Fail(provider, model, sw, Redact(result.Output));
        }
        var text = result.Output.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Fail(provider, model, sw, "Model returned empty response.");
        }
        return new ProbeResult(true, provider, model, text, (int)sw.ElapsedMilliseconds, null);
    }

    private static ProbeResult Fail(string provider, string model, Stopwatch sw, string error)
    {
        return new ProbeResult(false, provider, model, "", (int)sw.ElapsedMilliseconds, Redact(error));
    }

    internal static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var redacted = Regex.Replace(text, @"(ANTHROPIC_AUTH_TOKEN|ANTHROPIC_API_KEY)\s*[=:]\s*[^\s""]+", "$1=***", RegexOptions.IgnoreCase);
        return redacted;
    }
}
