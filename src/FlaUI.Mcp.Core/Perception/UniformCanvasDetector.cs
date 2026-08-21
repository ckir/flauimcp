using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>Is this bitmap effectively a single colour? A DIAGNOSTIC, never a gate (§3).
///
/// ⚠ NOT a darkness heuristic, and this is proven rather than argued. Evidence row F4 is an occluded
/// XAML window captured pixel-perfectly -- nav rail, toggles, body text, a warning banner -- whose
/// non-black fraction is 0.044 because it is a DARK THEME. A darkness predicate rejects a flawless
/// capture. The property is UNIFORMITY, not luminance.
///
/// ⚠ It must never grow into a correctness gate. §3's whole error budget is calibrated on the
/// consequence of a false positive being a spurious sentence; reusing this signal to SELECT A BACKEND
/// would re-price every error in it, and a false positive would then silently return a scrape -- for an
/// occluded window, a photograph of the occluder, the exact defect this feature exists to remove.</summary>
public static class UniformCanvasDetector
{
    // A sparse grid, not the full bitmap. Cost is O(GridN^2) regardless of resolution, which is what
    // satisfies criterion 3 on a 4K window. 64x64 = 4096 samples: dense enough that a real UI cannot hide
    // all of its contrast between the sample points, cheap enough to be free next to a PNG encode.
    private const int GridN = 64;

    /// <summary>TRUE when every sampled pixel has the same ARGB value.</summary>
    public static bool IsUniform(Bitmap bmp)
    {
        if (bmp.Width <= 0 || bmp.Height <= 0) return true;   // nothing to disagree

        int stepX = System.Math.Max(1, bmp.Width / GridN);
        int stepY = System.Math.Max(1, bmp.Height / GridN);

        // ⚠ FORMAT GUARD, and it is not decoration. LockBits asked for a pixel format the bitmap does
        // NOT already have converts the WHOLE image into a temporary buffer -- at 4K that is a ~33 MB
        // allocation on the capture path, trading a few ms of CPU for a large transient. So we lock in
        // the bitmap's OWN format and only when that format is 32bpp, which makes the lock zero-copy and
        // makes a 4-byte read per sample correct. MEASURED: every bitmap this predicate actually
        // receives is 32bpp -- `new Bitmap(w, h)` defaults to Format32bppArgb, which covers the scrape
        // path, the PrintWindow path and the tests -- so the fast path is the one that runs. The
        // fallback exists so an unexpected format degrades in SPEED rather than in MEMORY.
        if (Image.GetPixelFormatSize(bmp.PixelFormat) == 32)
        {
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                                    ImageLockMode.ReadOnly, bmp.PixelFormat);
            try
            {
                // ⚠ A NEGATIVE stride means a BOTTOM-UP DIB: Scan0 points at the LAST scanline and the
                // offset arithmetic below would walk backwards out of the buffer. Rare, and not worth
                // special-casing, so we fall through to GetPixel -- but we must UNLOCK first, because
                // GetPixel on a locked bitmap throws InvalidOperationException.
                if (data.Stride > 0) return ScanLocked(data, bmp.Width, bmp.Height, stepX, stepY);
            }
            finally { bmp.UnlockBits(data); }
        }

        return ScanWithGetPixel(bmp, stepX, stepY);
    }

    /// <summary>The fast path: raw 4-byte reads at the same sample points, no per-pixel marshalling.</summary>
    private static bool ScanLocked(BitmapData data, int width, int height, int stepX, int stepY)
    {
        IntPtr scan0 = data.Scan0;
        int stride = data.Stride;
        int first = Marshal.ReadInt32(scan0, 0);

        for (int y = 0; y < height; y += stepY)
            for (int x = 0; x < width; x += stepX)
                if (Marshal.ReadInt32(scan0, y * stride + x * 4) != first) return false;

        // The far edges are sampled explicitly: a stride that does not divide the dimension would
        // otherwise never look at the last row/column, and a render that failed only at one edge is
        // exactly the shape a grid can miss.
        for (int y = 0; y < height; y += stepY)
            if (Marshal.ReadInt32(scan0, y * stride + (width - 1) * 4) != first) return false;
        for (int x = 0; x < width; x += stepX)
            if (Marshal.ReadInt32(scan0, (height - 1) * stride + x * 4) != first) return false;

        return true;
    }

    /// <summary>The fallback, for a non-32bpp or bottom-up bitmap. Identical sample points and identical
    /// answer -- only slower. Verified against the fast path before the fast path was adopted.</summary>
    private static bool ScanWithGetPixel(Bitmap bmp, int stepX, int stepY)
    {
        int first = bmp.GetPixel(0, 0).ToArgb();
        for (int y = 0; y < bmp.Height; y += stepY)
            for (int x = 0; x < bmp.Width; x += stepX)
                if (bmp.GetPixel(x, y).ToArgb() != first) return false;

        for (int y = 0; y < bmp.Height; y += stepY)
            if (bmp.GetPixel(bmp.Width - 1, y).ToArgb() != first) return false;
        for (int x = 0; x < bmp.Width; x += stepX)
            if (bmp.GetPixel(x, bmp.Height - 1).ToArgb() != first) return false;

        return true;
    }
}
