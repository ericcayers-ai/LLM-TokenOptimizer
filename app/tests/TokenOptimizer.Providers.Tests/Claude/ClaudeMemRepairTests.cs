using TokenOptimizer.Providers.Claude;

namespace TokenOptimizer.Providers.Tests.Claude;

public class ClaudeMemRepairTests
{
    [Fact]
    public async Task IsWorkerHealthyAsync_ReturnsFalse_WhenNothingListeningOnPort()
    {
        // Port 1 is a reserved/unassigned port nothing binds to in normal
        // operation - a safe stand-in for "worker isn't running".
        var healthy = await ClaudeMemRepair.IsWorkerHealthyAsync(port: 1, timeoutMs: 300);
        Assert.False(healthy);
    }

    [Fact]
    public async Task RepairAsync_DoesNotThrow_WhenNoClaudeMemStateExists()
    {
        await ClaudeMemRepair.RepairAsync();
    }
}
