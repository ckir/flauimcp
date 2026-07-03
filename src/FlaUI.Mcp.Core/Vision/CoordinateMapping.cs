namespace FlaUI.Mcp.Core.Vision;

/// <summary>A mapped point: physical screen px (pairs with desktop_get_bounds) + window-relative fractions
/// (pairs with desktop_click_at's xPct/yPct).</summary>
public readonly record struct MappedPoint(int ScreenX, int ScreenY, double XPct, double YPct);

/// <summary>Pure inverse of the screenshot pipeline (Phase 9 §6 — THE dealbreaker). OCR reports pixels in the
/// DOWNSCALED capture bitmap, offset by the capture origin. This maps them back to physical screen px and then to
/// the target window's fractional coordinates. The origin term is the CAPTURE rect's top-left (CaptureResult.X/Y) —
/// NOT the window rect — because a straddling/off-screen window is captured cropped to visible bounds (§6 Seat D).
/// The window rect (winLeft/winTop/winWidth/winHeight) is the FULL window rect, used only for the fraction.</summary>
public static class CoordinateMapping
{
    public static MappedPoint BitmapToWindowPct(
        double bitmapX, double bitmapY, double scaleApplied, int captureX, int captureY,
        int winLeft, int winTop, int winWidth, int winHeight)
    {
        // Undo the downscale, then add the capture origin -> physical screen px.
        double s = scaleApplied > 0 ? scaleApplied : 1.0; // defensive: real callers always pass >0 (CaptureResult.ScaleApplied); guard against NaN/Inf
        double screenXd = captureX + bitmapX / s;
        double screenYd = captureY + bitmapY / s;
        int screenX = (int)System.Math.Round(screenXd);
        int screenY = (int)System.Math.Round(screenYd);
        // Fraction of the FULL window rect (clamped so an off-window point doesn't produce an out-of-[0,1] click).
        double xPct = winWidth  <= 0 ? 0.0 : (screenXd - winLeft) / winWidth;
        double yPct = winHeight <= 0 ? 0.0 : (screenYd - winTop) / winHeight;
        return new MappedPoint(screenX, screenY, Clamp01(xPct), Clamp01(yPct));
    }

    public static MappedPoint BitmapRectCenterToWindowPct(
        double bx, double by, double bw, double bh, double scaleApplied, int captureX, int captureY,
        int winLeft, int winTop, int winWidth, int winHeight)
        => BitmapToWindowPct(bx + bw / 2.0, by + bh / 2.0, scaleApplied, captureX, captureY,
                             winLeft, winTop, winWidth, winHeight);

    private static double Clamp01(double v) => v < 0.0 ? 0.0 : v > 1.0 ? 1.0 : v;
}
