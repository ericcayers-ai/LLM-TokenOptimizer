using TokenOptimizer.Providers.LmStudio;

namespace TokenOptimizer.Providers.Tests.LmStudio;

public class LmStudioAdapterTests
{
    [Theory]
    [InlineData(true, "The server is running on port 1234.", true)]
    [InlineData(true, "The server is not running.", false)]
    [InlineData(false, "The server is running on port 1234.", false)]
    [InlineData(true, "", false)]
    public void IsServerUp_DistinguishesRunningFromNotRunning(bool statusOk, string statusOutput, bool expected)
    {
        // "not running" contains the substring "running" - a naive check
        // would misreport a down server as up. This is the exact bug that
        // was fixed live in run_benchmarks.py's _server_is_up().
        Assert.Equal(expected, LmStudioAdapter.IsServerUp(statusOk, statusOutput));
    }
}
