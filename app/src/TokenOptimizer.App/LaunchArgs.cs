namespace TokenOptimizer.App;

/// <summary>
/// Set from Main() before the Avalonia lifetime starts, read by
/// MainViewModel's constructor. Lets an external launcher (the VS Code
/// extension's "Open TokenOptimizer App" command) hand off the current
/// workspace folder as the project to select on startup.
/// </summary>
public static class LaunchArgs
{
    public static string? InitialProjectPath { get; private set; }

    public static void Parse(string[] args)
    {
        if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
        {
            InitialProjectPath = args[0];
        }
    }

    /// <summary>Consumed once on first startup refresh so a later manual Refresh doesn't re-force the selection.</summary>
    public static void Consume() => InitialProjectPath = null;
}
