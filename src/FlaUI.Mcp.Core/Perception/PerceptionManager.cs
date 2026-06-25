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

    public Task<SnapshotResult> SnapshotAsync(WindowHandle handle, SnapshotOptions options) =>
        _windows.RunWithWindowAndDesktopAsync(handle, (win, desktop) =>
        {
            // popup roots: empty until Task 7 grafts owner-process menus.
            IReadOnlyList<AutomationElement> popups = Array.Empty<AutomationElement>();
            AutomationElement root = win; // RootRef rooting added in Task 6
            var snapshotId = _refs.BeginSnapshot(handle.Id);
            var (tree, count) = SnapshotEngine.Walk(root, popups, options, _refs, handle.Id);
            return new SnapshotResult(snapshotId, tree, count);
        });
}
