// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.Runtime.InteropServices;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>The DESKTOP-LEVEL popup case that ROADMAP recorded as "structural, not test-proven": find, wait_for
/// (valueEquals) and the snapshot graft were routed through PopupFinder.SearchRoots so they reach a popup that
/// UIA places at the desktop rather than under its owner window — but the fixture could only ever produce the
/// window-child shape, so nothing proved it.
///
/// MEASURED (ROADMAP item 16 investigation): the TestApp's context menu lands at DESKTOP LEVEL whenever the
/// window does not own the FOREGROUND when it opens, occluded or not (3 of 3 with an uncovered, inactive window).
/// Taking the foreground away is cheap and reliable from the test process — launch any later window. So each test launches a second TestApp after the target, moves it clear of the target without
/// activating it, and the arrange step ASSERTS PopupShape.DesktopLevel before anything is tested: MenuAlpha is
/// NOT under the target window, and IS in one of the target's own desktop-level windows.</summary>
[Trait("Category", "Desktop")]
public class DesktopLevelPopupTests
{
    private sealed class Rig : IDisposable
    {
        public TestAppFixture Target { get; } = new();
        public TestAppFixture Deactivator { get; }
        public AutomationDispatcher Dispatcher { get; } = new();
        public WindowManager Mgr { get; }
        public PerceptionManager Perception { get; }
        public WindowHandle Handle { get; private set; }

        private Rig()
        {
            Deactivator = new TestAppFixture();   // launched AFTER Target, so it takes activation away from it
            Mgr = new WindowManager(Dispatcher);
            Perception = new PerceptionManager(Mgr, new RefRegistry(), new SnapshotCache());
        }

        public static async Task<Rig> CreateAsync()
        {
            var rig = new Rig();
            try
            {
                // Clear of the target so the deactivator cannot occlude it: the shape must come from INACTIVITY.
                GetWindowRect(rig.Target.Process.MainWindowHandle, out var t);
                SetWindowPos(rig.Deactivator.Process.MainWindowHandle, IntPtr.Zero, t.R + 50, t.B + 50, 0, 0,
                             SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                rig.Handle = await rig.Mgr.OpenByPidAsync(rig.Target.Process.Id);
                await ContextMenuArrange.OpenAsync(rig.Mgr, rig.Handle, PopupShape.DesktopLevel);
                return rig;
            }
            catch
            {
                rig.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Mgr.Dispose();
            Dispatcher.Dispose();
            Deactivator.Dispose();
            Target.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
        private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }

    [Fact]
    public async Task Find_reaches_a_desktop_level_popup_exactly_once()
    {
        using var rig = await Rig.CreateAsync();

        var r = await rig.Perception.FindAsync(rig.Handle, new FindQuery("MenuAlpha", null, "eq", null, false),
                                               max: 20, scopeRef: null);

        var match = Assert.Single(r.Matches);
        Assert.Equal("MenuAlpha", match.AutomationId);
        Assert.Equal(1, r.TotalMatches);
    }

    [Fact]
    public async Task ValueEquals_reaches_a_desktop_level_popup()
    {
        using var rig = await Rig.CreateAsync();
        var wait = new WaitCoordinator(rig.Perception);

        // MenuAlpha's Header is "Alpha" (MainWindow.xaml:103); the value read falls back ValuePattern -> Name.
        var r = await wait.WaitForAsync(rig.Handle, "automationId", "MenuAlpha",
            until: "valueEquals", equals: "Alpha", timeoutMs: 1500, pollIntervalMs: 300);

        Assert.True(r.Satisfied);
    }

    [Fact]
    public async Task Snapshot_grafts_a_desktop_level_popup_under_Active_Overlays_exactly_once()
    {
        using var rig = await Rig.CreateAsync();

        var snap = await rig.Perception.SnapshotAsync(rig.Handle, new SnapshotOptions { FullProperties = true });

        Assert.Contains("[Active Overlays]", snap.Tree);
        Assert.Equal(1, snap.Tree.Split('\n').Count(l => l.Contains("aid=MenuAlpha")));
        // ...and it sits UNDER the overlay header, not in the main walk.
        Assert.True(snap.Tree.IndexOf("aid=MenuAlpha", StringComparison.Ordinal) >
                    snap.Tree.IndexOf("[Active Overlays]", StringComparison.Ordinal));
    }
}
