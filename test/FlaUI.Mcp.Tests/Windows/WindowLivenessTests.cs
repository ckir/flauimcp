using System;
using System.Collections.Generic;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Windows;

// Headless: exercises the pure liveness DECISION only (no UIA, no WindowManager instance),
// so it runs on the CI box (Category!=Desktop). The sweep→Invalidate→evict wiring is covered
// live in WindowManagerTests (Category=Desktop).
public class WindowLivenessTests
{
    private static KeyValuePair<string, IntPtr> Pair(string id, int hwnd) => new(id, new IntPtr(hwnd));

    [Fact]
    public void DeadWindowIds_returns_ids_whose_hwnd_is_not_alive()
    {
        var tracked = new[] { Pair("w1", 10), Pair("w2", 20), Pair("w3", 30) };
        var alive = new HashSet<IntPtr> { new IntPtr(10), new IntPtr(30) };
        var dead = WindowManager.DeadWindowIds(tracked, h => alive.Contains(h));
        Assert.Equal(new[] { "w2" }, dead);
    }

    [Fact]
    public void DeadWindowIds_treats_a_zero_hwnd_as_dead_without_calling_the_predicate()
    {
        var tracked = new[] { Pair("w1", 0) };
        var dead = WindowManager.DeadWindowIds(tracked, _ => throw new Exception("must not be called for IntPtr.Zero"));
        Assert.Equal(new[] { "w1" }, dead);
    }

    [Fact]
    public void DeadWindowIds_returns_empty_when_all_alive()
    {
        var tracked = new[] { Pair("w1", 10), Pair("w2", 20) };
        var dead = WindowManager.DeadWindowIds(tracked, _ => true);
        Assert.Empty(dead);
    }

    [Fact]
    public void DeadWindowIds_returns_empty_for_no_tracked_handles()
    {
        var dead = WindowManager.DeadWindowIds(Array.Empty<KeyValuePair<string, IntPtr>>(), _ => false);
        Assert.Empty(dead);
    }
}
