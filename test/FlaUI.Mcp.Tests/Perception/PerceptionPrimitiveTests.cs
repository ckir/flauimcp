using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class PerceptionPrimitiveTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public PerceptionPrimitiveTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task RunWithWindowAndDesktop_hands_back_the_window_and_a_desktop()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var (title, desktopHasChildren) = await mgr.RunWithWindowAndDesktopAsync(
            handle, (win, desktop) => (win.Title, desktop.FindAllChildren().Length > 0));

        Assert.Contains("TestApp", title);
        Assert.True(desktopHasChildren);
    }

    [Fact]
    public async Task RunWithWindowAndDesktop_on_a_stale_handle_throws_WindowHandleStale()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            mgr.RunWithWindowAndDesktopAsync(new WindowHandle("w999"), (w, d) => true));
        Assert.Equal(ToolErrorCode.WindowHandleStale, ex.Code);
    }
}
