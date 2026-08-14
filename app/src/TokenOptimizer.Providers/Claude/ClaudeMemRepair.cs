using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using TokenOptimizer.Core.Diagnostics;

namespace TokenOptimizer.Providers.Claude;

/// <summary>
/// Self-heal for a tracked claude-mem bug on Windows
/// (github.com/thedotmack/claude-mem/issues/2926): the background worker can
/// die without releasing its listener on port 37777, so the next session's
/// worker fails to bind and never comes up - and because claude-mem's
/// UserPromptSubmit hook fails CLOSED when the worker is unreachable, every
/// prompt gets blocked while hook-failures.json's consecutiveFailures
/// counter climbs without bound. Runs right before every Claude launch, and
/// is entirely best-effort: every step is wrapped so a failure here never
/// blocks or delays launch. Ported from Test-ClaudeMemWorkerHealthy /
/// Repair-ClaudeMemWorker.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ClaudeMemRepair
{
    private const int DefaultWorkerPort = 37777;
    private const int StuckFailureThreshold = 10;

    /// <summary>Plain TCP-connect liveness probe - a successful connect means something is actively accepting connections on the port right now.</summary>
    public static async Task<bool> IsWorkerHealthyAsync(int port = DefaultWorkerPort, int timeoutMs = 750)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync("127.0.0.1", port);
            var completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs)) == connectTask;
            return completed && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public static async Task RepairAsync()
    {
        using var repairMutex = new Mutex(false, "Global\\LLMTokenOptimizer_ClaudeMemRepair", out _);
        var haveMutex = false;
        try
        {
            haveMutex = repairMutex.WaitOne(TimeSpan.FromSeconds(3));
        }
        catch (AbandonedMutexException)
        {
            haveMutex = true;
        }
        catch
        {
            haveMutex = true; // fail open - a missing mutex should never block the repair entirely
        }

        if (!haveMutex) return; // another window is repairing right now

        try
        {
            await ReclaimOrphanedPortAsync();
            await ClearStuckFailureStateAsync();
        }
        finally
        {
            try { if (haveMutex) repairMutex.ReleaseMutex(); } catch { /* already released */ }
        }
    }

    private static async Task ReclaimOrphanedPortAsync()
    {
        var portEnv = Environment.GetEnvironmentVariable("CLAUDE_MEM_WORKER_PORT");
        var port = int.TryParse(portEnv, out var parsed) ? parsed : DefaultWorkerPort;

        try
        {
            var result = await ExternalCommandRunner.RunAsync("netstat", "-ano", timeoutSeconds: 10);
            if (!result.Success) return;

            foreach (var line in result.Output.Split('\n'))
            {
                if (!line.Contains($":{port} ") && !line.Contains($":{port}\t")) continue;
                if (!line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || !int.TryParse(parts[^1], out var ownerPid)) continue;

                try
                {
                    Process.GetProcessById(ownerPid);
                }
                catch (ArgumentException)
                {
                    // Listed by netstat but not enumerable via GetProcessById -
                    // the orphaned-holder signature the upstream bug describes.
                    try { Process.GetProcessById(ownerPid).Kill(); } catch { }
                }
            }
        }
        catch
        {
            // Best effort - a netstat parsing failure must never block launch.
        }
    }

    private static async Task ClearStuckFailureStateAsync()
    {
        var stateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude-mem", "state");
        var failFile = Path.Combine(stateDir, "hook-failures.json");
        if (!File.Exists(failFile)) return;

        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(failFile));
            var count = doc.RootElement.TryGetProperty("consecutiveFailures", out var countProp) && countProp.ValueKind == JsonValueKind.Number
                ? countProp.GetInt32() : 0;

            if (count >= StuckFailureThreshold && !await IsWorkerHealthyAsync())
            {
                File.Delete(failFile);
                var supervisorFile = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude-mem", "supervisor.json");
                if (File.Exists(supervisorFile)) File.Delete(supervisorFile);
            }
        }
        catch (JsonException)
        {
            // A malformed state file is exactly what this exists to route around.
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}
