using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class ActionResolutionTests
{
    private static string RefFor(string tree, string aid)
    {
        foreach (var line in tree.Split('\n'))
            if (line.Contains("aid=" + aid))
            { int lb = line.IndexOf('['), rb = line.IndexOf(']'); return line.Substring(lb + 1, rb - lb - 1); }
        throw new Xunit.Sdk.XunitException($"no ref for aid={aid}");
    }

    [Fact]
    public async Task Invoking_a_ref_on_the_action_STA_changes_UI_state()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry());
        var handle = await mgr.OpenByPidAsync(app.Process.Id);

        var snap = await perception.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });
        var okRef = RefFor(snap.Tree, "OkButton");

        await perception.RunOnRefActionAsync(handle, okRef, el => { Interactor.Invoke(el); return true; }, timeoutMs: 5000);

        var status = await perception.RunOnRefAsync(handle, RefFor(snap.Tree, "Status"), el => el.Name);
        Assert.StartsWith("clicked", status); // TestApp handler sets "clicked: {Input.Text}"
    }

    [Fact]
    public async Task An_offscreen_ref_is_rejected_before_invoking()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry());
        var handle = await mgr.OpenByPidAsync(app.Process.Id);

        var snap = await perception.SnapshotAsync(handle,
            new SnapshotOptions { FullProperties = true, IncludeOffscreen = true });
        var offRef = RefFor(snap.Tree, "OffscreenButton"); // IsOffscreen=true target

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            perception.RunOnRefActionAsync(handle, offRef, el => { Interactor.Invoke(el); return true; }, timeoutMs: 5000));
        Assert.Equal(ToolErrorCode.ElementNotActionable, ex.Code);
    }
}
