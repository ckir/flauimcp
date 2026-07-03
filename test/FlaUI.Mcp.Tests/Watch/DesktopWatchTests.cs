// test/FlaUI.Mcp.Tests/Watch/DesktopWatchTests.cs
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Watch;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Watch;

[Trait("Category", "Desktop")]
public class DesktopWatchTests
{
    private sealed class RecordingSink : IEventSink
    {
        public ConcurrentQueue<DesktopEventPayload> Events { get; } = new();
        public Task EmitAsync(DesktopEventPayload payload, CancellationToken ct)
        { Events.Enqueue(payload); return Task.CompletedTask; }
    }

    private static async Task<bool> WaitFor(RecordingSink sink, Func<DesktopEventPayload, bool> pred, int ms = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline)
        {
            if (sink.Events.Any(pred)) return true;
            await Task.Delay(50);
        }
        return false;
    }

    [Fact]
    public async Task Window_opened_fires_when_child_dialog_opens()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        var (svc, sink, handle, mgr, pump) = await BuildAsync(app, dispatcher);
        await using var _ = pump;
        using (mgr)
        {
            var sub = await svc.WatchAsync(handle.Id, new[] { WatchEventKind.WindowOpened }, null, 4000);
            // click ModalButton -> opens a child "Modal" window (MainWindow.xaml.cs:49 ModalButton_Click)
            await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            { win.FindFirstDescendant(cf => cf.ByAutomationId("ModalButton"))!.AsButton().Invoke(); return true; });

            Assert.True(await WaitFor(sink, e => e.Event == "window_opened" && e.SubscriptionId == sub.Id));
        }
    }

    [Fact]
    public async Task Structure_changed_coalesces_a_rebuild_into_one_event()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        var (svc, sink, handle, mgr, pump) = await BuildAsync(app, dispatcher);
        await using var _ = pump;
        using (mgr)
        {
            var sub = await svc.WatchAsync(handle.Id, new[] { WatchEventKind.StructureChanged }, null, 4000);
            await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            { win.FindFirstDescendant(cf => cf.ByAutomationId("RebuildItemsButton"))!.AsButton().Invoke(); return true; });

            Assert.True(await WaitFor(sink, e => e.Event == "structure_changed" && e.SubscriptionId == sub.Id));
            await Task.Delay(400); // let debounce settle any stragglers
            var count = sink.Events.Count(e => e.Event == "structure_changed" && e.SubscriptionId == sub.Id);
            Assert.True(count <= 2, $"expected coalesced structure_changed (<=2), got {count}");
        }
    }

    [Fact]
    public async Task Unwatch_stops_delivery()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        var (svc, sink, handle, mgr, pump) = await BuildAsync(app, dispatcher);
        await using var _ = pump;
        using (mgr)
        {
            var sub = await svc.WatchAsync(handle.Id, new[] { WatchEventKind.StructureChanged }, null, 4000);
            await svc.UnwatchAsync(sub.Id);
            await Task.Delay(100);
            sink.Events.Clear();
            await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            { win.FindFirstDescendant(cf => cf.ByAutomationId("RebuildItemsButton"))!.AsButton().Invoke(); return true; });
            await Task.Delay(500);
            Assert.DoesNotContain(sink.Events, e => e.SubscriptionId == sub.Id);
        }
    }

    [Fact]
    public async Task Drain_returns_buffered_events_for_a_subscription()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        var (svc, sink, handle, mgr, pump) = await BuildAsync(app, dispatcher);
        await using var _ = pump;
        using (mgr)
        {
            var sub = await svc.WatchAsync(handle.Id, new[] { WatchEventKind.WindowOpened }, null, 4000);
            await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            { win.FindFirstDescendant(cf => cf.ByAutomationId("ModalButton"))!.AsButton().Invoke(); return true; });
            Assert.True(await WaitFor(sink, e => e.Event == "window_opened" && e.SubscriptionId == sub.Id));
            // the same event is ALSO buffered for drain (push+drain)
            var drained = svc.Drain(sub.Id, null);
            Assert.Contains(drained, e => e.Event == "window_opened");
        }
    }

    // Wire the full pipeline (Task 8/9 shapes): WindowManager+RefRegistry, WatchRegistry, a bounded
    // Channel<EventEnvelope> (mirrors Program.cs), Uia3EventSource, RecordingSink (in place of McpEventSink),
    // WatchPump (started), WatchService. Model construction on FindTests.OpenAsync (FindTests.cs:14-22).
    private static async Task<(WatchService svc, RecordingSink sink, WindowHandle handle, WindowManager mgr, IAsyncDisposable pump)>
        BuildAsync(TestAppFixture app, AutomationDispatcher dispatcher)
    {
        var mgr = new WindowManager(dispatcher);
        var refs = new RefRegistry();
        var registry = new WatchRegistry();
        var drain = new WatchDrainBuffer();
        var channel = Channel.CreateBounded<EventEnvelope>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
        var source = new Uia3EventSource(mgr);
        var sink = new RecordingSink();
        var pump = new WatchPump(channel, mgr, refs, registry, sink, drain);
        await pump.StartAsync(CancellationToken.None);
        var svc = new WatchService(mgr, registry, source, channel, refs, drain);
        var handle = await mgr.OpenByPidAsync(app.Process.Id);
        return (svc, sink, handle, mgr, pump);
    }
}
