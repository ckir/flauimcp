using System.Text.Json;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using FlaUI.Mcp.Tests.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

[Trait("Category", "Desktop")]
public class SnapshotStatsTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public SnapshotStatsTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Stats_by_window_counts_controls_and_redactions()
    {
        var (snap, handle) = await Setup();
        var json = await snap.DesktopSnapshotStats(handle, null);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("total").GetInt32() > 0);
        Assert.Equal(1, doc.RootElement.GetProperty("redacted").GetInt32());
        Assert.True(doc.RootElement.GetProperty("byControlType").TryGetProperty("Button", out _));
    }

    [Fact]
    public async Task Stats_requires_exactly_one_arg()
    {
        var (snap, _) = await Setup();
        var json = await snap.DesktopSnapshotStats(null, null);
        Assert.Equal("InvalidArguments", JsonDocument.Parse(json).RootElement.GetProperty("error").GetString());
    }

    private async Task<(SnapshotTools, string)> Setup()
    {
        var dispatcher = new AutomationDispatcher();
        var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var snap = new SnapshotTools(perception, new WaitCoordinator(perception));
        var window = new WindowTools(mgr, new ServerOptions(ReadOnly: false, AllowElevation: false), new FakePlatformEnvironment());
        var opened = await window.DesktopOpenWindow("pid", _app.Process.Id.ToString());
        var handle = JsonDocument.Parse(opened).RootElement.GetProperty("handle").GetString()!;
        return (snap, handle);
    }
}
