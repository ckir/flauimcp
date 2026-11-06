// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.Runtime.InteropServices;

namespace FlaUI.Mcp.Tests;

/// <summary>ARRANGE helper: make a window the FOREGROUND window, and PROVE it. For tests whose arrange needs the
/// foreground (the window-child popup shape; OCR of a scraped screen region) — never for product behaviour.
///
/// MEASURED (ROADMAP item 16): Windows gives a newly launched TestApp the foreground only some of the time. In an
/// idle session, a run launched one way gave every TestApp the foreground; the same tests launched another way
/// stopped getting it ~35-40 s into the run, and every later TestApp stayed in the background while the terminal
/// kept the foreground. Once a foreign window holds it, a plain SetForegroundWindow from the test process returns
/// FALSE (0 of 3), and AttachThreadInput — to either the target's or the caller's thread — does not help (0 of 6).
/// `WindowManager.FocusAsync` can report gained=True in that state.
///
/// What works (5 of 5, interleaved with plain calls that failed): one zero-distance mouse move through SendInput
/// first. That makes the test process the one that "received the last input event", which is one of the documented
/// conditions under which SetForegroundWindow is allowed. The pointer does not move.
///
/// This is NOT `SyntheticInput` in the trait's sense: that trait marks tests needing flaui-mcp's human-granted input
/// LEASE (scripts/desktop-suite.ps1 excludes them because a scheduled run cannot get one). This needs no lease;
/// tagging a test SyntheticInput for using it would only drop it from the Desktop suite.</summary>
internal static class ForegroundTaker
{
    /// <param name="hwnd">The window to bring to the foreground.</param>
    /// <param name="diagnostics">Extra text for the failure message (e.g. the TestApp's event log).</param>
    public static async Task TakeAsync(IntPtr hwnd, Func<string>? diagnostics = null)
    {
        var input = new INPUT[1];
        input[0].type = INPUT_MOUSE;
        input[0].mi.dwFlags = MOUSEEVENTF_MOVE;   // dx = dy = 0: relative move of zero pixels
        if (SendInput(1, input, Marshal.SizeOf<INPUT>()) != 1)
            throw new Xunit.Sdk.XunitException(
                $"ARRANGE failure (not a product defect): SendInput rejected the zero-distance move, Win32 error " +
                $"{Marshal.GetLastWin32Error()}. The desktop is probably locked or the session disconnected.");
        SetForegroundWindow(hwnd);

        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (GetForegroundWindow() == hwnd) return;
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException(
            $"ARRANGE failure (not a product defect): window 0x{hwnd:X} did not become the foreground window; " +
            $"the foreground is {Describe(GetForegroundWindow())}." +
            (diagnostics is null ? "" : $" {diagnostics()}"));
    }

    private static string Describe(IntPtr h)
    {
        GetWindowThreadProcessId(h, out uint p);
        var title = new System.Text.StringBuilder(80);
        GetWindowText(h, title, title.Capacity);
        string name;
        try { name = System.Diagnostics.Process.GetProcessById((int)p).ProcessName; }
        catch (ArgumentException) { name = "?"; }
        return $"0x{h:X} {name}({p}) '{title}'";
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder buf, int max);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    private const uint INPUT_MOUSE = 0, MOUSEEVENTF_MOVE = 0x0001;
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    // INPUT is a union of MOUSEINPUT/KEYBDINPUT/HARDWAREINPUT; only the mouse arm is used. On x64 the union starts
    // at offset 8 and MOUSEINPUT is the largest arm, so this layout has the native size (40 bytes).
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public MOUSEINPUT mi; }
}
