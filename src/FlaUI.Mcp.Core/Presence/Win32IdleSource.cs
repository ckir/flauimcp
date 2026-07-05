using System.Runtime.InteropServices;

namespace FlaUI.Mcp.Core.Presence;

/// <summary>Real IIdleSource. GetLastInputInfo.dwTime and GetTickCount() are the same 32-bit clock;
/// IdleMath.Compute does the wrap-safe subtraction. Local, read-only — no synthetic input, no network.</summary>
public sealed class Win32IdleSource : IIdleSource
{
    public long IdleMs()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii)) return 0; // fail-soft to "active" (never falsely report away)
        return IdleMath.Compute(GetTickCount(), lii.dwTime);
    }

    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("kernel32.dll")] private static extern uint GetTickCount();
}
