using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>Server-side composite: select a background WT tab, settle, read its buffer, restore the
/// originally-active tab — all in one action-STA hop (refs change on every switch, so it must be atomic
/// and in-process). Spec §5.5 / §5.2 items 8,9,10,13. Anchors on the UIA control-type structure
/// (Tab → List → TabItem[]; buffer is a SIBLING Custom → Text), NOT WinUI AutomationIds.</summary>
public static class TerminalTabReader
{
    public readonly record struct Result(
        string Text, bool Truncated, string? TruncatedFrom, string TabTitle,
        bool Restored, string RestoreConfidence, int ActiveTabIndex);

    // Settle bound (spec §5.2.10): re-read + compare; cap the tries so a continuously-streaming pane
    // (never two equal reads) can't loop forever. Delay must exceed a frame so ConPTY auto-scroll lands.
    private const int SettleMaxTries = 4;
    private const int SettleDelayMs = 120;

    private static ControlType Ct(AutomationElement e)
    { try { return e.ControlType; } catch { return ControlType.Custom; } }

    private static string NameOf(AutomationElement e)
    { try { return e.Name ?? ""; } catch { return ""; } }

    /// <summary>Locate the tab strip and its immediate container. WT nests the strip (and the buffer) under
    /// an intermediate content-wrapper Pane, so on current builds the strip is a GRANDCHILD of the window;
    /// older builds put it as a direct child. Bounded to depth ≤ 2 from the window — deliberately NOT an
    /// unbounded FindAllDescendants (which could anchor on an unrelated Tab in a Settings/command-palette
    /// pane, or hang the single action STA sweeping a large buffer subtree). The Tab strip and the
    /// Custom→Text buffer are SIBLINGS under the returned container (spec §5.2.13).</summary>
    private static (AutomationElement Container, AutomationElement Tab) LocateTabStrip(AutomationElement win)
    {
        var direct = win.FindAllChildren();
        var t0 = direct.FirstOrDefault(c => Ct(c) == ControlType.Tab);
        if (t0 is not null) return (win, t0);               // older WT: strip is a direct child of the window
        foreach (var c in direct)                            // current WT: strip is one level down, under a content Pane
        {
            var t = c.FindAllChildren().FirstOrDefault(g => Ct(g) == ControlType.Tab);
            if (t is not null) return (c, t);
        }
        throw new ToolException(ToolErrorCode.PatternUnsupported,
            "Unrecognized terminal layout: no Tab strip under the window.", "verify this is a Windows Terminal window");
    }

    /// <summary>Locate the TabItem list via the tab strip: Tab → List → TabItem[] (spec §5.2.8). Throws
    /// "unrecognized terminal layout" if the structure isn't found.</summary>
    private static List<AutomationElement> EnumerateTabs(AutomationElement win)
    {
        var (_, tab) = LocateTabStrip(win);
        var list = tab.FindAllChildren().FirstOrDefault(c => Ct(c) == ControlType.List) ?? tab;
        var items = list.FindAllChildren().Where(c => Ct(c) == ControlType.TabItem).ToList();
        if (items.Count == 0)
            throw new ToolException(ToolErrorCode.PatternUnsupported,
                "Unrecognized terminal layout: no TabItems in the tab strip.", "verify this is a Windows Terminal window");
        return items;
    }

    private static bool IsSelected(AutomationElement tabItem)
    {
        try { var p = tabItem.Patterns.SelectionItem.PatternOrDefault; return p is not null && p.IsSelected.ValueOrDefault; }
        catch { return false; }
    }

    private static void Select(AutomationElement tabItem)
    {
        var p = tabItem.Patterns.SelectionItem.PatternOrDefault
            ?? throw new ToolException(ToolErrorCode.PatternUnsupported, "Tab is not selectable.", "re-snapshot the terminal");
        p.Select();
    }

    /// <summary>The active buffer pane: the Custom → Text (with TextPattern) that is a SIBLING of the tab
    /// strip under the SAME container (spec §5.2.13). Scoped to the container — NOT a global descendant
    /// sweep — so a command-palette/Settings Custom→Text elsewhere can't be mistaken for the buffer, and the
    /// walk stays bounded. Returns null (does NOT throw) when the pane isn't realized yet — WT realizes it
    /// ASYNCHRONOUSLY after a tab Select, so it can be absent for the first frame(s); the settle loop retries.
    /// Re-locates the container fresh each call (robust to a stale handle after the switch).</summary>
    private static AutomationElement? TryFindBuffer(AutomationElement win)
    {
        try
        {
            var (container, _) = LocateTabStrip(win);
            return container.FindAllChildren().Where(c => Ct(c) == ControlType.Custom)
                            .SelectMany(c => c.FindAllChildren())
                            .FirstOrDefault(t => Ct(t) == ControlType.Text && t.Patterns.Text.IsSupported);
        }
        catch { return null; } // transient stale-element fault (or strip not realized yet) => treat as not-yet-realized
    }

    /// <summary>Run the whole dance. <paramref name="readText"/> is PerceptionManager.ReadText bound to
    /// (selectionOnly:false, maxLength, fromEnd) so the settle/read reuse the exact §5.4 read path.
    /// INVARIANTS (agy plan-review): (a) the settled read's text ALWAYS reaches the returned Result on the
    /// success path; (b) Restore() NEVER throws — it degrades to Restored:false; (c) restore runs EXACTLY
    /// once — on the success path via the normal return, or on the error path via the catch, never both.</summary>
    public static Result Run(AutomationElement win, int tabIndex, bool restoreFocus, bool fromEnd, int maxLength,
        System.Func<AutomationElement, TextReadResult> readText)
    {
        var tabs = EnumerateTabs(win);
        int activeIndex = tabs.FindIndex(IsSelected);
        string activeTitle = activeIndex >= 0 ? NameOf(tabs[activeIndex]) : "";
        bool activeTitleUnique = activeIndex >= 0
            && tabs.Count(t => string.Equals(NameOf(t), activeTitle, System.StringComparison.Ordinal)) == 1;

        if (tabIndex < 0 || tabIndex >= tabs.Count)
            throw new ToolException(ToolErrorCode.InvalidArguments,
                $"tabIndex {tabIndex} is out of range (0..{tabs.Count - 1}).", "list tabs via desktop_snapshot first");

        string targetTitle = NameOf(tabs[tabIndex]);
        bool restoreNeeded = restoreFocus && activeIndex >= 0; // nothing active => nothing to restore

        try
        {
            try { Select(tabs[tabIndex]); }
            catch (ToolException) { throw; }   // genuinely not selectable (no SelectionItem) — a real, non-transient failure
            catch                              // transient UIA/COM fault — the switch may still have landed
            {
                // Don't abort blindly: confirm the target actually became active before reading (else the
                // settle loop would read the WRONG tab's buffer). Bounded poll; if it never lands, surface
                // the original fault via rethrow.
                int a = NowActive(win);
                for (int i = 0; i < SettleMaxTries && a != tabIndex; i++)
                {
                    System.Threading.Thread.Sleep(SettleDelayMs);
                    a = NowActive(win);
                }
                if (a != tabIndex) throw;
            }

            // Settle (spec §5.2.10): sleep-then-read, TOLERATING a not-yet-realized pane (TryFindBuffer may
            // be null for the first frame(s) after Select), and compare consecutive reads. Bounded so a
            // continuously-streaming pane can't loop forever. COST: each readText(buf) re-reads the FULL
            // buffer (for fromEnd:true a GetText(-1) over the whole scrollback), up to SettleMaxTries times
            // (the loop usually breaks after 2 equal reads) — so timeoutMs must cover up to SettleMaxTries
            // full-buffer fetches on a large scrollback.
            TextReadResult? read = null;
            for (int i = 0; i < SettleMaxTries; i++)
            {
                System.Threading.Thread.Sleep(SettleDelayMs);
                var buf = TryFindBuffer(win);
                if (buf is null) continue;                 // pane not realized yet — keep waiting (bounded)
                var next = readText(buf);
                if (read is not null && string.Equals(next.Text, read.Text, System.StringComparison.Ordinal))
                { read = next; break; }                    // two equal reads => settled
                read = next;
            }
            if (read is null)                              // pane never realized within the bound
                throw new ToolException(ToolErrorCode.PatternUnsupported,
                    "Terminal buffer pane did not realize after activating the tab.",
                    "retry, or read via the programmatic channel");

            // Success path: restore (never throws), then return WITH the settled read's text.
            var (ok, conf, active) = restoreNeeded ? Restore() : (false, "n/a", NowActive(win));
            return new Result(read.Text, read.Truncated, read.TruncatedFrom, targetTitle, ok, conf, active);
        }
        catch
        {
            // Error path: restore is still attempted (spec §5.2.9 finally-equivalent). Restore() never
            // throws, so this cannot double-fault; then rethrow the original error.
            if (restoreNeeded) Restore();
            throw;
        }

        // Re-enumerate + restore by recorded identity (title-if-unique-else-ordinal). NEVER throws: any
        // failure (tree shifted, window closing) degrades to an honest (false, "none", now-active).
        (bool Restored, string Confidence, int Active) Restore()
        {
            try
            {
                var fresh = EnumerateTabs(win);
                var titles = fresh.Select(NameOf).ToList();
                var d = RestoreTarget.Resolve(activeTitle, activeIndex, activeTitleUnique, titles);
                if (d.SelectIndex is int idx)
                {
                    Select(fresh[idx]);
                    // WT applies the selection ASYNCHRONOUSLY — poll the live selection briefly before concluding
                    // the restore didn't land (an instant read races the async switch → false restored:false under
                    // load; the settle loop cushions the TARGET select but the restore select had no cushion).
                    // Bounded, same discipline as the settle loop.
                    int nowActive = NowActive(win);
                    for (int i = 0; i < SettleMaxTries && nowActive != idx; i++)
                    {
                        System.Threading.Thread.Sleep(SettleDelayMs);
                        nowActive = NowActive(win);
                    }
                    return nowActive == idx ? (d.Restored, d.Confidence, nowActive) : (false, "none", nowActive);
                }
                return (d.Restored, d.Confidence, NowActive(win)); // SelectIndex null => already (false,"none")
            }
            catch { return (false, "none", NowActive(win)); }
        }

        int NowActive(AutomationElement w)
        { try { return EnumerateTabs(w).FindIndex(IsSelected); } catch { return -1; } }
    }
}
