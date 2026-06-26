using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

// Pure predicate — no UIA / desktop session, so it runs in the headless CI subset.
public class PerceptionPolicyTests
{
    [Theory]
    [InlineData("1Password")]   // case-insensitive
    [InlineData("keepassxc")]
    [InlineData("bitwarden")]
    [InlineData(" lastpass ")]  // trimmed
    public void Denied_credential_stores_are_blocked(string processName)
        => Assert.True(PerceptionPolicy.IsDenied(processName));

    [Theory]
    [InlineData("notepad")]
    [InlineData("chrome")]
    [InlineData("FlaUI.Mcp.TestApp")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ordinary_and_empty_processes_are_allowed(string? processName)
        => Assert.False(PerceptionPolicy.IsDenied(processName));
}
