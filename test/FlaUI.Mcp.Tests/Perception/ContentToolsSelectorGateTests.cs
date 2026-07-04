using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

// Non-Desktop: RequireExactlyOne fires BEFORE any UIA/perception work, so null! deps prove the gate —
// not the underlying tool — produced the error. Mirrors InputToolsSelectorGateTests
// (test/FlaUI.Mcp.Tests/Interaction/InputToolsContractTests.cs). ReadOnly:false on the ServerOptions so
// the read-only short circuit doesn't mask the gate for the Destructive tool (DesktopGridSelect).
public class ContentToolsSelectorGateTests
{
    private static ContentTools Make() =>
        new(perception: null!, windows: null!, new ServerOptions(ReadOnly: false, AllowElevation: false));

    [Fact]
    public async Task GetGridCell_neither_ref_nor_selector_is_InvalidArguments()
    {
        var tools = Make();
        var result = await tools.DesktopGetGridCell("w1", row: 0, col: 0);
        Assert.Contains("InvalidArguments", result);
    }

    [Fact]
    public async Task GetGridCell_both_ref_and_selector_is_InvalidArguments()
    {
        var tools = Make();
        var result = await tools.DesktopGetGridCell("w1", row: 0, col: 0, @ref: "e1", selector: new Selector(AutomationId: "Grid"));
        Assert.Contains("InvalidArguments", result);
    }

    [Fact]
    public async Task GridSelect_neither_ref_nor_selector_is_InvalidArguments()
    {
        var tools = Make();
        var result = await tools.DesktopGridSelect("w1", row: 0, col: 0);
        Assert.Contains("InvalidArguments", result);
    }

    [Fact]
    public async Task GridSelect_both_ref_and_selector_is_InvalidArguments()
    {
        var tools = Make();
        var result = await tools.DesktopGridSelect("w1", row: 0, col: 0, @ref: "e1", selector: new Selector(AutomationId: "Grid"));
        Assert.Contains("InvalidArguments", result);
    }

    [Fact]
    public async Task GetText_neither_ref_nor_selector_is_InvalidArguments()
    {
        var tools = Make();
        var result = await tools.DesktopGetText("w1");
        Assert.Contains("InvalidArguments", result);
    }

    [Fact]
    public async Task GetText_both_ref_and_selector_is_InvalidArguments()
    {
        var tools = Make();
        var result = await tools.DesktopGetText("w1", @ref: "e1", selector: new Selector(AutomationId: "Doc"));
        Assert.Contains("InvalidArguments", result);
    }
}
