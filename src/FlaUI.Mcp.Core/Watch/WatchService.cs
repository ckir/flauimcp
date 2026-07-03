// src/FlaUI.Mcp.Core/Watch/WatchService.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Channels;
using System.Threading.Tasks;
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Core.Watch;

/// <summary>What WatchAsync hands back to the tool layer (§3).</summary>
public sealed record WatchHandle(string Id, IReadOnlyList<WatchEventKind> Kinds);

/// <summary>§6/§9 façade the tools call. Resolves the window + deny-list + scopeRef on the query STA, reserves a
/// subscription in the WatchRegistry, then registers the UIA handlers via IUiaEventSource transactionally and
/// HOLDS the returned disposable (so it — not WatchPump — owns teardown). onCapture applies the §7 focus filter
/// then writes an EventEnvelope to the shared bounded channel (the COM thread never blocks). Also owns the
/// push+drain WatchDrainBuffer (Spike B): the pump appends built payloads there; this class removes a sub's
/// buffer on unwatch / window-evict. Subscribes to WindowManager.WindowInvalidated to evict + emit a synthetic
/// window_closed (§9). Process-lifetime singleton on stdio (Program.cs), so the '+=' never leaks.</summary>
public sealed class WatchService
{
    private readonly WindowManager _windowManager;
    private readonly WatchRegistry _registry;
    private readonly IUiaEventSource _source;
    private readonly Channel<EventEnvelope> _channel;
    private readonly RefRegistry _refs;
    private readonly WatchDrainBuffer _drainBuffer;

    private readonly object _gate = new();
    private readonly Dictionary<string, IDisposable> _registrations = new();

    public WatchService(
        WindowManager windowManager, WatchRegistry registry, IUiaEventSource source,
        Channel<EventEnvelope> channel, RefRegistry refs, WatchDrainBuffer drainBuffer)
    {
        _windowManager = windowManager;
        _registry = registry;
        _source = source;
        _channel = channel;
        _refs = refs;
        _drainBuffer = drainBuffer;
        // Phase-6 chokepoint: a closed window evicts its subs + fires the synthetic close. Fires OFF the query
        // STA (proc.Exited ThreadPool thread) — OnWindowInvalidated is thread-safe and the disposables it drives
        // self-marshal onto the query STA. Lifetime: singleton on stdio, so this never leaks (mirrors
        // PerceptionManager.cs:33-38); a future per-connection RefRegistry phase would need '-=' on teardown.
        _windowManager.WindowInvalidated += OnWindowInvalidated;
    }

    /// <summary>Subscribe. Enforces (in order) stale-handle, deny-list (§10 TargetDenied), and stale-scope
    /// (RefStaleUnresolvable) on the query STA BEFORE any UIA registration; reads the window's PID there for the
    /// §7 filter. Then reserves an id (TooManyWatches propagates) and registers the UIA handlers transactionally
    /// (on failure Remove the id + rethrow, leaving no phantom).</summary>
    public async Task<WatchHandle> WatchAsync(string windowId, IReadOnlyList<WatchEventKind> kinds, string? scopeRef, int timeoutMs)
    {
        // Deny-list + scope-resolve + PID read, all on the query STA (mirrors PerceptionManager.FindAsync guards).
        var (pid, coalesceScope) = await _windowManager.RunWithWindowAndDesktopAsync(
            new WindowHandle(windowId), (win, desktop) =>
            {
                var procName = SafeProcessName(win);
                if (PerceptionPolicy.IsDenied(procName))
                    throw new ToolException(ToolErrorCode.TargetDenied,
                        $"Watching windows owned by '{procName}' is blocked (credential store).",
                        "watch a different, non-sensitive window");
                if (!string.IsNullOrEmpty(scopeRef))
                    _ = _refs.Resolve(windowId, scopeRef!, PopupFinder.SearchRoots(win, desktop)); // REF_NOT_FOUND / REF_STALE_UNRESOLVABLE
                int p = -1;
                try { p = win.Properties.ProcessId.ValueOrDefault; } catch { /* PID-less -> §7 filter drops foreign */ }
                // CoalesceScope: stable per-subscription key part for structure_changed coalescing (§8). Scope ref
                // when narrowed, else the window id (window-root scope). Ignored for non-structure coalesce keys.
                return (p, scopeRef ?? windowId);
            });

        var id = _registry.Create(windowId, kinds, scopeRef); // TooManyWatches propagates
        try
        {
            var spec = new WatchSubscriptionSpec(id, windowId, pid, kinds, scopeRef, coalesceScope);
            var reg = _source.Register(spec, (meta, src) => OnCapture(spec, meta, src));
            lock (_gate) _registrations[id] = reg;
        }
        catch
        {
            _registry.Remove(id); // no phantom on a failed registration
            throw;
        }
        return new WatchHandle(id, kinds);
    }

    // COM-thread entry (via IUiaEventSource fan-out): apply the §7 filter BEFORE the channel (drop foreign /
    // PID-less), then non-blocking TryWrite. The channel is DropWrite-bounded so the COM thread never blocks; a
    // refused write bumps droppedCount (Wait-mode would return false; DropWrite silently drops — the pump-side
    // coalescer + drain buffer carry the meaningful drop accounting).
    private void OnCapture(WatchSubscriptionSpec spec, CapturedEventMeta meta, object? source)
    {
        if (!FocusEventFilter.ShouldDeliver(meta.Kind, spec.WindowProcessId, meta.SourceProcessId))
            return;
        var env = new EventEnvelope(meta, source, spec.CoalesceScope);
        if (!_channel.Writer.TryWrite(env))
            _registry.IncrementDropped(spec.SubscriptionId);
    }

    /// <summary>Stop a subscription. Disposes its UIA registration (the disposable self-marshals onto the query
    /// STA — safe from any thread), removes it from the registry, and drops its drain buffer. Idempotent.</summary>
    public Task UnwatchAsync(string subscriptionId)
    {
        IDisposable? reg;
        lock (_gate) _registrations.Remove(subscriptionId, out reg);
        try { reg?.Dispose(); } catch { /* best-effort: handlers may already be dead (window gone) */ }
        _registry.Remove(subscriptionId);
        _drainBuffer.Remove(subscriptionId);
        return Task.CompletedTask;
    }

    // Phase-6 close signal (fires OFF the query STA). Evict every sub on the closed window: capture their info
    // FIRST (need Kinds to decide the synthetic close), remove them from the registry, dispose each registration
    // (self-marshals), drop each drain buffer, and for any sub that watched window_closed enqueue a synthetic
    // close (§9) — null source (HasSource=false) so the pump builds a source-less payload without touching the
    // now-dead window; CoalesceScope=windowId so the pump can recover the window id after the registry entry is
    // gone. Swallows everything: this can fire on a raw ThreadPool thread where a throw is process-fatal.
    private void OnWindowInvalidated(string windowId)
    {
        try
        {
            var infos = _registry.List().Where(i => i.WindowId == windowId).ToList();
            _registry.RemoveByWindow(windowId);
            foreach (var info in infos)
            {
                IDisposable? reg;
                lock (_gate) _registrations.Remove(info.SubscriptionId, out reg);
                try { reg?.Dispose(); } catch { }
                _drainBuffer.Remove(info.SubscriptionId);
                if (info.Kinds.Contains(WatchEventKind.WindowClosed))
                {
                    var meta = new CapturedEventMeta(info.SubscriptionId, WatchEventKind.WindowClosed, 0, "", DateTime.UtcNow);
                    _channel.Writer.TryWrite(new EventEnvelope(meta, null, windowId));
                }
            }
        }
        catch { /* invalidation-path robustness > surfacing a fault on a process-fatal thread */ }
    }

    /// <summary>desktop_list_watches (§3).</summary>
    public IReadOnlyList<WatchInfo> List() => _registry.List();

    /// <summary>desktop_drain_events (Task 10): remove+return up to max buffered payloads for a subscription.</summary>
    public IReadOnlyList<DesktopEventPayload> Drain(string subscriptionId, int? max) => _drainBuffer.Drain(subscriptionId, max);

    /// <summary>§9 connection teardown: dispose every held UIA registration (each self-marshals onto the query
    /// STA) and clear them. The hosted service calls this on shutdown, AFTER stopping the pump.</summary>
    public ValueTask DisposeAllAsync()
    {
        List<IDisposable> regs;
        lock (_gate)
        {
            regs = _registrations.Values.ToList();
            _registrations.Clear();
        }
        foreach (var reg in regs)
            try { reg.Dispose(); } catch { /* best-effort */ }
        return ValueTask.CompletedTask;
    }

    // Owning process base name (no ".exe") from the window's PID, for the deny-list (mirrors
    // PerceptionManager.SafeProcessName).
    private static string? SafeProcessName(AutomationElement el)
    {
        int pid;
        try { pid = el.Properties.ProcessId.ValueOrDefault; } catch { pid = -1; }
        if (pid < 0) return null;
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return null; }
    }
}
