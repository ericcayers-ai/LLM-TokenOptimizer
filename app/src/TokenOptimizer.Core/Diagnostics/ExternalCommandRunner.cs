using System.Diagnostics;
using System.Text;

namespace TokenOptimizer.Core.Diagnostics;

/// <summary>
/// Runs an external process and captures stdout/stderr without the classic
/// full-pipe deadlock: both ReadToEndAsync calls are kicked off before we
/// wait on the process, so the child is never blocked writing into a full
/// buffer while we're synchronously waiting for it to exit.
/// </summary>
public static class ExternalCommandRunner
{
    public static async Task<CommandResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        int timeoutSeconds = 0,
        IReadOnlyDictionary<string, string>? extraEnvironment = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (extraEnvironment is not null)
        {
            foreach (var (key, value) in extraEnvironment)
            {
                psi.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = psi };

        try
        {
            if (!process.Start())
            {
                return new CommandResult { Success = false, Output = "Process failed to start" };
            }
        }
        catch (Exception ex)
        {
            return new CommandResult { Success = false, Output = ex.Message };
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            if (timeoutSeconds > 0)
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                try
                {
                    await process.WaitForExitAsync(linkedCts.Token);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    TryKill(process);
                    return new CommandResult
                    {
                        Success = false,
                        TimedOut = true,
                        Output = $"Command timed out after {timeoutSeconds}s",
                    };
                }
            }
            else
            {
                await process.WaitForExitAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdout = await SafeAwait(stdoutTask);
        var stderr = await SafeAwait(stderrTask);
        var combined = (stdout + stderr).Trim();

        return new CommandResult
        {
            Success = process.ExitCode == 0,
            ExitCode = process.ExitCode,
            Output = combined,
        };
    }

    /// <summary>
    /// Same contract as RunAsync but delivers stdout/stderr line-by-line as
    /// the child process writes them, for callers that want to show live
    /// output (a benchmark run streaming for minutes/hours) instead of
    /// waiting for the whole thing to finish. Still returns a final
    /// CommandResult with the fully aggregated output once the process exits.
    /// </summary>
    public static async Task<CommandResult> RunStreamingAsync(
        string fileName,
        string arguments,
        string? workingDirectory,
        Action<string> onOutputLine,
        Action<string>? onErrorLine = null,
        int timeoutSeconds = 0,
        IReadOnlyDictionary<string, string>? extraEnvironment = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (extraEnvironment is not null)
        {
            foreach (var (key, value) in extraEnvironment)
            {
                psi.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var allOutput = new StringBuilder();
        var outputLock = new object();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (outputLock) allOutput.AppendLine(e.Data);
            onOutputLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (outputLock) allOutput.AppendLine(e.Data);
            (onErrorLine ?? onOutputLine)(e.Data);
        };

        try
        {
            if (!process.Start())
            {
                return new CommandResult { Success = false, Output = "Process failed to start" };
            }
        }
        catch (Exception ex)
        {
            return new CommandResult { Success = false, Output = ex.Message };
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            if (timeoutSeconds > 0)
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                try
                {
                    await process.WaitForExitAsync(linkedCts.Token);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    TryKill(process);
                    return new CommandResult
                    {
                        Success = false,
                        TimedOut = true,
                        Output = $"Command timed out after {timeoutSeconds}s",
                    };
                }
            }
            else
            {
                await process.WaitForExitAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        lock (outputLock)
        {
            return new CommandResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                Output = allOutput.ToString().Trim(),
            };
        }
    }

    private static async Task<string> SafeAwait(Task<string> task)
    {
        try { return await task; }
        catch { return string.Empty; }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* best effort */ }
    }
}
