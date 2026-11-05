// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.Runtime.InteropServices;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Tests.Capture;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>Pins the ROADMAP item 16 fix: the context-menu ARRANGE must survive a window that
/// `FocusAsync` cannot raise the TestApp above. Without the topmost raise in
/// <see cref="ContextMenuArrange"/> this test fails with the R16 signature (MEASURED).</summary>
[Trait("Category", "Desktop")]
public class ContextMenuArrangeTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public ContextMenuArrangeTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task The_menu_opens_even_under_a_topmost_occluder()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        await mgr.FocusAsync(handle);
        var (rect, cp) = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            (win.BoundingRectangle,
             win.FindFirstDescendant(cf => cf.ByAutomationId("MenuTarget"))!.GetClickablePoint()));

        using var occluder = OccluderWindow.Show(rect);
        await Task.Delay(400);   // let DWM compose the occluder

        // POSITIVE CONTROL: the occluder (a window of THIS test process) really owns the click point, so
        // the arrange below is exercised against occlusion rather than passing over an uncovered window.
        GetWindowThreadProcessId(GetAncestor(WindowFromPoint(new POINT { X = cp.X, Y = cp.Y }), 2), out uint ownerPid);
        Assert.True(ownerPid == (uint)Environment.ProcessId,
            $"the occluder does not cover the click point (owner pid {ownerPid}) - this test would not exercise occlusion");

        await ContextMenuArrange.OpenAsync(mgr, handle);

        bool open = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            win.FindFirstDescendant(cf => cf.ByAutomationId("MenuAlpha")) is not null);
        Assert.True(open);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
