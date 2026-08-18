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

    /// <summary>One tab as reported by a PURE READ. Active means "this tab reported itself selected" —
    /// not ground truth: IsSelected swallows every COM fault to false (:62-66), so a tab whose
    /// SelectionItem pattern is unreadable reports Active:false exactly like an unselected one.</summary>
    public readonly record struct TabListing(int Index, string Title, bool Active);

    // Settle bound (spec §5.2.10): re-read + compare; cap the tries so a continuously-streaming pane
    // (never two equal reads) can't loop forever. Delay must exceed a frame so ConPTY auto-scroll lands.
    private const int SettleMaxTries = 4;
    private const int SettleDelayMs = 120;

    private static ControlType Ct(AutomationElement e)
    { try { return e.ControlType; } catch { return ControlType.Custom; } }

    /// <summary>The tab's title, classified ONCE. Read.Text is the EGRESS value (already redacted) and
    /// goes on the wire; Read.RawForIdentity is the raw value and is used ONLY to match a tab for restore,
    /// never emitted (BC-1). Splitting them here is the whole point: this file's five title reads divide
    /// into two egress and three identity, and blanket-redacting all five would break restore.</summary>
    private static ElementContent.Read Title(AutomationElement e, SensitivityClassifier classifier,
                                             string? processName)
        => ElementContent.Name(e, classifier, processName);

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

    /// <summary>Item 5: enumerate the tab strip WITHOUT selecting anything — no Select, no settle, no
    /// restore, no visible flicker. Shares EnumerateTabs with Run, which is the whole point: the index
    /// space is identical BY CONSTRUCTION rather than by agreement.
    ///
    /// NEVER call Select from here. That is load-bearing, not incidental — a settle loop or a
    /// "helpful" activation would silently turn a read-only tool into a destructive one, and the
    /// no-Select property cannot be proven at runtime (UIA event callbacks arrive on COM RPC threads,
    /// so a test asserting "no event fired" can pass before the event lands).
    ///
    /// ActiveTabIndex is -1 when NO tab reported itself selected, which conflates "nothing selected"
    /// with "selection state unreadable". Deliberate: see TabListing.</summary>
    public static (IReadOnlyList<TabListing> Tabs, int ActiveTabIndex) List(
        AutomationElement win, SensitivityClassifier classifier, string? processName)
    {
        var tabs = EnumerateTabs(win);
        var listing = new List<TabListing>(tabs.Count);
        int active = -1;
        for (int i = 0; i < tabs.Count; i++)
        {
            bool selected = IsSelected(tabs[i]);
            if (selected && active < 0) active = i;
            // EGRESS: TabListing.Title reaches the wire as `title` (ContentTools.cs:114).
            listing.Add(new TabListing(i, Title(tabs[i], classifier, processName).Text, selected));
        }
        return (listing, active);
    }

    /// <summary>Run the whole dance. <paramref name="readText"/> is PerceptionManager.ReadText bound to
    /// (selectionOnly:false, maxLength, fromEnd) so the settle/read reuse the exact §5.4 read path.
    /// INVARIANTS (agy plan-review): (a) the settled read's text ALWAYS reaches the returned Result on the
    /// success path; (b) Restore() NEVER throws — it degrades to Restored:false; (c) restore runs EXACTLY
    /// once — on the success path via the normal return, or on the error path via the catch, never both.</summary>
    public static Result Run(AutomationElement win, int tabIndex, bool restoreFocus, bool fromEnd, int maxLength,
        System.Func<AutomationElement, TextReadResult> readText,
        SensitivityClassifier classifier, string? processName)
    {
        var tabs = EnumerateTabs(win);
        int activeIndex = tabs.FindIndex(IsSelected);
        // Classify every tab ONCE up front: the egress title, the raw identity title and the sensitivity
        // all come out of the same read, so they cannot disagree with each other.
        var upFront = tabs.Select(t => Title(t, classifier, processName)).ToList();
        string activeTitle = activeIndex >= 0 ? upFront[activeIndex].RawForIdentity : ""; // IDENTITY: raw
        var activeSensitivity = activeIndex >= 0 ? upFront[activeIndex].Sensitivity : Sensitivity.Visible;
        // DEF-4: uniqueness is computed WITHIN the sensitivity partition. Counting across ALL tabs would
        // let a visible tab's title collide with a protected tab's and drop restoreConfidence from "high"
        // to "reduced" — an observable oracle for the protected tab's hidden title. Partitioning only
        // inside RestoreTarget.Resolve would leave this line as the same oracle, one step earlier.
        bool activeTitleUnique = activeIndex >= 0
            && upFront.Count(t => t.Sensitivity == activeSensitivity
                                  && string.Equals(t.RawForIdentity, activeTitle, System.StringComparison.Ordinal)) == 1;

        if (tabIndex < 0 || tabIndex >= tabs.Count)
            throw new ToolException(ToolErrorCode.InvalidArguments,
                $"tabIndex {tabIndex} is out of range (0..{tabs.Count - 1}).",
                // NOT desktop_snapshot: a snapshot's TabItem ordinal is not a valid tabIndex (four of the
                // five walk filters can drop a TabItem), which is exactly why desktop_list_terminal_tabs
                // exists. This recovery used to send the caller to the one path guaranteed to disagree.
                "call desktop_list_terminal_tabs to read the valid indexes for this window");

        // EGRESS: Result.TabTitle reaches the wire as `tabTitle` (ContentTools.cs:101). Reuses the
        // up-front classification — no second COM read.
        string targetTitle = upFront[tabIndex].Text;
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
                var reads = fresh.Select(f => Title(f, classifier, processName)).ToList();
                var titles = reads.Select(r => r.RawForIdentity).ToList();   // IDENTITY: raw, never emitted
                var sens = reads.Select(r => r.Sensitivity).ToList();        // DEF-4: the PARALLEL mask
                var d = RestoreTarget.Resolve(activeTitle, activeIndex, activeTitleUnique, titles,
                                              sens, activeSensitivity);
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
