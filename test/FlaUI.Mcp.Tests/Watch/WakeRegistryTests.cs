using System.Linq;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Watch;
using Xunit;

namespace FlaUI.Mcp.Tests.Watch;

public class WakeRegistryTests
{
    [Fact]
    public void Create_mints_monotonic_k_prefixed_never_reused_ids()
    {
        var r = new WakeRegistry();
        Assert.Equal("k1", r.Create("w1"));
        Assert.Equal("k2", r.Create("w2"));
        r.Remove("k1");
        Assert.Equal("k3", r.Create("w3")); // counter never resets
    }

    [Fact]
    public void Create_enforces_per_session_cap_TooManyWatches()
    {
        var r = new WakeRegistry();
        for (int i = 0; i < WakeRegistry.MaxPerSession; i++) r.Create("w" + i);
        var ex = Assert.Throws<ToolException>(() => r.Create("wZ"));
        Assert.Equal(ToolErrorCode.TooManyWatches, ex.Code);
    }

    [Fact]
    public void Remove_is_idempotent()
    {
        var r = new WakeRegistry();
        var a = r.Create("w1");
        Assert.True(r.Remove(a));
        Assert.False(r.Remove(a));
        Assert.False(r.Remove("k999"));
    }

    [Fact]
    public void RemoveByWindow_evicts_all_that_window_returns_ids()
    {
        var r = new WakeRegistry();
        var a = r.Create("w1");
        var b = r.Create("w1");
        var c = r.Create("w2");
        var evicted = r.RemoveByWindow("w1").OrderBy(x => x).ToArray();
        Assert.Equal(new[] { a, b }, evicted);
        Assert.True(r.TryGet(c, out _));
        Assert.False(r.TryGet(a, out _));
    }

    [Fact]
    public void List_reports_window_ids()
    {
        var r = new WakeRegistry();
        var a = r.Create("w1");
        var info = Assert.Single(r.List());
        Assert.Equal(a, info.WakeId);
        Assert.Equal("w1", info.WindowId);
    }
}
