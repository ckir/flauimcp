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

    [Fact]
    public void CanReuseHandle_reuses_only_when_recorded_pid_matches_the_enumerated_pid()
    {
        Assert.True(WindowManager.CanReuseHandle(cachedPid: 1234, enumeratedPid: 1234));
    }

    [Fact]
    public void CanReuseHandle_mints_fresh_when_pid_differs_hwnd_recycled_to_a_new_process()
    {
        Assert.False(WindowManager.CanReuseHandle(cachedPid: 1234, enumeratedPid: 5678));
    }

    [Fact]
    public void HwndStillOwnedBy_true_when_current_pid_matches_recorded()
        => Assert.True(WindowManager.HwndStillOwnedBy(recordedPid: 42, currentPidForHwnd: 42));

    [Fact]
    public void HwndStillOwnedBy_false_when_hwnd_recycled_to_a_different_pid()
        => Assert.False(WindowManager.HwndStillOwnedBy(recordedPid: 42, currentPidForHwnd: 99));

    [Fact]
    public void HwndStillOwnedBy_false_for_a_dead_hwnd_zero_pid()
        => Assert.False(WindowManager.HwndStillOwnedBy(recordedPid: 42, currentPidForHwnd: 0));

    // ── Foreground-restoration fallback (type-then-close keyboard-orphan fix) ──────────────
    // EnumTopLevel already yields visible, titled, non-sentinel windows in Z-order, so the pure
    // decision is only "first Z-ordered window that isn't the one we just closed". Live SetForeground
    // is Category=Desktop; this covers the selection.
    private static (IntPtr Hwnd, string Title, int Pid) Win(int hwnd, string title = "app", int pid = 100)
        => (new IntPtr(hwnd), title, pid);

    [Fact]
    public void PickForegroundFallback_returns_first_zorder_window_when_closed_hwnd_absent()
    {
        var z = new[] { Win(10), Win(20), Win(30) };
        Assert.Equal(new IntPtr(10), WindowManager.PickForegroundFallback(z, new IntPtr(999)));
    }

    [Fact]
    public void PickForegroundFallback_skips_the_closed_hwnd_and_returns_the_next_in_zorder()
    {
        var z = new[] { Win(10), Win(20), Win(30) };
        Assert.Equal(new IntPtr(20), WindowManager.PickForegroundFallback(z, new IntPtr(10)));
    }

    [Fact]
    public void PickForegroundFallback_returns_zero_when_only_the_closed_window_remains()
    {
        var z = new[] { Win(10) };
        Assert.Equal(IntPtr.Zero, WindowManager.PickForegroundFallback(z, new IntPtr(10)));
    }

    [Fact]
    public void PickForegroundFallback_returns_zero_for_an_empty_desktop()
        => Assert.Equal(IntPtr.Zero,
            WindowManager.PickForegroundFallback(Array.Empty<(IntPtr, string, int)>(), new IntPtr(10)));
}
