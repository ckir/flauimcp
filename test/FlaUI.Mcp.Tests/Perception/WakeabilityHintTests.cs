using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class WakeabilityHintTests
{
    [Theory]
    [InlineData("Chrome_WidgetWin_1", 3, true)]    // Chromium class + collapsed tree -> wakeable
    [InlineData("Chrome_WidgetWin_0", 5, true)]    // Chromium variant
    [InlineData("Chrome_WidgetWin_1", 200, false)] // Chromium but ALREADY accessible (rich tree) -> omit
    [InlineData("Notepad", 3, false)]              // non-Chromium opaque (e.g. a game) -> not wakeable
    [InlineData(null, 3, false)]                   // no class -> not wakeable
    [InlineData("", 3, false)]
    public void Wakeable_iff_chromium_class_and_collapsed_tree(string? className, int nodeCount, bool expected)
        => Assert.Equal(expected, WakeabilityHint.IsWakeable(className, nodeCount));

    [Fact]
    public void Threshold_is_the_collapse_boundary()
    {
        // opaque VS Code ~<=15 nodes; hydrated ~236. The boundary is CollapsedNodeThreshold.
        Assert.True(WakeabilityHint.IsWakeable("Chrome_WidgetWin_1", WakeabilityHint.CollapsedNodeThreshold));
        Assert.False(WakeabilityHint.IsWakeable("Chrome_WidgetWin_1", WakeabilityHint.CollapsedNodeThreshold + 1));
    }
}
