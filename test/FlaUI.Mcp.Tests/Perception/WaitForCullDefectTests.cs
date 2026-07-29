// test/FlaUI.Mcp.Tests/Perception/WaitForCullDefectTests.cs
// Backlog slug: wait-for-cull-disagrees-with-find
// See docs/fix-the-tool-backlog/wait-for-cull-disagrees-with-find.md
using System.Text.Json;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using FlaUI.Mcp.Tests.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>
/// TIER-2 PARTIAL REPRO for backlog slug <c>wait-for-cull-disagrees-with-find</c>.
///
/// <para><c>desktop_find</c> and <c>desktop_wait_for</c> disagree about whether an element EXISTS.
/// Waits and snapshots apply a spatial cull against the window's own BoundingRectangle
/// (<c>SnapshotEngine.cs:45-46</c> binds <c>cullBounds</c>; <c>:66-70</c> drops anything that does not
/// intersect it); <c>PerceptionManager.FindAsync</c> does not. So an element laid out beyond the
/// window's edge is findable yet can never satisfy a wait, for any timeout.</para>
///
/// <para>It is deliberately NOT asserting the correct behaviour, because which behaviour is correct is
/// an undecided product question — either the existence predicate stops culling, or the refusal
/// becomes legible instead of a bare timeout. Both are <c>src/</c> changes. The test exists to pin the
/// repro and to fail loudly until that decision is made.</para>
///
/// <para>BOTH traits, and both are load-bearing. <c>Desktop</c> because it needs a real window and a
/// live UIA tree, so it can never run headless. <c>KnownDefect</c> because it fails BY DESIGN, and the
/// Desktop suite is the v1.0 gate — a deliberately-red test sitting inside a gate makes that gate
/// unreadable, and "109 passed, 1 expected failure" is exactly the kind of caveat that decays into
/// someone ignoring a real failure. The headless CI filter already excludes both categories; the
/// Desktop gate excludes KnownDefect for this reason. Run it deliberately with
/// <c>--filter Category=KnownDefect</c>. It must NEVER be a plain [Fact].</para>
/// </summary>
[Trait("Category", "Desktop")]
[Trait("Category", "KnownDefect")]
public class WaitForCullDefectTests
{
    // The fixture's permanent instance of the condition: laid out at Canvas.Left="5000" inside a
    // clipped 1px Canvas, so it keeps a UIA peer and reports IsOffscreen=false while its rect falls
    // entirely outside the window. OffscreenCullTests depends on it being out of bounds, so this is a
    // stable repro rather than an accident of layout that a future fix might remove.
    private const string SpatialSentinelAid = "SpatialOffscreenButton";

    [Fact]
    public async Task Wait_for_and_find_disagree_on_a_spatially_culled_element()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);

        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var snap = new SnapshotTools(perception, new WaitCoordinator(perception));
        var window = new WindowTools(mgr, new ServerOptions(ReadOnly: false, AllowElevation: false),
            new FakePlatformEnvironment());

        var opened = await window.DesktopOpenWindow("pid", app.Process.Id.ToString());
        var handle = JsonDocument.Parse(opened).RootElement.GetProperty("handle").GetString()!;

        // find: does NOT cull spatially.
        var found = await perception.FindAsync(handle: new WindowHandle(handle),
            query: new FindQuery(SpatialSentinelAid, null, "eq", null, false), max: 20, scopeRef: null);

        // wait_for exists: goes through the culled snapshot walk. Short timeout on purpose -- the point
        // is that no timeout can ever help, so waiting longer only makes the repro slower.
        var waitJson = await snap.DesktopWaitFor(handle, "automationId", SpatialSentinelAid, "exists",
            null, 2000, 250);
        var satisfied = JsonDocument.Parse(waitJson).RootElement.GetProperty("satisfied").GetBoolean();

        Assert.Fail(
            $"wait-for-cull-disagrees-with-find: find returned {found.Matches.Count} match(es) for " +
            $"'{SpatialSentinelAid}' while wait_for(exists) reported satisfied={satisfied}. " +
            "Correct behaviour is not asserted yet -- see docs/fix-the-tool-backlog/" +
            "wait-for-cull-disagrees-with-find.md. When the decision is made, replace this with the " +
            "real assertion, run --filter Category=Desktop, then strip the trait and delete the " +
            "backlog file.");
    }
}
