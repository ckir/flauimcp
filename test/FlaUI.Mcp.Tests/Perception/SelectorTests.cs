// test/FlaUI.Mcp.Tests/Perception/SelectorTests.cs
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class SelectorTests
{
    [Fact]
    public void Validate_accepts_any_single_material_field()
    {
        new Selector(AutomationId: "num5Button").Validate();   // no throw
        new Selector(Name: "Five").Validate();
        new Selector(ControlType: "Button").Validate();
    }

    [Fact]
    public void Validate_rejects_no_material_field()
    {
        var ex = Assert.Throws<ToolException>(() => new Selector(Scope: "e12").Validate());
        Assert.Equal(ToolErrorCode.InvalidArguments, ex.Code);
    }

    [Fact]
    public void Validate_rejects_unknown_controlType()
    {
        var ex = Assert.Throws<ToolException>(() => new Selector(ControlType: "Widget").Validate());
        Assert.Equal(ToolErrorCode.InvalidArguments, ex.Code);
    }

    [Fact]
    public void ToFindQuery_defaults_ignoreCase_true()
    {
        Assert.True(new Selector(Name: "Five").ToFindQuery().IgnoreCase);
        Assert.False(new Selector(Name: "Five", IgnoreCase: false).ToFindQuery().IgnoreCase);
    }
}
