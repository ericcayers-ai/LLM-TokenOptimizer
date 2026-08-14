using TokenOptimizer.Core.Diagnostics;

namespace TokenOptimizer.Core.Tests.Diagnostics;

public class CommandAvailabilityTests
{
    [Fact]
    public void IsOnPath_ReturnsFalse_ForCommandThatDoesNotExist()
    {
        var availability = new CommandAvailability();
        Assert.False(availability.IsOnPath("definitely-not-a-real-command-xyz123"));
    }

    [Fact]
    public void IsOnPath_ReturnsTrue_ForCmdExe()
    {
        var availability = new CommandAvailability();
        Assert.True(availability.IsOnPath("cmd.exe"));
    }

    [Fact]
    public async Task ExecutesAsync_ReturnsTrue_ForCommandThatRunsSuccessfully()
    {
        var availability = new CommandAvailability();
        var resolved = availability.ResolveOnPath("cmd.exe");
        Assert.NotNull(resolved);
        var executes = await availability.ExecutesAsync(resolved!, "/c exit 0");
        Assert.True(executes);
    }
}
