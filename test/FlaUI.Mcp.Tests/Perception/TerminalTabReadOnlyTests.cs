using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

// Headless: the Destructive gate must short-circuit before any UIA work (null! deps prove it).
public class TerminalTabReadOnlyTests
{
    [Fact]
    public async Task Read_terminal_tab_is_blocked_in_read_only_mode()
    {
        var tools = new ContentTools(perception: null!, windows: null!,
            new ServerOptions(ReadOnly: true, AllowElevation: false));
        var json = await tools.DesktopReadTerminalTab("w1", tabIndex: 1);
        Assert.Contains("WriteBlockedReadOnly", json);
    }
}
