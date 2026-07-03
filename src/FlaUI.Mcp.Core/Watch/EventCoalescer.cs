// src/FlaUI.Mcp.Core/Watch/EventCoalescer.cs
using System;
using System.Collections.Generic;

namespace FlaUI.Mcp.Core.Watch;

/// <summary>§8 pure back-pressure/coalescing core. NOT thread-safe — the wiring drives it from the
/// single worker thread. Time is injected for deterministic tests. Holds one pending aggregate per
/// coalesce key; structure_changed aggregates debounce (settle) before becoming ready.</summary>
public sealed class EventCoalescer
{
    public sealed record Pending(CapturedEventMeta Meta, int CoalescedCount, DateTime FirstSeenUtc, DateTime LastSeenUtc);

    private sealed class Slot { public CapturedEventMeta Meta; public int Count; public DateTime First; public DateTime Last; }

    private readonly int _capacity;
    private readonly int _debounceMs;
    private readonly Dictionary<string, Slot> _pending = new();
    private readonly LinkedList<string> _order = new(); // insertion order for oldest-eviction

    public EventCoalescer(int capacity = 256, int debounceMs = 100)
    {
        _capacity = capacity < 1 ? 1 : capacity;
        _debounceMs = debounceMs < 0 ? 0 : debounceMs;
    }

    /// <summary>True when there is at least one pending aggregate. The WatchPump uses this to choose
    /// block-on-channel (idle) vs. race-a-short-timer (items settling) — Task 8's conditional wake.</summary>
    public bool HasPending => _pending.Count > 0;

    /// <summary>Offer an event under its coalesce key. Merges into an existing key (bumps count/lastSeen)
    /// or inserts a new one. If inserting a NEW key exceeds capacity, evict the OLDEST distinct key and
    /// return its SubscriptionId so the caller bumps that sub's droppedCount; otherwise returns null.</summary>
    public string? Offer(string coalesceKey, CapturedEventMeta meta, DateTime nowUtc)
    {
        if (_pending.TryGetValue(coalesceKey, out var slot))
        {
            slot.Meta = meta;          // keep the freshest meta (latest timestamp/source)
            slot.Count++;
            slot.Last = nowUtc;
            return null;
        }

        string? droppedSub = null;
        if (_pending.Count >= _capacity)
        {
            var oldestKey = _order.First!.Value;
            _order.RemoveFirst();
            if (_pending.Remove(oldestKey, out var evicted))
                droppedSub = evicted.Meta.SubscriptionId;
        }

        _pending[coalesceKey] = new Slot { Meta = meta, Count = 1, First = nowUtc, Last = nowUtc };
        _order.AddLast(coalesceKey);
        return droppedSub;
    }

    /// <summary>Remove and return every aggregate ready to emit at nowUtc. structure_changed is ready once
    /// quiet for debounceMs; every other kind is ready immediately.</summary>
    public IReadOnlyList<Pending> Drain(DateTime nowUtc)
    {
        var ready = new List<Pending>();
        var emit = new List<string>();
        foreach (var (key, slot) in _pending)
        {
            bool settled = slot.Meta.Kind != WatchEventKind.StructureChanged
                || (nowUtc - slot.Last).TotalMilliseconds >= _debounceMs;
            if (settled) emit.Add(key);
        }
        foreach (var key in emit)
        {
            var s = _pending[key];
            ready.Add(new Pending(s.Meta, s.Count, s.First, s.Last));
            _pending.Remove(key);
            _order.Remove(key);
        }
        return ready;
    }
}
