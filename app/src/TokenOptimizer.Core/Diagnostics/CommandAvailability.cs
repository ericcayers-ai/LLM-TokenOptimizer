using System.Collections.Concurrent;

namespace TokenOptimizer.Core.Diagnostics;

/// <summary>
/// Checks whether a command name resolves on PATH, and separately whether it
/// actually runs. On Windows a name resolving via PATH is frequently a Store
/// execution-alias stub or a stale venv leftover that fails the instant
/// something tries to run it - resolving is not the same as working.
/// </summary>
public sealed class CommandAvailability
{
    private readonly ConcurrentDictionary<string, bool> _cache = new(StringComparer.OrdinalIgnoreCase);

    public bool IsOnPath(string commandName, bool useCache = true)
    {
        if (useCache && _cache.TryGetValue(commandName, out var cached)) return cached;
        var found = ResolveOnPath(commandName) is not null;
        _cache[commandName] = found;
        return found;
    }

    public string? ResolveOnPath(string commandName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var pathExt = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        var hasExtension = Path.HasExtension(commandName);
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (hasExtension)
            {
                var candidate = Path.Combine(dir, commandName);
                if (File.Exists(candidate)) return candidate;
                continue;
            }

            foreach (var ext in pathExt)
            {
                var candidate = Path.Combine(dir, commandName + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    public async Task<bool> ExecutesAsync(string path, string arguments = "--version", int timeoutSeconds = 8)
    {
        try
        {
            var result = await ExternalCommandRunner.RunAsync(path, arguments, timeoutSeconds: timeoutSeconds);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }

    public void InvalidateCache(string commandName) => _cache.TryRemove(commandName, out _);
}
