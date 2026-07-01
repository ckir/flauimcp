using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

// Headless (NOT Category=Desktop): pure logic, no UIA / no SendInput.
public class TypedTextVerifierTests
{
    // ---- Check: Match / Mismatch on the empty precondition ----

    [Fact]
    public void Empty_before_and_exact_after_is_Match()
    {
        var o = TypedTextVerifier.Check(before: "", after: "hello world", typed: "hello world");
        Assert.Equal(VerifyStatus.Match, o.Status);
        Assert.Null(o.Reason);
        Assert.Null(o.Expected);
        Assert.Null(o.Actual);
    }

    [Fact]
    public void Empty_before_and_garbled_after_is_Mismatch_with_normalized_echoes()
    {
        var o = TypedTextVerifier.Check(before: "", after: "smoke 000000.cccccced", typed: "smoke test v0.7.2");
        Assert.Equal(VerifyStatus.Mismatch, o.Status);
        Assert.Null(o.Reason);
        Assert.Equal("smoke test v0.7.2", o.Expected);      // normalized typed
        Assert.Equal("smoke 000000.cccccced", o.Actual);    // normalized read-back
    }

    // ---- Check: Skipped branches ----

    [Fact]
    public void Null_before_is_Skipped_no_textpattern()
    {
        var o = TypedTextVerifier.Check(before: null, after: "anything", typed: "x");
        Assert.Equal(VerifyStatus.Skipped, o.Status);
        Assert.Equal("no-textpattern", o.Reason);
        Assert.Null(o.Expected);
        Assert.Null(o.Actual);
    }

    [Fact]
    public void Nonempty_before_is_Skipped_field_not_empty()
    {
        var o = TypedTextVerifier.Check(before: "x", after: "xhello", typed: "hello");
        Assert.Equal(VerifyStatus.Skipped, o.Status);
        Assert.Equal("field-not-empty", o.Reason);
    }

    [Fact]
    public void Newline_only_before_is_Skipped_field_not_empty_raw_precondition()
    {
        // RAW empty precondition: a lone "\n" is NOT empty (caret ambiguous) -> Skipped, not compared.
        var o = TypedTextVerifier.Check(before: "\n", after: "\nhello", typed: "hello");
        Assert.Equal(VerifyStatus.Skipped, o.Status);
        Assert.Equal("field-not-empty", o.Reason);
    }

    // ---- Check: normalization semantics through Check ----

    [Fact]
    public void Crlf_after_matches_lf_typed()
    {
        var o = TypedTextVerifier.Check(before: "", after: "a\r\nb", typed: "a\nb");
        Assert.Equal(VerifyStatus.Match, o.Status);
    }

    [Fact]
    public void Single_trailing_newline_appended_by_editor_still_matches()
    {
        var o = TypedTextVerifier.Check(before: "", after: "foo\n", typed: "foo");
        Assert.Equal(VerifyStatus.Match, o.Status);
    }

    [Fact]
    public void Double_trailing_newline_collapse_matches()
    {
        // typed ends with a newline AND the editor appends its own structural newline: strip ALL.
        var o = TypedTextVerifier.Check(before: "", after: "foo\n\n", typed: "foo\n");
        Assert.Equal(VerifyStatus.Match, o.Status);
    }

    [Fact]
    public void Emoji_exact_matches_and_differing_emoji_mismatches()
    {
        Assert.Equal(VerifyStatus.Match,
            TypedTextVerifier.Check(before: "", after: "hi \U0001F600", typed: "hi \U0001F600").Status);
        Assert.Equal(VerifyStatus.Mismatch,
            TypedTextVerifier.Check(before: "", after: "hi \U0001F601", typed: "hi \U0001F600").Status);
    }

    // ---- Normalize unit cases (internal via InternalsVisibleTo) ----

    [Theory]
    [InlineData("a\rb", "a\nb")]        // lone CR -> LF
    [InlineData("a\r\nb", "a\nb")]      // CRLF -> LF
    [InlineData("a\n", "a")]            // single trailing LF stripped
    [InlineData("a\n\n\n", "a")]        // multiple trailing LF all stripped
    [InlineData("a\r\n\r\n", "a")]      // trailing CRLFs all stripped
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData(" a b ", " a b ")]      // leading/interior/trailing-space (non-\n) preserved
    public void Normalize_cases(string? input, string expected)
        => Assert.Equal(expected, TypedTextVerifier.Normalize(input));

    // ---- Truncate (public) ----

    [Fact]
    public void Truncate_leaves_short_string_unchanged()
    {
        var s = new string('a', 256);
        Assert.Equal(s, TypedTextVerifier.Truncate(s, 256));
        Assert.Equal("abc", TypedTextVerifier.Truncate("abc", 256));
    }

    [Fact]
    public void Truncate_caps_oversized_to_exactly_max_including_ellipsis()
    {
        var s = new string('a', 257);
        var r = TypedTextVerifier.Truncate(s, 256);
        Assert.Equal(256, r.Length);                 // NOT 257 (off-by-one guard)
        Assert.EndsWith("…", r);                // ends with the ellipsis
        Assert.Equal(new string('a', 255) + "…", r);
    }

    [Fact]
    public void Truncate_result_never_exceeds_max_for_large_input()
    {
        var r = TypedTextVerifier.Truncate(new string('x', 100000), 256);
        Assert.True(r.Length <= 256);
    }
}
