using System;
using FlaUI.Mcp.Core.Attention;
using Xunit;

namespace FlaUI.Mcp.Tests.Attention;

public class TtsDebounceTests
{
    [Fact]
    public void Allows_up_to_capacity_then_blocks_within_window()
    {
        var t0 = DateTime.UtcNow;
        var d = new TtsDebounce(capacity: 3, window: TimeSpan.FromSeconds(30));
        Assert.True(d.TryTake(t0));
        Assert.True(d.TryTake(t0));
        Assert.True(d.TryTake(t0));
        Assert.False(d.TryTake(t0));                       // 4th within window → blocked
    }

    [Fact]
    public void Is_target_agnostic_oscillating_targets_do_not_evade()
    {
        // The bucket takes no target argument at all — proven by capacity being global.
        var t0 = DateTime.UtcNow;
        var d = new TtsDebounce(3, TimeSpan.FromSeconds(30));
        d.TryTake(t0); d.TryTake(t0); d.TryTake(t0);
        Assert.False(d.TryTake(t0));                       // still blocked regardless of any target churn
    }

    [Fact]
    public void Refills_as_the_window_slides()
    {
        var t0 = DateTime.UtcNow;
        var d = new TtsDebounce(3, TimeSpan.FromSeconds(30));
        d.TryTake(t0); d.TryTake(t0); d.TryTake(t0);
        Assert.True(d.TryTake(t0 + TimeSpan.FromSeconds(31))); // first three aged out
    }
}
