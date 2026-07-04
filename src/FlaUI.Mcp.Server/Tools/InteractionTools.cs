using System.ComponentModel;
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Windows;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Tools;

[McpServerToolType]
public sealed class InteractionTools
{
    private const int DefaultActionTimeoutMs = 4000;
    private readonly PerceptionManager _perception;
    private readonly WindowManager _windows;
    private readonly ServerOptions _options;
    private readonly IActionOverlay _overlay;

    public InteractionTools(PerceptionManager perception, WindowManager windows, ServerOptions options,
        IActionOverlay? overlay = null)
    { _perception = perception; _windows = windows; _options = options; _overlay = overlay ?? NullActionOverlay.Instance; }

    private Task<string> Act(string window, string? @ref, Selector? selector,
        Action<FlaUI.Core.AutomationElements.AutomationElement> act, int timeoutMs)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            SelectorGating.RequireExactlyOne(@ref, selector);
            var handle = new WindowHandle(window);
            if (selector is { } sel)
            {
                sel.Validate();
                // Overlay (post-authorization = after GuardWrite + a successful resolve; pre-effect):
                // phase-1 read bounds off the fresh selector walk, preview off-STA, then the atomic act.
                if (_overlay.Enabled)
                {
                    var (rect, _) = await _perception.RunOnSelectorReadAsync(handle, sel, BoundsOf, timeoutMs);
                    await _overlay.PreviewAsync(rect);
                }
                var (_, resolved) = await _perception.RunOnSelectorActionAsync(
                    handle, sel, el => { act(el); return true; }, timeoutMs);
                return ToolResponse.Ok(new { ok = true, pathUsed = "pattern", resolvedElement = resolved });
            }
            if (_overlay.Enabled)
            {
                var rect = await _perception.RunOnRefReadAsync(handle, @ref!, BoundsOf, timeoutMs);
                await _overlay.PreviewAsync(rect);
            }
            await _perception.RunOnRefActionAsync(handle, @ref!, el => { act(el); return true; }, timeoutMs);
            return ToolResponse.Ok(new { ok = true, pathUsed = "pattern" });
        });

    // Read an element's physical bounds as an OverlayRect on the STA (best-effort — a throw degrades to a
    // degenerate rect the overlay skips; INV-OV-4).
    private static OverlayRect BoundsOf(FlaUI.Core.AutomationElements.AutomationElement el)
    {
        try { var r = el.BoundingRectangle; return new OverlayRect(r.Left, r.Top, r.Width, r.Height); }
        catch { return default; }
    }

    private const string RefDesc = "Element ref from a snapshot, e.g. e23. Exactly one of ref | selector.";

    [McpServerTool(Destructive = true), Description("Invoke (activate) an element by ref via its UIA InvokePattern (e.g. click a button). If it opens a modal you get ActionBlockedPending — snapshot to see the dialog, then act on it.")]
    public Task<string> DesktopInvoke(
        [Description("Window handle, e.g. w1.")] string window,
        [Description(RefDesc)] string? @ref = null,
        [Description(SelectorGating.SelectorDesc)] Selector? selector = null,
        [Description("Block timeout in ms (default 4000).")] int timeoutMs = DefaultActionTimeoutMs)
        => Act(window, @ref, selector, Interactor.Invoke, timeoutMs);

    [McpServerTool(Destructive = true), Description("Set keyboard focus to an element by ref (UIA Focus). Often a prerequisite that reveals lazy-loaded content or enables downstream controls.")]
    public Task<string> DesktopSetFocus(
        [Description("Window handle, e.g. w1.")] string window,
        [Description(RefDesc)] string? @ref = null,
        [Description(SelectorGating.SelectorDesc)] Selector? selector = null,
        [Description("Block timeout in ms (default 4000).")] int timeoutMs = DefaultActionTimeoutMs)
        => Act(window, @ref, selector, Interactor.SetFocus, timeoutMs);

    [McpServerTool(Destructive = true), Description("Set an element's value by ref via UIA ValuePattern (fast text/value set; focuses first). ElementNotActionable if read-only, PatternUnsupported if no ValuePattern (no synthetic typing this phase).")]
    public Task<string> DesktopSetValue(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("The value to set.")] string value,
        [Description("Element ref, e.g. e23. Exactly one of ref | selector.")] string? @ref = null,
        [Description(SelectorGating.SelectorDesc)] Selector? selector = null,
        [Description("Block timeout in ms (default 4000).")] int timeoutMs = DefaultActionTimeoutMs)
        => Act(window, @ref, selector, el => Interactor.SetValue(el, value), timeoutMs);

    [McpServerTool(Destructive = true), Description("Toggle an element by ref via UIA TogglePattern (checkbox/switch).")]
    public Task<string> DesktopToggle(
        [Description("Window handle.")] string window,
        [Description(RefDesc)] string? @ref = null,
        [Description(SelectorGating.SelectorDesc)] Selector? selector = null,
        [Description("Block timeout in ms (default 4000).")] int timeoutMs = DefaultActionTimeoutMs)
        => Act(window, @ref, selector, Interactor.Toggle, timeoutMs);

    [McpServerTool(Destructive = true), Description("Expand or collapse an element by ref via UIA ExpandCollapsePattern (tree node / expander / combo).")]
    public Task<string> DesktopExpand(
        [Description("Window handle.")] string window,
        [Description(RefDesc)] string? @ref = null,
        [Description(SelectorGating.SelectorDesc)] Selector? selector = null,
        [Description("Block timeout in ms (default 4000).")] int timeoutMs = DefaultActionTimeoutMs)
        => Act(window, @ref, selector, Interactor.Expand, timeoutMs);

    [McpServerTool(Destructive = true), Description("Select an element by ref via UIA SelectionItemPattern (list item / radio / tab). Replaces the current selection (no multi-select this phase).")]
    public Task<string> DesktopSelect(
        [Description("Window handle.")] string window,
        [Description(RefDesc)] string? @ref = null,
        [Description(SelectorGating.SelectorDesc)] Selector? selector = null,
        [Description("Block timeout in ms (default 4000).")] int timeoutMs = DefaultActionTimeoutMs)
        => Act(window, @ref, selector, Interactor.Select, timeoutMs);

    [McpServerTool(Destructive = true), Description("Scroll an element into view by ref via UIA ScrollItemPattern (realize an item in a scrollable container, then re-snapshot).")]
    public Task<string> DesktopScrollIntoView(
        [Description("Window handle.")] string window,
        [Description(RefDesc)] string? @ref = null,
        [Description(SelectorGating.SelectorDesc)] Selector? selector = null,
        [Description("Block timeout in ms (default 4000).")] int timeoutMs = DefaultActionTimeoutMs)
        => Act(window, @ref, selector, Interactor.ScrollIntoView, timeoutMs);

    [McpServerTool(Destructive = true), Description("Scroll a container by ref via UIA ScrollPattern. direction = up|down|left|right; amount = number of small scroll steps (1..50). Realize virtualized items, then re-snapshot.")]
    public Task<string> DesktopScroll(
        [Description("Window handle.")] string window,
        [Description("up|down|left|right")] string direction,
        [Description("Container element ref. Exactly one of ref | selector.")] string? @ref = null,
        [Description(SelectorGating.SelectorDesc)] Selector? selector = null,
        [Description("Number of small scroll steps (default 1).")] double amount = 1,
        [Description("Block timeout in ms (default 4000).")] int timeoutMs = DefaultActionTimeoutMs)
        => Act(window, @ref, selector, el => Interactor.Scroll(el, direction, amount), timeoutMs);

    [McpServerTool(Destructive = true), Description("Transform a window by handle via UIA Window/Transform patterns. action = maximize|minimize|restore. (Close is desktop_close_window; move/resize land later.)")]
    public Task<string> DesktopWindowTransform(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("maximize|minimize|restore")] string action,
        [Description("Block timeout in ms (default 4000).")] int timeoutMs = DefaultActionTimeoutMs)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            await _windows.WindowTransformAsync(new WindowHandle(window), action, timeoutMs);
            return ToolResponse.Ok(new { ok = true, action });
        });
}
