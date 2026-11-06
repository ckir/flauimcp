// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.Globalization;
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Perception;

namespace FlaUI.Mcp.Core.Tray;

/// <summary>One tray icon as the agent sees it. Tooltip is ALREADY redacted (SPEC §3.4) — Redacted says so.
/// Location: "visible" (NotifyItemIcon under Shell_TrayWnd), "system" (SystemTrayIcon under Shell_TrayWnd,
/// chevron excluded) or "hidden" (NotifyItemIcon in the overflow). RuntimeId re-finds the element on another
/// STA; the centre is in physical pixels (UIA BoundingRectangle).</summary>
public sealed record TrayIconInfo(string Tooltip, bool Redacted, string Location, string RuntimeId,
    int CentreX, int CentreY, bool IsOffscreen, nint HostHwnd);

/// <summary>UIA reads of the tray. Runs on whichever STA the caller is on. Names go through the same
/// egress accessor a snapshot uses, evaluated against the icons' REAL owner, explorer (SPEC §3.4).</summary>
public static class TrayIconReader
{
    public const string ExplorerProcess = "explorer";
    public const string NotifyItemAid = "NotifyItemIcon";
    public const string SystemItemAid = "SystemTrayIcon";
    public const string ChevronClass = "SystemTray.NormalButton";

    /// <summary>SPEC "Finding the chevron": the FIRST element in tree order with aid=SystemTrayIcon and
    /// class SystemTray.NormalButton. Null when there is no candidate (nothing hidden).</summary>
    public static string? FindChevronRuntimeId(AutomationElement tray)
    {
        foreach (var el in tray.FindAllDescendants(cf => cf.ByAutomationId(SystemItemAid)))
        {
            string? cls = null;
            try { cls = el.Properties.ClassName.ValueOrDefault; } catch { }
            if (cls == ChevronClass) return RidOf(el);
        }
        return null;
    }

    public static List<TrayIconInfo> ReadTaskbar(AutomationElement tray, nint trayHwnd, SensitivityClassifier classifier, string? chevronRid)
    {
        var list = new List<TrayIconInfo>();
        foreach (var el in tray.FindAllDescendants(cf => cf.ByAutomationId(NotifyItemAid)))
            list.Add(Describe(el, "visible", trayHwnd, classifier));
        foreach (var el in tray.FindAllDescendants(cf => cf.ByAutomationId(SystemItemAid)))
        {
            var info = Describe(el, "system", trayHwnd, classifier);
            if (chevronRid is not null && info.RuntimeId == chevronRid) continue; // the chevron is not an icon
            list.Add(info);
        }
        return list;
    }

    public static List<TrayIconInfo> ReadOverflow(AutomationElement overflow, nint overflowHwnd, SensitivityClassifier classifier)
    {
        var list = new List<TrayIconInfo>();
        foreach (var el in overflow.FindAllDescendants(cf => cf.ByAutomationId(NotifyItemAid)))
            list.Add(Describe(el, "hidden", overflowHwnd, classifier));
        return list;
    }

    public static AutomationElement? FindByRuntimeId(AutomationElement root, string rid)
    {
        foreach (var el in root.FindAllDescendants(cf => cf.ByAutomationId(NotifyItemAid).Or(cf.ByAutomationId(SystemItemAid))))
            if (RidOf(el) == rid) return el;
        return null;
    }

    internal static string RidOf(AutomationElement el)
    {
        try
        {
            var raw = el.Properties.RuntimeId.ValueOrDefault;
            return raw is null ? string.Empty : string.Join(".", raw.Select(i => i.ToString(CultureInfo.InvariantCulture)));
        }
        catch { return string.Empty; }
    }

    private static TrayIconInfo Describe(AutomationElement el, string location, nint host, SensitivityClassifier classifier)
    {
        var read = ElementContent.Name(el, classifier, ExplorerProcess); // the egress accessor: [REDACTED] when a rule says so
        int cx = 0, cy = 0; bool off = true;
        try { var r = el.BoundingRectangle; cx = r.Left + r.Width / 2; cy = r.Top + r.Height / 2; off = r.Width <= 0 || r.Height <= 0; } catch { }
        try { off = off || el.Properties.IsOffscreen.ValueOrDefault; } catch { }
        return new TrayIconInfo(read.Text, read.Sensitivity.Redact, location, RidOf(el), cx, cy, off, host);
    }
}
