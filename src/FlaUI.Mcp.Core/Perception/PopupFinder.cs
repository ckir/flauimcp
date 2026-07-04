using FlaUI.Core.AutomationElements;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>Stateless popup/search-root construction shared by the query path
/// (PerceptionManager.SnapshotAsync/RunOnRefAsync) and the action path
/// (PerceptionManager.RunOnRefActionAsync). Context menus / dropdowns live at the Desktop or
/// as direct window children — both must appear as resolution search roots so a ref into a
/// grafted popup re-resolves identically on either STA.</summary>
public static class PopupFinder
{
    /// <summary>Window subtree first, then the owner-process popup subtrees.
    /// searchRoots[0] MUST be the window root (IndexPath is window-relative).</summary>
    public static IReadOnlyList<AutomationElement> SearchRoots(AutomationElement win, AutomationElement desktop)
    {
        var roots = new List<AutomationElement> { win };
        roots.AddRange(FindOwnerPopups(desktop, win));
        return roots;
    }

    // Context menus and dropdowns can appear either as desktop-level children (Win32 #32768 menus,
    // older WPF HwndWrapper hosts) OR as direct children of the main window in UIA (WPF/.NET 10+
    // context menus surface as CT=Window, cls=Popup children of the owner window — empirically
    // confirmed: no separate desktop entry appears for the popup PID). Both search paths use the
    // same guards (no tooltips, no offscreen, no zero-size hosts).
    public static IReadOnlyList<AutomationElement> FindOwnerPopups(AutomationElement desktop, AutomationElement targetWindow)
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
                if (cls == FlaUI.Mcp.Core.Interaction.OverlaySentinel.ClassName) continue; // never graft the intent overlay
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
                if (cls == FlaUI.Mcp.Core.Interaction.OverlaySentinel.ClassName) continue; // never graft the intent overlay
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
}
