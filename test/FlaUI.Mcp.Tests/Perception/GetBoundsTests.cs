using System.Linq;
using System.Text.Json;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using FlaUI.Mcp.Tests.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class GetBoundsTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public GetBoundsTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Get_bounds_matches_snapshot_bounds_for_Ok_button()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var tools = new ScreenshotTools(perception);
        var window = new WindowTools(mgr, new ServerOptions(ReadOnly: false, AllowElevation: false), new FakePlatformEnvironment());
        var opened = await window.DesktopOpenWindow("pid", _app.Process.Id.ToString());
        var handle = JsonDocument.Parse(opened).RootElement.GetProperty("handle").GetString()!;
        var snapJson = await new SnapshotTools(perception, new WaitCoordinator(perception)).DesktopSnapshot(handle, fullProperties: true);
        var tree = JsonDocument.Parse(snapJson).RootElement.GetProperty("tree").GetString()!;
        var line = tree.Split('\n').First(l => l.Contains("aid=OkButton"));
        var @ref = line[(line.IndexOf('[') + 1)..line.IndexOf(']')];
        var json = await tools.DesktopGetBounds(handle, @ref);
        using var doc = JsonDocument.Parse(json);
        var b = doc.RootElement.GetProperty("bounds");
        Assert.True(b.GetProperty("w").GetInt32() > 0);
        Assert.True(b.GetProperty("h").GetInt32() > 0);
        Assert.True(doc.RootElement.GetProperty("dpiScale").GetDouble() > 0);
        Assert.False(doc.RootElement.GetProperty("isOffscreen").GetBoolean());
    }
}
