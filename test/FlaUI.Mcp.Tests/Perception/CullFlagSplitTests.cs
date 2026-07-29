using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>The two filters behind SnapshotOptions are independent. SpatialOffscreenButton sits at
/// Canvas.Left=5000 and reports IsOffscreen=FALSE, so ONLY the spatial cull removes it; OffscreenButton
/// reports IsOffscreen=TRUE. Turning off the spatial cull must surface the first and NOT the second.</summary>
[Trait("Category", "Desktop")]
public class CullFlagSplitTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public CullFlagSplitTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Disabling_only_the_spatial_cull_surfaces_the_spatial_element_but_not_the_offscreen_one()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var snap = await perception.SnapshotAsync(handle,
            new SnapshotOptions { FullProperties = true, CullToWindowBounds = false });

        Assert.Contains("aid=SpatialOffscreenButton", snap.Tree);   // spatial cull was the only thing hiding it
        Assert.DoesNotContain("aid=OffscreenButton", snap.Tree);    // IsOffscreen filter still applies
    }

    [Fact]
    public async Task Default_options_are_unchanged_by_the_split()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var snap = await perception.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });

        Assert.DoesNotContain("aid=SpatialOffscreenButton", snap.Tree);
        Assert.DoesNotContain("aid=OffscreenButton", snap.Tree);
    }
}
