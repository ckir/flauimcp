using System;
using System.Runtime.InteropServices;
using FlaUI.Mcp.Core.Attention;
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Server.Attention;

/// <summary>Always-available attention channel (spec §4.3): flashes the target's taskbar button until the
/// window comes to the foreground (FLASHW_TIMERNOFG). Steals no focus, needs no foreground rights, works
/// cross-process with just the HWND. Best-effort — never throws.</summary>
public sealed class FlashSignal : IAttentionSignal
{
    private readonly IHwndSource _windows;
    public FlashSignal(IHwndSource windows) => _windows = windows;
    public bool Enabled => true;

    public void Signal(WindowHandle target)
    {
        try
        {
            if (!_windows.TryGetHwnd(target, out var hwnd)) return; // no HWND (e.g. closed) → nothing to flash
            var fw = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = hwnd,
                dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
                uCount = uint.MaxValue,
                dwTimeout = 0,
            };
            FlashWindowEx(ref fw);
        }
        catch { /* flash is best-effort; never break a tool result */ }
    }

    private const uint FLASHW_TRAY = 0x2, FLASHW_TIMERNOFG = 0xC;
    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO pfwi);
}
