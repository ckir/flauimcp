using System.Linq;
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>Capture geometry for OCR text-finding (Phase 9 §6 Task 10): the capture rect (whole window, or a
/// region sub-rect of it) PLUS the full window physical rect (needed separately so a region's fractions still
/// map against the WHOLE window, not just the cropped capture — CoordinateMapping's winLeft/winTop/winWidth/
/// winHeight parameters). Denied/Minimized mirror CaptureGeometry so the tool can convert them the same way
/// ScreenshotTools does (TargetDenied / ElementNotActionable).</summary>
public sealed record TextCaptureGeometry(
    bool Denied, string? DeniedProcess, bool Minimized,
    System.Drawing.Rectangle CaptureBounds, System.Collections.Generic.IReadOnlyList<System.Drawing.Rectangle> PasswordRects,
    int WindowLeft, int WindowTop, int WindowWidth, int WindowHeight)
{
    /// <summary>Given the FULL window physical rect and an optional [xPct,yPct,wPct,hPct] region (fractions in
    /// (0,1]), returns the absolute capture rect. Null region -> the full window rect. Throws
    /// ToolException(InvalidArguments) on a malformed region (wrong length, any fraction &lt;0 or &gt;1, zero-area
    /// w/h, or a region extending past the window). Pure — headless-testable without a live window.</summary>
    public static System.Drawing.Rectangle ComputeCaptureBounds(System.Drawing.Rectangle windowRect, double[]? region)
    {
        if (region is null) return windowRect;
        if (region.Length != 4)
            throw new ToolException(ToolErrorCode.InvalidArguments,
                "region must be [xPct,yPct,wPct,hPct] (4 fractions).", "pass 4 fractions in [0,1]");
        if (region.Any(v => v < 0.0 || v > 1.0))
            throw new ToolException(ToolErrorCode.InvalidArguments,
                "region fractions must each be in [0,1].", "pass 4 fractions in [0,1]");
        if (region[2] <= 0.0 || region[3] <= 0.0)
            throw new ToolException(ToolErrorCode.InvalidArguments,
                "region width/height must be > 0.", "pass a non-empty region (wPct>0, hPct>0)");
        const double eps = 1e-9;
        if (region[0] + region[2] > 1.0 + eps || region[1] + region[3] > 1.0 + eps)
            throw new ToolException(ToolErrorCode.InvalidArguments,
                "region extends past the window (xPct+wPct or yPct+hPct > 1).", "keep the region within [0,1]");
        int x = windowRect.X + (int)System.Math.Round(region[0] * windowRect.Width);
        int y = windowRect.Y + (int)System.Math.Round(region[1] * windowRect.Height);
        int w = (int)System.Math.Round(region[2] * windowRect.Width);
        int h = (int)System.Math.Round(region[3] * windowRect.Height);
        if (w <= 0 || h <= 0)
            throw new ToolException(ToolErrorCode.InvalidArguments,
                "region resolves to a zero-area rectangle for this window size.", "use a larger region");
        return new System.Drawing.Rectangle(x, y, w, h);
    }
}
