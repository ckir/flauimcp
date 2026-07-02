using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

// Each test launches its OWN TestApp: these MUTATE the list, so a shared fixture would leak state.
// Pure UIA tree ops (no SendInput) -> Desktop but NOT SyntheticInput; runs over RDP.
[Trait("Category", "Desktop")]
public class StrictResolutionTests
{
    private static string RefFor(string tree, string aid)
    {
        foreach (var line in tree.Split('\n'))
            if (line.Contains("aid=" + aid))
            { int lb = line.IndexOf('['), rb = line.IndexOf(']'); return line.Substring(lb + 1, rb - lb - 1); }
        throw new Xunit.Sdk.XunitException($"no ref for aid={aid}");
    }

    private static void Click(WindowManager mgr, WindowHandle h, string aid) =>
        mgr.RunWithWindowAndDesktopAsync(h, (win, _) =>
        {
            (win.FindFirstDescendant(cf => cf.ByAutomationId(aid))
                ?? throw new Xunit.Sdk.XunitException($"control {aid} not found")).AsButton().Invoke();
            return true;
        }).GetAwaiter().GetResult();

    // INV-8: a state-changing path must REFUSE a recycled element (same AutomationId, new RuntimeId)
    // that the LENIENT read path would happily rebind — no silent retarget of a destructive action.
    [Fact]
    public async Task Strict_resolution_refuses_a_recycled_element_that_lenient_would_rebind()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var handle = await mgr.OpenByPidAsync(app.Process.Id);

        var snap = await perception.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });
        var refB = RefFor(snap.Tree, "ItemB");

        Click(mgr, handle, "RebuildItemsButton"); // destroys + recreates ItemB with a NEW RuntimeId
        await Task.Delay(300);

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            perception.RunOnRefActionAsync(handle, refB, el => { Interactor.SetFocus(el); return true; }, timeoutMs: 5000));
        Assert.Equal(ToolErrorCode.RefStaleUnresolvable, ex.Code);

        var aid = await perception.RunOnRefAsync(handle, refB, el => el.AutomationId);
        Assert.Equal("ItemB", aid); // lenient read still recovers it (documents the asymmetry)
    }

    // Regression: strict must NOT over-refuse — an UNCHANGED element (RuntimeId intact) still acts.
    [Fact]
    public async Task Strict_resolution_acts_on_an_unchanged_element()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var handle = await mgr.OpenByPidAsync(app.Process.Id);

        var snap = await perception.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });
        var okRef = RefFor(snap.Tree, "OkButton");

        await perception.RunOnRefActionAsync(handle, okRef, el => { Interactor.Invoke(el); return true; }, timeoutMs: 5000);

        var status = await perception.RunOnRefAsync(handle, RefFor(snap.Tree, "Status"), el => el.Name);
        Assert.StartsWith("clicked", status);
    }
}
