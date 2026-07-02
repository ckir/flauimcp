// test/FlaUI.Mcp.Tests/Perception/FindQuerySpecTests.cs
using FlaUI.Core.Definitions;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class FindQuerySpecTests
{
    [Fact]
    public void TryParseControlType_parses_known_type_case_insensitively()
    {
        Assert.True(FindQuerySpec.TryParseControlType("button", out var ct));
        Assert.Equal(ControlType.Button, ct);
    }

    [Fact]
    public void TryParseControlType_rejects_unknown_type()
        => Assert.False(FindQuerySpec.TryParseControlType("NotAType", out _));

    [Fact]
    public void TryParseControlType_null_means_no_constraint_and_returns_false()
        => Assert.False(FindQuerySpec.TryParseControlType(null, out _));

    [Fact]
    public void Matches_name_eq_is_ordinal_exact()
    {
        var spec = new FindQuerySpec(new FindQuery(AutomationId: null, Name: "OK", NameMatch: "eq",
            ControlType: null, EnabledOnly: false));
        Assert.True(spec.MatchesPostFilter(name: "OK", enabled: true));
        Assert.False(spec.MatchesPostFilter(name: "OK ", enabled: true)); // exact, no trim
        Assert.False(spec.MatchesPostFilter(name: "ok", enabled: true));  // ordinal, case-sensitive
    }

    [Fact]
    public void Matches_name_contains_is_ordinal_substring()
    {
        var spec = new FindQuerySpec(new FindQuery(null, "Item", "contains", null, false));
        Assert.True(spec.MatchesPostFilter(name: "ListItem 3", enabled: true));
        Assert.False(spec.MatchesPostFilter(name: "widget", enabled: true));
    }

    [Fact]
    public void Matches_enabledOnly_filters_disabled()
    {
        var spec = new FindQuerySpec(new FindQuery(null, null, "eq", null, EnabledOnly: true));
        Assert.True(spec.MatchesPostFilter(name: "x", enabled: true));
        Assert.False(spec.MatchesPostFilter(name: "x", enabled: false));
    }

    [Fact]
    public void Matches_no_name_constraint_passes_any_name()
    {
        var spec = new FindQuerySpec(new FindQuery(AutomationId: "aid1", Name: null, NameMatch: "eq",
            ControlType: null, EnabledOnly: false));
        Assert.True(spec.MatchesPostFilter(name: "anything", enabled: true));
    }
}
