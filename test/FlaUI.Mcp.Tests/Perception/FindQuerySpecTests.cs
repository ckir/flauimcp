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

    // --- Null-element-name invariant (regression: v0.7.3 desktop_find `contains` NRE) ------------
    // UIA returns a null Name for unnamed containers (Panes/Groups) - nearly every real window has
    // one. EVERY name matcher must tolerate a null element name without throwing. Before the fix the
    // "contains" branch called name.Contains(...) on a null instance -> NullReferenceException, which
    // surfaced as an INTERNAL error and crashed desktop_find on essentially every real window (the eq
    // branch survived via the null-safe static string.Equals). See PerceptionManager.FindAsync's
    // el.Name read (SafeRead only substitutes its fallback on EXCEPTION, not on a null return).
    [Theory]
    [InlineData("eq")]
    [InlineData("contains")]
    public void Matches_null_element_name_never_throws_and_never_satisfies_a_name_constraint(string mode)
    {
        var spec = new FindQuerySpec(new FindQuery(AutomationId: null, Name: "Anything", NameMatch: mode,
            ControlType: null, EnabledOnly: false));
        Assert.False(spec.MatchesPostFilter(name: null, enabled: true));
    }

    [Fact]
    public void Matches_null_element_name_passes_when_no_name_constraint()
    {
        // No name constraint (element matched natively by automationId/controlType): a null-Named
        // element still passes the post-filter's name gate rather than crashing it.
        var spec = new FindQuerySpec(new FindQuery(AutomationId: "aid1", Name: null, NameMatch: "contains",
            ControlType: null, EnabledOnly: false));
        Assert.True(spec.MatchesPostFilter(name: null, enabled: true));
    }

    [Fact]
    public void Matches_null_element_name_still_honors_enabledOnly()
    {
        var spec = new FindQuerySpec(new FindQuery(AutomationId: null, Name: null, NameMatch: "eq",
            ControlType: null, EnabledOnly: true));
        Assert.True(spec.MatchesPostFilter(name: null, enabled: true));
        Assert.False(spec.MatchesPostFilter(name: null, enabled: false));
    }

    [Fact]
    public void MatchesPostFilter_contains_ignoreCase_matches_across_case()
    {
        var spec = new FindQuerySpec(new FindQuery(
            AutomationId: null, Name: "memory", NameMatch: "contains",
            ControlType: null, EnabledOnly: false, IgnoreCase: true));
        Assert.True(spec.MatchesPostFilter("Clear all Memory", enabled: true));
    }

    [Fact]
    public void MatchesPostFilter_eq_ignoreCase_matches_across_case()
    {
        var spec = new FindQuerySpec(new FindQuery(
            null, "five", "eq", null, false, IgnoreCase: true));
        Assert.True(spec.MatchesPostFilter("Five", enabled: true));
    }

    [Fact]
    public void MatchesPostFilter_ordinal_default_is_case_sensitive()
    {
        var spec = new FindQuerySpec(new FindQuery(
            null, "memory", "contains", null, false, IgnoreCase: false));
        Assert.False(spec.MatchesPostFilter("Clear all Memory", enabled: true));
    }
}
