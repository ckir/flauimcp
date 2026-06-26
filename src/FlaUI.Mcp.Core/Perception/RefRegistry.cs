using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
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

    /// <summary>Option-C resolution. Order: (1) cached element if its RuntimeId AND ControlType
    /// still match (the ControlType check guards against UIA RuntimeId recycling under
    /// virtualization); (2) AutomationId (or Name+ControlType) scoped under the nearest stable
    /// ancestor; (3) IndexPath as a last-resort fuzzy hint; else REF_STALE_UNRESOLVABLE. The
    /// caller supplies the search roots to walk IN ORDER — the window subtree first, then any
    /// grafted popup subtrees (context menus / dropdowns live at the Desktop, not under the
    /// window). Searching those small, process-correct subtrees — never the whole Desktop —
    /// avoids cross-application false matches on a shared Name+ControlType. searchRoots[0] MUST be
    /// the window root (IndexPath is window-relative). Throws REF_NOT_FOUND if the ref isn't live
    /// for this window. Must be called on the query STA.</summary>
    public AutomationElement Resolve(string windowId, string @ref, IReadOnlyList<AutomationElement> searchRoots)
    {
        var entry = Lookup(windowId, @ref); // REF_NOT_FOUND if absent
        var d = entry.Descriptor;

        // (1) cached fast-path — query-STA only. RuntimeId AND ControlType match AND not offscreen.
        if (entry.Cached is { } cached)
        {
            try
            {
                var rid = cached.Properties.RuntimeId.ValueOrDefault;
                if (rid != null && rid.AsEnumerable().SequenceEqual(d.RuntimeId) && cached.ControlType == d.ControlType
                    && !cached.Properties.IsOffscreen.ValueOrDefault)
                    return cached;
            }
            catch { /* element gone — fall through to the cache-free walk */ }
        }

        return ResolveDescriptor(d, searchRoots, @ref);
    }

    /// <summary>Cache-free re-resolution from a descriptor against caller-supplied roots
    /// (window first, then grafted popups). Used by the ACTION STA, which must NOT touch the
    /// query-STA cached element. Throws REF_STALE_UNRESOLVABLE if the element is gone.</summary>
    public AutomationElement ResolveDescriptor(ElementDescriptor d, IReadOnlyList<AutomationElement> searchRoots, string @ref)
    {
        // (2) descriptor re-walk per search root: AutomationId then Name+ControlType, scoped
        // under the nearest stable ancestor.
        foreach (var searchRoot in searchRoots)
        {
            if (searchRoot is null) continue;
            var scope = searchRoot;
            if (!string.IsNullOrEmpty(d.AncestorAutomationId))
            {
                var anc = TrySearch(searchRoot, cf => cf.ByAutomationId(d.AncestorAutomationId));
                if (anc is not null) scope = anc;
            }
            if (!string.IsNullOrEmpty(d.AutomationId))
            {
                var byAid = TrySearch(scope, cf => cf.ByAutomationId(d.AutomationId));
                if (byAid is not null) return byAid;
            }
            if (!string.IsNullOrEmpty(d.Name))
            {
                var byName = TrySearch(scope, cf => cf.ByName(d.Name).And(cf.ByControlType(d.ControlType)));
                if (byName is not null) return byName;
            }
        }

        // (3) IndexPath last-resort — window-relative only (searchRoots[0] is the window root).
        if (searchRoots.Count > 0)
        {
            var byPath = TryIndexPath(searchRoots[0], d.IndexPath);
            if (byPath is not null) return byPath;
        }

        throw new ToolException(ToolErrorCode.RefStaleUnresolvable,
            $"Ref '{@ref}' could not be re-resolved; the element appears to be gone.",
            "take a fresh desktop_snapshot");
    }

    private static AutomationElement? TrySearch(AutomationElement root,
        Func<ConditionFactory, ConditionBase> cond)
    {
        try { return root.FindFirstDescendant(cond); } catch { return null; }
    }

    private static AutomationElement? TryIndexPath(AutomationElement root, IReadOnlyList<int> path)
    {
        try
        {
            var cur = root;
            foreach (var i in path)
            {
                var kids = cur.FindAllChildren();
                if (i < 0 || i >= kids.Length) return null;
                cur = kids[i];
            }
            return cur;
        }
        catch { return null; }
    }
}
