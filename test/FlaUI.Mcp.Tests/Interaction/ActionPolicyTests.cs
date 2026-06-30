using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class ActionPolicyTests
{
    [Theory]
    [InlineData("consent", null)]
    [InlineData("1password", null)]
    public void Credential_and_uac_targets_are_denied(string proc, string? cls)
        => Assert.Equal(ActionVerdict.Denied, ActionPolicy.Classify(proc, cls));

    [Theory]
    [InlineData("WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS")]
    [InlineData("cmd", "ConsoleWindowClass")]
    public void Shells_are_interlocked(string proc, string cls)
        => Assert.Equal(ActionVerdict.Interlocked, ActionPolicy.Classify(proc, cls));

    [Fact]
    public void Ordinary_app_is_allowed()
        => Assert.Equal(ActionVerdict.Allowed, ActionPolicy.Classify("notepad", "Notepad"));
}
