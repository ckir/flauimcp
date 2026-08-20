using System;
using System.Collections.Generic;
using System.Drawing;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>SP4/A1 — the mask-escalation DECISION, with no UIA in it. Every row of the spec's case table
/// is a fact here. The ancestor source is a fake that counts its calls, so laziness and the depth cap are
/// observable rather than argued.</summary>
public class MaskEscalationTests
{
    private static readonly Rectangle Usable = new(10, 20, 30, 40);
    private static readonly Rectangle Other = new(1, 2, 300, 400);

    /// The captured region every fact below resolves against. Deliberately LARGER than Other, so no fact
    /// trips the blacks-out-the-capture rejection by accident - that rule gets its own dedicated facts.
    private static readonly Rectangle Capture = new(0, 0, 4000, 4000);

    /// A source that answers from a level->rect map and records every level it was asked for.
    private static Func<int, Rectangle?> Source(List<int> asked, params (int Level, Rectangle? Rect)[] answers)
        => level =>
        {
            asked.Add(level);
            foreach (var a in answers) if (a.Level == level) return a.Rect;
            return null;
        };

    [Fact]
    public void A_usable_own_rect_is_masked_precisely_and_never_asks_for_an_ancestor()
    {
        var asked = new List<int>();

        var r = MaskEscalation.Resolve(Usable, Source(asked), Capture);

        Assert.False(r.Refused);
        Assert.False(r.Escalated);
        Assert.Equal(Usable, r.Rect);
        Assert.Empty(asked); // laziness: no ancestor fetched when the element answers
    }

    [Fact]
    public void An_unreadable_own_rect_escalates_to_the_first_usable_ancestor()
    {
        var asked = new List<int>();

        var r = MaskEscalation.Resolve(null, Source(asked, (1, Other)), Capture);

        Assert.False(r.Refused);
        Assert.True(r.Escalated);
        Assert.Equal(Other, r.Rect);
        Assert.Equal(new[] { 1 }, asked); // level 2 never requested once level 1 answered
    }

    /// <summary>A provider unable to report bounds may return zeros rather than throw. A zero-area mask
    /// paints nothing, which is a silent leak wearing a mask's clothes — so it is treated as unusable.</summary>
    [Fact]
    public void A_zero_size_own_rect_is_unusable_and_escalates()
    {
        var asked = new List<int>();

        var r = MaskEscalation.Resolve(new Rectangle(5, 5, 0, 0), Source(asked, (1, Other)), Capture);

        Assert.True(r.Escalated);
        Assert.Equal(Other, r.Rect);
    }

    /// <summary>The case table's "parent rect also unusable" row: keep climbing. Note the decision cannot
    /// tell WHY a level was unusable — a throwing parent FETCH, a throwing bounds read, a zero-size rect
    /// and "there is no such ancestor" all present identically as null. That is what makes the
    /// parent-fetch refusal reachable without this function knowing anything about UIA.</summary>
    [Fact]
    public void An_unusable_ancestor_climbs_further_up_the_chain()
    {
        var asked = new List<int>();

        var r = MaskEscalation.Resolve(null, Source(asked, (1, null), (2, new Rectangle(0, 0, 0, 0)), (3, Other)), Capture);

        Assert.True(r.Escalated);
        Assert.Equal(Other, r.Rect);
        Assert.Equal(new[] { 1, 2, 3 }, asked);
    }

    /// <summary>Escalating to the window root and masking IT would return a SUCCESSFUL all-black
    /// screenshot, which is worse than an error: an agent hallucinates its contents or loops on it. A
    /// refusal forces a strategy change.</summary>
    [Fact]
    public void No_usable_ancestor_refuses_rather_than_returning_a_rect()
    {
        var asked = new List<int>();

        var r = MaskEscalation.Resolve(null, Source(asked), Capture);

        Assert.True(r.Refused);
        Assert.False(r.Escalated);
    }

    /// <summary>TERMINATION, not tuning. An uncapped climb on a provider with a structural cycle never
    /// returns, and this runs on the single query STA — a hang there wedges every LATER query, not just
    /// this capture. The cap must be a hard loop bound, and exhausting it refuses.</summary>
    [Fact]
    public void The_walk_stops_at_the_depth_cap_and_refuses()
    {
        var asked = new List<int>();

        var r = MaskEscalation.Resolve(null, Source(asked), Capture);

        Assert.True(r.Refused);
        Assert.Equal(MaskEscalation.MaxAncestorLevels, asked.Count);
        Assert.Equal(1, asked[0]);
        Assert.Equal(MaskEscalation.MaxAncestorLevels, asked[^1]);
    }

    /// <summary>`maskEscalations` counts ELEMENTS, not levels climbed: one element climbing three levels
    /// reports 1. The type enforces it — Escalated is a BOOL, so a per-level count is not representable in
    /// the decision's own output, and the caller can only add one entry per element.</summary>
    [Fact]
    public void Escalation_is_per_element_not_per_level()
    {
        var asked = new List<int>();

        var r = MaskEscalation.Resolve(null, Source(asked, (3, Other)), Capture);

        Assert.True(r.Escalated);
        Assert.Equal(3, asked.Count);            // three levels were climbed
        Assert.IsType<bool>(r.Escalated);        // and the outcome is still one boolean
    }

    /// <summary>REGRESSION PIN for the second defect the panel caught, which round 2's own fix introduced:
    /// a WPF light-dismiss popup's host is a transparent FULL-SCREEN overlay, so its BoundingRectangle is
    /// the whole monitor while the visible popup is small. Treating "it is only a popup" as "masking it is
    /// safe" would return a successful all-black screenshot for the most common popup shape there is.
    ///
    /// An escalated rect that COVERS the captured region is not a mask - it is an all-black image wearing
    /// one - and it is rejected on geometry, so the walk keeps climbing and ultimately REFUSES.</summary>
    [Fact]
    public void An_escalated_rect_that_covers_the_capture_is_rejected_and_the_walk_continues()
    {
        var asked = new List<int>();
        var wholeScreen = new Rectangle(-100, -100, 9000, 9000); // contains Capture

        // Level 1 would black out everything; level 2 is a real container.
        var r = MaskEscalation.Resolve(null, Source(asked, (1, wholeScreen), (2, Other)), Capture);

        Assert.True(r.Escalated);
        Assert.Equal(Other, r.Rect);                 // NOT the screen-covering rect
        Assert.Equal(new[] { 1, 2 }, asked);
    }

    /// <summary>The same rule with nothing above it to fall back on: refuse rather than return the
    /// all-black image. This is the window-root case expressed purely as geometry.</summary>
    [Fact]
    public void A_capture_covering_rect_with_no_alternative_refuses()
    {
        var wholeScreen = new Rectangle(-100, -100, 9000, 9000);

        var r = MaskEscalation.Resolve(null, level => level == 1 ? wholeScreen : (Rectangle?)null, Capture);

        Assert.True(r.Refused);
    }

    /// <summary>⚠ THIS FACT REPLACED ONE THAT ASSERTED THE OPPOSITE, and the reversal is the point.
    ///
    /// An earlier revision DROPPED an ancestor that did not overlap the capture, reasoning that a secret
    /// inside it could not appear in the photograph. A panel seat showed the reasoning codifies a LEAK: UIA
    /// logical parents do not always enclose their visual children. A WPF tooltip, popup or drag adorner is
    /// routinely ON SCREEN while its logical parent - a scrolled-away button - is entirely off it. If the
    /// tooltip's own bounds throw, its off-screen parent "cannot appear in the capture", the mask is
    /// dropped, and the tooltip is photographed in the clear. The test that asserted the drop was asserting
    /// the bug.
    ///
    /// A non-overlapping candidate is now UNUSABLE: keep climbing toward one that does overlap, and refuse
    /// if none does.</summary>
    [Fact]
    public void An_ancestor_outside_the_capture_is_unusable_and_the_walk_climbs_past_it()
    {
        var asked = new List<int>();
        var faraway = new Rectangle(50_000, 50_000, 100, 100); // no overlap with Capture

        var r = MaskEscalation.Resolve(null, Source(asked, (1, faraway), (2, Other)), Capture);

        Assert.True(r.Escalated);
        Assert.Equal(Other, r.Rect);              // the OVERLAPPING ancestor, not the far-away one
        Assert.Equal(new[] { 1, 2 }, asked);
    }

    /// <summary>The same rule with nothing overlapping above it: REFUSE. Dropping here is what leaked.</summary>
    [Fact]
    public void An_element_whose_every_ancestor_misses_the_capture_refuses()
    {
        var faraway = new Rectangle(50_000, 50_000, 100, 100);

        var r = MaskEscalation.Resolve(null, level => level == 1 ? faraway : (Rectangle?)null, Capture);

        Assert.True(r.Refused);
    }

    /// <summary>Nothing IntersectsWith a degenerate rectangle, so a degenerate yardstick would discard
    /// every candidate. The decision REFUSES there rather than returning an unmasked capture.
    ///
    /// ⚠ This is REACHABLE in production, and a review round was right to ask. For a window- or
    /// element-scoped capture the caller falls back to the UNCLIPPED capture rect when the renderable
    /// intersection is degenerate — and that rect is itself degenerate for a zero-size window, which
    /// arrives here. (The full-desktop sweep is the path that returns early instead, because a window with
    /// no renderable overlap contributes no pixels to a virtual-screen capture.) Were this branch ever
    /// unreachable it would be a false-GREEN, so if a future change makes the caller always pre-filter,
    /// delete this fact rather than leaving it asserting a guard nothing can reach.
    ///
    /// ⚠ THE ANCESTOR RECT IS PER-ROW, AND THAT IS THE ONLY REASON THIS FACT PINS ANYTHING. It originally
    /// used the shared `Other` fixture, and MEASURED, the fact was then VACUOUS on both non-zero rows: the
    /// `IsEmpty` mutant this exists to catch left every row green. `Other` = (1,2,300,400) CONTAINS both
    /// degenerate yardsticks, so once the mutant let execution reach the loop the candidate was rejected by
    /// the blacks-out guard instead — the same Refused outcome by a different route, which an assertion on
    /// `Refused` alone cannot see. A rect that OVERLAPS the yardstick without CONTAINING it is what makes
    /// the two guards disagree, and disagreement is the only thing a single-point mutant can detect.
    ///
    /// ⚠ Row 1 is deliberately NOT discriminating and cannot be made so — do not "fix" it. For
    /// Rectangle.Empty the mutant's `IsEmpty` is genuinely TRUE, so it refuses at the same point the real
    /// guard does; the mutant is simply CORRECT for that one input. No ancestor rect changes that, because
    /// the ancestor is never consulted. The row earns its place by covering the all-zero shape, not by
    /// killing the mutant. Rows 2 and 3 do the killing.</summary>
    [Theory]
    //          yardstick     |  level-1 ancestor  | why
    [InlineData(0, 0, 0, 0,      1, 2, 300, 400)]  // Rectangle.Empty — see the row-1 note above
    [InlineData(100, 50, 0, 30,  90, 40, 20, 20)]  // ⚠ NOT IsEmpty: Rectangle.Intersect compares with `>=`,
    [InlineData(100, 50, 30, 0,  90, 40, 20, 20)]  //   so two rects touching along an edge yield a
                                                   //   degenerate rect at NON-ZERO coords. The ancestor
                                                   //   overlaps without containing, so under the mutant it
                                                   //   is ACCEPTED as a mask and this fact goes red.
    public void A_degenerate_yardstick_refuses_rather_than_dropping_every_mask(
        int x, int y, int w, int h, int ax, int ay, int aw, int ah)
    {
        var ancestor = new Rectangle(ax, ay, aw, ah);

        var r = MaskEscalation.Resolve(null, Source(new List<int>(), (1, ancestor)), new Rectangle(x, y, w, h));

        Assert.True(r.Refused);
    }

    /// <summary>The rule applies to ESCALATED rects only. An element whose OWN rect covers the capture is
    /// genuinely that large, and masking it is correct rather than a degradation - refusing there would
    /// break a legitimate full-window redaction.</summary>
    [Fact]
    public void An_elements_OWN_capture_covering_rect_is_masked_not_refused()
    {
        var wholeScreen = new Rectangle(-100, -100, 9000, 9000);

        var r = MaskEscalation.Resolve(wholeScreen, Source(new List<int>()), Capture);

        Assert.False(r.Refused);
        Assert.False(r.Escalated);
        Assert.Equal(wholeScreen, r.Rect);
    }

    /// <summary>REGRESSION PIN for the defect an adversarial panel caught in this plan's first draft: the
    /// ancestor walk had NO root bound, and since GetParent succeeds at the window root and the root HAS
    /// valid bounds, escalation would have masked the whole window and returned a SUCCESSFUL all-black
    /// screenshot instead of refusing. Every headless fact still passed, because the fake source's chain
    /// ends in null and so described a boundary the real walk did not have.
    ///
    /// This fact pins the CONTRACT that made the fake lie: a source that reports the root as usable turns
    /// what should be a refusal into a mask, so the SOURCE owes the decision a null at the root.
    ///
    /// ⚠ It does NOT pin AncestorRectSource's actual bound. That needs a live element which fails to report
    /// bounds while its ancestors do not, and no fixture can stage one — the AB-1 limitation. The bound is
    /// therefore ledgered as accepted boundary AB-7, with this fact named as its compensation and its limit
    /// stated: if the root check were deleted from AncestorRectSource, no test goes red.</summary>
    [Fact]
    public void A_source_that_reports_the_root_as_usable_would_mask_instead_of_refusing()
    {
        var asked = new List<int>();

        // A source that keeps answering - i.e. one that failed to stop at the root.
        var unbounded = MaskEscalation.Resolve(null, level => { asked.Add(level); return Other; }, Capture);

        Assert.False(unbounded.Refused);   // this is the WRONG outcome, and it is what an unbounded walk gives
        Assert.True(unbounded.Escalated);

        // The bounded contract: the source must report the root as "no rect at this level".
        var bounded = MaskEscalation.Resolve(null, Source(new List<int>()), Capture);
        Assert.True(bounded.Refused);
    }
}
