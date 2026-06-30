using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class ActionPolicyFailClosedTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", null)]
    [InlineData(null, "   ")]
    [InlineData(null, "DirectUIHWND")]      // BLOCKER (agy): proc unresolvable (elevated/protected) but
    [InlineData("", "Chrome_WidgetWin_1")]  // class IS resolvable -> must NOT fall through to Allowed
    public void Unidentifiable_target_is_denied_not_allowed(string? proc, string? cls)
        => Assert.Equal(ActionVerdict.Denied, ActionPolicy.Classify(proc, cls));

    [Fact]
    public void A_named_benign_target_is_still_allowed()
        => Assert.Equal(ActionVerdict.Allowed, ActionPolicy.Classify("notepad", "Notepad"));
}
