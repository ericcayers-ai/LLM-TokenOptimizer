namespace TokenOptimizer.Core.Diagnostics;

/// <summary>
/// Checks the core toolchain the launcher relies on (git, node/npm, python,
/// pip, claude, graphify) and reports version strings when available,
/// mirroring Get-DependencySummary/Test-RequiredDependencies. Python is
/// checked via the same "actually execute it" verified locator every other
/// installer uses (Get-WorkingPythonExe) rather than a bare PATH resolve -
/// a Store execution-alias stub resolves fine but fails the instant
/// anything tries to run it.
/// </summary>
public sealed class DependencyChecker
{
    private static readonly (string Name, string Command, string VersionArgs)[] RequiredTools =
    [
        ("Git", "git", "--version"),
        ("Node.js", "node", "--version"),
        ("npm", "npm", "--version"),
        ("Claude Code", "claude", "--version"),
        ("Graphify", "graphify", "--version"),
    ];

    private readonly CommandAvailability _availability;
    private readonly PythonLocator _pythonLocator;

    public DependencyChecker(CommandAvailability availability)
        : this(availability, new PythonLocator(availability))
    {
    }

    public DependencyChecker(CommandAvailability availability, PythonLocator pythonLocator)
    {
        _availability = availability;
        _pythonLocator = pythonLocator;
    }

    public async Task<IReadOnlyList<DependencyStatus>> CheckAllAsync()
    {
        var results = new List<DependencyStatus>();
        foreach (var (name, command, versionArgs) in RequiredTools)
        {
            var resolved = _availability.ResolveOnPath(command);
            if (resolved is null)
            {
                results.Add(new DependencyStatus(name, false, null, null));
                continue;
            }

            var versionResult = await ExternalCommandRunner.RunAsync(command, versionArgs, timeoutSeconds: 10);
            var version = versionResult.Success ? versionResult.Output.Split('\n')[0].Trim() : null;
            results.Add(new DependencyStatus(name, true, resolved, version));
        }

        var pythonExe = await _pythonLocator.FindWorkingPythonAsync();
        if (pythonExe is null)
        {
            results.Add(new DependencyStatus("Python", false, null, null));
            results.Add(new DependencyStatus("pip", false, null, null));
        }
        else
        {
            var pyVersion = await ExternalCommandRunner.RunAsync(pythonExe, "--version", timeoutSeconds: 10);
            results.Add(new DependencyStatus("Python", true, pythonExe, pyVersion.Success ? pyVersion.Output.Trim() : null));

            var pipVersion = await ExternalCommandRunner.RunAsync(pythonExe, "-m pip --version", timeoutSeconds: 10);
            results.Add(new DependencyStatus("pip", pipVersion.Success, pythonExe, pipVersion.Success ? pipVersion.Output.Trim() : null));
        }

        return results;
    }
}
