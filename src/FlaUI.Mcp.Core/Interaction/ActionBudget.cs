using System;
using System.Collections.Generic;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Per-target-window rolling rate limit. Stops an injected click/keystroke flood from turning
/// a valid lease window into a blank cheque. Resets a window's counter when the lease write-time
/// advances (a fresh `unlock`). Deterministic — caller passes `now` and `leaseWriteUtc`.</summary>
public sealed class ActionBudget
{
    private readonly int _max;
    private readonly double _windowSeconds;
    private readonly Dictionary<nint, Queue<DateTime>> _hits = new();
    private DateTime _seenLeaseWrite = default;
    private readonly object _gate = new();

    public ActionBudget(int maxPerWindow = 60, double windowSeconds = 60)
    { _max = maxPerWindow; _windowSeconds = windowSeconds; }

    public bool TryConsume(nint window, DateTime now, DateTime leaseWriteUtc)
    {
        lock (_gate)
        {
            if (leaseWriteUtc > _seenLeaseWrite) { _hits.Clear(); _seenLeaseWrite = leaseWriteUtc; }
            if (!_hits.TryGetValue(window, out var q)) { q = new Queue<DateTime>(); _hits[window] = q; }
            var cutoff = now.AddSeconds(-_windowSeconds);
            while (q.Count > 0 && q.Peek() <= cutoff) q.Dequeue();
            if (q.Count >= _max) return false;
            q.Enqueue(now);
            return true;
        }
    }
}
