using System;
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Maps a window-relative fractional point (spec §5: xPct/yPct in [0,1], relative to the
/// target window's physical bounding rect) to a physical screen pixel. Pure given the rect.</summary>
public static class CoordinateMath
{
    public static (int px, int py) PctToPhysical(int left, int top, int width, int height, double xPct, double yPct)
    {
        if (xPct is < 0.0 or > 1.0 || yPct is < 0.0 or > 1.0)
            throw new ToolException(ToolErrorCode.InvalidArguments,
                $"Coordinate fractions must be in [0,1]; got ({xPct},{yPct}).",
                "pass xPct/yPct as fractions of the window's width/height");
        int px = left + (int)Math.Round(xPct * width);
        int py = top + (int)Math.Round(yPct * height);
        return (px, py);
    }
}
