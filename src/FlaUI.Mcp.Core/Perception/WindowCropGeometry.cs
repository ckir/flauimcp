using System.Drawing;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>The three rectangles a crop produces. Sizes are equal by construction.</summary>
public readonly record struct CropGeometry(Rectangle Effective, Rectangle Absolute, Rectangle Reported);

/// <summary>The crop GEOMETRY -- a PURE function of (bitmap size, E, W1, W2). Rectangles in, rectangles
/// out: no OS handle, no pixels, no allocation. This is what makes the element-crop invariant and the
/// clamp path HEADLESS-testable, which the spec's Testing section requires; if this logic lived inside
/// the component that calls GetWindowRect and PrintWindow, those tests could not exist.
///
/// Crop EXTRACTION -- `src = bitmap cropped to Effective` -- is a separate, decision-free bitmap
/// operation that lives in the seam. Every rectangle it uses was already computed here.</summary>
public static class WindowCropGeometry
{
    /// <summary>Compute the crop. Returns null when the intersection has no AREA, which the caller turns
    /// into a defined refusal.</summary>
    /// <param name="bitmap">The PrintWindow bitmap's size. Its (0,0) corresponds to W2's top-left.</param>
    /// <param name="e">The element rect from the UIA walk, absolute screen coords. EQUALS w1 for window
    /// scope -- there is no window-scope special case, and adding one caused three separate defects.</param>
    /// <param name="w1">The window rect from that SAME walk. Anchors BOTH mask-side translations.</param>
    /// <param name="w2">The GetWindowRect taken at capture time. Anchors ONLY the reported origin.</param>
    public static CropGeometry? Compute(Size bitmap, Rectangle e, Rectangle w1, Rectangle w2)
    {
        // W1 on the way IN. Using W2 here would misalign the crop by the movement delta on a pure move.
        var relative = new Rectangle(e.X - w1.X, e.Y - w1.Y, e.Width, e.Height);

        var effective = Rectangle.Intersect(relative, new Rectangle(0, 0, bitmap.Width, bitmap.Height));

        // ⚠ EXTENTS, not IsEmpty. Rectangle.Intersect compares with >= and so yields a zero-extent rect
        // at NON-ZERO coordinates -- (100,50,0,30) -- for two rects that merely touch along an edge, and
        // IsEmpty is FALSE there. MEASURED on this runtime. The existing yardstick guard at
        // PerceptionManager.cs:947 tests exactly this way, for exactly this reason.
        if (effective.Width <= 0 || effective.Height <= 0) return null;

        // Clamp ONCE, then derive BOTH operands from that one rectangle, so they cannot drift. An earlier
        // draft clamped the crop while handing Encode the UNCLAMPED bounds: the scale factor then came off
        // the clamped src.Width while masks translated against the unclamped origin -- the very
        // misalignment this guard exists to prevent, reintroduced by its own fix.
        return new CropGeometry(
            effective,
            // W1 on the way BACK for the MASK rectangle. Encode's arithmetic is absolute and the masks
            // were sampled alongside E, so they must meet at the same origin.
            new Rectangle(effective.X + w1.X, effective.Y + w1.Y, effective.Width, effective.Height),
            // W2 for the REPORTED rectangle -- where the pixels actually are. These two origins differ by
            // exactly the movement delta, and conflating them makes the response lie about a moved window.
            new Rectangle(effective.X + w2.X, effective.Y + w2.Y, effective.Width, effective.Height));
    }
}
