using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class RecycleGuardTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public RecycleGuardTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Fast_path_rejects_a_cached_element_whose_live_name_diverges()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var (same, diverged) = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            var status = win.FindFirstDescendant(cf => cf.ByAutomationId("Status"))!;
            var rid = status.Properties.RuntimeId.ValueOrDefault;
            var live = status.Name;
            var dSame = new ElementDescriptor(rid, status.ControlType, "Status", live, null, System.Array.Empty<int>());
            var dDiff = new ElementDescriptor(rid, status.ControlType, "Status", live + "-X", null, System.Array.Empty<int>());
            return (RefRegistry.FastPathMatches(status, dSame), RefRegistry.FastPathMatches(status, dDiff));
        });

        Assert.True(same);
        Assert.False(diverged);
    }
}
