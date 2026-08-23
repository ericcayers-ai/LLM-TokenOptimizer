using System.Collections.Concurrent;

namespace TokenOptimizer.Sandbox;

public sealed class FakeSandboxRuntime : ISandboxRuntime
{
    private int _counter;
    private readonly ConcurrentDictionary<string, SandboxSpec> _sandboxes = new();
    private readonly ConcurrentDictionary<string, bool> _dead = new();
    private readonly ConcurrentDictionary<string, Queue<ExecEvent>> _scripts = new();
    private readonly ConcurrentDictionary<string, string> _files = new(StringComparer.Ordinal);

    public async Task<SandboxHandle> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        await Task.CompletedTask;
        var id = $"sbx-{Interlocked.Increment(ref _counter):D6}";
        _sandboxes[id] = spec;
        return new SandboxHandle(id);
    }

    public async IAsyncEnumerable<ExecEvent> ExecAsync(
        string id, IReadOnlyList<string> argv, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureAlive(id);
        await Task.CompletedTask;
        if (!_scripts.TryRemove(id, out var queue))
        {
            yield return new ExecExit(0);
            yield break;
        }
        while (queue.Count > 0)
            yield return queue.Dequeue();
    }

    public async Task<string> ReadFileAsync(string id, string path, CancellationToken ct = default)
    {
        EnsureAlive(id);
        await Task.CompletedTask;
        return _files.TryGetValue(Key(id, path), out var content)
            ? content
            : throw new FileNotFoundException(path);
    }

    public async Task WriteFileAsync(string id, string path, string content, CancellationToken ct = default)
    {
        EnsureAlive(id);
        await Task.CompletedTask;
        _files[Key(id, path)] = content;
    }

    public async Task KillAsync(string id, CancellationToken ct = default)
    {
        await Task.CompletedTask;
        _dead[id] = true;
    }

    public void QueueOutput(string id, params ExecEvent[] events)
    {
        var queue = _scripts.GetOrAdd(id, _ => new Queue<ExecEvent>());
        foreach (var e in events)
            queue.Enqueue(e);
    }

    public bool IsDead(string id) => _dead.TryGetValue(id, out var dead) && dead;

    public SandboxSpec SpecOf(string id) => _sandboxes[id];

    private void EnsureAlive(string id)
    {
        if (!_sandboxes.ContainsKey(id))
            throw new InvalidOperationException($"Unknown sandbox '{id}'.");
        if (IsDead(id))
            throw new InvalidOperationException($"Sandbox '{id}' is dead.");
    }

    private static string Key(string id, string path) => id + "\u0000" + path;
}
