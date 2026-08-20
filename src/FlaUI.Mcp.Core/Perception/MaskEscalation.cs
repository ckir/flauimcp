using System;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>One redact-worthy element whose OWN rect was unusable, so its mask was taken from an ancestor.
///
/// ⚠ The element's Name is NEVER carried here — that would leak the identity of the very thing the mask
/// exists to hide. AutomationId and ControlType are both already published for redacted elements by
/// desktop_find (BC-1 targetability), so emitting them withholds nothing that redaction withholds.
///
/// ControlType is present because AutomationId ALONE is a blindspot: many elements legitimately have none
/// — this repo's own fixture depends on that — so an id-only diagnostic degrades to ["", "", ""] on
/// exactly the legacy and flat-tree UIs where A1's refusal path fires hardest. An element with no
/// AutomationId contributes an empty string and still reports its ControlType.</summary>
public sealed record MaskEscalationEntry(string AutomationId, string ControlType);

/// <summary>The outcome of resolving ONE redact-worthy element to a mask rect. <see cref="Escalated"/> is a
/// BOOL, deliberately: the wire's `maskEscalations` counts ELEMENTS, not levels climbed, and a type that
/// cannot represent a level count cannot drift into reporting one.</summary>
public readonly record struct MaskResolution(Rectangle Rect, bool Escalated, bool Refused)
{
    public static readonly MaskResolution Refusal = new(default, false, true);
}

/// <summary>SP4/A1. THE escalation decision, extracted so it is testable without UIA: the UIA-touching code
/// shrinks to "read a rect, hand it to the decision".
///
/// One rule, not a taxonomy. The tempting design branches on WHY a rect is missing (threw / element
/// vanished / zero-size); that was rejected deliberately, because distinguishing them multiplies
/// exception-type handling on the one path whose whole job is to withhold, and one auditable rule is worth
/// more here than a precise one.
///
/// ⚠ The accepted cost, stated rather than implied: an element that vanished mid-capture is very likely no
/// longer painted, so escalating masks a parent region holding no secret. On an animating or transitioning
/// UI that produces occasional black boxes over benign content. This is a real usability cost on a common
/// transition, accepted because a teardown-race element may still be painted and withholding is the point.
///
/// WARNING, and this is the honest limit of the whole mechanism: ESCALATION IS BEST-EFFORT, NOT A
/// CONTAINMENT GUARANTEE. It assumes an ancestor's BoundingRectangle encloses its descendants' painted
/// pixels. That is USUALLY true and is not universally true: a child can paint outside its parent's bounds
/// through a negative margin, an absolute or canvas position, or a render transform. When it does, the
/// ancestor mask covers less than the element did and the uncovered part of the secret is photographed.
///
/// This cannot be checked from here. Escalation happens precisely BECAUSE the element's own rect is
/// unreadable, so its true extent is the one quantity unavailable at the moment the decision is made. It is
/// stated rather than fixed, and ledgered as AB-8, because a guarantee that is 99% true and documented as
/// absolute is worse than one that is 99% true and says so.</summary>
public static class MaskEscalation
{
    /// <summary>TERMINATION BOUND, not a tuning knob. An uncapped ancestor climb on a provider with a
    /// structural cycle never returns, and this runs on the single query STA — a hang there wedges every
    /// SUBSEQUENT query, not merely this capture. Exhausting the cap is treated exactly as reaching the
    /// window root: REFUSE. Do not remove this for being "unreachable in practice".
    ///
    /// 32 is BELOW the snapshot walk's default maxDepth of 40, which looks like an over-refusal risk and is
    /// not one — worth writing down, because the next reader will re-derive the worry. Cap-exhaustion and
    /// root-reaching produce the IDENTICAL outcome, so the cap can only refuse a capture that would
    /// otherwise have succeeded in one shape: a usable ancestor above level 32 with all 32 below it
    /// unusable. Any tree in that state is already pathological.</summary>
    public const int MaxAncestorLevels = 32;

    /// <summary>A rect is usable only with positive area. A provider unable to report bounds may return
    /// zeros rather than throw, and a zero-area rect paints nothing — a silent leak, not a mask.
    ///
    /// ⚠ <see cref="NotNullWhenAttribute"/> is load-bearing, not decoration. Nullable flow analysis cannot
    /// carry a null-check across a method boundary, so without it every caller that reads <c>.Value</c>
    /// after a true return raises CS8629 and the build leaves 0-warnings. The attribute states the
    /// invariant this method actually guarantees — a true return means the value is present — rather than
    /// suppressing the warning at each call site with <c>!</c>, which would tell the next reader nothing.
    /// Verified: this works for a nullable VALUE type, not only for reference types.</summary>
    public static bool HasArea([NotNullWhen(true)] Rectangle? r) => r is not null && r.Value.Width > 0 && r.Value.Height > 0;

    /// <summary>Resolve one redact-worthy element to the rect that should be painted black.
    ///
    /// <paramref name="ancestorRect"/> is a LAZY accessor: level 1 is the element's parent, level 2 its
    /// grandparent, and level n+1 is requested ONLY if level n was unusable. No pre-fetch — supplying a
    /// materialised ordered list would force every ancestor of every redact-worthy element to be fetched
    /// before the decision runs, a cross-process COM cost on a path already dominated by property traffic.
    ///
    /// ⚠ FAILURE IS INDISTINGUISHABLE FROM ABSENCE. Any failure to obtain an ancestor rect — the parent
    /// FETCH itself throwing, the bounds read throwing, or simply running past the window root — presents
    /// here as null. That equivalence is what makes the case table's parent-fetch refusal reachable without
    /// this function knowing anything about UIA.
    ///
    /// ⚠⚠ <paramref name="captureYardstick"/> IS THE SAFETY NET, AND IT IS WHY THIS FUNCTION TAKES GEOMETRY
    /// IT OTHERWISE WOULD NOT NEED. An ESCALATED rect that covers the whole captured region is not a mask — it
    /// is an all-black screenshot wearing one, and returning it successfully is the single outcome A1 exists
    /// to refuse instead. Checking that here, geometrically, catches every route to it at once and needs no
    /// knowledge of which element produced the rect:
    ///   · the window root (its rect IS the capture);
    ///   · a WPF light-dismiss popup, whose host is a transparent FULL-SCREEN overlay — its BoundingRectangle
    ///     is the whole monitor even though the visible popup is small, so "it is only a popup, masking it is
    ///     safe" is false for exactly the popups that are most common;
    ///   · the DESKTOP, if a walk ever escapes its root through an unreadable ancestor identity.
    /// ⚠ Read this next sentence carefully, because an earlier revision of it was WRONG and a review seat
    /// caught the lie: the window-root-versus-popup-root fork in AncestorRectSource **still exists and is
    /// still correct**. What changed is its STATUS. It was once the SOLE mechanism, and as the sole
    /// mechanism it was wrong in both directions — it refused maskable popups AND it masked whole monitors,
    /// because a WPF light-dismiss overlay IS a popup and IS the whole screen. This geometric rule is now
    /// the primary decision; the fork remains beside it as an identity stop that geometry cannot express
    /// (see BlacksOutTheCapture on why a MOVING window defeats geometry alone).
    ///
    /// The check applies ONLY to escalated rects. An element whose OWN rect covers the capture is genuinely
    /// that large, and masking it is correct rather than a degradation.</summary>
    /// <param name="captureYardstick">The region an escalated mask is judged against. MUST be
    /// non-degenerate (positive width AND height); the caller guarantees that, and this function fails
    /// CLOSED if the guarantee is broken. Normally the capture rect clipped to the renderable desktop;
    /// the unclipped rect when clipping would be degenerate. It is NOT necessarily equal to the rect that
    /// ends up captured.</param>
    public static MaskResolution Resolve(Rectangle? ownRect, Func<int, Rectangle?> ancestorRect,
                                         Rectangle captureYardstick)
    {
        if (HasArea(ownRect)) return new MaskResolution(ownRect.Value, Escalated: false, Refused: false);

        // ⚠ A DEGENERATE yardstick makes both loop tests meaningless — nothing IntersectsWith a rect of
        // zero width or height, so every candidate would be discarded and, under an earlier design that
        // DROPPED such candidates, the capture came back UNMASKED. The caller guarantees a non-degenerate
        // yardstick; if that guarantee is ever broken, fail CLOSED rather than turning a guard into a leak.
        //
        // ⚠ HOISTED ABOVE THE LOOP, and the position is the whole point. A review seat called this check
        // redundant, and it is RIGHT ABOUT THE OUTCOME: the overlap test below would reject every candidate
        // anyway and the loop would refuse at the cap. It is kept because "at the cap" means
        // MaxAncestorLevels ANCESTOR FETCHES first, each a cross-process COM round trip on the single query
        // STA, for a question already answered. But INSIDE the loop it did not actually deliver that — it
        // could not run until an ancestor with area had already been fetched, so the short-circuit
        // short-circuited nothing. The yardstick does not change between iterations; testing it once, here,
        // is the only position at which this check does what it is justified by.
        //
        // ⚠ Tested on EXTENTS, not Rectangle.IsEmpty. IsEmpty requires all four fields to be zero, while
        // Rectangle.Intersect compares with `>=` and so yields a zero-width rect at non-zero coordinates —
        // (100, 50, 0, 30) — for two rects that merely touch along an edge. IsEmpty is false there and the
        // leak would return.
        if (captureYardstick.Width <= 0 || captureYardstick.Height <= 0) return MaskResolution.Refusal;

        for (int level = 1; level <= MaxAncestorLevels; level++)
        {
            var r = ancestorRect(level);
            if (!HasArea(r)) continue;

            // ⚠⚠ A CANDIDATE THAT DOES NOT OVERLAP THE CAPTURE IS UNUSABLE — climb on. An earlier revision
            // DROPPED it instead, reasoning that a secret inside an off-capture ancestor cannot appear in
            // the photograph. **That reasoning codified a leak**, and the test that asserted it was
            // asserting the bug: UIA logical parents do not always enclose their visual children. A WPF
            // tooltip, popup or drag adorner can be visually ON SCREEN while its logical parent — a
            // scrolled-away button, say — is entirely off it. If the tooltip's own bounds throw, its
            // off-screen parent "cannot appear in the capture", the mask is dropped, and the tooltip is
            // photographed in the clear. Treating it as unusable keeps climbing toward an ancestor that
            // does overlap, and refuses if none does.
            //
            // The cost is accepted and is the fail-closed direction: an element-scoped capture whose
            // unrelated sibling escalates past it now refuses rather than guessing the secret is elsewhere.
            if (!r.Value.IntersectsWith(captureYardstick)) continue;

            // Covering the capture entirely is not a mask either — see BlacksOutTheCapture.
            if (BlacksOutTheCapture(r.Value, captureYardstick)) continue;

            return new MaskResolution(r.Value, Escalated: true, Refused: false);
        }

        return MaskResolution.Refusal;
    }

    /// <summary>An escalated mask that CONTAINS the captured region hides everything, so it is not a mask.
    ///
    /// ⚠⚠ THE YARDSTICK IS NORMALLY THE VISIBLE CAPTURE — the capture rect INTERSECTED with the renderable
    /// desktop — and that distinction is most of the correctness of this check.
    ///
    /// ⚠ It is NOT always clipped, and this function must not assume it is. Read the contract on the
    /// parameter, not this paragraph: the ONE case where the caller passes the UNCLIPPED rect is a
    /// window- or element-scoped capture of a target with no renderable overlap, where clipping yields a
    /// degenerate rect that nothing intersects — and judging every mask against THAT would drop them all
    /// and return an unmasked image. An earlier revision of this comment asserted the yardstick was always
    /// clipped while the caller had already been changed to sometimes pass the raw rect, which is exactly
    /// the kind of confidently-wrong invariant that gets a guard "simplified" away by the next reader. A MAXIMIZED window's UIA BoundingRectangle BLEEDS PAST the monitor — its
    /// invisible resize border gives it a negative origin and a width and height larger than the screen. A
    /// WPF light-dismiss overlay is exactly the monitor. Against the raw rect, `overlay.Contains(window)` is
    /// FALSE (because -8 &lt; 0), so the full-monitor mask sails straight past this guard and returns the
    /// all-black image it exists to reject. Intersecting the capture with the renderable desktop first
    /// removes the bleed and the comparison becomes the one that was meant.
    ///
    /// ⚠ Honest limit, stated because the next reader will look for a tolerance and should know there is
    /// deliberately none: this is exact containment, not "covers most of". A rect one pixel short on one
    /// edge passes and blacks out ~99.99% of the image. A percentage threshold would close that and would
    /// introduce a tuning knob on a withholding path, which this design refuses on principle — so the
    /// root-identity stop in AncestorRectSource is kept as well. Neither guard is sufficient alone:
    /// identity catches the exact root whatever its geometry, geometry catches everything identity cannot
    /// name.
    ///
    /// ⚠ If you are about to delete the identity stop as redundant — someone will, because in the common
    /// case this geometric check rejects the window root anyway — here is the case that makes it
    /// load-bearing, because it is not obvious. `captureBounds` is read ONCE at the start of the walk; an
    /// ancestor's rect is read DURING it. On a window that MOVES in between, the window root's live rect
    /// no longer contains the stale `yardstick`, this check PASSES, and the root is accepted as a
    /// mask — producing an image blacked out in the wrong place with the secret possibly still visible.
    /// The identity stop does not care where the window is.</summary>
    public static bool BlacksOutTheCapture(Rectangle mask, Rectangle captureYardstick)
        => mask.Contains(captureYardstick);
}
