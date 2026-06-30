using System;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Maps a physical screen pixel into the 0-65535 absolute coordinate space over the VIRTUAL
/// desktop that MOUSEEVENTF_ABSOLUTE|MOUSEEVENTF_VIRTUALDESK expects. Pure given the virtual-screen
/// bounds (origin can be negative for monitors left of / above the primary). The 2026-06-30 spike
/// proved this formula pixel-perfect (target 960,540 -> landed 960,540).</summary>
public static class VirtualDesktopMap
{
    public static (int ax, int ay) ToAbsolute(int physX, int physY, int originX, int originY, int width, int height)
    {
        int ax = Scale(physX - originX, width);
        int ay = Scale(physY - originY, height);
        return (ax, ay);
    }

    // 65535 * offset / (span - 1), rounded, clamped to [0, 65535]. (span-1 so the last pixel hits 65535.)
    private static int Scale(int offset, int span)
    {
        if (span <= 1) return 0;
        long v = (long)Math.Round(65535.0 * offset / (span - 1));
        return (int)Math.Clamp(v, 0, 65535);
    }
}
