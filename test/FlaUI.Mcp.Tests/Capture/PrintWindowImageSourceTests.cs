using System;
using System.Drawing;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Capture;
using Xunit;

namespace FlaUI.Mcp.Tests.Capture;

[Trait("Category", "Desktop")]
public class PrintWindowImageSourceTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public PrintWindowImageSourceTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task It_renders_the_test_app_window_as_something_other_than_one_colour()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        var (hwnd, rect) = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            (win.Properties.NativeWindowHandle.ValueOrDefault, win.BoundingRectangle));

        var src = new PrintWindowImageSource();
        using var bmp = src.Acquire(hwnd, new Size(rect.Width, rect.Height), timeoutMs: 5000);

        Assert.NotNull(bmp);
        Assert.Equal(rect.Width, bmp!.Width);
        Assert.Equal(rect.Height, bmp.Height);
        // F1: PW_RENDERFULLCONTENT is mandatory -- flag 0 returns blank for four of five window classes.
        // If this asserts, check the flag before anything else.
        Assert.False(FlaUI.Mcp.Core.Perception.UniformCanvasDetector.IsUniform(bmp),
            "the window rendered as a single colour; check PW_RENDERFULLCONTENT (flag 2) is being passed");
    }

    // A zero handle must refuse rather than calling into Win32 with it.
    [Fact]
    public void A_zero_handle_throws_CaptureUnavailable()
    {
        var src = new PrintWindowImageSource();
        var ex = Assert.Throws<FlaUI.Mcp.Core.Errors.ToolException>(() =>
            src.Acquire(IntPtr.Zero, new Size(100, 100), 1000));
        Assert.Equal(FlaUI.Mcp.Core.Errors.ToolErrorCode.CaptureUnavailable, ex.Code);
    }
}
