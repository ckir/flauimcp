// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>Where the TestApp's open context menu lives in the UIA tree. MEASURED to be decided by whether the
/// TestApp window is the FOREGROUND window when the menu opens — not by occlusion (an inactive, uncovered window gave
/// the desktop-level shape 3 of 3), and not by WPF's IsActive (IsActive=True without the foreground also gave the
/// desktop-level shape).</summary>
internal enum PopupShape
{
    /// <summary>Window is the foreground: MenuAlpha is under the main window's UIA subtree, and no separate desktop-level
    /// window of the TestApp holds it. PopupFinder then reaches it from BOTH the window root and its window-child
    /// popup root, which is what PopupRootCoverageTests' cross-root dedup needs.</summary>
    WindowChild,

    /// <summary>Window is not the foreground: MenuAlpha is NOT under the main window; it is inside a separate desktop-level
    /// top-level window of the TestApp process.</summary>
    DesktopLevel,
}

/// <summary>ARRANGE helper: put the TestApp's `MenuTarget` context menu into a KNOWN state — closed, then open in
/// the expected <see cref="PopupShape"/> — without any screen input. This is ROADMAP item 16: the menu "never
/// opened" on the full Desktop gate yet opened fine in isolation.
///
/// ⚠⚠ DO NOT GO BACK TO A COORDINATE RIGHT-CLICK. Every form of it was MEASURED to fail:
///  1. The click goes to whatever window is on top at that point, and `FocusAsync` can report gained=True
///     while another window still covers the target.
///  2. While the TestApp is covered, UIA's `GetClickablePoint()` for MenuTarget returns a point OUTSIDE the
///     TestApp's own window ((1059,360) for a window whose right edge is x=1008), and FlaUI's
///     `element.RightClick()` clicks there.
///  3. Raising the window first does not save it. `SetWindowPos(HWND_TOPMOST)` returned TRUE with error 0 and
///     still left WS_EX_TOPMOST unset whenever another process held the foreground — e.g. a developer typing in
///     a terminal. That version (7169ab4) passed over RDP and failed 7 of 24 runs on the physical console.
///  4. A posted `WM_CONTEXTMENU` is ignored by WPF.
/// So the TestApp exposes registered window messages that open and close the SAME ContextMenu object itself
/// (`MainWindow.OpenContextMenuHook`).
///
/// ⚠⚠ AND ASSERT THE SHAPE, NEVER ASSUME IT. The TestApp's event log showed that the menu's UIA parent follows
/// FOREGROUND ownership: foreground ⇒ window child (MEASURED identical to a right-click: once via the window root,
/// once via the owner-popup roots, FindAsync total 1, one line under [Active Overlays]); otherwise ⇒ desktop level.
/// For the window-child shape the arrange therefore TAKES the foreground first (<see cref="ForegroundTaker"/>). A
/// readiness probe that accepted either would let a dedup test pass over a single root and prove nothing. The
/// shape is measured with RAW FlaUI (window subtree, plus the TestApp's own desktop-level windows), never with
/// PopupFinder or PerceptionManager: an arrange step that calls the code under test turns a product regression
/// into a confusing arrange failure.</summary>
internal static class ContextMenuArrange
{
    // Must equal FlaUI.Mcp.TestApp.MainWindow's constants (the test project launches the TestApp exe and does not
    // reference its assembly).
    private const string OpenContextMenuMessage = "FlaUI.Mcp.TestApp.OpenMenuTargetContextMenu";
    private const string CloseContextMenuMessage = "FlaUI.Mcp.TestApp.CloseMenuTargetContextMenu";

    public static async Task OpenAsync(WindowManager mgr, WindowHandle handle, PopupShape expected)
    {
        var (hwnd, pid) = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            (win.Properties.NativeWindowHandle.ValueOrDefault, win.Properties.ProcessId.ValueOrDefault));

        // 1. CLOSED first, so no test inherits the menu (or its shape) from an earlier one in the same app.
        Post(hwnd, CloseContextMenuMessage);
        if (!await PollAsync(mgr, handle, hwnd, pid, s => s.ViaWindow == 0 && s.ViaDesktop == 0))
            throw Fail("the menu did not close", await ShapeAsync(mgr, handle, hwnd, pid), pid);

        // 2. The window-child shape needs the TestApp to own the FOREGROUND when the menu opens (see the class
        //    docstring), and Windows does not reliably grant that to a freshly launched TestApp.
        if (expected == PopupShape.WindowChild)
            await ForegroundTaker.TakeAsync(hwnd, () => $"TestApp event log (most recent last):{Environment.NewLine}{LogTail(pid)}");

        // 3. OPEN, and wait until it is reachable ANYWHERE — where it lands is checked separately below.
        Post(hwnd, OpenContextMenuMessage);
        if (!await PollAsync(mgr, handle, hwnd, pid, s => s.ViaWindow + s.ViaDesktop > 0))
            throw Fail("the menu never appeared anywhere", await ShapeAsync(mgr, handle, hwnd, pid), pid);

        // 4. The SHAPE the calling test depends on.
        var shape = await ShapeAsync(mgr, handle, hwnd, pid);
        bool ok = expected == PopupShape.WindowChild
            ? shape.ViaWindow == 1 && shape.ViaDesktop == 0
            : shape.ViaWindow == 0 && shape.ViaDesktop == 1;
        if (!ok)
            throw Fail($"the menu opened, but not in the expected {expected} shape" +
                       (expected == PopupShape.WindowChild
                           ? " - the TestApp held the foreground a moment before the menu opened, so something " +
                             "took it back in between (see the foreground column in the log). The Desktop gate " +
                             "requires the operator to step away"
                           : ""),
                       shape, pid);
    }

    private readonly record struct Shape(int ViaWindow, int ViaDesktop);

    private static Task<Shape> ShapeAsync(WindowManager mgr, WindowHandle handle, IntPtr mainHwnd, int pid) =>
        mgr.RunWithWindowAndDesktopAsync(handle, (win, desktop) =>
        {
            int viaWindow = win.FindAllDescendants(cf => cf.ByAutomationId("MenuAlpha")).Length;
            // The TestApp's OTHER top-level windows, read raw from the desktop's children.
            int viaDesktop = desktop.FindAllChildren()
                .Where(c => SafePid(c) == pid && SafeHwnd(c) != mainHwnd)
                .Sum(c => c.FindAllDescendants(cf => cf.ByAutomationId("MenuAlpha")).Length);
            return new Shape(viaWindow, viaDesktop);
        });

    private static async Task<bool> PollAsync(WindowManager mgr, WindowHandle handle, IntPtr hwnd, int pid,
                                              Func<Shape, bool> done)
    {
        for (int attempt = 0; attempt < 25; attempt++)
        {
            if (done(await ShapeAsync(mgr, handle, hwnd, pid))) return true;
            await Task.Delay(100);
        }
        return false;
    }

    private static int SafePid(AutomationElement e)
    {
        try { return e.Properties.ProcessId.ValueOrDefault; }
        catch (COMException) { return -1; }   // a window vanishing mid-walk is not ours
    }

    private static IntPtr SafeHwnd(AutomationElement e)
    {
        try { return e.Properties.NativeWindowHandle.ValueOrDefault; }
        catch (COMException) { return IntPtr.Zero; }
    }

    private static void Post(IntPtr hwnd, string message)
    {
        uint msg = RegisterWindowMessage(message);
        if (msg == 0 || !PostMessage(hwnd, msg, IntPtr.Zero, IntPtr.Zero))
            throw new Xunit.Sdk.XunitException(
                $"ARRANGE failure: could not post {message} to the TestApp (0x{hwnd:X}), " +
                $"Win32 error {Marshal.GetLastWin32Error()}.");
    }

    private static Xunit.Sdk.XunitException Fail(string what, Shape shape, int pid) =>
        new($"ARRANGE failure (not a product defect): {what}. MenuAlpha under the main window: {shape.ViaWindow}; " +
            $"in the TestApp's desktop-level windows: {shape.ViaDesktop}. TestApp top-level windows: " +
            $"{DescribeTopLevelWindows(pid)}. TestApp event log (most recent last):{Environment.NewLine}{LogTail(pid)}");

    /// <summary>The TestApp's arrange event log — same path as FlaUI.Mcp.TestApp.MainWindow.LogPath.</summary>
    public static string LogPath(int pid) => Path.Combine(Path.GetTempPath(), $"flaui-testapp-{pid}.log");

    private static string LogTail(int pid, int lines = 15)
    {
        try
        {
            var all = File.ReadAllLines(LogPath(pid));
            return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)));
        }
        catch (IOException ex) { return $"<no log: {ex.Message}>"; }
    }

    private static string DescribeTopLevelWindows(int pid)
    {
        var parts = new List<string>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint p);
            if (p != (uint)pid) return true;
            var cls = new System.Text.StringBuilder(128);
            GetClassName(h, cls, cls.Capacity);
            GetWindowRect(h, out var r);
            parts.Add($"[0x{h:X} {cls} visible={IsWindowVisible(h)} rect=({r.L},{r.T},{r.R},{r.B})]");
            return true;
        }, IntPtr.Zero);
        return parts.Count == 0 ? "none" : string.Join(" ", parts);
    }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder buf, int max);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
}
