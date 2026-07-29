// test/FlaUI.Mcp.Tests/Perception/WaitForCullDefectTests.cs
// RETIRED backlog slug: wait-for-cull-disagrees-with-find (fixed in SP1; backlog file deleted).
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
/// The RESOLVED form of backlog slug <c>wait-for-cull-disagrees-with-find</c>. It began life as a
/// Tier-2 partial repro that asserted nothing and failed by design, carrying a second
/// <c>KnownDefect</c> trait so a deliberately-red test could not sit inside the v1.0 Desktop gate.
///
/// <para>The product question it was waiting on has been answered, and NOT by the option most people
/// assume. <c>exists</c> still culls — the semantics did not change — but the refusal stopped being
/// silent, and the caller was given a way through. So <c>find</c> and <c>wait_for</c> still return
/// different answers here, and that is now CORRECT: they answer different questions. What was fixed is
/// that the disagreement is explicable and escapable instead of an unattributable timeout.</para>
///
/// <para>Three claims, and the middle one is the actual fix. Asserting only the first and third would
/// pass against the original defect.</para>
///
/// <para>Keeps <c>Desktop</c> — it needs a real window and a live UIA tree. The <c>KnownDefect</c> trait
/// is GONE: this test now passes, so leaving it filtered out of the gate would hide a real regression.
/// </para>
/// </summary>
[Trait("Category", "Desktop")]
public class WaitForCullDefectTests
{
    // Laid out at Canvas.Left="5000" inside a clipped 1px Canvas, so it keeps a UIA peer and reports
    // IsOffscreen=false while its rect falls entirely outside the window. OffscreenCullTests depends on
    // it being out of bounds, so this is a stable repro rather than an accident of layout.
    private const string SpatialSentinelAid = "SpatialOffscreenButton";

    [Fact]
    public async Task A_spatially_culled_element_is_findable_explained_and_reachable_on_opt_in()
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

        // 1. find still sees it. Unchanged, and deliberately so — find never culled spatially.
        var found = await perception.FindAsync(handle: new WindowHandle(handle),
            query: new FindQuery(SpatialSentinelAid, null, "eq", null, false), max: 20, scopeRef: null);
        Assert.Equal(1, found.Matches.Count(m => m.AutomationId == SpatialSentinelAid));

        // 2. THE FIX. A default wait still refuses — but now says WHY, with both rectangles, so a
        //    geometry failure cannot be mistaken for a slow app. Before SP1 this was a bare
        //    {satisfied:false} and the caller had no way to tell those two apart.
        var defaultJson = await snap.DesktopWaitFor(handle, "automationId", SpatialSentinelAid,
            "exists", null, 2000, 250);
        using (var doc = JsonDocument.Parse(defaultJson))
        {
            var r = doc.RootElement;
            Assert.False(r.GetProperty("satisfied").GetBoolean());
            Assert.Equal("outsideWindowBounds", r.GetProperty("unsatisfiedBecause").GetString());
            Assert.Equal(4, r.GetProperty("elementBounds").GetArrayLength());
            Assert.Equal(4, r.GetProperty("windowBounds").GetArrayLength());
        }

        // 3. And the caller has an escape hatch, so the disagreement is no longer terminal.
        var optInJson = await snap.DesktopWaitFor(handle, "automationId", SpatialSentinelAid,
            "exists", null, 4000, 250, includeOffscreen: true);
        using (var doc = JsonDocument.Parse(optInJson))
        {
            var r = doc.RootElement;
            Assert.True(r.GetProperty("satisfied").GetBoolean());
            Assert.Equal(JsonValueKind.String, r.GetProperty("ref").ValueKind); // a usable ref, not null
        }
    }
}
