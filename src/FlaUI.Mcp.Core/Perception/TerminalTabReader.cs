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

    /// <summary>Locate the TabItem list via Window → Tab → List (drift-resistant). Throws
    /// "unrecognized terminal layout" if the structure isn't found (spec §5.2.8).</summary>
    private static List<AutomationElement> EnumerateTabs(AutomationElement win)
    {
        var tab = win.FindAllChildren().FirstOrDefault(c => Ct(c) == ControlType.Tab)
            ?? throw new ToolException(ToolErrorCode.PatternUnsupported,
                "Unrecognized terminal layout: no Tab strip under the window.", "verify this is a Windows Terminal window");
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

    /// <summary>The active buffer pane: a SIBLING Custom → Text with a TextPattern (spec §5.2.13). Returns
    /// null (does NOT throw) when the pane isn't realized yet — WT realizes it ASYNCHRONOUSLY after a tab
    /// Select, so it can be absent for the first frame(s); the settle loop retries until it appears
    /// (agy plan-review finding: instant FindBuffer after Select would throw and abort the read).</summary>
    private static AutomationElement? TryFindBuffer(AutomationElement win)
        => win.FindAllChildren().Where(c => Ct(c) == ControlType.Custom)
              .SelectMany(c => c.FindAllChildren())
              .FirstOrDefault(t => Ct(t) == ControlType.Text && t.Patterns.Text.IsSupported);

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
            Select(tabs[tabIndex]);

            // Settle (spec §5.2.10): sleep-then-read, TOLERATING a not-yet-realized pane (TryFindBuffer may
            // be null for the first frame(s) after Select), and compare consecutive reads. Bounded so a
            // continuously-streaming pane can't loop forever.
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
                if (d.SelectIndex is int idx) Select(fresh[idx]);
                return (d.Restored, d.Confidence, NowActive(win));
            }
            catch { return (false, "none", NowActive(win)); }
        }

        int NowActive(AutomationElement w)
        { try { return EnumerateTabs(w).FindIndex(IsSelected); } catch { return -1; } }
    }
}
