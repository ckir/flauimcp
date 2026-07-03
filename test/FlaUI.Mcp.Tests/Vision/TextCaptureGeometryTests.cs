using System.Drawing;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Vision;

/// <summary>Phase 9 Task 10 review: pure region-validation for desktop_find_text's optional [xPct,yPct,wPct,hPct]
/// region. Rejects zero-area and window-overflowing regions with ToolException(InvalidArguments) instead of
/// leaking an ArgumentException/INTERNAL error or capturing off-window.</summary>
public class TextCaptureGeometryTests
{
    [Fact]
    public void Null_region_returns_the_window_rect_unchanged()
    {
        var win = new Rectangle(100, 100, 400, 300);
        Assert.Equal(win, TextCaptureGeometry.ComputeCaptureBounds(win, null));
    }

    [Fact]
    public void Valid_interior_region_maps_to_the_exact_absolute_sub_rectangle()
    {
        var win = new Rectangle(100, 100, 400, 300);
        var r = TextCaptureGeometry.ComputeCaptureBounds(win, new[] { 0.25, 0.5, 0.5, 0.25 });
        Assert.Equal(new Rectangle(200, 250, 200, 75), r); // x=100+100, y=100+150, w=200, h=75
    }

    [Fact]
    public void Zero_area_region_throws_InvalidArguments()
    {
        var win = new Rectangle(100, 100, 400, 300);
        var ex = Assert.Throws<ToolException>(() => TextCaptureGeometry.ComputeCaptureBounds(win, new[] { 0.5, 0.5, 0.0, 0.0 }));
        Assert.Equal(ToolErrorCode.InvalidArguments, ex.Code);
    }

    [Fact]
    public void Overflowing_region_throws_InvalidArguments()
    {
        var win = new Rectangle(100, 100, 400, 300);
        var ex = Assert.Throws<ToolException>(() => TextCaptureGeometry.ComputeCaptureBounds(win, new[] { 0.9, 0.9, 0.5, 0.5 }));
        Assert.Equal(ToolErrorCode.InvalidArguments, ex.Code);
    }

    [Fact]
    public void Wrong_length_region_throws_InvalidArguments()
    {
        var win = new Rectangle(100, 100, 400, 300);
        var ex = Assert.Throws<ToolException>(() => TextCaptureGeometry.ComputeCaptureBounds(win, new[] { 0.0, 0.0, 1.0 }));
        Assert.Equal(ToolErrorCode.InvalidArguments, ex.Code);
    }

    [Fact]
    public void Out_of_range_fraction_throws_InvalidArguments()
    {
        var win = new Rectangle(100, 100, 400, 300);
        var ex = Assert.Throws<ToolException>(() => TextCaptureGeometry.ComputeCaptureBounds(win, new[] { 0.0, 0.0, 1.5, 0.5 }));
        Assert.Equal(ToolErrorCode.InvalidArguments, ex.Code);
    }
}
