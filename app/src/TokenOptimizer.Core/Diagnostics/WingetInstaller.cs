namespace TokenOptimizer.Core.Diagnostics;

/// <summary>Reports one step of a multi-step best-effort install pass, for live progress UI.</summary>
public readonly record struct InstallStepProgress(string Name, int StepNumber, int TotalSteps, string Status);

/// <summary>
/// winget-based dependency install/update, ported from Install-ViaWinget /
/// Update-ViaWinget / Test-WingetAvailable / Update-AllDependencies /
/// Install-MissingDependencies. Exit codes are checked numerically first
/// (locale-independent) with an English-text match as fallback only, same
/// as the original - a non-English Windows install would otherwise report
/// spurious failures on "already installed"/"access denied" outcomes.
/// </summary>
public sealed class WingetInstaller
{
    private const int AlreadyInstalledExitCode = -1978335189; // 0x8A150061
    private const int AccessDeniedExitCode = -2147024891;      // 0x80070005 E_ACCESSDENIED

    private static readonly IReadOnlyDictionary<string, (string WingetId, string FriendlyName)> InstallMap = new Dictionary<string, (string, string)>
    {
        ["Git"] = ("Git.Git", "Git"),
        ["Node.js"] = ("OpenJS.NodeJS.LTS", "Node.js LTS"),
        // npm ships bundled with Node.js - if npm is missing but node itself
        // is present, the Node install is broken/incomplete; reinstalling
        // Node.js repairs it.
        ["npm"] = ("OpenJS.NodeJS.LTS", "Node.js LTS (repairs npm)"),
        ["Python"] = ("Python.Python.3.12", "Python 3.12"),
    };

    private readonly CommandAvailability _availability;
    private bool? _wingetAvailable;

    public WingetInstaller(CommandAvailability availability)
    {
        _availability = availability;
    }

    public async Task<bool> IsWingetAvailableAsync()
    {
        if (_wingetAvailable is { } cached) return cached;

        if (!_availability.IsOnPath("winget"))
        {
            _wingetAvailable = false;
            return false;
        }

        var probe = await ExternalCommandRunner.RunAsync("winget", "--version", timeoutSeconds: 10);
        _wingetAvailable = probe.Success;
        return probe.Success;
    }

    public async Task<bool> InstallAsync(string wingetId, string friendlyName, int timeoutSeconds = 300)
    {
        if (!await IsWingetAvailableAsync()) return false;

        var baseArgs = $"install --id {wingetId} -e --source winget --accept-package-agreements --accept-source-agreements --silent --disable-interactivity";
        var result = await ExternalCommandRunner.RunAsync("winget", baseArgs, timeoutSeconds: timeoutSeconds);

        if (IsSuccessOrAlreadyInstalled(result)) return true;

        // Machine-scope installs commonly fail silently on a non-admin
        // account (no UAC prompt possible in --disable-interactivity mode).
        // Retry per-user scope, which most packages support without elevation.
        if (result.ExitCode == AccessDeniedExitCode ||
            ContainsAny(result.Output, "requires administrator", "elevat", "access is denied", "0x80070005"))
        {
            var userResult = await ExternalCommandRunner.RunAsync("winget", $"{baseArgs} --scope user", timeoutSeconds: timeoutSeconds);
            if (IsSuccessOrAlreadyInstalled(userResult)) return true;
        }

        return false;
    }

    public async Task UpdateAsync(string wingetId, string friendlyName)
    {
        if (!await IsWingetAvailableAsync()) return;

        var args = $"upgrade --id {wingetId} -e --source winget --accept-package-agreements --accept-source-agreements --silent --disable-interactivity";
        await ExternalCommandRunner.RunAsync("winget", args, timeoutSeconds: 180);
    }

    /// <summary>Installs whichever missing dependencies winget can handle, deduped by package id (Node.js/npm collapse to one reinstall).</summary>
    public async Task<IReadOnlyList<string>> InstallMissingAsync(
        IEnumerable<string> missingDependencyNames, IProgress<InstallStepProgress>? progress = null)
    {
        var toInstall = missingDependencyNames.Where(InstallMap.ContainsKey).ToList();
        if (toInstall.Count == 0) return Array.Empty<string>();

        if (!await IsWingetAvailableAsync())
        {
            progress?.Report(new InstallStepProgress("winget", 0, toInstall.Count, "winget is not available"));
            return Array.Empty<string>();
        }

        var installed = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var step = 0;
        var dedupedNames = toInstall.Where(name => seenIds.Add(InstallMap[name].WingetId)).ToList();
        foreach (var name in dedupedNames)
        {
            step++;
            var (wingetId, friendlyName) = InstallMap[name];
            progress?.Report(new InstallStepProgress(friendlyName, step, dedupedNames.Count, "installing..."));
            var ok = await InstallAsync(wingetId, friendlyName);
            if (ok) installed.Add(friendlyName);
            progress?.Report(new InstallStepProgress(friendlyName, step, dedupedNames.Count, ok ? "done" : "failed"));
        }

        return installed;
    }

    /// <summary>Best-effort version-check pass across the toolchain, mirroring Update-AllDependencies.</summary>
    public async Task UpdateAllAsync()
    {
        if (await IsWingetAvailableAsync())
        {
            if (_availability.IsOnPath("git")) await UpdateAsync("Git.Git", "Git");
            if (_availability.IsOnPath("node")) await UpdateAsync("OpenJS.NodeJS.LTS", "Node.js");
            if (_availability.IsOnPath("python")) await UpdateAsync("Python.Python.3.12", "Python");
        }

        if (_availability.IsOnPath("npm"))
        {
            await ExternalCommandRunner.RunAsync("npm", "install -g npm@latest", timeoutSeconds: 60);
        }

        if (_availability.IsOnPath("npm") && _availability.IsOnPath("claude"))
        {
            await ExternalCommandRunner.RunAsync("npm", "update -g @anthropic-ai/claude-code", timeoutSeconds: 120);
        }

        if (_availability.IsOnPath("autoskills"))
        {
            await ExternalCommandRunner.RunAsync("npm", "update -g autoskills", timeoutSeconds: 60);
        }
    }

    private static bool IsSuccessOrAlreadyInstalled(CommandResult result) =>
        result.Success || result.ExitCode == AlreadyInstalledExitCode ||
        ContainsAny(result.Output, "already installed", "No available upgrade");

    private static bool ContainsAny(string haystack, params string[] needles) =>
        needles.Any(n => haystack.Contains(n, StringComparison.OrdinalIgnoreCase));
}
