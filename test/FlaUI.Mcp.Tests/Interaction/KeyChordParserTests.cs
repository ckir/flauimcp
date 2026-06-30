using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class KeyChordParserTests
{
    [Fact]
    public void Parses_a_single_letter()
    {
        var c = KeyChordParser.Parse("a");
        Assert.Empty(c.ModifierVks);
        Assert.Equal((ushort)0x41, c.KeyVk); // VK_A
    }

    [Fact]
    public void Parses_modifiers_then_key_case_insensitively()
    {
        var c = KeyChordParser.Parse("Ctrl+Shift+S");
        Assert.Equal(new ushort[] { 0x11, 0x10 }, c.ModifierVks); // VK_CONTROL, VK_SHIFT
        Assert.Equal((ushort)0x53, c.KeyVk); // VK_S
    }

    [Theory]
    [InlineData("Enter", (ushort)0x0D)]
    [InlineData("Tab", (ushort)0x09)]
    [InlineData("Esc", (ushort)0x1B)]
    [InlineData("F5", (ushort)0x74)]
    [InlineData("PageDown", (ushort)0x22)]
    [InlineData("Left", (ushort)0x25)]
    public void Parses_named_keys(string token, ushort expectedVk)
        => Assert.Equal(expectedVk, KeyChordParser.Parse(token).KeyVk);

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl")]              // modifier with no key
    [InlineData("Ctrl+Alt")]         // two modifiers, no key
    [InlineData("Nope")]             // unknown key token
    [InlineData("Ctrl+Ctrl+A")]      // duplicate modifier as key slot
    [InlineData("A+B")]              // two keys
    public void Rejects_bad_grammar_with_InvalidArguments(string chord)
    {
        var ex = Assert.Throws<ToolException>(() => KeyChordParser.Parse(chord));
        Assert.Equal(ToolErrorCode.InvalidArguments, ex.Code);
    }
}
