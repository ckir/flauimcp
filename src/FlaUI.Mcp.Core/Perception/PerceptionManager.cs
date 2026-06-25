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

    // Window subtree first, then the owner-process popup subtrees (so a ref to a grafted
    // context-menu item re-resolves). Each popup root is small and process-correct — Resolve never
    // searches the whole Desktop. REPLACES the window-only version added in Task 6.
    private static IReadOnlyList<AutomationElement> SearchRoots(AutomationElement win, AutomationElement desktop)
    {
        var roots = new List<AutomationElement> { win };
        roots.AddRange(FindOwnerPopups(desktop, win));
        return roots;
    }

    private static int SafePid(AutomationElement el)
    {
        try { return el.Properties.ProcessId.ValueOrDefault; } catch { return -1; }
    }

    private static int[] SafeRuntimeId(AutomationElement el)
    {
        try { return el.Properties.RuntimeId.ValueOrDefault ?? Array.Empty<int>(); }
        catch { return Array.Empty<int>(); }
    }

    private static System.Drawing.Rectangle SafeRect(AutomationElement el)
    {
        try { return el.BoundingRectangle; } catch { return System.Drawing.Rectangle.Empty; }
    }

    // Context menus and dropdowns can appear either as desktop-level children (Win32 #32768 menus,
    // older WPF HwndWrapper hosts) OR as direct children of the main window in UIA (WPF/.NET 10+
    // context menus surface as CT=Window, cls=Popup children of the owner window — empirically
    // confirmed: no separate desktop entry appears for the popup PID). Both search paths use the
    // same guards (no tooltips, no offscreen, no zero-size hosts).
    private static IReadOnlyList<AutomationElement> FindOwnerPopups(AutomationElement desktop, AutomationElement targetWindow)
    {
        var found = new List<AutomationElement>();
        int ownerPid = SafePid(targetWindow);
        if (ownerPid < 0) return found;
        int[] targetRid = SafeRuntimeId(targetWindow);

        // Path 1 — desktop-level children: Win32 #32768 menus and older HwndWrapper WPF hosts.
        AutomationElement[] desktopChildren;
        try { desktopChildren = desktop.FindAllChildren(); } catch { desktopChildren = Array.Empty<AutomationElement>(); }
        foreach (var c in desktopChildren)
        {
            try
            {
                if (c.Properties.ProcessId.ValueOrDefault != ownerPid) continue;
                if (SafeRuntimeId(c).AsEnumerable().SequenceEqual(targetRid)) continue; // skip the window itself
                if (c.ControlType == FlaUI.Core.Definitions.ControlType.ToolTip) continue;
                if (c.Properties.IsOffscreen.ValueOrDefault) continue;
                var rect = SafeRect(c);
                if (rect.Width <= 0 || rect.Height <= 0) continue;
                var cls = c.Properties.ClassName.ValueOrDefault ?? "";
                bool looksPopup =
                    cls == "#32768"                                              // Win32 context menu
                    || cls.StartsWith("HwndWrapper", StringComparison.Ordinal)  // older WPF popup host
                    || cls.Contains("Popup", StringComparison.OrdinalIgnoreCase)
                    || c.ControlType == FlaUI.Core.Definitions.ControlType.Menu;
                if (looksPopup) found.Add(c);
            }
            catch { /* transient — skip */ }
        }

        // Path 2 — window direct children: WPF/.NET 10 ContextMenu/Popup hosts surface as
        // CT=Window, cls=Popup direct children of the owner window (not as desktop-level entries).
        // Guarded the same way: no tooltips, no offscreen, no zero-size.
        AutomationElement[] winChildren;
        try { winChildren = targetWindow.FindAllChildren(); } catch { winChildren = Array.Empty<AutomationElement>(); }
        foreach (var c in winChildren)
        {
            try
            {
                if (c.ControlType == FlaUI.Core.Definitions.ControlType.ToolTip) continue;
                if (c.Properties.IsOffscreen.ValueOrDefault) continue;
                var rect = SafeRect(c);
                if (rect.Width <= 0 || rect.Height <= 0) continue;
                var cls = c.Properties.ClassName.ValueOrDefault ?? "";
                bool looksPopup =
                    cls == "Popup"                                               // WPF/.NET 10 popup host
                    || cls.Contains("Popup", StringComparison.OrdinalIgnoreCase)
                    || c.ControlType == FlaUI.Core.Definitions.ControlType.Menu;
                if (looksPopup) found.Add(c);
            }
            catch { /* transient — skip */ }
        }

        return found;
    }

    public Task<SnapshotResult> SnapshotAsync(WindowHandle handle, SnapshotOptions options) =>
        _windows.RunWithWindowAndDesktopAsync(handle, (win, desktop) =>
        {
            IReadOnlyList<AutomationElement> popups = FindOwnerPopups(desktop, win);
            AutomationElement root = string.IsNullOrEmpty(options.RootRef)
                ? win
                : _refs.Resolve(handle.Id, options.RootRef!, SearchRoots(win, desktop));
            var snapshotId = _refs.BeginSnapshot(handle.Id);
            var (tree, count) = SnapshotEngine.Walk(root, popups, options, _refs, handle.Id);
            return new SnapshotResult(snapshotId, tree, count);
        });
}
