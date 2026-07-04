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

    [Fact]
    public async Task ResolveElementTarget_populates_allow_listed_identity_only()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(app.Process.Id);
        var target = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            var el = win.FindFirstDescendant(cf => cf.ByAutomationId("OkButton"))!;
            return InputTargeting.ResolveElementTarget(win, el);
        });

        Assert.NotNull(target.Element);
        var id = target.Element!.Value;
        Assert.False(string.IsNullOrEmpty(id.RuntimeId));
        Assert.Equal("OkButton", id.AutomationId);
        Assert.True(id.Bounds.W > 0 && id.Bounds.H > 0);
        // ElementIdentity structurally has no Name/Value field to leak — the type enforces the allow-list.
    }
}
