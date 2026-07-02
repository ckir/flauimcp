using System.Text.Json;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

[Trait("Category", "Desktop")]
public class WindowToolsTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public WindowToolsTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task ListWindows_returns_json_containing_TestApp()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var tools = new WindowTools(mgr, new ServerOptions(ReadOnly: false, AllowElevation: false));
        var json = await tools.DesktopListWindows();
        Assert.Contains("TestApp", json);
    }

    [Fact]
    public async Task OpenWindow_with_bad_pid_returns_structured_error_with_recovery()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var tools = new WindowTools(mgr, new ServerOptions(ReadOnly: false, AllowElevation: false));
        var json = await tools.DesktopOpenWindow("pid", "99999999");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("WindowNotFound", doc.RootElement.GetProperty("error").GetString());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("suggestedRecovery").GetString()));
    }
}
