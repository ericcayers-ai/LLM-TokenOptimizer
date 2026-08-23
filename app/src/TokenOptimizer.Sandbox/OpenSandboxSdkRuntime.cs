using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using OpenSandbox;
using OpenSandbox.Config;
using OpenSandbox.Models;
using SdkSandbox = OpenSandbox.Sandbox;

namespace TokenOptimizer.Sandbox;

/// <summary>
/// ISandboxRuntime adapter over the official Alibaba.OpenSandbox C# SDK.
/// Sandbox ids minted by this instance are tracked locally so unknown or killed
/// ids fail fast with InvalidOperationException, mirroring FakeSandboxRuntime.
/// </summary>
public sealed class OpenSandboxSdkRuntime : ISandboxRuntime
{
    private readonly SandboxSettings _settings;
    private readonly ConcurrentDictionary<string, SdkSandbox> _live = new();
    private readonly ConcurrentDictionary<string, bool> _dead = new();

    public OpenSandboxSdkRuntime(SandboxSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task<SandboxHandle> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        var sandbox = await SdkSandbox.CreateAsync(
            BuildCreateOptions(spec, BuildConnectionConfig()), ct).ConfigureAwait(false);
        _live[sandbox.Id] = sandbox;
        return new SandboxHandle(sandbox.Id);
    }

    public async IAsyncEnumerable<ExecEvent> ExecAsync(
        string id, IReadOnlyList<string> argv, [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureAlive(id);
        var sandbox = _live[id];
        var sawComplete = false;
        int? errorCode = null;

        await foreach (var ev in sandbox.Commands.RunStreamAsync(BuildCommand(argv), cancellationToken: ct)
                           .ConfigureAwait(false))
        {
            if (ev.Type == ServerStreamEventTypes.ExecutionComplete)
                sawComplete = true;
            else if (ev.Type == ServerStreamEventTypes.Error && TryParseErrorExitCode(ev.Error, out var code))
                errorCode = code;

            var mapped = MapStreamEvent(ev);
            if (mapped is not null)
                yield return mapped;
        }

        yield return new ExecExit(ResolveExitCode(sawComplete, errorCode));
    }

    public async Task<string> ReadFileAsync(string id, string path, CancellationToken ct = default)
    {
        EnsureAlive(id);
        return await _live[id].Files.ReadFileAsync(path, options: null, cancellationToken: ct).ConfigureAwait(false);
    }

    public Task WriteFileAsync(string id, string path, string content, CancellationToken ct = default)
    {
        EnsureAlive(id);
        return _live[id].Files.WriteFilesAsync(
            new[] { new WriteEntry { Path = path, Data = content } }, ct);
    }

    public async Task KillAsync(string id, CancellationToken ct = default)
    {
        if (_live.TryRemove(id, out var sandbox))
        {
            await sandbox.KillAsync(ct).ConfigureAwait(false);
            await sandbox.DisposeAsync().ConfigureAwait(false);
        }
        _dead[id] = true;
    }

    internal ConnectionConfig BuildConnectionConfig() => new(new ConnectionConfigOptions
    {
        Domain = _settings.Domain,
        Protocol = ParseProtocol(_settings.Protocol),
    });

    internal static ConnectionProtocol ParseProtocol(string? protocol) =>
        string.Equals(protocol, "https", StringComparison.OrdinalIgnoreCase)
            ? ConnectionProtocol.Https
            : ConnectionProtocol.Http;

    internal static SandboxCreateOptions BuildCreateOptions(SandboxSpec spec, ConnectionConfig? config)
    {
        IReadOnlyList<SandboxMount> mounts = spec.Mounts ?? Array.Empty<SandboxMount>();
        return new SandboxCreateOptions
        {
            ConnectionConfig = config,
            Image = spec.Image,
            Env = spec.Env,
            TimeoutSeconds = spec.Timeout is { } timeout ? Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds)) : null,
            ManualCleanup = spec.Timeout is null,
            Volumes = MapVolumes(mounts),
        };
    }

    internal static Volume[] MapVolumes(IReadOnlyList<SandboxMount> mounts) =>
        mounts.Select((mount, index) => new Volume
        {
            Name = $"mount-{index}",
            Host = new Host { Path = mount.Source },
            MountPath = mount.Target,
            ReadOnly = mount.ReadOnly,
        }).ToArray();

    internal static string BuildCommand(IReadOnlyList<string> argv)
    {
        if (argv is null || argv.Count == 0)
            throw new ArgumentException("argv must contain at least one element.", nameof(argv));
        var builder = new StringBuilder();
        foreach (var arg in argv)
        {
            if (builder.Length > 0)
                builder.Append(' ');
            builder.Append('\'').Append(arg.Replace("'", "'\\''")).Append('\'');
        }
        return builder.ToString();
    }

    internal static ExecEvent? MapStreamEvent(ServerStreamEvent ev) => ev.Type switch
    {
        ServerStreamEventTypes.Stdout => new ExecOutput("stdout", ev.Text ?? string.Empty),
        ServerStreamEventTypes.Stderr => new ExecOutput("stderr", ev.Text ?? string.Empty),
        _ => null,
    };

    internal static int ResolveExitCode(bool sawComplete, int? errorCode) =>
        errorCode ?? (sawComplete ? 0 : 1);

    internal static bool TryParseErrorExitCode(Dictionary<string, object>? error, out int code)
    {
        code = 0;
        if (error is null)
            return false;
        foreach (var key in new[] { "evalue", "value" })
        {
            if (error.TryGetValue(key, out var raw) &&
                int.TryParse(raw?.ToString(), out code))
                return true;
        }
        return false;
    }

    private void EnsureAlive(string id)
    {
        if (_dead.TryGetValue(id, out var dead) && dead)
            throw new InvalidOperationException($"Sandbox '{id}' is dead.");
        if (!_live.ContainsKey(id))
            throw new InvalidOperationException($"Unknown sandbox '{id}'.");
    }
}
