using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

// CONSOLE-MACHINE-ONLY: fires real SendInput; needs an active unlocked session + a granted lease
// (`flaui-mcp unlock --minutes 5`). CANNOT pass on the headless RDP CI box. Not part of Category!=Desktop.
[Trait("Category", "Desktop")]
[Trait("Category", "SyntheticInput")]
public class InputToolsTests
{
    private static InputTools BuildTools(WindowManager mgr, PerceptionManager perception)
    {
        var env = new Win32PlatformEnvironment();
        var sink = new Win32SyntheticInput(env);
        var leases = new FileLeaseProvider();
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        var guard = new InputGuard(sink, env, leases, new ActionBudget(), new InputAudit(System.IO.TextWriter.Null),
            currentSid: sid, isElevated: false, allowElevation: false);
        return new InputTools(perception, mgr, new ServerOptions(ReadOnly: false, AllowElevation: false), guard, env);
    }

    private static bool InputLocked()
    {
        var lease = new FileLeaseProvider().Read(out _);
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        return lease is null || !lease.IsValidNow(System.DateTime.UtcNow, sid);
    }

    private static string RefForAid(string tree, string aid)
    {
        var line = System.Linq.Enumerable.First(tree.Split('\n'), l => l.Contains($"aid={aid}"));
        return line.TrimStart().Split(']')[0].TrimStart('[');
    }

    [SkippableFact]
    public async Task Type_writes_text_into_the_focused_textbox()
    {
        Skip.If(InputLocked(), "no active input lease — grant one on a console with `flaui-mcp unlock`");
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var handle = await mgr.OpenByPidAsync(app.Process.Id);

        var snap = await perception.SnapshotAsync(handle, new SnapshotOptions());
        var inputRef = RefForAid(snap.Tree, "Input");

        var tools = BuildTools(mgr, perception);
        var json = await tools.DesktopType(handle.Id, inputRef, "hello-4b", 4000);
        Assert.DoesNotContain("\"error\"", json);

        var val = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            win.FindFirstDescendant(cf => cf.ByAutomationId("Input"))!.AsTextBox().Text);
        Assert.Equal("hello-4b", val);
    }

    [SkippableFact]
    public async Task Click_at_a_window_point_returns_no_error()
    {
        Skip.If(InputLocked(), "no active input lease — grant one on a console with `flaui-mcp unlock`");
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var handle = await mgr.OpenByPidAsync(app.Process.Id);
        var tools = BuildTools(mgr, perception);

        // Happy path: a click at the window's own area (0.5,0.5) hit-tests to the app window and is allowed.
        var json = await tools.DesktopClickAt(handle.Id, 0.5, 0.5, "left", 1, 4000);
        Assert.DoesNotContain("\"error\"", json);
    }

    [Fact]
    public async Task Select_text_range_selects_the_requested_span()
    {
        // No InputLocked() skip — set_caret/select_text_range are lease-EXEMPT (no SendInput). Desktop trait
        // is for the real UIA text provider, which the headless box still has over RDP for UIA (not SendInput).
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var handle = await mgr.OpenByPidAsync(app.Process.Id);
        var snap = await perception.SnapshotAsync(handle, new SnapshotOptions());
        var docRef = RefForAid(snap.Tree, "TextDoc");

        var tools = BuildTools(mgr, perception);
        var json = await tools.DesktopSelectTextRange(handle.Id, docRef, start: 0, length: 5, 4000);
        Assert.DoesNotContain("\"error\"", json);

        // verify the selection length via TextPattern GetSelection on the element
        var selLen = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            var el = win.FindFirstDescendant(cf => cf.ByAutomationId("TextDoc"))!;
            var sel = el.Patterns.Text.Pattern.GetSelection();
            return sel[0].GetText(-1).Length;
        });
        Assert.Equal(5, selLen);
    }
}
