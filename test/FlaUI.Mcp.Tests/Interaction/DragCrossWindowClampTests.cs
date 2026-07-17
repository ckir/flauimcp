using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

// fix-the-tool: docs/fix-the-tool-backlog/drag-single-window-pct-clamp.md
[Trait("Category", "KnownDefect")]
public class DragCrossWindowClampTests
{
    [Fact]
    public void PctToPhysical_hard_clamps_a_cross_window_endpoint_making_cross_app_drag_inexpressible()
    {
        // Arrange: two windows tiled side-by-side via Win+Left/Win+Right on a 1366-wide screen — the
        // SOURCE (left-half) window occupies left=0, width=683, height=768. A drop point inside the
        // RIGHT-half window, expressed as a fraction of the SOURCE window's own bounds (the only pct
        // space desktop_drag offers — both endpoints share one `window` handle), lands past x=1.0.
        const double crossWindowEndXPct = 1.445;
        const double crossWindowEndYPct = 0.55;

        // Invoke: the exact coordinate-resolution path desktop_drag's endXPct/endYPct run through
        // (InputTools.DesktopDrag -> ResolveWindowPctAsync -> CoordinateMath.PctToPhysical).
        var ex = Record.Exception(() =>
            CoordinateMath.PctToPhysical(left: 0, top: 0, width: 683, height: 768,
                xPct: crossWindowEndXPct, yPct: crossWindowEndYPct));

        var toolEx = Assert.IsType<ToolException>(ex);
        Assert.Equal(ToolErrorCode.InvalidArguments, toolEx.Code);

        Assert.Fail("drag-single-window-pct-clamp: desktop_drag hard-clamps both fractions to [0,1] " +
            $"(measured: endXPct={crossWindowEndXPct} -> {toolEx.Code} \"{toolEx.Message}\"), so a drop " +
            "point in ANOTHER window is inexpressible even when the two windows are tiled perfectly " +
            "side-by-side — cross-app drag-and-drop is structurally impossible with the current tool; " +
            "correct behavior not asserted yet — see docs/fix-the-tool-backlog/drag-single-window-pct-clamp.md");
    }
}
