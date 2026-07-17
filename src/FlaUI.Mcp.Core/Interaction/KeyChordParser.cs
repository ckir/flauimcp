using System;
using System.Collections.Generic;
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Parses a `desktop_key` chord string into Win32 virtual-key codes. Grammar (spec §4):
/// `+`-delimited, zero-or-more modifiers (Ctrl|Alt|Shift|Win) followed by exactly one key from the
/// fixed table. Any unknown/empty token or a malformed shape throws InvalidArguments — never a silent
/// mis-key.</summary>
public readonly record struct ParsedChord(ushort[] ModifierVks, ushort KeyVk);

public static class KeyChordParser
{
    private static readonly Dictionary<string, ushort> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = 0x11, ["Alt"] = 0x12, ["Shift"] = 0x10, ["Win"] = 0x5B, // VK_LWIN
    };

    private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = 0x0D, ["Tab"] = 0x09, ["Esc"] = 0x1B, ["Backspace"] = 0x08, ["Delete"] = 0x2E,
        ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27, ["Space"] = 0x20,
    };

    private static ToolException Bad(string chord) => new(
        ToolErrorCode.InvalidArguments,
        $"Unrecognized key chord '{chord}'. Use modifiers Ctrl|Alt|Shift|Win + one key (letter/digit, Enter Tab Esc Backspace Delete Home End PageUp PageDown Up Down Left Right Space, or F1-F24).",
        "send a single valid chord, e.g. \"Ctrl+S\" or \"Enter\"");

    public static ParsedChord Parse(string? chord)
    {
        if (string.IsNullOrWhiteSpace(chord)) throw Bad(chord ?? "");
        var tokens = chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) throw Bad(chord);

        var mods = new List<ushort>();
        for (int i = 0; i < tokens.Length - 1; i++) // every token except the last must be a modifier
        {
            if (!Modifiers.TryGetValue(tokens[i], out var mvk)) throw Bad(chord);
            if (mods.Contains(mvk)) throw Bad(chord); // duplicate modifier
            mods.Add(mvk);
        }
        var key = ResolveKey(tokens[^1]) ?? throw Bad(chord);
        return new ParsedChord(mods.ToArray(), key);
    }

    /// <summary>Maps a list of modifier tokens (Ctrl|Alt|Shift|Win, case-insensitive) to Win32 VK codes,
    /// reusing the SAME <see cref="Modifiers"/> table <see cref="Parse"/> uses for chord modifiers — the
    /// single source of truth for the modifier vocabulary. Throws InvalidArguments on an unknown token or
    /// a duplicate. Null/empty input returns an empty array.</summary>
    public static ushort[] MapModifiers(IReadOnlyList<string>? tokens)
    {
        if (tokens is null || tokens.Count == 0) return Array.Empty<ushort>();
        var vks = new List<ushort>(tokens.Count);
        foreach (var token in tokens)
        {
            if (!Modifiers.TryGetValue(token, out var vk))
                throw new ToolException(ToolErrorCode.InvalidArguments,
                    $"Unrecognized modifier '{token}'.", "use Ctrl|Alt|Shift|Win (case-insensitive)");
            if (vks.Contains(vk))
                throw new ToolException(ToolErrorCode.InvalidArguments,
                    $"Duplicate modifier '{token}'.", "list each modifier once");
            vks.Add(vk);
        }
        return vks.ToArray();
    }

    private static ushort? ResolveKey(string token)
    {
        if (Modifiers.ContainsKey(token)) return null; // a bare modifier is not a key
        if (NamedKeys.TryGetValue(token, out var nk)) return nk;
        if (token.Length == 1)
        {
            char ch = char.ToUpperInvariant(token[0]);
            if (ch is >= 'A' and <= 'Z') return (ushort)ch;       // VK_A..VK_Z == 'A'..'Z'
            if (ch is >= '0' and <= '9') return (ushort)ch;       // VK_0..VK_9 == '0'..'9'
            return null;
        }
        if ((token[0] is 'F' or 'f') && int.TryParse(token.AsSpan(1), out var n) && n is >= 1 and <= 24)
            return (ushort)(0x70 + (n - 1));                       // VK_F1 == 0x70
        return null;
    }
}
