using TokenOptimizer.Core.RateLimit;

namespace TokenOptimizer.Core.Tests.RateLimit;

public class RateLimitWatcherTests
{
    [Fact]
    public void NewWatcher_HasNotDetectedRateLimit()
    {
        var watcher = new RateLimitWatcher();
        Assert.False(watcher.RateLimitDetected);
        Assert.Null(watcher.ResumeAtUtc);
    }

    [Fact]
    public void Stop_WithoutStart_DoesNotThrow()
    {
        var watcher = new RateLimitWatcher();
        watcher.Stop();
        Assert.False(watcher.RateLimitDetected);
    }
}
