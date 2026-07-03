using System.Text.Json;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

// Headless: the pure outcome->wire mapping. Key presence is attribute-driven, so a default
// Serialize reproduces the tool's wire shape exactly (ToolResponse uses no naming/null policy).
public class VerifyResultTests
{
    private static JsonElement Wire(VerifyResult r)
        => JsonDocument.Parse(JsonSerializer.Serialize(r)).RootElement;

    private static bool Has(JsonElement e, string key) => e.TryGetProperty(key, out _);

    private static void AssertBooleansPresent(JsonElement e)
    {
        Assert.True(Has(e, "ran"));
        Assert.True(Has(e, "verified"));
        Assert.True(Has(e, "mismatch"));
    }

    [Fact]
    public void Disabled_is_ran_false_reason_disabled_no_echoes()
    {
        var e = Wire(VerifyResult.Disabled);
        AssertBooleansPresent(e);
        Assert.False(e.GetProperty("ran").GetBoolean());
        Assert.False(e.GetProperty("verified").GetBoolean());
        Assert.False(e.GetProperty("mismatch").GetBoolean());
        Assert.Equal("disabled", e.GetProperty("reason").GetString());
        Assert.False(Has(e, "expected"));
        Assert.False(Has(e, "actual"));
        Assert.False(Has(e, "recommendedFallbackTool"));
    }

    [Fact]
    public void Match_is_verified_true_no_reason_no_echoes()
    {
        var e = Wire(VerifyResult.From(new VerifyOutcome(VerifyStatus.Match, null, null, null)));
        AssertBooleansPresent(e);
        Assert.True(e.GetProperty("ran").GetBoolean());
        Assert.True(e.GetProperty("verified").GetBoolean());
        Assert.False(e.GetProperty("mismatch").GetBoolean());
        Assert.False(Has(e, "reason"));        // reason present ONLY on skip/disabled
        Assert.False(Has(e, "expected"));
    }

    [Fact]
    public void Mismatch_carries_stable_fallback_prose_remedy_and_bounded_echoes()
    {
        var outcome = new VerifyOutcome(VerifyStatus.Mismatch, null,
            Expected: "smoke test v0.7.2", Actual: "smoke 000000.cccccced");
        var e = Wire(VerifyResult.From(outcome));
        AssertBooleansPresent(e);
        Assert.True(e.GetProperty("ran").GetBoolean());
        Assert.False(e.GetProperty("verified").GetBoolean());
        Assert.True(e.GetProperty("mismatch").GetBoolean());
        Assert.False(Has(e, "reason"));        // no reason on mismatch
        Assert.Equal("smoke test v0.7.2", e.GetProperty("expected").GetString());
        Assert.Equal("smoke 000000.cccccced", e.GetProperty("actual").GetString());
        Assert.Equal("desktop_set_value", e.GetProperty("recommendedFallbackTool").GetString());
        Assert.Contains("desktop_set_value", e.GetProperty("remedy").GetString());
    }

    [Fact]
    public void Mismatch_truncates_both_echoes_to_exactly_the_ceiling()
    {
        var big = new string('a', 1000);
        var e = Wire(VerifyResult.From(new VerifyOutcome(VerifyStatus.Mismatch, null, big, big)));
        // Exact length (not just <=) proves the mapper actually POPULATED and truncated the echo —
        // a bug that emptied/shortened it would slip past a <= assertion. (agy AGY-AFTER finding #3.)
        Assert.Equal(VerifyResult.VerifyEchoMax, e.GetProperty("expected").GetString()!.Length);
        Assert.Equal(VerifyResult.VerifyEchoMax, e.GetProperty("actual").GetString()!.Length);
        Assert.EndsWith("…", e.GetProperty("expected").GetString());
        Assert.EndsWith("…", e.GetProperty("actual").GetString());
    }

    [Theory]
    [InlineData("field-not-empty")]
    [InlineData("no-textpattern")]
    [InlineData("read-failed")]
    public void Skipped_reasons_are_verified_false_mismatch_false_with_reason_no_echoes(string reason)
    {
        var e = Wire(VerifyResult.From(new VerifyOutcome(VerifyStatus.Skipped, reason, null, null)));
        AssertBooleansPresent(e);
        Assert.True(e.GetProperty("ran").GetBoolean());
        Assert.False(e.GetProperty("verified").GetBoolean());
        Assert.False(e.GetProperty("mismatch").GetBoolean());
        Assert.Equal(reason, e.GetProperty("reason").GetString());
        Assert.False(Has(e, "expected"));
        Assert.False(Has(e, "actual"));
        Assert.False(Has(e, "recommendedFallbackTool"));
    }

    [Fact]
    public void Redacted_skip_never_echoes_a_secret()
    {
        var e = Wire(VerifyResult.From(new VerifyOutcome(VerifyStatus.Skipped, "redacted", null, null)));
        Assert.Equal("redacted", e.GetProperty("reason").GetString());
        Assert.False(Has(e, "expected"));
        Assert.False(Has(e, "actual"));
    }

    [Fact]
    public void Mismatch_canSetValue_true_recommends_set_value_and_emits_the_fact()
    {
        var o = new VerifyOutcome(VerifyStatus.Mismatch, null, Expected: "x", Actual: "y");
        var e = Wire(VerifyResult.From(o, canSetValue: true));
        Assert.True(e.GetProperty("mismatch").GetBoolean());
        Assert.True(Has(e, "canSetValue"));
        Assert.True(e.GetProperty("canSetValue").GetBoolean());
        Assert.Equal("desktop_set_value", e.GetProperty("recommendedFallbackTool").GetString());
    }

    [Fact]
    public void Mismatch_canSetValue_false_recommends_paste_text_and_emits_the_fact()
    {
        var o = new VerifyOutcome(VerifyStatus.Mismatch, null, Expected: "x", Actual: "y");
        var e = Wire(VerifyResult.From(o, canSetValue: false));
        Assert.True(Has(e, "canSetValue"));
        Assert.False(e.GetProperty("canSetValue").GetBoolean());
        Assert.Equal("desktop_paste_text", e.GetProperty("recommendedFallbackTool").GetString());
    }

    [Fact]
    public void Mismatch_canSetValue_null_defaults_to_set_value_and_omits_the_fact()
    {
        var o = new VerifyOutcome(VerifyStatus.Mismatch, null, Expected: "x", Actual: "y");
        var e = Wire(VerifyResult.From(o, canSetValue: null));
        Assert.False(Has(e, "canSetValue")); // JsonIgnore-when-null: unknown -> absent
        Assert.Equal("desktop_set_value", e.GetProperty("recommendedFallbackTool").GetString()); // safe default
    }

    [Fact]
    public void Mismatch_remedy_prose_names_both_strategies_and_warns_off_the_truncated_echo()
    {
        var o = new VerifyOutcome(VerifyStatus.Mismatch, null, Expected: "x", Actual: "y");
        var remedy = Wire(VerifyResult.From(o, canSetValue: false)).GetProperty("remedy").GetString()!;
        Assert.Contains("desktop_set_value", remedy);
        Assert.Contains("desktop_paste_text", remedy);
        Assert.Contains("expected", remedy); // the "do NOT use the truncated 'expected' echo" warning
    }

    [Fact]
    public void Mismatch_with_no_writable_valuepattern_recommends_desktop_paste_text()
    {
        var outcome = new VerifyOutcome(VerifyStatus.Mismatch, null, "expected", "actual");
        var r = VerifyResult.From(outcome, canSetValue: false);
        Assert.Equal("desktop_paste_text", r.RecommendedFallbackTool);
    }

    [Fact]
    public void Mismatch_with_writable_valuepattern_still_recommends_set_value()
    {
        var outcome = new VerifyOutcome(VerifyStatus.Mismatch, null, "expected", "actual");
        var r = VerifyResult.From(outcome, canSetValue: true);
        Assert.Equal("desktop_set_value", r.RecommendedFallbackTool);
    }

    [Fact]
    public void NonMismatch_arms_never_emit_canSetValue()
    {
        Assert.False(Has(Wire(VerifyResult.From(new VerifyOutcome(VerifyStatus.Match, null, null, null))), "canSetValue"));
        Assert.False(Has(Wire(VerifyResult.From(new VerifyOutcome(VerifyStatus.Skipped, "field-not-empty", null, null))), "canSetValue"));
        Assert.False(Has(Wire(VerifyResult.Disabled), "canSetValue"));
    }
}
