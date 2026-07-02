using System.Linq;
using System.Text.Json;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

[Trait("Category", "Desktop")]
public class ListWindowsExtensionTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public ListWindowsExtensionTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Default_list_has_no_bounds_but_includeBounds_adds_them()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var tools = new WindowTools(mgr, new ServerOptions(ReadOnly: false, AllowElevation: false));

        var plain = await tools.DesktopListWindows();
        Assert.DoesNotContain("\"Bounds\"", plain);
        Assert.DoesNotContain("\"ZOrder\"", plain);

        var rich = await tools.DesktopListWindows(includeBounds: true);
        using var doc = JsonDocument.Parse(rich);
        var any = doc.RootElement.EnumerateArray().First();
        Assert.True(any.TryGetProperty("Bounds", out _));
        Assert.True(any.TryGetProperty("ZOrder", out _));
        // Back-compat: existing keys keep PascalCase.
        Assert.True(any.TryGetProperty("Title", out _));
    }
}
