using System.Runtime.InteropServices;

namespace FlaUI.Mcp.Core.Perception.Geometry;

/// <summary>Effective per-monitor DPI scale for a screen point. Informational only (design §2):
/// bounds are already physical px, so dpiScale is metadata, never load-bearing for targeting.</summary>
public static class DpiHelper
{
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
    [DllImport("Shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    public static double ScaleForPoint(int x, int y)
    {
        try
        {
            var mon = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return 1.0;
            if (GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out uint dpiX, out _) != 0) return 1.0;
            return dpiX <= 0 ? 1.0 : dpiX / 96.0;
        }
        catch { return 1.0; }
    }
}
