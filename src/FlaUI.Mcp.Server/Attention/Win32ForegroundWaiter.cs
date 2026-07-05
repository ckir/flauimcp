using System;
using System.Runtime.InteropServices;
using System.Threading;
using FlaUI.Mcp.Core.Attention;

namespace FlaUI.Mcp.Server.Attention;

/// <summary>Real IForegroundWaiter (spec §4.5): runs a private message loop on a DEDICATED background thread
/// (NOT the query/action STA — hooking there would starve every other tool) and installs a WinEvent hook for
/// EVENT_SYSTEM_FOREGROUND + EVENT_OBJECT_DESTROY. Returns as soon as the target gains foreground, the target
/// HWND is destroyed, or the timeout elapses.</summary>
public sealed class Win32ForegroundWaiter : IForegroundWaiter
{
    public WaitResult Wait(IntPtr targetHwnd, int timeoutMs)
    {
        if (GetForegroundWindow() == targetHwnd) return new WaitResult(true, WaitReason.Gained);
        if (!IsWindow(targetHwnd)) return new WaitResult(false, WaitReason.WindowDestroyed);

        WaitResult result = new(false, WaitReason.Timeout);
        var done = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            WinEventDelegate cb = (hook, ev, hwnd, idObj, idChild, thr, time) =>
            {
                if (ev == EVENT_SYSTEM_FOREGROUND && GetForegroundWindow() == targetHwnd)
                { result = new WaitResult(true, WaitReason.Gained); PostQuitMessage(0); }
                else if (ev == EVENT_OBJECT_DESTROY && hwnd == targetHwnd && idObj == OBJID_WINDOW)
                { result = new WaitResult(false, WaitReason.WindowDestroyed); PostQuitMessage(0); }
            };
            var hFg = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, cb, 0, 0, WINEVENT_OUTOFCONTEXT);
            var hDes = SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_DESTROY, IntPtr.Zero, cb, 0, 0, WINEVENT_OUTOFCONTEXT);
            // Re-check after hooks are installed (close the race where it gained/died between the fast path and here).
            if (GetForegroundWindow() == targetHwnd) result = new WaitResult(true, WaitReason.Gained);
            else if (!IsWindow(targetHwnd)) result = new WaitResult(false, WaitReason.WindowDestroyed);
            else
            {
                var timer = SetTimer(IntPtr.Zero, IntPtr.Zero, (uint)timeoutMs, IntPtr.Zero);
                if (timer != IntPtr.Zero)
                {
                    while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                    {
                        // Match OUR timer id (SEAT-C fold): STA initializes COM, which can post its own
                        // WM_TIMER to this thread's queue; accepting any WM_TIMER could abort the wait early.
                        if (msg.message == WM_TIMER && msg.wParam == timer) break;   // our timeout only
                        TranslateMessage(ref msg); DispatchMessage(ref msg);
                    }
                    KillTimer(IntPtr.Zero, timer);
                }
                // SetTimer failed (returned 0) → do NOT enter an unbounded GetMessage loop: if the hooks
                // also failed to fire there would be no escape and this dedicated STA thread would leak for
                // the process lifetime. Leave `result` at its Timeout default; the caller re-invokes
                // (slow-poll design) so there is no functional harm — just no thread leak on resource failure.
            }
            if (hFg != IntPtr.Zero) UnhookWinEvent(hFg);
            if (hDes != IntPtr.Zero) UnhookWinEvent(hDes);
            GC.KeepAlive(cb);
            done.Set();
        }) { IsBackground = true, Name = "flaui-mcp-fg-wait" };
        thread.SetApartmentState(ApartmentState.STA); // WinEvent hooks want a message pump; STA on a DEDICATED thread is safe
        thread.Start();
        done.Wait(timeoutMs + 2000);                  // hard ceiling so a wedged hook can't leak the caller
        return result;
    }

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_OBJECT_DESTROY = 0x8001, WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int OBJID_WINDOW = 0; private const uint WM_TIMER = 0x0113;
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventDelegate cb, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, IntPtr id);
}
