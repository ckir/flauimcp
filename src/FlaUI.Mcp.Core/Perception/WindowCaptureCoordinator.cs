using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Core.Perception;

/// <param name="MaxAttempts">The retry bound. "Retry" MUST be bounded or a continuously-changing window
/// is a livelock: an animating window never satisfies W1.Size == W2.Size, so an unconditional retry tells
/// the agent to loop forever on a capture that can never succeed.</param>
/// <param name="TimeoutMs">The per-acquisition bound handed to IWindowImageSource.</param>
public sealed record CaptureRetryOptions(int MaxAttempts, int TimeoutMs)
{
    /// <summary>The whole retry sequence must terminate within a wall-clock budget small enough that a
    /// caller does not experience it as a hang, and that budget is documented in the tool description
    /// alongside the timeout. A retry loop whose worst case is unbounded in time is the livelock in a
    /// different costume. 3 x 1500ms = 4.5s worst case.</summary>
    public static readonly CaptureRetryOptions Default = new(MaxAttempts: 3, TimeoutMs: 1500);

    public int WorstCaseMs => MaxAttempts * TimeoutMs;
}

/// <summary>What a capture produced, plus the geometry it came from — the tool layer needs both, because
/// escalations live on the geometry and not on the result.</summary>
/// <param name="UnmaskedProcesses">Populated ONLY on a fallback scrape, from the desktop mask walk that
/// path performs. Empty on the PrintWindow path, where it is genuinely the truth: that image contains one
/// window and its own mask set covered it.</param>
/// <param name="Escalations">The escalations of the walk that produced the masks ACTUALLY PAINTED -- the
/// target's on the PrintWindow path, the DESKTOP walk's on a fallback scrape. Reporting the target's
/// beside a desktop-masked image would describe a mask set that is not the one in the pixels.</param>
public sealed record WindowCaptureOutcome(CaptureResult Result, CaptureGeometry Geometry,
                                          IReadOnlyList<string> UnmaskedProcesses,
                                          IReadOnlyList<MaskEscalationEntry> Escalations);

/// <summary>Canonical steps 1-9: the walk, the retry loop, the bookend validation walk and the scrape
/// fallbacks. THE CALLER, in the sense the spec uses that word.
///
/// The geometry walk arrives as a DELEGATE so this whole class is headless-testable against a scripted
/// walk and a fake acquisition. That is the only way the retry policy and the bookend walk get tests, and
/// they are the two least-reviewed parts of this design.</summary>
public sealed class WindowCaptureCoordinator
{
    private readonly Func<WindowHandle, string?, Task<CaptureGeometry>> _walk;
    private readonly IWindowImageSource _source;
    private readonly CaptureRetryOptions _opts;
    private readonly Func<IntPtr, Rectangle?>? _w2Probe;
    private readonly Func<IntPtr, bool>? _minimizedProbe;
    private readonly Func<IntPtr, bool> _isHung;
    // ⚠ DECLARED HERE, and it was missing for five rounds. Task 19 said "add a constructor parameter
    // stored as `_breaker`" -- an instruction to the implementer that never became a declaration, while
    // the round-5 fold added `_breaker = breaker;` to the constructor and four uses to the body. CS0103
    // on the first build. An instruction that names a field is not the same as declaring one.
    // *(AGY-AFTER panel over this plan, round 10, Literal Implementer.)*
    private readonly CaptureCircuitBreaker? _breaker;
    private readonly Func<Task<bool>>? _denylistedVisible;
    private readonly Func<Task<DesktopMaskSet>>? _desktopMasks;
    private readonly Func<Rectangle, IReadOnlyList<Rectangle>, int, CaptureScope,
                          IReadOnlyList<CaptureWarning>, CaptureResult> _scrape;

    public WindowCaptureCoordinator(
        Func<WindowHandle, string?, Task<CaptureGeometry>> walk,
        IWindowImageSource source,
        CaptureRetryOptions opts,
        Func<IntPtr, Rectangle?>? w2Probe = null,
        Func<IntPtr, bool>? minimizedProbe = null,
        Func<Rectangle, IReadOnlyList<Rectangle>, int, CaptureScope,
             IReadOnlyList<CaptureWarning>, CaptureResult>? scrape = null,
        CaptureCircuitBreaker? breaker = null,
        Func<IntPtr, bool>? isHungProbe = null,
        Func<Task<bool>>? denylistedVisible = null,
        Func<Task<DesktopMaskSet>>? desktopMasks = null)
    {
        _denylistedVisible = denylistedVisible;
        _desktopMasks = desktopMasks;
        _walk = walk; _source = source; _opts = opts;
        _w2Probe = w2Probe; _minimizedProbe = minimizedProbe;
        // ⚠ AN EXPLICIT LAMBDA, NOT A METHOD GROUP. `ScreenCapture.CaptureRectangle` carries an optional
        // SIXTH parameter -- the `IScreenImageSource?` test seam added in Task 10 -- so its natural type
        // is the six-argument delegate, not this field's five-argument one, and `??` cannot convert
        // between them: `scrape ?? ScreenCapture.CaptureRectangle` is CS0019. The lambda pins the
        // five-argument shape and lets the seam take its default in production.
        _scrape = scrape ?? ((r, masks, w, sc, warn) => ScreenCapture.CaptureRectangle(r, masks, w, sc, warn));
        _breaker = breaker;
        // Injected so the breaker's recovery behaviour is headless-testable; production uses the OS.
        _isHung = isHungProbe ?? IsHungAppWindow;
    }

    /// <summary>The production composition, in ONE place, used by the DI container AND by the Desktop
    /// tests. The coordinator itself deliberately takes a DELEGATE for the geometry walk rather than a
    /// <see cref="PerceptionManager"/>, which is what keeps the whole retry/fallback path
    /// headless-testable; this factory is the one spot that closes that seam over the real thing.
    ///
    /// ⚠⚠ IT EXISTS TO MAKE `denylistedVisible` AND `desktopMasks` IMPOSSIBLE TO FORGET. Both are
    /// OPTIONAL constructor parameters, and omitting the first is exactly what made round 5's denylist
    /// fix INERT in production while every test still passed. The seam now throws rather than silently
    /// skipping the guard -- but a throw is a late, loud symptom, and the real fix is that no caller
    /// gets to choose. Five Desktop test call sites would otherwise each have rewired this by hand.
    ///
    /// The environment-shaped dependencies stay parameters: tests inject their own image source, and an
    /// isolated breaker keeps one test's tripped window from colouring another's.
    ///
    /// ⚠ THIS REPLACES THE INLINE LAMBDA THAT USED TO SIT IN THE DI REGISTRATION -- it does not add to
    /// it. `CaptureGeometryCallSiteTests` pins the number of `ResolveWindowCaptureGeometryAsync` call
    /// sites at THREE across `src/`, so moving the call here rather than duplicating it is what keeps
    /// that sweep green.</summary>
    public static WindowCaptureCoordinator ForPerception(
        PerceptionManager perception,
        IWindowImageSource source,
        CaptureCircuitBreaker? breaker = null,
        CaptureRetryOptions? options = null)
        => new((h, r) => perception.ResolveWindowCaptureGeometryAsync(h, r),
               source,
               options ?? CaptureRetryOptions.Default,
               breaker: breaker,
               denylistedVisible: () => perception.DenylistedWindowsVisibleAsync(),
               desktopMasks: () => perception.AllMaskRectsAsync());

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr hWnd);

    public async Task<WindowCaptureOutcome> CaptureAsync(WindowHandle handle, string? @ref,
                                                         CaptureScope scope, int maxWidth)
    {
        CaptureGeometry geo = default!;

        for (int attempt = 1; attempt <= _opts.MaxAttempts; attempt++)
        {
            // Step 1. Every attempt re-walks, which is what makes a retry meaningful.
            geo = await _walk(handle, @ref);

            // Step 1b. The EXISTING geometry-time refusals, unchanged. Neither is retryable.
            if (geo.Denied)
                throw new ToolException(ToolErrorCode.TargetDenied,
                    $"Capturing windows owned by '{geo.DeniedProcess}' is blocked.",
                    "capture a non-sensitive window");
            if (geo.Minimized)
                throw new ToolException(ToolErrorCode.ElementNotActionable,
                    "Window is minimized; restore it first.",
                    "desktop_window_transform restore, then retry");

            // Step 2's signal. RETRYABLE, and distinguishable from minimized above -- a window caught
            // mid-open reports a degenerate rect for a frame, which is exactly the transient this loop
            // exists to absorb. Both surface to the agent as ElementNotActionable once the budget is
            // spent; the internal signal is what differs.
            if (geo.DegenerateWindow)
            {
                if (attempt < _opts.MaxAttempts) continue;
                throw new ToolException(ToolErrorCode.ElementNotActionable,
                    "The target window reported no renderable area on every attempt.",
                    "restore or resize the window, then retry");
            }

            // Warnings are scoped to THIS ATTEMPT. A discarded attempt's warnings are discarded with it.
            var warnings = geo.HasPopupRoots
                ? new[] { CaptureWarnings.For(CaptureWarnings.PopupsNotRendered) }
                : Array.Empty<CaptureWarning>();

            // The breaker short-circuits BEFORE acquisition, which is the whole point: the leak happens
            // inside Acquire, so avoiding the call is the only way to avoid the leak.
            // Two conditions divert to the scrape, and they cover different windows in time: the breaker
            // covers everything AFTER a timeout was observed, the in-flight check covers the gap DURING
            // the first request, before any timeout has been recorded.
            // ⚠⚠ AND THE BREAKER ASKS WHETHER THE TARGET IS STILL HUNG BEFORE IT DIVERTS. Without this
            // the path emits `scrapeFallbackTargetUnresponsive`, whose recourse tells the agent in the
            // PRESENT TENSE that "the target is not pumping messages: it will not respond to input
            // either, so do not queue clicks against it" -- on the strength of a timeout that may be
            // almost five minutes old. An app that hung once and recovered would have every capture
            // degraded to a scrape, and every one of them labelled with a false statement about it.
            //
            // `IsHungAppWindow` is the OS's own answer to this question -- it is what Task Manager uses --
            // and it does not block on the target's message loop, so asking is safe on precisely the
            // window we are avoiding. A recovered window RESETS the breaker and takes the normal path, so
            // the cooldown becomes a bound on how long a STILL-hung window is skipped rather than a flat
            // penalty for having hung once.
            // *(Driver's solo Guard-Consistency pass, round 5: a warning whose text is false of the image
            // it annotates is the same defect class as a guard that disagrees with its neighbour.)*
            if (_breaker is not null
                && (_breaker.IsTripped(geo.NativeWindowHandle)
                    || _breaker.AnotherAcquisitionIsStuck(geo.NativeWindowHandle,
                                                          TimeSpan.FromMilliseconds(_opts.TimeoutMs)))
                && HungOrReset(geo.NativeWindowHandle))
            {
                // ⚠⚠ THE TARGET-STATE GUARDS STILL RUN. Skipping straight to the scrape also skips
                // canonical steps 4-5, which live inside CaptureWindow -- and those are the guards that
                // stop a DEAD or MINIMIZED window being photographed at the rectangle it used to occupy.
                //
                // The reachable defect: a hung window trips the breaker, then minimizes. Without this
                // call the next capture scrapes its old rect and returns a photograph of whatever is now
                // behind it -- confidently, as a success. That is precisely the failure item 8 exists to
                // remove, reintroduced by the mechanism added to contain a different problem.
                //
                // These guards are about the TARGET's state, not about the backend, so every path that is
                // about to photograph a named window owes them.
                var bw2 = ScreenCapture.GuardTargetState(geo.NativeWindowHandle, _w2Probe, _minimizedProbe);
                // Degeneracy is retryable here for the same reason it is inside the seam, and scraping a
                // window with no renderable area would photograph whatever now occupies its old rect.
                if (bw2.Width <= 0 || bw2.Height <= 0)
                {
                    if (attempt < _opts.MaxAttempts) continue;
                    throw new ToolException(ToolErrorCode.ElementNotActionable,
                        "The target window reported no renderable area on every attempt.",
                        "restore or resize the window, then retry");
                }
                // ⚠⚠ AND THE RESIZE CHECK. Skipping the seam also skips `if (w1.Size != w2.Size)`, so a
                // hung window that RECOVERS and resizes during the cooldown would be scraped with stale
                // W1 masks and never checked -- bypassing the exact protection the seam path enforces.
                // *(AGY-AFTER panel over this plan, round 4, Guard-Consistency Auditor.)*
                // ⚠ The resize case needs no refusal here either, for the reason given in
                // OnResizeExhaustedAsync: this path scrapes with a FRESH desktop mask walk, so a size
                // change between the walk and the capture cannot leave the masks stale. It is recorded
                // rather than deleted because round 4 added the check here deliberately and a future
                // reader will wonder where it went.

                var (breakerImage, breakerUnmasked, breakerEsc) = await ScrapeAsync(
                    geo, scope, maxWidth, warnings, CaptureWarnings.ScrapeFallbackTargetUnresponsive);
                return new WindowCaptureOutcome(breakerImage, geo, breakerUnmasked, breakerEsc);
            }

            // Steps 4-8, inside the seam. The in-flight marker brackets the acquisition so a CONCURRENT
            // request for the same window can see that this one is stuck before any timeout is recorded.
            CaptureOutcome outcome;
            _breaker?.BeginAcquisition(geo.NativeWindowHandle);
            try
            {
                outcome = ScreenCapture.CaptureWindow(geo, maxWidth, scope, warnings, _source,
                                                      _opts.TimeoutMs, _w2Probe, _minimizedProbe);
            }
            finally { _breaker?.EndAcquisition(geo.NativeWindowHandle); }

            switch (outcome.Kind)
            {
                case CaptureOutcomeKind.Completed:
                {
                    // Step 9. THE BOOKEND VALIDATION WALK (§2.5). Closes the relayout leak: a window can
                    // reflow massively at a CONSTANT outer size -- an SPA navigating, an accordion
                    // opening, a splitter dragged -- so W1.Size == W2.Size holds, the resize guard never
                    // fires, and mask rects sampled during the walk are painted over the WRONG REGIONS of
                    // an image composed afterwards.
                    //
                    // ⚠ IT DOES NOT COVER RISK 3's STALE COMPOSITION. There the pixels are older than the
                    // tree and a value can be revealed IN PLACE, leaving the element's rect mathematically
                    // identical. M1 == M2 and this walk passes a genuinely stale capture. It closes the
                    // RELAYOUT leak and nothing else.
                    //
                    // ⚠ It CANNOT run between steps 6b and 7 where it would be cheapest: those are both
                    // inside CaptureWindow, and the walk lives out here. So a mismatch WASTES a full
                    // encode. Accepted -- the mismatch is the rare path, and the alternative is giving the
                    // seam the ability to walk the UIA tree.
                    //
                    // ⚠ An EMPTY mask set skips the walk entirely. Nothing could have gone stale, and this
                    // is what keeps the common case free.
                    if (geo.MaskRects.Count == 0)
                        return new WindowCaptureOutcome(outcome.Result!, geo,
                                                        System.Array.Empty<string>(), geo.Escalations);

                    // ⚠ THE BOOKEND WALK IS GUARDED, AND UNGUARDED IT TURNED SUCCESS INTO A REFUSAL.
                    // This walk can throw for the same reasons the first one can -- a tearing-down window
                    // raises RedactionUnmaskable from the mask sweep. Letting that escape would discard a
                    // GOOD image that is already in hand, and would introduce a terminal refusal on
                    // ELEMENT scope, which the design states has none.
                    //
                    // A walk that fails is treated as a MISMATCH, not as an error: we could not confirm
                    // the mask set survived the capture, and "could not confirm" must not read as
                    // "confirmed". The retry then re-walks, and on exhaustion the scope's own terminal
                    // outcome applies -- window-with-masks refuses, element falls back. If the window is
                    // genuinely gone, the NEXT attempt's step-1 walk throws and that one is deliberately
                    // unguarded, so the agent still learns the target died.
                    bool confirmed;
                    try
                    {
                        var after = await _walk(handle, @ref);
                        confirmed = MaskSetsMatch(geo, after);
                    }
                    catch (ToolException) { confirmed = false; }

                    if (confirmed)
                        return new WindowCaptureOutcome(outcome.Result!, geo,
                                                        System.Array.Empty<string>(), geo.Escalations);

                    if (attempt < _opts.MaxAttempts) continue;

                    // ⚠⚠ A BOOKEND EXHAUSTION REFUSES ON **BOTH** SCOPES, and it must NOT be routed
                    // through OnResizeExhausted. That method falls back to the scrape for element scope,
                    // which is correct for a RESIZE -- there the failure is a geometry mismatch and the
                    // masks may be perfectly fine. It is WRONG here.
                    //
                    // ⛔⛔ THE REASON WRITTEN HERE WAS FALSE, AND THE REFUSAL IS RETAINED ON A DIFFERENT
                    // ONE. It read: "Falling back would scrape the window and paint those same rects --
                    // rects we have just PROVEN are stale -- producing an under-redacted image."
                    //
                    // `ScrapeAsync` does NOT paint those rects. Round 6 changed it to take a FRESH
                    // full-desktop mask walk at capture time (`var desk = await _desktopMasks();`), which
                    // is the whole reason the RESIZE path was allowed to stop refusing. So the stated
                    // justification was destroyed by that fold and the guard kept standing on it.
                    // *(AGY-CAPSTONE round 1, finding 2.)*
                    //
                    // THE HONEST ARGUMENT FOR STILL REFUSING, stated so it can be argued with: a fallback
                    // repeats the same walk-then-shutter cycle this window has JUST BEEN MEASURED to lose.
                    // The bookend fired because the mask set moved between two walks bracketing one
                    // capture; a scrape is another walk and another capture, on a window still moving.
                    // Fresh rects are not the same as rects that are still correct when the shutter opens.
                    //
                    // ⚠ AND THE COST IS REAL, so this is a trade rather than an obvious call: a window
                    // that animates continuously AND holds a redacted element gets a total outage here,
                    // where the resize path would have degraded to a scrape. **Open for the operator** —
                    // the decision is retained as shipped, not re-decided by this review.
                    //
                    // Note this needs no mask-set test: the bookend only runs when M1 is non-empty.
                    throw new ToolException(ToolErrorCode.RedactionUnmaskable,
                        "The window's redacted regions kept moving during the capture, so they cannot be " +
                        "reliably located in the image.",
                        "wait for the window to settle, then retry, or capture a different window");
                }

                case CaptureOutcomeKind.TimedOut:
                    _breaker?.Trip(geo.NativeWindowHandle);
                    // A MECHANISM failure: the window is on screen with real pixels and PrintWindow simply
                    // could not get a copy because the target's loop is blocked. The scrape reads the
                    // composited desktop and is unaffected, so it genuinely has a better answer than
                    // nothing. Refusing here would take away behaviour that works today.
                    var (timeoutImage, timeoutUnmasked, timeoutEsc) = await ScrapeAsync(
                        geo, scope, maxWidth, warnings, CaptureWarnings.ScrapeFallbackTargetUnresponsive);
                    return new WindowCaptureOutcome(timeoutImage, geo, timeoutUnmasked, timeoutEsc);

                case CaptureOutcomeKind.TargetTransient:
                    // The target's geometry was momentarily unusable -- a degenerate W2, or an element
                    // rect outside its own window. Both are UIA reporting a bad rectangle for a frame,
                    // which is the transient this loop exists to absorb and which the design already
                    // absorbs for a degenerate W1. Terminal only when the budget is spent, and it
                    // surfaces to the agent as the SAME code the W1 case does.
                    if (attempt < _opts.MaxAttempts) continue;
                    throw new ToolException(ToolErrorCode.ElementNotActionable,
                        $"{outcome.TransientReason} This held on every attempt.",
                        "re-snapshot the window for fresh bounds, or restore/resize it, then retry");

                case CaptureOutcomeKind.Resized:
                    if (attempt < _opts.MaxAttempts) continue;
                    var (resizeImage, resizeUnmasked, resizeEsc) =
                        await OnResizeExhaustedAsync(geo, scope, maxWidth, warnings);
                    return new WindowCaptureOutcome(resizeImage, geo, resizeUnmasked, resizeEsc);
            }
        }

        throw new ToolException(ToolErrorCode.ElementNotActionable,
            "The capture did not converge within the retry budget.",
            "wait for the window to settle, then retry");
    }

    /// <summary>The terminal outcome when the size never settled. The two scopes differ, and the
    /// difference is NOT inconsistency.</summary>
    private async Task<(CaptureResult Result, IReadOnlyList<string> Unmasked,
                        IReadOnlyList<MaskEscalationEntry> Escalations)> OnResizeExhaustedAsync(
        CaptureGeometry geo, CaptureScope scope, int maxWidth, IReadOnlyList<CaptureWarning> warnings)
    {
        // WINDOW SCOPE WITH MASKS: REFUSE, and never scrape. The mask rects were computed against the
        // pre-resize layout; a resize reflows content, so they no longer necessarily cover what they were
        // sampled to cover. A scrape reproduces that exactly -- the same stale rects over the same
        // reflowed content -- so switching backends cannot fix a MASK problem.
        // ⚠⚠ NEITHER SCOPE REFUSES HERE ANY MORE — OPERATOR DECISION, 2026-08-21, REVERSING PART OF
        // RATIFICATION ITEM 2 ON EVIDENCE THAT ITEM 2's OWN PREMISE HAD BECOME FALSE.
        //
        // The refusal existed because the mask rects were measured at T1 and the fallback scrape happened
        // at T2, so a reflow in between could leave them covering the wrong content. `ScrapeAsync` now
        // performs a FRESH desktop mask walk at T2 (panel round 6), so the fallback acquires its image and
        // its mask set together and they are in sync. The skew this guard existed to prevent no longer
        // exists on this path, and keeping the refusal cost a TOTAL OUTAGE for any animating window that
        // happened to contain one redactable field — windows today's tool captures perfectly well.
        //
        // ⚠ WHAT STILL REFUSES, and why the distinction is real: a BOOKEND mismatch. There the mask set is
        // PROVEN to have moved, which is evidence rather than possibility, and no fresh walk can make a
        // moved mask set describe the frame that was already composed. Every target-state guard
        // (degenerate, minimized, destroyed, denylisted) is untouched.
        //
        // ⚠ The residual risk is UNCHANGED and disclosed: `geo.Bounds` is still the T1 rect, so the
        // captured REGION may be stale even though its masks are not. That is the inherent walk-then-
        // capture race §2 has always accepted, and `scrapeFallbackTargetChanging` is what names it.
        // *(AGY-AFTER panel over this plan, round 8, Adversary of the Reviewer — the seat asked to find
        // the fold most likely to be WRONG, which found one of the review's own.)*

        // ⚠⚠ ELEMENT SCOPE WITH A NON-EMPTY MASK SET REFUSES TOO, and an earlier version of this plan
        // let it fall back. A resize reflows the WINDOW's layout regardless of which scope asked for the
        // capture, so painting pre-resize mask rects onto a post-resize scrape under-redacts exactly as it
        // would for window scope. Falling back "because that is what the tool does today" is the
        // pre-existing-defect defence this project does not accept, and it contradicted the very argument
        // that makes window scope refuse.
        // *(AGY-AFTER panel over this plan, round 4, Guard-Consistency Auditor.)*
        //
        // ⚠ The REGRESSION ARGUMENT FOR THE FALLBACK SURVIVES INTACT, because it was always about a
        // different population. Spinners, progress dialogs and expanding windows -- the cases the fallback
        // was justified by -- carry NO redacted content, so they still fall back. Only a resizing window
        // that also holds something worth masking now refuses.


        // ELEMENT SCOPE, NOTHING TO MASK: fall back. Its failure is a GEOMETRY mismatch on an image that
        // is otherwise sound, nothing was going to be redacted, and the scrape is what this tool does for
        // this window today -- so the fallback restores current behaviour rather than returning nothing.
        return await ScrapeAsync(geo, scope, maxWidth, warnings,
                                 CaptureWarnings.ScrapeFallbackTargetChanging);
    }

    /// <summary>The fallback scrape, using the LAST attempt's geometry. Reusing the first attempt's would
    /// hand the scrape coordinates staler by the entire duration of the loop.
    ///
    /// ⚠ The scrape is NOT synchronised with the geometry, and nothing here claims it is: it takes PIXELS
    /// at one instant while the element rect still comes from a UIA walk that happened earlier. That
    /// staleness is the inherent race §2 documents -- it is today's behaviour, not a new defect -- and the
    /// fallback is justified by "better than nothing", not by synchronisation it does not have.</summary>
    private async Task<(CaptureResult Result, IReadOnlyList<string> Unmasked,
                        IReadOnlyList<MaskEscalationEntry> Escalations)> ScrapeAsync(
        CaptureGeometry geo, CaptureScope scope, int maxWidth,
        IReadOnlyList<CaptureWarning> warnings, string code)
    {
        // ⚠⚠ THE DENYLIST GUARD RUNS ON EVERY FALLBACK, and without it this path bypassed a refusal the
        // full-desktop path treats as absolute. A fallback takes RAW DESKTOP PIXELS of the target's rect,
        // but the mask set was walked for the TARGET ONLY -- so a credential window overlapping the
        // target is photographed completely unmasked. `ScreenshotTools.cs:38` refuses a full-desktop
        // capture outright when any denylisted window is visible, for exactly this reason, and that guard
        // sits inside `if (string.IsNullOrEmpty(window))` so it never reached here.
        //
        // ⚠ THIS EXPOSURE IS PRE-EXISTING, AND ITEM 8 NARROWS IT RATHER THAN CREATING IT. Today EVERY
        // window-scope capture is a scrape with this same hole. After item 8 the primary path renders only
        // the target window, so an overlapping credential window is STRUCTURALLY ABSENT -- the exposure
        // survives only on the fallbacks. Closing it here means the feature strictly improves the posture
        // instead of carrying a known hole into its own new code paths.
        //
        // Refusing is the only honest option: we are already here because PrintWindow could not deliver,
        // so there is no unaffected image to return instead.
        // ⚠⚠ FAIL CLOSED. An earlier version read `if (_denylistedVisible is not null && ...)`, so an
        // unwired delegate silently DISABLED the guard — and it WAS unwired: Phase 6's DI construction did
        // not pass it, the parameter is optional, and the whole round-5 fix was therefore inert in
        // production while every test still passed. A security guard whose absence is indistinguishable
        // from a pass is worse than no guard, because it reads as protection.
        // *(AGY-AFTER panel over this plan, round 6, Literal Implementer — the single defect it named as
        // making the plan not ready to execute.)*
        if (_denylistedVisible is null)
            throw new ToolException(ToolErrorCode.CaptureUnavailable,
                "This capture can only fall back to a screen scrape, and the denylist guard is not wired.",
                "this is a server wiring defect - report it rather than retrying");
        if (await _denylistedVisible())
            throw new ToolException(ToolErrorCode.TargetDenied,
                "A credential/denylisted window is currently visible, and this capture can only fall " +
                "back to a screen scrape, which would photograph it unmasked.",
                "dismiss the credential window, then retry");

        // ⚠⚠ A FALLBACK SCRAPE USES THE **DESKTOP** MASK SET, NOT THE TARGET'S. This path takes raw
        // desktop pixels of the target's rect, so every window OVERLAPPING the target is in the image --
        // and the target's own mask walk knows nothing about them. Painting only the target's rects leaves
        // every overlapping window's sensitive regions in the clear.
        //
        // It also makes `unmaskedProcesses` HONEST. The window path hardcodes it empty, on the comment
        // "empty on that branch is therefore the truth, not a placeholder" -- true of a PrintWindow
        // capture, which contains one window, and FALSE of a scrape, which contains whatever overlapped
        // it. Returning an empty list there tells the agent "everything needing masking was masked" while
        // background windows sit unmasked in the pixels.
        //
        // The cost is one full-desktop mask walk, on a path that is already the slow rare one -- reached
        // only after retries are spent or an acquisition timed out.
        // *(AGY-AFTER panel over this plan, round 6, Guard-Consistency Auditor.)*
        // ⚠⚠ FAIL CLOSED, FOR THE SECOND TIME AND THE SAME REASON. `_desktopMasks` is an optional
        // constructor parameter, and when it is absent this method silently reverts to the TARGET's mask
        // set -- which is precisely the leak the desktop walk was added to close. That is the identical
        // shape that made the denylist guard inert for a whole round: an optional dependency whose
        // absence is indistinguishable from a pass. Once was a mistake; twice would be a pattern.
        // *(Driver's round-8 pass, prompted by the peer's Adversary-of-the-Reviewer seat, which argued
        // from the premise that a fallback's masks are ALWAYS fresh -- true only if this is wired.)*
        if (_desktopMasks is null)
            throw new ToolException(ToolErrorCode.CaptureUnavailable,
                "This capture can only fall back to a screen scrape, and the desktop mask walk is not wired.",
                "this is a server wiring defect - report it rather than retrying");

        // ⚠⚠ THERE IS NO FALLBACK TO `geo.MaskRects` HERE, AND THE ABSENCE IS DELIBERATE. An earlier shape
        // initialised these three from the TARGET's walk and overwrote them inside a conditional. Once the
        // guard above made `_desktopMasks` non-null the conditional was always taken, so those
        // initialisers were DEAD CODE THAT READ AS A LIVE PATH — and the path it appeared to offer was
        // exactly the stale-mask leak the desktop walk exists to close. **Do not "restore" a fallback:
        // there is nothing safe to fall back to.** If `_desktopMasks` is missing, the correct outcome is
        // the refusal above, not the target's masks.
        //
        // ⚠ THE ESCALATIONS MUST COME FROM THE SAME WALK AS THE MASKS. The tool publishes
            // `maskEscalations` and `escalated` beside the image; taking the masks from the desktop walk
            // while reporting the TARGET walk's escalations describes a mask set that is not the one
            // painted. An operator looking at a giant black box and `maskEscalations: 0` has been handed
            // a contradiction. *(Driver's solo pass, round 7 -- a consequence of the round-6 fold.)*
            //
            // ⚠⚠ AND THIS INHERITS THE FULL-DESKTOP REFUSAL. `AllMaskRectsAsync` RETHROWS
            // `RedactionUnmaskable` (`PerceptionManager.cs:1199`) for any window it can SEE and cannot
            // MASK. So a window-scope fallback now refuses when ANY window on the desktop is unmaskable --
            // including one that does not overlap the target and is therefore not in the image. That is
            // STRICTER THAN NECESSARY and it is accepted deliberately: the alternative is scraping with a
            // mask set we know to be incomplete, which is the leak this fold closed. The hint below is
            // what keeps it actionable rather than baffling.
        // ⚠ THIS IS THE MOST EXPENSIVE THING ON ANY CAPTURE PATH, and it is worth knowing before you
        // profile a slow fallback and go looking for the wrong cause. `AllMaskRectsAsync` resolves the
        // geometry of EVERY VISIBLE WINDOW (`PerceptionManager.cs:1163-1183`), and each of those is a UIA
        // descendant walk. On a busy desktop that is not milliseconds.
        //
        // It is paid on EVERY fallback -- timeout, breaker and resize exhaustion alike -- and it lands on
        // a request that has already spent its retry budget. Plus two `DenylistedWindowsVisibleAsync`
        // enumerations, one either side of the shutter. **The tool description now says a fallback costs
        // more than the retry budget rather than implying it fits inside it**, and Task 25 measures it.
        //
        // Not bounded, deliberately: a timeout on this walk would mean scraping with a PARTIAL mask set,
        // which is the leak it exists to close. The honest options are "pay it" or "refuse", and paying it
        // on a path already in degraded mode is the better of the two.
        var desk = await _desktopMasks();
        var masks = desk.Rects;
        var unmasked = desk.UnmaskedProcesses;
        var escalations = desk.Escalations;

        var image = _scrape(geo.Bounds, masks, maxWidth, scope,
                            ScreenCapture.Append(warnings, CaptureWarnings.For(code)));

        // ⚠⚠ THE DENYLIST IS CHECKED AGAIN, AFTER THE PIXELS ARE TAKEN. The check above runs before the
        // desktop mask walk, and that walk is the slowest thing on this path -- so between "no denylisted
        // window is visible" and the shutter there is a window of tens to hundreds of milliseconds. A
        // credential window appearing in that gap is photographed IN THE CLEAR, because
        // `AllMaskRectsAsync` SKIPS denylisted windows rather than masking them
        // (`PerceptionManager.cs:1178`) -- so it contributes no rects, and the scrape paints nothing over
        // it. An application that hangs its own message loop can force this path deliberately and then
        // time the appearance.
        //
        // Re-checking cannot un-take the pixels. What it does is stop them being RETURNED, which is the
        // part that matters: the image is discarded and the capture refuses.
        // *(AGY-AFTER panel over this plan, round 9, Boundary Smuggler.)*
        //
        // ⚠ RESIDUAL, and it is narrow enough to state rather than chase: a denylisted window that
        // appears AND disappears entirely between the two checks would evade both. That is not the
        // exploit above — an attacker wants the window visible while the shutter is open, and a window
        // visible then is still visible microseconds later at this check.
        if (await _denylistedVisible())
            throw new ToolException(ToolErrorCode.TargetDenied,
                "A credential/denylisted window became visible while this fallback scrape was being " +
                "taken, so the image has been discarded.",
                "dismiss the credential window, then retry");

        return (image, unmasked, escalations);
    }

    /// <summary>TRUE when the window is still hung and the divert should happen. When the OS says it has
    /// RECOVERED, this clears the breaker as a side effect and returns false, so the capture takes the
    /// normal path immediately instead of serving degraded scrapes for the rest of the cooldown.
    ///
    /// ⚠ `Reset` existed and was never called until round 6 -- the prose claimed "a recovered window
    /// RESETS the breaker and takes the normal path" while nothing performed the reset, so a recovered
    /// window merely waited out the timer. *(AGY-AFTER round 6, Literal Implementer.)*</summary>
    private bool HungOrReset(IntPtr hwnd)
    {
        if (_isHung(hwnd)) return true;
        _breaker?.Reset(hwnd);
        return false;
    }

    /// <summary>M1 vs M2, as an ORDER-INSENSITIVE SET of WINDOW-RELATIVE rectangles.
    ///
    /// ⚠⚠ WINDOW-RELATIVE, NOT ABSOLUTE, AND THAT IS THE WHOLE CORRECTNESS OF THIS GUARD. Mask rects are
    /// absolute SCREEN coordinates. Comparing them absolutely means a user DRAGGING the window between
    /// the two walks shifts every rect, the bookend reports a mismatch, and a harmless move is treated as
    /// an internal reflow -- retried, and on exhaustion REFUSED. The design states in three separate
    /// places that a pure move is harmless and must not be flagged, so an absolute comparison contradicts
    /// it directly. Normalising each list against ITS OWN walk's window origin isolates internal layout
    /// from window position, which is the only thing this guard is trying to see.
    /// *(AGY-AFTER panel over this plan, round 1, Type-Flow Auditor. The bookend walk is not panel-tested
    /// -- it postdates the spec's thirty rounds -- and this was the first defect found in it.)*
    ///
    /// ⚠ THIS PARAGRAPH USED TO SAY THE OPPOSITE OF THE CODE BELOW, and the contradiction survived
    /// into the pinned block. It read: "Ordered rather than as a set because the walk is deterministic:
    /// it enumerates roots and descendants in a fixed order, so a REORDERING is itself evidence the tree
    /// changed under the capture." That was round 1's rationale, and **round 4 destroyed it** -- the body
    /// comment below explains why, and the implementation SORTS both lists before comparing, so it is
    /// order-insensitive in fact. A doc comment that contradicts its own method is worse than no comment:
    /// the next reader trusts the summary and never reaches the body.
    ///
    /// A window RESIZE between the two walks can also produce a mismatch here. That is correct and not
    /// double-handling: a resize genuinely may have reflowed the content, and the terminal outcome is the
    /// same one the resize rule would reach.</summary>
    private static bool MaskSetsMatch(CaptureGeometry before, CaptureGeometry after)
    {
        var a = before.MaskRects;
        var b = after.MaskRects;
        if (a.Count != b.Count) return false;

        var oa = before.WindowBounds.Location;
        var ob = after.WindowBounds.Location;

        // ⚠ ORDER-INSENSITIVE. An earlier version compared by index, on the reasoning that the walk is
        // deterministic so a reordering would itself be evidence the tree changed. That reasoning is
        // wrong twice over: UIA enumeration order across two walks is not a guarantee this repo owns, and
        // -- more importantly -- a REORDERING WITH IDENTICAL GEOMETRY IS NOT A REFLOW. The question this
        // guard asks is "do the masks still cover the same regions", and the answer does not depend on
        // the order the walk happened to return them in. Comparing by index only added a false-refusal
        // mode. *(AGY-AFTER panel over this plan, round 4, direct answer 1.)*
        static (int X, int Y, int W, int H) Key(Rectangle r, Point o)
            => (r.X - o.X, r.Y - o.Y, r.Width, r.Height);

        var ka = a.Select(r => Key(r, oa)).OrderBy(k => k.X).ThenBy(k => k.Y)
                                          .ThenBy(k => k.W).ThenBy(k => k.H).ToList();
        var kb = b.Select(r => Key(r, ob)).OrderBy(k => k.X).ThenBy(k => k.Y)
                                          .ThenBy(k => k.W).ThenBy(k => k.H).ToList();
        for (int i = 0; i < ka.Count; i++)
            if (ka[i] != kb[i]) return false;
        return true;
    }
}

/// <summary>Remembers windows whose PrintWindow acquisition timed out, and routes subsequent captures of
/// those windows straight to the scrape for a cooldown.
///
/// ⚠ THIS CONTAINS; IT DOES NOT RECLAIM. Each blocked call keeps its thread, its HDC, its GDI bitmap and
/// its managed bitmap for as long as the TARGET PROCESS lives -- a blocked Win32 call cannot be
/// cancelled, so nothing in-process can take them back. MEASURED (Phase 0): they are released when the
/// target's message loop resumes, and within 130ms of the target process exiting. Treat that as
/// unbounded, because neither event is under this server's control. What this bounds is the
/// MULTIPLIER: N captures of a hung window cost ONE leak instead of N. Operator ratification of
/// 2026-08-21 accepted that trade explicitly; the out-of-process worker that would actually reclaim is
/// filed as ROADMAP debt.</summary>
public sealed class CaptureCircuitBreaker
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, DateTime> _tripped = new();
    // ⚠ IN-FLIGHT ACQUISITIONS, and without this the breaker bounds only SERIALIZED captures. Trip() runs
    // AFTER a timeout elapses, so three overlapping requests for the same hung window all read IsTripped
    // as false, all call Acquire, all block, and all leak -- three threads and three bitmaps for one
    // window, defeating the containment this class exists to provide.
    // *(AGY-AFTER panel over this plan, round 3, State Corruptor.)*
    private readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, DateTime> _inFlight = new();
    private readonly TimeSpan _cooldown;
    private readonly Func<DateTime> _clock;

    public CaptureCircuitBreaker(TimeSpan cooldown, Func<DateTime> clock)
    { _cooldown = cooldown; _clock = clock; }

    public static CaptureCircuitBreaker Default => new(TimeSpan.FromMinutes(5), () => DateTime.UtcNow);

    /// <summary>How many windows are currently tracked. Exists so a test can prove the dictionary is
    /// pruned rather than growing forever — the growth is otherwise invisible until it matters.</summary>
    public int TrackedCount => _tripped.Count;

    public bool IsTripped(IntPtr hwnd)
        => _tripped.TryGetValue(hwnd, out var at) && _clock() - at < _cooldown;

    /// <summary>TRUE when another acquisition for this window has ALREADY outlived <paramref name="budget"/>
    /// and is therefore known to be blocked -- so this call would block too, for the same reason, and add
    /// one more permanent leak.
    ///
    /// ⚠ It deliberately does NOT divert merely because another acquisition is in flight. Two agents
    /// capturing the same HEALTHY window concurrently is ordinary, and those calls finish in milliseconds;
    /// diverting them to a scrape would reintroduce occlusion for a window that was working fine. Only an
    /// acquisition that has already exceeded the whole timeout budget is evidence of a hang.</summary>
    public bool AnotherAcquisitionIsStuck(IntPtr hwnd, TimeSpan budget)
        => _inFlight.TryGetValue(hwnd, out var started) && _clock() - started > budget;

    /// <summary>Records the start of an acquisition. Keeps the EARLIEST start for a window, so a stream of
    /// overlapping requests cannot keep pushing the "stuck" judgement into the future.</summary>
    public void BeginAcquisition(IntPtr hwnd) => _inFlight.TryAdd(hwnd, _clock());

    /// <summary>Clears the in-flight marker. Safe to call for a request that TIMED OUT: the abandoned
    /// thread is still blocked, but Trip() has by then recorded the window and the cooldown takes over.</summary>
    public void EndAcquisition(IntPtr hwnd) => _inFlight.TryRemove(hwnd, out _);

    /// <summary>Forget a window entirely -- called when the OS reports it is no longer hung, so a target
    /// that recovered stops being penalised for having hung once. See WindowCaptureCoordinator.HungOrReset,
    /// which is the ONLY caller and the reason this method exists.</summary>
    public void Reset(IntPtr hwnd)
    {
        _tripped.TryRemove(hwnd, out _);
        _inFlight.TryRemove(hwnd, out _);
    }

    public void Trip(IntPtr hwnd)
    {
        // ⚠ PRUNE ON WRITE. Without this the dictionary is UNBOUNDED: every window that ever hung leaves
        // a permanent entry, in a server designed to run for weeks. The entries are tiny, so this is not
        // the leak that matters -- but a subproject whose entire subject is not leaking must not ship a
        // collection that only grows, and HWNDs are recycled by the OS, so a stale entry can also
        // mis-trip the breaker for an unrelated window that happens to reuse the handle value.
        //
        // Pruning on Trip rather than on a timer keeps this allocation-free in the common case: Trip only
        // runs when a capture actually timed out, which is rare by construction.
        var now = _clock();
        foreach (var kv in _tripped)
            if (now - kv.Value >= _cooldown) _tripped.TryRemove(kv.Key, out _);
        _tripped[hwnd] = now;
    }
}
