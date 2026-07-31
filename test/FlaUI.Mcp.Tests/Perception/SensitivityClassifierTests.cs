using System;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class SensitivityClassifierTests
{
    private static SensitivityClassifier Rules(params RedactionRule[] rules) =>
        SensitivityClassifier.ForRules(rules);

    [Fact]
    public void OsOnly_reports_no_rules_and_redacts_only_password_fields()
    {
        var c = SensitivityClassifier.OsOnly;
        Assert.False(c.HasRules);
        Assert.Equal(RedactionSource.Os, c.Classify("app", () => "aid", () => "name", () => true).Source);
        Assert.False(c.Classify("app", () => "aid", () => "name", () => false).Redact);
    }

    [Fact]
    public void Os_wins_over_a_matching_rule()
    {
        var c = Rules(new RedactionRule("r1", "app", false, "aid", null, null));
        var s = c.Classify("app", () => "aid", () => "name", () => true);
        Assert.True(s.Redact);
        Assert.Equal(RedactionSource.Os, s.Source);
        Assert.Null(s.RuleName);
    }

    [Fact]
    public void A_throwing_IsPassword_read_fails_closed()
    {
        var c = SensitivityClassifier.OsOnly;
        var s = c.Classify("app", () => "aid", () => "name", () => throw new InvalidOperationException());
        Assert.True(s.Redact);
        Assert.Equal(RedactionSource.Os, s.Source);
    }

    [Fact]
    public void Predicates_AND_together()
    {
        var c = Rules(new RedactionRule("r1", "app", false, "aid", null, "^nope$"));
        Assert.False(c.Classify("app", () => "aid", () => "name", () => false).Redact);
    }

    [Fact]
    public void A_rule_scoped_to_another_process_does_not_match()
    {
        var c = Rules(new RedactionRule("r1", "other", false, "aid", null, null));
        Assert.False(c.Classify("app", () => "aid", () => "name", () => false).Redact);
    }

    [Fact]
    public void First_matching_rule_wins_and_names_itself()
    {
        var c = Rules(new RedactionRule("first", "app", false, "aid", null, null),
                      new RedactionRule("second", "app", false, "aid", null, null));
        var s = c.Classify("app", () => "aid", () => "name", () => false);
        Assert.Equal(RedactionSource.Rule, s.Source);
        Assert.Equal("first", s.RuleName);
    }

    /// A null Name must NOT throw into fail-closed. Regex.IsMatch(null, ...) throws
    /// ArgumentNullException, and many UIA elements legitimately have no Name — under a naive
    /// fail-closed rule ONE namePattern rule would redact every nameless element in its process.
    /// Spec §5.2: an absent value normalises to empty and evaluates normally.
    [Fact]
    public void A_null_name_does_not_match_a_name_pattern_and_does_not_fail_closed()
    {
        var c = Rules(new RedactionRule("r1", "app", false, null, null, "secret"));
        Assert.False(c.Classify("app", () => null, () => null, () => false).Redact);
    }

    [Fact]
    public void An_empty_matching_pattern_still_matches_an_absent_name()
    {
        var c = Rules(new RedactionRule("r1", "app", false, null, null, "^$"));
        Assert.True(c.Classify("app", () => null, () => null, () => false).Redact);
    }

    /// A determinate FALSE makes the AND determinate regardless of the throwing predicate,
    /// so the element is correctly left unredacted (spec §5.2).
    [Fact]
    public void A_throwing_predicate_does_not_force_a_match_when_another_is_determinately_false()
    {
        var c = Rules(new RedactionRule("r1", "app", false, "nomatch", null, "whatever"));
        Assert.False(c.Classify("app", () => "aid", () => throw new InvalidOperationException(), () => false).Redact);
    }

    /// Spec §7.1 mandates a `global` handling test and an earlier draft of this plan had none.
    [Fact]
    public void A_global_rule_matches_regardless_of_process()
    {
        var c = Rules(new RedactionRule("g", null, true, "aid", null, null));
        Assert.True(c.Classify("anything", () => "aid", () => "n", () => false).Redact);
        Assert.True(c.Classify("other", () => "aid", () => "n", () => false).Redact);
        Assert.False(c.Classify("anything", () => "different", () => "n", () => false).Redact);
    }

    /// Cheapest-first: a false processName must short-circuit before any name thunk is pulled.
    [Fact]
    public void A_non_matching_process_never_evaluates_the_name_thunk()
    {
        bool pulled = false;
        var c = Rules(new RedactionRule("r1", "other", false, null, null, ".*"));
        c.Classify("app", () => "aid", () => { pulled = true; return "n"; }, () => false);
        Assert.False(pulled);
    }
}
