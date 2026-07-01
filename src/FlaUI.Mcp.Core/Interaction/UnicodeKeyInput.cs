using System.Collections.Generic;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Builds the keyboard INPUT[] for a unicode string via KEYEVENTF_UNICODE (wVk=0, wScan=UTF-16
/// unit). Each UTF-16 code unit emits a down + up; a non-BMP char is naturally two units (surrogate
/// pair) -> four INPUTs, exactly as Windows expects. Pure (no SendInput) -> headless-testable.</summary>
public static class UnicodeKeyInput
{
    public static INPUT[] Build(string text)
    {
        var list = new List<INPUT>((text?.Length ?? 0) * 2);
        foreach (char ch in text ?? string.Empty)
        {
            list.Add(Unit(ch, Win32Interop.KEYEVENTF_UNICODE));
            list.Add(Unit(ch, Win32Interop.KEYEVENTF_UNICODE | Win32Interop.KEYEVENTF_KEYUP));
        }
        return list.ToArray();
    }

    /// <summary>Splits the text into per-CHARACTER INPUT groups so the caller can pace keystrokes with an
    /// inter-key delay (4b remediation) without splitting a surrogate pair across a pause. A BMP char yields
    /// one 2-INPUT group (down+up); a non-BMP char (surrogate pair) yields one 4-INPUT group. Concatenating
    /// every group reproduces <see cref="Build"/> byte-for-byte, so pacing never changes the OS wire sequence.</summary>
    public static IEnumerable<INPUT[]> Groups(string text)
    {
        var s = text ?? string.Empty;
        int i = 0;
        while (i < s.Length)
        {
            int units = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
            var group = new INPUT[units * 2];
            for (int k = 0; k < units; k++)
            {
                group[2 * k] = Unit(s[i + k], Win32Interop.KEYEVENTF_UNICODE);
                group[2 * k + 1] = Unit(s[i + k], Win32Interop.KEYEVENTF_UNICODE | Win32Interop.KEYEVENTF_KEYUP);
            }
            yield return group;
            i += units;
        }
    }

    private static INPUT Unit(char ch, uint flags) => new()
    {
        type = Win32Interop.INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = flags, time = 0, dwExtraInfo = 0 } }
    };
}
