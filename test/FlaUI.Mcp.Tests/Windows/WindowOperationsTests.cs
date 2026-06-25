using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Windows;

public class WindowOperationsTests
{
    private static string TestAppPath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "test", "FlaUI.Mcp.TestApp", "bin", "Debug", "net10.0-windows",
        "FlaUI.Mcp.TestApp.exe"));

    [Fact]
    public async Task LaunchApp_returns_a_handle_to_a_window_with_a_title()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        // Deterministic, multi-instance target. notepad.exe is single-instance/tabbed on
        // Win11: a second launch adds a tab to an existing Notepad rather than opening a
        // new window, so it is unreliable as a repeatable gate (the Win11 reparenting path
        // is documented as a known limitation instead).
        var (handle, _) = await mgr.LaunchAppAsync(TestAppPath(), args: null, timeoutMs: 8000);
        try
        {
            var title = await mgr.RunOnWindowAsync(handle, w => w.Title);
            Assert.False(string.IsNullOrEmpty(title));
        }
        finally { await mgr.CloseAsync(handle); }
    }

    [Fact]
    public async Task LaunchApp_with_bad_path_throws()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        await Assert.ThrowsAsync<ToolException>(
            () => mgr.LaunchAppAsync("C:/no/such/app.exe", null, 2000));
    }

    [Fact]
    public async Task OpenByTitle_with_multiple_matches_throws_AmbiguousMatch()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var exe = TestAppPath();
        var (h1, _) = await mgr.LaunchAppAsync(exe, null, 8000);
        var (h2, _) = await mgr.LaunchAppAsync(exe, null, 8000);
        try
        {
            var ex = await Assert.ThrowsAsync<ToolException>(
                () => mgr.OpenByTitleAsync("FlaUI.Mcp TestApp"));
            Assert.Equal(ToolErrorCode.AmbiguousMatch, ex.Code);
        }
        finally { await mgr.CloseAsync(h1); await mgr.CloseAsync(h2); }
    }
}
