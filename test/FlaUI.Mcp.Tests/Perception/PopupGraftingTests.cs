using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class PopupGraftingTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public PopupGraftingTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task A_context_menu_opened_by_right_click_is_grafted_under_Popups()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry());
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        await mgr.FocusAsync(handle);
        await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            var target = win.FindFirstDescendant(cf => cf.ByAutomationId("MenuTarget"))!;
            target.RightClick();
            return true;
        });
        await Task.Delay(400); // let the menu open as a desktop-level popup

        var snap = await perception.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });
        Assert.Contains("[Popups]", snap.Tree);
        Assert.Contains("aid=MenuAlpha", snap.Tree);
    }
}
