namespace TokenOptimizer.Core.Diagnostics;

/// <summary>
/// Finds a Python interpreter that actually runs, not just one that resolves
/// on PATH. The `py` launcher (registered by python.org installers, not
/// Store aliases) is the most reliable resolver on Windows; bare "python"/
/// "python3" and known install locations are fallbacks.
/// </summary>
public sealed class PythonLocator
{
    private readonly CommandAvailability _availability;
    private string? _cachedExe;
    private bool _resolved;

    public PythonLocator(CommandAvailability availability)
    {
        _availability = availability;
    }

    public async Task<string?> FindWorkingPythonAsync()
    {
        if (_resolved) return _cachedExe;

        var candidates = new List<string>();

        if (_availability.IsOnPath("py"))
        {
            var pyResult = await ExternalCommandRunner.RunAsync("py", "-3 -c \"import sys; print(sys.executable)\"", timeoutSeconds: 8);
            if (pyResult.Success && !string.IsNullOrWhiteSpace(pyResult.Output))
            {
                candidates.Add(pyResult.Output.Trim());
            }
        }

        foreach (var name in new[] { "python", "python3" })
        {
            var resolved = _availability.ResolveOnPath(name);
            if (resolved is not null) candidates.Add(resolved);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var root in new[]
                 {
                     Path.Combine(localAppData, "Programs", "Python"),
                     programFiles,
                 })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root, "Python3*")
                         .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var exe = Path.Combine(dir, "python.exe");
                if (File.Exists(exe)) candidates.Add(exe);
            }
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (await _availability.ExecutesAsync(candidate))
            {
                _cachedExe = candidate;
                _resolved = true;
                return candidate;
            }
        }

        _resolved = true;
        _cachedExe = null;
        return null;
    }
}
