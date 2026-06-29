using System.ComponentModel;
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
        [Description("Grid element ref from a snapshot, e.g. e23.")] string @ref,
        [Description("0-based row index.")] int row,
        [Description("0-based column index.")] int col,
        [Description("Read timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.Guard(async () =>
        {
            var c = await _perception.GetGridCellAsync(new WindowHandle(window), @ref, row, col, timeoutMs);
            return ToolResponse.Ok(new { value = c.Value, controlType = c.ControlType, automationId = c.AutomationId, isPassword = c.IsPassword });
        });

    [McpServerTool(Destructive = true), Description("Select a grid/table cell by (row,col) via UIA SelectionItemPattern. ref = the Grid element; row/col 0-based. GridCellOutOfRange if out of bounds; ElementNotActionable if the cell is off-screen (scroll first); PatternUnsupported if the cell isn't selectable. Blocked in --read-only-mode.")]
    public Task<string> DesktopGridSelect(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Grid element ref, e.g. e23.")] string @ref,
        [Description("0-based row index.")] int row,
        [Description("0-based column index.")] int col,
        [Description("Block timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            await _perception.RunOnRefActionAsync(new WindowHandle(window), @ref,
                el => { Interactor.GridSelect(el, row, col); return true; }, timeoutMs);
            return ToolResponse.Ok(new { ok = true, pathUsed = "pattern" });
        });

    [McpServerTool(ReadOnly = true), Description("Read an element's text via UIA TextPattern. selectionOnly=true reads the current selection (empty if none). maxLength caps output (default 10000, 1..200000); truncated=true if the text exceeded it. A password field returns text=\"[REDACTED]\", isPassword=true. Off-screen targets ARE readable. PatternUnsupported if no TextPattern.")]
    public Task<string> DesktopGetText(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Element ref from a snapshot, e.g. e23.")] string @ref,
        [Description("Read only the current selection (default false = full text).")] bool selectionOnly = false,
        [Description("Max chars (default 10000).")] int maxLength = 10000,
        [Description("Read timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.Guard(async () =>
        {
            var t = await _perception.GetTextAsync(new WindowHandle(window), @ref, selectionOnly, maxLength, timeoutMs);
            return ToolResponse.Ok(new { text = t.Text, truncated = t.Truncated, isPassword = t.IsPassword });
        });
}
