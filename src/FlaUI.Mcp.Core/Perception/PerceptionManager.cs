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

    public PerceptionManager(WindowManager windows, RefRegistry refs, SnapshotCache cache)
    {
        _windows = windows;
        _refs = refs;
        _cache = cache;
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

    // Replicate the snapshot security floor for targeted reads (they bypass SnapshotEngine).
    private static void EnsureAllowed(AutomationElement el)
    {
        var procName = SafeProcessName(el);
        if (PerceptionPolicy.IsDenied(procName))
            throw new ToolException(ToolErrorCode.TargetDenied,
                $"Reading content from windows owned by '{procName}' is blocked (credential store).",
                "target a different, non-sensitive element");
    }

    public Task<GridCellInfo> GetGridCellAsync(WindowHandle handle, string @ref, int row, int col, int timeoutMs) =>
        RunOnRefReadAsync(handle, @ref, el =>
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
        }, timeoutMs);

    public Task<TextReadResult> GetTextAsync(WindowHandle handle, string @ref, bool selectionOnly, int maxLength, int timeoutMs) =>
        RunOnRefReadAsync(handle, @ref, el =>
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
                        raw = (sel is { Length: > 0 }) ? sel[0].GetText(cap + 1) : string.Empty;
                    }
                    catch { raw = string.Empty; } // GetSelection is brittle (throws when no selection)
                }
                else raw = tp.DocumentRange.GetText(cap + 1);

                bool truncated = raw.Length > cap;
                if (truncated) raw = raw.Substring(0, cap);
                return new TextReadResult(raw, truncated, false);
            }
            catch (System.UnauthorizedAccessException)
            { throw new ToolException(ToolErrorCode.AccessDeniedIntegrity, "Cannot read the target (higher-integrity/elevated window).", "run the target at the same integrity level"); }
        }, timeoutMs);

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
        WindowHandle handle, SnapshotOptions options, RefRegistry refs) =>
        _windows.RunWithWindowAndDesktopAsync(handle, (win, desktop) =>
        {
            var procName = SafeProcessName(win);
            if (PerceptionPolicy.IsDenied(procName))
                throw new ToolException(ToolErrorCode.TargetDenied,
                    $"Snapshotting windows owned by '{procName}' is blocked (credential store).",
                    "snapshot a different, non-sensitive window");
            IReadOnlyList<AutomationElement> popups = PopupFinder.FindOwnerPopups(desktop, win);
            AutomationElement root = string.IsNullOrEmpty(options.RootRef)
                ? win : refs.Resolve(handle.Id, options.RootRef!, PopupFinder.SearchRoots(win, desktop));
            var snapshotId = refs.BeginSnapshot(handle.Id);
            var model = SnapshotEngine.Build(root, popups, options, refs, handle.Id);
            return (snapshotId, model);
        });

    /// <summary>desktop_find: resolve a UIA condition on the query STA and mint durable refs for the
    /// matches WITHOUT superseding the window's snapshot refs (additive Register - a narrow find must
    /// not invalidate a held snapshot ref). Applies the snapshot security floor: deny-list guard on
    /// the window (INV-5) + IsPassword name redaction BEFORE the match decision (no name-oracle).
    /// Matches are capped at max in tree order; TotalMatches/IsTruncated report the full count so
    /// truncation is never silent.</summary>
    public Task<FindResult> FindAsync(WindowHandle handle, FindQuery query, int max, string? scopeRef) =>
        _windows.RunWithWindowAndDesktopAsync(handle, (win, desktop) =>
        {
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
                if (!string.IsNullOrEmpty(query.Name) && string.Equals(query.NameMatch, "eq", System.StringComparison.Ordinal))
                    c = c is null ? cf.ByName(query.Name) : c.And(cf.ByName(query.Name));
                return c;
            }

            // hasNative iff a native-expressible constraint exists (NOT name-contains / enabledOnly).
            // When absent, match ALL via the no-arg overload (repo idiom PerceptionManager.cs:226) -
            // NOT a TrueCondition/double-negation surrogate.
            bool hasNative = !string.IsNullOrEmpty(query.AutomationId) || hasCtConstraint
                || (!string.IsNullOrEmpty(query.Name) && string.Equals(query.NameMatch, "eq", System.StringComparison.Ordinal));
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
                string rawName = SafeRead(() => el.Name, "");         // raw -> descriptor (re-resolution key)
                string name = isPwd ? "[REDACTED]" : rawName;         // redacted -> match + output
                bool enabled = SafeRead(() => el.IsEnabled, false);
                if (!spec.MatchesPostFilter(name, enabled)) continue; // match on the redacted name (no name-oracle)
                total++;
                if (matches.Count >= max) continue; // keep counting total, stop collecting

                // Read each primitive ONCE; reuse for BOTH the descriptor and the FindMatch (no double reads).
                int[] rid = SafeRead(() => el.Properties.RuntimeId.ValueOrDefault, (int[]?)null) ?? System.Array.Empty<int>();
                var ctEnum = SafeRead(() => el.ControlType, FlaUI.Core.Definitions.ControlType.Custom);
                string aid = SafeRead(() => el.AutomationId, "");
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

    private static T SafeRead<T>(Func<T> read, T fallback) { try { return read(); } catch { return fallback; } }

    public async Task<SnapshotResult> SnapshotAsync(WindowHandle handle, SnapshotOptions options)
    {
        var (snapshotId, model) = await BuildModelAsync(handle, options, _refs);
        _cache.Put(snapshotId, model);
        return new SnapshotResult(snapshotId, SnapshotEngine.Render(model, options), model.NodeCount);
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

    public async Task<SnapshotDiffResult> DiffAsync(WindowHandle handle, string baselineSnapshotId)
    {
        if (!_cache.TryGet(baselineSnapshotId, out var baseline) || baseline is null)
            throw new ToolException(ToolErrorCode.SnapshotNotFound, $"Baseline snapshot '{baselineSnapshotId}' is not in the cache.", "re-take the baseline snapshot");
        var baseWindowId = baselineSnapshotId.Split(':')[0];
        if (!string.Equals(baseWindowId, handle.Id, System.StringComparison.Ordinal))
            throw new ToolException(ToolErrorCode.SnapshotWindowMismatch, $"Baseline '{baselineSnapshotId}' belongs to window '{baseWindowId}', not '{handle.Id}'.", "pass a baselineSnapshotId from the same window");
        var (currentId, current) = await BuildModelAsync(handle, new SnapshotOptions(), _refs);
        _cache.Put(currentId, current);
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

public sealed record TextReadResult(string Text, bool Truncated, bool IsPassword);
