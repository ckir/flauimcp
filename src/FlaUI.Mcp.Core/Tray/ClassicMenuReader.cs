// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.Text;

namespace FlaUI.Mcp.Core.Tray;

/// <summary>One HMENU entry as Win32 reports it, before cleaning.</summary>
public readonly record struct RawMenuEntry(int Position, string RawText, uint State);

/// <summary>A readable K3 item. RawPosition indexes the HMENU INCLUDING separators, because
/// GetMenuItemRect/GetMenuState index by raw position (SPEC §2 step 7).</summary>
public sealed record ClassicMenuItem(int RawPosition, string Text, bool Enabled, bool HasSubmenu);

/// <summary>Reads a `#32768` menu window through Win32 (UIA sees nothing, M13).</summary>
public static class ClassicMenuReader
{
    /// <summary>The HMENU behind a `#32768` window, or 0 when it has none (then it is not K3).</summary>
    public static nint HmenuOf(nint menuHwnd) => TrayWin32.SendMessageBounded(menuHwnd, TrayWin32.MN_GETHMENU, 0, 0);

    public static List<RawMenuEntry> ReadRaw(nint hmenu)
    {
        var list = new List<RawMenuEntry>();
        int n = TrayWin32.GetMenuItemCount(hmenu);
        for (int i = 0; i < n; i++)
        {
            var sb = new StringBuilder(256);
            TrayWin32.GetMenuStringW(hmenu, (uint)i, sb, sb.Capacity, TrayWin32.MF_BYPOSITION);
            uint state = TrayWin32.GetMenuState(hmenu, (uint)i, TrayWin32.MF_BYPOSITION);
            list.Add(new RawMenuEntry(i, sb.ToString(), state));
        }
        return list;
    }

    /// <summary>Pure: clean labels, drop separators, keep raw positions.</summary>
    public static List<ClassicMenuItem> FromRaw(IEnumerable<RawMenuEntry> raw)
    {
        var items = new List<ClassicMenuItem>();
        foreach (var e in raw)
        {
            if ((e.State & TrayWin32.MF_POPUP) == 0 && (e.State & TrayWin32.MF_SEPARATOR) != 0) continue;
            bool enabled = (e.State & (TrayWin32.MF_GRAYED | TrayWin32.MF_DISABLED)) == 0;
            bool submenu = (e.State & TrayWin32.MF_POPUP) != 0;
            items.Add(new ClassicMenuItem(e.Position, MenuLabel.Clean(e.RawText), enabled, submenu));
        }
        return items;
    }

    public static List<ClassicMenuItem> Read(nint hmenu) => FromRaw(ReadRaw(hmenu));

    /// <summary>Screen rect of a raw position, via GetMenuItemRect with a NULL window handle — MEASURED to
    /// work for a tray `#32768` menu (M17; Q2 covers 125 %). Null when Win32 refuses.</summary>
    public static (int X, int Y)? CentreOf(nint hmenu, int rawPosition)
    {
        if (!TrayWin32.GetMenuItemRect(0, hmenu, (uint)rawPosition, out var r)) return null;
        if (r.Right <= r.Left || r.Bottom <= r.Top) return null;
        return ((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
    }
}
