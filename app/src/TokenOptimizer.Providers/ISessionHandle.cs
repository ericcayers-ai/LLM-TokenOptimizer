namespace TokenOptimizer.Providers;

public interface ISessionHandle
{
    string ProviderName { get; }
    string ProjectPath { get; }
    int? ProcessId { get; }
    bool IsRunning { get; }
    DateTimeOffset StartedAt { get; }
}
