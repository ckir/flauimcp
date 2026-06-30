using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

[Trait("Category", "Desktop")]
public class InputTargetingTests
{
    [Fact]
    public async Task Resolves_root_pid_process_and_class_from_a_window_element()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(app.Process.Id);
        var target = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) => InputTargeting.ResolveRefTarget(win));

        Assert.NotEqual(nint.Zero, target.Root);
        Assert.Equal(app.Process.Id, target.Pid);
        Assert.False(string.IsNullOrEmpty(target.ProcessName));
        Assert.False(string.IsNullOrEmpty(target.WindowClass));
    }
}
