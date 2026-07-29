using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>SpatialOffscreenButton exists in the UIA tree with IsOffscreen=false and a rect outside
/// the window. It is NOT gone. Before SP1, until="gone" satisfied on it immediately.</summary>
[Trait("Category", "Desktop")]
public class WaitGoneCullTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public WaitGoneCullTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Gone_does_not_satisfy_on_a_spatially_culled_element_that_still_exists()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var wait = new WaitCoordinator(perception);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var r = await wait.WaitForAsync(handle, "automationId", "SpatialOffscreenButton",
            until: "gone", equals: null, timeoutMs: 1500, pollIntervalMs: 300);

        Assert.False(r.Satisfied);
    }

    [Fact]
    public async Task Gone_still_satisfies_for_an_element_that_genuinely_is_not_there()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var wait = new WaitCoordinator(perception);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var r = await wait.WaitForAsync(handle, "automationId", "NoSuchElement_SP1",
            until: "gone", equals: null, timeoutMs: 2000, pollIntervalMs: 300);

        Assert.True(r.Satisfied);
    }

    [Fact]
    public async Task The_latch_stops_the_confirmation_walk_from_doubling_every_poll()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var wait = new WaitCoordinator(perception);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        // ~5 polls against a permanently culled element. Without the latch every poll would walk
        // TWICE (culled says absent -> confirmation finds it), so the count would be about 2x polls.
        var r = await wait.WaitForAsync(handle, "automationId", "SpatialOffscreenButton",
            until: "gone", equals: null, timeoutMs: 1500, pollIntervalMs: 300);

        Assert.False(r.Satisfied);
        // One walk per poll, plus the single confirmation that flipped the latch. Generous upper
        // bound: assert we are nowhere near the 2x-per-poll shape, without pinning an exact count
        // that a timing wobble would flake.
        Assert.InRange(wait.WalkCount, 2, 9);
    }
}
