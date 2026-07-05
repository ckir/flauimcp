using FlaUI.Mcp.Core.Presence;
using Xunit;

namespace FlaUI.Mcp.Tests.Presence;

public class IdleActivityTests
{
    [Theory]
    [InlineData(0, Activity.Active)]
    [InlineData(59_999, Activity.Active)]
    [InlineData(60_000, Activity.Nearby)]   // X boundary → nearby
    [InlineData(299_999, Activity.Nearby)]
    [InlineData(300_000, Activity.Away)]     // Y boundary → away
    [InlineData(10_000_000, Activity.Away)]
    public void Buckets_idle_ms(long idleMs, Activity expected)
        => Assert.Equal(expected, IdleActivity.Bucket(idleMs, nearbyMs: 60_000, awayMs: 300_000));

    [Fact]
    public void Negative_idle_is_clamped_to_active()   // a bad seam read must not read as away
        => Assert.Equal(Activity.Active, IdleActivity.Bucket(-5, 60_000, 300_000));

    [Theory]
    [InlineData(60, 300, true)]
    [InlineData(300, 60, false)]   // Y < X → invalid
    [InlineData(60, 60, false)]    // Y == X → invalid (must be strictly greater)
    public void Validates_Y_greater_than_X(int x, int y, bool valid)
        => Assert.Equal(valid, IdleActivity.IsValidThresholds(x, y));
}
