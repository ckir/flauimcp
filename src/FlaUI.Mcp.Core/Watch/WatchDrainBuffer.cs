// src/FlaUI.Mcp.Core/Watch/WatchDrainBuffer.cs
using System;
using System.Collections.Generic;

namespace FlaUI.Mcp.Core.Watch;

/// <summary>§Spike-B push+drain: a bounded per-subscription ring the agent polls (via desktop_drain_events)
/// so a host that does NOT surface server→client notifications can still recover streamed events. Thread-safe
/// (locked): the worker WatchPump appends while the tool thread drains and the invalidate/unwatch path removes.
/// Pure/headless — holds already-built payloads only; carries NO TTL/expiry (agy risk#3: a buffered ref must
/// not be expired before the agent drains — ref lifetime is bounded solely by the RefRegistry 64-cap).</summary>
public sealed class WatchDrainBuffer
{
    /// <summary>agy risk#1: small, and &lt;&lt; RefRegistry.EventRefCap(64) so the number of buffered payloads
    /// referencing a still-live event ref stays well inside the ref pool (bounds the stale-ref window).</summary>
    public const int PerSubCap = 20;

    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<DesktopEventPayload>> _queues = new();

    /// <summary>Enqueue a built payload for the subscription. If the sub's queue then exceeds
    /// <see cref="PerSubCap"/>, dequeue the OLDEST and return true (a drop happened — the caller bumps the
    /// registry's droppedCount, agy risk#2 summed-drops); otherwise return false.</summary>
    public bool Append(string subscriptionId, DesktopEventPayload payload)
    {
        lock (_gate)
        {
            if (!_queues.TryGetValue(subscriptionId, out var q))
                _queues[subscriptionId] = q = new Queue<DesktopEventPayload>();
            q.Enqueue(payload);
            if (q.Count > PerSubCap)
            {
                q.Dequeue();
                return true;
            }
            return false;
        }
    }

    /// <summary>Remove and return up to <paramref name="max"/> (or ALL when null/&lt;=0) oldest-first payloads,
    /// leaving the rest buffered.</summary>
    public IReadOnlyList<DesktopEventPayload> Drain(string subscriptionId, int? max)
    {
        lock (_gate)
        {
            if (!_queues.TryGetValue(subscriptionId, out var q) || q.Count == 0)
                return Array.Empty<DesktopEventPayload>();
            int take = (max is null || max.Value <= 0) ? q.Count : Math.Min(max.Value, q.Count);
            var result = new List<DesktopEventPayload>(take);
            for (int i = 0; i < take; i++) result.Add(q.Dequeue());
            return result;
        }
    }

    /// <summary>Drop the whole queue for a subscription (on unwatch / window-evict).</summary>
    public void Remove(string subscriptionId)
    {
        lock (_gate) _queues.Remove(subscriptionId);
    }
}
