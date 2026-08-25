namespace TokenOptimizer.Sandbox;

public interface ISandboxRuntime
{
    Task<SandboxHandle> CreateAsync(SandboxSpec spec, CancellationToken ct = default);
    IAsyncEnumerable<ExecEvent> ExecAsync(string id, IReadOnlyList<string> argv, CancellationToken ct = default);
    Task<string> ReadFileAsync(string id, string path, CancellationToken ct = default);
    Task WriteFileAsync(string id, string path, string content, CancellationToken ct = default);
    Task KillAsync(string id, CancellationToken ct = default);
}

public sealed record SandboxMount(string Target, string Source, bool ReadOnly = false);

public sealed record SandboxSpec(
    string Image,
    IReadOnlyList<SandboxMount> Mounts,
    TimeSpan? Timeout = null,
    IReadOnlyDictionary<string, string>? Env = null);

public sealed record SandboxHandle(string Id);

public abstract record ExecEvent(string Text);

public sealed record ExecOutput(string Stream, string Text) : ExecEvent(Text);

public sealed record ExecExit(int Code) : ExecEvent(Code.ToString());
