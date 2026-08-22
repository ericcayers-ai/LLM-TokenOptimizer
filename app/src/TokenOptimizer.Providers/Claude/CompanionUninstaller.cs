using System.Text.Json;
using System.Text.Json.Nodes;
using TokenOptimizer.Core.Config;
using TokenOptimizer.Core.Diagnostics;

namespace TokenOptimizer.Providers.Claude;

/// <summary>
/// Complete targeted uninstall of everything this app (and its PowerShell
/// predecessor) installed - plugins, skills, MCP registrations, RTK, the
/// Claude CLI itself, claude-mem, and app data - while leaving base runtimes
/// (Node.js, Python, Git) intact. Ported from Invoke-CompleteUninstaller;
/// the original's "type rm then X to confirm" console gesture is replaced by
/// the caller requiring an explicit typed confirmation in the UI - the
/// deliberate friction is the point, not the specific mechanism.
/// </summary>
public sealed class CompanionUninstaller
{
    private readonly CommandAvailability _availability;
    private readonly ConfigStore _configStore;

    public CompanionUninstaller(CommandAvailability availability, ConfigStore configStore)
    {
        _availability = availability;
        _configStore = configStore;
    }

    public async Task<IReadOnlyList<string>> UninstallAllAsync()
    {
        var log = new List<string>();
        var claudeBase = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        var skillsDir = Path.Combine(claudeBase, "skills");
        var pluginsDir = Path.Combine(claudeBase, "plugins");

        if (_availability.IsOnPath("claude", useCache: true))
        {
            await ExternalCommandRunner.RunAsync("claude", "mcp remove omniroute --scope user", timeoutSeconds: 15);
            await ExternalCommandRunner.RunAsync("claude", "mcp remove context7 --scope user", timeoutSeconds: 15);
            await ExternalCommandRunner.RunAsync("claude", "plugin uninstall claude-code-setup@claude-plugins-official --scope user", timeoutSeconds: 30);
            await ExternalCommandRunner.RunAsync("claude", "plugin uninstall claude-md-management@claude-plugins-official --scope user", timeoutSeconds: 30);
            await ExternalCommandRunner.RunAsync("claude", "plugin uninstall caveman@caveman --scope user", timeoutSeconds: 30);
            await ExternalCommandRunner.RunAsync("claude", "plugin uninstall ponytail@ponytail --scope user", timeoutSeconds: 30);
            await ExternalCommandRunner.RunAsync("claude", "plugin uninstall context-mode@context-mode --scope user", timeoutSeconds: 30);
            await ExternalCommandRunner.RunAsync("claude", "plugin uninstall claude-mem@thedotmack --scope user", timeoutSeconds: 30);
            log.Add("Uninstalled official Claude Code plugins + Caveman, Ponytail, context-mode, claude-mem");
        }

        // RTK's PreToolUse hook is a direct command entry inside settings.json
        // (not a separate wrapper script) - remove that entry before deleting
        // the binary, or a broken hook reference survives "uninstall everything".
        var globalSettingsPath = Path.Combine(claudeBase, "settings.json");
        if (RemoveHookCommandsContaining(globalSettingsPath, "rtk.exe hook claude", "rtk hook claude"))
        {
            log.Add("Removed RTK hook entry from settings.json");
        }

        var rtkDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rtk");
        if (Directory.Exists(rtkDir)) { TryDelete(rtkDir); log.Add("Removed RTK"); }
        var rtkHook = Path.Combine(claudeBase, "hooks", "rtk-rewrite.sh");
        if (File.Exists(rtkHook)) { TryDeleteFile(rtkHook); }

        // headroom: its statusline entry and PostToolUse hook both live inside
        // settings.json alongside unrelated hooks (limitping, etc.) - remove
        // just headroom's pieces, then its two loose script files.
        if (RemoveHookCommandsContaining(globalSettingsPath, "context-counter.py"))
        {
            log.Add("Removed headroom hook entry from settings.json");
        }
        if (RemoveStatuslineIfMatches(globalSettingsPath, "statusline.sh"))
        {
            log.Add("Removed headroom statusline entry from settings.json");
        }
        var headroomStatusline = Path.Combine(claudeBase, "statusline.sh");
        if (File.Exists(headroomStatusline)) { TryDeleteFile(headroomStatusline); }
        var headroomHook = Path.Combine(claudeBase, "hooks", "context-counter.py");
        if (File.Exists(headroomHook)) { TryDeleteFile(headroomHook); log.Add("Removed headroom"); }

        // claude-mem runs a background worker process holding its own data
        // directory open - stop it first so the delete below isn't silently
        // partial (a running worker can hold file locks Directory.Delete
        // can't override).
        TryStopClaudeMemWorker();

        foreach (var path in new[]
                 {
                     Path.Combine(pluginsDir, "cache", "superpowers"),
                     Path.Combine(pluginsDir, "marketplaces", "thedotmack", "claude-mem"),
                 })
        {
            if (Directory.Exists(path)) { TryDelete(path); log.Add($"Removed plugin path: {path}"); }
        }

        var installedJsonPath = Path.Combine(pluginsDir, "installed_plugins.json");
        if (File.Exists(installedJsonPath))
        {
            try
            {
                var json = JsonNode.Parse(await File.ReadAllTextAsync(installedJsonPath));
                var plugins = json?["plugins"]?.AsObject();
                if (plugins is not null)
                {
                    foreach (var key in new[] { "superpowers", "last30days", "frontend-design" })
                    {
                        plugins.Remove(key);
                    }
                    await File.WriteAllTextAsync(installedJsonPath, json!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    log.Add("Cleaned script plugin entries from installed_plugins.json");
                }
            }
            catch (JsonException) { /* best effort */ }
        }

        foreach (var skillName in new[] { "last30days", "frontend-design", "bencium-controlled-ux-designer", "graphify", "impeccable", "task-observer" })
        {
            var skillPath = Path.Combine(skillsDir, skillName);
            if (Directory.Exists(skillPath)) { TryDelete(skillPath); log.Add($"Removed skill: {skillName}"); }
        }

        if (_availability.IsOnPath("npm", useCache: true))
        {
            await ExternalCommandRunner.RunAsync(
                "npm", "uninstall -g @anthropic-ai/claude-code omniroute claude-mem autoskills", timeoutSeconds: 120);
            log.Add("Uninstalled global npm packages (Claude CLI, claude-mem, autoskills)");
        }

        if (_availability.IsOnPath("pip", useCache: true))
        {
            await ExternalCommandRunner.RunAsync("pip", "uninstall -y graphifyy", timeoutSeconds: 60);
            log.Add("Uninstalled Graphify (pip)");
        }

        var claudeMemConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude-mem");
        if (Directory.Exists(claudeMemConfigDir)) { TryDelete(claudeMemConfigDir); log.Add("Removed ~/.claude-mem"); }

        var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenOptimizer");
        if (Directory.Exists(appDataDir)) { TryDelete(appDataDir); log.Add("Removed app data (config, credentials, isolated profiles)"); }

        log.Add("Targeted uninstallation complete - base runtimes (Node.js, Python, Git) left intact.");
        return log;
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch (IOException) { }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }

    /// <summary>Removes any hook entries whose command contains one of the given substrings from every hook event/matcher group in settings.json. Returns true if anything was removed.</summary>
    private static bool RemoveHookCommandsContaining(string settingsPath, params string[] commandSubstrings)
    {
        if (!File.Exists(settingsPath)) return false;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
            var hooks = root?["hooks"] as JsonObject;
            if (hooks is null) return false;

            var removedAny = false;
            foreach (var eventKey in hooks.Select(kv => kv.Key).ToList())
            {
                if (hooks[eventKey] is not JsonArray groups) continue;
                for (var i = groups.Count - 1; i >= 0; i--)
                {
                    if (groups[i] is not JsonObject group || group["hooks"] is not JsonArray entries) continue;
                    for (var j = entries.Count - 1; j >= 0; j--)
                    {
                        var command = entries[j]?["command"]?.GetValue<string>() ?? string.Empty;
                        if (!commandSubstrings.Any(s => command.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
                        entries.RemoveAt(j);
                        removedAny = true;
                    }
                    if (entries.Count == 0) groups.RemoveAt(i);
                }
                if (groups.Count == 0) hooks.Remove(eventKey);
            }

            if (!removedAny) return false;
            File.WriteAllText(settingsPath, root!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    /// <summary>Removes the top-level "statusline" key from settings.json if its command references the given script name.</summary>
    private static bool RemoveStatuslineIfMatches(string settingsPath, string scriptNameSubstring)
    {
        if (!File.Exists(settingsPath)) return false;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
            var command = root?["statusline"]?["command"]?.GetValue<string>() ?? string.Empty;
            if (!command.Contains(scriptNameSubstring, StringComparison.OrdinalIgnoreCase)) return false;

            root!.Remove("statusline");
            File.WriteAllText(settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    /// <summary>Best-effort stop of the claude-mem background worker so its data directory isn't locked when deleted below.</summary>
    private static void TryStopClaudeMemWorker()
    {
        var pidFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude-mem", "worker.pid");
        if (!File.Exists(pidFile)) return;
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(pidFile));
            var pid = json?["pid"]?.GetValue<int>();
            if (pid is int p)
            {
                using var process = System.Diagnostics.Process.GetProcessById(p);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { /* best effort - already stopped, or pid stale */ }
    }
}
