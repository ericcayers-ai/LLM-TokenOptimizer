using System.Diagnostics;

namespace TokenOptimizer.Sandbox;

public interface IProcessRunner
{
    Task<ProcResult> RunAsync(string exe, IReadOnlyList<string> args,
        IDictionary<string, string>? env = null, CancellationToken ct = default);
}

public sealed record ProcResult(int ExitCode, string StdOut, string StdErr);

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcResult> RunAsync(string exe, IReadOnlyList<string> args,
        IDictionary<string, string>? env = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{exe}'.");
        {
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return new ProcResult(proc.ExitCode, await stdoutTask, await stderrTask);
        }
    }
}
