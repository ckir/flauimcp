using System.Linq;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class CaptureGeometryShapeTests
{
    // Positional record, same append-only rule as CaptureResult and for the same reason.
    [Fact]
    public void The_positional_order_is_append_only()
    {
        var ctor = typeof(CaptureGeometry).GetConstructors().Single();
        Assert.Equal(new[]
        {
            "Bounds", "MaskRects", "Minimized", "Denied", "DeniedProcess", "Escalations",
            "WindowBounds", "NativeWindowHandle", "DegenerateWindow",
        }, ctor.GetParameters().Select(p => p.Name).ToArray());
    }

    // THE DISTINCTION THAT MAKES THE RETRY LOOP WORK. Both surface to the agent as
    // ElementNotActionable, but one is a transient worth retrying and the other never will be.
    [Fact]
    public void Degenerate_and_minimized_are_separate_flags()
    {
        var degenerate = new CaptureGeometry(default, System.Array.Empty<System.Drawing.Rectangle>(),
            Minimized: false, Denied: false, null, System.Array.Empty<MaskEscalationEntry>(),
            default, System.IntPtr.Zero, DegenerateWindow: true);
        var minimized = new CaptureGeometry(default, System.Array.Empty<System.Drawing.Rectangle>(),
            Minimized: true, Denied: false, null, System.Array.Empty<MaskEscalationEntry>(),
            default, System.IntPtr.Zero, DegenerateWindow: false);

        Assert.True(degenerate.DegenerateWindow);
        Assert.False(degenerate.Minimized);
        Assert.True(minimized.Minimized);
        Assert.False(minimized.DegenerateWindow);
    }

    // A headless test can construct one with ANY handle value and a fake acquisition, and every crop,
    // yardstick and mask assertion still runs -- nothing between the walk and the seam dereferences it.
    // That is what keeps the HWND from breaking headless testability.
    [Fact]
    public void A_synthetic_handle_is_carried_through_untouched()
    {
        var geo = new CaptureGeometry(new System.Drawing.Rectangle(10, 20, 30, 40),
            System.Array.Empty<System.Drawing.Rectangle>(), false, false, null,
            System.Array.Empty<MaskEscalationEntry>(),
            new System.Drawing.Rectangle(0, 0, 100, 200), new System.IntPtr(0xDEAD), false);
        Assert.Equal(new System.IntPtr(0xDEAD), geo.NativeWindowHandle);
        Assert.Equal(new System.Drawing.Rectangle(0, 0, 100, 200), geo.WindowBounds);
        Assert.Equal(new System.Drawing.Rectangle(10, 20, 30, 40), geo.Bounds);   // the ELEMENT rect
    }

    // ⚠ The OCR path is the THIRD caller of the geometry walk and had no degenerate guard, so a window
    // that was degenerate at walk time and valid at capture time was OCR'd with an EMPTY mask set --
    // returning redacted text as plaintext. This pins that the flag exists for that caller to act on.
    [Fact]
    public void A_degenerate_geometry_is_distinguishable_by_a_caller_that_must_refuse()
    {
        var degenerate = new CaptureGeometry(default, System.Array.Empty<System.Drawing.Rectangle>(),
            Minimized: false, Denied: false, null, System.Array.Empty<MaskEscalationEntry>(),
            default, System.IntPtr.Zero, DegenerateWindow: true);

        // The exact test the OCR wrapper performs. Denied/Minimized alone would let this through.
        Assert.False(degenerate.Denied || degenerate.Minimized);
        Assert.True(degenerate.DegenerateWindow);
        Assert.Empty(degenerate.MaskRects);   // ...and this is what would have been OCR'd in the clear
    }
}
