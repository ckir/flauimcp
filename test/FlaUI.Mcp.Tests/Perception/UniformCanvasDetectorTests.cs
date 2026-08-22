using System.Diagnostics;
using System.Drawing;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class UniformCanvasDetectorTests
{
    private static Bitmap Solid(int w, int h, Color c)
    {
        var b = new Bitmap(w, h);
        using var g = Graphics.FromImage(b);
        using var brush = new SolidBrush(c);
        g.FillRectangle(brush, 0, 0, w, h);
        return b;
    }

    // Criterion 1: a failed render is one colour. Black is the common case; white and a mid-grey are
    // included because "uniform" is the property, not "dark" -- F4 proved a darkness heuristic unsound.
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(255, 255, 255)]
    [InlineData(128, 128, 128)]
    public void A_uniform_bitmap_is_detected(int r, int g, int b)
    {
        using var bmp = Solid(400, 300, Color.FromArgb(r, g, b));
        Assert.True(UniformCanvasDetector.IsUniform(bmp));
    }

    // Criterion 2: THE F4 CASE. A dark-themed but real render -- mostly near-black with sparse light
    // content, ~4% non-black. This MUST come back false. It is the single case that rules out every
    // darkness-based predicate.
    [Fact]
    public void The_F4_dark_themed_real_render_is_not_uniform()
    {
        using var bmp = Solid(400, 300, Color.FromArgb(18, 18, 18));   // a dark theme's background
        using (var g = Graphics.FromImage(bmp))
        using (var brush = new SolidBrush(Color.FromArgb(230, 230, 230)))
        {
            // ~4% of the area as light content, scattered the way real UI chrome is.
            for (int i = 0; i < 12; i++) g.FillRectangle(brush, 20 + i * 30, 20 + (i % 5) * 50, 24, 8);
        }
        Assert.False(UniformCanvasDetector.IsUniform(bmp));
    }

    // A single differing pixel is still not uniform. This pins that the predicate is about COLOUR COUNT
    // and not about a proportion threshold that could be tuned into swallowing real content.
    [Fact]
    public void One_differing_region_is_enough_to_be_non_uniform()
    {
        using var bmp = Solid(400, 300, Color.Black);
        using (var g = Graphics.FromImage(bmp))
        using (var brush = new SolidBrush(Color.White))
            g.FillRectangle(brush, 200, 150, 12, 12);
        Assert.False(UniformCanvasDetector.IsUniform(bmp));
    }

    // ⚠ THE FALLBACK PATH. IsUniform takes a fast LockBits route only for 32bpp bitmaps and falls back
    // to GetPixel otherwise. Every bitmap the predicate receives in production is 32bpp, which means the
    // fallback would be entirely UNEXERCISED without these two -- dead code that still ships and still
    // has to be right. A 24bpp bitmap is the cheapest way to reach it through the public API.
    [Fact]
    public void A_non_32bpp_uniform_bitmap_is_detected_through_the_fallback()
    {
        using var bmp = new Bitmap(400, 300, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        using (var brush = new SolidBrush(Color.FromArgb(18, 18, 18)))
            g.FillRectangle(brush, 0, 0, 400, 300);
        Assert.True(UniformCanvasDetector.IsUniform(bmp));
    }

    [Fact]
    public void A_non_32bpp_bitmap_with_content_is_not_uniform_through_the_fallback()
    {
        using var bmp = new Bitmap(400, 300, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            using (var bg = new SolidBrush(Color.FromArgb(18, 18, 18))) g.FillRectangle(bg, 0, 0, 400, 300);
            using (var fg = new SolidBrush(Color.White)) g.FillRectangle(fg, 200, 150, 12, 12);
        }
        Assert.False(UniformCanvasDetector.IsUniform(bmp));
    }

    // Criterion 3: it runs over a bitmap that is already allocated and already being encoded. A detector
    // that costs real time has chosen the wrong sampling strategy. 4K-wide is the worst realistic case.
    [Fact]
    [Trait("Category", "Measurement")]
    public void It_costs_no_measurable_time_on_a_4K_bitmap()
    {
        using var bmp = Solid(3840, 2160, Color.FromArgb(18, 18, 18));
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) UniformCanvasDetector.IsUniform(bmp);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 100,
            $"20 detections took {sw.ElapsedMilliseconds}ms; the sampling strategy is too expensive");
    }
}
