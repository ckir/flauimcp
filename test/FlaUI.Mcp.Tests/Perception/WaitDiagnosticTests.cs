using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>The diagnostic must MEASURE the geometry, never infer it from presence across two walks.
/// Blaming the wrong cause confidently is worse than the bare timeout it replaces.</summary>
[Trait("Category", "Desktop")]
public class WaitDiagnosticTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public WaitDiagnosticTests(TestAppFixture app) => _app = app;

    private static async Task<(WaitCoordinator Wait, WindowHandle Handle)> ArrangeAsync(
        AutomationDispatcher dispatcher, WindowManager mgr, int pid)
    {
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        return (new WaitCoordinator(perception), await mgr.OpenByPidAsync(pid));
    }

    [Fact]
    public async Task Exists_timing_out_on_a_culled_element_reports_the_reason_and_both_rects()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var (wait, handle) = await ArrangeAsync(dispatcher, mgr, _app.Process.Id);

        var r = await wait.WaitForAsync(handle, "automationId", "SpatialOffscreenButton",
            until: "exists", equals: null, timeoutMs: 900, pollIntervalMs: 300);

        Assert.False(r.Satisfied);
        Assert.Equal("outsideWindowBounds", r.UnsatisfiedBecause);
        Assert.NotNull(r.ElementBounds);
        Assert.NotNull(r.WindowBounds);
        Assert.Equal(4, r.ElementBounds!.Length);   // {X, Y, Width, Height}
        Assert.Equal(4, r.WindowBounds!.Length);
    }

    [Fact]
    public async Task A_selector_that_matches_nothing_gets_no_geometry_blame()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var (wait, handle) = await ArrangeAsync(dispatcher, mgr, _app.Process.Id);

        var r = await wait.WaitForAsync(handle, "automationId", "NoSuchElement_SP1",
            until: "exists", equals: null, timeoutMs: 900, pollIntervalMs: 300);

        Assert.False(r.Satisfied);
        Assert.Null(r.UnsatisfiedBecause);
        Assert.Null(r.ElementBounds);
        Assert.Null(r.WindowBounds);
    }

    [Fact]
    public async Task Enabled_timing_out_on_a_disabled_in_window_element_is_not_blamed_on_geometry()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var (wait, handle) = await ArrangeAsync(dispatcher, mgr, _app.Process.Id);

        // DisabledButton is inside the window and IsEnabled=false, so it is present in BOTH walks.
        // A presence-keyed rule would blame geometry here; the intersection test must not.
        var r = await wait.WaitForAsync(handle, "automationId", "DisabledButton",
            until: "enabled", equals: null, timeoutMs: 900, pollIntervalMs: 300);

        Assert.False(r.Satisfied);
        Assert.Null(r.UnsatisfiedBecause);
    }

    [Fact]
    public async Task Opting_in_lets_exists_satisfy_on_a_spatially_culled_element()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var (wait, handle) = await ArrangeAsync(dispatcher, mgr, _app.Process.Id);

        var r = await wait.WaitForAsync(handle, "automationId", "SpatialOffscreenButton",
            until: "exists", equals: null, timeoutMs: 1500, pollIntervalMs: 300,
            includeOffscreen: true);

        Assert.True(r.Satisfied);

        // A satisfied `exists` MUST hand back a usable ref. This is not incidental: the satisfy
        // snapshot is taken in a SEPARATE walk from the one that decided, and if that walk re-applies
        // the cull it drops the very element just matched, yielding satisfied:true with ref:null.
        // That exact defect was found by review on the valueEquals path; this pins the opt-in path.
        Assert.NotNull(r.Ref);
    }
}
