// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Core.Tray;

/// <summary>What a right-click produced (SPEC "Three kinds of right-click result"). On the wire
/// ClassicMenu and UiaMenu are both `menuKind: "menu"`; AppWindow is `menuKind: "window"`.</summary>
public enum MenuKind { None, ClassicMenu, UiaMenu, AppWindow }

/// <summary>Everything the classifier needs about one new top-level window, read by the orchestrator
/// (Win32 for class/title/pid/foreground/hmenu; UIA for the two menu facts, on the action STA).</summary>
public sealed record CandidateFacts(
    nint Hwnd, string ClassName, bool HasTitle, int Pid, bool IsForeground,
    bool HasHmenu, bool UiaRootIsMenu, bool HasActionableMenuItems);

/// <summary>One poll's view of a candidate: its kind and how many items it currently exposes.</summary>
public readonly record struct ClassifiedWindow(nint Hwnd, MenuKind Kind, int ItemCount);

/// <summary>SPEC §2 steps 4–5, pure.</summary>
public static class TrayMenuClassifier
{
    public const string ClassicMenuClass = "#32768";
    public const string OverflowClass = "TopLevelWindowForOverflowXamlIsland";
    public const string TaskbarClass = "Shell_TrayWnd";

    /// <summary>Windows the poll never considers: explorer's tooltip/popup host, menu shadows, the
    /// overflow itself, and the intent overlay (`WindowManager.ShouldSkipTopLevel`).</summary>
    public static bool IsIgnoredClass(string className)
        => className is "Xaml_WindowedPopupClass" or "SysShadow" or OverflowClass
           || WindowManager.ShouldSkipTopLevel(className);

    /// <summary>K3 > K1 > K2, each gated by the facts the spec names. Only a window of the foreground
    /// process is a candidate at all (F6).</summary>
    public static MenuKind Classify(CandidateFacts f, int foregroundPid)
    {
        if (f.Pid != foregroundPid) return MenuKind.None;
        if (f.ClassName == ClassicMenuClass && f.HasHmenu) return MenuKind.ClassicMenu;
        if (!f.HasTitle && (f.UiaRootIsMenu || f.HasActionableMenuItems)) return MenuKind.UiaMenu;
        if (f.HasTitle && f.IsForeground) return MenuKind.AppWindow;
        return MenuKind.None;
    }

    private static int Rank(MenuKind k) => k switch
    {
        MenuKind.ClassicMenu => 3, MenuKind.UiaMenu => 2, MenuKind.AppWindow => 1, _ => 0,
    };

    public static ClassifiedWindow? Strongest(IEnumerable<ClassifiedWindow> candidates)
    {
        ClassifiedWindow? best = null;
        foreach (var c in candidates)
            if (c.Kind != MenuKind.None && (best is null || Rank(c.Kind) > Rank(best.Value.Kind))) best = c;
        return best;
    }

    /// <summary>F1: stable = the previous poll exists, both are non-empty, and they hold the same
    /// (hwnd, kind, itemCount) set.</summary>
    public static bool IsStable(IReadOnlyList<ClassifiedWindow>? previous, IReadOnlyList<ClassifiedWindow> current)
    {
        if (previous is null || previous.Count == 0 || current.Count == 0 || previous.Count != current.Count) return false;
        var prev = previous.OrderBy(w => w.Hwnd).ToArray();
        var cur = current.OrderBy(w => w.Hwnd).ToArray();
        for (int i = 0; i < prev.Length; i++) if (prev[i] != cur[i]) return false;
        return true;
    }
}
