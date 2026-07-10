using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Windows;

// Headless: pure string decision, no UIA, no WindowManager instance (Category!=Desktop).
public class MultiplexerHintTests
{
    [Theory]
    [InlineData("WindowsTerminal")]
    [InlineData("WindowsTerminalPreview")]
    public void For_recognized_multiplexer_returns_a_hint_mentioning_active_tab_only(string proc)
    {
        var hint = MultiplexerHint.For(proc);
        Assert.NotNull(hint);
        Assert.Contains("active tab", hint!);   // the load-bearing nugget: this is ONLY the active tab
    }

    [Theory]
    [InlineData("notepad")]
    [InlineData("explorer")]
    [InlineData("")]
    [InlineData("windowsterminal")]  // case-sensitive: .NET ProcessName is "WindowsTerminal" exactly
    public void For_other_processes_returns_null(string proc)
        => Assert.Null(MultiplexerHint.For(proc));
}
