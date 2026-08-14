namespace TokenOptimizer.Core.Diagnostics;

public sealed class CommandResult
{
    public bool Success { get; init; }
    public int ExitCode { get; init; } = -1;
    public string Output { get; init; } = string.Empty;
    public bool TimedOut { get; init; }
}
