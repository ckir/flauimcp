using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class ScreenCaptureTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public ScreenCaptureTests(TestAppFixture app) => _app = app;

    [Fact]
    public void Desktop_is_renderable_on_a_connected_session() => Assert.True(ScreenCapture.IsDesktopRenderable());

    [Fact]
    public async Task Captures_a_png_of_a_window_rect_with_one_redaction()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        var (winRect, secretRect) = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            var secret = win.FindFirstDescendant(cf => cf.ByAutomationId("Secret"))!;
            return (win.BoundingRectangle, secret.BoundingRectangle);
        });
        var result = ScreenCapture.CaptureRectangle(winRect, new[] { secretRect }, 1600);
        Assert.True(result.Png.Length > 100);
        Assert.Equal(1, result.Redactions);
        Assert.Equal(0x89, result.Png[0]);
        Assert.Equal((byte)'P', result.Png[1]);
    }
}
