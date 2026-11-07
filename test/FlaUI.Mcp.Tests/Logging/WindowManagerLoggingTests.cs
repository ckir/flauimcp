// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

public class WindowManagerLoggingTests
{
    [Fact]
    public void A_fresh_lazy_mint_emits_window_bind_and_a_reuse_does_not()
    {
        var log = new CapturingLogger<WindowManager>();
        using var d = new AutomationDispatcher();
        var wm = new WindowManager(d, log);
        var pid = Environment.ProcessId;
        var id = wm.LazyHandleFor((IntPtr)0x7FFF0001, pid);
        Assert.Equal(id, wm.LazyHandleFor((IntPtr)0x7FFF0001, pid));
        var ev = log.Inner.Single("window.bind");
        Assert.Equal(id, ev.Fields.Single(f => f.Key == "handle").Value);
        Assert.Equal((long)0x7FFF0001, ev.Fields.Single(f => f.Key == "hwnd").Value);
        Assert.Equal(pid, ev.Fields.Single(f => f.Key == "pid").Value);
        Assert.Equal(Process.GetCurrentProcess().ProcessName, ev.Fields.Single(f => f.Key == "process").Value);
        Assert.True(ev.Fields.Single(f => f.Key == "title").UiText);
    }

    [Fact]
    public async Task ActionBlockedPending_emits_action_blocked_and_rethrows()
    {
        var log = new CapturingLogger<WindowManager>();
        using var d = new AutomationDispatcher();
        var wm = new WindowManager(d, log);
        var blocked = Task.FromException<int>(new ToolException(ToolErrorCode.ActionBlockedPending, "blocked"));
        var ex = await Assert.ThrowsAsync<ToolException>(() => wm.ObserveBlockedAsync(blocked));
        Assert.Equal(ToolErrorCode.ActionBlockedPending, ex.Code);
        var ev = log.Inner.Single("action.blocked");
        Assert.Contains(ev.Fields, f => f.Key == "fgHwnd");
        Assert.True(ev.Fields.Single(f => f.Key == "fgTitle").UiText);
    }

    [Fact]
    public async Task Other_failures_are_not_action_blocked()
    {
        var log = new CapturingLogger<WindowManager>();
        using var d = new AutomationDispatcher();
        var wm = new WindowManager(d, log);
        await Assert.ThrowsAsync<ToolException>(() => wm.ObserveBlockedAsync(
            Task.FromException<int>(new ToolException(ToolErrorCode.WindowHandleStale, "stale"))));
        Assert.Empty(log.Inner.Events);
    }

    [Fact]
    public void Focus_not_gained_names_the_foreground_owner()
    {
        var log = new CapturingLogger<WindowManager>();
        using var d = new AutomationDispatcher();
        var wm = new WindowManager(d, log);
        wm.EmitFocus(new WindowHandle("w9"), new FocusResult(false, (IntPtr)0x10, (IntPtr)0x20));
        var ev = log.Inner.Single("focus.attempt");
        Assert.Equal("w9", ev.Fields.Single(f => f.Key == "target").Value);
        Assert.False((bool)ev.Fields.Single(f => f.Key == "gained").Value!);
        Assert.Equal((long)0x20, ev.Fields.Single(f => f.Key == "fgHwnd").Value);
        Assert.Contains(ev.Fields, f => f.Key == "fgProcess");
    }

    [Fact]
    public void Focus_gained_carries_no_foreground_fields()
    {
        var log = new CapturingLogger<WindowManager>();
        using var d = new AutomationDispatcher();
        new WindowManager(d, log).EmitFocus(new WindowHandle("w9"), new FocusResult(true, (IntPtr)0x10, (IntPtr)0x10));
        Assert.DoesNotContain(log.Inner.Single("focus.attempt").Fields, f => f.Key.StartsWith("fg", StringComparison.Ordinal));
    }
}
