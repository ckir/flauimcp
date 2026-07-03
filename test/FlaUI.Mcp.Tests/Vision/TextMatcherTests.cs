using System.Collections.Generic;
using System.Linq;
using FlaUI.Mcp.Core.Vision;
using Xunit;

namespace FlaUI.Mcp.Tests.Vision;

public class TextMatcherTests
{
    // A word on a line: (text, x, lineId). y/w/h fixed for simplicity; x orders words within a line.
    private static OcrWord W(string t, int x, int lineId) => new(t, x, 0, 10, 10, lineId);

    [Fact]
    public void Single_word_exact_normalizes_case_and_whitespace_no_edit_distance()
    {
        var words = new List<OcrWord> { W("Submit", 0, 0), W("5ubmit", 0, 1), W("submit", 0, 2) };
        var hits = TextMatcher.Match("submit", words, MatchMode.Exact).ToList();
        Assert.Equal(2, hits.Count); // "Submit" and "submit" normalize-equal; "5ubmit" does NOT (exact)
        Assert.All(hits, h => Assert.Equal(1.0, h.Confidence, 5));
    }

    [Fact]
    public void Single_word_fuzzy_tolerates_one_ocr_misread()
    {
        var words = new List<OcrWord> { W("5ubmit", 0, 0), W("Cancel", 20, 0) };
        var hits = TextMatcher.Match("Submit", words, MatchMode.Fuzzy).ToList();
        var hit = Assert.Single(hits);
        Assert.Equal("5ubmit", hit.Text);
        Assert.True(hit.Confidence < 1.0 && hit.Confidence > 0.5);
    }

    [Fact]
    public void Multi_word_phrase_matches_adjacent_words_on_a_line_with_union_bounds()
    {
        // "Submit Order" as two adjacent words on line 0; "Cancel" on line 1.
        var words = new List<OcrWord>
        {
            new("Submit", 10, 5, 30, 12, 0),
            new("Order",  45, 5, 25, 12, 0),
            new("Cancel", 10, 40, 30, 12, 1),
        };
        var hit = Assert.Single(TextMatcher.Match("Submit Order", words, MatchMode.Fuzzy));
        Assert.Equal("Submit Order", hit.Text);
        Assert.Equal(1.0, hit.Confidence, 5);
        // Union bitmap rect: x=10, y=5, right=45+25=70 -> w=60, h=12.
        Assert.Equal(10, hit.X); Assert.Equal(5, hit.Y);
        Assert.Equal(60, hit.W); Assert.Equal(12, hit.H);
    }

    [Fact]
    public void Phrase_does_not_span_across_lines()
    {
        var words = new List<OcrWord> { W("Submit", 0, 0), W("Order", 0, 1) }; // different lines
        Assert.Empty(TextMatcher.Match("Submit Order", words, MatchMode.Fuzzy));
    }

    [Fact]
    public void Ocr_over_split_single_word_rejoins_via_a_two_word_window()
    {
        // OCR split "Submit" into "Subm" + "it" on the same line; query is the single token "Submit".
        var words = new List<OcrWord> { new("Subm", 0, 0, 20, 10, 0), new("it", 22, 0, 8, 10, 0) };
        var hit = Assert.Single(TextMatcher.Match("Submit", words, MatchMode.Fuzzy));
        Assert.Equal("Subm it", hit.Text);       // rejoined window
        Assert.True(hit.Confidence > 0.5);        // "subm it" vs "submit" = edit distance 1
    }

    [Fact]
    public void Fuzzy_rejects_beyond_tolerance()
        => Assert.Empty(TextMatcher.Match("Submit", new List<OcrWord> { W("Delete", 0, 0) }, MatchMode.Fuzzy));

    [Fact]
    public void Returns_all_matches_ordered_by_confidence_desc()
    {
        var words = new List<OcrWord> { W("Submit", 0, 0), W("5ubmit", 0, 1), W("Submit", 0, 2) };
        var hits = TextMatcher.Match("Submit", words, MatchMode.Fuzzy).ToList();
        Assert.Equal(3, hits.Count);
        Assert.True(hits[0].Confidence >= hits[1].Confidence);
        Assert.True(hits[1].Confidence >= hits[2].Confidence);
    }

    [Fact]
    public void Empty_query_matches_nothing()
        => Assert.Empty(TextMatcher.Match("", new List<OcrWord> { W("Submit", 0, 0) }, MatchMode.Fuzzy));
}
