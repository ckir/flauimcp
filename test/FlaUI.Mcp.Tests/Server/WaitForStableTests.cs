using System.Text.Json;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

[Trait("Category", "Desktop")]
public class WaitForStableTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public WaitForStableTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Structure_is_stable_despite_a_live_ticker()
    {
        var (snap, handle) = await Setup();
        var json = await snap.DesktopWaitForStable(handle, null, null, false, 500, 5000, 250);
        Assert.True(JsonDocument.Parse(json).RootElement.GetProperty("stable").GetBoolean());
    }

    [Fact]
    public async Task IncludeText_on_a_live_ticker_times_out_unstable()
    {
        var (snap, handle) = await Setup();
        var json = await snap.DesktopWaitForStable(handle, null, null, true, 500, 1500, 250);
        Assert.False(JsonDocument.Parse(json).RootElement.GetProperty("stable").GetBoolean());
    }

    private async Task<(SnapshotTools, string)> Setup()
    {
        var dispatcher = new AutomationDispatcher();
        var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var snap = new SnapshotTools(perception, new WaitCoordinator(perception));
        var window = new WindowTools(mgr, new ServerOptions(ReadOnly: false, AllowElevation: false));
        var opened = await window.DesktopOpenWindow("pid", _app.Process.Id.ToString());
        var handle = JsonDocument.Parse(opened).RootElement.GetProperty("handle").GetString()!;
        return (snap, handle);
    }
}
