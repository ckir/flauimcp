// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Mcp.Core.Perception;

namespace FlaUI.Mcp.Core.Tray;

/// <summary>A K1 item: cleaned text plus the facts §2 step 7 needs; RuntimeId re-finds it for Invoke.</summary>
public sealed record UiaMenuItem(string Text, bool Enabled, bool HasSubmenu, string RuntimeId);

/// <summary>The two UIA facts the classifier needs about a candidate window, plus the K1 item list.</summary>
public static class UiaMenuReader
{
    public static bool RootIsMenu(AutomationElement root)
    {
        try { return root.ControlType == ControlType.Menu; } catch { return false; }
    }

    private static AutomationElement[] MenuItems(AutomationElement root)
    {
        try { return root.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem)); } catch { return Array.Empty<AutomationElement>(); }
    }

    /// <summary>True when at least one MenuItem supports Invoke or ExpandCollapse (M6 ShareX: both; M7
    /// OneDrive: neither — Value only — so a titled OneDrive window is also excluded by the title rule).</summary>
    public static bool HasActionableMenuItems(AutomationElement root)
        => MenuItems(root).Any(mi => Safe(() => mi.Patterns.Invoke.IsSupported) || Safe(() => mi.Patterns.ExpandCollapse.IsSupported));

    public static List<UiaMenuItem> Read(AutomationElement root, SensitivityClassifier classifier, string? processName)
    {
        var list = new List<UiaMenuItem>();
        foreach (var mi in MenuItems(root))
        {
            bool invoke = Safe(() => mi.Patterns.Invoke.IsSupported);
            bool expand = Safe(() => mi.Patterns.ExpandCollapse.IsSupported);
            if (!invoke && !expand) continue; // a separator or a non-actionable row
            var read = ElementContent.Name(mi, classifier, processName);
            bool enabled = Safe(() => mi.Properties.IsEnabled.ValueOrDefault);
            bool submenu = expand && (Safe(() => mi.FindAllChildren().Length > 0)
                                      || Safe(() => mi.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value != ExpandCollapseState.LeafNode));
            list.Add(new UiaMenuItem(MenuLabel.Clean(read.Text), enabled, submenu, TrayIconReader.RidOf(mi)));
        }
        return list;
    }

    public static AutomationElement? FindItem(AutomationElement root, string rid)
        => MenuItems(root).FirstOrDefault(mi => TrayIconReader.RidOf(mi) == rid);

    private static bool Safe(Func<bool> f) { try { return f(); } catch { return false; } }
}
