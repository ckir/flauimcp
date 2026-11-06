// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.Text;

namespace FlaUI.Mcp.Core.Tray;

/// <summary>Win32/WinForms menu label → the text an agent would read on screen (SPEC §2 step 7):
/// `&amp;&amp;` is a literal ampersand, a single `&amp;` marks the accelerator and is dropped, everything
/// from the first TAB is shortcut text, and the result is trimmed.</summary>
public static class MenuLabel
{
    public static string Clean(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        int tab = raw.IndexOf('\t');
        if (tab >= 0) raw = raw[..tab];
        var sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '&') { sb.Append(raw[i]); continue; }
            if (i + 1 < raw.Length && raw[i + 1] == '&') { sb.Append('&'); i++; }
            // a single '&' is the accelerator marker: dropped
        }
        return sb.ToString().Trim();
    }
}
