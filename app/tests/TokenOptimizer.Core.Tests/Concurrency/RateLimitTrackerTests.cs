using TokenOptimizer.Core.Concurrency;
using TokenOptimizer.Core.Config;
using TokenOptimizer.Core.Models;

namespace TokenOptimizer.Core.Tests.Concurrency;

public class RateLimitTrackerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly RateLimitTracker _tracker;

    public RateLimitTrackerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tokopt-ratelimit-" + Guid.NewGuid().ToString("N"));
        _tracker = new RateLimitTracker(new ConfigStore(_tempDir));
    }

    [Fact]
    public async Task IsRateLimitedAsync_ReturnsFalse_WhenNeverRecorded()
    {
        Assert.False(await _tracker.IsRateLimitedAsync(FallbackProvider.Codex));
    }

    [Fact]
    public async Task IsRateLimitedAsync_ReturnsTrue_WhileCooldownStillInFuture()
    {
        await _tracker.RecordRateLimitAsync(FallbackProvider.Antigravity, DateTimeOffset.UtcNow.AddMinutes(30));
        Assert.True(await _tracker.IsRateLimitedAsync(FallbackProvider.Antigravity));
    }

    [Fact]
    public async Task IsRateLimitedAsync_ReturnsFalse_OnceCooldownHasExpired()
    {
        await _tracker.RecordRateLimitAsync(FallbackProvider.Cursor, DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.False(await _tracker.IsRateLimitedAsync(FallbackProvider.Cursor));
    }

    [Fact]
    public async Task RecordRateLimitAsync_TracksProvidersIndependently()
    {
        await _tracker.RecordRateLimitAsync(FallbackProvider.Codex, DateTimeOffset.UtcNow.AddMinutes(30));

        Assert.True(await _tracker.IsRateLimitedAsync(FallbackProvider.Codex));
        Assert.False(await _tracker.IsRateLimitedAsync(FallbackProvider.Cursor));
        Assert.False(await _tracker.IsRateLimitedAsync(FallbackProvider.Antigravity));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
