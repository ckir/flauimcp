using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

[Trait("Category", "Desktop")]
public class GetFocusedElementTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public GetFocusedElementTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Returns_focused_ref_and_window_handle_after_focusing_Ok()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var snap = new SnapshotTools(perception, new WaitCoordinator(perception));
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        { win.AsWindow().Focus(); win.FindFirstDescendant(cf => cf.ByAutomationId("OkButton"))!.Focus(); return true; });
        var json = await snap.DesktopGetFocusedElement();
        using var doc = JsonDocument.Parse(json);
        Assert.StartsWith("e", doc.RootElement.GetProperty("ref").GetString());
        Assert.StartsWith("w", doc.RootElement.GetProperty("window").GetProperty("handle").GetString());
    }
}
