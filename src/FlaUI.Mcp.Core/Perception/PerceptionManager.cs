using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>Core façade for perception. Orchestrates SnapshotEngine + RefRegistry on the
/// query STA via WindowManager. RunOnRefAsync (option-C resolution) is added in Task 6.</summary>
public sealed class PerceptionManager
{
    private readonly WindowManager _windows;
    private readonly RefRegistry _refs;
    private readonly SnapshotCache _cache;

    // Break-glass: FLAUI_MCP_REF_STRICT=off forces Lenient on state-changing paths too (disables INV-8).
    // The env->mode mapping lives in RefResolveConfig.WriteMode so it is unit-tested (see Step 1).
    private static readonly RefResolveMode WriteMode =
        RefResolveConfig.WriteMode(System.Environment.GetEnvironmentVariable("FLAUI_MCP_REF_STRICT"));

    // Node cap for the Selector bounded-BFS resolver (Phase 10 #2 T4). No existing traversal/scope-cap
    // env var fit this (FLAUI_MCP_REF_MAXSCOPES bounds ANCESTOR fan-out in RefRegistry.GatherScopes, a
    // different axis), so this is a NEW var mirroring that one's parse idiom.
    private static readonly int MaxSelectorNodes =
        RefResolveConfig.MaxSelectorNodes(System.Environment.GetEnvironmentVariable("FLAUI_MCP_SELECTOR_MAXNODES"));

    public PerceptionManager(WindowManager windows, RefRegistry refs, SnapshotCache cache)
    {
        _windows = windows;
        _refs = refs;
        _cache = cache;
        // Phase 6: close signal → evict the window's refs, but MARSHALED onto the single query STA via
        // PostToQuerySta. RefRegistry is only otherwise mutated (BeginSnapshot/Register) on that STA, so
        // routing eviction through it too keeps ALL RefRegistry mutations serialized on one thread: an
        // evict fired while a snapshot/find walk of the same window is in flight simply queues BEHIND the
        // walk (walk finishes registering, THEN evict wipes) — no mid-walk counter restart / ref aliasing
        // and no orphaned cached-COM leak. The push signal can originate off-STA (proc.Exited on a
        // ThreadPool thread); the marshal is what makes that safe.
        // Lifetime: on stdio, WindowManager/RefRegistry/PerceptionManager are ALL process-lifetime
        // singletons (Program.cs), so this '+=' never leaks. FUTURE HTTP/SSE NOTE: if RefRegistry becomes
        // per-connection while WindowManager stays a singleton, this subscription would root every dropped
        // connection's RefRegistry — that phase must make PerceptionManager IDisposable and '-=' on
        // teardown. Do NOT add that now (YAGNI — no second connection exists on stdio).
        _windows.WindowInvalidated += id => _windows.PostToQuerySta(() => _refs.EvictWindow(id));
    }

    /// <summary>Resolve a ref to its live element on the query STA and run a read over it.
    /// The element never crosses the STA boundary (COM is thread-affine) — only the
    /// projection T returns.</summary>
    public Task<T> RunOnRefAsync<T>(WindowHandle handle, string @ref, Func<AutomationElement, T> func) =>
        _windows.RunWithWindowAndDesktopAsync(handle, (win, desktop) =>
        {
            var el = _refs.Resolve(handle.Id, @ref, PopupFinder.SearchRoots(win, desktop));
            return func(el);
        });

    /// <summary>Resolve a ref and run a state-changing pattern action on a TRANSIENT action STA.
    /// The descriptor is read here (plain data, thread-safe); the element is re-resolved CACHE-FREE
    /// on the action STA against window+popup roots built by that STA's own automation — no query-STA
    /// COM object crosses apartments. An offscreen target is rejected before acting (offscreen Invoke
    /// can hang). On modal block past timeoutMs the call surfaces ACTION_BLOCKED_PENDING.</summary>
    public Task<T> RunOnRefActionAsync<T>(WindowHandle handle, string @ref, Func<AutomationElement, T> func, int timeoutMs)
    {
        var descriptor = _refs.Lookup(handle.Id, @ref).Descriptor; // REF_NOT_FOUND if absent (cheap, off-STA)
        return _windows.RunOnWindowActionAsync(handle, (win, desktop) =>
        {
            var roots = PopupFinder.SearchRoots(win, desktop);
            var el = _refs.ResolveDescriptor(descriptor, roots, @ref, WriteMode); // INV-8 (break-glass: FLAUI_MCP_REF_STRICT=off)
            if (el.Properties.IsOffscreen.ValueOrDefault)
                throw new ToolException(ToolErrorCode.ElementNotActionable,
                    "Element is off-screen; cannot act on it reliably.", "desktop_scroll_into_view then retry");
            return func(el);
        }, timeoutMs);
    }

    /// <summary>Like RunOnRefActionAsync but the callback also receives the resolved top-level WINDOW
    /// element (for input targeting: the ActionTarget's Root/Pid/Class come from the window, while the
    /// action runs against the ref'd element). Same transient-action-STA + offscreen preflight.</summary>
    public Task<T> RunOnRefForInputAsync<T>(WindowHandle handle, string @ref,
        Func<AutomationElement, AutomationElement, T> func, int timeoutMs)
    {
        var descriptor = _refs.Lookup(handle.Id, @ref).Descriptor;
        return _windows.RunOnWindowActionAsync(handle, (win, desktop) =>
        {
            var roots = PopupFinder.SearchRoots(win, desktop);
            var el = _refs.ResolveDescriptor(descriptor, roots, @ref, WriteMode); // INV-8 (break-glass: FLAUI_MCP_REF_STRICT=off)
            if (el.Properties.IsOffscreen.ValueOrDefault)
                throw new ToolException(ToolErrorCode.ElementNotActionable,
                    "Element is off-screen; cannot act on it reliably.", "desktop_scroll_into_view then retry");
            return func(win, el);
        }, timeoutMs);
    }

    /// <summary>Resolve a ref and run a READ on a TRANSIENT STA (timeout-guarded), cache-free
    /// like the action path but WITHOUT the offscreen preflight — reads are allowed on
    /// off-screen elements (matching desktop_snapshot includeOffscreen). GetItem/GetText can
    /// force the target app to realize layout, so the abandonable transient STA + timeout
    /// protects the long-lived query STA. Shares the action in-flight cap (MaxPendingActions).</summary>
    public Task<T> RunOnRefReadAsync<T>(WindowHandle handle, string @ref, Func<AutomationElement, T> func, int timeoutMs)
    {
        var descriptor = _refs.Lookup(handle.Id, @ref).Descriptor;
        return _windows.RunOnWindowActionAsync(handle, (win, desktop) =>
        {
            var roots = PopupFinder.SearchRoots(win, desktop);
            var el = _refs.ResolveDescriptor(descriptor, roots, @ref, RefResolveMode.Lenient); // read: descriptor re-walk (ambiguity-aware)
            return func(el);
        }, timeoutMs);
    }

    /// <summary>Phase 10 #2 T4 (the crux): resolve a Selector to EXACTLY ONE live element, INSIDE the
    /// action-STA callback, against a caller-supplied root (the window, or a scope subtree already
    /// resolved cache-free by the caller). Deliberately NOT root.FindAllDescendants(...) — that is a
    /// synchronous native whole-tree COM call the action STA's timeoutMs can time out the AWAIT of but
    /// cannot interrupt the blocked STA thread itself, so a broad selector would permanently wedge the
    /// single action STA. Instead this walks a BOUNDED, caller-controlled BFS (FindAllChildren level by
    /// level) and evaluates the WHOLE match (automationId / controlType / name+enabled via
    /// FindQuerySpec.MatchesPostFilter, matched on the REDACTED name — mirrors FindAsync's INV-5
    /// redact-before-match so a selector can never oracle a password field's real name) in managed code
    /// per node, capped at MaxSelectorNodes visited nodes. count==1 requires finding ALL matches (to
    /// detect ambiguity), so this cannot early-exit on the first hit — it walks the whole (capped)
    /// subtree collecting every hit, then fails closed unless exactly one was found. Mints a
    /// DESCRIPTOR-ONLY ref (cached: null) — unlike a snapshot/find ref there is no query-STA cached
    /// element to reuse; the descriptor is the only durable handle. Must be called ON the action STA
    /// (it takes no STA dependency itself - it only walks the AutomationElement tree it's given).
    /// BOUNDING (honest residual): the visited cap PLUS the per-node fan-out guard below (each
    /// FindAllChildren result must fit the remaining scan budget) bound the aggregate walk and fail
    /// closed. One FindAllChildren() call still marshals a single node's direct-child array atomically
    /// (unavoidable through this API); this is an accepted narrow residual because UIA direct-child
    /// fan-out is virtualization-bounded in practice, unlike the transitive FindAllDescendants this
    /// design replaces. A TreeWalker is deliberately NOT used — its control-view vs raw-view children
    /// could differ from FindAllChildren and silently change which nodes the count==1 guarantee sees.</summary>
    private (AutomationElement El, string Ref) ResolveSelectorOnSta(string windowId, AutomationElement root, Selector sel)
    {
        var q = sel.ToFindQuery();
        var spec = new FindQuerySpec(q);
        bool hasCtConstraint = FindQuerySpec.TryParseControlType(q.ControlType, out var wantedCt);

        // A matched node carries its already-read primitives (read ONCE, reused for the descriptor
        // mint) — mirrors FindAsync's "Read each primitive ONCE; reuse for BOTH" idiom and closes the
        // TOCTOU where a live-updating control's Name/ControlType could differ between match and mint.
        var hits = new List<(AutomationElement El, int[] Rid, FlaUI.Core.Definitions.ControlType Ct,
            string Aid, string RawName, bool HasFocus)>();

        int visited = 0;

        // Fail-closed if a single node's direct-child array exceeds the REMAINING scan budget: one
        // FindAllChildren() marshals that whole array atomically, so a huge direct-child set could
        // spike memory / block the action STA before the per-node visited check fires. Budget shrinks
        // as the walk proceeds (MaxSelectorNodes - visited).
        void GuardFanOut(AutomationElement[] children)
        {
            if (children.Length > MaxSelectorNodes - visited)
                throw new ToolException(ToolErrorCode.InvalidArguments,
                    "selector too broad — a node exposes more direct children than the remaining scan budget.",
                    "narrow it: add automationId, a scope, or a more specific controlType");
        }

        var seed = SafeRead(() => root.FindAllChildren(), System.Array.Empty<AutomationElement>());
        GuardFanOut(seed);
        var queue = new Queue<AutomationElement>(seed);

        while (queue.Count > 0)
        {
            var el = queue.Dequeue();
            visited++;
            if (visited > MaxSelectorNodes)
                throw new ToolException(ToolErrorCode.InvalidArguments,
                    "selector too broad — scanned > N nodes without a bounded result.",
                    "narrow it: add automationId, a scope, or a more specific controlType");

            // Managed match, entirely in-process — no native ConditionBase for this walk.
            string aid = SafeRead(() => el.AutomationId, "") ?? string.Empty;
            var ctEnum = SafeRead(() => el.ControlType, FlaUI.Core.Definitions.ControlType.Custom);
            bool aidOk = string.IsNullOrEmpty(q.AutomationId)
                || string.Equals(aid, q.AutomationId, System.StringComparison.Ordinal);
            bool ctOk = !hasCtConstraint || ctEnum == wantedCt;
            if (aidOk && ctOk)
            {
                // INV-5: redact BEFORE the match decision, matching FindAsync (PerceptionManager.cs:281-292)
                // — a selector must not be usable as a password-field name oracle.
                bool isPwd = RedactionPolicy.IsPasswordOrFailClosed(() => el.Properties.IsPassword.ValueOrDefault);
                string rawName = SafeRead(() => el.Name, "") ?? string.Empty;
                string name = isPwd ? "[REDACTED]" : rawName;
                bool enabled = SafeRead(() => el.IsEnabled, false);
                if (spec.MatchesPostFilter(name, enabled))
                {
                    // Read the remaining descriptor primitives HERE (only on a match) and capture them
                    // with the element — no second read of the winner after the loop (no double-read,
                    // no TOCTOU). Descriptor keeps the RAW name for re-resolution.
                    int[] rid = SafeRead(() => el.Properties.RuntimeId.ValueOrDefault, (int[]?)null) ?? System.Array.Empty<int>();
                    bool hasFocus = SafeRead(() => el.Properties.HasKeyboardFocus.ValueOrDefault, false);
                    hits.Add((el, rid, ctEnum, aid, rawName, hasFocus));
                }
            }

            var children = SafeRead(() => el.FindAllChildren(), System.Array.Empty<AutomationElement>());
            GuardFanOut(children);
            foreach (var child in children)
                queue.Enqueue(child);
        }

        if (hits.Count == 0)
            throw new ToolException(ToolErrorCode.SelectorNoMatch,
                "selector matched no element in this window right now.",
                "the target may not be present yet — reveal it (act / desktop_wait_for) then retry, or desktop_snapshot to see current state");
        if (hits.Count > 1)
            throw new ToolException(ToolErrorCode.AmbiguousMatch,
                $"selector matched {hits.Count} elements; cannot safely pick one.",
                "refine: add controlType/automationId, add a scope, set ignoreCase:false for an exact-case name, or desktop_snapshot and target a unique eN");

        var h = hits[0];
        var descriptor = new ElementDescriptor(h.Rid, h.Ct, h.Aid, h.RawName,
            SnapshotEngine.NearestAncestorAutomationId(h.El), System.Array.Empty<int>(), h.HasFocus);
        var mintedRef = _refs.Register(windowId, descriptor, cached: null); // descriptor-only mint (Selector, Task 4)
        return (h.El, mintedRef);
    }

    /// <summary>Selector counterpart to RunOnRefActionAsync: resolve sel.Scope (if set) cache-free
    /// off-STA to a descriptor, then on the SAME transient action STA re-resolve that scope descriptor
    /// (or use the window itself), run the bounded selector walk, apply the identical offscreen
    /// preflight, and run the state-changing action. Threading/gates are byte-for-byte the ref
    /// sibling's — the only difference is resolution is a fresh bounded walk (no prior descriptor to be
    /// stale against) and the mint is descriptor-only.</summary>
    public Task<(T Value, string ResolvedRef)> RunOnSelectorActionAsync<T>(WindowHandle handle, Selector sel,
        Func<AutomationElement, T> func, int timeoutMs)
    {
        var scopeDescriptor = string.IsNullOrEmpty(sel.Scope) ? null : _refs.Lookup(handle.Id, sel.Scope!).Descriptor;
        return _windows.RunOnWindowActionAsync(handle, (win, desktop) =>
        {
            var roots = PopupFinder.SearchRoots(win, desktop);
            AutomationElement root = scopeDescriptor is null
                ? win
                : _refs.ResolveDescriptor(scopeDescriptor, roots, sel.Scope!, WriteMode);
            var (el, r) = ResolveSelectorOnSta(handle.Id, root, sel);
            if (el.Properties.IsOffscreen.ValueOrDefault)
                throw new ToolException(ToolErrorCode.ElementNotActionable,
                    "Element is off-screen; cannot act on it reliably.", "desktop_scroll_into_view then retry");
            return (func(el), r);
        }, timeoutMs);
    }

    /// <summary>Like RunOnSelectorActionAsync but the callback also receives the resolved top-level
    /// WINDOW element (for input targeting, mirroring RunOnRefForInputAsync). Same transient-action-STA,
    /// scope handling, and offscreen preflight.</summary>
    public Task<(T Value, string ResolvedRef)> RunOnSelectorForInputAsync<T>(WindowHandle handle, Selector sel,
        Func<AutomationElement, AutomationElement, T> func, int timeoutMs)
    {
        var scopeDescriptor = string.IsNullOrEmpty(sel.Scope) ? null : _refs.Lookup(handle.Id, sel.Scope!).Descriptor;
        return _windows.RunOnWindowActionAsync(handle, (win, desktop) =>
        {
            var roots = PopupFinder.SearchRoots(win, desktop);
            AutomationElement root = scopeDescriptor is null
                ? win
                : _refs.ResolveDescriptor(scopeDescriptor, roots, sel.Scope!, WriteMode);
            var (el, r) = ResolveSelectorOnSta(handle.Id, root, sel);
            if (el.Properties.IsOffscreen.ValueOrDefault)
                throw new ToolException(ToolErrorCode.ElementNotActionable,
                    "Element is off-screen; cannot act on it reliably.", "desktop_scroll_into_view then retry");
            return (func(win, el), r);
        }, timeoutMs);
    }

    /// <summary>Like RunOnSelectorActionAsync but for a READ (mirrors RunOnRefReadAsync): scope
    /// resolution and the walk's own count==1 gate are unchanged, but there is NO offscreen preflight —
    /// reads are allowed on off-screen elements — and scope resolution uses Lenient (read semantics),
    /// not WriteMode.</summary>
    public Task<(T Value, string ResolvedRef)> RunOnSelectorReadAsync<T>(WindowHandle handle, Selector sel,
        Func<AutomationElement, T> func, int timeoutMs)
    {
        var scopeDescriptor = string.IsNullOrEmpty(sel.Scope) ? null : _refs.Lookup(handle.Id, sel.Scope!).Descriptor;
        return _windows.RunOnWindowActionAsync(handle, (win, desktop) =>
        {
            var roots = PopupFinder.SearchRoots(win, desktop);
            AutomationElement root = scopeDescriptor is null
                ? win
                : _refs.ResolveDescriptor(scopeDescriptor, roots, sel.Scope!, RefResolveMode.Lenient);
            var (el, r) = ResolveSelectorOnSta(handle.Id, root, sel);
            return (func(el), r);
        }, timeoutMs);
    }

    // Replicate the snapshot security floor for targeted reads (they bypass SnapshotEngine).
    private static void EnsureAllowed(AutomationElement el)
    {
        var procName = SafeProcessName(el);
        if (PerceptionPolicy.IsDenied(procName))
            throw new ToolException(ToolErrorCode.TargetDenied,
                $"Reading content from windows owned by '{procName}' is blocked (credential store).",
                "target a different, non-sensitive element");
    }

    // Verbatim-extracted read lambda from GetGridCellAsync (Phase 10 #2 T7): the shared body for both
    // the ref path (RunOnRefReadAsync) and the selector path (RunOnSelectorReadAsync) — byte-identical
    // logic, no behavior change; existing GetGridCellAsync tests are the oracle that the extraction
    // preserved behavior.
    private static GridCellInfo ReadGridCell(AutomationElement el, int row, int col)
    {
        EnsureAllowed(el);
        try
        {
            var gp = el.Patterns.Grid.PatternOrDefault
                ?? throw new ToolException(ToolErrorCode.PatternUnsupported, "Element does not support the Grid pattern.", "pick a grid/table element");
            int rows = gp.RowCount.ValueOrDefault, cols = gp.ColumnCount.ValueOrDefault;
            if (row < 0 || col < 0 || row >= rows || col >= cols)
                throw new ToolException(ToolErrorCode.GridCellOutOfRange, $"Cell ({row},{col}) is outside the {rows}x{cols} grid.", "use in-range 0-based row/col");
            var cell = gp.GetItem(row, col)
                ?? throw new ToolException(ToolErrorCode.GridCellOutOfRange, $"Grid has no realized cell at ({row},{col}).", "scroll the grid to realize the row, then retry");
            // Defensive UIA reads — a dynamically-realized cell from a faulty provider can throw
            // COMException on a property/pattern access; mirror EvaluateSelectorValueAsync's
            // try/catch-per-read so a flaky cell degrades gracefully, never leaks as INTERNAL.
            bool isPwd = RedactionPolicy.IsPasswordOrFailClosed(() => cell.Properties.IsPassword.ValueOrDefault);
            string value;
            if (isPwd) value = "[REDACTED]";
            else
            {
                string? v = null;
                try { v = cell.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault; } catch { }
                if (string.IsNullOrEmpty(v)) { try { v = cell.Name; } catch { } }
                value = v ?? string.Empty;
            }
            string ct = "Unknown", aid = string.Empty;
            try { ct = cell.ControlType.ToString(); } catch { }
            try { aid = cell.Properties.AutomationId.ValueOrDefault ?? string.Empty; } catch { }
            return new GridCellInfo(value, ct, aid, isPwd);
        }
        catch (System.UnauthorizedAccessException)
        { throw new ToolException(ToolErrorCode.AccessDeniedIntegrity, "Cannot read the target (higher-integrity/elevated window).", "run the target at the same integrity level"); }
        catch (System.Runtime.InteropServices.COMException)
        { throw new ToolException(ToolErrorCode.ElementNotActionable, "The grid provider threw while reporting its cells.", "re-snapshot the grid and retry"); }
    }

    public Task<GridCellInfo> GetGridCellAsync(WindowHandle handle, string @ref, int row, int col, int timeoutMs) =>
        RunOnRefReadAsync(handle, @ref, el => ReadGridCell(el, row, col), timeoutMs);

    /// <summary>Selector twin of GetGridCellAsync (Phase 10 #2 T7): identical ReadGridCell body, resolved
    /// via the bounded selector walk (RunOnSelectorReadAsync — Lenient, no offscreen guard, mirrors the
    /// ref read path) instead of a ref lookup.</summary>
    public Task<(GridCellInfo Value, string ResolvedRef)> GetGridCellBySelectorAsync(WindowHandle handle, Selector sel, int row, int col, int timeoutMs) =>
        RunOnSelectorReadAsync(handle, sel, el => ReadGridCell(el, row, col), timeoutMs);

    // Verbatim-extracted read lambda from GetTextAsync (Phase 10 #2 T7): byte-identical logic (password
    // short-circuit, TextPattern read, truncation) shared by the ref and selector read paths.
    private static TextReadResult ReadText(AutomationElement el, bool selectionOnly, int maxLength, bool fromEnd)
    {
        EnsureAllowed(el);
        // Password short-circuit FIRST — never ask the provider for a secret's text/selection.
        // Read IsPassword defensively (a COMException here must not bypass clean handling and
        // surface as INTERNAL); if it can't be read it's a flaky non-password field → proceed.
        bool isPwd = RedactionPolicy.IsPasswordOrFailClosed(() => el.Properties.IsPassword.ValueOrDefault);
        if (isPwd) return new TextReadResult("[REDACTED]", false, true);
        try
        {
            var tp = el.Patterns.Text.PatternOrDefault
                ?? throw new ToolException(ToolErrorCode.PatternUnsupported, "Element does not support the Text pattern.", "pick a text/document element");
            int cap = System.Math.Clamp(maxLength, 1, 200000);
            string raw;
            if (selectionOnly)
            {
                try
                {
                    var sel = tp.GetSelection();
                    // fromEnd on a selection: fetch the whole selection (-1) so the tail is real, not the head.
                    raw = (sel is { Length: > 0 }) ? sel[0].GetText(fromEnd ? -1 : cap + 1) : string.Empty;
                }
                catch { raw = string.Empty; } // GetSelection is brittle (throws when no selection)
            }
            // fromEnd needs the FULL text (GetText(-1)) because GetText(cap+1) returns the HEAD; the head
            // read keeps the cheap cap+1 fetch (spec §5.4: default byte-identical to today).
            else raw = tp.DocumentRange.GetText(fromEnd ? -1 : cap + 1);

            bool truncated = raw.Length > cap;
            string? truncatedFrom = null;
            if (truncated)
            {
                if (fromEnd) { raw = TextTail.Slice(raw, cap); truncatedFrom = "head"; } // kept tail, dropped head
                else         { raw = raw.Substring(0, cap);     truncatedFrom = "tail"; } // kept head, dropped tail
            }
            return new TextReadResult(raw, truncated, false, truncatedFrom);
        }
        catch (System.UnauthorizedAccessException)
        { throw new ToolException(ToolErrorCode.AccessDeniedIntegrity, "Cannot read the target (higher-integrity/elevated window).", "run the target at the same integrity level"); }
    }

    public Task<TextReadResult> GetTextAsync(WindowHandle handle, string @ref, bool selectionOnly, int maxLength, bool fromEnd, int timeoutMs) =>
        RunOnRefReadAsync(handle, @ref, el => ReadText(el, selectionOnly, maxLength, fromEnd), timeoutMs);

    /// <summary>Selector twin of GetTextAsync: identical ReadText body, resolved via the bounded selector walk.</summary>
    public Task<(TextReadResult Value, string ResolvedRef)> GetTextBySelectorAsync(WindowHandle handle, Selector sel, bool selectionOnly, int maxLength, bool fromEnd, int timeoutMs) =>
        RunOnSelectorReadAsync(handle, sel, el => ReadText(el, selectionOnly, maxLength, fromEnd), timeoutMs);

    // Resolve the owning process base name (no ".exe") from a UIA element's pid, for the denylist.
    private static string? SafeProcessName(AutomationElement el)
    {
        int pid;
        try { pid = el.Properties.ProcessId.ValueOrDefault; } catch { pid = -1; }
        if (pid < 0) return null;
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return p.ProcessName; }
        catch { return null; }
    }

    public Task<(string SnapshotId, SnapshotModel Model)> BuildModelAsync(
        WindowHandle handle, SnapshotOptions options, RefRegistry refs)
    {
        return _windows.RunWithWindowAndDesktopAsync(handle, (win, desktop) =>
        {
            _windows.PruneClosedWindows(); // Phase 6 backstop: reclaim windows closed w/o a process exit
            var procName = SafeProcessName(win);
            if (PerceptionPolicy.IsDenied(procName))
                throw new ToolException(ToolErrorCode.TargetDenied,
                    $"Snapshotting windows owned by '{procName}' is blocked (credential store).",
                    "snapshot a different, non-sensitive window");
            IReadOnlyList<AutomationElement> popups = PopupFinder.FindOwnerPopups(desktop, win);
            bool isFullWindow = string.IsNullOrEmpty(options.RootRef);
            AutomationElement root = isFullWindow
                ? win : refs.Resolve(handle.Id, options.RootRef!, PopupFinder.SearchRoots(win, desktop));
            var snapshotId = refs.BeginSnapshot(handle.Id);
            var model = SnapshotEngine.Build(root, popups, options, refs, handle.Id);
            // Phase 9 §3: wakeable hint is a whole-WINDOW opacity signal, not a subtree one — only computed for
            // a full-window snapshot (RootRef null). win is the window root (same element the tree was built
            // from when isFullWindow); read its ClassName defensively (WindowManager.cs idiom).
            if (isFullWindow)
            {
                string? cls;
                try { cls = win.Properties.ClassName.ValueOrDefault; } catch { cls = null; }
                model = model with { Wakeable = WakeabilityHint.IsWakeable(cls, model.NodeCount) };
            }
            return (snapshotId, model);
        });
    }

    /// <summary>desktop_find: resolve a UIA condition on the query STA and mint durable refs for the
    /// matches WITHOUT superseding the window's snapshot refs (additive Register - a narrow find must
    /// not invalidate a held snapshot ref). Applies the snapshot security floor: deny-list guard on
    /// the window (INV-5) + IsPassword name redaction BEFORE the match decision (no name-oracle).
    /// Matches are capped at max in tree order; TotalMatches/IsTruncated report the full count so
    /// truncation is never silent.</summary>
    public Task<FindResult> FindAsync(WindowHandle handle, FindQuery query, int max, string? scopeRef)
    {
        return _windows.RunWithWindowAndDesktopAsync(handle, (win, desktop) =>
        {
            _windows.PruneClosedWindows(); // Phase 6 backstop
            var procName = SafeProcessName(win);
            if (PerceptionPolicy.IsDenied(procName))
                throw new ToolException(ToolErrorCode.TargetDenied,
                    $"Finding in windows owned by '{procName}' is blocked (credential store).",
                    "target a different, non-sensitive window");

            var spec = new FindQuerySpec(query);
            bool hasCtConstraint = FindQuerySpec.TryParseControlType(query.ControlType, out var wantedCt);
            if (!string.IsNullOrWhiteSpace(query.ControlType) && !hasCtConstraint)
                throw new ToolException(ToolErrorCode.InvalidArguments,
                    $"Unknown controlType '{query.ControlType}'.",
                    "use a UIA ControlType name, e.g. Button, Edit, ListItem");

            AutomationElement root = string.IsNullOrEmpty(scopeRef)
                ? win
                : _refs.Resolve(handle.Id, scopeRef!, PopupFinder.SearchRoots(win, desktop));

            // Native condition for the indexed props (AutomationId, ControlType, exact Name). Name
            // "contains" and enabledOnly are not indexed-expressible -> post-filter.
            FlaUI.Core.Conditions.ConditionBase? Build(FlaUI.Core.Conditions.ConditionFactory cf)
            {
                FlaUI.Core.Conditions.ConditionBase? c = null;
                if (!string.IsNullOrEmpty(query.AutomationId)) c = cf.ByAutomationId(query.AutomationId);
                if (hasCtConstraint) c = c is null ? cf.ByControlType(wantedCt) : c.And(cf.ByControlType(wantedCt));
                if (!string.IsNullOrEmpty(query.Name) && string.Equals(query.NameMatch, "eq", System.StringComparison.Ordinal) && !query.IgnoreCase)
                    c = c is null ? cf.ByName(query.Name) : c.And(cf.ByName(query.Name));
                return c;
            }

            // hasNative iff a native-expressible constraint exists (NOT name-contains / enabledOnly).
            // When absent, match ALL via the no-arg overload (repo idiom PerceptionManager.cs:226) -
            // NOT a TrueCondition/double-negation surrogate.
            bool hasNative = !string.IsNullOrEmpty(query.AutomationId) || hasCtConstraint
                || (!string.IsNullOrEmpty(query.Name) && string.Equals(query.NameMatch, "eq", System.StringComparison.Ordinal) && !query.IgnoreCase);
            AutomationElement[] raw;
            try
            {
                raw = (hasNative ? root.FindAllDescendants(cf => Build(cf)!) : root.FindAllDescendants()).ToArray();
            }
            catch { raw = System.Array.Empty<AutomationElement>(); }

            var matches = new List<FindMatch>();
            int total = 0;
            foreach (var el in raw)
            {
                // INV-5: redact a password element's Name BEFORE any match decision, not just on output.
                // Otherwise find is a name-oracle snapshot never exposes (find name="guess" -> hit => leak).
                // Matching on the redacted name makes password fields unfindable-by-name, matching the
                // snapshot render (SnapshotEngine.cs:131 shows password Name as "[REDACTED]").
                bool isPwd = RedactionPolicy.IsPasswordOrFailClosed(() => el.Properties.IsPassword.ValueOrDefault);
                // el.Name returns NULL for unnamed containers (SafeRead's "" fallback fires only on an
                // EXCEPTION, not a null return) - coalesce so the name is never null downstream (would
                // NRE the "contains" post-filter) and the FindMatch wire contract stays "empty, never null".
                string rawName = SafeRead(() => el.Name, "") ?? string.Empty; // raw -> descriptor (re-resolution key)
                string name = isPwd ? "[REDACTED]" : rawName;         // redacted -> match + output
                bool enabled = SafeRead(() => el.IsEnabled, false);
                if (!spec.MatchesPostFilter(name, enabled)) continue; // match on the redacted name (no name-oracle)
                total++;
                if (matches.Count >= max) continue; // keep counting total, stop collecting

                // Read each primitive ONCE; reuse for BOTH the descriptor and the FindMatch (no double reads).
                int[] rid = SafeRead(() => el.Properties.RuntimeId.ValueOrDefault, (int[]?)null) ?? System.Array.Empty<int>();
                var ctEnum = SafeRead(() => el.ControlType, FlaUI.Core.Definitions.ControlType.Custom);
                string aid = SafeRead(() => el.AutomationId, "") ?? string.Empty; // never null on the wire (contract)
                var b = SafeRead(() => el.BoundingRectangle, System.Drawing.Rectangle.Empty);
                bool offscreen = SafeRead(() => el.Properties.IsOffscreen.ValueOrDefault, false);
                bool hasFocus = SafeRead(() => el.Properties.HasKeyboardFocus.ValueOrDefault, false);

                // Descriptor uses the RAW name - a redacted "[REDACTED]" would break Name-based re-resolution
                // for a password field. cached: el (like snapshot's Register at SnapshotEngine.cs:87) so the
                // ref is IMMEDIATELY usable via the RuntimeId fast-path - INCLUDING anonymous controls that
                // have a RuntimeId but no AutomationId/Name (which cached:null could not re-resolve, making
                // the ref dead-on-arrival - AGY-AFTER R3). Additive find refs therefore retain a COM handle
                // (same bounded pinning snapshot already does; ~<=max per find; Phase-6 per-connection
                // lifecycle is the eviction fix). Usability of the returned ref beats the bounded memory cost.
                var descriptor = new ElementDescriptor(rid, ctEnum, aid, rawName,
                    SnapshotEngine.NearestAncestorAutomationId(el), System.Array.Empty<int>(), hasFocus);
                var @ref = _refs.Register(handle.Id, descriptor, cached: el); // ADDITIVE, cached (usable ref)
                matches.Add(new FindMatch(@ref, aid, name, ctEnum.ToString(),
                    new[] { b.X, b.Y, b.Width, b.Height }, offscreen, enabled, hasFocus)); // name already redacted
            }
            return new FindResult(matches, total, total > max);
        });
    }

    private static T SafeRead<T>(Func<T> read, T fallback) { try { return read(); } catch { return fallback; } }

    public async Task<SnapshotResult> SnapshotAsync(WindowHandle handle, SnapshotOptions options)
    {
        var (snapshotId, model) = await BuildModelAsync(handle, options, _refs);
        _cache.Put(snapshotId, model);
        return new SnapshotResult(snapshotId, SnapshotEngine.Render(model, options), model.NodeCount, model.Wakeable);
    }

    public async Task<(string SnapshotId, SnapshotModel Model)> SnapshotModelForWaitAsync(
        WindowHandle handle, SnapshotOptions options)
    {
        var (snapshotId, model) = await BuildModelAsync(handle, options, _refs);
        _cache.Put(snapshotId, model);
        return (snapshotId, model);
    }

    public Task<(bool Found, string? Value)> EvaluateSelectorValueAsync(WindowHandle handle, string by, string value) =>
        _windows.RunWithWindowAndDesktopAsync<(bool, string?)>(handle, (win, desktop) =>
        {
            bool NotOffscreen(AutomationElement e) { try { return !e.Properties.IsOffscreen.ValueOrDefault; } catch { return false; } }
            AutomationElement? Match()
            {
                try
                {
                    return by switch
                    {
                        "automationId" => win.FindAllDescendants(cf => cf.ByAutomationId(value)).FirstOrDefault(NotOffscreen),
                        "name" => win.FindAllDescendants(cf => cf.ByName(value)).FirstOrDefault(NotOffscreen),
                        "controlType" => win.FindAllDescendants().FirstOrDefault(e => { try { return NotOffscreen(e) && e.ControlType.ToString().Equals(value, System.StringComparison.OrdinalIgnoreCase); } catch { return false; } }),
                        _ => null
                    };
                }
                catch { return null; }
            }
            var el = Match();
            if (el is null) return (false, null);
            try { var vp = el.Patterns.Value.PatternOrDefault; if (vp is not null) return (true, vp.Value.ValueOrDefault); } catch { }
            try { var nm = el.Name; if (!string.IsNullOrEmpty(nm)) return (true, nm); } catch { }
            try { var la = el.Patterns.LegacyIAccessible.PatternOrDefault; if (la is not null) return (true, la.Value.ValueOrDefault); } catch { }
            return (true, null);
        });

    public async Task<SnapshotDiffResult> DiffAsync(WindowHandle handle, string baselineSnapshotId, string? scopeRef = null)
    {
        if (!_cache.TryGet(baselineSnapshotId, out var baseline) || baseline is null)
            throw new ToolException(ToolErrorCode.SnapshotNotFound, $"Baseline snapshot '{baselineSnapshotId}' is not in the cache.", "re-take the baseline snapshot");
        var baseWindowId = baselineSnapshotId.Split(':')[0];
        if (!string.Equals(baseWindowId, handle.Id, System.StringComparison.Ordinal))
            throw new ToolException(ToolErrorCode.SnapshotWindowMismatch, $"Baseline '{baselineSnapshotId}' belongs to window '{baseWindowId}', not '{handle.Id}'.", "pass a baselineSnapshotId from the same window");

        // Scope: read the scope descriptor off-STA now. (RefNotFound if the ref was superseded -
        // surfaces cleanly via ToolResponse.Guard.) BuildModelAsync resolves RootRef BEFORE its
        // BeginSnapshot, so the same ref also re-resolves inside the walk.
        var scopeDescriptor = string.IsNullOrEmpty(scopeRef) ? null : _refs.Lookup(handle.Id, scopeRef!).Descriptor;

        var currentOptions = string.IsNullOrEmpty(scopeRef) ? new SnapshotOptions() : new SnapshotOptions { RootRef = scopeRef };
        var (currentId, current) = await BuildModelAsync(handle, currentOptions, _refs);
        _cache.Put(currentId, current);

        if (scopeDescriptor is not null)
            baseline = SnapshotDiff.Subtree(baseline, scopeDescriptor); // slice baseline to the same subtree (in-memory)

        return SnapshotDiff.Compute(baselineSnapshotId, baseline, currentId, current);
    }

    public async Task<SnapshotStats> StatsByWindowAsync(WindowHandle handle)
    {
        var (id, model) = await BuildModelAsync(handle, new SnapshotOptions { InteractiveOnly = false, IncludeOffscreen = true }, _refs);
        _cache.Put(id, model);
        return Tally(id, model);
    }

    public SnapshotStats StatsBySnapshotId(string id)
    {
        if (!_cache.TryGet(id, out var model) || model is null)
            throw new ToolException(ToolErrorCode.SnapshotNotFound,
                $"Snapshot '{id}' is not in the cache (evicted or never taken).", "take a fresh desktop_snapshot and use its snapshotId");
        return Tally(id, model);
    }

    private static SnapshotStats Tally(string id, SnapshotModel m)
    {
        var nodes = m.Nodes.ToList();
        return new SnapshotStats(id, nodes.Count, nodes.Count(SnapshotEngine.IsInteractiveNode),
            nodes.Count(n => n.IsOffscreen), nodes.Count(n => n.IsPassword),
            nodes.GroupBy(n => n.ControlType.ToString()).ToDictionary(g => g.Key, g => g.Count()));
    }

    public Task<CaptureGeometry> ResolveWindowCaptureGeometryAsync(WindowHandle handle, string? @ref) =>
        _windows.RunWithWindowAndDesktopAsync(handle, (win, desktop) =>
        {
            var procName = SafeProcessName(win);
            if (PerceptionPolicy.IsDenied(procName))
                return new CaptureGeometry(default, System.Array.Empty<System.Drawing.Rectangle>(), false, true, procName);
            try
            {
                var wp = win.Patterns.Window.PatternOrDefault;
                if (wp is not null && wp.WindowVisualState.ValueOrDefault == FlaUI.Core.Definitions.WindowVisualState.Minimized)
                    return new CaptureGeometry(default, System.Array.Empty<System.Drawing.Rectangle>(), true, false, null);
            }
            catch { }
            var target = string.IsNullOrEmpty(@ref) ? (AutomationElement)win : _refs.Resolve(handle.Id, @ref!, PopupFinder.SearchRoots(win, desktop));
            var pw = new List<System.Drawing.Rectangle>();
            foreach (var rootEl in PopupFinder.SearchRoots(win, desktop))
            {
                try { foreach (var d in rootEl.FindAllDescendants()) { try { if (d.Properties.IsPassword.ValueOrDefault) pw.Add(d.BoundingRectangle); } catch { } } } catch { }
            }
            return new CaptureGeometry(target.BoundingRectangle, pw, false, false, null);
        });

    /// <summary>Phase 9 Task 10 (§6): resolve BOTH the capture rect (the whole window, or a region sub-rect of it)
    /// AND the full window physical rect, for desktop_find_text. Wraps ResolveWindowCaptureGeometryAsync(handle,
    /// @ref: null) — which already returns the FULL window rect as CaptureGeometry.Bounds when no @ref is given —
    /// then crops to `region` (window-relative fractions [xPct,yPct,wPct,hPct] in [0,1]) if supplied. Purely
    /// additive: no existing caller of ResolveWindowCaptureGeometryAsync is touched.</summary>
    public async Task<TextCaptureGeometry> ResolveTextCaptureGeometryAsync(WindowHandle handle, double[]? region)
    {
        var geo = await ResolveWindowCaptureGeometryAsync(handle, null);
        if (geo.Denied || geo.Minimized)
            return new TextCaptureGeometry(geo.Denied, geo.DeniedProcess, geo.Minimized,
                geo.Bounds, geo.PasswordRects, geo.Bounds.X, geo.Bounds.Y, geo.Bounds.Width, geo.Bounds.Height);

        var win = geo.Bounds; // full window physical rect (target was `win` itself since @ref is null)
        var capture = TextCaptureGeometry.ComputeCaptureBounds(win, region);
        return new TextCaptureGeometry(false, null, false, capture, geo.PasswordRects, win.X, win.Y, win.Width, win.Height);
    }

    public async Task<bool> DenylistedWindowsVisibleAsync()
    {
        var windows = await _windows.ListWindowsAsync();
        return windows.Any(w => PerceptionPolicy.IsDenied(w.ProcessName));
    }

    public async Task<FocusedElementInfo> GetFocusedElementAsync()
    {
        (WindowHandle Handle, string Title, int Pid)? owner;
        try { owner = await _windows.ResolveFocusedWindowAsync(); }
        catch (System.UnauthorizedAccessException)
        { throw new ToolException(ToolErrorCode.AccessDeniedIntegrity, "Cannot read the focused element (secure/UAC desktop).", "dismiss the secure prompt and retry"); }
        if (owner is null)
            throw new ToolException(ToolErrorCode.NoFocusedElement, "No element currently has UIA focus.", "click or tab to a control, then retry");
        var o = owner.Value;
        // Snapshot the owning window (full tree) and pick the focused node so the ref is actionable.
        var (snapId, model) = await BuildModelAsync(o.Handle, new SnapshotOptions { InteractiveOnly = false, IncludeOffscreen = true }, _refs);
        _cache.Put(snapId, model);
        var node = model.Nodes.FirstOrDefault(n => n.Focused) ?? model.Nodes.First();
        var line = SnapshotEngine.Render(new SnapshotModel(new[] { (SnapshotItem)node }), new SnapshotOptions()).TrimEnd('\r', '\n');
        return new FocusedElementInfo(node.Ref, line, o.Title, o.Pid, o.Handle.Id);
    }
}

public sealed record FocusedElementInfo(string Ref, string DescriptorLine, string Title, int Pid, string? WindowHandle);

public sealed record CaptureGeometry(System.Drawing.Rectangle Bounds, IReadOnlyList<System.Drawing.Rectangle> PasswordRects, bool Minimized, bool Denied, string? DeniedProcess);

public sealed record GridCellInfo(string Value, string ControlType, string AutomationId, bool IsPassword);

public sealed record TextReadResult(string Text, bool Truncated, bool IsPassword, string? TruncatedFrom = null);
