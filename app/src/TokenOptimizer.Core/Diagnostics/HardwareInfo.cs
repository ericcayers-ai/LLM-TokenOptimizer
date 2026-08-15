namespace TokenOptimizer.Core.Diagnostics;

/// <summary>
/// Best-effort local hardware detection for sizing LM Studio's context-length
/// presets to what this specific machine can actually support - mirrors
/// run_benchmarks.py's own VRAM-via-nvidia-smi / system-RAM-fallback logic
/// (see filter_models_by_resources) so the app and the benchmark script never
/// disagree about what "fits this machine" means.
/// </summary>
public static class HardwareInfo
{
    /// <summary>Total VRAM of the first NVIDIA GPU in GB via nvidia-smi, or null if unavailable (no NVIDIA GPU, driver not installed, or nvidia-smi not on PATH).</summary>
    public static async Task<double?> GetVramGbAsync()
    {
        var nvidiaSmi = new CommandAvailability().ResolveOnPath("nvidia-smi");
        if (nvidiaSmi is null) return null;

        var result = await ExternalCommandRunner.RunAsync(
            nvidiaSmi, "--query-gpu=memory.total --format=csv,noheader,nounits", timeoutSeconds: 10);
        if (!result.Success) return null;

        var firstLine = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return double.TryParse(firstLine, out var mib) ? mib / 1024.0 : null;
    }

    /// <summary>Total physical system RAM in GB - the fallback inference pool when no VRAM was detected.</summary>
    public static double GetSystemRamGb() =>
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024.0 / 1024.0;

    /// <summary>VRAM if a GPU was detected, else system RAM - the pool a local model actually has to fit in.</summary>
    public static async Task<double> GetInferencePoolGbAsync() =>
        await GetVramGbAsync() ?? GetSystemRamGb();
}
