using TokenOptimizer.Core.Config;
using TokenOptimizer.Core.Diagnostics;

namespace TokenOptimizer.Providers.Claude;

/// <summary>
/// Locates claude.exe: standard installer bin dirs first, then PATH
/// (skipping the buggy global npm wrapper), then falls back to nothing -
/// the caller decides whether to trigger the official installer or ask the
/// user. Ported from Find-ClaudeExecutable.
/// </summary>
public sealed class ClaudeExecutableLocator
{
    private readonly ConfigStore _configStore;
    private readonly CommandAvailability _availability;

    public ClaudeExecutableLocator(ConfigStore configStore, CommandAvailability availability)
    {
        _configStore = configStore;
        _availability = availability;
    }

    public async Task<string?> FindAsync()
    {
        var config = await _configStore.LoadAsync();
        if (!string.IsNullOrWhiteSpace(config.ClaudePath) && File.Exists(config.ClaudePath))
        {
            return config.ClaudePath;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var installerBinDirs = new[]
        {
            Path.Combine(userProfile, ".local", "bin"),
            Path.Combine(userProfile, ".claude", "bin"),
            Path.Combine(localAppData, "Programs", "claude"),
        };

        foreach (var dir in installerBinDirs)
        {
            var candidate = Path.Combine(dir, "claude.exe");
            if (File.Exists(candidate))
            {
                await PersistPathAsync(candidate);
                return candidate;
            }
        }

        var onPath = _availability.ResolveOnPath("claude");
        if (onPath is not null && !onPath.Contains(Path.Combine("AppData", "Roaming", "npm", "claude"), StringComparison.OrdinalIgnoreCase))
        {
            await PersistPathAsync(onPath);
            return onPath;
        }

        return null;
    }

    private async Task PersistPathAsync(string path)
    {
        var config = await _configStore.LoadAsync();
        config.ClaudePath = path;
        await _configStore.SaveAsync(config);
    }
}
