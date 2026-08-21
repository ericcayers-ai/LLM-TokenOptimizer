using System.Text.Json;
using TokenOptimizer.Core.Config;
using TokenOptimizer.Core.Diagnostics;

namespace TokenOptimizer.Providers.Claude;

public sealed record AgencyAgentInfo(string Division, string Slug, string Name, string Description);

public sealed class AgencyAgentsInstaller
{
    private const string RepoUrl = "https://github.com/msitarzewski/agency-agents.git";
    private const string ManifestFileName = ".agency-agents-synced.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ConfigStore _configStore;
    private readonly CommandAvailability _availability;

    /// <summary>Test-only path override. When set, overrides the default repo directory.</summary>
    internal string? RepoDirOverride { get; set; }

    /// <summary>Test-only path override. When set, overrides the default Claude agents directory.</summary>
    internal string? AgentsDirOverride { get; set; }

    public AgencyAgentsInstaller(ConfigStore configStore, CommandAvailability availability)
    {
        _configStore = configStore;
        _availability = availability;
    }

    private string GetRepoDir() =>
        RepoDirOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tokenoptimizer", "agency-agents");

    private string GetClaudeConfigDir() =>
        AgentsDirOverride ??
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    public async Task<bool> EnsureClonedAsync()
    {
        if (!_availability.IsOnPath("git", useCache: true)) return false;

        var repoDir = GetRepoDir();
        var gitDir = Path.Combine(repoDir, ".git");

        if (Directory.Exists(gitDir))
        {
            var pull = await ExternalCommandRunner.RunAsync(
                "git", "pull --ff-only", repoDir, timeoutSeconds: 30,
                extraEnvironment: new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" });
            if (pull.Success) return true;

            try { Directory.Delete(repoDir, recursive: true); } catch (IOException) { }
        }

        if (!Directory.Exists(repoDir))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(repoDir)!);
            var clone = await ExternalCommandRunner.RunAsync(
                "git", $"clone --quiet --depth 1 \"{RepoUrl}\" \"{repoDir}\"",
                timeoutSeconds: 60,
                extraEnvironment: new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" });
            if (!clone.Success && !Directory.Exists(repoDir)) return false;
        }

        var config = await _configStore.LoadAsync();
        config.AgencyAgentsCloned = true;
        await _configStore.SaveAsync(config);
        return true;
    }

    public async Task<IReadOnlyList<AgencyAgentInfo>> ListAvailableAgentsAsync()
    {
        var repoDir = GetRepoDir();
        if (!Directory.Exists(repoDir)) return [];

        var divisionsPath = Path.Combine(repoDir, "divisions.json");
        if (!File.Exists(divisionsPath)) return [];

        var agents = new List<AgencyAgentInfo>();

        try
        {
            var divisionsRaw = await File.ReadAllTextAsync(divisionsPath);
            using var doc = JsonDocument.Parse(divisionsRaw);

            foreach (var divProp in doc.RootElement.EnumerateObject())
            {
                var division = divProp.Name;
                if (divProp.Value.ValueKind != JsonValueKind.Array) continue;

                foreach (var entry in divProp.Value.EnumerateArray())
                {
                    var slug = entry.GetString();
                    if (string.IsNullOrWhiteSpace(slug)) continue;

                    var mdPath = Path.Combine(repoDir, division, $"{slug}.md");
                    if (!File.Exists(mdPath)) continue;

                    var (name, description) = ParseFrontmatter(mdPath);
                    agents.Add(new AgencyAgentInfo(division, slug, name ?? slug, description ?? string.Empty));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return [];
        }

        return agents;
    }

    public async Task<int> SyncTickedAgentsAsync(IReadOnlyList<string> tickedSlugs)
    {
        var repoDir = GetRepoDir();
        if (!Directory.Exists(repoDir)) return 0;

        var agents = await ListAvailableAgentsAsync();
        if (agents.Count == 0) return 0;

        var agentsDir = Path.Combine(GetClaudeConfigDir(), "agents");
        Directory.CreateDirectory(agentsDir);

        var manifestPath = Path.Combine(agentsDir, ManifestFileName);
        var previousManifest = await LoadManifestAsync(manifestPath);
        var nextManifest = new List<string>();
        var synced = 0;

        // UI stores keys as "division/slug"; extract bare slugs for matching
        var tickedSlugsSet = new HashSet<string>(
            tickedSlugs.Select(k => k.Contains('/') ? k[(k.LastIndexOf('/') + 1)..] : k),
            StringComparer.OrdinalIgnoreCase);

        foreach (var agent in agents)
        {
            var source = Path.Combine(repoDir, agent.Division, $"{agent.Slug}.md");
            var dest = Path.Combine(agentsDir, $"{agent.Slug}.md");

            if (tickedSlugsSet.Contains(agent.Slug))
            {
                try
                {
                    File.Copy(source, dest, overwrite: true);
                    nextManifest.Add(agent.Slug);
                    synced++;
                }
                catch (IOException) { /* best effort */ }
            }
            else if (previousManifest.Contains(agent.Slug))
            {
                try { File.Delete(dest); } catch (IOException) { }
            }
        }

        await SaveManifestAsync(manifestPath, nextManifest);
        return synced;
    }

    internal static (string? name, string? description) ParseFrontmatter(string mdPath)
    {
        var content = File.ReadAllText(mdPath);
        if (!content.StartsWith("---")) return (null, null);

        var secondDash = content.IndexOf("---", 3, StringComparison.Ordinal);
        if (secondDash < 0) return (null, null);

        var frontmatter = content.Substring(3, secondDash - 3);
        string? name = null;
        string? description = null;

        foreach (var line in frontmatter.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                name = trimmed.Substring(5).Trim().Trim('"', '\'');
            else if (trimmed.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                description = trimmed.Substring(12).Trim().Trim('"', '\'');
        }

        return (name, description);
    }

    private static async Task<List<string>> LoadManifestAsync(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath)) return [];
            var raw = await File.ReadAllTextAsync(manifestPath);
            return JsonSerializer.Deserialize<List<string>>(raw, JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return [];
        }
    }

    private static async Task SaveManifestAsync(string manifestPath, List<string> slugs)
    {
        try
        {
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(slugs, JsonOptions));
        }
        catch (IOException) { /* best effort */ }
    }
}
