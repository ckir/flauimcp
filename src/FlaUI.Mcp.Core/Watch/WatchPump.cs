// src/FlaUI.Mcp.Core/Watch/WatchPump.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Core.Watch;

/// <summary>§6/§8 worker: the single long-running loop that drains the shared channel, coalesces/debounces, and
/// for each ready aggregate builds a payload on the query STA (minting the event ref per §8/R5) then BOTH pushes
/// it (sink.EmitAsync) AND buffers it for polling (WatchDrainBuffer) — Spike B push+drain. Worker-confined: it
/// OWNS its EventCoalescer and the source/close-context maps, so no lock is needed on them (§11 — only this
/// thread touches them). BUILD-ONLY here; validated by the Task 10 Desktop tests with a recording sink.</summary>
public sealed class WatchPump : IAsyncDisposable
{
    private const int DebounceMs = 100;

    private readonly Channel<EventEnvelope> _channel;
    private readonly WindowManager _windowManager;
    private readonly RefRegistry _refs;
    private readonly WatchRegistry _registry;
    private readonly IEventSink _sink;
    private readonly WatchDrainBuffer _drainBuffer;

    // Worker-confined (single-threaded): the pump constructs and OWNS its coalescer (§11 — shared with nothing).
    private readonly EventCoalescer _coalescer = new(256, DebounceMs);
    // Source token per coalesce key for source-bearing kinds (focus_changed / window_opened). structure_changed
    // uses the SCOPE element (resolved on the STA), not this; window_closed has no source.
    private readonly Dictionary<string, object?> _sourceByKey = new();
    // windowId per subscription for a SYNTHETIC window_closed, whose registry entry is already evicted by the
    // time the pump processes it (OnWindowInvalidated removes the sub, then enqueues the close with
    // CoalesceScope=windowId).
    private readonly Dictionary<string, string> _closeWindowId = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public WatchPump(
        Channel<EventEnvelope> channel, WindowManager windowManager, RefRegistry refs,
        WatchRegistry registry, IEventSink sink, WatchDrainBuffer drainBuffer)
    {
        _channel = channel;
        _windowManager = windowManager;
        _refs = refs;
        _registry = registry;
        _sink = sink;
        _drainBuffer = drainBuffer;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    /// <summary>Complete the channel + cancel the loop ONLY (registration teardown belongs to
    /// WatchService.DisposeAllAsync).</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        try { _cts?.Cancel(); } catch { }
        try { _channel.Writer.TryComplete(); } catch { }
        if (_loop is not null)
            try { await _loop; } catch { /* loop is self-contained; swallow shutdown faults */ }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    // CONDITIONAL-timer liveness (§8): when the coalescer holds NO pending, block indefinitely on
    // WaitToReadAsync (idle = truly asleep, no idle-wake drain). When it DOES hold unsettled structure_changed
    // aggregates, race the (single, reused) pending read against a short Task.Delay(~debounce/2) so the loop
    // wakes to Drain(now) on settle even if no new envelope arrives. A single WaitToReadAsync is kept alive
    // across iterations so a timer wake never leaks a second outstanding waiter.
    private async Task RunLoopAsync(CancellationToken ct)
    {
        var reader = _channel.Reader;
        Task<bool>? waitTask = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await DrainReadyAsync(ct); // emit any aggregates ready at 'now'

                waitTask ??= reader.WaitToReadAsync(ct).AsTask();
                if (_coalescer.HasPending)
                {
                    var timer = Task.Delay(Math.Max(1, DebounceMs / 2), ct);
                    var done = await Task.WhenAny(waitTask, timer);
                    if (done != waitTask) continue; // timer fired -> loop back and Drain the settled aggregate
                }

                bool more;
                try { more = await waitTask; }
                catch (OperationCanceledException) { break; }
                waitTask = null;
                if (!more) { await DrainReadyAsync(ct); break; } // channel completed -> flush + exit
                DrainChannelInto(reader);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception) { /* terminal emit failure (client gone) or fatal -> stop cleanly (§16.3) */ }
        finally { try { _channel.Writer.TryComplete(); } catch { } }
    }

    // Offer every currently-available envelope into the coalescer (single-threaded). Track the source token /
    // close windowId so the later STA build can wrap the right element and recover the window id.
    private void DrainChannelInto(ChannelReader<EventEnvelope> reader)
    {
        while (reader.TryRead(out var env))
        {
            var meta = env.Meta;
            var now = DateTime.UtcNow;
            var key = meta.CoalesceKey(env.CoalesceScope);
            var droppedSub = _coalescer.Offer(key, meta, now);
            if (droppedSub is not null) _registry.IncrementDropped(droppedSub);

            if (meta.Kind is WatchEventKind.FocusChanged or WatchEventKind.WindowOpened)
                _sourceByKey[key] = env.Source;                       // freshest source (matches coalescer)
            else if (meta.Kind == WatchEventKind.WindowClosed)
                _closeWindowId[meta.SubscriptionId] = env.CoalesceScope; // windowId (sub may be evicted)
        }
    }

    // Emit every aggregate ready at 'now'. Per-item try/catch for the BUILD (log-and-continue, §16.3); the
    // sink.EmitAsync failure is NOT caught here — it propagates to RunLoopAsync as the terminal client-gone stop.
    private async Task DrainReadyAsync(CancellationToken ct)
    {
        foreach (var pending in _coalescer.Drain(DateTime.UtcNow))
        {
            var meta = pending.Meta;
            string? windowId;
            string? scopeRef = null;
            object? source = null;

            if (meta.Kind == WatchEventKind.WindowClosed)
            {
                // Registry first (a real UIA close on a still-live sub), else the synthetic-close windowId map.
                if (_registry.TryGet(meta.SubscriptionId, out var i) && i is not null) windowId = i.WindowId;
                else _closeWindowId.TryGetValue(meta.SubscriptionId, out windowId);
                _closeWindowId.Remove(meta.SubscriptionId);
            }
            else
            {
                if (!_registry.TryGet(meta.SubscriptionId, out var info) || info is null)
                {
                    _sourceByKey.Remove(meta.CoalesceKey("")); // sub gone -> drop + clean up the source token
                    continue;
                }
                windowId = info.WindowId;
                scopeRef = info.Scope;
                if (meta.Kind is WatchEventKind.FocusChanged or WatchEventKind.WindowOpened)
                    _sourceByKey.Remove(meta.CoalesceKey(""), out source); // non-structure key: scope arg ignored
            }

            if (windowId is null) continue;

            DesktopEventPayload? payload;
            try { payload = await BuildPayloadAsync(meta, pending.CoalescedCount, source, windowId, scopeRef); }
            catch { continue; } // §16.3 build/STA fault -> log-and-continue (window may have just closed, etc.)
            if (payload is null) continue; // per-event deny-list dropped it (§10)

            await _sink.EmitAsync(payload, ct); // push (terminal on failure -> RunLoopAsync stops)
            if (_drainBuffer.Append(payload.SubscriptionId, payload)) // drain: same built payload for pollers
                _registry.IncrementDropped(payload.SubscriptionId);   // summed-drop accounting (agy risk#2)
        }
    }

    // Build one payload. window_closed (HasSource=false) needs NO STA hop — it touches no UIA (the window is
    // gone). Every other kind marshals a payload-build onto the query STA per §8/R5: structure_changed wraps the
    // subscribed SCOPE (scope ref, else the window root) so MintRef refs the scope, not the transient child;
    // focus_changed/window_opened wrap the event SOURCE. A per-event deny-list re-check (§10) drops a
    // source-bearing event whose owning process is denied; window_closed skips it (no source; already dead).
    private async Task<DesktopEventPayload?> BuildPayloadAsync(
        CapturedEventMeta meta, int coalescedCount, object? source, string windowId, string? scopeRef)
    {
        if (meta.Kind == WatchEventKind.WindowClosed)
            return WatchPayloadBuilder.Build(meta, windowId, coalescedCount, ClosedReader.Instance);

        return await _windowManager.RunWithWindowAndDesktopAsync(
            new WindowHandle(windowId), (win, desktop) =>
            {
                AutomationElement? target = meta.Kind == WatchEventKind.StructureChanged
                    ? (string.IsNullOrEmpty(scopeRef)
                        ? win // §8/R5: default scope is the window root
                        : _refs.Resolve(windowId, scopeRef!, PopupFinder.SearchRoots(win, desktop)))
                    : source as AutomationElement; // focus_changed / window_opened: the event source

                if (target is not null)
                {
                    var procName = SafeProcessName(target); // §10 defense-in-depth (process-coarse)
                    if (PerceptionPolicy.IsDenied(procName)) return (DesktopEventPayload?)null;
                }

                var reader = new LiveEventSourceReader(target, _refs, windowId);
                return (DesktopEventPayload?)WatchPayloadBuilder.Build(meta, windowId, coalescedCount, reader);
            });
    }

    // Owning process base name from an element's PID (mirrors PerceptionManager.SafeProcessName). Null (not a
    // throw) on a dead PID -> IsDenied(null)=false, so a raced-away source never spuriously blocks.
    private static string? SafeProcessName(AutomationElement el)
    {
        int pid;
        try { pid = el.Properties.ProcessId.ValueOrDefault; } catch { pid = -1; }
        if (pid < 0) return null;
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return null; }
    }

    // Live STA-side reader over an already-resolved element (query STA). Fail-soft per read; IsPassword is
    // fail-closed (INV-5). Mints the event ref into the bounded event-ref layer (§16.5) from a descriptor built
    // exactly like FindAsync's (raw Name -> descriptor for re-resolution; cached: el for an immediately-usable ref).
    private sealed class LiveEventSourceReader : IEventSourceReader
    {
        private readonly AutomationElement? _el;
        private readonly RefRegistry _refs;
        private readonly string _windowId;

        public LiveEventSourceReader(AutomationElement? el, RefRegistry refs, string windowId)
        {
            _el = el;
            _refs = refs;
            _windowId = windowId;
        }

        public bool HasSource => _el is not null;
        public bool IsPassword => _el is not null &&
            RedactionPolicy.IsPasswordOrFailClosed(() => _el.Properties.IsPassword.ValueOrDefault);
        public string? ControlType => _el is null ? null : Safe(() => _el.ControlType.ToString(), null);
        public string? Name => _el is null ? null : Safe(() => _el.Name, null);

        public int[]? Bounds
        {
            get
            {
                if (_el is null) return null;
                var r = Safe(() => _el.BoundingRectangle, System.Drawing.Rectangle.Empty);
                return r.IsEmpty ? null : new[] { r.X, r.Y, r.Width, r.Height };
            }
        }

        public string? MintRef()
        {
            if (_el is null) return null;
            try
            {
                int[] rid = Safe(() => _el.Properties.RuntimeId.ValueOrDefault, (int[]?)null) ?? Array.Empty<int>();
                var ct = Safe(() => _el.ControlType, FlaUI.Core.Definitions.ControlType.Custom);
                string aid = Safe(() => _el.AutomationId, "") ?? string.Empty;
                string rawName = Safe(() => _el.Name, "") ?? string.Empty; // RAW -> descriptor (never redacted)
                var descriptor = new ElementDescriptor(rid, ct, aid, rawName,
                    SnapshotEngine.NearestAncestorAutomationId(_el), Array.Empty<int>(), false);
                return _refs.RegisterEventRef(_windowId, descriptor, cached: _el);
            }
            catch { return null; }
        }

        private static T Safe<T>(Func<T> read, T fallback) { try { return read(); } catch { return fallback; } }
    }

    // Source-less reader for window_closed (HasSource=false -> the builder emits ref/controlType/name/bounds all null).
    private sealed class ClosedReader : IEventSourceReader
    {
        public static readonly ClosedReader Instance = new();
        public bool HasSource => false;
        public bool IsPassword => false;
        public string? ControlType => null;
        public string? Name => null;
        public int[]? Bounds => null;
        public string? MintRef() => null;
    }
}
