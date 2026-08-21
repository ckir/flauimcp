using System.Drawing;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class EncodeContractTests
{
    private static Bitmap Solid(int w, int h, Color c)
    {
        var b = new Bitmap(w, h);
        using var g = Graphics.FromImage(b);
        using var brush = new SolidBrush(c);
        g.FillRectangle(brush, 0, 0, w, h);
        return b;
    }

    // THE AB-2 CASE, and it is leak-shaped. A WINDOW-sized bitmap cropped to an ELEMENT-sized rect: the
    // mask must land relative to the ELEMENT's origin, not the window's. Under the scrape this invariant
    // was structural; under PrintWindow it must be re-established by hand.
    [Fact]
    public void A_mask_lands_relative_to_the_absolute_rectangle_it_was_given()
    {
        // src is the already-cropped element region: 200x100 at absolute (300,400).
        using var src = Solid(200, 100, Color.White);
        var absolute = new Rectangle(300, 400, 200, 100);
        var mask = new Rectangle(350, 430, 50, 20);   // 50,30 inside the element

        var r = ScreenCapture.Encode(src, absolute, reported: absolute, new[] { mask }, maxWidth: 0,
                                     method: "printWindow", warnings: System.Array.Empty<CaptureWarning>());

        Assert.Equal(1, r.Redactions);
        using var ms = new System.IO.MemoryStream(r.Png);
        using var outBmp = new Bitmap(ms);
        Assert.Equal(Color.Black.ToArgb(), outBmp.GetPixel(60, 40).ToArgb());   // inside the mask
        Assert.Equal(Color.White.ToArgb(), outBmp.GetPixel(10, 10).ToArgb());   // outside it
    }

    // X/Y come from REPORTED (where the pixels are), never from ABSOLUTE (where the masks are). They
    // differ by exactly the movement delta whenever the window moved.
    [Fact]
    public void XY_report_the_W2_anchored_rectangle_not_the_mask_rectangle()
    {
        using var src = Solid(80, 60, Color.Gray);
        var absolute = new Rectangle(100, 200, 80, 60);
        var reported = new Rectangle(300, 500, 80, 60);

        var r = ScreenCapture.Encode(src, absolute, reported, System.Array.Empty<Rectangle>(), 0,
                                     "printWindow", System.Array.Empty<CaptureWarning>());

        Assert.Equal(300, r.X);
        Assert.Equal(500, r.Y);
        Assert.Equal(80, r.W);
        Assert.Equal(60, r.H);
    }

    [Fact]
    public void The_method_and_warnings_reach_the_result_unchanged()
    {
        using var src = Solid(10, 10, Color.Red);
        var warn = new[] { CaptureWarnings.For(CaptureWarnings.PopupsNotRendered) };
        var r = ScreenCapture.Encode(src, new Rectangle(0, 0, 10, 10), new Rectangle(0, 0, 10, 10),
                                     System.Array.Empty<Rectangle>(), 0, "printWindow", warn);
        Assert.Equal("printWindow", r.CaptureMethod);
        Assert.Single(r.CaptureWarnings);
        Assert.Equal("popupsNotRendered", r.CaptureWarnings[0].Code);
    }

    // THE CLAMP PATH. src is SMALLER than the element rect originally requested, because the crop clamped
    // it. Masks must still land correctly and nothing may throw. This is the regression test for the
    // defect panel round 2's own fix introduced.
    [Fact]
    public void A_clamped_src_still_places_masks_correctly()
    {
        using var src = Solid(120, 90, Color.White);            // clamped down from 200x100
        var absolute = new Rectangle(300, 400, 120, 90);        // derived FROM the clamped rect
        var mask = new Rectangle(320, 420, 30, 20);

        var r = ScreenCapture.Encode(src, absolute, absolute, new[] { mask }, 0, "printWindow",
                                     System.Array.Empty<CaptureWarning>());

        Assert.Equal(1, r.Redactions);
        using var ms = new System.IO.MemoryStream(r.Png);
        using var outBmp = new Bitmap(ms);
        Assert.Equal(Color.Black.ToArgb(), outBmp.GetPixel(25, 25).ToArgb());
    }
}
