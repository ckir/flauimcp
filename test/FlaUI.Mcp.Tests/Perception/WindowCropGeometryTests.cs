using System.Drawing;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class WindowCropGeometryTests
{
    // Window scope: E == W1, nothing moved, nothing resized. The crop is the whole bitmap and both
    // translations land back on the window's own origin.
    [Fact]
    public void Window_scope_static_window_is_the_whole_bitmap()
    {
        var w1 = new Rectangle(100, 200, 800, 600);
        var g = WindowCropGeometry.Compute(new Size(800, 600), e: w1, w1: w1, w2: w1);
        Assert.NotNull(g);
        Assert.Equal(new Rectangle(0, 0, 800, 600), g!.Value.Effective);
        Assert.Equal(new Rectangle(100, 200, 800, 600), g.Value.Absolute);
        Assert.Equal(new Rectangle(100, 200, 800, 600), g.Value.Reported);
    }

    // THE MOVE CASE. This is the numeric trace panel round 7 verified by hand and it is the reason W1
    // anchors the mask side: window (-8,-8,1936,1036) moves to (92,92,...), element at (100,200,300,50).
    // Using W2 on the way back would give absolute=(200,300) against masks sampled at (100,200) -- every
    // mask 100px off.
    [Fact]
    public void A_pure_move_keeps_masks_on_W1_and_reports_on_W2()
    {
        var w1 = new Rectangle(-8, -8, 1936, 1036);
        var w2 = new Rectangle(92, 92, 1936, 1036);
        var e  = new Rectangle(100, 200, 300, 50);
        var g = WindowCropGeometry.Compute(new Size(1936, 1036), e, w1, w2);
        Assert.NotNull(g);
        Assert.Equal(new Rectangle(108, 208, 300, 50), g!.Value.Effective);
        Assert.Equal(new Rectangle(100, 200, 300, 50), g.Value.Absolute);   // masks land here
        Assert.Equal(new Rectangle(200, 300, 300, 50), g.Value.Reported);   // pixels are actually here
    }

    // A window that GREW exposes area the UIA walk never inspected -- pixels no mask was computed for.
    // Intersect discards exactly that region because `relative` is W1-sized. Without this the design
    // returns unscanned pixels and calls it a success.
    [Fact]
    public void A_grown_window_is_cropped_back_to_the_region_that_was_scanned()
    {
        var w1 = new Rectangle(0, 0, 800, 600);
        var w2 = new Rectangle(0, 0, 1000, 700);
        var g = WindowCropGeometry.Compute(new Size(1000, 700), e: w1, w1: w1, w2: w2);
        Assert.NotNull(g);
        Assert.Equal(new Rectangle(0, 0, 800, 600), g!.Value.Effective);
        Assert.Equal(800, g.Value.Absolute.Width);
        Assert.Equal(600, g.Value.Absolute.Height);
    }

    [Fact]
    public void A_shrunk_window_clamps_so_nothing_reads_past_the_bitmap()
    {
        var w1 = new Rectangle(0, 0, 800, 600);
        var w2 = new Rectangle(0, 0, 500, 400);
        var g = WindowCropGeometry.Compute(new Size(500, 400), e: w1, w1: w1, w2: w2);
        Assert.NotNull(g);
        Assert.Equal(new Rectangle(0, 0, 500, 400), g!.Value.Effective);
    }

    // The invariant the whole subsection exists to protect, asserted on every case above at once.
    [Theory]
    [InlineData(0, 0, 800, 600, 0, 0, 800, 600)]
    [InlineData(-8, -8, 1936, 1036, 92, 92, 1936, 1036)]
    [InlineData(0, 0, 800, 600, 0, 0, 1000, 700)]
    [InlineData(0, 0, 800, 600, 0, 0, 500, 400)]
    public void Src_absolute_and_reported_always_share_one_size(
        int x1, int y1, int cx1, int cy1, int x2, int y2, int cx2, int cy2)
    {
        var w1 = new Rectangle(x1, y1, cx1, cy1);
        var w2 = new Rectangle(x2, y2, cx2, cy2);
        var g = WindowCropGeometry.Compute(new Size(cx2, cy2), e: w1, w1: w1, w2: w2);
        Assert.NotNull(g);
        Assert.Equal(g!.Value.Effective.Size, g.Value.Absolute.Size);
        Assert.Equal(g.Value.Effective.Size, g.Value.Reported.Size);
    }

    // An element that fell entirely outside the new bitmap. Returns null; the caller refuses. Unguarded,
    // Bitmap.Clone on this throws ArgumentException, which ScreenCapture.cs:40's COMException/
    // ExternalException filter does NOT catch, so it escapes as a raw unmapped exception.
    [Fact]
    public void An_element_entirely_outside_the_bitmap_returns_null()
    {
        var w1 = new Rectangle(0, 0, 800, 600);
        var w2 = new Rectangle(0, 0, 200, 150);
        var e  = new Rectangle(500, 400, 100, 40);
        Assert.Null(WindowCropGeometry.Compute(new Size(200, 150), e, w1, w2));
    }

    // GUARD ON EXTENTS, NOT IsEmpty. Rectangle.Intersect yields a ZERO-EXTENT rect at NON-ZERO
    // coordinates for rects that merely TOUCH along an edge, and IsEmpty is false there. This repo has
    // already shipped the wrong form of this exact guard and documents the resulting leak at
    // PerceptionManager.cs:942-946. An element touching the window's right edge is that case.
    [Fact]
    public void A_zero_width_touching_intersection_is_rejected_even_though_IsEmpty_is_false()
    {
        var w1 = new Rectangle(0, 0, 800, 600);
        var e  = new Rectangle(800, 100, 50, 30);   // starts exactly at the right edge
        var g = WindowCropGeometry.Compute(new Size(800, 600), e, w1, w2: w1);
        Assert.Null(g);
    }
}
