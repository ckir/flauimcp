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
            var el = _refs.ResolveDescriptor(descriptor, roots, @ref); // REF_STALE_UNRESOLVABLE if gone
            if (el.Properties.IsOffscreen.ValueOrDefault)
                throw new ToolException(ToolErrorCode.ElementNotActionable,
                    "Element is off-screen; cannot act on it reliably.", "desktop_scroll_into_view then retry");
            return func(el);
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
            var el = _refs.ResolveDescriptor(descriptor, roots, @ref);
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
                bool isPwd = false;
                try { isPwd = cell.Properties.IsPassword.ValueOrDefault; } catch { }
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
