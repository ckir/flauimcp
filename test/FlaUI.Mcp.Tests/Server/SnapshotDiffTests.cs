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
public class SnapshotDiffTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public SnapshotDiffTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Diff_detects_a_changed_status_label_after_invoke()
    {
        var (snap, inter, _, handle) = await Setup();
        var baseJson = await snap.DesktopSnapshot(handle, fullProperties: true);
        var baselineId = JsonDocument.Parse(baseJson).RootElement.GetProperty("snapshotId").GetString()!;
        var tree = JsonDocument.Parse(baseJson).RootElement.GetProperty("tree").GetString()!;
        var okRef = RefFor(tree, "aid=OkButton");
        await inter.DesktopInvoke(handle, okRef);
        var diffJson = await snap.DesktopSnapshotDiff(handle, baselineId);
        using var doc = JsonDocument.Parse(diffJson);
        Assert.Equal(baselineId, doc.RootElement.GetProperty("baselineSnapshotId").GetString());
        Assert.True(doc.RootElement.GetProperty("changed").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Diff_rejects_a_missing_baseline()
    {
        var (snap, _, _, handle) = await Setup();
        var json = await snap.DesktopSnapshotDiff(handle, handle + ":999");
        Assert.Equal("SnapshotNotFound", JsonDocument.Parse(json).RootElement.GetProperty("error").GetString());
    }

    private static string RefFor(string tree, string needle)
    { var line = tree.Split('\n').First(l => l.Contains(needle)); return line[(line.IndexOf('[') + 1)..line.IndexOf(']')]; }

    private async Task<(SnapshotTools, InteractionTools, WindowTools, string)> Setup()
    {
        var dispatcher = new AutomationDispatcher();
        var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var snap = new SnapshotTools(perception, new WaitCoordinator(perception));
        var inter = new InteractionTools(perception, mgr, ServerOptions.FromArgs(System.Array.Empty<string>()));
        var window = new WindowTools(mgr);
        var opened = await window.DesktopOpenWindow("pid", _app.Process.Id.ToString());
        var handle = JsonDocument.Parse(opened).RootElement.GetProperty("handle").GetString()!;
        return (snap, inter, window, handle);
    }
}
