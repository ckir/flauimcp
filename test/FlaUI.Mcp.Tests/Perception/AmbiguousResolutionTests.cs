using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

// Pure UIA tree ops (no SendInput) -> Desktop but NOT SyntheticInput; runs over RDP.
// These do NOT mutate the app, so a shared fixture is fine.
[Trait("Category", "Desktop")]
public class AmbiguousResolutionTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public AmbiguousResolutionTests(TestAppFixture app) => _app = app;

    private async Task AssertAmbiguous(ElementDescriptor descriptor)
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var refs = new RefRegistry();
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        refs.BeginSnapshot(handle.Id);
        var refX = refs.Register(handle.Id, descriptor, cached: null);

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
                refs.Resolve(handle.Id, refX, new AutomationElement[] { win }).AutomationId));
        Assert.Equal(ToolErrorCode.AmbiguousMatch, ex.Code);
    }

    [Fact]
    public Task Duplicate_AutomationId_yields_AMBIGUOUS_MATCH() =>
        AssertAmbiguous(new ElementDescriptor(
            RuntimeId: System.Array.Empty<int>(),
            ControlType: FlaUI.Core.Definitions.ControlType.Button,
            AutomationId: "DupAid", Name: "DupA", AncestorAutomationId: "DupHost",
            IndexPath: System.Array.Empty<int>()));

    [Fact]
    public Task Duplicate_Name_and_ControlType_yields_AMBIGUOUS_MATCH() =>
        AssertAmbiguous(new ElementDescriptor(
            RuntimeId: System.Array.Empty<int>(),
            ControlType: FlaUI.Core.Definitions.ControlType.Button,
            AutomationId: "", Name: "DupName", AncestorAutomationId: "DupHost",
            IndexPath: System.Array.Empty<int>()));

    // MAJOR-2: AncestorAutomationId matching TWO ancestors, each holding a same-aid target.
    [Fact]
    public Task Duplicate_ancestor_scope_yields_AMBIGUOUS_MATCH() =>
        AssertAmbiguous(new ElementDescriptor(
            RuntimeId: System.Array.Empty<int>(),
            ControlType: FlaUI.Core.Definitions.ControlType.Button,
            AutomationId: "RowBtn", Name: "Row1Btn", AncestorAutomationId: "DupRow",
            IndexPath: System.Array.Empty<int>()));
}
