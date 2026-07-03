// test/FlaUI.Mcp.Tests/Watch/WatchRegistryTests.cs
using System.Linq;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Watch;
using Xunit;

namespace FlaUI.Mcp.Tests.Watch;

public class WatchRegistryTests
{
    private static readonly WatchEventKind[] Focus = { WatchEventKind.FocusChanged };

    [Fact]
    public void Create_mints_monotonic_never_reused_ids()
    {
        var r = new WatchRegistry();
        var a = r.Create("w1", Focus, null);
        var b = r.Create("w1", Focus, null);
        Assert.Equal("s1", a);
        Assert.Equal("s2", b);
        r.Remove(a);
        var c = r.Create("w1", Focus, null);
        Assert.Equal("s3", c); // counter never resets (no id reuse)
    }

    [Fact]
    public void Create_enforces_per_window_cap_TooManyWatches()
    {
        var r = new WatchRegistry();
        for (int i = 0; i < WatchRegistry.MaxPerWindow; i++) r.Create("w1", Focus, null);
        var ex = Assert.Throws<ToolException>(() => r.Create("w1", Focus, null));
        Assert.Equal(ToolErrorCode.TooManyWatches, ex.Code);
        Assert.Equal("s" + (WatchRegistry.MaxPerWindow + 1), r.Create("w2", Focus, null));
    }

    [Fact]
    public void Create_enforces_per_session_cap_TooManyWatches()
    {
        var r = new WatchRegistry();
        int made = 0;
        for (int w = 1; made < WatchRegistry.MaxPerSession; w++)
            for (int i = 0; i < WatchRegistry.MaxPerWindow && made < WatchRegistry.MaxPerSession; i++)
            { r.Create("w" + w, Focus, null); made++; }
        var ex = Assert.Throws<ToolException>(() => r.Create("wZ", Focus, null));
        Assert.Equal(ToolErrorCode.TooManyWatches, ex.Code);
    }

    [Fact]
    public void Remove_is_idempotent()
    {
        var r = new WatchRegistry();
        var a = r.Create("w1", Focus, null);
        Assert.True(r.Remove(a));
        Assert.False(r.Remove(a));
        Assert.False(r.Remove("s999"));
    }

    [Fact]
    public void RemoveByWindow_evicts_all_that_window_returns_ids()
    {
        var r = new WatchRegistry();
        var a = r.Create("w1", Focus, null);
        var b = r.Create("w1", Focus, null);
        var c = r.Create("w2", Focus, null);
        var evicted = r.RemoveByWindow("w1").OrderBy(x => x).ToArray();
        Assert.Equal(new[] { a, b }, evicted);
        Assert.True(r.TryGet(c, out _));
        Assert.False(r.TryGet(a, out _));
    }

    [Fact]
    public void List_and_TryGet_report_kinds_scope_dropped()
    {
        var r = new WatchRegistry();
        var a = r.Create("w1", new[] { WatchEventKind.StructureChanged }, "e5");
        r.IncrementDropped(a);
        r.IncrementDropped(a);
        Assert.True(r.TryGet(a, out var info));
        Assert.Equal("w1", info!.WindowId);
        Assert.Equal("e5", info.Scope);
        Assert.Equal(2, info.DroppedCount);
        Assert.Contains(WatchEventKind.StructureChanged, info.Kinds);
        var listed = Assert.Single(r.List());
        Assert.Equal(a, listed.SubscriptionId);
    }
}
