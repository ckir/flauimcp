using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Stateless executor of a single UIA control-pattern action against an
/// already-resolved element on the correct (action) STA. No synthetic input (Phase 4),
/// no ref resolution, no dispatcher awareness. Throws PATTERN_UNSUPPORTED when the element
/// lacks the requested pattern.</summary>
public static class Interactor
{
    private static ToolException Unsupported(string pattern) => new(
        ToolErrorCode.PatternUnsupported,
        $"Element does not support the {pattern} pattern.",
        "snapshot to see the element's available patterns, or use a different tool");

    public static void Invoke(AutomationElement el)
    {
        if (!el.Patterns.Invoke.IsSupported) throw Unsupported("Invoke");
        el.Patterns.Invoke.Pattern.Invoke();
    }

    public static void SetValue(AutomationElement el, string value)
    {
        if (!el.Patterns.Value.IsSupported) throw Unsupported("Value");
        if (el.Patterns.Value.Pattern.IsReadOnly.ValueOrDefault)
            throw new ToolException(ToolErrorCode.ElementNotActionable,
                "The value control is read-only.", "pick an editable control");
        el.Focus();
        el.Patterns.Value.Pattern.SetValue(value);
    }

    public static void Toggle(AutomationElement el)
    {
        if (!el.Patterns.Toggle.IsSupported) throw Unsupported("Toggle");
        el.Patterns.Toggle.Pattern.Toggle();
    }

    public static void Expand(AutomationElement el)
    {
        if (!el.Patterns.ExpandCollapse.IsSupported) throw Unsupported("ExpandCollapse");
        var p = el.Patterns.ExpandCollapse.Pattern;
        if (p.ExpandCollapseState.ValueOrDefault == ExpandCollapseState.Collapsed) p.Expand();
        else p.Collapse();
    }

    public static void Select(AutomationElement el)
    {
        if (!el.Patterns.SelectionItem.IsSupported) throw Unsupported("SelectionItem");
        el.Patterns.SelectionItem.Pattern.Select();
    }

    public static void ScrollIntoView(AutomationElement el)
    {
        if (!el.Patterns.ScrollItem.IsSupported) throw Unsupported("ScrollItem");
        el.Patterns.ScrollItem.Pattern.ScrollIntoView();
    }

    public static void Scroll(AutomationElement el, string direction, double amount)
    {
        if (!el.Patterns.Scroll.IsSupported) throw Unsupported("Scroll");
        var p = el.Patterns.Scroll.Pattern;
        var v = ScrollAmount.NoAmount; var h = ScrollAmount.NoAmount;
        switch (direction.Trim().ToLowerInvariant())
        {
            case "up": v = ScrollAmount.SmallDecrement; break;
            case "down": v = ScrollAmount.SmallIncrement; break;
            case "left": h = ScrollAmount.SmallDecrement; break;
            case "right": h = ScrollAmount.SmallIncrement; break;
            default:
                throw new ToolException(ToolErrorCode.ElementNotActionable,
                    $"Unknown scroll direction '{direction}'.", "use up|down|left|right");
        }
        int reps = Math.Clamp((int)Math.Round(amount <= 0 ? 1 : amount), 1, 50);
        for (int i = 0; i < reps; i++) p.Scroll(h, v);
    }

    public static void SetFocus(AutomationElement el) => el.Focus();

    public static void WindowTransform(Window win, string action)
    {
        switch (action.Trim().ToLowerInvariant())
        {
            case "maximize":
                if (!win.Patterns.Window.IsSupported || !win.Patterns.Window.Pattern.CanMaximize.ValueOrDefault)
                    throw new ToolException(ToolErrorCode.ElementNotActionable, "Window cannot maximize.", "try restore/minimize");
                win.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized); break;
            case "minimize":
                if (!win.Patterns.Window.IsSupported || !win.Patterns.Window.Pattern.CanMinimize.ValueOrDefault)
                    throw new ToolException(ToolErrorCode.ElementNotActionable, "Window cannot minimize.", "try restore/maximize");
                win.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Minimized); break;
            case "restore":
                if (!win.Patterns.Window.IsSupported)
                    throw new ToolException(ToolErrorCode.ElementNotActionable, "Window pattern unsupported.", "n/a");
                win.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Normal); break;
            default:
                throw new ToolException(ToolErrorCode.ElementNotActionable,
                    $"Unknown window action '{action}'.", "use maximize|minimize|restore");
        }
    }
}
