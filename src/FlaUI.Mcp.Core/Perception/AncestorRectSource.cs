using System;
using System.Collections.Generic;
using System.Drawing;
using FlaUI.Core.AutomationElements;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>SP4/A1. The production ancestor source for <see cref="MaskEscalation"/>: walks REAL ancestors
/// on the raw view and memoizes each resolved rect for the lifetime of ONE capture. This is the only UIA
/// in the escalation path.
///
/// RAW view, not control view, because the enumeration being masked is `FindAllDescendants()` with no
/// condition. PerceptionManager.cs:152 records why a TreeWalker was rejected for the SELECTOR walk — a
/// control-view/raw-view difference could change which nodes the count==1 guarantee sees. That reasoning
/// does not reach this walk: it counts nothing and decides no identity, it only hunts for an enclosing
/// rectangle. A node this walk sees that the enumeration did not is harmlessly skipped when its rect is
/// unusable, and harmlessly used as a mask when it is not.
///
/// ⚠⚠ THE WALK IS BOUNDED AT THE ROOT, AND THAT BOUND IS THE WHOLE FEATURE. An earlier draft of this type
/// climbed with GetParent and no stop condition. That draft was WRONG in the most dangerous possible way:
/// GetParent SUCCEEDS at the window root, and the window root HAS valid bounds — so escalation would have
/// returned the root's rect, masked the entire window, and handed back a SUCCESSFUL all-black screenshot.
/// That is precisely the outcome A1 exists to prevent, because an agent hallucinates an all-black image's
/// contents or loops on it, where a refusal forces a strategy change. It would never have thrown
/// RedactionUnmaskable at all, and the headless tests would still have passed — they inject a fake whose
/// chain ends in null, so they described a root boundary the real walk did not have. A green suite over a
/// dead guarantee.
///
/// The bound is the root's RuntimeId, read ONCE at construction. An ancestor whose RuntimeId matches it is
/// reported as "no rect at this level", so reaching the root is indistinguishable from running out of
/// ancestors — which is exactly what MaskEscalation.Resolve turns into a refusal.
///
/// ⚠ If the root's own RuntimeId cannot be read, the walk CANNOT be bounded safely, so every ancestor
/// request answers null and the element refuses. Fail closed: an unbounded climb would sail past the root
/// into the desktop, and masking the desktop is a worse answer than refusing one capture.
///
/// ⚠ MEMOIZATION: the honest justification is COHERENCE, not cost. If one container's bounds read is
/// broken, every one of its redact-worthy children escalates through the SAME chain; without a cache, two
/// siblings failing at different moments read a SCROLLING container's bounds at different times and produce
/// two MISALIGNED masks of one region. The cache locks one rect for the whole pass.
/// It does NOT save cross-process traffic, and the earlier draft's claim that it did was wrong: the key is
/// the ancestor's RuntimeId, which must be READ before the cache can be consulted, so N siblings still cost
/// N RuntimeId reads. The cache trades one COM read for another of the same order and buys coherence with
/// the difference. Stated plainly because "it's an optimisation" would be false.
///
/// ⚠ THE CACHE KEY IS NOT THE ELEMENT OBJECT. Keying a dictionary on AutomationElement invokes its
/// GetHashCode, which fetches RuntimeId cross-process anyway — but INVISIBLY, and once per lookup rather
/// than once per ancestor. Reading it explicitly keeps the cost where a reader can see it.
///
/// One instance per SEARCH ROOT (the window, or one popup), not one per capture: ancestor chains never
/// cross a root boundary, so a per-root cache is exactly as coherent and its bound is unambiguous. Never
/// reuse an instance across captures — a rect is frozen for one pass and must be re-read on the next.</summary>
public sealed class AncestorRectSource
{
    private readonly Dictionary<string, Rectangle?> _byRuntimeId = new(StringComparer.Ordinal);
    private readonly AutomationElement _root;
    private readonly string? _rootId;
    private readonly bool _rootIsWindow;

    /// <summary><paramref name="root"/> is the search root this source is bounded by — the window element,
    /// or the popup element, whichever subtree is being scanned. <paramref name="rootIsWindow"/> decides
    /// what happens WHEN the climb reaches it, and the two answers are genuinely different:
    ///
    /// • **The window root (true): REFUSE.** Masking it blacks out the entire capture, and a successful
    ///   all-black image is worse than an error — an agent hallucinates its contents or loops on it.
    /// • **A popup root (false): OFFER IT, then stop.** Masking a small popup blacks out the popup and
    ///   leaves the rest of the capture intact, which is ordinary over-masking and the direction this
    ///   feature deliberately degrades in. Refusing there would be strictly worse AND stricter than the
    ///   spec, whose rule names the WINDOW root specifically.
    ///   ⚠ OFFER, not decide. Whether that rect is ACCEPTED is MaskEscalation's call, and it rejects one
    ///   that covers the captured region — which is exactly the WPF light-dismiss popup, whose host is a
    ///   transparent FULL-SCREEN overlay. Its BoundingRectangle is the whole monitor while the visible
    ///   popup is small, so "it is only a popup, masking it is safe" is false for the most common popup
    ///   shape there is. This class must not make that judgement itself; it has no idea what is being
    ///   captured.
    ///
    /// ⚠ That distinction is not academic. PopupFinder returns Path-2 popups — WPF/.NET popup hosts that
    /// are direct CHILDREN of the window (PopupFinder.cs:63-78) — as their own search roots, while
    /// roots[0].FindAllDescendants() already covers those same subtrees. So every Path-2 popup's contents
    /// are scanned TWICE. With a refuse-at-every-root bound, pass 0 masks such an element successfully via
    /// the popup and pass 1 then REFUSES the identical element: the redundant scan would crash a capture
    /// the first pass had already handled correctly.</summary>
    public AncestorRectSource(AutomationElement root, bool rootIsWindow)
    { _root = root; _rootId = IdOf(root); _rootIsWindow = rootIsWindow; }

    /// <summary>The lazy accessor <see cref="MaskEscalation.Resolve"/> consumes, bound to ONE element.
    /// Level 1 is that element's parent. Returns null for "no rect at this level", covering the parent
    /// fetch throwing, the bounds read throwing, the bounds coming back empty, and there being no further
    /// ancestor.
    ///
    /// ⚠ Reaching the root is NOT one of those: the root is OFFERED (its rect returned) unless it is the
    /// window root, and the walk then stops. An earlier revision returned null there too and this sentence
    /// still said so one revision after it stopped being true — check the code, not this comment, if the
    /// two ever disagree again.
    ///
    /// The cursor only ever moves FORWARD. Resolve requests levels strictly in ascending order, so each
    /// ancestor of this element is fetched at most once, before the shared cache is consulted at all.</summary>
    public Func<int, Rectangle?> For(AutomationElement element)
    {
        // Unbounded is not an option — see the root-bound warning on this type. With no readable root id
        // there is nothing to stop the climb at, so every level answers "unusable" and the element refuses.
        if (_rootId is null) return _ => null;

        AutomationElement? cursor = element;
        string? lastId = null;
        int reached = 0;
        bool exhausted = false;
        return level =>
        {
            if (exhausted) return null;
            while (reached < level)
            {
                cursor = Parent(cursor);
                reached++;
                if (cursor is null) { exhausted = true; return null; }

                // ⚠ AN UNREADABLE ANCESTOR IDENTITY DOES NOT ABORT THE WALK, and an earlier draft's
                // decision that it should was over-refusal: IdOf answers null both when the read throws AND
                // for any element whose provider exposes no RuntimeId, and one anonymous intermediate
                // container — a bare WPF Border is the ordinary case — would have refused the whole capture
                // while perfectly good ancestors sat one level above it.
                //
                // Climbing on is safe ONLY because the identity stop is not the sole guard: MaskEscalation
                // rejects any escalated rect that covers the captured region, so a walk that escapes this
                // root through an unreadable ancestor still cannot return the desktop as a mask — that rect
                // blacks out the capture, is rejected, and the walk runs to its cap and REFUSES. Identity
                // catches the exact root whatever its geometry; geometry catches everything identity cannot
                // name. Neither is sufficient alone, which is why both are here.
                string? id = IdOf(cursor);
                lastId = id;

                // string.Equals(null, x, Ordinal) is FALSE, so no null guard is needed here — and an
                // unreadable id must NOT stop the walk anyway; see the paragraph above, which is the whole
                // reason this reads as a comparison rather than an abort.
                if (string.Equals(id, _rootId, StringComparison.Ordinal))
                {
                    exhausted = true; // never climb PAST the root
                    // The window root is never a usable mask — its rect IS the capture. A popup root may be,
                    // and MaskEscalation makes that call geometrically: a small popup masks, a full-screen
                    // light-dismiss overlay is rejected there for covering the capture.
                    // `cursor` IS the root here and its id is in hand — do not re-read either.
                    return _rootIsWindow ? null : RectOf(cursor, id);
                }
            }
            // The id was read on the way in; passing it on is not a micro-optimisation but the difference
            // between ONE cross-process RuntimeId read per level and THREE (the bound check, then the cache
            // key, then the popup branch). On the one path whose cost the memoization was supposed to
            // contain, reading the same property three times would have made the cache a net loss.
            return cursor is null ? null : RectOf(cursor, lastId);
        };
    }

    private static AutomationElement? Parent(AutomationElement? el)
    {
        if (el is null) return null;
        // The walker is fetched per step rather than cached in a field so this type names no FlaUI walker
        // type. It only runs on the escalation path, which is rare by construction: an element that reports
        // its own bounds never reaches here at all.
        try { return el.Automation.TreeWalkerFactory.GetRawViewWalker().GetParent(el); }
        catch { return null; }
    }

    private static string? IdOf(AutomationElement el)
    {
        try
        {
            var rid = el.Properties.RuntimeId.ValueOrDefault;
            return rid is not null && rid.Length > 0 ? string.Join(",", rid) : null;
        }
        catch { return null; }
    }

    /// <summary><paramref name="key"/> is the element's already-read RuntimeId — the caller has it, and
    /// re-reading it here would cost a second cross-process call per level.</summary>
    private Rectangle? RectOf(AutomationElement el, string? key)
    {
        if (key is not null && _byRuntimeId.TryGetValue(key, out var cached)) return cached;

        Rectangle? rect = null;
        try { rect = el.BoundingRectangle; } catch { }
        if (key is not null) _byRuntimeId[key] = rect;
        return rect;  // an ancestor whose RuntimeId is unreadable resolves UNCACHED rather than being
                      // skipped — losing the cache must never lose the mask
    }
}
