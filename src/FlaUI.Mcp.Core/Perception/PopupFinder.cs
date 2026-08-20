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

        // ⚠⚠ NOT GUARDED, DELIBERATELY, AND THIS READ USED TO BE THE MOST FRAGILE POINT IN THE FILE.
        // It was `SafePid(targetWindow)` with `if (ownerPid < 0) return found;` — so ONE failed ProcessId
        // read on the target window returned an EMPTY popup set, and the caller could not tell that from
        // "this window genuinely has no popups". On the mask path that meant every desktop-level popup
        // went unmasked while the capture SUCCEEDED: an A1-class fail-open on a path A1 never touched.
        // Found at SP4 capstone round 4. Letting it throw makes the mask walk convert it to
        // RedactionUnmaskable, and makes every other caller fail loudly instead of silently seeing
        // "no popups".
        int ownerPid = targetWindow.Properties.ProcessId.ValueOrDefault;
        int[] targetRid = SafeRuntimeId(targetWindow);

        // ⚠⚠ AN UNESTABLISHABLE OWNER PID IS NOT A PID OF ZERO — it is "I do not know", and treating it as
        // a value is a MIS-GRAFT, not a miss. `ValueOrDefault` answers 0 for an unset property WITHOUT
        // throwing, and the Path-1 filter below is `c.ProcessId != ownerPid`. So with ownerPid == 0 every
        // desktop window whose own pid is also unreadable compares EQUAL and is considered for grafting
        // into THIS window's popup set. For the mask path that over-masks (harmless); for ref resolution
        // and the ACTION path it is not harmless at all — a ref could resolve into an unrelated
        // application's window and be clicked.
        //
        // ⚠ PRE-EXISTING, and the previous guard did not cover it: `SafePid` returned -1 only when the read
        // THREW, and the `< 0` test caught only that. A successful read of an UNSET property returned 0 and
        // sailed through. Found at SP4 capstone round 5.
        if (ownerPid <= 0)
            throw new InvalidOperationException(
                "Cannot establish the owning process of the target window, so its popup set cannot be " +
                "attributed. Refusing rather than grafting every unattributable window.");

        // Path 1 — desktop-level children: Win32 #32768 menus and older HwndWrapper WPF hosts.
        // ⚠ NOT GUARDED (capstone round 4). This enumeration IS Path 1 — swallowing it substituted an
        // empty array, so Win32 #32768 menus and older WPF popup hosts silently vanished from the search
        // roots. Those are DESKTOP children, so `win.FindAllDescendants()` does not cover them and nothing
        // downstream could notice they were missing.
        AutomationElement[] desktopChildren = desktop.FindAllChildren();
        foreach (var c in desktopChildren)
        {
            try
            {
                if (c.Properties.ProcessId.ValueOrDefault != ownerPid) continue;
                // ⚠ THE LENGTH TEST IS LOAD-BEARING: without it, TWO EMPTIES COMPARE EQUAL. `SafeRuntimeId`
                // answers an empty array when the read fails, so if the TARGET window's RuntimeId is
                // unreadable, every desktop child whose RuntimeId is ALSO unreadable satisfies
                // SequenceEqual and is skipped as "the window itself" — silently dropping a real popup
                // from the mask set while the capture succeeds. Pre-existing; found at capstone round 5.
                //
                // An empty targetRid means the self-exclusion simply cannot be performed. That is safe to
                // proceed without: the window itself does not match the `looksPopup` class/control-type
                // test below, so it is not grafted into its own popup set.
                if (targetRid.Length > 0 && SafeRuntimeId(c).AsEnumerable().SequenceEqual(targetRid)) continue; // skip the window itself
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
            // ⚠ THIS PER-CHILD CATCH STAYS LENIENT, and the asymmetry with the three reads above is the
            // point. This loop iterates EVERY top-level window on the desktop, most of them belonging to
            // unrelated applications. Making it strict would tie the success of a capture to reading
            // properties on every other app's windows — one misbehaving unrelated process would refuse
            // captures machine-wide. Skipping one CANDIDATE also loses at most one popup, whereas the
            // three unguarded reads above each lost an entire class of them.
            catch { /* transient — skip: see the note above on why this one is not strict */ }
        }

        // Path 2 — window direct children: WPF/.NET 10 ContextMenu/Popup hosts surface as
        // CT=Window, cls=Popup direct children of the owner window (not as desktop-level entries).
        // Guarded the same way: no tooltips, no offscreen, no zero-size.
        // ⚠ NOT GUARDED (capstone round 4), same reasoning as Path 1. Path-2 popups ARE window children,
        // so the window's own descendant walk happens to cover them for MASKING — but not for ref
        // resolution or the action path, where losing them silently means a ref into a popup stops
        // resolving with no explanation.
        AutomationElement[] winChildren = targetWindow.FindAllChildren();
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
