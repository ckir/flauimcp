using System;
using System.Collections.Generic;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Builds the SendInput INPUT[] for a synthetic mouse click, including any modifier keys held
/// for its duration (e.g. Ctrl+click for additive-select). Mirrors KeyChord's press-hold-release pattern:
/// modifier key-DOWNs first (in the given order), then the mouse move + down/up sequence UNCHANGED from
/// the pre-modifiers sink, then modifier key-UPs in REVERSE order. Pure (no SendInput call) ->
/// headless-testable, following <see cref="UnicodeKeyInput"/>'s extract-a-pure-builder style. With an
/// empty modifier array this emits ZERO keyboard INPUTs -> byte-identical to the mouse-only sequence.</summary>
public static class MouseClickInput
{
    public static INPUT[] Build(int ax, int ay, uint downFlag, uint upFlag, int count, ushort[] modifierVks)
    {
        var inputs = new List<INPUT>();
        foreach (var vk in modifierVks) inputs.Add(Vk(vk, 0));
        inputs.Add(Move(ax, ay));
        for (int i = 0; i < Math.Clamp(count, 1, 2); i++) { inputs.Add(Mouse(ax, ay, downFlag)); inputs.Add(Mouse(ax, ay, upFlag)); }
        for (int i = modifierVks.Length - 1; i >= 0; i--) inputs.Add(Vk(modifierVks[i], Win32Interop.KEYEVENTF_KEYUP));
        return inputs.ToArray();
    }

    private static INPUT Vk(ushort vk, uint flags) => new()
    { type = Win32Interop.INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = flags, time = 0, dwExtraInfo = 0 } } };

    private static INPUT Move(int ax, int ay) => Mouse(ax, ay, Win32Interop.MOUSEEVENTF_MOVE);

    private static INPUT Mouse(int ax, int ay, uint action) => new()
    {
        type = Win32Interop.INPUT_MOUSE,
        U = new InputUnion { mi = new MOUSEINPUT { dx = ax, dy = ay, mouseData = 0,
            dwFlags = action | Win32Interop.MOUSEEVENTF_ABSOLUTE | Win32Interop.MOUSEEVENTF_VIRTUALDESK, time = 0, dwExtraInfo = 0 } }
    };
}
