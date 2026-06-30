using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class PerceptionManagerShouldFixTests
{
    [Fact]
    public void IsPassword_read_that_throws_is_treated_as_password_fail_closed()
    {
        Assert.True(RedactionPolicy.IsPasswordOrFailClosed(() => throw new System.Runtime.InteropServices.COMException()));
        Assert.True(RedactionPolicy.IsPasswordOrFailClosed(() => true));
        Assert.False(RedactionPolicy.IsPasswordOrFailClosed(() => false));
    }
}
