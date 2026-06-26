using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class FocusedFlagTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public FocusedFlagTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Focused_control_renders_the_focused_flag()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var tree = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            win.AsWindow().Focus();
            win.FindFirstDescendant(cf => cf.ByAutomationId("OkButton"))!.Focus();
            var refs = new RefRegistry(); refs.BeginSnapshot(handle.Id);
            var model = SnapshotEngine.Build(win, System.Array.Empty<AutomationElement>(), new SnapshotOptions(), refs, handle.Id);
            return SnapshotEngine.Render(model, new SnapshotOptions());
        });

        Assert.Contains("focused", tree);
    }
}
