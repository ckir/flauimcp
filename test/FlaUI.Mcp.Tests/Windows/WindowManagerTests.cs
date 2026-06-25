using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Windows;

[Trait("Category", "Desktop")]
public class WindowManagerTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public WindowManagerTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task ListWindows_includes_the_TestApp()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var windows = await mgr.ListWindowsAsync();
        Assert.Contains(windows, w => w.Title.Contains("TestApp"));
    }

    [Fact]
    public async Task OpenByPid_registers_a_handle_then_resolves_to_a_live_window()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        Assert.StartsWith("w", handle.Id);
        var title = await mgr.RunOnWindowAsync(handle, w => w.Title);
        Assert.Contains("TestApp", title);
    }

    [Fact]
    public async Task RunOnWindow_after_invalidate_throws_WindowHandleStale()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        mgr.Invalidate(handle); // simulates the Process.Exited path deterministically
        var ex = await Assert.ThrowsAsync<ToolException>(
            () => mgr.RunOnWindowAsync(handle, w => w.Title));
        Assert.Equal(ToolErrorCode.WindowHandleStale, ex.Code);
    }
}
