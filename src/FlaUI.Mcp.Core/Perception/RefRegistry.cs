using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>Option-C ref store. Refs (e1, e2, …) are namespaced per window handle and
/// per-snapshot scoped: BeginSnapshot clears a window's refs but never resets its
/// counter, so a stale held ref can never silently alias a new element — it misses
/// with REF_NOT_FOUND. Accessed only from the dispatcher's single query STA, but
/// guarded for safety. Process-wide singleton this phase (per-connection in Phase 6).</summary>
public sealed class RefRegistry
{
    internal sealed record Entry(ElementDescriptor Descriptor, AutomationElement? Cached);

    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, Entry>> _byWindow = new();
    private readonly Dictionary<string, int> _counter = new();
    private readonly Dictionary<string, int> _snapshotSeq = new();

    /// <summary>Clear a window's refs (supersede prior snapshot) and return a new snapshot id.</summary>
    public string BeginSnapshot(string windowId)
    {
        lock (_gate)
        {
            _byWindow[windowId] = new Dictionary<string, Entry>();
            int seq = _snapshotSeq.TryGetValue(windowId, out var s) ? s + 1 : 1;
            _snapshotSeq[windowId] = seq;
            return $"{windowId}:{seq}";
        }
    }

    /// <summary>Register an element; returns its fresh ref (e.g. "e23").</summary>
    public string Register(string windowId, ElementDescriptor descriptor, AutomationElement? cached)
    {
        lock (_gate)
        {
            if (!_byWindow.TryGetValue(windowId, out var map))
                _byWindow[windowId] = map = new Dictionary<string, Entry>();
            int n = _counter.TryGetValue(windowId, out var c) ? c + 1 : 1;
            _counter[windowId] = n;
            var @ref = $"e{n}";
            map[@ref] = new Entry(descriptor, cached);
            return @ref;
        }
    }

    /// <summary>Throw REF_NOT_FOUND if the ref isn't live for this window; else return its entry.
    /// (Element re-resolution is added in Task 6.)</summary>
    internal Entry Lookup(string windowId, string @ref)
    {
        lock (_gate)
        {
            if (_byWindow.TryGetValue(windowId, out var map) && map.TryGetValue(@ref, out var e))
                return e;
            throw new ToolException(ToolErrorCode.RefNotFound,
                $"Ref '{@ref}' is not in the current snapshot of window '{windowId}'.",
                "take a fresh desktop_snapshot and use a ref from it");
        }
    }
}
