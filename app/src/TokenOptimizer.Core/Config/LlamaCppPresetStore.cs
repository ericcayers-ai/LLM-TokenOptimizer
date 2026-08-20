using System.Text.Json;
using TokenOptimizer.Core.Models;

namespace TokenOptimizer.Core.Config;

/// <summary>
/// Per-model+quant saved launch settings (plan §5d) - llama-server is
/// flag-driven only with no persistent config store of its own, this is
/// TokenOptimizer's per-model preset store for Unsloth-served local models. Same
/// atomic-write shape as ConfigStore, kept as its own file rather than
/// folded into AppConfig since preset count grows with how many
/// model+quant combinations a user tries, unlike AppConfig's fixed fields.
/// </summary>
public sealed class LlamaCppPresetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public LlamaCppPresetStore(string? configDirectory = null)
    {
        var dir = configDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenOptimizer");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "llamacpp-presets.json");
    }

    private static string Key(string repoId, string quant) => $"{repoId}:{quant}".ToLowerInvariant();

    public async Task<LlamaCppLaunchOptions?> GetAsync(string repoId, string quant)
    {
        var all = await LoadAllAsync();
        return all.GetValueOrDefault(Key(repoId, quant));
    }

    public async Task SaveAsync(string repoId, string quant, LlamaCppLaunchOptions options)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await LoadAllLockedAsync();
            all[Key(repoId, quant)] = options;
            await SaveAllLockedAsync(all);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Dictionary<string, LlamaCppLaunchOptions>> LoadAllAsync()
    {
        await _lock.WaitAsync();
        try { return await LoadAllLockedAsync(); }
        finally { _lock.Release(); }
    }

    private async Task<Dictionary<string, LlamaCppLaunchOptions>> LoadAllLockedAsync()
    {
        if (!File.Exists(_path)) return new Dictionary<string, LlamaCppLaunchOptions>();
        try
        {
            await using var stream = File.OpenRead(_path);
            var data = await JsonSerializer.DeserializeAsync<Dictionary<string, LlamaCppLaunchOptions>>(stream, JsonOptions);
            return data ?? new Dictionary<string, LlamaCppLaunchOptions>();
        }
        catch
        {
            return new Dictionary<string, LlamaCppLaunchOptions>();
        }
    }

    private async Task SaveAllLockedAsync(Dictionary<string, LlamaCppLaunchOptions> all)
    {
        var tempPath = _path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, all, JsonOptions);
        }
        File.Move(tempPath, _path, overwrite: true);
    }
}
