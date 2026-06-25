using FlaUI.Core.AutomationElements;
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
            var el = _refs.Resolve(handle.Id, @ref, SearchRoots(win, desktop));
            return func(el);
        });

    // Roots to re-resolve a ref against, in order. Window subtree only for now; Task 7 appends the
    // owner-process popup subtrees so popup refs resolve too. searchRoots[0] is always the window.
    private static IReadOnlyList<AutomationElement> SearchRoots(AutomationElement win, AutomationElement desktop)
        => new AutomationElement[] { win };

    public Task<SnapshotResult> SnapshotAsync(WindowHandle handle, SnapshotOptions options) =>
        _windows.RunWithWindowAndDesktopAsync(handle, (win, desktop) =>
        {
            // popup roots: empty until Task 7 grafts owner-process menus.
            IReadOnlyList<AutomationElement> popups = Array.Empty<AutomationElement>();
            AutomationElement root = string.IsNullOrEmpty(options.RootRef)
                ? win
                : _refs.Resolve(handle.Id, options.RootRef!, SearchRoots(win, desktop));
            var snapshotId = _refs.BeginSnapshot(handle.Id);
            var (tree, count) = SnapshotEngine.Walk(root, popups, options, _refs, handle.Id);
            return new SnapshotResult(snapshotId, tree, count);
        });
}
