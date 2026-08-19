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
    private readonly SensitivityClassifier _classifier;

    /// <summary>The DURABLE registry. Exposed so a wait can resolve a caller's ref against it while
    /// registering its own per-poll walk into a throwaway (BuildModelAsync's resolveRefs).</summary>
    internal RefRegistry Refs => _refs;

    /// <summary>SP3 plumbing (Task 4b): reachable here for Tasks 5-9's redaction decisions. Not yet
    /// consumed — this accessor exists so the backing field is READ (avoids CS0169/CS0414) and so
    /// InputTools can reach the classifier without a second DI parameter (Classifier accessor, not a
    /// new constructor param). PUBLIC, not internal: InputTools lives in FlaUI.Mcp.Server, a DIFFERENT
    /// assembly from this Core type, and Core's [assembly: InternalsVisibleTo] (AssemblyInfo.cs:2) grants
    /// access only to "FlaUI.Mcp.Tests" — an `internal` accessor here is invisible to Server and fails to
    /// compile (verified: CS1061 across all four InputTools.cs call sites).</summary>
    public SensitivityClassifier Classifier => _classifier;

    // Break-glass: FLAUI_MCP_REF_STRICT=off forces Lenient on state-changing paths too (disables INV-8).
    // The env->mode mapping lives in RefResolveConfig.WriteMode so it is unit-tested (see Step 1).
    private static readonly RefResolveMode WriteMode =
        RefResolveConfig.WriteMode(System.Environment.GetEnvironmentVariable("FLAUI_MCP_REF_STRICT"));

    // Node cap for the Selector bounded-BFS resolver (Phase 10 #2 T4). No existing traversal/scope-cap
    // env var fit this (FLAUI_MCP_REF_MAXSCOPES bounds ANCESTOR fan-out in RefRegistry.GatherScopes, a
    // different axis), so this is a NEW var mirroring that one's parse idiom.
    private static readonly int MaxSelectorNodes =
        RefResolveConfig.MaxSelectorNodes(System.Environment.GetEnvironmentVariable("FLAUI_MCP_SELECTOR_MAXNODES"));

    // SP3 Task 4b: ~35 existing test files construct PerceptionManager directly (new PerceptionManager(...),
    // not via DI) and cannot pass a 4th argument without editing every one of them — forbidden ("no
    // pre-existing test may be edited"). `classifier` is therefore OPTIONAL, defaulting to OsOnly (the
    // same no-redaction-rules default Program.cs uses absent --redaction-rules) so every existing test
    // constructor call keeps compiling unchanged. Production/DI always supplies the real singleton
    // (Program.cs:76 AddSingleton(classifier)), so this default is exercised only by tests that don't
    // care about classification.
    public PerceptionManager(WindowManager windows, RefRegistry refs, SnapshotCache cache, SensitivityClassifier? classifier = null)
    {
        _windows = windows;
        _refs = refs;
        _cache = cache;
        _classifier = classifier ?? SensitivityClassifier.OsOnly;
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
    public Task<T> RunOnRefActionAsync<T>(WindowHandle handle, string @ref, Func<AutomationElement, T> func, int timeoutMs, bool skipOffscreenGuard = false)
    {
        var descriptor = _refs.Lookup(handle.Id, @ref).Descriptor; // REF_NOT_FOUND if absent (cheap, off-STA)
        return _windows.RunOnWindowActionAsync(handle, (win, desktop) =>
        {
            var roots = PopupFinder.SearchRoots(win, desktop);
            var el = _refs.ResolveDescriptor(descriptor, roots, @ref, WriteMode); // INV-8 (break-glass: FLAUI_MCP_REF_STRICT=off)
            if (!skipOffscreenGuard && el.Properties.IsOffscreen.ValueOrDefault)
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

        // SP3: the classifier needs the owning process name. Read it ONCE from the walk root rather than
        // per visited node — the selector walk is scoped to a single window, so every descendant shares
        // the process, and a per-node read would add a COM call to every node of a bounded BFS.
        var procName = SafeProcessName(root);

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
                var sens = ElementContent.SensitivityOf(el, _classifier, procName);
                string rawName = SafeRead(() => el.Name, "") ?? string.Empty;
                string name = sens.Redact ? "[REDACTED]" : rawName;
                bool enabled = SafeRead(() => el.IsEnabled, false);
                // DEF-3: withhold a redacted element from NAME search only — matching it on the token made
                // the selector a locator oracle for every password field. Driven by the ELEMENT's
                // classification, never by the query string, so an element legitimately named "[REDACTED]"
                // is unaffected. BC-1: automationId/controlType still reach it, so it stays targetable.
                if (sens.Redact && spec.HasNameConstraint) { /* not a name-searchable hit */ }
                else if (spec.MatchesPostFilter(name, enabled))
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
        Func<AutomationElement, T> func, int timeoutMs, bool skipOffscreenGuard = false)
    {
        var scopeDescriptor = string.IsNullOrEmpty(sel.Scope) ? null : _refs.Lookup(handle.Id, sel.Scope!).Descriptor;
        return _windows.RunOnWindowActionAsync(handle, (win, desktop) =>
        {
            var roots = PopupFinder.SearchRoots(win, desktop);
            AutomationElement root = scopeDescriptor is null
                ? win
                : _refs.ResolveDescriptor(scopeDescriptor, roots, sel.Scope!, WriteMode);
            var (el, r) = ResolveSelectorOnSta(handle.Id, root, sel);
            if (!skipOffscreenGuard && el.Properties.IsOffscreen.ValueOrDefault)
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
    // Returns the process base name it ALREADY resolved, so a caller that needs it for rule-scoped
    // redaction does not pay a second PID lookup.
    private static string? EnsureAllowed(AutomationElement el)
    {
        var procName = SafeProcessName(el);
        if (PerceptionPolicy.IsDenied(procName))
            throw new ToolException(ToolErrorCode.TargetDenied,
                $"Reading content from windows owned by '{procName}' is blocked (credential store).",
                "target a different, non-sensitive element");
        return procName;
    }

    // Verbatim-extracted read lambda from GetGridCellAsync (Phase 10 #2 T7): the shared body for both
    // the ref path (RunOnRefReadAsync) and the selector path (RunOnSelectorReadAsync) — byte-identical
    // logic, no behavior change; existing GetGridCellAsync tests are the oracle that the extraction
    // preserved behavior.
    private static GridCellInfo ReadGridCell(AutomationElement el, int row, int col,
                                             SensitivityClassifier classifier)
    {
        var procName = EnsureAllowed(el);
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
            // ElementContent.Value carries that same per-read tolerance internally.
            var read = ElementContent.Value(cell, classifier, procName);
            string ct = "Unknown", aid = string.Empty;
            try { ct = cell.ControlType.ToString(); } catch { }
            try { aid = cell.Properties.AutomationId.ValueOrDefault ?? string.Empty; } catch { }
            return new GridCellInfo(read.Text, ct, aid, read.Sensitivity.Source == RedactionSource.Os,
                read.Sensitivity.Redact, ElementContent.RedactedBy(read.Sensitivity));
        }
        catch (System.UnauthorizedAccessException)
        { throw new ToolException(ToolErrorCode.AccessDeniedIntegrity, "Cannot read the target (higher-integrity/elevated window).", "run the target at the same integrity level"); }
        catch (System.Runtime.InteropServices.COMException)
        { throw new ToolException(ToolErrorCode.ElementNotActionable, "The grid provider threw while reporting its cells.", "re-snapshot the grid and retry"); }
    }

    public Task<GridCellInfo> GetGridCellAsync(WindowHandle handle, string @ref, int row, int col, int timeoutMs) =>
        RunOnRefReadAsync(handle, @ref, el => ReadGridCell(el, row, col, _classifier), timeoutMs);

    /// <summary>Selector twin of GetGridCellAsync (Phase 10 #2 T7): identical ReadGridCell body, resolved
    /// via the bounded selector walk (RunOnSelectorReadAsync — Lenient, no offscreen guard, mirrors the
    /// ref read path) instead of a ref lookup.</summary>
    public Task<(GridCellInfo Value, string ResolvedRef)> GetGridCellBySelectorAsync(WindowHandle handle, Selector sel, int row, int col, int timeoutMs) =>
        RunOnSelectorReadAsync(handle, sel, el => ReadGridCell(el, row, col, _classifier), timeoutMs);

    // Verbatim-extracted read lambda from GetTextAsync (Phase 10 #2 T7): byte-identical logic (password
    // short-circuit, TextPattern read, truncation) shared by the ref and selector read paths.
    private static TextReadResult ReadText(AutomationElement el, bool selectionOnly, int maxLength, bool fromEnd,
                                           SensitivityClassifier classifier)
    {
        var procName = EnsureAllowed(el);
        // Truncation is decided INSIDE the read thunk (it depends on the text that came back), so it is
        // captured out through locals. When the value is redacted the thunk never runs and both stay at
        // their defaults — matching the old short-circuit, which returned (truncated:false, from:null).
        bool truncated = false;
        string? truncatedFrom = null;
        try
        {
            // The password short-circuit lives in ElementContent now: it classifies BEFORE the thunk, so
            // the provider is still never asked for a secret's text or selection.
            var read = ElementContent.Text(el, classifier, procName, () =>
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

                truncated = raw.Length > cap;
                if (truncated)
                {
                    if (fromEnd) { raw = TextTail.Slice(raw, cap); truncatedFrom = "head"; } // kept tail, dropped head
                    else         { raw = raw.Substring(0, cap);     truncatedFrom = "tail"; } // kept head, dropped tail
                }
                return raw;
            });
            return new TextReadResult(read.Text, truncated, read.Sensitivity.Source == RedactionSource.Os, truncatedFrom,
                read.Sensitivity.Redact, ElementContent.RedactedBy(read.Sensitivity));
        }
        catch (System.UnauthorizedAccessException)
        { throw new ToolException(ToolErrorCode.AccessDeniedIntegrity, "Cannot read the target (higher-integrity/elevated window).", "run the target at the same integrity level"); }
    }

    public Task<TextReadResult> GetTextAsync(WindowHandle handle, string @ref, bool selectionOnly, int maxLength, bool fromEnd, int timeoutMs) =>
        RunOnRefReadAsync(handle, @ref, el => ReadText(el, selectionOnly, maxLength, fromEnd, _classifier), timeoutMs);

    /// <summary>Selector twin of GetTextAsync: identical ReadText body, resolved via the bounded selector walk.</summary>
    public Task<(TextReadResult Value, string ResolvedRef)> GetTextBySelectorAsync(WindowHandle handle, Selector sel, bool selectionOnly, int maxLength, bool fromEnd, int timeoutMs) =>
        RunOnSelectorReadAsync(handle, sel, el => ReadText(el, selectionOnly, maxLength, fromEnd, _classifier), timeoutMs);

    /// <summary>Composite terminal-tab read (spec §5.5): select tabIndex → settle → read the sibling
    /// buffer (fromEnd/maxLength) → restore the originally-active tab on both the success and error paths
    /// (finally-equivalent). Runs entirely on one transient action STA (refs change on every switch, so it
    /// must be atomic and in-process). Destructive at the tool layer; the pattern actions themselves are
    /// lease-exempt (spec §3.1).</summary>
    public Task<TerminalTabReader.Result> ReadTerminalTabAsync(
        WindowHandle handle, int tabIndex, bool restoreFocus, bool fromEnd, int maxLength, int timeoutMs) =>
        _windows.RunOnWindowActionAsync(handle,
            (win, _) => TerminalTabReader.Run(win, tabIndex, restoreFocus, fromEnd, maxLength,
                buf => ReadText(buf, selectionOnly: false, maxLength, fromEnd, _classifier),
                _classifier, SafeProcessName(win)),
            timeoutMs);

    /// <summary>Item 5: pure-read tab enumeration. Runs on the QUERY STA, not the transient action STA
    /// that ReadTerminalTabAsync uses (:398) — it mutates nothing, so it needs neither the action hop nor
    /// the in-flight action cap. Same STA path BuildModelAsync uses (:416).</summary>
    public Task<(IReadOnlyList<TerminalTabReader.TabListing> Tabs, int ActiveTabIndex)>
        ListTerminalTabsAsync(WindowHandle handle) =>
        _windows.RunWithWindowAndDesktopAsync(handle,
            (win, _) => TerminalTabReader.List(win, _classifier, SafeProcessName(win)));

    // Resolve the owning process base name (no ".exe") from a UIA element's pid, for the denylist.
    /// <summary>Delegates to <see cref="ProcessIdentity"/>, which is the ONE implementation.
    ///
    /// ⚠ This used to be a full private copy, and WatchPump carried a second one commented "mirrors
    /// PerceptionManager.SafeProcessName". They drifted: a capstone fix taught this one to recover a pid of
    /// 0 through the window handle and the copy did not follow, so the watch path began dropping events for
    /// windows this path served. Two implementations of one decision is the defect class that has now bitten
    /// this branch three separate times — do not re-inline it.</summary>
    private static string? SafeProcessName(AutomationElement el) => ProcessIdentity.OfElement(el);

    /// <summary><paramref name="resolveRefs"/> is the registry the ROOT REF resolves against; refs minted
    /// by the walk always register into <paramref name="refs"/>. They are the same registry for every
    /// existing caller (default null => refs), and DIFFERENT only for the wait paths, which resolve a
    /// caller's durable ref while registering walked nodes into a throwaway so per-poll walks never grow
    /// the durable registry (WaitCoordinator.cs:18-19).</summary>
    public Task<(string SnapshotId, SnapshotModel Model)> BuildModelAsync(
        WindowHandle handle, SnapshotOptions options, RefRegistry refs, RefRegistry? resolveRefs = null)
    {
        return _windows.RunWithWindowAndDesktopAsync(handle, (win, desktop) =>
        {
            _windows.PruneClosedWindows(); // Phase 6 backstop: reclaim windows closed w/o a process exit
            var procName = SafeProcessName(win);
            if (PerceptionPolicy.IsDenied(procName))
                throw new ToolException(ToolErrorCode.TargetDenied,
                    $"Snapshotting windows owned by '{procName}' is blocked (credential store).",
                    "snapshot a different, non-sensitive window");
            // ONE popup scan per build. It used to run twice on the RootRef path -- once here and once
            // inside SearchRoots (PopupFinder.cs:17) -- and each scan is desktop.FindAllChildren() plus
            // ~6 cross-process reads per desktop child (PopupFinder.cs:35-47). The two consumers below
            // take DIFFERENT lists and must not be conflated: the grafting loop takes popups ALONE, while
            // ref resolution needs the WINDOW FIRST (PopupFinder.cs:12-13 -- searchRoots[0] MUST be the
            // window root, IndexPath is window-relative). Passing popups alone to Resolve makes every
            // window-rooted ref unresolvable; passing {win}+popups to the grafting loop makes the engine
            // visit the window a second time as its own popup root (SnapshotEngine.cs:47-54).
            IReadOnlyList<AutomationElement> popups = PopupFinder.FindOwnerPopups(desktop, win);
            bool isFullWindow = string.IsNullOrEmpty(options.RootRef);
            AutomationElement root;
            if (isFullWindow)
            {
                root = win;
            }
            else
            {
                var searchRoots = new List<AutomationElement> { win };
                searchRoots.AddRange(popups);
                root = (resolveRefs ?? refs).Resolve(
                    handle.Id, options.RootRef!, searchRoots, options.RootResolveMode);
            }
            var snapshotId = refs.BeginSnapshot(handle.Id);
            var model = SnapshotEngine.Build(root, popups, options, refs, handle.Id, _classifier, procName);
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

            // A popup can live at the desktop level (Win32 #32768) or as a window child (WPF/.NET 10) —
            // PopupFinder.cs:21-25. Rooting at bare `win` reaches only the second, so find disagreed with
            // snapshot about whether a menu item exists.
            IReadOnlyList<AutomationElement> roots = string.IsNullOrEmpty(scopeRef)
                ? PopupFinder.SearchRoots(win, desktop)
                : new[] { _refs.Resolve(handle.Id, scopeRef!, PopupFinder.SearchRoots(win, desktop)) };

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
            // PER-ROOT isolation: one broad catch around the whole loop would let a transient
            // ElementNotAvailableException in ANY popup (a tooltip closing mid-search) discard every
            // valid match from the window and the other popups. A root that throws contributes nothing.
            // Root order is preserved and dedup happens BEFORE the `max` cap below, so truncation never
            // silently prefers window matches over popup ones.
            // ONE root cannot produce a duplicate: FindAllDescendants yields each descendant once, and
            // duplication only arises when the SAME element is reachable from two different roots. So the
            // dedup is skipped entirely in the single-root case -- which is every find with no popup open,
            // i.e. almost all of them. This is not a micro-optimisation: the RuntimeId read is a COM
            // property access measured at ~1.0ms PER NODE on this host, so paying it unconditionally would
            // add ~3s to a find over a 3000-node window, plus ~0.9s for the O(n^2) scan. The whole cost is
            // charged only when a popup is actually open, where correctness requires it.
            bool needDedup = roots.Count > 1;
            var rawList = new List<AutomationElement>();
            var seenRids = new List<int[]>();
            AutomationElement[] Enumerate(AutomationElement r)
                => (hasNative ? r.FindAllDescendants(cf => Build(cf)!) : r.FindAllDescendants()).ToArray();

            for (int rootIndex = 0; rootIndex < roots.Count; rootIndex++)
            {
                AutomationElement[] perRoot;
                if (rootIndex == 0)
                {
                    // roots[0] is the WINDOW (PopupFinder.cs:16) -- or, under a scopeRef, the single
                    // resolved element. A failure here is the target dying, not a popup closing
                    // mid-search, and swallowing it made find answer "no matches" for a window that no
                    // longer exists: a wrong belief dressed as an empty result, which is the same defect
                    // class this branch exists to remove. EvaluateSelectorValueAsync was given this
                    // exemption first; leaving find without it left the two siblings disagreeing about
                    // what a dead window means.
                    perRoot = Enumerate(roots[rootIndex]);
                }
                else
                {
                    // PER-ROOT isolation, popups only: a tooltip closing mid-search must not zero out
                    // the window's own matches. A root that throws contributes nothing.
                    try { perRoot = Enumerate(roots[rootIndex]); }
                    catch { continue; }
                }

                if (!needDedup) { rawList.AddRange(perRoot); continue; }

                foreach (var el in perRoot)
                {
                    var rid = SafeRead(() => el.Properties.RuntimeId.ValueOrDefault, (int[]?)null) ?? System.Array.Empty<int>();
                    // A window-child popup is reachable from BOTH `win` and its own popup root.
                    // An element whose RuntimeId is unreadable (the SafeRead fallback) is KEPT rather
                    // than dropped, so it can in principle appear twice. That is deliberate: with no
                    // identity to compare, the only alternatives are to emit a possible duplicate or to
                    // discard a possible unique match, and a dropped match is the worse failure -- find
                    // silently missing an element is precisely the defect this method was changed to fix.
                    // Do NOT "fix" this with element equality: the two wrappers come from separate
                    // FindAllDescendants calls, so any comparison that actually worked would be another
                    // per-element COM round-trip, reintroducing the cost the roots.Count guard removed.
                    if (rid.Length > 0 && seenRids.Any(s => SnapshotEngine.RidEqual(s, rid))) continue;
                    if (rid.Length > 0) seenRids.Add(rid);
                    rawList.Add(el);
                }
            }
            AutomationElement[] raw = rawList.ToArray();

            var matches = new List<FindMatch>();
            int total = 0;
            foreach (var el in raw)
            {
                // INV-5: redact a password element's Name BEFORE any match decision, not just on output.
                // Otherwise find is a name-oracle snapshot never exposes (find name="guess" -> hit => leak).
                // Matching on the redacted name makes password fields unfindable-by-name, matching the
                // snapshot render (SnapshotEngine.cs:131 shows password Name as "[REDACTED]").
                // ElementContent.Name coalesces a NULL el.Name (unnamed containers) to "" internally, so
                // the name is never null downstream (would NRE the "contains" post-filter) and the
                // FindMatch wire contract stays "empty, never null".
                var read = ElementContent.Name(el, _classifier, procName);
                string rawName = read.RawForIdentity;   // raw -> descriptor (re-resolution key), BC-1
                string name = read.Text;                // already redacted -> match + output
                bool enabled = SafeRead(() => el.IsEnabled, false);
                // DEF-3: withhold a redacted element from NAME search only (BC-1: aid/controlType still reach it).
                if (read.Sensitivity.Redact && spec.HasNameConstraint) continue;
                if (!spec.MatchesPostFilter(name, enabled)) continue;
                total++;
                if (matches.Count >= max) continue; // keep counting total, stop collecting

                // Read each primitive ONCE; reuse for BOTH the descriptor and the FindMatch (no double reads).
                int[] rid = SafeRead(() => el.Properties.RuntimeId.ValueOrDefault, (int[]?)null) ?? System.Array.Empty<int>();
                var ctEnum = SafeRead(() => el.ControlType, FlaUI.Core.Definitions.ControlType.Custom);
                string aid = SafeRead(() => el.AutomationId, "") ?? string.Empty; // never null on the wire (contract)
                var b = SafeRead(() => el.BoundingRectangle, System.Drawing.Rectangle.Empty);
                bool offscreen = SafeRead(() => el.Properties.IsOffscreen.ValueOrDefault, false);
                bool hasFocus = SafeRead(() => el.Properties.HasKeyboardFocus.ValueOrDefault, false);
                bool selected = SafeRead(() => el.Patterns.SelectionItem.PatternOrDefault?.IsSelected.ValueOrDefault ?? false, false);

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
                    new[] { b.X, b.Y, b.Width, b.Height }, offscreen, enabled, hasFocus, selected)); // name already redacted
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

    public Task<(bool Found, string? Value)> EvaluateSelectorValueAsync(WindowHandle handle, string by, string value,
        bool includeOffscreen = false) =>
        _windows.RunWithWindowAndDesktopAsync<(bool, string?)>(handle, (win, desktop) =>
        {
            // includeOffscreen is the CALLER's opt-out (desktop_wait_for's parameter). Without threading
            // it here, wait_for(until:valueEquals, includeOffscreen:true) silently kept filtering — the
            // flag was accepted and ignored on exactly one of the four `until` values.
            // WHAT AN UNREADABLE IsOffscreen MEANS -- the two sides of this codebase used to disagree.
            // SnapshotEngine reads it as Safe(..., fallback: false), so an element whose IsOffscreen
            // THROWS is treated as on-screen and KEPT in the walk. This evaluator did the opposite: its
            // catch returned false for "not offscreen", so the element was silently DROPPED. Net effect,
            // for an element with a throwing IsOffscreen: until=exists satisfies, until=valueEquals can
            // never satisfy, forever. Two predicates disagreeing about whether an element exists at all.
            //
            // Aligned here, but NOT by simply flipping the catch to "keep it" as first proposed. This is
            // a FIRST-MATCH selector: flipping the fallback makes a broken element WIN over a healthy
            // sibling carrying the same automationId, so a wait that works today would start timing out.
            // Instead: prefer a definitely-visible match, and fall back to an unreadable one only when
            // there is no visible candidate at all -- which is precisely the case where the engine would
            // have kept it, so the divergence closes without reordering anything that already worked.
            // Definitely-OFFSCREEN elements are still skipped outright; that was never in question.
            static bool? TryReadOffscreen(AutomationElement e)
            { try { return e.Properties.IsOffscreen.ValueOrDefault; } catch { return null; } }

            AutomationElement? PickVisible(AutomationElement[] candidates)
            {
                AutomationElement? unreadable = null;
                foreach (var e in candidates)
                {
                    if (includeOffscreen) return e;          // caller opted out of the filter entirely
                    var offscreen = TryReadOffscreen(e);
                    if (offscreen == false) return e;        // definitely on-screen: best answer, stop
                    if (offscreen is null) unreadable ??= e; // could not tell: hold as a last resort
                }
                return unreadable;
            }

            // Parsed once per CALL, hoisted out of the per-root loop below. (Not once per WAIT: the
            // caller polls this method, so the parse still runs each poll -- it is a string parse, not
            // the tree walk that mattered.) by="controlType" used to enumerate the ENTIRE tree with no
            // native condition and then compare ControlType.ToString() in managed code -- every element
            // marshalled across the UIA IPC boundary, on EVERY poll of a wait that polls every 500ms.
            // ControlType is an indexed UIA property, so it pushes down the same way AutomationId and
            // Name already do (the FindAsync idiom). An unparseable name yields no condition and
            // therefore no match, which is exactly what the string compare did.
            bool hasCt = FindQuerySpec.TryParseControlType(value, out var wantedCt);
            AutomationElement? Probe(AutomationElement r) => by switch
            {
                "automationId" => PickVisible(r.FindAllDescendants(cf => cf.ByAutomationId(value))),
                "name" => PickVisible(r.FindAllDescendants(cf => cf.ByName(value))),
                "controlType" => hasCt ? PickVisible(r.FindAllDescendants(cf => cf.ByControlType(wantedCt))) : null,
                _ => null
            };

            AutomationElement? Match()
            {
                var roots = PopupFinder.SearchRoots(win, desktop);
                for (int i = 0; i < roots.Count; i++)
                {
                    AutomationElement? hit;
                    if (i == 0)
                    {
                        // SearchRoots[0] IS the window (PopupFinder.cs:16). A failure here means the
                        // window died, not that a popup closed mid-search, and swallowing it made
                        // valueEquals the ONE predicate that reports a plain "condition not met" for a
                        // dead window and then keeps polling to the full budget -- exists/enabled/gone
                        // all surface the death immediately, because BuildModelAsync throws straight out
                        // of the loop. Let it propagate so all four predicates agree.
                        hit = Probe(r: roots[i]);
                    }
                    else
                    {
                        // PER-ROOT isolation, for POPUPS only: a tooltip or menu closing mid-search must
                        // not zero out the window's own matches. A root that throws contributes nothing.
                        try { hit = Probe(r: roots[i]); }
                        catch { continue; }
                    }
                    if (hit is not null) return hit;
                }
                return null;
            }
            var el = Match();
            if (el is null) return (false, null);

            // INV-5, applied here for the first time. This method reads an element's VALUE and its only
            // consumer, wait_for(until:valueEquals), reports whether that value equals a caller-supplied
            // string -- which is a password ORACLE if the element is a password field: no secret crosses
            // the wire, but an agent can CONFIRM a guess, and confirming is the whole attack. Every
            // sibling path already applies this floor (snapshot renders "[REDACTED]", find matches on the
            // redacted name so a password is unfindable by name, get_text redacts the text); this one
            // relied on the UIA provider to blank the value itself.
            // MEASURED before adding: a conformant WPF PasswordBox returns an EMPTY ValuePattern value,
            // so there is no live leak on the fixture and both oracle probes (right password and wrong)
            // came back unsatisfied. This closes the NON-conformant case -- a provider that sets
            // IsPassword and still exposes the value through LegacyIAccessible -- which is exactly the
            // case RedactionPolicy.IsPasswordOrFailClosed exists for: it fails CLOSED on a throwing read
            // rather than trusting the control. Returning null (not "") also means valueEquals can never
            // satisfy on a password field, since `equals` is required to be non-null.
            // ⚠ SP3 CAPSTONE FIX (finding L1). The gate below USED to be the IsPassword read alone, i.e.
            // OS-ONLY — so an operator RULE never reached it. A rule-redacted element fell straight through
            // to the raw Value / Name / LegacyIAccessible reads underneath, and wait_for(until:valueEquals)
            // became a CONFIRMATION ORACLE against exactly the fields an operator configured a rule to
            // protect. The comment above states that threat model correctly and then closed it for one of
            // the two signals only; SP3 extended redaction across twelve other sites and missed this one.
            //
            // SensitivityOf is the right primitive here precisely because it decides WITHOUT reading the
            // content it is protecting, and it still fails CLOSED on a throwing IsPassword read — so this
            // is a strict superset of the OS-only gate it replaces, never a weakening.
            if (ElementContent.SensitivityOf(el, _classifier, SafeProcessName(win)).Redact)
                return (true, null);

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

    internal static SnapshotStats Tally(string id, SnapshotModel m)
    {
        var nodes = m.Nodes.ToList();
        return new SnapshotStats(id, nodes.Count, nodes.Count(SnapshotEngine.IsInteractiveNode),
            nodes.Count(n => n.IsOffscreen), nodes.Count(n => n.Sensitivity.Source == RedactionSource.Os),
            nodes.Count(n => n.Sensitivity.Redact),
            nodes.GroupBy(n => n.ControlType.ToString()).ToDictionary(g => g.Key, g => g.Count()));
    }

    /// <param name="skipIfNoRenderableOverlap">TRUE only for the full-desktop mask sweep, where a window with no
    /// renderable overlap contributes no pixels and may be skipped. FALSE for a caller that NAMED this
    /// window and will photograph its rect regardless — suppressing that window's masks would hand back an
    /// unmasked image of the named target, which is the one thing this feature must not do.</param>
    public Task<CaptureGeometry> ResolveWindowCaptureGeometryAsync(WindowHandle handle, string? @ref,
                                                                   bool skipIfNoRenderableOverlap = false) =>
        _windows.RunWithWindowAndDesktopAsync(handle, (win, desktop) =>
        {
            var procName = SafeProcessName(win);
            if (PerceptionPolicy.IsDenied(procName))
                return new CaptureGeometry(default, System.Array.Empty<System.Drawing.Rectangle>(), false, true, procName, System.Array.Empty<MaskEscalationEntry>());
            try
            {
                var wp = win.Patterns.Window.PatternOrDefault;
                if (wp is not null && wp.WindowVisualState.ValueOrDefault == FlaUI.Core.Definitions.WindowVisualState.Minimized)
                    return new CaptureGeometry(default, System.Array.Empty<System.Drawing.Rectangle>(), true, false, null, System.Array.Empty<MaskEscalationEntry>());
            }
            catch { }
            var target = string.IsNullOrEmpty(@ref) ? (AutomationElement)win : _refs.Resolve(handle.Id, @ref!, PopupFinder.SearchRoots(win, desktop));
            // ⚠ EVERY FAILURE FROM HERE TO THE RETURN MUST BECOME RedactionUnmaskable, NEVER A RAW
            // EXCEPTION. AllMaskRectsAsync (the FULL-DESKTOP path) wraps this call in `catch { }` to
            // skip a window it cannot bind, and rethrows ONLY RedactionUnmaskable. So a raw COMException
            // escaping here does not fail the capture — it silently drops this window's ENTIRE mask set and
            // photographs it in the clear. That is a leak, and it defeats two decisions at once: A1's
            // refusal, and the strict-on-roots[0] rule below, whose whole point is that a dead target must
            // not be quietly skipped.
            //
            // Note this hazard PREDATES SP4 — the old code read target.BoundingRectangle unguarded at its
            // return statement, with the same swallow downstream. It is fixed here because A1 is what makes
            // the difference between "no mask" and "refuse" load-bearing.
            //
            // This read keeps its OWN guard even though a blanket conversion follows, because it is the one
            // failure worth naming precisely in the message an operator reads.
            System.Drawing.Rectangle captureBounds;
            try { captureBounds = target.BoundingRectangle; }
            catch (System.Exception ex)
            {
                throw new ToolException(ToolErrorCode.RedactionUnmaskable,
                    $"Could not read the capture bounds of window '{handle.Id}' (process '{procName}'), so its redacted regions cannot be located ({ex.GetType()})",
                    "retry once the UI has settled, or capture a different window");
            }

            // The yardstick is the capture clipped to the RENDERABLE desktop. A maximized window's rect
            // bleeds past the monitor by its invisible resize border, and comparing against that bleed is
            // what let a full-monitor mask pass the blacks-out check. Read once, so the rect the decision
            // judges against is the same rect returned as Bounds and ultimately captured.
            // ⚠ It does NOT eliminate movement-induced misalignment: if the window moves mid-walk, live mask
            // rects land against a stale capture rect. Reading it AFTER the walk just inverts which side is
            // stale. That race is inherent to capturing a moving window — do not read this as claiming
            // otherwise.
            var yardstick = System.Drawing.Rectangle.Intersect(captureBounds, ScreenCapture.VirtualScreenBounds());

            // ⚠ A window with NO renderable overlap contributes no pixels to any capture, so there is
            // nothing here to withhold and nothing to refuse over. Returning early is not a shortcut: it is
            // what stops one invisible off-screen window with a broken provider from refusing — and, on the
            // full-desktop path, FATALLY FAILING — a capture it could not have appeared in.
            //
            // ⚠ Tested on EXTENTS, not Rectangle.IsEmpty. IsEmpty requires all four fields to be zero, while
            // Rectangle.Intersect compares with `>=` and so yields a zero-width rect at NON-ZERO coordinates
            // — (100, 50, 0, 30) — for two rects that merely touch along an edge. IsEmpty is false there,
            // and the walk would then judge every mask against a degenerate yardstick that nothing
            // intersects: every mask dropped, capture returned unmasked. A guard producing a leak.
            if (yardstick.Width <= 0 || yardstick.Height <= 0)
            {
                // ⚠⚠ TWO CALLERS, TWO CORRECT ANSWERS, AND THEY ARE NOT THE SAME ANSWER. This is why the
                // parameter exists: the method cannot infer which caller it is serving, and guessing was
                // wrong in BOTH directions across two review rounds.
                //
                // FULL-DESKTOP (skipIfNoRenderableOverlap: true): a window with no renderable overlap contributes no
                // pixels to a virtual-screen capture, so there is nothing to withhold. Return an empty mask
                // set. This is what stops ONE invisible off-screen window with a broken provider from
                // refusing — and so fatally failing — a whole-desktop capture it could not have appeared in.
                //
                // WINDOW- OR ELEMENT-SCOPED (false, the default): the caller NAMED this window and will
                // photograph its rect whatever is or is not rendered there. Suppressing its masks would
                // hand back an unmasked image of the named target, which is the one thing this feature must
                // not do. Fall back to the unclipped rect and compute masks normally — the maximized-bleed
                // case the clipping exists for cannot arise when nothing is on screen to bleed over.
                if (skipIfNoRenderableOverlap)
                    return new CaptureGeometry(captureBounds, System.Array.Empty<System.Drawing.Rectangle>(),
                                               false, false, null, System.Array.Empty<MaskEscalationEntry>());
                // ⚠ The UNCLIPPED rect becomes the yardstick here, and MaskEscalation's parameter contract
                // says that is allowed: the requirement is NON-DEGENERATE, not "clipped". Clipping has
                // already produced a degenerate rect for this target, and judging every mask against THAT
                // would discard them all and return an unmasked image of a window the caller named.
                yardstick = captureBounds;
            }

            var pw = new List<System.Drawing.Rectangle>();
            var escalations = new List<MaskEscalationEntry>();

            // ⚠ ONE BLANKET CONVERSION, not a guard per read. Round 4 wrapped the capture-bounds read and
            // the roots[0] enumeration individually and STILL missed PopupFinder.SearchRoots, which is a
            // UIA walk of its own. Any raw exception escaping this region is swallowed by
            // AllMaskRectsAsync's `catch { }` on the full-desktop path and becomes a window photographed
            // with NO mask set — so the safe default has to be structural, not a list of remembered sites.
            //
            // A ToolException passes through UNCHANGED: those are deliberate, already-classified outcomes
            // (RefNotFound from the ref resolution above, and the RedactionUnmaskable refusals raised
            // inside the loop), and re-wrapping them would destroy the code an agent branches on.
            //
            // ⚠ THE EXCEPTION'S TYPE NAME GOES ON THE WIRE, NEVER ITS MESSAGE. An earlier revision
            // interpolated ex.Message, which is UNCLASSIFIED THIRD-PARTY TEXT of unknown provenance being
            // written into a payload an agent reads — a direct violation of this increment's own binding
            // constraint 1 ("no content may reach the wire unclassified"), inside the one feature whose
            // whole subject is that constraint. A type name is bounded, diagnostic enough to route an
            // investigation, and cannot carry an element's Name or value.
            //
            // Nothing on the query path is cancellable, so this broad catch reclassifies no cancellation:
            // VERIFIED at AutomationDispatcher.cs:61, RunQueryAsync is `_query.RunAsync(func)` with no
            // timeout and no CancellationToken (only the ACTION path has AwaitWithTimeout).
            try
            {
            var roots = PopupFinder.SearchRoots(win, desktop);
            for (int rootIndex = 0; rootIndex < roots.Count; rootIndex++)
            {
                // ⚠ ONE SOURCE PER ROOT, and it is CONSTRUCTED FROM THAT ROOT. The root is what bounds the
                // ancestor climb: without it the walk sails through the window into the desktop, masks
                // everything, and returns a successful all-black image instead of refusing. Ancestor chains
                // never cross a root boundary, so a per-root cache is exactly as coherent as a per-capture
                // one and its bound is unambiguous.
                var ancestors = new AncestorRectSource(roots[rootIndex], rootIsWindow: rootIndex == 0);
                AutomationElement[] descendants;
                if (rootIndex == 0)
                {
                    // roots[0] IS the window (PopupFinder.cs:16). A failure here is the TARGET dying, not a
                    // popup closing mid-scan, and swallowing it returns a capture whose mask set is empty
                    // because the tree could not be read — a leak dressed as a successful screenshot.
                    // FindAsync (:583-596) and EvaluateSelectorValueAsync (:745-760) already draw exactly
                    // this line for exactly this reason; this makes the third sibling agree with them.
                    //
                    // ⚠ CONVERTED, not propagated raw. Letting a COMException escape would be caught by
                    // AllMaskRectsAsync's `catch { }` on the full-desktop path and turn this strictness
                    // into a SILENT SKIP - the precise opposite of the decision this branch encodes.
                    try { descendants = roots[rootIndex].FindAllDescendants(); }
                    catch (System.Exception ex)
                    {
                        throw new ToolException(ToolErrorCode.RedactionUnmaskable,
                            $"Could not enumerate window '{handle.Id}' (process '{procName}'), so its redacted regions cannot be located ({ex.GetType()})",
                            "retry once the UI has settled, or capture a different window");
                    }
                }
                else
                {
                    // PER-ROOT isolation, POPUPS only: a tooltip or menu closing mid-scan must not fail the
                    // window's own capture. A root that throws contributes nothing.
                    try { descendants = roots[rootIndex].FindAllDescendants(); }
                    catch { continue; }
                }

                foreach (var d in descendants)
                {
                    // DEF-1: this read used to be RAW (`d.Properties.IsPassword.ValueOrDefault`) inside a
                    // swallowing catch, so a provider that THREW yielded no rect — text redacted, PIXELS
                    // CAPTURED, while all eleven text sites failed closed. SensitivityOf routes through the
                    // same classifier, which fails closed on a throw AND honours configured rules.
                    // ⚠ THIS CATCH IS UNREACHABLE TODAY, and saying so is the point of writing it down.
                    // TRACED at ef17ed9: every path inside SensitivityOf swallows — RedactionPolicy
                    // .IsPasswordOrFailClosed (RedactionPolicy.cs:10), ElementContent.SafeIdentity
                    // (ElementContent.cs:137), RedactionRule.Safe and RedactionRule.IsMatch
                    // (RedactionRules.cs:66,73, the latter `catch { return true; }`). It is kept as
                    // defence-in-depth against a future change INSIDE the classifier, and it fails CLOSED,
                    // because the failure it guards against is a silent pixel leak. Do not read it as
                    // evidence that SensitivityOf throws, and do not write a test for it — nothing can
                    // reach it.
                    //
                    // The sharper reason to LABEL it rather than delete it: an unlabelled catch here reads
                    // as "the CALLER provides the fail-closed guarantee". A future developer who believed
                    // that could strip the fail-closed logic from inside ElementContent and leave the eleven
                    // TEXT egress sites - which have no such catch - silently leaking, while the pixel path
                    // kept working and hid the regression.
                    Sensitivity sens;
                    try { sens = ElementContent.SensitivityOf(d, _classifier, procName); }
                    catch { sens = Sensitivity.UnreadableIdentity; } // an element we cannot CLASSIFY is
                                                                     // masked, never skipped
                    if (!sens.Redact) continue;

                    // A1: the rect read used to sit in that same swallowing catch, so a redact-worthy
                    // element whose BoundingRectangle THREW contributed NO mask and its pixels were
                    // captured — the identical fail-OPEN shape as DEF-1, on the other half of one line.
                    System.Drawing.Rectangle? own = null;
                    try { own = d.BoundingRectangle; } catch { }

                    var resolution = MaskEscalation.Resolve(own, ancestors.For(d), yardstick);
                    string aid = SafeRead(() => d.AutomationId, "") ?? string.Empty;
                    string ct = SafeRead(() => d.ControlType, FlaUI.Core.Definitions.ControlType.Custom).ToString();
                    if (resolution.Refused)
                        // Escalating to the root and masking IT would return a SUCCESSFUL all-black image,
                        // which is worse than an error: an agent hallucinates its contents or loops on it.
                        //
                        // The message names automationId and controlType, NEVER the Name — that is the
                        // identity the mask exists to hide. It ALSO names the window handle and process,
                        // because this same method backs the FULL-DESKTOP capture: without them an operator
                        // whose whole-desktop screenshot just refused sees `controlType='Edit',
                        // automationId=''` for a desktop of a dozen windows and has nothing to act on. The
                        // window is not withheld content — desktop_list_windows publishes handle, process
                        // and title already.
                        throw new ToolException(ToolErrorCode.RedactionUnmaskable,
                            $"A redact-worthy element in window '{handle.Id}' (process '{procName}') could not be masked (automationId='{aid}', controlType='{ct}'): neither it nor any ancestor reported usable bounds.",
                            "capture that window alone to confirm, or retry once the UI has settled");

                    pw.Add(resolution.Rect);
                    if (resolution.Escalated) escalations.Add(new MaskEscalationEntry(aid, ct));
                }
            }
            return new CaptureGeometry(captureBounds, pw, false, false, null, escalations);
            }
            catch (ToolException) { throw; }
            // ⚠ A CRITICAL failure is NOT a redaction outcome. Reclassifying OutOfMemoryException — or a
            // cancellation, if this path ever gains one — as RedactionUnmaskable would tell the agent to
            // "retry once the UI has settled" while the process is actually dying, and would hide the real
            // cause from every log above. (Measured: the query path has no cancellation today —
            // AutomationDispatcher.cs:61 is `_query.RunAsync(func)` with no timeout and no
            // CancellationToken; only the ACTION path has AwaitWithTimeout. The filter is there so that
            // stays true by construction if the query path ever gains one.)
            catch (System.Exception ex) when (ex is not System.OutOfMemoryException
                                              and not System.OperationCanceledException)
            {
                throw new ToolException(ToolErrorCode.RedactionUnmaskable,
                    $"Could not determine the redacted regions of window '{handle.Id}' (process '{procName}'): {ex.GetType()}",
                    "retry once the UI has settled, or capture a different window");
            }
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
                geo.Bounds, geo.MaskRects, geo.Bounds.X, geo.Bounds.Y, geo.Bounds.Width, geo.Bounds.Height);

        var win = geo.Bounds; // full window physical rect (target was `win` itself since @ref is null)
        var capture = TextCaptureGeometry.ComputeCaptureBounds(win, region);
        return new TextCaptureGeometry(false, null, false, capture, geo.MaskRects, win.X, win.Y, win.Width, win.Height);
    }

    /// <summary>DEF-2: full-desktop capture passed Array.Empty&lt;Rectangle&gt;() and so masked NOTHING — the one
    /// capture mode that photographs every window at once was the only mode with no redaction at all, while
    /// window- and element-scoped capture both masked correctly. Collects the mask rects of every visible
    /// non-denied window.
    ///
    /// A window that fails to resolve is SKIPPED rather than fatal: one unreadable window must not fail the
    /// whole capture. That is not a hole — ScreenshotTools already REFUSES a full-desktop capture outright
    /// when any denylisted credential window is visible, so this path only ever runs when none is.</summary>
    public async Task<IReadOnlyList<System.Drawing.Rectangle>> AllMaskRectsAsync()
    {
        var rects = new List<System.Drawing.Rectangle>();
        var windows = await _windows.ListWindowsAsync(includeBounds: false, includeHandles: true);
        foreach (var w in windows)
        {
            if (w.Handle is null || PerceptionPolicy.IsDenied(w.ProcessName)) continue;
            try
            {
                var geo = await ResolveWindowCaptureGeometryAsync(new WindowHandle(w.Handle), null);
                if (!geo.Denied && !geo.Minimized) rects.AddRange(geo.MaskRects);
            }
            catch { } // a window that closed mid-enumeration, or one we cannot bind: skip it
        }
        return rects;
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

public sealed record CaptureGeometry(System.Drawing.Rectangle Bounds, IReadOnlyList<System.Drawing.Rectangle> MaskRects, bool Minimized, bool Denied, string? DeniedProcess,
    IReadOnlyList<MaskEscalationEntry> Escalations);

public sealed record GridCellInfo(string Value, string ControlType, string AutomationId, bool IsPassword,
    bool Redacted, string? RedactedBy);

public sealed record TextReadResult(string Text, bool Truncated, bool IsPassword, string? TruncatedFrom = null,
    bool Redacted = false, string? RedactedBy = null);
