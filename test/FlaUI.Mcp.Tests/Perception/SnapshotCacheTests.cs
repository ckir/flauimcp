using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class SnapshotCacheTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public SnapshotCacheTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Snapshot_caches_its_model_retrievable_by_id()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var cache = new SnapshotCache();
        var perception = new PerceptionManager(mgr, new RefRegistry(), cache);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        var r = await perception.SnapshotAsync(handle, new SnapshotOptions());
        Assert.True(cache.TryGet(r.SnapshotId, out var model));
        Assert.True(model!.NodeCount > 0);
        Assert.False(cache.TryGet("w999:1", out _));
    }
}
