using System.Linq;
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

    // Every shipped code must have at least one emission site in production. This is the other half of
    // the retirement: it is what would have caught `windowResized` going dead on its own.
    [Fact]
    public void No_shipped_code_is_unreachable()
    {
        var root = RepoRoot();
        var src = string.Join("\n", System.IO.Directory.EnumerateFiles(
            System.IO.Path.Combine(root, "src"), "*.cs", System.IO.SearchOption.AllDirectories)
            .Select(f => StripComments(System.IO.File.ReadAllText(f))));
        foreach (var c in CaptureWarnings.AllCodes)
        {
            var member = char.ToUpperInvariant(c[0]) + c.Substring(1);
            Assert.True(src.Contains("CaptureWarnings." + member),
                $"'{c}' is documented but never emitted - retire it or emit it");
        }
    }

    /// <summary>⚠ COMMENTS STRIPPED FIRST. Without it, a code named only in a comment — and this plan's
    /// comments name these codes constantly, to explain the paths that emit them — satisfies the gate
    /// while nothing emits it. That is the same defect item 12 shipped and the same one the metadata
    /// sweep had, arriving a third time in the test written to prevent a code going dead.
    /// MEASURED: the substring check matches a commented line.</summary>
    private static string StripComments(string source)
    {
        var noBlocks = System.Text.RegularExpressions.Regex.Replace(
            source, @"/\*.*?\*/", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline);
        return string.Join("\n", noBlocks.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", System.StringComparison.Ordinal)));
    }

    private static string RepoRoot()
    {
        var d = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
        while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "FlaUI.Mcp.slnx")))
            d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
}
