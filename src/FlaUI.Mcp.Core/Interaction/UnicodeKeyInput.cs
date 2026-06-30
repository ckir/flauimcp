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

    private static INPUT Unit(char ch, uint flags) => new()
    {
        type = Win32Interop.INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = flags, time = 0, dwExtraInfo = 0 } }
    };
}
