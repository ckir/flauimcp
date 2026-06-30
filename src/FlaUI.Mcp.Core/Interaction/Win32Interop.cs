using System;
using System.Runtime.InteropServices;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Shared Win32 P/Invoke surface for the synthetic-input leaf and platform probes. The INPUT
/// struct layout is byte-for-byte the layout validated by the 2026-06-30 active-RDP spike (SendInput
/// returned non-zero / fired). Do NOT reshape — a wrong size/alignment makes SendInput silently
/// return 0 on x64.</summary>
public static class Win32Interop
{
    public const uint INPUT_MOUSE = 0;
    public const uint INPUT_KEYBOARD = 1;

    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;

    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    public const uint GA_ROOT = 2;

    // GetSystemMetrics indices for the virtual screen.
    public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    public const uint MAXIMUM_ALLOWED = 0x02000000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] public static extern nint WindowFromPhysicalPoint(POINT p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(nint hWnd, System.Text.StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)] public static extern nint OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseDesktop(nint hDesktop);
}

[StructLayout(LayoutKind.Sequential)]
public struct POINT { public int X; public int Y; }

[StructLayout(LayoutKind.Sequential)]
public struct INPUT { public uint type; public InputUnion U; }

[StructLayout(LayoutKind.Explicit)]
public struct InputUnion
{
    [FieldOffset(0)] public MOUSEINPUT mi;
    [FieldOffset(0)] public KEYBDINPUT ki;
    [FieldOffset(0)] public HARDWAREINPUT hi;
}

[StructLayout(LayoutKind.Sequential)]
public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public nint dwExtraInfo; }

[StructLayout(LayoutKind.Sequential)]
public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public nint dwExtraInfo; }

[StructLayout(LayoutKind.Sequential)]
public struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }
