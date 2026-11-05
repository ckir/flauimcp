// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>ARRANGE helper: open the TestApp's `MenuTarget` context menu and wait until `MenuAlpha` is
/// reachable. Opening it needs a REAL right-click at a screen coordinate — MEASURED, WPF ignores a posted
/// `WM_CONTEXTMENU`, and `MenuTarget` is a non-focusable TextBlock with no UIA pattern that opens it.
///
/// ⚠ ROADMAP item 16: the menu "never opened" on the full Desktop gate yet opened fine in isolation. Two
/// MEASURED facts explain it, and `FocusAsync` returning gained=True rules out neither:
///  1. A coordinate click goes to whatever window is on top at that point. A topmost window over the
///     TestApp survives `FocusAsync`, and so did this developer's maximized terminal on one run.
///  2. ⚠⚠ WHILE THE TESTAPP IS COVERED, `GetClickablePoint()` FOR MenuTarget RETURNS A POINT OUTSIDE THE
///     TESTAPP'S OWN WINDOW — (1059,360) for a window whose right edge is x=1008, where the uncovered point
///     was (847,288). UIA hunts for a visible point and lands somewhere else entirely. FlaUI's
///     `element.RightClick()` computes its point the same way, so a partly covered TestApp sent the click
///     to a DIFFERENT WINDOW, and no amount of waiting could open the menu.
/// A stacked plain sibling TestApp and a maximized Explorer did NOT reproduce it: focus raised the TestApp
/// above both.
///
/// So the ORDER below is load-bearing: raise the window to TOPMOST first, compute the click point only
/// after that, confirm our window owns it, and click at that exact verified point. Computing the point
/// before the raise (the first version of this helper) failed deterministically under a topmost occluder.
/// If something still covers the point, such as a shell surface above the topmost band, the failure names
/// that process instead of timing out with a guess.</summary>
internal static class ContextMenuArrange
{
    public static async Task OpenAsync(WindowManager mgr, WindowHandle handle)
    {
        await mgr.FocusAsync(handle);
        IntPtr hwnd = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            win.Properties.NativeWindowHandle.ValueOrDefault);

        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        try
        {
            // POLL, do not check once, and RE-READ the point each time: it is only trustworthy once the
            // window is actually on top (fact 2 above).
            System.Drawing.Point cp = default;
            IntPtr owner = IntPtr.Zero;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                cp = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
                    win.FindFirstDescendant(cf => cf.ByAutomationId("MenuTarget"))!.GetClickablePoint());
                owner = GetAncestor(WindowFromPoint(new POINT { X = cp.X, Y = cp.Y }), GA_ROOT);
                if (owner == hwnd) break;
                await Task.Delay(50);
            }
            if (owner != hwnd)
                throw new Xunit.Sdk.XunitException(
                    $"ARRANGE failure, not a product defect: the click point ({cp.X},{cp.Y}) is covered by " +
                    $"{Describe(owner)} even after raising the TestApp to topmost, so a right-click there " +
                    "would not reach MenuTarget.");

            // Click the VERIFIED point. `element.RightClick()` would recompute it.
            FlaUI.Core.Input.Mouse.RightClick(cp);

            // The readiness probe deliberately uses RAW FlaUI against the window root, NOT PopupFinder or
            // PerceptionManager: an arrange step that calls the code under test turns a product regression
            // into a confusing arrange failure. Searching `win` alone is correct because the WPF context
            // menu is a window CHILD on this host (PopupRootCoverageTests' docstring records the measurement).
            for (int attempt = 0; attempt < 25; attempt++)
            {
                bool open = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
                    win.FindFirstDescendant(cf => cf.ByAutomationId("MenuAlpha")) is not null);
                if (open) return;
                await Task.Delay(100);
            }
            throw new Xunit.Sdk.XunitException(
                "ARRANGE failure: the click point was ours, yet the context menu never opened within 2.5s " +
                "of the right-click.");
        }
        finally
        {
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    private static string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "no window";
        GetWindowThreadProcessId(hwnd, out uint pid);
        string name;
        try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; }
        catch (ArgumentException) { name = "<exited>"; }
        return $"window 0x{hwnd:X} of process {name} (pid {pid})";
    }

    private static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
