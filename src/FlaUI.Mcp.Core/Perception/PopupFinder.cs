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
        // throwing, and the Path-1 filter is `c.ProcessId != ownerPid`. So with ownerPid == 0 every desktop
        // window whose own pid is also unreadable compares EQUAL and would be grafted into THIS window's
        // popup set. On the mask path that over-masks (harmless); on ref resolution and the ACTION path it
        // is not harmless at all — a ref could resolve into an unrelated application's window and be
        // clicked. PRE-EXISTING: `SafePid` returned -1 only when the read THREW, so a successful read of an
        // UNSET property returned 0 and sailed through the `< 0` test. Found at capstone round 5.
        //
        // ⚠ ROUND 5 THREW HERE, AND ROUND 6 SHOWED THAT WAS WRONG. A window with an unreadable pid and no
        // popups at all is perfectly capturable and interactive, and throwing denied service to it on all
        // twelve call sites — snapshot, find, wait, action — to prevent a mis-graft that only Path 1 can
        // commit. Path 1 is now simply SKIPPED when the owner cannot be attributed, and Path 2 (window
        // CHILDREN, which needs no pid) still runs. Nothing is mis-grafted and nothing is denied.
        //
        // ⚠ The residual is real and ledgered as AB-13: for a window whose pid cannot be read, DESKTOP-level
        // popups cannot be attributed to it and so are not masked. That is inherent — without a pid there is
        // no way to tell that window's popups from any other process's — not a choice being made here.
        bool canAttributeByPid = ownerPid > 0;

        // Path 1 — desktop-level children: Win32 #32768 menus and older HwndWrapper WPF hosts.
        // ⚠ NOT GUARDED (capstone round 4). This enumeration IS Path 1 — swallowing it substituted an
        // empty array, so Win32 #32768 menus and older WPF popup hosts silently vanished from the search
        // roots. Those are DESKTOP children, so `win.FindAllDescendants()` does not cover them and nothing
        // downstream could notice they were missing.
        AutomationElement[] desktopChildren = canAttributeByPid ? desktop.FindAllChildren() : Array.Empty<AutomationElement>();
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
                // ⚠⚠ THE HWND FALLBACK IS NOT BELT-AND-BRACES — WITHOUT IT THE WINDOW GRAFTS ITSELF.
                // Round 5 added the length test with a comment claiming the target window "does not match
                // the looksPopup test below". THAT CLAIM WAS FALSE and was asserted without checking:
                // `looksPopup` accepts `cls.StartsWith("HwndWrapper")`, and HwndWrapper[...] is exactly the
                // class name WPF gives EVERY top-level window, not just popup hosts. So with an unreadable
                // RuntimeId the window matched its own popup test and was appended to its own search roots,
                // giving [win, win] and a duplicated subtree. Caught at capstone round 6.
                if (IsSameElement(c, targetWindow, targetRid)) continue; // skip the window itself
                if (c.ControlType == FlaUI.Core.Definitions.ControlType.ToolTip) continue;
                if (c.Properties.IsOffscreen.ValueOrDefault) continue;
                // ⚠ A THROWN bounds read is NOT a zero-size popup. SafeRect used to answer Rectangle.Empty
                // for both, so a popup whose BoundingRectangle THREW was skipped here and never reached the
                // search roots at all — which DEFEATS A1 on exactly its own case: A1 exists to catch a
                // throwing BoundingRectangle during the mask walk and escalate, and the mask walk cannot
                // escalate over an element it was never given. Found at capstone round 6. A genuine
                // zero-size host is still skipped; an unreadable one proceeds to the popup test below and
                // lets the mask walk make the call.
                var rect = SafeRect(c);
                if (rect is not null && (rect.Value.Width <= 0 || rect.Value.Height <= 0)) continue;
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
                // ⚠ Same as Path 1: a THROWN bounds read must not drop the popup, or A1 never sees it.
                var rect = SafeRect(c);
                if (rect is not null && (rect.Value.Width <= 0 || rect.Value.Height <= 0)) continue;
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

    /// <summary>NULL means the bounds could not be READ, which is different from a zero-size rect and must
    /// stay different: a caller that conflates them silently drops the element A1 exists to catch.</summary>
    private static System.Drawing.Rectangle? SafeRect(AutomationElement el)
    {
        try { return el.BoundingRectangle; } catch { return null; }
    }

    /// <summary>Is <paramref name="candidate"/> the target window itself? RuntimeId first; HWND when the
    /// target's RuntimeId could not be read.
    ///
    /// ⚠ Both comparisons are guarded against the EMPTY/ZERO degenerate, because that is how this went
    /// wrong twice. Two unreadable RuntimeIds compare equal as empty arrays, and two unreadable HWNDs
    /// compare equal as IntPtr.Zero — either would skip a real popup as "the window itself". When neither
    /// identity can be established the answer is NO, and the popup test downstream decides.</summary>
    private static bool IsSameElement(AutomationElement candidate, AutomationElement targetWindow, int[] targetRid)
    {
        if (targetRid.Length > 0)
        {
            var rid = SafeRuntimeId(candidate);
            return rid.Length > 0 && rid.AsEnumerable().SequenceEqual(targetRid);
        }

        IntPtr targetHwnd = SafeHwnd(targetWindow);
        if (targetHwnd == IntPtr.Zero) return false;
        return SafeHwnd(candidate) == targetHwnd;
    }

    private static IntPtr SafeHwnd(AutomationElement el)
    {
        try { return el.Properties.NativeWindowHandle.ValueOrDefault; } catch { return IntPtr.Zero; }
    }
}
