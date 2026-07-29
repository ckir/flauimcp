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

    /// <summary>Right-click the fixture's menu target and WAIT until the menu is actually reachable,
    /// rather than sleeping a fixed 400ms and hoping. The blind sleep was a live flake vector, not a
    /// theoretical one: this class fails with "expected 1, actual 0" -- the menu never opened -- when
    /// Desktop classes are co-run, and passes in isolation.
    /// The readiness probe deliberately uses RAW FlaUI against the window root, NOT PopupFinder or
    /// PerceptionManager: an arrange step that calls the code under test turns a product regression into
    /// a confusing arrange failure. Searching `win` alone is correct because of a MEASURED fact recorded
    /// in this class's docstring -- the WPF context menu is a window CHILD on this host. If that ever
    /// stops holding, this throws with a named reason instead of an assertion mismatch.</summary>
    private static async Task OpenContextMenuAsync(WindowManager mgr, WindowHandle handle)
    {
        await mgr.FocusAsync(handle);
        await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            var target = win.FindFirstDescendant(cf => cf.ByAutomationId("MenuTarget"))!;
            target.RightClick();
            return true;
        });

        for (int attempt = 0; attempt < 25; attempt++)
        {
            bool open = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
                win.FindFirstDescendant(cf => cf.ByAutomationId("MenuAlpha")) is not null);
            if (open) return;
            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException(
            "the fixture's context menu never opened within 2.5s of the right-click. This is an ARRANGE " +
            "failure, not a product defect: the right-click is coordinate-based, so a sibling TestApp " +
            "window stacked at the same default position can swallow it. Run this class on its own.");
    }

    [Fact]
    public async Task A_popup_element_is_returned_exactly_once_across_roots()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        await OpenContextMenuAsync(mgr, handle);

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

        await OpenContextMenuAsync(mgr, handle);

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
