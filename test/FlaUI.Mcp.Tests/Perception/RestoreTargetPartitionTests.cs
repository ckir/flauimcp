using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// DEF-4: SP3 CREATES this defect — it does not exist today. restoreConfidence reaches the wire at
/// ContentTools.cs:101, so a TITLE COMPARISON is observable to the caller as a string. The moment some
/// titles are hidden, an agent can set its own tab's title to a guess and watch confidence drop from
/// "high" to "reduced" to learn a hidden tab's exact title. The fix partitions the identity space by
/// Sensitivity. No Desktop lease is needed: Resolve takes titles and returns a Result.
public class RestoreTargetPartitionTests
{
    private static readonly Sensitivity Hidden = new(true, RedactionSource.Rule, "r1");

    /// FACT 1 — the oracle is closed. Seeing the cross-partition collision IS the leak.
    [Fact]
    public void A_visible_tab_cannot_collide_with_a_redacted_one()
    {
        var r = RestoreTarget.Resolve("Guess", recordedOrdinal: 0, wasTitleUnique: true,
            freshTitles: new[] { "Guess", "Guess" },
            freshSensitivities: new[] { Sensitivity.Visible, Hidden },
            recordedSensitivity: Sensitivity.Visible);
        Assert.Equal("high", r.Confidence);
        Assert.Equal(0, r.SelectIndex);
    }

    /// FACT 2 — restore still works for protected tabs; the partition must not break the feature.
    [Fact]
    public void Two_protected_tabs_with_distinct_titles_still_restore_confidently()
    {
        var r = RestoreTarget.Resolve("Secret-B", 1, wasTitleUnique: true,
            new[] { "Secret-A", "Secret-B" },
            new[] { Hidden, Hidden }, Hidden);
        Assert.Equal("high", r.Confidence);
        Assert.Equal(1, r.SelectIndex);
    }

    /// FACT 3 — a genuine SAME-partition collision still degrades honestly. The title was unique up front
    /// and a concurrent add duplicated it; the partition must not manufacture confidence it does not have.
    [Fact]
    public void A_same_partition_collision_still_falls_back_to_ordinal()
    {
        var r = RestoreTarget.Resolve("Same", 0, wasTitleUnique: true,
            new[] { "Same", "Same" },
            new[] { Hidden, Hidden }, Hidden);
        Assert.Equal("reduced", r.Confidence);
        Assert.Equal(0, r.SelectIndex);
    }

    /// FACT 4 — INDEX ALIGNMENT. This is the fact that catches a filtered-list implementation: it returns
    /// 0 here and silently restores the WRONG tab. Facts 1-3 all pass on that broken implementation.
    [Fact]
    public void SelectIndex_is_an_index_into_the_FULL_list_not_a_filtered_one()
    {
        var r = RestoreTarget.Resolve("Visible-Target", 2, wasTitleUnique: true,
            new[] { "a", "b", "Visible-Target" },
            new[] { Hidden, Hidden, Sensitivity.Visible },
            Sensitivity.Visible);
        Assert.Equal(2, r.SelectIndex);
        Assert.Equal("high", r.Confidence);
    }
}
