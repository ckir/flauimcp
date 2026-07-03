using System.Collections.Generic;
using System.Linq;
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Watch;

/// <summary>Public view of one held wake (for desktop_list_wakes, §3).</summary>
public sealed record WakeInfo(string WakeId, string WindowId);

/// <summary>Pure bookkeeping of held accessibility-wake handles (Phase 9 §4.3). SEPARATE from WatchRegistry with
/// its OWN caps so waking a window's UIA tree never consumes the event-watch quota. Thread-safe (locked): the tool
/// layer and the Phase-6 WindowInvalidated evict path both touch it. UIA registration is NOT here — WakeService
/// drives the IUiaEventSource seam with a null sink.</summary>
public sealed class WakeRegistry
{
    // Waking is a baseline perception need (an agent may need Slack+VSCode+Teams+Chrome+Discord awake at once just
    // to SEE them), so the session cap is generous and there is NO per-window cap (one wake per window suffices; a
    // duplicate wake on the same window is allowed but wasteful — the tool layer may reuse instead, Task 5).
    public const int MaxPerSession = 32;

    private readonly object _gate = new();
    private int _counter;
    private readonly Dictionary<string, string> _windowByWake = new(); // wakeId -> windowId

    /// <summary>Reserve a new wake id, enforcing the per-session cap atomically. Throws TooManyWatches when the cap
    /// is reached. The caller (WakeService) then registers the UIA handler and, on failure, calls Remove(id).</summary>
    public string Create(string windowId)
    {
        lock (_gate)
        {
            if (_windowByWake.Count >= MaxPerSession)
                throw new ToolException(ToolErrorCode.TooManyWatches,
                    $"This session already holds {MaxPerSession} accessibility wakes (the per-session cap).",
                    "desktop_release_accessibility a wake you no longer need");
            var id = $"k{++_counter}";
            _windowByWake[id] = windowId;
            return id;
        }
    }

    /// <summary>Idempotent: true if the wake existed and was removed, false otherwise.</summary>
    public bool Remove(string wakeId)
    {
        lock (_gate) return _windowByWake.Remove(wakeId);
    }

    /// <summary>Evict every wake on a (closed) window; returns the removed ids (for handler cleanup).</summary>
    public IReadOnlyList<string> RemoveByWindow(string windowId)
    {
        lock (_gate)
        {
            var ids = _windowByWake.Where(kv => kv.Value == windowId).Select(kv => kv.Key).ToList();
            foreach (var id in ids) _windowByWake.Remove(id);
            return ids;
        }
    }

    /// <summary>The first active wake id on this window, if any (for idempotent reuse in the tool layer).</summary>
    public string? FirstByWindow(string windowId)
    {
        lock (_gate) return _windowByWake.Where(kv => kv.Value == windowId).Select(kv => kv.Key).FirstOrDefault();
    }

    public bool TryGet(string wakeId, out WakeInfo? info)
    {
        lock (_gate)
        {
            if (_windowByWake.TryGetValue(wakeId, out var win)) { info = new WakeInfo(wakeId, win); return true; }
            info = null; return false;
        }
    }

    public IReadOnlyList<WakeInfo> List()
    {
        lock (_gate) return _windowByWake.Select(kv => new WakeInfo(kv.Key, kv.Value)).ToList();
    }
}
