using System.Linq;
using System.Text.Json;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

[Trait("Category", "Desktop")]
public class WaitForTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public WaitForTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Missing_control_times_out_as_data()
    {
        var (snap, _, handle) = await Setup();
        var json = await snap.DesktopWaitFor(handle, "automationId", "NoSuchControl", "exists", null, 1000, 250);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("satisfied").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Delayed_control_becomes_satisfied_with_a_ref()
    {
        var (snap, inter, handle) = await Setup();
        var baseJson = await snap.DesktopSnapshot(handle, fullProperties: true);
        var tree = JsonDocument.Parse(baseJson).RootElement.GetProperty("tree").GetString()!;
        var line = tree.Split('\n').First(l => l.Contains("aid=DelayRevealButton"));
        var btn = line[(line.IndexOf('[') + 1)..line.IndexOf(']')];
        await inter.DesktopInvoke(handle, btn);
        var json = await snap.DesktopWaitFor(handle, "automationId", "DelayedLabel", "exists", null, 5000, 500);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("satisfied").GetBoolean());
        Assert.StartsWith("e", doc.RootElement.GetProperty("ref").GetString());
    }

    private async Task<(SnapshotTools, InteractionTools, string)> Setup()
    {
        var dispatcher = new AutomationDispatcher();
        var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var snap = new SnapshotTools(perception, new WaitCoordinator(perception));
        var inter = new InteractionTools(perception, mgr, ServerOptions.FromArgs(System.Array.Empty<string>()));
        var window = new WindowTools(mgr, new ServerOptions(ReadOnly: false, AllowElevation: false));
        var opened = await window.DesktopOpenWindow("pid", _app.Process.Id.ToString());
        var handle = JsonDocument.Parse(opened).RootElement.GetProperty("handle").GetString()!;
        return (snap, inter, handle);
    }
}
