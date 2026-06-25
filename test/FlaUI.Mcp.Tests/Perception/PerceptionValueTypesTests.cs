using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class PerceptionValueTypesTests
{
    [Fact]
    public void SnapshotOptions_has_perception_friendly_defaults()
    {
        var o = new SnapshotOptions();
        Assert.True(o.InteractiveOnly);
        Assert.False(o.FullProperties);
        Assert.Equal(40, o.MaxDepth);
        Assert.Null(o.RootRef);
    }

    [Fact]
    public void ElementDescriptor_carries_its_option_c_keys()
    {
        var d = new ElementDescriptor(
            RuntimeId: new[] { 7, 42 },
            ControlType: FlaUI.Core.Definitions.ControlType.Button,
            AutomationId: "OkButton",
            Name: "OK",
            AncestorAutomationId: "MainWindow",
            IndexPath: new[] { 0, 1 });
        Assert.Equal("OkButton", d.AutomationId);
        Assert.Equal("MainWindow", d.AncestorAutomationId);
        Assert.Equal(new[] { 0, 1 }, d.IndexPath);
    }
}
