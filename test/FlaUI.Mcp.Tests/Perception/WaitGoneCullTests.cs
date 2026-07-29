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

        // Pins the MECHANISM, not just the outcome: the confirmation walk must actually have run and
        // must not have blocked the happy path. Without this the test passes with the whole
        // confirmation block deleted, since a culled poll already reports a missing element as gone.
        Assert.Equal(1, wait.ConfirmationWalkCount);
    }

    [Fact]
    public async Task The_latch_stops_the_confirmation_walk_from_doubling_every_poll()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var wait = new WaitCoordinator(perception);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        // A tree walk costs seconds on this fixture, so the budget must be large enough to afford
        // SEVERAL polls. With a short budget the loop completes one iteration and a latched build is
        // indistinguishable from an unlatched one -- that is precisely how the original version of
        // this test passed without exercising the latch at all.
        var r = await wait.WaitForAsync(handle, "automationId", "SpatialOffscreenButton",
            until: "gone", equals: null, timeoutMs: 15000, pollIntervalMs: 300);

        Assert.False(r.Satisfied);

        // Forced more than one poll, so the latch had an opportunity to fail.
        Assert.True(wait.PollWalkCount >= 2,
            $"budget did not afford a second poll (polls={wait.PollWalkCount}, elapsed={r.ElapsedMs}ms); " +
            "the latch is untested at this walk cost -- raise timeoutMs rather than weakening the assertion");

        // THE LATCH: exactly one confirmation for the whole call, no matter how many polls ran.
        // An unlatched build would issue one per poll, so this equals PollWalkCount and fails.
        Assert.Equal(1, wait.ConfirmationWalkCount);
    }

    /// <summary>The caller-opt-in path (Task 7) had no `gone` coverage at all: every other test here
    /// runs with includeOffscreen=false, so nothing pinned what happens when the caller has ALREADY
    /// disabled both filters. Two claims, and the second is the one that can silently regress —
    /// deleting the `!includeOffscreen` guard on the confirmation block still leaves Satisfied=false,
    /// so only the walk count catches the redundant second walk per poll.</summary>
    [Fact]
    public async Task Gone_under_includeOffscreen_sees_the_element_and_skips_the_confirmation_walk()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var wait = new WaitCoordinator(perception);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var r = await wait.WaitForAsync(handle, "automationId", "SpatialOffscreenButton",
            until: "gone", equals: null, timeoutMs: 1500, pollIntervalMs: 300, includeOffscreen: true);

        Assert.False(r.Satisfied);
        Assert.Equal(0, wait.ConfirmationWalkCount);
    }
}
