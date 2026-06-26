using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class OffscreenCullTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public OffscreenCullTests(TestAppFixture app) => _app = app;

    // TestApp hosts OffscreenButton at Canvas.Left=5000 inside a clipped 1px Canvas, so UIA reports
    // it IsOffscreen=true. Default snapshots cull it; includeOffscreen reaches it.
    [Fact]
    public async Task Offscreen_elements_are_culled_by_default_and_included_on_opt_in()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry());
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var culled = await perception.SnapshotAsync(handle,
            new SnapshotOptions { FullProperties = true });
        Assert.DoesNotContain("aid=OffscreenButton", culled.Tree);

        var included = await perception.SnapshotAsync(handle,
            new SnapshotOptions { FullProperties = true, IncludeOffscreen = true });
        Assert.Contains("aid=OffscreenButton", included.Tree);
    }

    // SpatialOffscreenButton reports IsOffscreen=FALSE but its rect is outside the window, so only the
    // bounding-box backstop culls it.
    [Fact]
    public async Task Spatially_offscreen_elements_are_culled_by_default_and_included_on_opt_in()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry());
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var culled = await perception.SnapshotAsync(handle,
            new SnapshotOptions { FullProperties = true });
        Assert.DoesNotContain("aid=SpatialOffscreenButton", culled.Tree);

        var included = await perception.SnapshotAsync(handle,
            new SnapshotOptions { FullProperties = true, IncludeOffscreen = true });
        Assert.Contains("aid=SpatialOffscreenButton", included.Tree);
    }

    // A shallow MaxDepth must announce the cut rather than truncate silently.
    [Fact]
    public async Task Depth_limit_emits_a_truncation_marker()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry());
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var snap = await perception.SnapshotAsync(handle, new SnapshotOptions { MaxDepth = 1 });
        Assert.Contains("depth limit 1", snap.Tree);
    }
}
