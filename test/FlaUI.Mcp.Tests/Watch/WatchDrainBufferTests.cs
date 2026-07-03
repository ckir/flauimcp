// test/FlaUI.Mcp.Tests/Watch/WatchDrainBufferTests.cs
using System.Linq;
using FlaUI.Mcp.Core.Watch;
using Xunit;

namespace FlaUI.Mcp.Tests.Watch;

public class WatchDrainBufferTests
{
    private static DesktopEventPayload P(string id) =>
        new(id, "focus_changed", "w1", null, null, null, null, 1, "2026-07-03T10:00:00.000Z");

    [Fact]
    public void Append_then_Drain_returns_and_clears_in_order()
    {
        var b = new WatchDrainBuffer();
        Assert.False(b.Append("s1", P("a")));
        Assert.False(b.Append("s1", P("a")));
        var got = b.Drain("s1", null);
        Assert.Equal(2, got.Count);
        Assert.Empty(b.Drain("s1", null)); // cleared
    }

    [Fact]
    public void Append_past_cap_evicts_oldest_and_reports_drop()
    {
        var b = new WatchDrainBuffer();
        bool anyDrop = false;
        for (int i = 0; i < WatchDrainBuffer.PerSubCap + 5; i++) anyDrop |= b.Append("s1", P("x"));
        Assert.True(anyDrop);
        Assert.Equal(WatchDrainBuffer.PerSubCap, b.Drain("s1", null).Count);
    }

    [Fact]
    public void Drain_respects_max_and_Remove_clears()
    {
        var b = new WatchDrainBuffer();
        for (int i = 0; i < 5; i++) b.Append("s1", P("y"));
        Assert.Equal(2, b.Drain("s1", 2).Count);
        Assert.Equal(3, b.Drain("s1", null).Count);
        b.Append("s1", P("z"));
        b.Remove("s1");
        Assert.Empty(b.Drain("s1", null));
    }
}
