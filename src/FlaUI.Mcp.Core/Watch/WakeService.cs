using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Core.Watch;

/// <summary>Prong A (§4): HOLD a UIA event registration on a window to keep Chromium/Electron AXMode active so its
/// native accessibility tree stays hydrated. Reuses the Phase-8 IUiaEventSource seam with a NULL SINK — the COM
/// callback drops every event, so waking never feeds the watch channel/coalescer/drain (§4.3). Separate accounting
/// (WakeRegistry, own cap). Auto-releases via WindowManager.WindowInvalidated (§9). Process-lifetime singleton on
/// stdio (Program.cs), so the '+=' never leaks.</summary>
public sealed class WakeService
{
    // Spike β decision: StructureChanged-only holds the tree awake. If β proved a different minimal kind, change
    // this ONE array (the rest is kind-agnostic).
    private static readonly WatchEventKind[] WakeKinds = { WatchEventKind.StructureChanged };

    private readonly IUiaEventSource _source;
    private readonly WakeRegistry _registry;
    private readonly WindowManager? _windowManager;

    private readonly object _gate = new();
    private readonly Dictionary<string, IDisposable> _registrations = new();

    public WakeService(IUiaEventSource source, WakeRegistry registry, WindowManager? windowManager)
    {
        _source = source;
        _registry = registry;
        _windowManager = windowManager;
        if (_windowManager is not null)
            _windowManager.WindowInvalidated += OnWindowInvalidated;
    }

    /// <summary>Register + hold a null-sink wake on windowId (its PID pre-resolved by the caller for the spec).
    /// Reserves a wake id (TooManyWatches propagates) then registers transactionally (Remove on failure).</summary>
    public Task<string> WakeAsync(string windowId, int pid)
    {
        var id = _registry.Create(windowId); // TooManyWatches propagates
        try
        {
            // CoalesceScope/ScopeRef are irrelevant for a null sink; pass the window id as a stable scope string.
            var spec = new WatchSubscriptionSpec(id, windowId, pid, WakeKinds, null, windowId);
            var reg = _source.Register(spec, NullSink); // <- the wake: activate AXMode, drop the events
            lock (_gate) _registrations[id] = reg;

            // Race guard: the window can close DURING Register (OnWindowInvalidated runs on the query STA and
            // RemoveByWindow evicts this id from the registry before we stored the disposable). If the registry
            // no longer tracks this id, reconcile — drop + dispose off the STA so no live registration is orphaned.
            if (!_registry.TryGet(id, out _))
            {
                IDisposable? orphan;
                lock (_gate) _registrations.Remove(id, out orphan);
                if (orphan is not null) { var o = orphan; _ = Task.Run(() => { try { o.Dispose(); } catch { } }); }
            }
        }
        catch
        {
            _registry.Remove(id); // no phantom on a failed registration
            throw;
        }
        return Task.FromResult(id);
    }

    /// <summary>Release a held wake by id: dispose its registration (self-marshals onto the query STA), remove it.
    /// Idempotent.</summary>
    public Task ReleaseAsync(string wakeId)
    {
        IDisposable? reg;
        lock (_gate) _registrations.Remove(wakeId, out reg);
        try { reg?.Dispose(); } catch { /* best-effort: handler may already be dead (window gone) */ }
        _registry.Remove(wakeId);
        return Task.CompletedTask;
    }

    /// <summary>The first held wake id on this window, if any (for idempotent reuse by the tool layer).</summary>
    public string? ActiveWakeFor(string windowId) => _registry.FirstByWindow(windowId);

    /// <summary>desktop_list_wakes (§3).</summary>
    public IReadOnlyList<WakeInfo> List() => _registry.List();

    // The NULL SINK (§4.3): fires on a COM RPC thread. Waking registers the handler ONLY to activate AXMode; we
    // DROP the event storm here (236+ StructureChanged on a complex app) — it must NOT reach the watch pipeline.
    private static void NullSink(CapturedEventMeta _, object? __) { /* intentionally empty — drop the event */ }

    // Phase-6 close signal (fires OFF the query STA on the proc.Exited ThreadPool thread). Release every wake on
    // the closed window. STA-REENTRANCY (mirrors WatchService.OnWindowInvalidated): WindowInvalidated can fire ON
    // the query STA (PruneClosedWindows → Invalidate, synchronous); the disposable self-marshals onto that SAME
    // STA and would deadlock if disposed inline — offload the blocking dispose to the ThreadPool. Swallow all: a
    // throw here can be process-fatal on a raw ThreadPool thread.
    private void OnWindowInvalidated(string windowId)
    {
        try
        {
            foreach (var id in _registry.RemoveByWindow(windowId))
            {
                IDisposable? reg;
                lock (_gate) _registrations.Remove(id, out reg);
                if (reg is not null) _ = Task.Run(() => { try { reg.Dispose(); } catch { } });
            }
        }
        catch { /* invalidation-path robustness > surfacing a fault on a process-fatal thread */ }
    }
}
