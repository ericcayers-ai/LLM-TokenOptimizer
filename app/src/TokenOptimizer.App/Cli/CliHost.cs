using System.Text.Json;
using System.Text.Json.Serialization;
using TokenOptimizer.Core.Concurrency;
using TokenOptimizer.Core.Config;
using TokenOptimizer.Core.Diagnostics;
using TokenOptimizer.Core.Models;
using TokenOptimizer.Core.Projects;
using TokenOptimizer.Core.Security;
using TokenOptimizer.Providers;
using TokenOptimizer.Providers.Claude;
using TokenOptimizer.Providers.Diagnostics;
using TokenOptimizer.Providers.Fallback;
using TokenOptimizer.Providers.Rag;

namespace TokenOptimizer.App.Cli;

/// <summary>
/// Headless command surface for TokenOptimizer.App - `TokenOptimizer.App.exe
/// --cli &lt;command&gt; [options]`, one JSON object on stdout, exit 0/1. This is
/// the ONLY thing the VS Code extension talks to: every feature in the
/// Avalonia UI (MainViewModel) and this CLI wire the exact same Core/Providers
/// services, so the two front-ends can never drift into different behavior.
/// No Avalonia types here - this must run with no display/window server.
/// </summary>
public static class CliHost
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Records like ProjectCandidate/DependencyStatus serialize with their
        // C# PascalCase property names by default - force camelCase so every
        // JSON consumer of this CLI (the VS Code extension, any future one)
        // can rely on one consistent casing regardless of whether a given
        // field came from a hand-written anonymous object or a Core/Providers
        // record type.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<int> RunAsync(string[] args, ModelProbeService? probeService = null)
    {
        if (args.Length == 0)
        {
            return Fail("No command given. Try: status, providers, launch, install-dependencies, "
                + "install-companion-tooling, reset-config, uninstall, master-folder-set, master-folder-list, "
                + "create-project, history, add-project, "
                + "set-credential, opt-in, export-handoff, mcp-rag-server.");
        }

        var command = args[0];
        var opts = ParseOptions(args[1..]);

        if (command == "mcp-rag-server")
        {
            // Long-running MCP stdio server, not a one-shot JSON command - spawned
            // directly by Claude Code (via RagMcpRegistrar's `claude mcp add`), so
            // stdout is a raw JSON-RPC stream, not this file's {ok, data} envelope.
            if (!opts.TryGetValue("project", out var ragProject) || string.IsNullOrWhiteSpace(ragProject))
            {
                await Console.Error.WriteLineAsync("--project <path> is required.");
                return 1;
            }
            if (!opts.TryGetValue("embeddings-url", out var ragUrl) || !Uri.TryCreate(ragUrl, UriKind.Absolute, out var ragUri))
            {
                await Console.Error.WriteLineAsync("--embeddings-url <url> is required and must be an absolute URL.");
                return 1;
            }
            return await RagMcpStdioServer.RunAsync(ragProject, ragUri);
        }

        var configStore = new ConfigStore();
        var availability = new CommandAvailability();
        var pythonLocator = new PythonLocator(availability);
        var dependencyChecker = new DependencyChecker(availability, pythonLocator);
        var claudeLocator = new ClaudeExecutableLocator(configStore, availability);
        var claudeAdapter = new ClaudeCodeAdapter(claudeLocator, availability);
        var llamaCppAdapter = new TokenOptimizer.Providers.LlamaCpp.LlamaCppAdapter();
        var credentials = new ProxyCredentialStore();
        var antigravityAdapter = new AntigravityAdapter(credentials);
        var codexAdapter = new CodexAdapter(credentials);
        var cursorAdapter = new CursorAdapter(credentials);
        var groqAdapter = new GroqAdapter(credentials, claudeLocator);
        var deepSeekHarnessAdapter = new DeepSeekHarnessAdapter();
        var openCodeAdapter = new OpenCodeAdapter(credentials, claudeLocator);
        var rateLimits = new RateLimitTracker(configStore);
        var fallbackResolver = new FallbackChainResolver(
            claudeAdapter, antigravityAdapter, codexAdapter, cursorAdapter, groqAdapter, deepSeekHarnessAdapter, openCodeAdapter, llamaCppAdapter, rateLimits);
        var companionTooling = new CompanionToolingInstaller(configStore, claudeLocator, availability, pythonLocator);
        var projectHistory = new ProjectHistoryService(configStore);
        var masterFolderService = new MasterFolderService(configStore, projectHistory);
        var claudeMdService = new ProjectClaudeMdService();
        var uninstaller = new CompanionUninstaller(availability, configStore);
        var providerCliInstaller = new ProviderCliInstaller();
        var effectiveProbeService = probeService ?? new ModelProbeService(claudeLocator, credentials);

        var providers = new IProviderAdapter[]
        {
            claudeAdapter, antigravityAdapter, openCodeAdapter, llamaCppAdapter, codexAdapter, cursorAdapter, groqAdapter, deepSeekHarnessAdapter,
        };

        try
        {
            switch (command)
            {
                case "status":
                    return await Ok(new
                    {
                        dependencies = await dependencyChecker.CheckAllAsync(),
                        fallbackChain = await fallbackResolver.DescribeChainAsync(),
                        projectHistory = await projectHistory.GetHistoryAsync(),
                        masterFolder = await masterFolderService.GetMasterFolderAsync(),
                    });

                case "providers":
                {
                    var list = new List<object>();
                    foreach (var p in providers)
                    {
                        list.Add(new { name = p.Name, available = await p.IsAvailableAsync() });
                    }
                    return await Ok(new { providers = list, auto = "Auto (fallback chain)" });
                }

                case "launch":
                {
                    if (!opts.TryGetValue("project", out var project) || string.IsNullOrWhiteSpace(project))
                    {
                        return Fail("--project <path> is required.");
                    }
                    if (!ProjectHistoryService.IsValidProjectDirectory(project, out var pathError))
                    {
                        return Fail($"Invalid project path: {pathError}");
                    }

                    opts.TryGetValue("provider", out var providerName);
                    IProviderAdapter? provider;
                    if (string.IsNullOrWhiteSpace(providerName) || providerName == "auto")
                    {
                        provider = await fallbackResolver.ResolveAsync();
                        if (provider is null) return Fail("No backend in the fallback chain is currently available.");
                    }
                    else
                    {
                        provider = providers.FirstOrDefault(p => string.Equals(p.Name, providerName, StringComparison.OrdinalIgnoreCase));
                        if (provider is null) return Fail($"Unknown provider: {providerName}");
                        if (!await provider.IsAvailableAsync()) return Fail($"{provider.Name} is not available on this machine.");
                    }

                    if (provider == claudeAdapter || provider == groqAdapter)
                    {
                        // Same ~/.claude environment either way - keep it in sync
                        // before every launch, exactly like MainViewModel does,
                        // so switching providers between the app UI and this CLI
                        // (VS Code) is zero-friction: same skills, plugins, MCP
                        // tools, and claude-mem memory, every time.
                        await companionTooling.EnsureSharedClaudeEnvironmentAsync(project);

                        Uri? ragEmbeddingsUrl = opts.TryGetValue("rag-embeddings-url", out var ragUrlOpt) && Uri.TryCreate(ragUrlOpt, UriKind.Absolute, out var parsedRagUri)
                            ? parsedRagUri
                            : null;
                        await ProjectSessionPrep.PrepareProjectDirectiveAsync(project, claudeMdService, availability, provider: provider, ragEmbeddingsBaseUrl: ragEmbeddingsUrl);
                    }

                    opts.TryGetValue("model", out var model);

                    var resumeMode = opts.TryGetValue("resume", out var resumeStr) && Enum.TryParse<SessionResumeMode>(resumeStr, true, out var parsed)
                        ? parsed : SessionResumeMode.Continue;
                    var isolate = opts.ContainsKey("isolate");

                    var options = new SessionLaunchOptions(project, string.IsNullOrWhiteSpace(model) ? null : model, isolate, resumeMode);
                    var handle = await provider.LaunchSessionAsync(options);
                    await projectHistory.AddAsync(project);

                    return await Ok(new
                    {
                        provider = handle.ProviderName,
                        project = handle.ProjectPath,
                        processId = handle.ProcessId,
                    });
                }

                case "install-dependencies":
                {
                    var deps = await dependencyChecker.CheckAllAsync();
                    var missing = deps.Where(d => !d.IsAvailable).Select(d => d.Name).ToList();
                    if (missing.Count == 0) return await Ok(new { installed = Array.Empty<string>(), message = "Nothing missing." });

                    var wingetInstaller = new WingetInstaller(availability);
                    var installed = await wingetInstaller.InstallMissingAsync(missing, progress: null);
                    return await Ok(new { requested = missing, installed });
                }

                case "install-companion-tooling":
                {
                    var steps = new (string Name, Func<Task<bool>> Install)[]
                    {
                        ("Graphify", companionTooling.InstallGraphifyAsync),
                        ("claude-mem", companionTooling.InstallClaudeMemAsync),
                        ("headroom", companionTooling.InstallHeadroomStatuslineAsync),
                        ("rtk", companionTooling.InstallRtkCliAsync),
                        ("context7", companionTooling.RegisterContext7McpAsync),
                        ("context-mode", companionTooling.InstallContextModeMcpAsync),
                        ("caveman", companionTooling.InstallCavemanPluginAsync),
                        ("ponytail", companionTooling.InstallPonytailPluginAsync),
                        ("claude-md-management", companionTooling.InstallClaudeMdManagementPluginAsync),
                        ("impeccable", companionTooling.InstallImpeccableSkillAsync),
                        ("task-observer", companionTooling.InstallTaskObserverSkillAsync),
                        ("Codex CLI", providerCliInstaller.InstallCodexCliAsync),
                        ("Antigravity CLI", providerCliInstaller.InstallAntigravityCliAsync),
                        ("Cursor CLI", providerCliInstaller.InstallCursorCliAsync),
                        ("Antigravity plugin parity", async () => { await providerCliInstaller.SyncClaudePluginsIntoAntigravityAsync(); return true; }),
                        ("DeepSeek Harness (dsh)", providerCliInstaller.InstallDeepSeekHarnessCliAsync),
                        ("DeepSeek Harness plugin parity", async () => { await providerCliInstaller.SyncClaudePluginsIntoDeepSeekHarnessAsync(); return true; }),
                        ("ccusage (token/cost tracking)", providerCliInstaller.InstallCcusageAsync),
                    };

                    var results = new List<object>();
                    foreach (var (name, install) in steps)
                    {
                        results.Add(new { name, ok = await install() });
                    }

                    if (opts.TryGetValue("project", out var project) && !string.IsNullOrWhiteSpace(project))
                    {
                        var codeIntel = await companionTooling.InstallCodeIntelligencePluginAsync(project);
                        results.Add(new { name = "code intelligence", ok = codeIntel is not null, detail = codeIntel });
                    }

                    var active = await companionTooling.DescribeActiveCompressionAsync();
                    return await Ok(new { steps = results, activeCompression = active });
                }

                case "reset-config":
                {
                    if (File.Exists(configStore.ConfigPath)) File.Delete(configStore.ConfigPath);
                    return await Ok(new { reset = true });
                }

                case "uninstall":
                {
                    if (!opts.TryGetValue("confirm", out var confirm) || confirm != "UNINSTALL")
                    {
                        return Fail("Pass --confirm UNINSTALL (exact case) to proceed.");
                    }
                    var log = await uninstaller.UninstallAllAsync();
                    return await Ok(new { log });
                }

                case "master-folder-set":
                {
                    if (!opts.TryGetValue("path", out var path)) return Fail("--path <folder> is required.");
                    if (!MasterFolderService.IsValidMasterFolder(path, out var error))
                    {
                        return Fail($"Invalid master folder: {error}");
                    }
                    await masterFolderService.SetMasterFolderAsync(path);
                    return await Ok(new { masterFolder = path });
                }

                case "master-folder-list":
                {
                    if (!opts.TryGetValue("path", out var path))
                    {
                        path = await masterFolderService.GetMasterFolderAsync() ?? "";
                    }
                    if (!MasterFolderService.IsValidMasterFolder(path, out var error))
                    {
                        return Fail($"Invalid master folder: {error}");
                    }
                    var candidates = await masterFolderService.ListCandidatesAsync(path);
                    return await Ok(new { masterFolder = path, candidates });
                }

                case "create-project":
                {
                    if (!opts.TryGetValue("path", out var masterFolder)) return Fail("--path <master-folder> is required.");
                    if (!MasterFolderService.IsValidMasterFolder(masterFolder, out var error))
                    {
                        return Fail($"Invalid master folder: {error}");
                    }
                    if (!opts.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
                    {
                        return Fail("--name <folder-name> is required.");
                    }
                    var created = MasterFolderService.CreateProjectFolder(masterFolder, name);
                    return created is null ? Fail($"Could not create folder: {name}") : await Ok(new { created });
                }

                case "history":
                    return await Ok(new { history = await projectHistory.GetHistoryAsync() });

                case "add-project":
                {
                    if (!opts.TryGetValue("path", out var path)) return Fail("--path <project> is required.");
                    if (!ProjectHistoryService.IsValidProjectDirectory(path, out var error))
                    {
                        return Fail($"Invalid project path: {error}");
                    }
                    await projectHistory.AddAsync(path);
                    return await Ok(new { added = path });
                }

                case "set-credential":
                {
                    if (!opts.TryGetValue("provider", out var providerStr) || !Enum.TryParse<FallbackProvider>(providerStr, true, out var fbProvider))
                    {
                        return Fail("--provider <codex|groq> is required.");
                    }
                    if (!opts.TryGetValue("key", out var key) || string.IsNullOrWhiteSpace(key))
                    {
                        return Fail("--key <value> is required.");
                    }
                    credentials.SetCredential(fbProvider, key);
                    return await Ok(new { stored = fbProvider.ToString() });
                }

                case "opt-in":
                {
                    if (!opts.TryGetValue("provider", out var providerStr) || !Enum.TryParse<FallbackProvider>(providerStr, true, out var fbProvider)
                        || fbProvider is not (FallbackProvider.Antigravity or FallbackProvider.Cursor))
                    {
                        return Fail("--provider <antigravity|cursor> is required.");
                    }
                    credentials.SetCredential(fbProvider, "opted-in");
                    return await Ok(new { optedIn = fbProvider.ToString() });
                }

                case "export-handoff":
                {
                    if (!opts.TryGetValue("project", out var project)) return Fail("--project <path> is required.");
                    if (!ProjectHistoryService.IsValidProjectDirectory(project, out var error))
                    {
                        return Fail($"Invalid project path: {error}");
                    }
                    var isolate = opts.ContainsKey("isolate");
                    var claudeConfigDir = SessionHandoffExporter.GetEffectiveClaudeConfigDir(project, isolate);
                    var handoffFile = SessionHandoffExporter.Export(project, claudeConfigDir);
                    return await Ok(new { handoffFile });
                }

                case "test-model":
                {
                    if (!opts.TryGetValue("provider", out var providerName) || string.IsNullOrWhiteSpace(providerName))
                    {
                        return Fail("--provider <name> is required.");
                    }
                    if (!opts.TryGetValue("model", out var model) || string.IsNullOrWhiteSpace(model))
                    {
                        return Fail("--model <id> is required.");
                    }
                    opts.TryGetValue("project", out var project);
                    var result = await effectiveProbeService.ProbeAsync(providerName, model, project, CancellationToken.None);
                    if (result.Skipped)
                    {
                        return await Ok(new { result });
                    }
                    return result.Ok
                        ? await Ok(new { result })
                        : Fail(result.Error ?? "Probe failed.");
                }

                case "selftest":
                {
                    opts.TryGetValue("project", out var project);
                    var results = await effectiveProbeService.ProbeAllAsync(SelftestMatrix.Entries, CancellationToken.None);
                    var passed = results.Count(r => r.Ok);
                    var skipped = results.Count(r => r.Skipped);
                    var failed = results.Count(r => !r.Ok && !r.Skipped);
                    var allPassed = failed == 0;
                    var report = new
                    {
                        results,
                        summary = new { total = results.Count, passed, skipped, failed },
                    };
                    Console.WriteLine(JsonSerializer.Serialize(new { ok = allPassed, data = report }, JsonOptions));
                    return allPassed ? 0 : 1;
                }

                default:
                    return Fail($"Unknown command: {command}");
            }
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] rest)
    {
        var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rest.Length; i++)
        {
            if (!rest[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = rest[i][2..];
            var hasValue = i + 1 < rest.Length && !rest[i + 1].StartsWith("--", StringComparison.Ordinal);
            opts[key] = hasValue ? rest[++i] : "true";
        }
        return opts;
    }

    private static Task<int> Ok(object data)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { ok = true, data }, JsonOptions));
        return Task.FromResult(0);
    }

    private static int Fail(string error)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error }, JsonOptions));
        return 1;
    }
}
