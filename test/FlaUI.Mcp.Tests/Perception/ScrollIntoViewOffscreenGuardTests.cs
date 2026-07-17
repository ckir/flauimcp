using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

// fix-the-tool: docs/fix-the-tool-backlog/scroll-into-view-offscreen-guard.md
[Trait("Category", "Desktop")]
public class ScrollIntoViewOffscreenGuardTests
{
    [Fact]
    public async Task ScrollIntoView_wrongly_refuses_an_offscreen_element_ElementNotActionable()
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

        // Invoke: the exact path desktop_scroll_into_view runs through
        // (InteractionTools.DesktopScrollIntoView -> Act -> PerceptionManager.RunOnRefActionAsync with
        // Interactor.ScrollIntoView).
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            perception.RunOnRefActionAsync(handle, offscreenRef,
                el => { Interactor.ScrollIntoView(el); return true; }, timeoutMs: 4000));

        Assert.Equal(ToolErrorCode.ElementNotActionable, ex.Code);

        // KNOWN DEFECT: this is the SAME preflight guard desktop_select hits on an off-screen element —
        // ScrollItemPattern.ScrollIntoView is specifically designed to realize off-screen items, but
        // RunOnRefActionAsync's generic IsOffscreen check rejects it before Interactor.ScrollIntoView
        // ever runs, so the tool the select-error recommends as recovery refuses the same element.
        Assert.Fail("scroll-into-view-offscreen-guard: desktop_scroll_into_view refuses an off-screen " +
            $"element with {ex.Code} (\"{ex.Message}\") via the same generic preflight guard used for " +
            "desktop_select, even though UIA ScrollItemPattern.ScrollIntoView exists to realize off-screen " +
            "items; correct behavior not asserted yet — see " +
            "docs/fix-the-tool-backlog/scroll-into-view-offscreen-guard.md");
    }
}
