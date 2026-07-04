using System.Linq;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

// Headless (NOT Category=Desktop): pure reflection over the tool signature, no SendInput.
public class InputToolsContractTests
{
    [Fact]
    public void DesktopType_defaults_the_inter_key_delay_to_15ms()
    {
        var p = typeof(InputTools).GetMethod(nameof(InputTools.DesktopType))!
            .GetParameters().Single(x => x.Name == "interKeyDelayMs");
        Assert.Equal(15, p.DefaultValue);
    }

    [Fact]
    public void DesktopType_defaults_verify_to_true()
    {
        var p = typeof(InputTools).GetMethod(nameof(InputTools.DesktopType))!
            .GetParameters().Single(x => x.Name == "verify");
        Assert.Equal(true, p.DefaultValue);
    }

    [Fact]
    public void DesktopPasteText_is_declared_Destructive_with_expected_params()
    {
        var m = typeof(InputTools).GetMethod("DesktopPasteText");
        Assert.NotNull(m);
        var names = m!.GetParameters().Select(p => p.Name).ToArray();
        Assert.Contains("forceOverwriteClipboard", names);
        Assert.Contains("verify", names);
    }
}

// Non-Desktop: RequireExactlyOne (and DesktopKey's at-most-one variant) fires BEFORE any UIA/perception/
// InputGuard work, so null! deps prove the gate — not the underlying tool — produced the error.
// ReadOnly:false so the read-only short circuit doesn't mask this gate.
public class InputToolsSelectorGateTests
{
    private static InputTools Make() =>
        new(perception: null!, windows: null!, new ServerOptions(ReadOnly: false, AllowElevation: false),
            guard: null!, env: null!);

    [Fact]
    public async Task SetCaret_neither_ref_nor_selector_is_InvalidArguments()
    {
        var tools = Make();
        var result = await tools.DesktopSetCaret("w1", offset: 0);
        Assert.Contains("InvalidArguments", result);
    }

    [Fact]
    public async Task SetCaret_both_ref_and_selector_is_InvalidArguments()
    {
        var tools = Make();
        var result = await tools.DesktopSetCaret("w1", offset: 0, @ref: "e1", selector: new Selector(AutomationId: "Doc"));
        Assert.Contains("InvalidArguments", result);
    }

    [Fact]
    public async Task SelectTextRange_neither_ref_nor_selector_is_InvalidArguments()
    {
        var tools = Make();
        var result = await tools.DesktopSelectTextRange("w1", start: 0, length: 5);
        Assert.Contains("InvalidArguments", result);
    }

    [Fact]
    public async Task Click_both_ref_and_selector_is_InvalidArguments()
    {
        var tools = Make();
        var result = await tools.DesktopClick("w1", @ref: "e1", selector: new Selector(AutomationId: "Ok"));
        Assert.Contains("InvalidArguments", result);
    }

    [Fact]
    public async Task Click_neither_ref_nor_selector_is_InvalidArguments()
    {
        var tools = Make();
        var result = await tools.DesktopClick("w1");
        Assert.Contains("InvalidArguments", result);
    }

    [Fact]
    public async Task Key_both_ref_and_selector_is_at_most_one_InvalidArguments()
    {
        var tools = Make();
        var result = await tools.DesktopKey("Enter", @ref: "e1", window: "w1", selector: new Selector(AutomationId: "Ok"));
        Assert.Contains("InvalidArguments", result);
        Assert.Contains("at most one", result);
    }

    [Fact]
    public async Task Key_selector_without_window_is_InvalidArguments()
    {
        var tools = Make();
        var result = await tools.DesktopKey("Enter", selector: new Selector(AutomationId: "Ok"));
        Assert.Contains("InvalidArguments", result);
    }

    [Fact]
    public async Task Type_neither_ref_nor_selector_is_InvalidArguments()
    {
        var tools = Make();
        var result = await tools.DesktopType("w1", "hello");
        Assert.Contains("InvalidArguments", result);
    }

    [Fact]
    public async Task Type_both_ref_and_selector_is_InvalidArguments()
    {
        var tools = Make();
        var result = await tools.DesktopType("w1", "hello", @ref: "e1", selector: new Selector(AutomationId: "Input"));
        Assert.Contains("InvalidArguments", result);
    }

    [Fact]
    public async Task PasteText_neither_ref_nor_selector_is_InvalidArguments()
    {
        var tools = Make();
        var result = await tools.DesktopPasteText("w1", "hello");
        Assert.Contains("InvalidArguments", result);
    }

    [Fact]
    public async Task PasteText_both_ref_and_selector_is_InvalidArguments()
    {
        var tools = Make();
        var result = await tools.DesktopPasteText("w1", "hello", @ref: "e1", selector: new Selector(AutomationId: "Input"));
        Assert.Contains("InvalidArguments", result);
    }
}
