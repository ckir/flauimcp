using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class CoordinateMathTests
{
    [Theory]
    [InlineData(0.0, 0.0, 100, 200)]     // top-left corner of a rect at (100,200)
    [InlineData(1.0, 1.0, 740, 680)]     // bottom-right: 100+640=740, 200+480=680
    [InlineData(0.5, 0.5, 420, 440)]     // center: 100+320, 200+240
    public void Maps_pct_into_the_window_rect(double xp, double yp, int ex, int ey)
    {
        var (px, py) = CoordinateMath.PctToPhysical(left: 100, top: 200, width: 640, height: 480, xp, yp);
        Assert.Equal(ex, px);
        Assert.Equal(ey, py);
    }

    [Theory]
    [InlineData(-0.01, 0.5)]
    [InlineData(0.5, 1.01)]
    public void Rejects_out_of_range_fractions(double xp, double yp)
    {
        var ex = Assert.Throws<ToolException>(() => CoordinateMath.PctToPhysical(0, 0, 100, 100, xp, yp));
        Assert.Equal(ToolErrorCode.InvalidArguments, ex.Code);
    }
}
