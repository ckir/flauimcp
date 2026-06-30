using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class UnicodeKeyInputTests
{
    [Fact]
    public void Emits_a_keydown_and_keyup_per_bmp_char()
    {
        var inputs = UnicodeKeyInput.Build("ab");
        Assert.Equal(4, inputs.Length); // 2 chars * (down+up)
    }

    [Fact]
    public void Emits_surrogate_pairs_as_separate_units()
    {
        // U+1F600 GRINNING FACE is a surrogate pair -> 2 UTF-16 units -> 4 INPUTs.
        var inputs = UnicodeKeyInput.Build("\U0001F600");
        Assert.Equal(4, inputs.Length);
    }

    [Fact]
    public void Each_unit_carries_KEYEVENTF_UNICODE_and_the_scan_code_is_the_utf16_unit()
    {
        var inputs = UnicodeKeyInput.Build("A");
        Assert.Equal((uint)1, inputs[0].type);               // INPUT_KEYBOARD
        Assert.Equal((ushort)'A', inputs[0].U.ki.wScan);
        Assert.Equal(Win32Interop.KEYEVENTF_UNICODE, inputs[0].U.ki.dwFlags);
        Assert.Equal(Win32Interop.KEYEVENTF_UNICODE | Win32Interop.KEYEVENTF_KEYUP, inputs[1].U.ki.dwFlags);
    }
}
