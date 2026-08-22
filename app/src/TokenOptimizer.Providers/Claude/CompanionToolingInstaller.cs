using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TokenOptimizer.Core.Config;
using TokenOptimizer.Core.Diagnostics;

namespace TokenOptimizer.Providers.Claude;

/// <summary>
/// Ports LLM-TokenOptimizer.ps1's "COMPANION TOOLING" and "AGENT SKILLS
/// ECOSYSTEM" sections: a set of independently best-effort installers for
/// Claude Code add-ons, all user-scope, all idempotent, all "warn and move
/// on" on failure rather than blocking a launch. Every install writes its
/// own sticky-true flag to AppConfig, matching the original script's
/// "install-time flags are trusted forever" design (see TestCompressionMethodsActiveAsync
/// for the read-only re-verification pass that runs right before launch).
/// </summary>
public sealed class CompanionToolingInstaller
{
    /// <summary>
    /// claude-mem's worker is ONE process shared by every Claude Code session
    /// pointed at the default data dir/port - including the standalone Claude
    /// Code Desktop app, which this C# app has no connection to and no way to
    /// coordinate with. Confirmed live: opening a session from this app while
    /// the Desktop app has one open causes the Desktop app's next prompt to
    /// get "hook blocked your prompt". Port alone isn't enough to fix this -
    /// checked worker-service.cjs: its single-instance spawn.lock lives under
    /// the data dir, NOT namespaced by port, so a second worker on a
    /// different port would still see "another launcher holds the spawn
    /// lock" and refuse to start. CLAUDE_MEM_DATA_DIR (a real, supported
    /// override the worker reads via its Ui() state-dir resolver) is what
    /// actually isolates the lock file, settings, and state - the port
    /// override then just avoids the two workers' actual TCP listeners
    /// colliding. Every session this app launches sets both, so it gets its
    /// own separate worker - never touching, never blocking, the Desktop
    /// app's. Sessions launched by THIS app still share ONE worker with each
    /// other (same shared-across-concurrent-windows design as before, just
    /// pointed at a different home).
    /// </summary>
    public const int IsolatedWorkerPort = 37778;

    public static readonly string IsolatedDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude-mem-tokenoptimizer");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly ConfigStore _configStore;
    private readonly ClaudeExecutableLocator _claudeLocator;
    private readonly CommandAvailability _availability;
    private readonly PythonLocator _pythonLocator;
    private readonly AgencyAgentsInstaller _agencyAgents;

    public CompanionToolingInstaller(
        ConfigStore configStore,
        ClaudeExecutableLocator claudeLocator,
        CommandAvailability availability,
        PythonLocator pythonLocator,
        AgencyAgentsInstaller agencyAgents)
    {
        _configStore = configStore;
        _claudeLocator = claudeLocator;
        _availability = availability;
        _pythonLocator = pythonLocator;
        _agencyAgents = agencyAgents;
    }

    // ------------------------------------------------------------------
    // GRAPHIFY
    // ------------------------------------------------------------------

    public async Task<bool> InstallGraphifyAsync()
    {
        if (_availability.IsOnPath("graphify", useCache: true)) return true;

        var pythonExe = await _pythonLocator.FindWorkingPythonAsync();
        if (pythonExe is null) return false;

        var result = await ExternalCommandRunner.RunAsync(pythonExe, "-m pip install --upgrade graphifyy", timeoutSeconds: 180);
        if (!result.Success && (result.Output.Contains("Permission denied") || result.Output.Contains("Access is denied") || result.Output.Contains("WinError 5")))
        {
            result = await ExternalCommandRunner.RunAsync(pythonExe, "-m pip install --upgrade --user graphifyy", timeoutSeconds: 180);
        }

        if (!result.Success) return false;

        _availability.InvalidateCache("graphify");
        return _availability.IsOnPath("graphify", useCache: false);
    }

    public async Task<string?> TestGraphifyVersionAsync()
    {
        if (!_availability.IsOnPath("graphify", useCache: true)) return null;
        var result = await ExternalCommandRunner.RunAsync("graphify", "--version", timeoutSeconds: 10);
        if (!result.Success) return null;

        var version = result.Output.Trim();
        var config = await _configStore.LoadAsync();
        config.LastGraphifyVersion = version;
        await _configStore.SaveAsync(config);
        return version;
    }

    /// <summary>Graphify ships via pip, outside winget's reach - its own lightweight best-effort update step.</summary>
    public async Task UpdateGraphifyIfNeededAsync()
    {
        if (!_availability.IsOnPath("graphify", useCache: true)) return;
        if (!_availability.IsOnPath("pip", useCache: true)) return;
        await ExternalCommandRunner.RunAsync("pip", "install --upgrade graphifyy", timeoutSeconds: 120);
    }

    // ------------------------------------------------------------------
    // CLAUDE-MEM
    // ------------------------------------------------------------------

    public async Task<bool> InstallClaudeMemAsync()
    {
        var config = await _configStore.LoadAsync();
        if (config.ClaudeMemInstalled) return true;
        if (!_availability.IsOnPath("npm", useCache: true)) return false;

        var cmemDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude-mem");
        var cmemSettings = Path.Combine(cmemDir, "settings.json");
        if (!File.Exists(cmemSettings))
        {
            try
            {
                Directory.CreateDirectory(cmemDir);
                var defaultConfig = new
                {
                    runtime = "worker",
                    provider = "claude-agent-sdk",
                    authMethod = "subscription",
                    model = "claude-haiku-4-5-20251001",
                    onboardingComplete = true,
                    skipEmail = true,
                };
                await File.WriteAllTextAsync(cmemSettings, JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (IOException) { /* best effort pre-seed */ }
        }

        var result = await ExternalCommandRunner.RunAsync(
            "cmd.exe", "/c \"echo. | npx -y claude-mem@latest install --ide claude-code\"",
            timeoutSeconds: 45,
            extraEnvironment: new Dictionary<string, string> { ["CI"] = "true", ["NON_INTERACTIVE"] = "1" });

        var pluginPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "plugins", "marketplaces", "thedotmack", "claude-mem");
        var pluginHasContent = Directory.Exists(pluginPath) && Directory.EnumerateFiles(pluginPath, "*", SearchOption.AllDirectories).Any();

        if (result.Success || pluginHasContent || File.Exists(cmemSettings))
        {
            config.ClaudeMemInstalled = true;
            await _configStore.SaveAsync(config);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Self-heal for github.com/thedotmack/claude-mem#2926 (open upstream):
    /// claude-mem's background worker can die without releasing its listener
    /// on port 37777 (CLAUDE_MEM_WORKER_PORT). The next session's worker then
    /// fails to bind, and because claude-mem's UserPromptSubmit hook fails
    /// CLOSED when the worker is unreachable, every prompt gets blocked while
    /// ~/.claude-mem/state/hook-failures.json's consecutiveFailures counter
    /// climbs without bound ("hook blocked your prompt"). Ported 1:1 from
    /// LLM-TokenOptimizer.ps1's Repair-ClaudeMemWorker - runs right before
    /// every launch (see EnsureSharedClaudeEnvironmentAsync), entirely
    /// best-effort. Multi-session safe: the worker is one process shared by
    /// every concurrently-open Claude Code window, so this runs under a
    /// machine-wide mutex and only reclaims the port if its owning process is
    /// orphaned (no longer enumerable) - a live worker actually in use by
    /// another window is left alone.
    /// </summary>
    public async Task RepairClaudeMemWorkerAsync()
    {
        var config = await _configStore.LoadAsync();
        if (!config.ClaudeMemInstalled) return;

        using var repairMutex = new Mutex(false, "Global\\LLMTokenOptimizer_ClaudeMemRepair");
        bool haveMutex;
        try { haveMutex = repairMutex.WaitOne(TimeSpan.FromSeconds(3)); }
        catch (AbandonedMutexException) { haveMutex = true; }
        if (!haveMutex) return;

        try
        {
            var port = IsolatedWorkerPort;

            // 1. Reclaim the port only if its owning process is orphaned.
            try
            {
                var netstat = await ExternalCommandRunner.RunAsync("netstat", "-ano -p TCP", timeoutSeconds: 10);
                if (netstat.Success)
                {
                    foreach (var line in netstat.Output.Split('\n'))
                    {
                        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 5 || !parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!parts[1].EndsWith($":{port}", StringComparison.Ordinal)) continue;
                        if (!parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!int.TryParse(parts[4], out var ownerPid)) continue;

                        try { Process.GetProcessById(ownerPid); }
                        catch (ArgumentException)
                        {
                            try { Process.GetProcessById(ownerPid).Kill(); } catch { /* already gone */ }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { /* best effort */ }

            // 2. Clear a stuck failure counter, only if the worker is demonstrably unreachable.
            try
            {
                var failFile = Path.Combine(IsolatedDataDir, "state", "hook-failures.json");
                if (File.Exists(failFile))
                {
                    var raw = await File.ReadAllTextAsync(failFile);
                    using var doc = JsonDocument.Parse(raw);
                    var count = doc.RootElement.TryGetProperty("consecutiveFailures", out var countProp) ? countProp.GetInt32() : 0;
                    if (count >= 10 && !await IsClaudeMemWorkerHealthyAsync(port))
                    {
                        File.Delete(failFile);
                        var supervisorFile = Path.Combine(IsolatedDataDir, "supervisor.json");
                        if (File.Exists(supervisorFile)) File.Delete(supervisorFile);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException) { /* malformed state file - not fatal */ }
        }
        finally
        {
            try { if (haveMutex) repairMutex.ReleaseMutex(); } catch { /* already released */ }
        }
    }

    /// <summary>Plain TCP-connect liveness probe - deliberately doesn't assume any HTTP path on claude-mem's bundled worker.</summary>
    private static async Task<bool> IsClaudeMemWorkerHealthyAsync(int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var connectTask = client.ConnectAsync("127.0.0.1", port);
            var completed = await Task.WhenAny(connectTask, Task.Delay(750));
            return completed == connectTask && client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Stops claude-mem's isolated worker (see IsolatedDataDir) when the app
    /// window closes, but ONLY if no other `claude` process launched by this
    /// app is still running - since every session this app launches shares
    /// that one isolated worker, killing it while another of this app's own
    /// sessions is still using it would interrupt that session's memory
    /// capture. Nothing in claude-mem's own hooks ever stops the worker (its
    /// SessionStart hook only ever runs "start", never "stop"), so left
    /// alone it runs forever after the last window closes - this is the
    /// missing other half of RepairClaudeMemWorkerAsync. Same machine-wide
    /// mutex, so the two never race each other. Because this worker is
    /// isolated to its own data dir/port, this never touches (and never
    /// needs to check for) the standalone Claude Code Desktop app's own
    /// separate worker.
    /// </summary>
    public async Task StopClaudeMemWorkerIfLastWindowAsync()
    {
        var config = await _configStore.LoadAsync();
        if (!config.ClaudeMemInstalled) return;

        if (Process.GetProcessesByName("claude").Length > 0) return;

        var lockFile = Path.Combine(IsolatedDataDir, "spawn.lock");
        if (!File.Exists(lockFile)) return;

        using var repairMutex = new Mutex(false, "Global\\LLMTokenOptimizer_ClaudeMemRepair");
        bool haveMutex;
        try { haveMutex = repairMutex.WaitOne(TimeSpan.FromSeconds(3)); }
        catch (AbandonedMutexException) { haveMutex = true; }
        if (!haveMutex) return;

        try
        {
            var raw = await File.ReadAllTextAsync(lockFile);
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("pid", out var pidProp)) return;
            var pid = pidProp.GetInt32();

            // Re-check under the mutex - another window may have started since the check above.
            if (Process.GetProcessesByName("claude").Length > 0) return;

            try
            {
                var proc = Process.GetProcessById(pid);
                if (!proc.ProcessName.Contains("bun", StringComparison.OrdinalIgnoreCase)) return;
                proc.Kill();
                File.Delete(lockFile);
            }
            catch (ArgumentException) { /* already gone */ }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { /* malformed lock file - not fatal */ }
        finally
        {
            try { if (haveMutex) repairMutex.ReleaseMutex(); } catch { /* already released */ }
        }
    }

    // ------------------------------------------------------------------
    // HEADROOM STATUSLINE
    // ------------------------------------------------------------------

    private static string GetClaudeConfigDir() =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    public async Task<bool> TestHeadroomWorkingAsync()
    {
        var statuslinePath = Path.Combine(GetClaudeConfigDir(), "statusline.sh");
        if (!File.Exists(statuslinePath)) return false;

        var bash = GitBashLocator.Find();
        if (bash is null) return false;

        var posixPath = statuslinePath.Replace('\\', '/');
        var result = await ExternalCommandRunner.RunAsync(bash, $"-lc \"bash '{posixPath}'\"", timeoutSeconds: 10);
        return result.Success;
    }

    /// <summary>
    /// headroom's upstream installer hardcodes bare `python3`, which on
    /// Windows routinely resolves to a broken Store execution-alias stub.
    /// Rewrites the generated statusline.sh and settings.json hook command to
    /// call an absolute, execution-verified interpreter instead.
    /// </summary>
    public async Task<bool> RepairHeadroomPython3RefsAsync(string pythonExe)
    {
        var configDir = GetClaudeConfigDir();
        var forwardSlashPython = pythonExe.Replace('\\', '/');
        var changed = false;
        var utf8NoBom = new UTF8Encoding(false);

        var statuslinePath = Path.Combine(configDir, "statusline.sh");
        if (File.Exists(statuslinePath))
        {
            try
            {
                var content = await File.ReadAllTextAsync(statuslinePath);
                if (!content.Contains("PYTHON3="))
                {
                    var newContent = System.Text.RegularExpressions.Regex.Replace(
                        content, @"\bpython3\b", "\"$PYTHON3\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    newContent = System.Text.RegularExpressions.Regex.Replace(
                        newContent, @"(?m)^#!/bin/bash", $"#!/bin/bash\nPYTHON3=\"{forwardSlashPython}\"");
                    await File.WriteAllTextAsync(statuslinePath, newContent, utf8NoBom);
                    changed = true;
                }
            }
            catch (IOException) { /* best effort */ }
        }

        var settingsPath = Path.Combine(configDir, "settings.json");
        if (File.Exists(settingsPath))
        {
            try
            {
                var raw = await File.ReadAllTextAsync(settingsPath);
                if (System.Text.RegularExpressions.Regex.IsMatch(raw, "\"command\":\\s*\"python3 "))
                {
                    var newRaw = System.Text.RegularExpressions.Regex.Replace(
                        raw, "\"command\":\\s*\"python3 ", $"\"command\": \"\\\"{forwardSlashPython}\\\" ");
                    await File.WriteAllTextAsync(settingsPath, newRaw, utf8NoBom);
                    changed = true;
                }
            }
            catch (IOException) { /* best effort */ }
        }

        return changed;
    }

    public async Task<bool> InstallHeadroomStatuslineAsync()
    {
        var config = await _configStore.LoadAsync();
        if (config.HeadroomInstalled && await TestHeadroomWorkingAsync()) return true;

        var pythonExe = await _pythonLocator.FindWorkingPythonAsync();
        if (pythonExe is null) return false;

        var bash = GitBashLocator.Find();
        if (bash is null) return false;

        var shimDir = Path.Combine(Path.GetTempPath(), "tokoptimizer-python3-shim");
        Directory.CreateDirectory(shimDir);
        var shimPath = Path.Combine(shimDir, "python3");
        var forwardSlashPython = pythonExe.Replace('\\', '/');
        await File.WriteAllTextAsync(shimPath, $"#!/bin/sh\nexec \"{forwardSlashPython}\" \"$@\"\n", new UTF8Encoding(false));

        var shimPathPosix = "/" + shimDir.Replace('\\', '/').Replace(":", "");
        const string installerUrl = "https://raw.githubusercontent.com/henchmarketing-rgb/headroom/main/install.sh";
        var bashCmd = $"chmod +x '{shimPathPosix}/python3' 2>/dev/null; PATH=\"{shimPathPosix}:$PATH\" bash -c \"curl -fsSL {installerUrl} | bash\"";

        var result = await ExternalCommandRunner.RunAsync(bash, $"-lc \"{bashCmd}\"", timeoutSeconds: 60);
        try { Directory.Delete(shimDir, recursive: true); } catch (IOException) { }

        if (!result.Success) return false;

        await RepairHeadroomPython3RefsAsync(pythonExe);

        var working = await TestHeadroomWorkingAsync();
        config = await _configStore.LoadAsync();
        config.HeadroomInstalled = working;
        await _configStore.SaveAsync(config);
        return working;
    }

    // ------------------------------------------------------------------
    // CLAUDE PLUGINS & SKILLS ARCHITECTURE
    //   Sets up ~/.claude/plugins (cache/data/marketplaces), registers
    //   Superpowers + last30days + frontend-design in installed_plugins.json,
    //   clones the Superpowers framework, and cleans up legacy placeholder
    //   skill stubs. Idempotent and cheap after the first run - re-checked
    //   on every launch so the shared ~/.claude environment (read by every
    //   Claude-binary-based provider: Claude Code direct AND Unsloth-local,
    //   since both launch the identical `claude` executable against the
    //   identical config dir) never drifts out of sync between them.
    // ------------------------------------------------------------------

    private static readonly string[] LegacyPlaceholderSkillStubs =
        ["last30days", "frontend-design", "bencium-controlled-ux-designer"];

    public async Task InstallClaudePluginsAndSkillsAsync()
    {
        var config = await _configStore.LoadAsync();
        if (config.ClaudePluginsAndSkillsInstalled) return;

        var claudeBase = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        var pluginsDir = Path.Combine(claudeBase, "plugins");
        var skillsDir = Path.Combine(claudeBase, "skills");

        foreach (var folder in new[] { "cache", "data", "marketplaces" })
        {
            Directory.CreateDirectory(Path.Combine(pluginsDir, folder));
        }

        var installedJsonPath = Path.Combine(pluginsDir, "installed_plugins.json");
        var registry = new
        {
            version = 1,
            plugins = new Dictionary<string, object>
            {
                ["superpowers"] = new { scope = "user", enabled = true, source = "https://github.com/obra/superpowers.git" },
                ["last30days"] = new { scope = "user", enabled = true, source = "local" },
                ["frontend-design"] = new { scope = "user", enabled = true, source = "local" },
            },
        };
        await File.WriteAllTextAsync(installedJsonPath, JsonSerializer.Serialize(registry, new JsonSerializerOptions { WriteIndented = true }));

        var superpowersPath = Path.Combine(pluginsDir, "cache", "superpowers");
        if (!Directory.Exists(superpowersPath) && _availability.IsOnPath("git", useCache: true))
        {
            await ExternalCommandRunner.RunAsync(
                "git", $"clone --quiet \"https://github.com/obra/superpowers.git\" \"{superpowersPath}\"",
                timeoutSeconds: 60, extraEnvironment: new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" });
        }

        if (Directory.Exists(skillsDir))
        {
            foreach (var legacyStub in LegacyPlaceholderSkillStubs)
            {
                var stubFile = Path.Combine(skillsDir, legacyStub, "SKILL.md");
                if (!File.Exists(stubFile)) continue;
                try
                {
                    var content = await File.ReadAllTextAsync(stubFile);
                    if (content.Contains("Active and ready for tool execution."))
                    {
                        Directory.Delete(Path.Combine(skillsDir, legacyStub), recursive: true);
                    }
                }
                catch (IOException) { /* best effort */ }
            }
        }

        var legacyFolder = Path.Combine(skillsDir, "claude-skills-final");
        if (Directory.Exists(legacyFolder))
        {
            try { Directory.Delete(legacyFolder, recursive: true); } catch (IOException) { }
        }

        config.ClaudePluginsAndSkillsInstalled = true;
        await _configStore.SaveAsync(config);
    }

    /// <summary>
    /// Everything that needs to be true of the shared ~/.claude environment
    /// before ANY Claude-binary-based session launches, regardless of which
    /// model backend that session talks to - this is what makes switching
    /// between Claude Code (Anthropic) and Claude Code (local Unsloth
    /// model) carry the exact same skills/plugins/MCP tools/memory with zero
    /// manual sync step. Every call here is cheap once installed (sticky
    /// flags/existence checks short-circuit), so it's safe to run before
    /// every single launch rather than requiring a separate setup click.
    /// </summary>
    public async Task EnsureSharedClaudeEnvironmentAsync(string? projectDirectory)
    {
        await InstallClaudePluginsAndSkillsAsync();
        await RepairClaudeMemWorkerAsync();
        if (projectDirectory is not null)
        {
            await InstallCodeIntelligencePluginAsync(projectDirectory);
        }

        await _agencyAgents.EnsureClonedAsync();
        var config = await _configStore.LoadAsync();
        await _agencyAgents.SyncTickedAgentsAsync(config.TickedAgencyAgents ?? new List<string>());
    }

    // ------------------------------------------------------------------
    // PLUGINS (via marketplace)
    // ------------------------------------------------------------------

    private async Task<bool> InstallMarketplacePluginAsync(string marketplaceLocator, string pluginId, string marketplaceName, string scope = "user")
    {
        var exe = await _claudeLocator.FindAsync();
        if (exe is null) return false;

        await ExternalCommandRunner.RunAsync(exe, $"plugin marketplace add {marketplaceLocator}", timeoutSeconds: 30);
        var installResult = await ExternalCommandRunner.RunAsync(exe, $"plugin install {pluginId}@{marketplaceName} --scope {scope}", timeoutSeconds: 60);
        var reportedSuccess = installResult.Success || installResult.Output.Contains("already installed", StringComparison.OrdinalIgnoreCase);

        var listResult = await ExternalCommandRunner.RunAsync(exe, "plugin list", timeoutSeconds: 15);
        if (!listResult.Success) return reportedSuccess;
        return listResult.Output.Contains(pluginId, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> InstallCavemanPluginAsync()
    {
        var config = await _configStore.LoadAsync();
        if (config.CavemanPluginInstalled) return true;
        if (!_availability.IsOnPath("claude", useCache: true)) return false;

        var installed = await InstallMarketplacePluginAsync("JuliusBrussee/caveman", "caveman", "caveman");
        if (installed)
        {
            config.CavemanPluginInstalled = true;
            await _configStore.SaveAsync(config);
        }
        return installed;
    }

    public async Task<bool> InstallPonytailPluginAsync()
    {
        var config = await _configStore.LoadAsync();
        if (config.PonytailPluginInstalled) return true;
        if (!_availability.IsOnPath("claude", useCache: true)) return false;

        var installed = await InstallMarketplacePluginAsync("DietrichGebert/ponytail", "ponytail", "ponytail");
        if (installed)
        {
            config.PonytailPluginInstalled = true;
            await _configStore.SaveAsync(config);
        }
        return installed;
    }

    public async Task<bool> InstallClaudeMdManagementPluginAsync()
    {
        var config = await _configStore.LoadAsync();
        if (config.ClaudeMdManagementPluginInstalled) return true;
        if (!_availability.IsOnPath("claude", useCache: true)) return false;

        var installed = await InstallMarketplacePluginAsync("anthropics/claude-plugins-official", "claude-md-management", "claude-plugins-official");
        if (installed)
        {
            config.ClaudeMdManagementPluginInstalled = true;
            await _configStore.SaveAsync(config);
        }
        return installed;
    }

    public async Task<bool> InstallContextModeMcpAsync()
    {
        var config = await _configStore.LoadAsync();
        if (config.ContextModeMcpInstalled) return true;
        if (!_availability.IsOnPath("claude", useCache: true)) return false;

        var installed = await InstallMarketplacePluginAsync("mksglu/context-mode", "context-mode", "context-mode");
        if (installed)
        {
            config.ContextModeMcpInstalled = true;
            await _configStore.SaveAsync(config);
        }
        return installed;
    }

    /// <summary>Anthropic's own guidance: install a code-intelligence plugin for the project's dominant typed language, only if that language server binary is already on PATH.</summary>
    private static readonly IReadOnlyDictionary<string, (string Plugin, string Binary)> CodeIntelligencePluginMap = new Dictionary<string, (string, string)>
    {
        [".ts"] = ("typescript-lsp", "typescript-language-server"),
        [".tsx"] = ("typescript-lsp", "typescript-language-server"),
        [".js"] = ("typescript-lsp", "typescript-language-server"),
        [".jsx"] = ("typescript-lsp", "typescript-language-server"),
        [".py"] = ("pyright-lsp", "pyright-langserver"),
        [".go"] = ("gopls-lsp", "gopls"),
        [".rs"] = ("rust-analyzer-lsp", "rust-analyzer"),
        [".java"] = ("jdtls-lsp", "jdtls"),
        [".cs"] = ("csharp-lsp", "csharp-ls"),
        [".cpp"] = ("clangd-lsp", "clangd"),
        [".cc"] = ("clangd-lsp", "clangd"),
        [".c"] = ("clangd-lsp", "clangd"),
        [".h"] = ("clangd-lsp", "clangd"),
        [".hpp"] = ("clangd-lsp", "clangd"),
        [".kt"] = ("kotlin-lsp", "kotlin-language-server"),
        [".lua"] = ("lua-lsp", "lua-language-server"),
        [".php"] = ("php-lsp", "intelephense"),
        [".swift"] = ("swift-lsp", "sourcekit-lsp"),
    };

    private static readonly string[] CodeIntelligenceExcludeDirs =
        ["node_modules", ".git", ".graphify", "graphify-out", "dist", "build", "out", "bin", "obj", "__pycache__", ".venv", "venv", ".next", "target"];

    public static string? GetProjectDominantLanguage(string projectDirectory)
    {
        try
        {
            var counts = new Dictionary<string, int>();
            foreach (var file in Directory.EnumerateFiles(projectDirectory, "*", SearchOption.AllDirectories))
            {
                if (CodeIntelligenceExcludeDirs.Any(dir => file.Contains($"{Path.DirectorySeparatorChar}{dir}{Path.DirectorySeparatorChar}"))) continue;
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (!CodeIntelligencePluginMap.ContainsKey(ext)) continue;
                counts[ext] = counts.GetValueOrDefault(ext) + 1;
            }
            return counts.Count == 0 ? null : counts.OrderByDescending(kv => kv.Value).First().Key;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task<string?> InstallCodeIntelligencePluginAsync(string projectDirectory)
    {
        var ext = GetProjectDominantLanguage(projectDirectory);
        if (ext is null || !CodeIntelligencePluginMap.TryGetValue(ext, out var info)) return null;
        if (!_availability.IsOnPath(info.Binary, useCache: true)) return null;

        var installed = await InstallMarketplacePluginAsync("anthropics/claude-plugins-official", info.Plugin, "claude-plugins-official");
        return installed ? info.Plugin : null;
    }

    // ------------------------------------------------------------------
    // IMPECCABLE SKILL (https://github.com/pbakaus/impeccable)
    // ------------------------------------------------------------------

    /// <summary>
    /// Impeccable ships as a Claude Code plugin whose marketplace.json lives
    /// inside its own repo (github.com/pbakaus/impeccable), not on a hosted
    /// marketplace slug - so "install" means clone it locally, then register
    /// that local clone's directory itself as a marketplace (via the same
    /// InstallMarketplacePluginAsync every other companion plugin uses) and
    /// install from it. The previous implementation only cloned the repo and
    /// checked for a SKILL.md file on disk - it never actually ran `claude
    /// plugin marketplace add`/`plugin install`, so the plugin was never
    /// registered with Claude Code at all. That's the fix here.
    /// </summary>
    public async Task<bool> InstallImpeccableSkillAsync()
    {
        var config = await _configStore.LoadAsync();
        var skillDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills", "impeccable");
        if (config.ImpeccableSkillInstalled) return true;

        if (!_availability.IsOnPath("git", useCache: true)) return false;

        if (Directory.Exists(skillDir))
        {
            // Re-clone cleanly rather than trying to reconcile a partial/stale checkout.
            var pull = await ExternalCommandRunner.RunAsync("git", "pull --quiet --ff-only", skillDir, timeoutSeconds: 30);
            if (!pull.Success)
            {
                try { Directory.Delete(skillDir, recursive: true); } catch (IOException) { }
            }
        }

        if (!Directory.Exists(skillDir))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(skillDir)!);
            var clone = await ExternalCommandRunner.RunAsync(
                "git", $"clone --quiet --depth 1 \"https://github.com/pbakaus/impeccable.git\" \"{skillDir}\"",
                timeoutSeconds: 60, extraEnvironment: new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" });
            if (!clone.Success && !Directory.Exists(skillDir)) return false;
        }

        if (!File.Exists(Path.Combine(skillDir, ".claude-plugin", "marketplace.json"))) return false;

        var installed = await InstallMarketplacePluginAsync(skillDir, "impeccable", "impeccable");
        if (installed)
        {
            config.ImpeccableSkillInstalled = true;
            await _configStore.SaveAsync(config);
        }
        return installed;
    }

    // ------------------------------------------------------------------
    // TASK-OBSERVER SKILL
    // ------------------------------------------------------------------

    public async Task<bool> InstallTaskObserverSkillAsync()
    {
        var config = await _configStore.LoadAsync();
        if (config.TaskObserverSkillInstalled) return true;

        var skillDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills", "task-observer");
        var skillFile = Path.Combine(skillDir, "SKILL.md");
        try
        {
            Directory.CreateDirectory(skillDir);
            const string sourceUrl = "https://raw.githubusercontent.com/iamneilroberts/claude-skills/main/skills/task-observer/SKILL.md";
            var content = await Http.GetStringAsync(sourceUrl);
            await File.WriteAllTextAsync(skillFile, content);
            config.TaskObserverSkillInstalled = true;
            await _configStore.SaveAsync(config);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // RTK CLI
    // ------------------------------------------------------------------

    public async Task<bool> InstallRtkCliAsync()
    {
        var config = await _configStore.LoadAsync();
        if (config.RtkCliInstalled) return true;

        var rtkDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rtk");
        var rtkExe = Path.Combine(rtkDir, "rtk.exe");

        if (!File.Exists(rtkExe))
        {
            var zipPath = Path.Combine(Path.GetTempPath(), $"rtk-windows-{Environment.ProcessId}.zip");
            try
            {
                Directory.CreateDirectory(rtkDir);
                var bytes = await Http.GetByteArrayAsync("https://github.com/rtk-ai/rtk/releases/latest/download/rtk-x86_64-pc-windows-msvc.zip");
                await File.WriteAllBytesAsync(zipPath, bytes);
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, rtkDir, overwriteFiles: true);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                return false;
            }
            finally
            {
                try { File.Delete(zipPath); } catch (IOException) { }
            }
        }

        if (!File.Exists(rtkExe)) return false;

        _availability.InvalidateCache("rtk");

        var bash = GitBashLocator.Find();
        if (bash is null) return false;

        if (!_availability.IsOnPath("jq", useCache: true))
        {
            // No winget package for jq confirmed missing here is handled by
            // the caller's dependency-install pass; RTK hook registration
            // below will simply fail cleanly if jq still isn't present.
        }

        var bashRtkDir = "/" + rtkDir.Replace('\\', '/').Replace(":", "");
        var result = await ExternalCommandRunner.RunAsync(
            bash, $"-lc \"export PATH='{bashRtkDir}':$PATH; rtk init -g\"", timeoutSeconds: 30);

        if (!result.Success) return false;

        config.RtkCliInstalled = true;
        await _configStore.SaveAsync(config);
        return true;
    }

    // ------------------------------------------------------------------
    // CONTEXT7 MCP
    // ------------------------------------------------------------------

    public async Task<bool> RegisterContext7McpAsync()
    {
        var config = await _configStore.LoadAsync();
        if (config.Context7McpInstalled) return true;
        if (!_availability.IsOnPath("claude", useCache: true) || !_availability.IsOnPath("npx", useCache: true)) return false;

        var exe = await _claudeLocator.FindAsync();
        if (exe is null) return false;

        var result = await ExternalCommandRunner.RunAsync(exe, "mcp add --scope user context7 -- npx -y @upstash/context7-mcp", timeoutSeconds: 30);
        if (!result.Success && !result.Output.Contains("already exists", StringComparison.OrdinalIgnoreCase) &&
            !result.Output.Contains("already added", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        config.Context7McpInstalled = true;
        await _configStore.SaveAsync(config);
        return true;
    }

    // ------------------------------------------------------------------
    // ANTIGRAVITY PROXY SCAFFOLDING (clone + npm install only - never signs in)
    // ------------------------------------------------------------------

    public async Task<bool> InstallAntigravityProxySupportAsync()
    {
        var config = await _configStore.LoadAsync();
        if (config.AntigravityProxyInstalled) return true;
        if (!_availability.IsOnPath("git", useCache: true) || !_availability.IsOnPath("npm", useCache: true)) return false;

        var toolsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tokenoptimizer", "antigravity-proxy");

        if (!Directory.Exists(toolsDir))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(toolsDir)!);
            var cloneResult = await ExternalCommandRunner.RunAsync(
                "git", $"clone --quiet \"https://github.com/frieser/antigravity-proxy.git\" \"{toolsDir}\"",
                timeoutSeconds: 60, extraEnvironment: new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" });
            if (!cloneResult.Success && !Directory.Exists(toolsDir)) return false;
        }

        if (!Directory.Exists(toolsDir)) return false;

        var envExample = Path.Combine(toolsDir, ".env.example");
        var envFile = Path.Combine(toolsDir, ".env");
        if (File.Exists(envExample) && !File.Exists(envFile))
        {
            try { File.Copy(envExample, envFile); } catch (IOException) { }
        }

        var npmResult = await ExternalCommandRunner.RunAsync("npm", "install", toolsDir, timeoutSeconds: 180);
        if (!npmResult.Success) return false;

        config.AntigravityProxyInstalled = true;
        await _configStore.SaveAsync(config);
        return true;
    }

    // ------------------------------------------------------------------
    // COMPRESSION STATUS CHECK (read-only, runs right before launch)
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<string>> DescribeActiveCompressionAsync()
    {
        // Live re-verification pass: reports the ACTUAL on-disk/CLI state of
        // every compression/companion tool, deliberately NOT gated on the
        // sticky install flags (a stale flag would otherwise hide a real
        // install, or claim one that was removed - the flags drift when these
        // tools are installed/removed outside this app's own flow). Runs
        // `claude plugin list`/`claude mcp list` once each and checks every
        // tool against that single output rather than shelling out per tool.
        var lines = new List<string>();
        var exe = await _claudeLocator.FindAsync();

        string? pluginListOutput = null;
        if (exe is not null)
        {
            var pluginList = await ExternalCommandRunner.RunAsync(exe, "plugin list", timeoutSeconds: 15);
            if (pluginList.Success) pluginListOutput = pluginList.Output;
        }

        lines.Add($"caveman {(pluginListOutput is not null && pluginListOutput.Contains("caveman", StringComparison.OrdinalIgnoreCase) ? "[OK]" : "[MISSING]")}");
        lines.Add($"context-mode {(pluginListOutput is not null && pluginListOutput.Contains("context-mode", StringComparison.OrdinalIgnoreCase) ? "[OK]" : "[MISSING]")}");
        lines.Add($"ponytail {(pluginListOutput is not null && pluginListOutput.Contains("ponytail", StringComparison.OrdinalIgnoreCase) ? "[OK]" : "[MISSING]")}");

        var rtkHook = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "hooks", "rtk-rewrite.sh");
        var rtkExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rtk", "rtk.exe");
        lines.Add($"rtk {(File.Exists(rtkHook) || File.Exists(rtkExe) ? "[OK]" : "[MISSING]")}");

        if (exe is not null)
        {
            var mcpList = await ExternalCommandRunner.RunAsync(exe, "mcp list", timeoutSeconds: 15);
            var context7Active = mcpList.Success && System.Text.RegularExpressions.Regex.IsMatch(mcpList.Output, "context7.*Connected");
            lines.Add($"context7 {(context7Active ? "[OK]" : "[MISSING]")}");
        }

        return lines;
    }
}
