using System.Text.Json;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

[Trait("Category", "Desktop")]
public class SnapshotToolsTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public SnapshotToolsTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task DesktopSnapshot_returns_a_tree_with_refs_for_an_open_window()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var refs = new RefRegistry();
        var perception = new PerceptionManager(mgr, refs, new SnapshotCache());
        var snap = new SnapshotTools(perception, new WaitCoordinator(perception));
        var window = new WindowTools(mgr, new ServerOptions(ReadOnly: false, AllowElevation: false));

        var opened = await window.DesktopOpenWindow("pid", _app.Process.Id.ToString());
        var handle = JsonDocument.Parse(opened).RootElement.GetProperty("handle").GetString()!;

        var json = await snap.DesktopSnapshot(handle);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("nodeCount").GetInt32() > 0);
        Assert.StartsWith(handle + ":", doc.RootElement.GetProperty("snapshotId").GetString());
        Assert.Contains("Button", doc.RootElement.GetProperty("tree").GetString());
    }

    [Fact]
    public async Task DesktopSnapshot_on_a_stale_handle_returns_a_structured_error()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var snap = new SnapshotTools(perception, new WaitCoordinator(perception));
        var json = await snap.DesktopSnapshot("w999");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("WindowHandleStale", doc.RootElement.GetProperty("error").GetString());
    }
}
