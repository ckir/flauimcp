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
