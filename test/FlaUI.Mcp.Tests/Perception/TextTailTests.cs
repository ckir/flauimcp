using System.Text.Json;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

// Headless: pure slicing decision (Category!=Desktop). The surrogate-safety case is the load-bearing one.
public class TextTailTests
{
    [Fact]
    public void Slice_returns_the_last_cap_chars_for_plain_bmp_text()
        => Assert.Equal("cde", TextTail.Slice("abcde", 3));

    [Fact]
    public void Slice_returns_whole_string_when_shorter_than_cap()
        => Assert.Equal("ab", TextTail.Slice("ab", 10));

    [Fact]
    public void Slice_never_starts_on_an_unpaired_low_surrogate()
    {
        // "A" + one emoji (a surrogate PAIR). Length is 3 chars: 'A', high, low.
        // A naive last-2 slice would start on the LOW surrogate => unpaired => corruption.
        var s = "A\U0001F600";                 // 'A' + emoji
        var tail = TextTail.Slice(s, 2);
        // Must back off to the code-point boundary: either the full pair (2 valid) or drop it entirely.
        Assert.False(char.IsLowSurrogate(tail[0]), "tail must not begin on an unpaired low surrogate");
        // And it must round-trip through System.Text.Json without throwing or emitting U+FFFD.
        var json = JsonSerializer.Serialize(new { text = tail });
        Assert.DoesNotContain("\\uFFFD", json);
        Assert.DoesNotContain("�", json);
    }

    [Fact]
    public void Slice_result_is_always_valid_utf16()
    {
        var s = "\U0001F600\U0001F601\U0001F602"; // three emoji = six chars
        for (int cap = 1; cap <= 6; cap++)
        {
            var tail = TextTail.Slice(s, cap);
            if (tail.Length > 0)
                Assert.False(char.IsLowSurrogate(tail[0]));
            // no unpaired surrogate anywhere
            for (int i = 0; i < tail.Length; i++)
                if (char.IsHighSurrogate(tail[i]))
                    Assert.True(i + 1 < tail.Length && char.IsLowSurrogate(tail[i + 1]));
        }
    }

    [Fact]
    public void Unpaired_surrogate_is_mangled_by_json_so_TextTail_is_required()
    {
        // A lone high surrogate (no low) — what a naive tail slice could leave behind.
        var bad = "x\uD83D"; // 'x' + unpaired high surrogate
        var json = JsonSerializer.Serialize(new { text = bad });
        // System.Text.Json does NOT round-trip it verbatim: it emits the replacement char. This is the
        // corruption TextTail.Slice prevents (proven by Slice_never_starts_on_an_unpaired_low_surrogate).
        Assert.Contains("�", JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json).GetProperty("text").GetString()!);
    }
}
