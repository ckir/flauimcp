using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class ScrollIntoViewOffscreenGuardTests
{
    [Fact]
    public async Task ScrollIntoView_is_allowed_on_an_offscreen_element_while_the_guard_still_protects_other_actions()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var handle = await mgr.OpenByPidAsync(app.Process.Id);

        // Arrange: OffscreenButton (AutomationProperties.IsOffscreenBehavior="Offscreen") is the same
        // TestApp fixture OffscreenCullTests uses to get a real off-screen UIA peer (IsOffscreen=true) —
        // it stands in for an off-screen tab/list row. Only reachable via includeOffscreen, same as the
        // off-screen items desktop_select/desktop_scroll_into_view are meant to handle.
        var snap = await perception.SnapshotAsync(handle,
            new SnapshotOptions { FullProperties = true, IncludeOffscreen = true });
        var offscreenRef = RefLineHelper.RefFor(snap.Tree, "OffscreenButton");

        // (a) guard SKIPPED when asked — trivial callback so the result doesn't depend on whether
        // OffscreenButton itself supports ScrollItemPattern.
        var ok = await perception.RunOnRefActionAsync(handle, offscreenRef, el => true, timeoutMs: 4000, skipOffscreenGuard: true);
        Assert.True(ok);

        // (b) guard INTACT by default — still protects normal (non-scroll_into_view) actions.
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            perception.RunOnRefActionAsync(handle, offscreenRef, el => true, timeoutMs: 4000));
        Assert.Equal(ToolErrorCode.ElementNotActionable, ex.Code);

        // (c) end-to-end tool wiring — DesktopScrollIntoView actually passes skipOffscreenGuard: true.
        // Success or a ScrollItem-unsupported error are both acceptable; it must NOT be the off-screen
        // guard's rejection.
        var tools = new InteractionTools(perception, mgr, new ServerOptions(ReadOnly: false, AllowElevation: false));
        var resp = await tools.DesktopScrollIntoView(handle.Id, offscreenRef);
        Assert.DoesNotContain("off-screen; cannot act on it reliably", resp);
    }
}
