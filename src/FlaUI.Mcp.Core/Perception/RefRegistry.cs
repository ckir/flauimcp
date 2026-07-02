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

        // (1) cached fast-path — query-STA only. RuntimeId AND ControlType match AND not offscreen AND Name matches.
        if (entry.Cached is { } cached)
        {
            try { if (FastPathMatches(cached, d)) return cached; }
            catch { /* element gone — fall through to the cache-free walk */ }
        }

        return ResolveDescriptor(d, searchRoots, @ref);
    }

    /// <summary>Cache-free re-resolution from a descriptor against caller-supplied roots (window
    /// first, then grafted popups). Used by the ACTION STA, which must NOT touch the query-STA cached
    /// element. <paramref name="mode"/> selects strictness: Strict (state-changing paths, INV-8)
    /// matches ONLY the exact element by live RuntimeId and never rebinds; Lenient (reads) re-walks
    /// the descriptor. Throws REF_STALE_UNRESOLVABLE if the element is gone.</summary>
    public AutomationElement ResolveDescriptor(ElementDescriptor d, IReadOnlyList<AutomationElement> searchRoots,
        string @ref, RefResolveMode mode = RefResolveMode.Lenient)
    {
        if (mode == RefResolveMode.Strict)
            return ResolveStrict(d, searchRoots, @ref);

        // (2) descriptor re-walk. Identity key = AutomationId if present, else Name+ControlType.
        // Gather the ancestor scope(s) (all matching ancestors across all roots; fail-closed if a
        // demanded ancestor is gone — never widen to the whole window), accumulate matches across every
        // scope, dedup by live RuntimeId. >1 distinct => AMBIGUOUS_MATCH. NO positional/IndexPath
        // fallback: an identity-unverified positional match is a data-integrity hazard on a read that
        // feeds the agent's context.
        var scopes = GatherScopes(searchRoots, d, @ref); // fail-closed on a missing/over-matched ancestor
        var matches = new List<AutomationElement>();
        foreach (var scope in scopes)
        {
            if (!string.IsNullOrEmpty(d.AutomationId))
                matches.AddRange(TrySearchAll(scope, cf => cf.ByAutomationId(d.AutomationId)));
            else if (!string.IsNullOrEmpty(d.Name))
                matches.AddRange(TrySearchAll(scope, cf => cf.ByName(d.Name).And(cf.ByControlType(d.ControlType))));
        }

        var distinct = DistinctByRuntimeId(matches);
        if (distinct.Count > 1)
            throw new ToolException(ToolErrorCode.AmbiguousMatch,
                $"Ref '{@ref}' matches {distinct.Count} elements by " +
                    (string.IsNullOrEmpty(d.AutomationId) ? "Name + control type" : $"AutomationId '{d.AutomationId}'") +
                    "; cannot safely pick one for re-resolution.",
                "re-snapshot and use a more specific ref; if the duplication is structural (a fresh snapshot yields the same ambiguity) use coordinate-based tools instead");
        if (distinct.Count == 1)
            return distinct[0];

        throw new ToolException(ToolErrorCode.RefStaleUnresolvable,
            $"Ref '{@ref}' ({Key(d)}) could not be re-resolved; the element appears to be gone.",
            "take a fresh desktop_snapshot");
    }

    // A demanded ancestor matching more than this many containers is treated as irrecoverably ambiguous
    // (and this bounds the M×N per-scope search fan-out a hostile UI could force). Task 3 makes this
    // env-tunable; a plain const here keeps Task 1 self-contained.
    private const int MaxResolveScopes = 512;

    // Safe descriptor label for diagnostics: AutomationId is a developer-assigned constant (safe to log
    // and essential for triage); Name is frequently user content and is NEVER echoed.
    private static string Key(ElementDescriptor d) =>
        string.IsNullOrEmpty(d.AutomationId) ? "no AutomationId" : $"AutomationId '{d.AutomationId}'";

    /// <summary>Resolve the scope(s) to search within. No AncestorAutomationId -> each search root. A
    /// demanded ancestor ABSENT from every root fails closed (REF_STALE_UNRESOLVABLE) rather than
    /// widening to the whole window — silently expanding a targeted scope to a global one is a
    /// confused-deputy retarget. More than MaxResolveScopes matching ancestors -> AMBIGUOUS_MATCH.</summary>
    private static IReadOnlyList<AutomationElement> GatherScopes(
        IReadOnlyList<AutomationElement> searchRoots, ElementDescriptor d, string @ref)
    {
        if (string.IsNullOrEmpty(d.AncestorAutomationId))
        {
            var roots = new List<AutomationElement>();
            foreach (var r in searchRoots) if (r is not null) roots.Add(r);
            return roots;
        }

        var scopes = new List<AutomationElement>();
        foreach (var r in searchRoots)
        {
            if (r is null) continue;
            // The nearest stable ancestor can BE a search root itself (e.g. a top-level control whose
            // only stable ancestor is the window, which carries its own AutomationId) — FindAllDescendants
            // never matches the root it's called on, so check the root's own identity explicitly too.
            if (string.Equals(Safe(() => r.Properties.AutomationId.ValueOrDefault, string.Empty),
                    d.AncestorAutomationId, System.StringComparison.Ordinal))
                scopes.Add(r);
            scopes.AddRange(TrySearchAll(r, cf => cf.ByAutomationId(d.AncestorAutomationId)));
        }
        if (scopes.Count == 0)
            throw new ToolException(ToolErrorCode.RefStaleUnresolvable,
                $"Ref '{@ref}' names ancestor container '{d.AncestorAutomationId}' which is no longer present; not widening the search (avoids retargeting a different control).",
                "take a fresh desktop_snapshot and use a ref from it");
        if (scopes.Count > MaxResolveScopes)
            throw new ToolException(ToolErrorCode.AmbiguousMatch,
                $"Ref '{@ref}' names ancestor container '{d.AncestorAutomationId}' which matches {scopes.Count} elements (> cap {MaxResolveScopes}); too many to safely disambiguate.",
                "this is a structural ambiguity a fresh snapshot cannot resolve — use coordinate-based desktop_click_at, or raise FLAUI_MCP_REF_MAXSCOPES if the UI is legitimately this large");
        return scopes;
    }

    // Collapse duplicates by live RuntimeId (unique among live elements) so the same element found via
    // overlapping roots/scopes counts once. An element whose RuntimeId cannot be read gets a unique key
    // -> kept distinct (conservative: fail-safe toward ambiguity rather than silently binding).
    private static IReadOnlyList<AutomationElement> DistinctByRuntimeId(IReadOnlyList<AutomationElement> els)
    {
        var result = new List<AutomationElement>();
        var seen = new HashSet<string>();
        foreach (var el in els)
        {
            string key;
            try
            {
                var rid = el.Properties.RuntimeId.ValueOrDefault;
                key = rid != null ? string.Join(",", rid) : "unreadable:" + result.Count;
            }
            catch { key = "unreadable:" + result.Count; }
            if (seen.Add(key)) result.Add(el);
        }
        return result;
    }

    /// <summary>Strict identity re-resolution (INV-8): return ONLY the element whose live RuntimeId
    /// equals the descriptor's. Within the ancestor scope(s) NARROW with a native UIA query
    /// (AutomationId, else Name+ControlType) and RuntimeId-verify the handful — never a full-subtree
    /// scan. EXACTLY one match required (0 -> gone/recycled -> REF_STALE; a spoofed >1 -> AMBIGUOUS).
    /// No captured RuntimeId, or no queryable key, -> fail closed.</summary>
    private static AutomationElement ResolveStrict(ElementDescriptor d,
        IReadOnlyList<AutomationElement> searchRoots, string @ref)
    {
        if (d.RuntimeId.Count == 0)
            throw new ToolException(ToolErrorCode.RefStaleUnresolvable,
                $"Ref '{@ref}' ({Key(d)}) has no stable UIA identity (RuntimeId) to re-verify for a state-changing action.",
                "this element (or app) does not expose a stable identity; if a fresh desktop_snapshot keeps failing, use coordinate-based desktop_click_at instead");

        var scopes = GatherScopes(searchRoots, d, @ref); // fail-closed on a missing/over-matched ancestor
        // Return the (unique-among-live-elements) element whose live RuntimeId matches. Short-circuit on
        // the first match — NO accumulate/dedup: a dedup would be BY RuntimeId, and every hit already has
        // the SAME RuntimeId, so it would always collapse to one and could never signal a spoof. A
        // malicious custom UIA provider that deliberately reissues a colliding RuntimeId on a decoy is
        // out of the practical threat model (it needs attacker-controlled provider code); RuntimeId
        // uniqueness among live elements is assumed, so the first RuntimeId match IS the element.
        foreach (var scope in scopes)
        {
            try { if (RidMatches(scope, d.RuntimeId)) return scope; } catch { /* keep scanning */ } // scope itself could be the target (popup root)

            IReadOnlyList<AutomationElement> candidates =
                !string.IsNullOrEmpty(d.AutomationId)
                    ? TrySearchAll(scope, cf => cf.ByAutomationId(d.AutomationId))
                    : !string.IsNullOrEmpty(d.Name)
                        ? TrySearchAll(scope, cf => cf.ByName(d.Name).And(cf.ByControlType(d.ControlType)))
                        : System.Array.Empty<AutomationElement>();

            foreach (var el in candidates)
            {
                try { if (RidMatches(el, d.RuntimeId)) return el; } catch { /* one flaky node — keep scanning */ }
            }
        }

        throw new ToolException(ToolErrorCode.RefStaleUnresolvable,
            $"Ref '{@ref}' ({Key(d)}) could not be re-resolved to the exact element for a state-changing action; it appears to be gone or was recycled.",
            "take a fresh desktop_snapshot; if this app's UIA identity is unstable, an operator can set FLAUI_MCP_REF_STRICT=off (disables the INV-8 guard)");
    }

    private static bool RidMatches(AutomationElement el, IReadOnlyList<int> runtimeId)
    {
        var rid = el.Properties.RuntimeId.ValueOrDefault;
        return rid != null && rid.AsEnumerable().SequenceEqual(runtimeId);
    }

    private static IReadOnlyList<AutomationElement> TrySearchAll(AutomationElement root,
        Func<ConditionFactory, ConditionBase> cond)
    {
        try { return root.FindAllDescendants(cond); } catch { return System.Array.Empty<AutomationElement>(); }
    }

    internal static bool FastPathMatches(AutomationElement cached, ElementDescriptor d)
    {
        var rid = cached.Properties.RuntimeId.ValueOrDefault;
        return rid != null && rid.AsEnumerable().SequenceEqual(d.RuntimeId)
            && cached.ControlType == d.ControlType
            && !cached.Properties.IsOffscreen.ValueOrDefault
            && string.Equals(Safe(() => cached.Name, ""), d.Name, System.StringComparison.Ordinal);
    }

    private static T Safe<T>(Func<T> read, T fallback) { try { return read(); } catch { return fallback; } }
}
