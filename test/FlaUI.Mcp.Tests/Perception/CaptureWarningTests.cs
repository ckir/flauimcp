using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class CaptureWarningTests
{
    // The wire contract. These strings are read by every consuming agent, so they are pinned here
    // rather than left to drift. camelCase, matching every other field in the response.
    [Theory]
    [InlineData("uniformCanvas")]
    [InlineData("desktopCanvasUniform")]
    [InlineData("elementCanvasUniform")]
    [InlineData("popupsNotRendered")]
    [InlineData("scrapeFallbackTargetUnresponsive")]
    [InlineData("scrapeFallbackTargetChanging")]
    public void Every_shipped_code_has_a_recourse(string code)
    {
        var w = CaptureWarnings.For(code);
        Assert.Equal(code, w.Code);
        Assert.False(string.IsNullOrWhiteSpace(w.Recourse));
    }

    // ⚠ SIX. `windowResized` was retired at panel round 11 when the path that emitted it was found to
    // be a leak and removed. A code that cannot fire teaches a consumer that its condition never happens.
    [Fact]
    public void Exactly_six_codes_ship()
        => Assert.Equal(6, CaptureWarnings.AllCodes.Count);

    // §5: `code` is what a caller branches on, so it must be a stable identifier -- never prose.
    [Fact]
    public void Codes_are_camelCase_identifiers_not_sentences()
        => Assert.All(CaptureWarnings.AllCodes, c =>
        {
            Assert.DoesNotContain(' ', c);
            Assert.True(char.IsLower(c[0]), $"'{c}' must start lowercase");
        });

    [Fact]
    public void An_unknown_code_throws_rather_than_inventing_a_recourse()
        => Assert.Throws<System.ArgumentOutOfRangeException>(() => CaptureWarnings.For("notACode"));
}
