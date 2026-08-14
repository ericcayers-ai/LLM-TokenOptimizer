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
            await ExternalCommandRunner.RunAsync("claude", "plugin uninstall claude-code-setup@claude-plugins-official --scope user", timeoutSeconds: 30);
            await ExternalCommandRunner.RunAsync("claude", "plugin uninstall claude-md-management@claude-plugins-official --scope user", timeoutSeconds: 30);
            await ExternalCommandRunner.RunAsync("claude", "plugin uninstall caveman@caveman --scope user", timeoutSeconds: 30);
            log.Add("Uninstalled official Claude Code plugins + Caveman");
        }

        var rtkDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rtk");
        if (Directory.Exists(rtkDir)) { TryDelete(rtkDir); log.Add("Removed RTK"); }
        var rtkHook = Path.Combine(claudeBase, "hooks", "rtk-rewrite.sh");
        if (File.Exists(rtkHook)) { TryDeleteFile(rtkHook); }

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
}
