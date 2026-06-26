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
        var model = Build(root, popupRoots, options, refs, windowId);
        return (Render(model, options), model.NodeCount);
    }

    public static SnapshotModel Build(
        AutomationElement root, IReadOnlyList<AutomationElement> popupRoots,
        SnapshotOptions options, RefRegistry refs, string windowId)
    {
        var items = new List<SnapshotItem>();
        var popupRids = new List<int[]>();
        foreach (var p in popupRoots)
        {
            var prid = Safe(() => p.Properties.RuntimeId.ValueOrDefault, (int[]?)null);
            if (prid != null) popupRids.Add(prid);
        }
        var rootBounds = Safe(() => root.BoundingRectangle, System.Drawing.Rectangle.Empty);
        Visit(root, 0, Array.Empty<int>(), null, "", rootBounds);
        if (popupRoots.Count > 0)
        {
            items.Add(new OverlaysHeaderItem());
            for (int i = 0; i < popupRoots.Count; i++)
            {
                var pb = Safe(() => popupRoots[i].BoundingRectangle, System.Drawing.Rectangle.Empty);
                Visit(popupRoots[i], 0, new[] { -1 - i }, null, "  ", pb);
            }
        }
        return new SnapshotModel(items);

        void Visit(AutomationElement el, int depth, int[] indexPath, string? ancestorAid, string indent,
            System.Drawing.Rectangle cullBounds)
        {
            int[] rid = Safe(() => el.Properties.RuntimeId.ValueOrDefault, (int[]?)null) ?? Array.Empty<int>();
            if (depth > 0)
                foreach (var prid in popupRids)
                    if (RidEqual(rid, prid)) return;
            if (depth > 0 && !options.IncludeOffscreen && Safe(() => el.Properties.IsOffscreen.ValueOrDefault, false)) return;
            if (depth > 0 && !options.IncludeOffscreen && cullBounds.Width > 0 && cullBounds.Height > 0)
            {
                var rect0 = Safe(() => el.BoundingRectangle, System.Drawing.Rectangle.Empty);
                if (rect0.Width <= 0 || rect0.Height <= 0 || !rect0.IntersectsWith(cullBounds)) return;
            }
            string aid = Safe(() => el.AutomationId, "");
            ControlType ct = Safe(() => el.ControlType, ControlType.Custom);
            string name = Safe(() => el.Name, "");
            bool include = depth == 0 || !options.InteractiveOnly || IsInteresting(el, ct, name);
            string childIndent = indent;
            if (include)
            {
                var descriptor = new ElementDescriptor(rid, ct, aid, name, ancestorAid, indexPath);
                var @ref = refs.Register(windowId, descriptor, el);
                var rect = Safe(() => el.BoundingRectangle, System.Drawing.Rectangle.Empty);
                bool enabled = Safe(() => el.IsEnabled, false);
                bool focusable = Safe(() => el.Properties.IsKeyboardFocusable.ValueOrDefault, false);
                bool isPassword = Safe(() => el.Properties.IsPassword.ValueOrDefault, false);
                bool offscreen = Safe(() => el.Properties.IsOffscreen.ValueOrDefault, false);
                var patterns = SupportedPatterns(el);
                string help = Safe(() => el.HelpText, "");
                items.Add(new SnapshotNode(@ref, depth, indent, ct, aid, name, rect, enabled, focusable,
                    false, isPassword, offscreen, rid, patterns, help));
                childIndent = indent + "  ";
            }
            var nextAncestor = string.IsNullOrEmpty(aid) ? ancestorAid : aid;
            AutomationElement[] children = Safe(() => el.FindAllChildren(), Array.Empty<AutomationElement>());
            if (depth >= options.MaxDepth)
            {
                if (children.Length > 0) items.Add(new DepthLimitItem(childIndent, children.Length, options.MaxDepth));
                return;
            }
            for (int i = 0; i < children.Length; i++)
            {
                var nextPath = new int[indexPath.Length + 1];
                Array.Copy(indexPath, nextPath, indexPath.Length);
                nextPath[^1] = i;
                Visit(children[i], depth + 1, nextPath, nextAncestor, childIndent, cullBounds);
            }
        }
    }

    public static string Render(SnapshotModel model, SnapshotOptions options)
    {
        var sb = new StringBuilder();
        foreach (var item in model.Items)
            switch (item)
            {
                case SnapshotNode n: sb.AppendLine(FormatNode(n, options)); break;
                case OverlaysHeaderItem: sb.AppendLine("[Active Overlays]"); break;
                case DepthLimitItem d:
                    sb.Append(d.Indent).Append("… ").Append(d.MoreCount)
                      .Append(" more (depth limit ").Append(d.MaxDepth).AppendLine(")");
                    break;
            }
        return sb.ToString();
    }

    private static string FormatNode(SnapshotNode n, SnapshotOptions options)
    {
        var state = new List<string>();
        if (n.Enabled) state.Add("enabled");
        if (n.Focusable) state.Add("focusable");
        if (n.Focused) state.Add("focused");
        string shownName = n.IsPassword ? "[REDACTED]" : n.Name;
        var sb = new StringBuilder();
        sb.Append(n.Indent).Append('[').Append(n.Ref).Append("] ").Append(n.ControlType).Append(' ')
          .Append('"').Append(shownName).Append('"')
          .Append(" @{").Append(n.Bounds.X).Append(',').Append(n.Bounds.Y).Append(',')
          .Append(n.Bounds.Width).Append(',').Append(n.Bounds.Height).Append('}')
          .Append(" {").Append(string.Join(", ", state)).Append('}');
        if (n.Patterns.Count > 0) sb.Append(" [").Append(string.Join(",", n.Patterns)).Append(']');
        if (options.FullProperties)
            sb.Append(" aid=").Append(n.AutomationId).Append(" help=\"").Append(n.HelpText).Append('"');
        return sb.ToString();
    }

    private static bool IsInteresting(AutomationElement el, ControlType ct, string name)
    {
        if (InteractiveTypes.Contains(ct)) return true;
        if (ct == ControlType.Text && !string.IsNullOrWhiteSpace(name)) return true; // named labels inform
        if (Safe(() => el.Properties.IsKeyboardFocusable.ValueOrDefault, false)) return true;
        // any actionable pattern makes it interesting
        return SupportedPatterns(el).Length > 0;
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

    // Zero-allocation RuntimeId equality (UIA RuntimeIds are small int[]).
    private static bool RidEqual(int[] a, int[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); } catch { return fallback; }
    }
}
