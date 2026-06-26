using System.ComponentModel;
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

    public InteractionTools(PerceptionManager perception, WindowManager windows, ServerOptions options)
    { _perception = perception; _windows = windows; _options = options; }

    private Task<string> Act(string window, string @ref,
        Action<FlaUI.Core.AutomationElements.AutomationElement> act, int timeoutMs)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            await _perception.RunOnRefActionAsync(new WindowHandle(window), @ref,
                el => { act(el); return true; }, timeoutMs);
            return ToolResponse.Ok(new { ok = true, pathUsed = "pattern" });
        });

    [McpServerTool(Destructive = true), Description("Invoke (activate) an element by ref via its UIA InvokePattern (e.g. click a button). If it opens a modal you get ActionBlockedPending — snapshot to see the dialog, then act on it.")]
    public Task<string> DesktopInvoke(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Element ref from a snapshot, e.g. e23.")] string @ref,
        [Description("Block timeout in ms (default 4000).")] int timeoutMs = DefaultActionTimeoutMs)
        => Act(window, @ref, Interactor.Invoke, timeoutMs);

    [McpServerTool(Destructive = true), Description("Set keyboard focus to an element by ref (UIA Focus). Often a prerequisite that reveals lazy-loaded content or enables downstream controls.")]
    public Task<string> DesktopSetFocus(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Element ref from a snapshot, e.g. e23.")] string @ref,
        [Description("Block timeout in ms (default 4000).")] int timeoutMs = DefaultActionTimeoutMs)
        => Act(window, @ref, Interactor.SetFocus, timeoutMs);

    [McpServerTool(Destructive = true), Description("Set an element's value by ref via UIA ValuePattern (fast text/value set; focuses first). ElementNotActionable if read-only, PatternUnsupported if no ValuePattern (no synthetic typing this phase).")]
    public Task<string> DesktopSetValue(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Element ref, e.g. e23.")] string @ref,
        [Description("The value to set.")] string value,
        [Description("Block timeout in ms (default 4000).")] int timeoutMs = DefaultActionTimeoutMs)
        => Act(window, @ref, el => Interactor.SetValue(el, value), timeoutMs);

    [McpServerTool(Destructive = true), Description("Toggle an element by ref via UIA TogglePattern (checkbox/switch).")]
    public Task<string> DesktopToggle(
        [Description("Window handle.")] string window, [Description("Element ref.")] string @ref,
        [Description("Block timeout in ms (default 4000).")] int timeoutMs = DefaultActionTimeoutMs)
        => Act(window, @ref, Interactor.Toggle, timeoutMs);

    [McpServerTool(Destructive = true), Description("Expand or collapse an element by ref via UIA ExpandCollapsePattern (tree node / expander / combo).")]
    public Task<string> DesktopExpand(
        [Description("Window handle.")] string window, [Description("Element ref.")] string @ref,
        [Description("Block timeout in ms (default 4000).")] int timeoutMs = DefaultActionTimeoutMs)
        => Act(window, @ref, Interactor.Expand, timeoutMs);

    [McpServerTool(Destructive = true), Description("Select an element by ref via UIA SelectionItemPattern (list item / radio / tab). Replaces the current selection (no multi-select this phase).")]
    public Task<string> DesktopSelect(
        [Description("Window handle.")] string window, [Description("Element ref.")] string @ref,
        [Description("Block timeout in ms (default 4000).")] int timeoutMs = DefaultActionTimeoutMs)
        => Act(window, @ref, Interactor.Select, timeoutMs);

    [McpServerTool(Destructive = true), Description("Scroll an element into view by ref via UIA ScrollItemPattern (realize an item in a scrollable container, then re-snapshot).")]
    public Task<string> DesktopScrollIntoView(
        [Description("Window handle.")] string window, [Description("Element ref.")] string @ref,
        [Description("Block timeout in ms (default 4000).")] int timeoutMs = DefaultActionTimeoutMs)
        => Act(window, @ref, Interactor.ScrollIntoView, timeoutMs);

    [McpServerTool(Destructive = true), Description("Scroll a container by ref via UIA ScrollPattern. direction = up|down|left|right; amount = number of small scroll steps (1..50). Realize virtualized items, then re-snapshot.")]
    public Task<string> DesktopScroll(
        [Description("Window handle.")] string window, [Description("Container element ref.")] string @ref,
        [Description("up|down|left|right")] string direction,
        [Description("Number of small scroll steps (default 1).")] double amount = 1,
        [Description("Block timeout in ms (default 4000).")] int timeoutMs = DefaultActionTimeoutMs)
        => Act(window, @ref, el => Interactor.Scroll(el, direction, amount), timeoutMs);
}
