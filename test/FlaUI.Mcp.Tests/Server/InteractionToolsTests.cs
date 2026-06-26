using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

[Trait("Category", "Desktop")]
public class InteractionToolsTests
{
    private static InteractionTools Make(TestAppFixture app, out WindowHandle handle,
        out PerceptionManager p, out WindowManager m, out AutomationDispatcher d)
    {
        d = new AutomationDispatcher();
        m = new WindowManager(d);
        p = new PerceptionManager(m, new RefRegistry());
        var tools = new InteractionTools(p, m, new ServerOptions(ReadOnly: false));
        handle = m.OpenByPidAsync(app.Process.Id).GetAwaiter().GetResult();
        return tools;
    }

    private static string RefFor(string tree, string aid)
    {
        foreach (var line in tree.Split('\n'))
            if (line.Contains("aid=" + aid))
            { int lb = line.IndexOf('['), rb = line.IndexOf(']'); return line.Substring(lb + 1, rb - lb - 1); }
        throw new Xunit.Sdk.XunitException($"no ref for aid={aid}");
    }

    [Fact]
    public async Task Invoke_changes_state_and_set_focus_reveals_a_label()
    {
        using var app = new TestAppFixture();
        var tools = Make(app, out var handle, out var p, out var m, out var d); using (d) using (m)
        {
            var snap = await p.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });
            Assert.DoesNotContain("error", await tools.DesktopInvoke(handle.Id, RefFor(snap.Tree, "OkButton")));
            await tools.DesktopSetFocus(handle.Id, RefFor(snap.Tree, "FocusReveal"));
            var label = await p.RunOnRefAsync(handle, RefFor(snap.Tree, "RevealedLabel"), el => el.Name);
            Assert.Equal("revealed", label);
        }
    }

    [Fact]
    public async Task SetValue_toggle_expand_select_round_trip()
    {
        using var app = new TestAppFixture();
        var tools = Make(app, out var handle, out var p, out var m, out var d); using (d) using (m)
        {
            var snap = await p.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });

            Assert.DoesNotContain("error", await tools.DesktopSetValue(handle.Id, RefFor(snap.Tree, "Input"), "typed"));
            Assert.Equal("typed", await p.RunOnRefAsync(handle, RefFor(snap.Tree, "Input"), el => el.AsTextBox().Text));

            Assert.DoesNotContain("error", await tools.DesktopToggle(handle.Id, RefFor(snap.Tree, "Check")));
            Assert.DoesNotContain("error", await tools.DesktopExpand(handle.Id, RefFor(snap.Tree, "Exp")));
            Assert.DoesNotContain("error", await tools.DesktopSelect(handle.Id, RefFor(snap.Tree, "ItemB")));
        }
    }

    [Fact]
    public async Task ScrollIntoView_and_scroll_do_not_error()
    {
        using var app = new TestAppFixture();
        var tools = Make(app, out var handle, out var p, out var m, out var d); using (d) using (m)
        {
            var snap = await p.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });
            Assert.DoesNotContain("error", await tools.DesktopScrollIntoView(handle.Id, RefFor(snap.Tree, "ItemC")));
            Assert.DoesNotContain("error", await tools.DesktopScroll(handle.Id, RefFor(snap.Tree, "ItemList"), "down", 1));
        }
    }

    [Fact]
    public async Task WindowTransform_maximize_then_restore()
    {
        using var app = new TestAppFixture();
        var tools = Make(app, out var handle, out var p, out var m, out var d); using (d) using (m)
        {
            Assert.DoesNotContain("error", await tools.DesktopWindowTransform(handle.Id, "maximize"));
            Assert.DoesNotContain("error", await tools.DesktopWindowTransform(handle.Id, "restore"));
        }
    }

    // THE critical Task-C proof: a blocked action must NOT freeze the action context.
    [Fact]
    public async Task A_blocked_modal_action_does_not_deadlock_a_second_action()
    {
        using var app = new TestAppFixture();
        var tools = Make(app, out var handle, out var p, out var m, out var d); using (d) using (m)
        {
            var snap = await p.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });
            // Invoke ModalButton: ShowDialog blocks the UI thread -> ACTION_BLOCKED_PENDING.
            var blocked = await tools.DesktopInvoke(handle.Id, RefFor(snap.Tree, "ModalButton"), timeoutMs: 800);
            Assert.Contains("ActionBlockedPending", blocked);

            // The modal is now open. Snapshot it and click OK on a FRESH action thread.
            // A single-thread action queue would hang here forever.
            // Bounded poll: ShowDialog creates+registers the dialog on the TestApp UI
            // thread slightly AFTER our Invoke times out, so a one-shot lookup races the
            // OS window registration. Retry until found or a 3s deadline (no magic sleep).
            WindowHandle modal = default;
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                try { modal = await m.OpenByTitleAsync("Modal"); if (modal.Id is not null) break; }
                catch { /* not registered yet — retry */ }
                await Task.Delay(50);
            }
            Assert.NotNull(modal.Id);
            var modalSnap = await p.SnapshotAsync(modal, new SnapshotOptions { FullProperties = true });
            var dismiss = await tools.DesktopInvoke(modal.Id, RefFor(modalSnap.Tree, "ModalOk"), timeoutMs: 3000);
            Assert.DoesNotContain("error", dismiss);
        }
    }
}

// Non-Desktop: read-only guard short-circuits before any UIA work.
// null! dependencies prove the guard fires BEFORE they are touched.
public class InteractionToolsReadOnlyTests
{
    [Fact]
    public async Task Read_only_mode_blocks_every_action_tool()
    {
        var tools = new InteractionTools(perception: null!, windows: null!, new ServerOptions(ReadOnly: true));
        Assert.Contains("WriteBlockedReadOnly", await tools.DesktopInvoke("w1", "e1"));
        Assert.Contains("WriteBlockedReadOnly", await tools.DesktopSetValue("w1", "e1", "x"));
        Assert.Contains("WriteBlockedReadOnly", await tools.DesktopToggle("w1", "e1"));
        Assert.Contains("WriteBlockedReadOnly", await tools.DesktopExpand("w1", "e1"));
        Assert.Contains("WriteBlockedReadOnly", await tools.DesktopSelect("w1", "e1"));
        Assert.Contains("WriteBlockedReadOnly", await tools.DesktopScrollIntoView("w1", "e1"));
        Assert.Contains("WriteBlockedReadOnly", await tools.DesktopScroll("w1", "e1", "down"));
        Assert.Contains("WriteBlockedReadOnly", await tools.DesktopSetFocus("w1", "e1"));
        Assert.Contains("WriteBlockedReadOnly", await tools.DesktopWindowTransform("w1", "maximize"));
    }
}
