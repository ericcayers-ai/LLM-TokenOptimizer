using System.Text.Json;
using TokenOptimizer.Core.Models;

namespace TokenOptimizer.Core.Config;

/// <summary>
/// Persists AppConfig as JSON under %APPDATA%\TokenOptimizer\config.json,
/// replacing the PowerShell launcher's Save-Configuration/ConvertTo-Configuration
/// pair. Writes go through a temp file + move so a crash mid-write can never
/// leave a truncated/corrupt config on disk.
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _configPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ConfigStore(string? configDirectory = null)
    {
        var dir = configDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenOptimizer");
        Directory.CreateDirectory(dir);
        _configPath = Path.Combine(dir, "config.json");
    }

    public string ConfigPath => _configPath;

    public async Task<AppConfig> LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(_configPath)) return new AppConfig();
            await using var stream = File.OpenRead(_configPath);
            var config = await JsonSerializer.DeserializeAsync<AppConfig>(stream, JsonOptions);
            return config ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(AppConfig config)
    {
        await _lock.WaitAsync();
        try
        {
            await SaveLockedAsync(config);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Atomic read-modify-write: loads the current config, applies the
    /// mutation, and saves - all under one lock hold, so two concurrent
    /// callers (e.g. AddAsync from two nearly-simultaneous launches) can
    /// never race a separate Load...mutate...Save sequence and silently
    /// drop one caller's change. Mirrors Invoke-WithConfigLock.
    /// </summary>
    public async Task UpdateAsync(Action<AppConfig> mutate)
    {
        await _lock.WaitAsync();
        try
        {
            AppConfig config;
            try
            {
                if (!File.Exists(_configPath))
                {
                    config = new AppConfig();
                }
                else
                {
                    await using var stream = File.OpenRead(_configPath);
                    config = await JsonSerializer.DeserializeAsync<AppConfig>(stream, JsonOptions) ?? new AppConfig();
                }
            }
            catch
            {
                config = new AppConfig();
            }

            mutate(config);
            await SaveLockedAsync(config);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SaveLockedAsync(AppConfig config)
    {
        var tempPath = _configPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, config, JsonOptions);
        }
        File.Move(tempPath, _configPath, overwrite: true);
    }
}
