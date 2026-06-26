using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

[Trait("Category", "Desktop")]
public class InteractorTests
{
    // Resolve a descendant on the window the active query-STA callback ALREADY provides.
    // This must NOT open a NEW query-STA dispatch: the single long-lived query STA is busy
    // running the enclosing callback, so a nested RunWith... enqueues work the STA can never
    // pick up -> deterministic self-deadlock (the cause of the full-suite hang here).
    private static AutomationElement Find(AutomationElement win, string aid) =>
        win.FindFirstDescendant(cf => cf.ByAutomationId(aid))
        ?? throw new Xunit.Sdk.XunitException($"{aid} not found");

    [Fact]
    public async Task Invoke_clicks_a_button()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(app.Process.Id);
        await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        { Interactor.Invoke(Find(win, "OkButton")); return true; });
        var status = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            win.FindFirstDescendant(cf => cf.ByAutomationId("Status"))!.Name);
        Assert.StartsWith("clicked", status); // TestApp handler sets "clicked: {Input.Text}"
    }

    [Fact]
    public async Task SetValue_writes_text_into_a_value_control()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(app.Process.Id);
        await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        { Interactor.SetValue(Find(win, "Input"), "hello"); return true; });
        var val = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            win.FindFirstDescendant(cf => cf.ByAutomationId("Input"))!.AsTextBox().Text);
        Assert.Equal("hello", val);
    }

    [Fact]
    public async Task Invoke_on_an_element_without_the_pattern_throws_PatternUnsupported()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(app.Process.Id);
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            { Interactor.Invoke(Find(win, "Status")); return true; })); // Text has no InvokePattern
        Assert.Equal(ToolErrorCode.PatternUnsupported, ex.Code);
    }
}
