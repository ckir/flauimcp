using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

// Each test launches its OWN TestApp: these tests MUTATE the list, so a shared IClassFixture would
// leak state across tests and make them order-dependent.
[Trait("Category", "Desktop")]
public class RefResolutionTests
{
    // Snapshot with fullProperties, then find the ref whose line carries aid=<automationId>.
    private static string RefFor(string tree, string automationId)
    {
        foreach (var line in tree.Split('\n'))
            if (line.Contains("aid=" + automationId))
            {
                int lb = line.IndexOf('['), rb = line.IndexOf(']');
                return line.Substring(lb + 1, rb - lb - 1);
            }
        throw new Xunit.Sdk.XunitException($"no ref line for aid={automationId} in:\n{tree}");
    }

    private static void Invoke(WindowManager mgr, WindowHandle h, string automationId) =>
        mgr.RunWithWindowAndDesktopAsync(h, (win, _) =>
        {
            var el = win.FindFirstDescendant(cf => cf.ByAutomationId(automationId))
                     ?? throw new Xunit.Sdk.XunitException($"control {automationId} not found");
            el.AsButton().Invoke();
            return true;
        }).GetAwaiter().GetResult();

    [Fact]
    public async Task The_automationId_branch_recovers_a_ref_even_when_the_cached_element_is_dead()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry());
        var handle = await mgr.OpenByPidAsync(app.Process.Id);

        var snap = await perception.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });
        var refB = RefFor(snap.Tree, "ItemB");

        Invoke(mgr, handle, "RebuildItemsButton"); // destroys + recreates ItemB
        await Task.Delay(300);

        var aid = await perception.RunOnRefAsync(handle, refB, el => el.AutomationId);
        Assert.Equal("ItemB", aid);
    }

    [Fact]
    public async Task The_automationId_branch_ignores_a_stale_IndexPath()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var refs = new RefRegistry();
        var handle = await mgr.OpenByPidAsync(app.Process.Id);

        refs.BeginSnapshot(handle.Id);
        var descriptor = new ElementDescriptor(
            RuntimeId: Array.Empty<int>(),
            ControlType: FlaUI.Core.Definitions.ControlType.ListItem,
            AutomationId: "ItemB", Name: "B", AncestorAutomationId: "ItemList",
            IndexPath: new[] { 99 }); // wrong on purpose
        var refX = refs.Register(handle.Id, descriptor, cached: null);

        var aid = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            refs.Resolve(handle.Id, refX, new AutomationElement[] { win }).AutomationId);

        Assert.Equal("ItemB", aid);
    }

    [Fact]
    public async Task The_name_plus_controltype_branch_resolves_an_element_lacking_an_automationId()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var refs = new RefRegistry();
        var handle = await mgr.OpenByPidAsync(app.Process.Id);

        refs.BeginSnapshot(handle.Id);
        var descriptor = new ElementDescriptor(
            RuntimeId: Array.Empty<int>(),
            ControlType: FlaUI.Core.Definitions.ControlType.ListItem,
            AutomationId: "", Name: "NamedOnly", AncestorAutomationId: "ItemList",
            IndexPath: Array.Empty<int>());
        var refX = refs.Register(handle.Id, descriptor, cached: null);

        var name = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            refs.Resolve(handle.Id, refX, new AutomationElement[] { win }).Name);

        Assert.Equal("NamedOnly", name);
    }

    [Fact]
    public async Task A_genuinely_removed_element_yields_REF_STALE_UNRESOLVABLE()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry());
        var handle = await mgr.OpenByPidAsync(app.Process.Id);

        var snap = await perception.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });
        var refB = RefFor(snap.Tree, "ItemB");

        Invoke(mgr, handle, "ClearItemsButton"); // ItemB truly gone
        await Task.Delay(300);

        var ex = await Assert.ThrowsAsync<ToolException>(() => perception.RunOnRefAsync(handle, refB, el => el.Name));
        Assert.Equal(ToolErrorCode.RefStaleUnresolvable, ex.Code);
    }

    [Fact]
    public async Task A_new_snapshot_supersedes_the_prior_refs()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry());
        var handle = await mgr.OpenByPidAsync(app.Process.Id);

        var first = await perception.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });
        var oldRef = RefFor(first.Tree, "ItemB");
        await perception.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true }); // supersede

        var ex = await Assert.ThrowsAsync<ToolException>(() => perception.RunOnRefAsync(handle, oldRef, el => el.Name));
        Assert.Equal(ToolErrorCode.RefNotFound, ex.Code);
    }
}
