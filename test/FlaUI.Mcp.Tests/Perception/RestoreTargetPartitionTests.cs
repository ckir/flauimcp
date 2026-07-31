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

    /// FACT 5 — the SAME oracle in the OTHER direction, and the one facts 1-4 all miss. Here the tab being
    /// restored is HIDDEN and the decoy is VISIBLE — an attacker titling their own tab to match a protected
    /// one. Facts 2 and 3 only ever pit a hidden target against other hidden tabs, so an implementation that
    /// applies the mask ONLY when the recorded tab is Visible passes all four of them while leaving exactly
    /// this attack open. Found by an AGY-CAPSTONE round; verified by tracing that broken variant against
    /// facts 1-4 before folding it.
    [Fact]
    public void A_visible_decoy_cannot_collide_with_a_REDACTED_target()
    {
        var r = RestoreTarget.Resolve("Secret", recordedOrdinal: 0, wasTitleUnique: true,
            freshTitles: new[] { "Secret", "Secret" },
            freshSensitivities: new[] { Hidden, Sensitivity.Visible },
            recordedSensitivity: Hidden);
        Assert.Equal("high", r.Confidence);
        Assert.Equal(0, r.SelectIndex);
    }

    /// FACT 6 — a mask of the WRONG LENGTH is ignored WHOLESALE rather than half-applied. A partly-applied
    /// mask produces wrong-but-plausible confidences, which is harder to notice than no partition at all.
    ///
    /// ⚠ The mask entry here must EXCLUDE index 0, or the fact cannot fail: with a mask of {Visible} both
    /// the correct implementation and one that guards with `i < mask.Count` return the same answer. With
    /// {Hidden}, the half-applying implementation skips index 0, matches index 1 uniquely and reports
    /// high/1 — while the correct one compares both, finds two, and falls back to ordinal.
    [Fact]
    public void A_mask_of_the_wrong_length_is_ignored_wholesale_not_half_applied()
    {
        var r = RestoreTarget.Resolve("Guess", 0, wasTitleUnique: true,
            new[] { "Guess", "Guess" },
            new[] { Hidden },                // one short, and it would exclude index 0
            Sensitivity.Visible);
        Assert.Equal("reduced", r.Confidence);  // pre-SP3 behaviour: both compared, so not unique
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
