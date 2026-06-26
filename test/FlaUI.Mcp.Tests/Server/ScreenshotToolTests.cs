using System.Linq;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Tools;
using ModelContextProtocol.Protocol;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

[Trait("Category", "Desktop")]
public class ScreenshotToolTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public ScreenshotToolTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Window_screenshot_returns_image_plus_metadata_with_redaction()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var tools = new ScreenshotTools(perception);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        var result = await tools.DesktopScreenshot(handle.Id);
        Assert.False(result.IsError);
        Assert.Contains(result.Content, c => c is ImageContentBlock);
        Assert.Contains("\"redactions\":1", result.Content.OfType<TextContentBlock>().First().Text);
    }

    [Fact]
    public async Task Output_file_returns_NotImplemented()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var tools = new ScreenshotTools(perception);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        var result = await tools.DesktopScreenshot(handle.Id, output: "file");
        Assert.True(result.IsError);
        Assert.Contains("NotImplemented", result.Content.OfType<TextContentBlock>().First().Text);
    }
}
