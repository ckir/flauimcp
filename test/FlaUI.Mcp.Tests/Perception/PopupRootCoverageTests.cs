using System.Linq;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>Routing find through PopupFinder.SearchRoots means a WINDOW-CHILD popup is reachable from
/// two roots — via `win` and via the popup root. SnapshotEngine prunes that duplication for snapshots
/// (pinned by PopupGraftingTests); find has no such pruning, so it must dedup by RuntimeId.
/// MEASURED on this host: viaWindowRoot=1, viaPopupRoots=1 — the fixture's WPF context menu is a
/// window child that ALSO appears as an owner popup, so it exercises dedup but CANNOT reproduce the
/// desktop-level blindness the fix also addresses. There is deliberately no blindness test here.</summary>
[Trait("Category", "Desktop")]
public class PopupRootCoverageTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public PopupRootCoverageTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task A_popup_element_is_returned_exactly_once_across_roots()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        await mgr.FocusAsync(handle);
        await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            var target = win.FindFirstDescendant(cf => cf.ByAutomationId("MenuTarget"))!;
            target.RightClick();
            return true;
        });
        await Task.Delay(400); // let the menu open

        // FindQuery is a POSITIONAL record (FindQuery.cs:13-19) — object-initializer syntax does not
        // compile. Order: AutomationId, Name, NameMatch, ControlType, EnabledOnly, IgnoreCase=false.
        // NameMatch is non-nullable and must be supplied even when Name is null.
        var query = new FindQuery("MenuAlpha", null, "eq", null, false);
        var r = await perception.FindAsync(handle, query, max: 20, scopeRef: null);

        Assert.Equal(1, r.Matches.Count(m => m.AutomationId == "MenuAlpha"));
        Assert.Equal(1, r.TotalMatches);
    }

    /// <summary>Step 3 rewrites EvaluateSelectorValueAsync's search into a multi-root loop, and
    /// until="valueEquals" is its ONLY consumer. Without this, a typo in that loop shows up as a
    /// valueEquals that silently never satisfies — indistinguishable from a slow app. This asserts
    /// COVERAGE, not the fix: the fixture menu is a window child, so valueEquals could already reach
    /// it. It guards the rewrite.</summary>
    [Fact]
    public async Task ValueEquals_reaches_an_element_inside_an_open_popup()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var wait = new WaitCoordinator(perception);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        await mgr.FocusAsync(handle);
        await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            var target = win.FindFirstDescendant(cf => cf.ByAutomationId("MenuTarget"))!;
            target.RightClick();
            return true;
        });
        await Task.Delay(400);

        // MenuAlpha's Header is "Alpha" (MainWindow.xaml:103), and the value read falls back
        // ValuePattern -> Name, so a MenuItem with no ValuePattern yields its Name.
        var r = await wait.WaitForAsync(handle, "automationId", "MenuAlpha",
            until: "valueEquals", equals: "Alpha", timeoutMs: 1500, pollIntervalMs: 300);

        Assert.True(r.Satisfied);
    }

    /// <summary>Z2: desktop_wait_for accepts includeOffscreen, but the valueEquals evaluator applied
    /// its IsOffscreen filter unconditionally — so the flag was accepted and ignored on exactly one of
    /// the four `until` values. OffscreenButton reports IsOffscreen=true (MainWindow.xaml:62-65,
    /// IsOffscreenBehavior="Offscreen") with Content="Offscreen", which UIA surfaces as its Name.
    /// Both halves matter: without the first the fix is unpinned, without the second a fix that just
    /// deleted the filter would pass.</summary>
    [Fact]
    public async Task ValueEquals_honours_includeOffscreen()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var wait = new WaitCoordinator(perception);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var without = await wait.WaitForAsync(handle, "automationId", "OffscreenButton",
            until: "valueEquals", equals: "Offscreen", timeoutMs: 1200, pollIntervalMs: 300);
        Assert.False(without.Satisfied);

        var with = await wait.WaitForAsync(handle, "automationId", "OffscreenButton",
            until: "valueEquals", equals: "Offscreen", timeoutMs: 3000, pollIntervalMs: 300,
            includeOffscreen: true);
        Assert.True(with.Satisfied);
        Assert.NotNull(with.Ref);   // the satisfy snapshot must be taken under options that still see it
    }
}
