using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Capture;

/// <summary>AGY-CAPSTONE round 3, de-risking my own round-2 fix. The OCR bookend now compares a full
/// RECTANGLE from two different sources: the walk's `geo.Bounds` (UIA `BoundingRectangle`) against
/// `GetWindowRect`. If those disagree systematically for some window class, the bookend refuses EVERY
/// OCR capture of it — a self-inflicted outage far worse than the move blindspot it closed.
///
/// The OLD check compared SIZE across the same two sources, so the cross-source risk already existed;
/// adding POSITION widens it. This measures the actual agreement across every visible window rather than
/// inferring it from the one test app.
///
/// ⚠ Dual-trait, so it runs in NEITHER gate: headless excludes Desktop, the Desktop gate excludes
/// Measurement. It is a probe, not a gate.</summary>
[Trait("Category", "Desktop")]
[Trait("Category", "Measurement")]
public class UiaVsGetWindowRectProbe
{
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [Fact]
    public async Task Measure_UIA_bounds_against_GetWindowRect_for_every_visible_window()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var windows = await mgr.ListWindowsAsync(includeBounds: true, includeHandles: true);

        int same = 0, sizeOnly = 0, differ = 0, unresolved = 0;
        var examples = new System.Collections.Generic.List<string>();

        foreach (var w in windows)
        {
            if (w.Handle is null) { unresolved++; continue; }

            // Resolve the native handle AND the UIA rect from the SAME walk the capture path uses -
            // w.Handle is a logical id ("w1"), not an HWND.
            IntPtr hwnd; Rectangle uia;
            try
            {
                (hwnd, uia) = await mgr.RunWithWindowAndDesktopAsync(new WindowHandle(w.Handle),
                    (win, _) => (win.Properties.NativeWindowHandle.ValueOrDefault, win.BoundingRectangle));
            }
            catch { unresolved++; continue; }

            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) { unresolved++; continue; }
            var win32 = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

            if (uia == win32) same++;
            else if (uia.Size == win32.Size) { sizeOnly++; if (examples.Count < 4) examples.Add($"{w.ProcessName}: uia={uia} win32={win32} (SAME SIZE, different origin)"); }
            else { differ++; if (examples.Count < 4) examples.Add($"{w.ProcessName}: uia={uia} win32={win32}"); }
        }

        Assert.Fail(
            $"PROBE | windows={windows.Count} | identical={same} | sameSizeDifferentOrigin={sizeOnly} | " +
            $"differentSize={differ} | unresolved={unresolved} | examples: {string.Join(" ;; ", examples)}");
    }
}
