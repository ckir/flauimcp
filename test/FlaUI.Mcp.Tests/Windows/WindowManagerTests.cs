using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Windows;

[Trait("Category", "Desktop")]
public class WindowManagerTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public WindowManagerTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task ListWindows_includes_the_TestApp()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var windows = await mgr.ListWindowsAsync();
        Assert.Contains(windows, w => w.Title.Contains("TestApp"));
    }

    [Fact]
    public async Task OpenByPid_registers_a_handle_then_resolves_to_a_live_window()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        Assert.StartsWith("w", handle.Id);
        var title = await mgr.RunOnWindowAsync(handle, w => w.Title);
        Assert.Contains("TestApp", title);
    }

    [Fact]
    public async Task RunOnWindow_after_invalidate_throws_WindowHandleStale()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        mgr.Invalidate(handle); // simulates the Process.Exited path deterministically
        var ex = await Assert.ThrowsAsync<ToolException>(
            () => mgr.RunOnWindowAsync(handle, w => w.Title));
        Assert.Equal(ToolErrorCode.WindowHandleStale, ex.Code);
    }

    [Fact]
    public async Task An_action_primitive_resolves_the_window_on_a_distinct_STA_thread()
    {
        using var app = new FlaUI.Mcp.Tests.TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(app.Process.Id);

        var queryThread = await dispatcher.RunQueryAsync(() => Environment.CurrentManagedThreadId);
        var (title, actionThread) = await mgr.RunOnWindowActionAsync(handle,
            (win, _) => (win.AsWindow().Title, Environment.CurrentManagedThreadId), timeoutMs: 5000);

        Assert.Contains("TestApp", title);
        Assert.NotEqual(queryThread, actionThread); // resolved on a transient action STA, not the query STA
    }

    [Fact]
    public async Task Invalidate_raises_WindowInvalidated_once_and_only_when_state_was_removed()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var fired = new List<string>();
        mgr.WindowInvalidated += id => fired.Add(id);

        mgr.Invalidate(handle);            // live state → fires once
        mgr.Invalidate(handle);            // already gone → no phantom fire

        Assert.Equal(new[] { handle.Id }, fired);
    }

    [Fact]
    public async Task PruneClosedWindows_invalidates_a_tracked_handle_a_fake_predicate_reports_dead()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id); // registers _hwnds[handle.Id]

        var fired = new List<string>();
        mgr.WindowInvalidated += id => fired.Add(id);

        mgr.PruneClosedWindows(isAlive: _ => false); // force "everything dead"

        Assert.Contains(handle.Id, fired);
        var ex = await Assert.ThrowsAsync<ToolException>(
            () => mgr.RunOnWindowAsync(handle, w => w.Title));
        Assert.Equal(ToolErrorCode.WindowHandleStale, ex.Code); // handle really was invalidated
    }

    [Fact]
    public async Task Closing_a_window_evicts_its_RefRegistry_entry_via_the_PerceptionManager_wiring()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var refs = new FlaUI.Mcp.Core.Perception.RefRegistry();
        // Constructing PerceptionManager subscribes refs.EvictWindow to mgr.WindowInvalidated.
        _ = new FlaUI.Mcp.Core.Perception.PerceptionManager(
            mgr, refs, new FlaUI.Mcp.Core.Perception.SnapshotCache());

        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        refs.BeginSnapshot(handle.Id);
        var elRef = refs.Register(handle.Id,
            new FlaUI.Mcp.Core.Perception.ElementDescriptor(
                Array.Empty<int>(), FlaUI.Core.Definitions.ControlType.Button, "a", "a", null, Array.Empty<int>()),
            cached: null);

        Assert.Equal("a", refs.Lookup(handle.Id, elRef).Descriptor.AutomationId); // present before close

        mgr.Invalidate(handle); // fires WindowInvalidated → refs.EvictWindow(handle.Id)

        var ex = Assert.Throws<ToolException>(() => refs.Lookup(handle.Id, elRef));
        Assert.Equal(ToolErrorCode.RefNotFound, ex.Code); // evicted through the wiring
    }
}
