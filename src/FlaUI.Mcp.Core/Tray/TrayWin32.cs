// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.Runtime.InteropServices;
using System.Text;

namespace FlaUI.Mcp.Core.Tray;

/// <summary>Tray-specific user32 imports. Pure Win32, thread-agnostic, never blocks on another process
/// (GetWindowText/GetClassName/GetMenu* read cached state; SendMessage(MN_GETHMENU) is answered by the
/// menu window, which is modal-loop driven and responsive while the menu is up).</summary>
internal static class TrayWin32
{
    public const uint MN_GETHMENU = 0x01E1;
    public const uint MF_BYPOSITION = 0x400, MF_GRAYED = 0x1, MF_DISABLED = 0x2, MF_POPUP = 0x10, MF_SEPARATOR = 0x800;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, nint lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint FindWindowW(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(nint hwnd, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLengthW(nint hwnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint SendMessageW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern nint SendMessageTimeoutW(nint hwnd, uint msg, nint w, nint l, uint flags, uint timeoutMs, out nint result);
    public const uint SMTO_BLOCK = 0x1, SMTO_ABORTIFHUNG = 0x2;

    /// <summary>SendMessage that cannot hang the caller: returns the result, or 0 when the call times out or fails.</summary>
    public static nint SendMessageBounded(nint hwnd, uint msg, nint w, nint l, uint timeoutMs = 500)
        => SendMessageTimeoutW(hwnd, msg, w, l, SMTO_ABORTIFHUNG | SMTO_BLOCK, timeoutMs, out var result) == 0 ? 0 : result;
    [DllImport("user32.dll")] public static extern int GetMenuItemCount(nint hMenu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetMenuStringW(nint hMenu, uint item, StringBuilder s, int max, uint flags);
    [DllImport("user32.dll")] public static extern uint GetMenuState(nint hMenu, uint item, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMenuItemRect(nint hwnd, nint hMenu, uint item, out RECT rect);

    public static string ClassOf(nint hwnd) { var sb = new StringBuilder(256); return GetClassNameW(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty; }
    public static bool HasTitle(nint hwnd) => GetWindowTextLengthW(hwnd) > 0;
    public static int PidOf(nint hwnd) { GetWindowThreadProcessId(hwnd, out uint pid); return (int)pid; }

    /// <summary>Every VISIBLE top-level HWND, titled or not (SPEC §2 step 2: no title filter).</summary>
    public static List<nint> VisibleTopLevel()
    {
        var list = new List<nint>();
        EnumWindows((h, _) => { if (IsWindowVisible(h)) list.Add(h); return true; }, 0);
        return list;
    }

    /// <summary>First visible top-level window of the given class, or 0.</summary>
    public static nint FindVisibleByClass(string className)
        => VisibleTopLevel().FirstOrDefault(h => ClassOf(h) == className);
}
