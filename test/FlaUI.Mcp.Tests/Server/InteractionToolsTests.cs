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
        p = new PerceptionManager(m, new RefRegistry(), new SnapshotCache());
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
            // RevealedLabel starts as an EMPTY TextBlock — it has no findable peer in the first
            // snapshot. set_focus's GotFocus sets its text, so re-snapshot to resolve it now.
            var snap2 = await p.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });
            var label = await p.RunOnRefAsync(handle, RefFor(snap2.Tree, "RevealedLabel"), el => el.Name);
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

    // THE critical Task-C proof: an action that PHYSICALLY blocks the action STA (target UI thread
    // frozen, not pumping COM RPC) must not poison the dispatcher for the NEXT action.
    //
    // Why a real Thread.Sleep freeze and not a WPF modal: UIA InvokePattern.Invoke is fire-and-forget
    // and WPF ShowDialog keeps the UIA pipeline pumping, so a modal NEVER blocks the caller's STA.
    // A genuine UI-thread freeze is the only faithful repro of the cross-apartment block Task-C guards
    // against. Because the freeze-trigger invoke is itself fire-and-forget, invoking FreezeButton
    // RETURNS ok and the freeze starts just after — so the block lands on the action issued DURING the
    // freeze, which is the one this test abandons and then recovers from.
    [Fact]
    public async Task A_blocked_action_does_not_deadlock_a_second_action()
    {
        using var app = new TestAppFixture();
        var tools = Make(app, out var handle, out var p, out var m, out var d); using (d) using (m)
        {
            var snap = await p.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });
            var freezeRef = RefFor(snap.Tree, "FreezeButton");
            var okRef = RefFor(snap.Tree, "OkButton");

            // (1) Trigger the freeze. Fire-and-forget invoke returns ok; the UI thread then sleeps.
            Assert.DoesNotContain("error", await tools.DesktopInvoke(handle.Id, freezeRef));
            await Task.Delay(150); // let the queued click dequeue and Thread.Sleep actually begin

            // (2) An action issued WHILE the UI is frozen blocks the transient action STA on COM RPC;
            //     the short timeout abandons that thread and returns ActionBlockedPending.
            var blocked = await tools.DesktopInvoke(handle.Id, okRef, timeoutMs: 500);
            Assert.Contains("ActionBlockedPending", blocked);

            // (3) Wait out the freeze so the target recovers AND the abandoned action STA drains its COM
            //     call (it returns once the UI pumps again) — so teardown never kills a mid-RPC target.
            //     The abandonment itself is already crash-safe (background thread + catch-all + finally
            //     dispose); this wait is for deterministic, noise-free cleanup, not for safety.
            await Task.Delay(2200); // > TestApp FreezeButton's Thread.Sleep (MainWindow.FreezeMs = 2000)

            // (4) A fresh action now succeeds — proof the abandoned thread did NOT poison the dispatcher
            //     (a single shared action thread would still be parked and this would itself time out).
            var recovered = await tools.DesktopInvoke(handle.Id, okRef, timeoutMs: 2000);
            Assert.DoesNotContain("error", recovered);
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
