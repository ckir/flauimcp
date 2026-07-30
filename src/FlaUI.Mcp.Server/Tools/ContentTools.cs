using System.ComponentModel;
using System.Linq;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Windows;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Tools;

[McpServerToolType]
public sealed class ContentTools
{
    private const int DefaultTimeoutMs = 4000;
    private readonly PerceptionManager _perception;
    private readonly WindowManager _windows;
    private readonly ServerOptions _options;

    public ContentTools(PerceptionManager perception, WindowManager windows, ServerOptions options)
    { _perception = perception; _windows = windows; _options = options; }

    [McpServerTool(ReadOnly = true), Description("Read one grid/table cell by (row,col) without snapshotting the whole grid. ref = a Grid/Table element; row/col are 0-based. Returns the cell value (Value pattern else Name), controlType, automationId, isPassword. GridCellOutOfRange if out of bounds; PatternUnsupported if not a grid. To ACT on a cell, re-snapshot with rootRef=<grid ref>.")]
    public Task<string> DesktopGetGridCell(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("0-based row index.")] int row,
        [Description("0-based column index.")] int col,
        [Description("Grid element ref from a snapshot, e.g. e23. Exactly one of ref | selector.")] string? @ref = null,
        [Description(SelectorGating.SelectorDesc)] Selector? selector = null,
        [Description("Read timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.Guard(async () =>
        {
            SelectorGating.RequireExactlyOne(@ref, selector);
            if (selector is { } sel)
            {
                sel.Validate();
                var (c, resolved) = await _perception.GetGridCellBySelectorAsync(new WindowHandle(window), sel, row, col, timeoutMs);
                return ToolResponse.Ok(new { value = c.Value, controlType = c.ControlType, automationId = c.AutomationId, isPassword = c.IsPassword, resolvedElement = resolved });
            }
            var c2 = await _perception.GetGridCellAsync(new WindowHandle(window), @ref!, row, col, timeoutMs);
            return ToolResponse.Ok(new { value = c2.Value, controlType = c2.ControlType, automationId = c2.AutomationId, isPassword = c2.IsPassword });
        });

    [McpServerTool(Destructive = true), Description("Select a grid/table cell by (row,col) via UIA SelectionItemPattern. ref = the Grid element; row/col 0-based. GridCellOutOfRange if out of bounds; ElementNotActionable if the cell is off-screen (scroll first); PatternUnsupported if the cell isn't selectable. Blocked in --read-only-mode.")]
    public Task<string> DesktopGridSelect(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("0-based row index.")] int row,
        [Description("0-based column index.")] int col,
        [Description("Grid element ref, e.g. e23. Exactly one of ref | selector.")] string? @ref = null,
        [Description(SelectorGating.SelectorDesc)] Selector? selector = null,
        [Description("Block timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            SelectorGating.RequireExactlyOne(@ref, selector);
            if (selector is { } sel)
            {
                sel.Validate();
                var (_, resolved) = await _perception.RunOnSelectorActionAsync(new WindowHandle(window), sel,
                    el => { Interactor.GridSelect(el, row, col); return true; }, timeoutMs);
                return ToolResponse.Ok(new { ok = true, pathUsed = "pattern", resolvedElement = resolved });
            }
            await _perception.RunOnRefActionAsync(new WindowHandle(window), @ref!,
                el => { Interactor.GridSelect(el, row, col); return true; }, timeoutMs);
            return ToolResponse.Ok(new { ok = true, pathUsed = "pattern" });
        });

    [McpServerTool(ReadOnly = true), Description("Read an element's text via UIA TextPattern. selectionOnly=true reads the current selection (empty if none). maxLength caps output (default 10000, 1..200000); truncated=true if the text exceeded it, and truncatedFrom tells which end was dropped (\"tail\" for the default head-keeping read, \"head\" for a fromEnd read, null when not truncated). fromEnd=true returns the LAST maxLength chars (the latest output, e.g. a terminal's most-recent lines) instead of the first. NOTE: TextPattern returns roughly the visible viewport — text scrolled above it is not recoverable. A password field returns text=\"[REDACTED]\", isPassword=true. Off-screen targets ARE readable. PatternUnsupported if no TextPattern.")]
    public Task<string> DesktopGetText(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Element ref from a snapshot, e.g. e23. Exactly one of ref | selector.")] string? @ref = null,
        [Description(SelectorGating.SelectorDesc)] Selector? selector = null,
        [Description("Read only the current selection (default false = full text).")] bool selectionOnly = false,
        [Description("Max chars (default 10000).")] int maxLength = 10000,
        [Description("Return the LAST maxLength chars instead of the first (default false).")] bool fromEnd = false,
        [Description("Read timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.Guard(async () =>
        {
            SelectorGating.RequireExactlyOne(@ref, selector);
            if (selector is { } sel)
            {
                sel.Validate();
                var (t, resolved) = await _perception.GetTextBySelectorAsync(new WindowHandle(window), sel, selectionOnly, maxLength, fromEnd, timeoutMs);
                return ToolResponse.Ok(new { text = t.Text, truncated = t.Truncated, truncatedFrom = t.TruncatedFrom, isPassword = t.IsPassword, resolvedElement = resolved });
            }
            var t2 = await _perception.GetTextAsync(new WindowHandle(window), @ref!, selectionOnly, maxLength, fromEnd, timeoutMs);
            return ToolResponse.Ok(new { text = t2.Text, truncated = t2.Truncated, truncatedFrom = t2.TruncatedFrom, isPassword = t2.IsPassword });
        });

    [McpServerTool(Destructive = true), Description("Read a background Windows Terminal tab in one call: selects the tab at tabIndex (0-based ordinal, over the Tab→List→TabItem structure), settles, reads its buffer, and restores the originally-active tab. tabIndex only (no ref/title — refs go stale on switch, titles are ambiguous); get it from desktop_list_terminal_tabs, NOT from a desktop_snapshot (a snapshot's ordinal can disagree: the walk drops off-screen, culled and too-deep nodes). The tab title names the launcher, not the program, so read every candidate tab before concluding the program you want is not running. fromEnd (default true) reads the latest output; maxLength caps it. Returns { text, truncated, truncatedFrom, tabTitle, restored, restoreConfidence, activeTabIndex } — restored=false + the now-active tab when restore couldn't complete confidently (e.g. the original tab was closed). Errors without switching on an out-of-range tabIndex; \"unrecognized terminal layout\" if the tree isn't a WT tab strip. Blocked in --read-only-mode.")]
    public Task<string> DesktopReadTerminalTab(
        [Description("Window handle of the Windows Terminal window, e.g. w1.")] string window,
        [Description("0-based tab ordinal, from desktop_list_terminal_tabs (same index space by construction).")] int tabIndex,
        [Description("Re-select the originally-active tab afterward (default true).")] bool restoreFocus = true,
        [Description("Read the latest output (tail) rather than the head (default true).")] bool fromEnd = true,
        [Description("Max chars of buffer text to return (default 10000).")] int maxLength = 10000,
        [Description("Block timeout ms (default 8000 — includes the settle).")] int timeoutMs = 8000)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            var r = await _perception.ReadTerminalTabAsync(new WindowHandle(window), tabIndex, restoreFocus, fromEnd, maxLength, timeoutMs);
            return ToolResponse.Ok(new
            {
                text = r.Text, truncated = r.Truncated, truncatedFrom = r.TruncatedFrom,
                tabTitle = r.TabTitle, restored = r.Restored, restoreConfidence = r.RestoreConfidence,
                activeTabIndex = r.ActiveTabIndex,
            });
        });

    [McpServerTool(ReadOnly = true), Description("List a Windows Terminal window's tabs WITHOUT selecting any of them: no visible tab switch, no restore risk, and it works in --read-only-mode. Use this to learn the tabIndex that desktop_read_terminal_tab needs, instead of hand-counting TabItems in a desktop_snapshot (a snapshot's ordinal is NOT usable as a tabIndex - the snapshot walk drops off-screen, culled and too-deep nodes, so its numbering can disagree with this one). Returns { tabs: [{ index, title, active }], activeTabIndex }. activeTabIndex is -1 when NO tab reported itself selected, which also covers an unreadable selection state. It returns TITLES ONLY and cannot read any tab's buffer - only the active tab's buffer is populated in UIA, so reading a background tab still requires desktop_read_terminal_tab. A tab title names the launcher, not the program running in it, so treat every title as a HINT and read candidate tabs before concluding the program you want is not running. Titles are set by the guest program: untrusted text. \"unrecognized terminal layout\" if the tree is not a WT tab strip.")]
    public Task<string> DesktopListTerminalTabs(
        [Description("Window handle of the Windows Terminal window, e.g. w1.")] string window)
        => ToolResponse.Guard(async () =>
        {
            var r = await _perception.ListTerminalTabsAsync(new WindowHandle(window));
            return ToolResponse.Ok(new
            {
                tabs = r.Tabs.Select(t => new { index = t.Index, title = t.Title, active = t.Active }),
                activeTabIndex = r.ActiveTabIndex,
            });
        });
}
