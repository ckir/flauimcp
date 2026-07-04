// test/FlaUI.Mcp.Tests/Perception/SelectorResolveTests.cs
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class SelectorResolveTests
{
    private static async Task<(WindowManager mgr, PerceptionManager perception, WindowHandle handle)>
        OpenAsync(TestAppFixture app, AutomationDispatcher dispatcher)
    {
        var mgr = new WindowManager(dispatcher);
        var refs = new RefRegistry();
        var perception = new PerceptionManager(mgr, refs, new SnapshotCache());
        var handle = await mgr.OpenByPidAsync(app.Process.Id);
        return (mgr, perception, handle);
    }

    [Fact]
    public async Task Selector_resolves_unique_automationId_and_survives_snapshot_churn()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        var (mgr, perception, handle) = await OpenAsync(app, dispatcher);
        using (mgr)
        {
            var sel = new Selector(AutomationId: "OkButton");

            var (value, resolvedRef) = await perception.RunOnSelectorActionAsync(
                handle, sel, el => el.AutomationId, 4000);
            Assert.Equal("OkButton", value);
            Assert.StartsWith("e", resolvedRef);

            // Renumber refs out from under any held eN - a selector must re-resolve fresh, not rely on
            // a stale ref surviving the churn.
            await perception.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });

            var (value2, resolvedRef2) = await perception.RunOnSelectorActionAsync(
                handle, sel, el => el.AutomationId, 4000);
            Assert.Equal("OkButton", value2);
            Assert.StartsWith("e", resolvedRef2);
        }
    }

    [Fact]
    public async Task Selector_ambiguous_controlType_throws_AmbiguousMatch()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        var (mgr, perception, handle) = await OpenAsync(app, dispatcher);
        using (mgr)
        {
            var sel = new Selector(ControlType: "Button");
            var ex = await Assert.ThrowsAsync<ToolException>(() =>
                perception.RunOnSelectorActionAsync(handle, sel, el => el.AutomationId, 4000));
            Assert.Equal(ToolErrorCode.AmbiguousMatch, ex.Code);
        }
    }

    [Fact]
    public async Task Selector_no_match_throws_SelectorNoMatch()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        var (mgr, perception, handle) = await OpenAsync(app, dispatcher);
        using (mgr)
        {
            var sel = new Selector(AutomationId: "NoSuchControl_zzz");
            var ex = await Assert.ThrowsAsync<ToolException>(() =>
                perception.RunOnSelectorActionAsync(handle, sel, el => el.AutomationId, 4000));
            Assert.Equal(ToolErrorCode.SelectorNoMatch, ex.Code);
        }
    }
}
