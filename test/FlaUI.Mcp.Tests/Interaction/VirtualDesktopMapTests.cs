using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class VirtualDesktopMapTests
{
    // virtual screen origin (0,0), size 1920x1080 (the spike box).
    [Theory]
    [InlineData(0, 0, 0, 0)]                 // top-left
    [InlineData(1919, 1079, 65535, 65535)]   // bottom-right maps to max
    [InlineData(960, 540, 32785, 32798)]     // center = round((px - origin) * 65535 / (size-1)); plan's literal 32777,32784 was a hand-calc error
    public void Maps_physical_to_absolute_0_65535(int px, int py, int ax, int ay)
    {
        var (gotX, gotY) = VirtualDesktopMap.ToAbsolute(px, py, originX: 0, originY: 0, width: 1920, height: 1080);
        Assert.Equal(ax, gotX);
        Assert.Equal(ay, gotY);
    }

    [Fact]
    public void Honors_a_negative_virtual_origin_secondary_monitor_left_of_primary()
    {
        // virtual screen from x=-1920 width 3840: a point at x=-1920 maps to 0.
        var (gotX, _) = VirtualDesktopMap.ToAbsolute(-1920, 0, originX: -1920, originY: 0, width: 3840, height: 1080);
        Assert.Equal(0, gotX);
    }
}
