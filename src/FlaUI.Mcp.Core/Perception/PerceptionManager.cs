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

    public PerceptionManager(WindowManager windows, RefRegistry refs)
    {
        _windows = windows;
        _refs = refs;
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

    public Task<SnapshotResult> SnapshotAsync(WindowHandle handle, SnapshotOptions options) =>
        _windows.RunWithWindowAndDesktopAsync(handle, (win, desktop) =>
        {
            // Security floor: refuse to snapshot a window owned by a known credential store. A snapshot
            // would pull its entire UIA tree into agent context (exfiltration risk + prompt-injection
            // target). Reject BEFORE BeginSnapshot/Walk so no refs or tree are produced. See PerceptionPolicy.
            var procName = SafeProcessName(win);
            if (PerceptionPolicy.IsDenied(procName))
                throw new ToolException(ToolErrorCode.TargetDenied,
                    $"Snapshotting windows owned by '{procName}' is blocked (credential store).",
                    "snapshot a different, non-sensitive window");

            IReadOnlyList<AutomationElement> popups = PopupFinder.FindOwnerPopups(desktop, win);
            AutomationElement root = string.IsNullOrEmpty(options.RootRef)
                ? win
                : _refs.Resolve(handle.Id, options.RootRef!, PopupFinder.SearchRoots(win, desktop));
            var snapshotId = _refs.BeginSnapshot(handle.Id);
            var (tree, count) = SnapshotEngine.Walk(root, popups, options, _refs, handle.Id);
            return new SnapshotResult(snapshotId, tree, count);
        });
}
