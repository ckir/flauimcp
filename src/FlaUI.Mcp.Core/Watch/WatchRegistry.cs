// src/FlaUI.Mcp.Core/Watch/WatchRegistry.cs
using System.Collections.Generic;
using System.Linq;
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Watch;

/// <summary>Public view of one subscription (for desktop_list_watches, §3).</summary>
public sealed record WatchInfo(
    string SubscriptionId, string WindowId,
    IReadOnlyList<WatchEventKind> Kinds, string? Scope, int DroppedCount);

/// <summary>Pure subscription bookkeeping (§3/§9). Thread-safe (locked): the tool layer and the Phase-6
/// WindowInvalidated evict path both touch it. UIA (un)registration is NOT here — WatchService drives the
/// IUiaEventSource seam transactionally around Create/Remove.</summary>
public sealed class WatchRegistry
{
    public const int MaxPerWindow = 5;   // §3 (round-4 Seat Q): bound UIA COM resource use per window
    public const int MaxPerSession = 20; // and per session

    private sealed class Sub
    {
        public required string Id;
        public required string WindowId;
        public required IReadOnlyList<WatchEventKind> Kinds;
        public string? Scope;
        public int DroppedCount;
    }

    private readonly object _gate = new();
    private int _counter;
    private readonly Dictionary<string, Sub> _subs = new();

    /// <summary>Reserve a new subscription id, enforcing per-window AND per-session caps atomically.
    /// Throws TooManyWatches when either cap is reached. The caller (WatchService) then registers UIA
    /// handlers and, on failure, calls Remove(id) so a failed registration leaves no phantom.</summary>
    public string Create(string windowId, IReadOnlyList<WatchEventKind> kinds, string? scope)
    {
        lock (_gate)
        {
            int perWindow = _subs.Values.Count(s => s.WindowId == windowId);
            if (perWindow >= MaxPerWindow)
                throw new ToolException(ToolErrorCode.TooManyWatches,
                    $"Window '{windowId}' already has {MaxPerWindow} active watches (the per-window cap).",
                    "desktop_unwatch one of its subscriptions, or reuse an existing watch");
            if (_subs.Count >= MaxPerSession)
                throw new ToolException(ToolErrorCode.TooManyWatches,
                    $"This session already has {MaxPerSession} active watches (the per-session cap).",
                    "desktop_unwatch a subscription you no longer need");
            var id = $"s{++_counter}";
            _subs[id] = new Sub { Id = id, WindowId = windowId, Kinds = kinds, Scope = scope };
            return id;
        }
    }

    /// <summary>Idempotent: true if the sub existed and was removed, false otherwise (unknown/already-gone).</summary>
    public bool Remove(string subscriptionId)
    {
        lock (_gate) return _subs.Remove(subscriptionId);
    }

    /// <summary>Evict every subscription on a (closed) window; returns the removed ids (for handler cleanup).</summary>
    public IReadOnlyList<string> RemoveByWindow(string windowId)
    {
        lock (_gate)
        {
            var ids = _subs.Values.Where(s => s.WindowId == windowId).Select(s => s.Id).ToList();
            foreach (var id in ids) _subs.Remove(id);
            return ids;
        }
    }

    public void IncrementDropped(string subscriptionId)
    {
        lock (_gate) { if (_subs.TryGetValue(subscriptionId, out var s)) s.DroppedCount++; }
    }

    public bool TryGet(string subscriptionId, out WatchInfo? info)
    {
        lock (_gate)
        {
            if (_subs.TryGetValue(subscriptionId, out var s))
            { info = new WatchInfo(s.Id, s.WindowId, s.Kinds, s.Scope, s.DroppedCount); return true; }
            info = null; return false;
        }
    }

    public IReadOnlyList<WatchInfo> List()
    {
        lock (_gate)
            return _subs.Values.Select(s => new WatchInfo(s.Id, s.WindowId, s.Kinds, s.Scope, s.DroppedCount)).ToList();
    }
}
