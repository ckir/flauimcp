using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class MouseClickInputTests
{
    private const ushort VK_CTRL = 0x11, VK_SHIFT = 0x10;

    [Fact]
    public void Holds_modifiers_down_before_the_click_and_releases_in_reverse_after()
    {
        var modifierVks = KeyChordParser.MapModifiers(new[] { "Ctrl", "Shift" });
        var inputs = MouseClickInput.Build(100, 200, Win32Interop.MOUSEEVENTF_LEFTDOWN, Win32Interop.MOUSEEVENTF_LEFTUP, 1, modifierVks);

        // 2 modifier-downs + move + down + up + 2 modifier-ups == 7
        Assert.Equal(7, inputs.Length);

        Assert.Equal(Win32Interop.INPUT_KEYBOARD, inputs[0].type);
        Assert.Equal(VK_CTRL, inputs[0].U.ki.wVk);
        Assert.Equal((uint)0, inputs[0].U.ki.dwFlags);

        Assert.Equal(Win32Interop.INPUT_KEYBOARD, inputs[1].type);
        Assert.Equal(VK_SHIFT, inputs[1].U.ki.wVk);
        Assert.Equal((uint)0, inputs[1].U.ki.dwFlags);

        // middle: move + left-down + left-up, all INPUT_MOUSE
        Assert.Equal(Win32Interop.INPUT_MOUSE, inputs[2].type);
        Assert.Equal(Win32Interop.INPUT_MOUSE, inputs[3].type);
        Assert.Equal(Win32Interop.INPUT_MOUSE, inputs[4].type);

        Assert.Equal(Win32Interop.INPUT_KEYBOARD, inputs[5].type);
        Assert.Equal(VK_SHIFT, inputs[5].U.ki.wVk);
        Assert.True((inputs[5].U.ki.dwFlags & Win32Interop.KEYEVENTF_KEYUP) != 0);

        Assert.Equal(Win32Interop.INPUT_KEYBOARD, inputs[6].type);
        Assert.Equal(VK_CTRL, inputs[6].U.ki.wVk);
        Assert.True((inputs[6].U.ki.dwFlags & Win32Interop.KEYEVENTF_KEYUP) != 0);
    }

    [Fact]
    public void Empty_modifiers_emits_zero_keyboard_inputs_mouse_only_sequence_unchanged()
    {
        var inputs = MouseClickInput.Build(100, 200, Win32Interop.MOUSEEVENTF_LEFTDOWN, Win32Interop.MOUSEEVENTF_LEFTUP, 1, System.Array.Empty<ushort>());

        Assert.Equal(3, inputs.Length); // move + down + up
        Assert.All(inputs, i => Assert.Equal(Win32Interop.INPUT_MOUSE, i.type));
    }

    [Fact]
    public void Count_two_holds_modifiers_across_both_down_up_pairs()
    {
        var modifierVks = KeyChordParser.MapModifiers(new[] { "Ctrl" });
        var inputs = MouseClickInput.Build(100, 200, Win32Interop.MOUSEEVENTF_LEFTDOWN, Win32Interop.MOUSEEVENTF_LEFTUP, 2, modifierVks);

        // 1 modifier-down + move + (down+up)*2 + 1 modifier-up == 7
        Assert.Equal(7, inputs.Length);

        Assert.Equal(Win32Interop.INPUT_KEYBOARD, inputs[0].type);
        Assert.Equal(VK_CTRL, inputs[0].U.ki.wVk);
        Assert.Equal((uint)0, inputs[0].U.ki.dwFlags);

        // indices 1..5 are the mouse move + two down/up pairs, all between the modifier-down and modifier-up
        for (int i = 1; i <= 5; i++) Assert.Equal(Win32Interop.INPUT_MOUSE, inputs[i].type);

        Assert.Equal(Win32Interop.INPUT_KEYBOARD, inputs[6].type);
        Assert.Equal(VK_CTRL, inputs[6].U.ki.wVk);
        Assert.True((inputs[6].U.ki.dwFlags & Win32Interop.KEYEVENTF_KEYUP) != 0);
    }
}
