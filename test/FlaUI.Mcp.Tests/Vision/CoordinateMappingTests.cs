using FlaUI.Mcp.Core.Vision;
using Xunit;

namespace FlaUI.Mcp.Tests.Vision;

public class CoordinateMappingTests
{
    [Fact]
    public void Unscaled_capture_origin_zero_window_at_origin_is_identity_fraction()
    {
        // capture origin (0,0), scale 1.0; window rect (0,0,100x100); bitmap point (50,50) -> center -> (0.5,0.5)
        var m = CoordinateMapping.BitmapToWindowPct(
            bitmapX: 50, bitmapY: 50, scaleApplied: 1.0, captureX: 0, captureY: 0,
            winLeft: 0, winTop: 0, winWidth: 100, winHeight: 100);
        Assert.Equal(50, m.ScreenX);
        Assert.Equal(50, m.ScreenY);
        Assert.Equal(0.5, m.XPct, 5);
        Assert.Equal(0.5, m.YPct, 5);
    }

    [Fact]
    public void Downscaled_capture_undoes_scale_before_mapping()
    {
        // 150% display: source was 1500px wide, clamped to 1000 -> scaleApplied = 0.6667. A bitmap x of 300 maps
        // back to screen 300/0.6667 = 450. Capture origin (200,100). Window rect (200,100,600x400).
        double scale = 1000.0 / 1500.0;
        var m = CoordinateMapping.BitmapToWindowPct(
            bitmapX: 300, bitmapY: 200, scaleApplied: scale, captureX: 200, captureY: 100,
            winLeft: 200, winTop: 100, winWidth: 600, winHeight: 400);
        Assert.Equal(200 + 300 / scale, m.ScreenX, 3); // 200 + 450 = 650
        Assert.Equal(100 + 200 / scale, m.ScreenY, 3); // 100 + 300 = 400
        Assert.Equal((650 - 200) / 600.0, m.XPct, 5);  // 0.75
        Assert.Equal((400 - 100) / 400.0, m.YPct, 5);  // 0.75
    }

    [Fact]
    public void Negative_capture_origin_multi_monitor_left_of_primary()
    {
        // Window on a monitor left of primary: capture origin negative. bitmap (10,10), scale 1, win (-800,0,400x300)
        var m = CoordinateMapping.BitmapToWindowPct(
            bitmapX: 10, bitmapY: 10, scaleApplied: 1.0, captureX: -800, captureY: 0,
            winLeft: -800, winTop: 0, winWidth: 400, winHeight: 300);
        Assert.Equal(-790, m.ScreenX);
        Assert.Equal(10, m.ScreenY);
        Assert.Equal(10 / 400.0, m.XPct, 5);
        Assert.Equal(10 / 300.0, m.YPct, 5);
    }

    [Fact]
    public void Cropped_capture_origin_differs_from_window_origin()
    {
        // Window straddles a screen edge: window rect (-50,0,400x300) but capture cropped to visible (0,0,350x300).
        // A bitmap point uses the CAPTURE origin (0,0), and the fraction uses the WINDOW rect (-50,...).
        var m = CoordinateMapping.BitmapToWindowPct(
            bitmapX: 100, bitmapY: 30, scaleApplied: 1.0, captureX: 0, captureY: 0,
            winLeft: -50, winTop: 0, winWidth: 400, winHeight: 300);
        Assert.Equal(100, m.ScreenX);                 // capture origin 0 + 100
        Assert.Equal((100 - (-50)) / 400.0, m.XPct, 5); // (screenX - winLeft)/winWidth = 150/400 = 0.375
    }

    [Fact]
    public void Center_of_a_word_rect_maps_from_rect_helper()
    {
        // The rect helper takes bitmap rect (x,y,w,h) and returns the CENTER's window-pct + physical center.
        var c = CoordinateMapping.BitmapRectCenterToWindowPct(
            bx: 40, by: 40, bw: 20, bh: 10, scaleApplied: 1.0, captureX: 0, captureY: 0,
            winLeft: 0, winTop: 0, winWidth: 100, winHeight: 100);
        Assert.Equal(50, c.ScreenX); // 40 + 20/2
        Assert.Equal(45, c.ScreenY); // 40 + 10/2
        Assert.Equal(0.5, c.XPct, 5);
        Assert.Equal(0.45, c.YPct, 5);
    }
}
