namespace TokenOptimizer.Core.Concurrency;

/// <summary>
/// Named-mutex lock scoped to a project path, preventing two sessions from
/// racing dependency installs/config writes for the same project at once.
/// Opening the same project twice deliberately still succeeds elsewhere
/// (an independent second session is allowed) - this only guards the
/// setup/config phase, not the whole session lifetime.
/// </summary>
public sealed class InstanceLock : IDisposable
{
    private readonly Mutex _mutex;
    private bool _acquired;

    private InstanceLock(Mutex mutex)
    {
        _mutex = mutex;
    }

    public static InstanceLock? TryAcquire(string projectPath, TimeSpan? timeout = null)
    {
        var name = "Local\\TokenOptimizer_" + Math.Abs(projectPath.ToLowerInvariant().GetHashCode());
        var mutex = new Mutex(initiallyOwned: false, name, out _);
        var acquired = mutex.WaitOne(timeout ?? TimeSpan.FromSeconds(2));
        if (!acquired)
        {
            mutex.Dispose();
            return null;
        }

        return new InstanceLock(mutex) { _acquired = true };
    }

    public void Dispose()
    {
        if (_acquired)
        {
            try { _mutex.ReleaseMutex(); } catch { /* already released */ }
        }
        _mutex.Dispose();
    }
}
