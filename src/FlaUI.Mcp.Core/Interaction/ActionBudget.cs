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

    /// <summary>Non-consuming peek: does this window currently have a free budget slot? Used by
    /// desktop_paste_text's pre-flight to fail-closed BEFORE mutating the clipboard, without spending
    /// the slot (the later KeyChord's TryConsume spends it). Prunes the window's expired hits like
    /// TryConsume, but never enqueues. Mirrors TryConsume's lease-renewal reset: a fresh unlock
    /// (leaseWriteUtc past what we've consumed against) means the budget is reset, so report "free" —
    /// the actual _hits.Clear() is left to the consuming TryConsume so the peek stays side-effect-light.</summary>
    public bool HasFreeSlot(nint window, DateTime now, DateTime leaseWriteUtc)
    {
        lock (_gate)
        {
            if (leaseWriteUtc > _seenLeaseWrite) return true; // fresh unlock -> budget reset -> free
            if (!_hits.TryGetValue(window, out var q)) return true;
            var cutoff = now.AddSeconds(-_windowSeconds);
            while (q.Count > 0 && q.Peek() <= cutoff) q.Dequeue();
            return q.Count < _max;
        }
    }

    /// <summary>Whole seconds until this window's oldest action ages out and a budget slot frees
    /// (0 if the window currently has spare budget). For the InputBudgetExceeded recovery hint.</summary>
    public int SecondsUntilFreeSlot(nint window, DateTime now)
    {
        lock (_gate)
        {
            if (!_hits.TryGetValue(window, out var q) || q.Count < _max) return 0;
            var secs = (q.Peek().AddSeconds(_windowSeconds) - now).TotalSeconds;
            return (int)Math.Max(0, Math.Ceiling(secs));
        }
    }
}
