using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>Walks a UIA subtree into an indented, ref-tagged text snapshot. Stateless;
/// registers each surfaced element into the supplied RefRegistry. Runs on the caller's
/// thread — callers MUST invoke it on the query STA (see WindowManager primitive).</summary>
public static class SnapshotEngine
{
    // Curated "meaningful" roles surfaced under interactiveOnly. Containers/decoration are
    // pruned from OUTPUT but still recursed THROUGH so their interactive descendants appear.
    private static readonly HashSet<ControlType> InteractiveTypes = new()
    {
        ControlType.Button, ControlType.CheckBox, ControlType.ComboBox, ControlType.Edit,
        ControlType.Hyperlink, ControlType.ListItem, ControlType.MenuItem, ControlType.RadioButton,
        ControlType.Slider, ControlType.Spinner, ControlType.SplitButton, ControlType.Tab,
        ControlType.TabItem, ControlType.TreeItem, ControlType.Document, ControlType.List,
        ControlType.Menu, ControlType.Tree, ControlType.DataGrid, ControlType.Table,
    };

    public static (string Tree, int NodeCount) Walk(
        AutomationElement root,
        IReadOnlyList<AutomationElement> popupRoots,
        SnapshotOptions options,
        RefRegistry refs,
        string windowId)
    {
        var sb = new StringBuilder();
        int count = 0;
        Visit(root, depth: 0, indexPath: Array.Empty<int>(), ancestorAid: null, indent: "");

        if (popupRoots.Count > 0)
        {
            sb.AppendLine("[Popups]");
            for (int i = 0; i < popupRoots.Count; i++)
                Visit(popupRoots[i], depth: 0, indexPath: new[] { -1 - i }, ancestorAid: null, indent: "  ");
        }
        return (sb.ToString(), count);

        void Visit(AutomationElement el, int depth, int[] indexPath, string? ancestorAid, string indent)
        {
            string aid = Safe(() => el.AutomationId, "");
            ControlType ct = Safe(() => el.ControlType, ControlType.Custom);
            string name = Safe(() => el.Name, "");

            bool include = depth == 0 || !options.InteractiveOnly || IsInteresting(el, ct, name);
            string childIndent = indent;
            if (include)
            {
                var descriptor = new ElementDescriptor(
                    RuntimeId: Safe(() => (IReadOnlyList<int>)(el.Properties.RuntimeId.ValueOrDefault ?? Array.Empty<int>()), Array.Empty<int>()),
                    ControlType: ct, AutomationId: aid, Name: name,
                    AncestorAutomationId: ancestorAid, IndexPath: indexPath);
                var @ref = refs.Register(windowId, descriptor, el);
                sb.AppendLine(FormatLine(indent, @ref, el, ct, name, aid, options));
                count++;
                childIndent = indent + "  ";
            }

            if (depth >= options.MaxDepth) return;
            var nextAncestor = string.IsNullOrEmpty(aid) ? ancestorAid : aid;
            AutomationElement[] children = Safe(() => el.FindAllChildren(), Array.Empty<AutomationElement>());
            for (int i = 0; i < children.Length; i++)
            {
                var nextPath = new int[indexPath.Length + 1];
                Array.Copy(indexPath, nextPath, indexPath.Length);
                nextPath[^1] = i;
                Visit(children[i], depth + 1, nextPath, nextAncestor, childIndent);
            }
        }
    }

    private static bool IsInteresting(AutomationElement el, ControlType ct, string name)
    {
        if (InteractiveTypes.Contains(ct)) return true;
        if (ct == ControlType.Text && !string.IsNullOrWhiteSpace(name)) return true; // named labels inform
        if (Safe(() => el.Properties.IsKeyboardFocusable.ValueOrDefault, false)) return true;
        // any actionable pattern makes it interesting
        return SupportedPatterns(el).Length > 0;
    }

    private static string FormatLine(string indent, string @ref, AutomationElement el,
        ControlType ct, string name, string aid, SnapshotOptions options)
    {
        var r = Safe(() => el.BoundingRectangle, System.Drawing.Rectangle.Empty);
        bool enabled = Safe(() => el.IsEnabled, false);
        bool focusable = Safe(() => el.Properties.IsKeyboardFocusable.ValueOrDefault, false);
        var state = new List<string>();
        if (enabled) state.Add("enabled");
        if (focusable) state.Add("focusable");

        var patterns = SupportedPatterns(el);
        var sb = new StringBuilder();
        sb.Append(indent).Append('[').Append(@ref).Append("] ").Append(ct).Append(' ')
          .Append('"').Append(name).Append('"')
          .Append(" @{").Append(r.X).Append(',').Append(r.Y).Append(',').Append(r.Width).Append(',').Append(r.Height).Append('}')
          .Append(" {").Append(string.Join(", ", state)).Append('}');
        if (patterns.Length > 0) sb.Append(" [").Append(string.Join(",", patterns)).Append(']');
        if (options.FullProperties)
            sb.Append(" aid=").Append(aid).Append(" help=\"").Append(Safe(() => el.HelpText, "")).Append('"');
        return sb.ToString();
    }

    private static string[] SupportedPatterns(AutomationElement el)
    {
        var p = el.Patterns;
        var checks = new (string Name, Func<bool> Supported)[]
        {
            ("Invoke", () => p.Invoke.IsSupported),
            ("Value", () => p.Value.IsSupported),
            ("Toggle", () => p.Toggle.IsSupported),
            ("ExpandCollapse", () => p.ExpandCollapse.IsSupported),
            ("Selection", () => p.Selection.IsSupported),
            ("SelectionItem", () => p.SelectionItem.IsSupported),
            ("ScrollItem", () => p.ScrollItem.IsSupported),
            ("Scroll", () => p.Scroll.IsSupported),
            ("Grid", () => p.Grid.IsSupported),
            ("Text", () => p.Text.IsSupported),
            ("Window", () => p.Window.IsSupported),
            ("Transform", () => p.Transform.IsSupported),
        };
        return checks.Where(c => Safe(c.Supported, false)).Select(c => c.Name).ToArray();
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); } catch { return fallback; }
    }
}
