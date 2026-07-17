using System.Linq;
using System.Reflection;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class DragCrossWindowClampTests
{
    [Fact]
    public void DesktopDrag_exposes_an_endWindow_for_cross_window_drag_while_retaining_the_single_window_clamp()
    {
        // Assert 1: the single-window [0,1] clamp is INTENTIONALLY retained — Option B (endWindow) adds a
        // cross-window drag MODE, it does not remove or loosen the existing same-window pct clamp.
        var ex = Record.Exception(() =>
            CoordinateMath.PctToPhysical(left: 0, top: 0, width: 683, height: 768,
                xPct: 1.445, yPct: 0.55));

        var toolEx = Assert.IsType<ToolException>(ex);
        Assert.Equal(ToolErrorCode.InvalidArguments, toolEx.Code);

        // Assert 2: the cross-window mode now exists on the tool contract — DesktopDrag exposes an optional
        // `endWindow` (defaults to null, i.e. same-window behavior when omitted) alongside the existing
        // window-relative pct parameters.
        var method = typeof(InputTools).GetMethod(nameof(InputTools.DesktopDrag));
        Assert.NotNull(method);

        var endWindowParam = method!.GetParameters().SingleOrDefault(p => p.Name == "endWindow");
        Assert.NotNull(endWindowParam);
        Assert.Equal(typeof(string), endWindowParam!.ParameterType);
        Assert.True(endWindowParam.IsOptional);
        Assert.True(endWindowParam.HasDefaultValue);
        Assert.Null(endWindowParam.DefaultValue);

        var description = method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description;
        Assert.NotNull(description);
        Assert.Contains("endWindow", description);

        // End-to-end cross-window drag behavior (the SendInput sink already re-verifies two distinct roots;
        // only pct routing changed) is verified by live dogfooding, not this headless unit test.
    }
}
