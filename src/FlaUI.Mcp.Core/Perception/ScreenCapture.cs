using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FlaUI.Core.Capturing;
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Perception;

public sealed record CaptureResult(byte[] Png, int X, int Y, int W, int H, double ScaleApplied, int Redactions);

/// <summary>Screen-region capture (no occlusion handling — callers focus-first; no UIA element reads).
/// Captures by absolute screen rectangle so it can run OFF the query STA (spec §8). Paints black
/// redaction rects (live bounds passed in), clamps width to a hard ceiling, PNG-encodes. Headless/
/// disconnected sessions are detected before capture so we never hand back a black frame.</summary>
public static class ScreenCapture
{
    private const int MaxCaptureWidth = 1920; // hard ceiling — bounds base64 payload even if maxWidth<=0

    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr hDesktop);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    public static bool IsDesktopRenderable()
    {
        var h = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (h == IntPtr.Zero) return false;
        CloseDesktop(h); return true;
    }

    public static Rectangle VirtualScreenBounds() => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    public static CaptureResult CaptureRectangle(Rectangle absolute, IReadOnlyList<Rectangle> redactAbsolute, int maxWidth)
    {
        CaptureImage cap;
        try { cap = Capture.Rectangle(absolute, null); }
        catch (System.Exception ex) when (ex is COMException or System.Runtime.InteropServices.ExternalException)
        { throw new ToolException(ToolErrorCode.CaptureUnavailable, "Screen capture failed (session may be disconnected/locked).", "reconnect to restore rendering"); }
        using (cap) return Encode(cap.Bitmap, absolute, redactAbsolute, maxWidth);
    }

    private static CaptureResult Encode(Bitmap src, Rectangle captureBounds, IReadOnlyList<Rectangle> redactAbsolute, int maxWidth)
    {
        int cap = maxWidth <= 0 ? MaxCaptureWidth : System.Math.Min(maxWidth, MaxCaptureWidth);
        double scale = src.Width > cap ? (double)cap / src.Width : 1.0;
        int outW = System.Math.Max(1, (int)System.Math.Round(src.Width * scale));
        int outH = System.Math.Max(1, (int)System.Math.Round(src.Height * scale));
        using var outBmp = new Bitmap(outW, outH);
        using (var g = Graphics.FromImage(outBmp))
        {
            g.DrawImage(src, new Rectangle(0, 0, outW, outH));
            int painted = 0;
            using var black = new SolidBrush(Color.Black);
            foreach (var r in redactAbsolute)
            {
                if (!r.IntersectsWith(captureBounds)) continue; // off-crop field — don't count/paint
                var clip = Rectangle.Intersect(r, captureBounds);  // clip to the captured region
                var rel = new Rectangle(
                    (int)System.Math.Round((clip.X - captureBounds.X) * scale), (int)System.Math.Round((clip.Y - captureBounds.Y) * scale),
                    (int)System.Math.Round(clip.Width * scale), (int)System.Math.Round(clip.Height * scale));
                if (rel.Width <= 0 || rel.Height <= 0) continue;
                g.FillRectangle(black, rel); painted++;
            }
            using var ms = new MemoryStream();
            outBmp.Save(ms, ImageFormat.Png);
            return new CaptureResult(ms.ToArray(), captureBounds.X, captureBounds.Y, captureBounds.Width, captureBounds.Height, scale, painted);
        }
    }
}
