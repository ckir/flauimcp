using System;
using System.Collections.Generic;
using System.Linq;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class UnicodeKeyTyperTests
{
    // ---- UnicodeKeyInput.Groups: per-character grouping that preserves the exact Build() wire sequence ----

    [Fact]
    public void Groups_yields_one_group_of_down_up_per_bmp_char()
    {
        var groups = UnicodeKeyInput.Groups("ab").ToList();
        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Equal(2, g.Length)); // down + up
    }

    [Fact]
    public void Groups_keeps_a_surrogate_pair_together_as_one_group()
    {
        // "a😀b": U+1F600 is a surrogate pair (2 UTF-16 units -> 4 INPUTs) and must NOT be split by a delay.
        var groups = UnicodeKeyInput.Groups("a\U0001F600b").ToList();
        Assert.Equal(new[] { 2, 4, 2 }, groups.Select(g => g.Length).ToArray());
    }

    [Fact]
    public void Groups_concatenated_equal_Build_exactly()
    {
        // Pin the invariant: pacing must not change the bytes on the wire vs the shipped v0.7.0 blast.
        foreach (var text in new[] { "ab", "Hello, world!", "a\U0001F600b" })
        {
            var flat = UnicodeKeyInput.Groups(text).SelectMany(g => g).ToArray();
            var build = UnicodeKeyInput.Build(text);
            Assert.Equal(build.Length, flat.Length);
            for (int i = 0; i < build.Length; i++)
            {
                Assert.Equal(build[i].U.ki.wScan, flat[i].U.ki.wScan);
                Assert.Equal(build[i].U.ki.dwFlags, flat[i].U.ki.dwFlags);
            }
        }
    }

    [Fact]
    public void Groups_on_empty_text_yields_nothing()
        => Assert.Empty(UnicodeKeyInput.Groups(""));

    // ---- UnicodeKeyTyper.Drive: reverify-before-each + inter-key sleep + mid-type abort ----

    [Fact]
    public void Drive_reverifies_before_each_char_and_sleeps_between_keys()
    {
        int reverifies = 0, sends = 0;
        var sleeps = new List<int>();
        UnicodeKeyTyper.Drive("abc", interKeyDelayMs: 15,
            reverify: () => reverifies++,
            send: _ => sends++,
            sleep: sleeps.Add);

        Assert.Equal(3, reverifies);           // once per character
        Assert.Equal(3, sends);                // once per character
        Assert.Equal(new[] { 15, 15 }, sleeps.ToArray()); // BETWEEN keys: n-1 pauses, none before first
    }

    [Fact]
    public void Drive_with_zero_delay_never_sleeps()
    {
        var sleeps = new List<int>();
        UnicodeKeyTyper.Drive("abc", interKeyDelayMs: 0,
            reverify: () => { }, send: _ => { }, sleep: sleeps.Add);
        Assert.Empty(sleeps);
    }

    [Fact]
    public void Drive_aborts_mid_type_when_reverify_throws_on_a_later_char()
    {
        // focus stolen after the 1st char: the 2nd reverify throws -> only 1 char sent, no further sends.
        int reverifies = 0, sends = 0;
        var boom = new ToolException(ToolErrorCode.ElementDisappearedDuringAction, "focus stolen", "re-focus");
        var ex = Assert.Throws<ToolException>(() =>
            UnicodeKeyTyper.Drive("abc", interKeyDelayMs: 15,
                reverify: () => { if (++reverifies == 2) throw boom; },
                send: _ => sends++,
                sleep: _ => { }));

        Assert.Same(boom, ex);
        Assert.Equal(1, sends); // partial: only the first character landed before the abort
    }

    [Fact]
    public void Drive_on_empty_text_does_nothing()
    {
        int reverifies = 0, sends = 0;
        UnicodeKeyTyper.Drive("", interKeyDelayMs: 15,
            reverify: () => reverifies++, send: _ => sends++, sleep: _ => { });
        Assert.Equal(0, reverifies);
        Assert.Equal(0, sends);
    }
}
