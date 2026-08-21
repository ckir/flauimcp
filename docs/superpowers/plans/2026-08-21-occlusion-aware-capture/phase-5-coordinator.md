> **Part of the [Occlusion-Aware Window Capture implementation plan](../2026-08-21-occlusion-aware-capture.md).** Read that index first — it carries the REQUIRED-SUB-SKILL directive, the five things that would be undone by re-deriving from the panel record, and the repo rules that fail the build if ignored.

## Phase 5 — The coordinator: the retry loop, the bookend walk, the fallbacks

**The caller owns the loop, and this is the component that IS the caller.** `CaptureWindow` reports a mismatch; it cannot resolve one, because resolving means re-walking the UIA tree and a seam handed a finished `CaptureGeometry` has no way to do that.

**Why a new class rather than putting this in `ScreenshotTools`:** the loop takes the geometry walk as a delegate, which makes the whole of steps 1–9 headless-testable against a fake walk and a fake acquisition. That is the only way the bookend walk and the retry policy get tests at all, and those two are the least-reviewed parts of the design.

### Task 17: `WindowCaptureCoordinator` — the retry loop and the resize policy

**Files:**
- Create: `src/FlaUI.Mcp.Core/Perception/WindowCaptureCoordinator.cs`
- Test: `test/FlaUI.Mcp.Tests/Perception/WindowCaptureCoordinatorTests.cs`
- Modify: `test/FlaUI.Mcp.Tests/Perception/CaptureGeometryCallSiteTests.cs` (the count goes 3 → 4)

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class WindowCaptureCoordinatorTests
{
    private static CaptureGeometry Geo(Rectangle capture, Rectangle window, params Rectangle[] masks)
        => new(capture, masks, false, false, null, Array.Empty<MaskEscalationEntry>(),
               window, new IntPtr(0x1234), false);

    /// <summary>A walk that returns a scripted sequence, one entry per attempt, so a test can stage a
    /// window that settles on attempt N.</summary>
    private static Func<WindowHandle, string?, Task<CaptureGeometry>> Walk(params CaptureGeometry[] seq)
    {
        int i = 0;
        return (_, _) => Task.FromResult(seq[Math.Min(i++, seq.Length - 1)]);
    }

    private static WindowCaptureCoordinator Make(
        Func<WindowHandle, string?, Task<CaptureGeometry>> walk,
        IWindowImageSource source,
        Func<IntPtr, Rectangle?> w2,
        CaptureRetryOptions? opts = null)
        => new(walk, source, opts ?? new CaptureRetryOptions(MaxAttempts: 3, TimeoutMs: 1000),
               w2Probe: w2, minimizedProbe: _ => false,
               scrape: (bounds, masks, mw, scope, warns) =>
                   new CaptureResult(Array.Empty<byte>(), bounds.X, bounds.Y, bounds.Width, bounds.Height,
                                     1.0, masks.Count, "screenScrape", warns));

    [Fact]
    public async Task A_static_window_captures_on_the_first_attempt()
    {
        var w = new Rectangle(0, 0, 400, 300);
        var c = Make(Walk(Geo(w, w)), FakeWindowImageSource.Solid(Color.White), _ => w);
        var r = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.Equal("printWindow", r.Result.CaptureMethod);
        Assert.Empty(r.Result.CaptureWarnings);
    }

    // THE RATIFIED BEHAVIOUR. Window scope WITH masks retries rather than refusing on the first mismatch.
    // Attempt 1 sees a resize; attempt 2 is consistent; the capture succeeds with NO warning, because
    // nothing about the returned image is stale.
    [Fact]
    public async Task Window_scope_with_masks_retries_and_succeeds_when_the_window_settles()
    {
        var stale  = new Rectangle(0, 0, 800, 600);
        var settled = new Rectangle(0, 0, 900, 600);
        var mask = new Rectangle(10, 10, 50, 20);
        var c = Make(Walk(Geo(stale, stale, mask), Geo(settled, settled, mask)),
                     FakeWindowImageSource.Solid(Color.White), _ => settled);
        var r = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.Equal("printWindow", r.Result.CaptureMethod);
        Assert.DoesNotContain(r.Result.CaptureWarnings, w => w.Code == "windowResized");
    }

    // ...and on exhaustion FALLS BACK, with masks re-measured at capture time.
    //
    // ⚠ This REFUSED between panel rounds 4 and 8. The refusal existed because the mask rects were
    // measured at T1 and painted onto a T2 scrape; `ScrapeAsync` now takes a FRESH desktop mask walk at
    // T2, so image and masks are in sync and the refusal only cost a total outage on any animating window
    // that happened to hold one redactable field. Operator decision, 2026-08-21, reversing part of
    // ratification item 2 on evidence its own premise had become false.
    [Fact]
    public async Task Window_scope_with_masks_falls_back_on_exhaustion_with_fresh_masks()
    {
        var w1 = new Rectangle(0, 0, 800, 600);
        var mask = new Rectangle(10, 10, 50, 20);
        var c = Make(Walk(Geo(w1, w1, mask)), FakeWindowImageSource.Solid(Color.White),
                     _ => new Rectangle(0, 0, 900, 600));
        var r = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.Equal("screenScrape", r.Result.CaptureMethod);
        Assert.Contains(r.Result.CaptureWarnings, w => w.Code == "scrapeFallbackTargetChanging");
    }

    // Element scope on exhaustion DOES fall back -- its failure is a geometry mismatch on an image that
    // is otherwise sound, so the scrape genuinely offers something better than nothing.
    [Fact]
    public async Task Element_scope_falls_back_to_the_scrape_on_exhaustion()
    {
        var w1 = new Rectangle(0, 0, 800, 600);
        var e  = new Rectangle(100, 100, 200, 150);
        var c = Make(Walk(Geo(e, w1)), FakeWindowImageSource.Solid(Color.White),
                     _ => new Rectangle(0, 0, 700, 600));
        var r = await c.CaptureAsync(new WindowHandle("w1"), "e5", CaptureScope.Element, 0);
        Assert.Equal("screenScrape", r.Result.CaptureMethod);
        Assert.Contains(r.Result.CaptureWarnings, w => w.Code == "scrapeFallbackTargetChanging");
    }

    // ⚠ A NON-EMPTY MASK SET FALLS BACK TOO, and the masks it paints are FRESH. Between rounds 4 and 8
    // this refused, on the reasoning that a resize reflows the window's layout so pre-resize rects
    // under-redact a post-resize scrape. Round 6 removed the premise: `ScrapeAsync` re-walks the desktop
    // at capture time. What still refuses is a BOOKEND mismatch, where the mask set is PROVEN to have
    // moved and no re-walk can fix the frame already composed.
    [Fact]
    public async Task Element_scope_WITH_masks_falls_back_with_FRESH_masks_rather_than_refusing()
    {
        var w1 = new Rectangle(0, 0, 800, 600);
        var e  = new Rectangle(100, 100, 200, 150);
        var mask = new Rectangle(110, 110, 40, 20);
        bool scraped = false;
        var c = new WindowCaptureCoordinator(
            (_, _) => Task.FromResult(new CaptureGeometry(e, new[] { mask }, false, false, null,
                Array.Empty<MaskEscalationEntry>(), w1, new IntPtr(0x1234), false)),
            FakeWindowImageSource.Solid(Color.White), new CaptureRetryOptions(2, 1000),
            w2Probe: _ => new Rectangle(0, 0, 700, 600), minimizedProbe: _ => false,
            scrape: (b, m, mw, s, warns) => { scraped = true; return new CaptureResult(Array.Empty<byte>(),
                b.X, b.Y, b.Width, b.Height, 1.0, m.Count, "screenScrape", warns); });

        var r = await c.CaptureAsync(new WindowHandle("w1"), "e5", CaptureScope.Element, 0);
        Assert.Equal("screenScrape", r.Result.CaptureMethod);
        Assert.True(scraped, "the fallback must run - its masks come fresh from the T2 desktop walk");
        Assert.Contains(r.Result.CaptureWarnings, w => w.Code == "scrapeFallbackTargetChanging");
    }

    // The fallback must use the LAST attempt's geometry. Reusing the first would hand the scrape
    // coordinates staler by the entire duration of the loop -- the loop actively degrading the fallback
    // it exists to reach.
    [Fact]
    public async Task The_fallback_scrape_uses_the_LAST_attempts_geometry()
    {
        var w1 = new Rectangle(0, 0, 800, 600);
        var first  = new Rectangle(100, 100, 200, 150);
        var latest = new Rectangle(140, 160, 200, 150);
        var c = Make(Walk(Geo(first, w1), Geo(latest, w1), Geo(latest, w1)),
                     FakeWindowImageSource.Solid(Color.White),
                     _ => new Rectangle(0, 0, 700, 600));
        var r = await c.CaptureAsync(new WindowHandle("w1"), "e5", CaptureScope.Element, 0);
        Assert.Equal(140, r.Result.X);
        Assert.Equal(160, r.Result.Y);
    }

    // A timeout is a MECHANISM failure: the window is on screen with real pixels and PrintWindow simply
    // could not get a copy. Refusing here would be a regression -- an agent can photograph a hung window
    // perfectly well today.
    [Fact]
    public async Task A_timeout_falls_back_to_the_scrape_with_the_unresponsive_code()
    {
        var w = new Rectangle(0, 0, 400, 300);
        var c = Make(Walk(Geo(w, w)), FakeWindowImageSource.TimesOut(), _ => w);
        var r = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.Equal("screenScrape", r.Result.CaptureMethod);
        Assert.Contains(r.Result.CaptureWarnings, w => w.Code == "scrapeFallbackTargetUnresponsive");
    }

    // A window caught mid-open reports a degenerate rect for a frame. That is the transient the loop
    // exists to absorb, and it must NOT fail terminally on the first bad frame.
    [Fact]
    public async Task A_degenerate_W1_retries_rather_than_failing_terminally()
    {
        var good = new Rectangle(0, 0, 400, 300);
        var degenerate = new CaptureGeometry(default, Array.Empty<Rectangle>(), false, false, null,
            Array.Empty<MaskEscalationEntry>(), default, new IntPtr(0x1234), DegenerateWindow: true);
        var c = Make(Walk(degenerate, Geo(good, good)), FakeWindowImageSource.Solid(Color.White), _ => good);
        var r = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.Equal("printWindow", r.Result.CaptureMethod);
    }

    // ...but an ALREADY-MINIMIZED window must NOT be retried: retrying just fails identically until the
    // budget runs out. Both surface as ElementNotActionable; the internal signal is what differs.
    [Fact]
    public async Task An_already_minimized_window_refuses_without_burning_the_retry_budget()
    {
        var minimized = new CaptureGeometry(default, Array.Empty<Rectangle>(), Minimized: true, false, null,
            Array.Empty<MaskEscalationEntry>(), default, new IntPtr(0x1234), false);
        int walks = 0;
        var c = Make((_, _) => { walks++; return Task.FromResult(minimized); },
                     FakeWindowImageSource.Solid(Color.White), _ => new Rectangle(0, 0, 1, 1));
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0));
        Assert.Equal(ToolErrorCode.ElementNotActionable, ex.Code);
        Assert.Equal(1, walks);
    }

    [Fact]
    public async Task A_denied_window_refuses_with_TargetDenied()
    {
        var denied = new CaptureGeometry(default, Array.Empty<Rectangle>(), false, Denied: true, "keeper",
            Array.Empty<MaskEscalationEntry>(), default, IntPtr.Zero, false);
        var c = Make(Walk(denied), FakeWindowImageSource.Solid(Color.White), _ => new Rectangle(0, 0, 1, 1));
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0));
        Assert.Equal(ToolErrorCode.TargetDenied, ex.Code);
    }

    // WARNINGS ARE SCOPED TO THE ATTEMPT. Each retry re-walks, so each attempt has its OWN geometry-time
    // warnings. Carrying attempt 1's popupsNotRendered into attempt 2's successful result would state
    // something false about the image that was actually returned.
    [Fact]
    public async Task A_discarded_attempts_warnings_are_discarded_with_it()
    {
        var w1 = new Rectangle(0, 0, 800, 600);
        var settled = new Rectangle(0, 0, 900, 600);
        // Attempt 1 has a popup root; attempt 2 does not.
        var withPopup = new CaptureGeometry(w1, Array.Empty<Rectangle>(), false, false, null,
            Array.Empty<MaskEscalationEntry>(), w1, new IntPtr(0x1234), false) { HasPopupRoots = true };
        var without = Geo(settled, settled);
        var c = Make(Walk(withPopup, without), FakeWindowImageSource.Solid(Color.White), _ => settled);
        var r = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.DoesNotContain(r.Result.CaptureWarnings, w => w.Code == "popupsNotRendered");
    }
}
```

- [ ] **Step 2: Add `HasPopupRoots` to `CaptureGeometry`**

The test above needs it and so does `popupsNotRendered`, which is decided at geometry time. Add it as an **init-only property**, not a positional parameter, so the append-only ordering test is unaffected:

In `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs`, change the `CaptureGeometry` declaration's closing `;` to a body:

```csharp
    System.Drawing.Rectangle WindowBounds, System.IntPtr NativeWindowHandle, bool DegenerateWindow)
{
    /// <summary>This window had popup roots at geometry time — PopupFinder.SearchRoots returned more
    /// than the window itself. Decided during the WALK, consumed by the caller, which is why it travels
    /// on the geometry rather than being re-derived downstream.
    ///
    /// An init-only PROPERTY rather than a positional parameter, deliberately: the positional list is
    /// pinned by CaptureGeometryShapeTests and adding to it would churn every construction site for a
    /// flag most of them do not care about.</summary>
    public bool HasPopupRoots { get; init; }
}
```

Then set it at the success return (`PerceptionManager.cs:1110`), where `roots` is in scope:

```csharp
            return new CaptureGeometry(captureBounds, pw, false, false, null, escalations,
                                       windowBounds, NativeHandleOf(win), false)
            { HasPopupRoots = roots.Count > 1 };
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~WindowCaptureCoordinatorTests"`
Expected: FAIL — `WindowCaptureCoordinator` does not exist.

- [ ] **Step 4: Write the coordinator**

Create `src/FlaUI.Mcp.Core/Perception/WindowCaptureCoordinator.cs`:

```csharp
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
        _scrape = scrape ?? ScreenCapture.CaptureRectangle;
        _breaker = breaker;
        // Injected so the breaker's recovery behaviour is headless-testable; production uses the OS.
        _isHung = isHungProbe ?? IsHungAppWindow;
    }

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
                    // Step 9 is added in Task 18, which REPLACES this arm.
                    // ⚠ FOUR arguments. `WindowCaptureOutcome` carries the unmasked-process list and the
                    // escalations of the walk that produced the masks ACTUALLY PAINTED; on this path both
                    // come from the target's own walk. An earlier version passed two here and would not
                    // have compiled. *(AGY-AFTER round 7, Fold Auditor -- a defect in the driver's own
                    // round-7 fold, which updated Task 18's copies of this arm and missed Task 17's.)*
                    return new WindowCaptureOutcome(outcome.Result!, geo,
                                                    System.Array.Empty<string>(), geo.Escalations);

                case CaptureOutcomeKind.TimedOut:
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
        var desk = await _desktopMasks();
        var masks = desk.Rects;
        var unmasked = desk.UnmaskedProcesses;
        var escalations = desk.Escalations;

        return (_scrape(geo.Bounds, masks, maxWidth, scope,
                        ScreenCapture.Append(warnings, CaptureWarnings.For(code))), unmasked, escalations);
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
}
```

- [ ] **Step 5: Make `ScreenCapture.Append` visible to the coordinator**

It is `internal` and both types are in `FlaUI.Mcp.Core`, so no change is needed. Confirm with `dotnet build FlaUI.Mcp.slnx`.

- [ ] **Step 6: Confirm Task 12's call-site sweep is still green and still says 3**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~CaptureGeometryCallSiteTests"`
Expected: PASS, unchanged.

The coordinator takes the walk as a **delegate**, so it adds no call site to `ResolveWindowCaptureGeometryAsync` — the count is still `ScreenshotTools:53`, `PerceptionManager:1136` (OCR) and `PerceptionManager:1181` (full-desktop). **If this test went red, the coordinator was wired by calling `PerceptionManager` directly instead of through the injected delegate, which would also destroy every headless test in Tasks 17–19. Fix the wiring, not the number.**

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: PASS, 0 failed, build 0 warnings / 0 errors.

- [ ] **Step 8: Prove the gates are non-vacuous with three logic mutants**

1. Make `ScrapeAsync` use `geo.MaskRects` instead of the fresh desktop walk (i.e. undo round 6).
   Expected: `A_fallback_scrape_uses_FRESH_masks_not_the_stale_target_set` FAILS — and that failure is
   the whole justification for relaxing the resize refusal, so it is the one mutant in this task that
   must be seen to go red before the relaxation is trusted.
2. Change the degenerate branch to `throw` immediately instead of `continue`.
   Expected: `A_degenerate_W1_retries_rather_than_failing_terminally` FAILS.
3. Hoist `warnings` outside the `for` loop and accumulate into it across attempts.
   Expected: `A_discarded_attempts_warnings_are_discarded_with_it` FAILS.

**Revert every mutant.**

- [ ] **Step 9: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/WindowCaptureCoordinator.cs src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs test/FlaUI.Mcp.Tests/Perception/
git commit -m "feat(capture): the coordinator - bounded retry, ratified resize policy, scrape fallbacks"
```

### Task 18: The bookend validation walk

**This closes the relayout leak, and it is the newest and least-reviewed idea in the design.** It postdates all thirty panel rounds. Treat every claim in §2.5 as unproven.

**What it does NOT do:** it does not mitigate risk 3's stale composition. There the pixels are older than the tree and a value can be revealed in place, leaving the element's rectangle mathematically identical. `M1 == M2` and the walk passes a genuinely stale capture.

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/WindowCaptureCoordinator.cs`
- Test: `test/FlaUI.Mcp.Tests/Perception/BookendWalkTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class BookendWalkTests
{
    private static CaptureGeometry Geo(Rectangle w, params Rectangle[] masks)
        => new(w, masks, false, false, null, Array.Empty<MaskEscalationEntry>(),
               w, new IntPtr(0x1234), false);

    private static WindowCaptureCoordinator Make(List<CaptureGeometry> script, out Func<int> walkCount)
    {
        int i = 0;
        walkCount = () => i;
        var walk = new Func<WindowHandle, string?, Task<CaptureGeometry>>(
            (_, _) => Task.FromResult(script[Math.Min(i++, script.Count - 1)]));
        return new WindowCaptureCoordinator(walk, FakeWindowImageSource.Solid(Color.White),
            new CaptureRetryOptions(3, 1000),
            w2Probe: _ => new Rectangle(0, 0, 400, 300), minimizedProbe: _ => false,
            scrape: (b, m, mw, s, warns) => new CaptureResult(Array.Empty<byte>(), b.X, b.Y, b.Width,
                                                             b.Height, 1.0, m.Count, "screenScrape", warns));
    }

    private static readonly Rectangle W = new(0, 0, 400, 300);

    // THE PROPERTY THAT KEEPS THE COMMON CASE FREE. An empty mask set needs no second walk: there is
    // nothing that could have gone stale. Invisible if it breaks, so it is pinned.
    [Fact]
    public async Task An_empty_mask_set_performs_no_second_walk()
    {
        var c = Make(new List<CaptureGeometry> { Geo(W) }, out var walks);
        var r = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.Equal("printWindow", r.Result.CaptureMethod);
        Assert.Equal(1, walks());   // ONE walk, not two
    }

    [Fact]
    public async Task A_stable_mask_set_performs_a_second_walk_and_succeeds()
    {
        var mask = new Rectangle(10, 10, 50, 20);
        var c = Make(new List<CaptureGeometry> { Geo(W, mask), Geo(W, mask) }, out var walks);
        var r = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.Equal("printWindow", r.Result.CaptureMethod);
        Assert.Equal(2, walks());   // pre-capture + bookend
    }

    // THE LEAK THIS CLOSES. The window did not change size -- W1.Size == W2.Size throughout, so the
    // resize guard never fires -- but the content reflowed and the mask moved. Without the bookend the
    // stale rect is painted over the wrong region and sensitive content is exposed silently.
    [Fact]
    public async Task A_mask_that_moved_under_the_capture_triggers_a_retry()
    {
        var before = new Rectangle(10, 10, 50, 20);
        var after  = new Rectangle(10, 90, 50, 20);   // reflowed down, window size unchanged
        var c = Make(new List<CaptureGeometry>
        {
            Geo(W, before),   // attempt 1 pre-capture
            Geo(W, after),    // attempt 1 bookend  -> MISMATCH, retry
            Geo(W, after),    // attempt 2 pre-capture
            Geo(W, after),    // attempt 2 bookend  -> match, succeed
        }, out var walks);
        var r = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.Equal("printWindow", r.Result.CaptureMethod);
        Assert.Equal(4, walks());
    }

    // ⚠⚠ THE TEST THE WHOLE RELAXATION RESTS ON. Both scopes now fall back on resize exhaustion instead
    // of refusing, and that is only safe because the fallback re-measures its masks at capture time. If
    // this ever goes green while `ScrapeAsync` uses the stale target set, the relaxation is a leak.
    [Fact]
    public async Task A_fallback_scrape_uses_FRESH_masks_not_the_stale_target_set()
    {
        var w1 = new Rectangle(0, 0, 800, 600);
        var staleMask = new Rectangle(10, 10, 50, 20);
        var freshMask = new Rectangle(400, 300, 60, 25);
        IReadOnlyList<Rectangle> painted = Array.Empty<Rectangle>();

        var c = new WindowCaptureCoordinator(
            (_, _) => Task.FromResult(new CaptureGeometry(w1, new[] { staleMask }, false, false, null,
                Array.Empty<MaskEscalationEntry>(), w1, new IntPtr(0x1234), false)),
            FakeWindowImageSource.Solid(Color.White), new CaptureRetryOptions(2, 1000),
            w2Probe: _ => new Rectangle(0, 0, 900, 600), minimizedProbe: _ => false,
            scrape: (b, m, mw, s, warns) => { painted = m; return new CaptureResult(Array.Empty<byte>(),
                b.X, b.Y, b.Width, b.Height, 1.0, m.Count, "screenScrape", warns); },
            denylistedVisible: () => Task.FromResult(false),
            desktopMasks: () => Task.FromResult(new DesktopMaskSet(
                new[] { freshMask }, Array.Empty<MaskEscalationEntry>(), Array.Empty<string>())));

        await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);

        Assert.Equal(new[] { freshMask }, painted);
        Assert.DoesNotContain(staleMask, painted);
    }

    // ⚠ A PURE MOVE MUST NOT TRIP IT. Mask rects are ABSOLUTE screen coordinates, so a user dragging the
    // window between the two walks shifts every one of them. Compared absolutely, that reads as a total
    // relayout and a harmless drag becomes a refusal -- contradicting the design's rule, stated in three
    // places, that a pure move is harmless. The comparison is WINDOW-RELATIVE for exactly this reason.
    [Fact]
    public async Task A_pure_window_move_between_the_two_walks_does_not_trip_the_bookend()
    {
        var at0   = new Rectangle(0, 0, 400, 300);
        var at500 = new Rectangle(500, 250, 400, 300);      // dragged, SAME size
        // The mask sits at the same place INSIDE the window in both walks.
        var before = new CaptureGeometry(at0, new[] { new Rectangle(10, 10, 50, 20) }, false, false, null,
            Array.Empty<MaskEscalationEntry>(), at0, new IntPtr(0x1234), false);
        var after = new CaptureGeometry(at500, new[] { new Rectangle(510, 260, 50, 20) }, false, false, null,
            Array.Empty<MaskEscalationEntry>(), at500, new IntPtr(0x1234), false);

        int i = 0;
        var script = new[] { before, after };
        var c = new WindowCaptureCoordinator(
            (_, _) => Task.FromResult(script[Math.Min(i++, script.Length - 1)]),
            FakeWindowImageSource.Solid(Color.White), new CaptureRetryOptions(3, 1000),
            w2Probe: _ => at0, minimizedProbe: _ => false,
            scrape: (b, m, mw, s, warns) => new CaptureResult(Array.Empty<byte>(), b.X, b.Y, b.Width,
                                                             b.Height, 1.0, m.Count, "screenScrape", warns));

        var r = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.Equal("printWindow", r.Result.CaptureMethod);   // NOT a refusal
    }

    // ⚠ A BOOKEND WALK THAT THROWS IS A FAILED CONFIRMATION, NOT AN ESCAPING ERROR. Unguarded it escaped
    // CaptureAsync and discarded a good image already in hand. It is now treated as a MISMATCH -- retry,
    // then the bookend's own terminal outcome.
    //
    // The walk here throws ElementNotActionable while the coordinator's refusal is RedactionUnmaskable,
    // so this proves the coordinator's OWN defined outcome surfaces rather than the walk's exception
    // simply passing through. Those two being the same code would make this test vacuous.
    [Fact]
    public async Task A_bookend_walk_that_throws_becomes_the_defined_refusal_not_a_passthrough()
    {
        var w1 = new Rectangle(0, 0, 400, 300);
        var e  = new Rectangle(50, 50, 100, 80);
        var mask = new Rectangle(60, 60, 20, 10);
        int call = 0;
        var walk = new Func<WindowHandle, string?, Task<CaptureGeometry>>((_, _) =>
        {
            call++;
            // Odd calls are the pre-capture walk; even calls are the bookend, which always throws.
            if (call % 2 == 0)
                throw new ToolException(ToolErrorCode.ElementNotActionable,
                    "the window vanished mid-confirmation", "re-list windows and retry");
            return Task.FromResult(new CaptureGeometry(e, new[] { mask }, false, false, null,
                Array.Empty<MaskEscalationEntry>(), w1, new IntPtr(0x1234), false));
        });
        var c = new WindowCaptureCoordinator(walk, FakeWindowImageSource.Solid(Color.White),
            new CaptureRetryOptions(2, 1000),
            w2Probe: _ => w1, minimizedProbe: _ => false,
            scrape: (b, m, mw, s, warns) => new CaptureResult(Array.Empty<byte>(), b.X, b.Y, b.Width,
                                                             b.Height, 1.0, m.Count, "screenScrape", warns));

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            c.CaptureAsync(new WindowHandle("w1"), "e5", CaptureScope.Element, 0));
        // The coordinator's OWN outcome, not the walk's exception passing through.
        Assert.Equal(ToolErrorCode.RedactionUnmaskable, ex.Code);
    }

    // ⚠ A BOOKEND EXHAUSTION REFUSES ON ELEMENT SCOPE TOO -- unlike a RESIZE exhaustion, which falls back
    // to the scrape. The difference is what the failure PROVES: a resize proves a geometry mismatch and
    // says nothing about the masks, while a bookend mismatch is direct evidence the mask set MOVED.
    // Scraping with rects we have proven stale would ship an under-redacted image.
    [Fact]
    public async Task A_moving_mask_set_refuses_for_ELEMENT_scope_too_and_never_scrapes()
    {
        int n = 0;
        var e = new Rectangle(50, 50, 100, 80);
        var walk = new Func<WindowHandle, string?, Task<CaptureGeometry>>(
            (_, _) => Task.FromResult(new CaptureGeometry(e,
                new[] { new Rectangle(60, 60 + (n++ * 7), 20, 10) }, false, false, null,
                Array.Empty<MaskEscalationEntry>(), W, new IntPtr(0x1234), false)));
        bool scraped = false;
        var c = new WindowCaptureCoordinator(walk, FakeWindowImageSource.Solid(Color.White),
            new CaptureRetryOptions(3, 1000),
            w2Probe: _ => W, minimizedProbe: _ => false,
            scrape: (b, m, mw, s, warns) => { scraped = true; return new CaptureResult(Array.Empty<byte>(),
                b.X, b.Y, b.Width, b.Height, 1.0, m.Count, "screenScrape", warns); });

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            c.CaptureAsync(new WindowHandle("w1"), "e5", CaptureScope.Element, 0));
        Assert.Equal(ToolErrorCode.RedactionUnmaskable, ex.Code);
        Assert.False(scraped, "a proven-stale mask set must never be painted onto a scrape");
    }

    // A window whose masked content animates continuously never settles. Window scope with masks refuses
    // -- the SAME terminal outcome as the resize case, reached by the other detector.
    [Fact]
    public async Task A_continuously_moving_mask_set_refuses_for_window_scope()
    {
        int n = 0;
        var walk = new Func<WindowHandle, string?, Task<CaptureGeometry>>(
            (_, _) => Task.FromResult(Geo(W, new Rectangle(10, 10 + (n++ * 7), 50, 20))));
        var c = new WindowCaptureCoordinator(walk, FakeWindowImageSource.Solid(Color.White),
            new CaptureRetryOptions(3, 1000),
            w2Probe: _ => W, minimizedProbe: _ => false,
            scrape: (b, m, mw, s, warns) => new CaptureResult(Array.Empty<byte>(), b.X, b.Y, b.Width,
                                                             b.Height, 1.0, m.Count, "screenScrape", warns));
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0));
        Assert.Equal(ToolErrorCode.RedactionUnmaskable, ex.Code);
    }

    // ⚠ THE PROPERTY THAT KEEPS IT A GUARD RATHER THAN AN OUTAGE. A bookend that reports a difference for
    // a window nobody touched would refuse every masked capture on the machine. This is ratification item
    // 5's added measurement, expressed as a test.
    [Fact]
    public async Task A_static_window_never_trips_the_bookend()
    {
        var mask = new Rectangle(10, 10, 50, 20);
        var script = new List<CaptureGeometry>();
        for (int i = 0; i < 10; i++) script.Add(Geo(W, mask));
        var c = Make(script, out var walks);
        for (int i = 0; i < 5; i++)
        {
            var r = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
            Assert.Equal("printWindow", r.Result.CaptureMethod);
        }
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~BookendWalkTests"`
Expected: FAIL — `An_empty_mask_set_performs_no_second_walk` passes trivially, the rest fail because no second walk happens.

- [ ] **Step 3: Add step 9 to the coordinator**

Replace the `case CaptureOutcomeKind.Completed:` arm in `CaptureAsync` with:

```csharp
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
                    // A bookend mismatch is DIRECT EVIDENCE that the mask set moved under the capture.
                    // Falling back would scrape the window and paint those same rects -- rects we have
                    // just PROVEN are stale -- producing an under-redacted image. That is the failure
                    // class SP4 exists to close, and the argument is the identical one that makes window
                    // scope refuse rather than scrape.
                    //
                    // Note this needs no mask-set test: the bookend only runs when M1 is non-empty.
                    throw new ToolException(ToolErrorCode.RedactionUnmaskable,
                        "The window's redacted regions kept moving during the capture, so they cannot be " +
                        "reliably located in the image.",
                        "wait for the window to settle, then retry, or capture a different window");
                }
```

Add the comparison as a private static member:

```csharp
    /// <summary>M1 vs M2, as an ORDERED SEQUENCE of WINDOW-RELATIVE rectangles.
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
    /// Ordered rather than as a set because the walk is deterministic: it enumerates roots and descendants
    /// in a fixed order, so a REORDERING is itself evidence the tree changed under the capture.
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
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~BookendWalkTests"`
Expected: PASS — 5 passed.

- [ ] **Step 5: Prove the gates are non-vacuous with two logic mutants**

1. Change `MaskSetsMatch` to `=> true`.
   Expected: `A_mask_that_moved_under_the_capture_triggers_a_retry` FAILS at `Assert.Equal(4, walks())` with 2 — the leak is open again, and seeing this fail once is the point.
2. Delete the `if (geo.MaskRects.Count == 0)` short circuit.
   Expected: `An_empty_mask_set_performs_no_second_walk` FAILS with 2 walks.

**Revert both.**

- [ ] **Step 6: Run the whole headless suite**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: PASS, 0 failed, build 0/0.

- [ ] **Step 7: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/WindowCaptureCoordinator.cs test/FlaUI.Mcp.Tests/Perception/BookendWalkTests.cs
git commit -m "feat(capture): the bookend validation walk - closes the relayout leak (ratification 4)

Closes the leak that made the spec's 'none is paid in a leak' claim false:
a window can reflow at a constant outer size, so the resize guard never
fires and stale masks land on the wrong regions. Compares the mask list
before and after the capture; empty mask set skips it entirely.

Does NOT cover risk 3's stale composition - a value revealed IN PLACE
leaves the rect identical. That risk remains unmitigated and one-sided."
```

### Task 19: The per-HWND circuit breaker

**Build this only if Task 1 measured a block.** If it did not, skip the task and record the skip in the measurements doc.

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/WindowCaptureCoordinator.cs`
- Test: `test/FlaUI.Mcp.Tests/Perception/CaptureCircuitBreakerTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class CaptureCircuitBreakerTests
{
    private static readonly Rectangle W = new(0, 0, 400, 300);

    private static CaptureGeometry Geo() =>
        new(W, Array.Empty<Rectangle>(), false, false, null, Array.Empty<MaskEscalationEntry>(),
            W, new IntPtr(0xBEEF), false);

    // N captures of a hung window must cost ONE leaked acquisition, not N. This CONTAINS the cumulative
    // degradation; it does NOT reclaim anything -- the first blocked call still holds its thread, its HDC
    // and both bitmaps forever. Only killing a separate process reclaims, and that is ROADMAP debt.
    [Fact]
    public async Task A_window_that_timed_out_is_not_retried_through_PrintWindow_during_the_cooldown()
    {
        var src = FakeWindowImageSource.TimesOut();
        var breaker = new CaptureCircuitBreaker(cooldown: TimeSpan.FromMinutes(5), clock: () => DateTime.UtcNow);
        var c = new WindowCaptureCoordinator(
            (_, _) => Task.FromResult(Geo()), src, new CaptureRetryOptions(1, 50),
            w2Probe: _ => W, minimizedProbe: _ => false,
            scrape: (b, m, mw, s, warns) => new CaptureResult(Array.Empty<byte>(), b.X, b.Y, b.Width,
                                                             b.Height, 1.0, 0, "screenScrape", warns),
            breaker: breaker);

        for (int i = 0; i < 5; i++)
        {
            var r = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
            Assert.Equal("screenScrape", r.Result.CaptureMethod);
            Assert.Contains(r.Result.CaptureWarnings, w => w.Code == "scrapeFallbackTargetUnresponsive");
        }

        Assert.Equal(1, src.Calls);   // ONE leak, not five
    }

    [Fact]
    public async Task The_breaker_reopens_after_the_cooldown()
    {
        var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var src = FakeWindowImageSource.TimesOut();
        var breaker = new CaptureCircuitBreaker(TimeSpan.FromMinutes(5), () => now);
        var c = new WindowCaptureCoordinator(
            (_, _) => Task.FromResult(Geo()), src, new CaptureRetryOptions(1, 50),
            w2Probe: _ => W, minimizedProbe: _ => false,
            scrape: (b, m, mw, s, warns) => new CaptureResult(Array.Empty<byte>(), b.X, b.Y, b.Width,
                                                             b.Height, 1.0, 0, "screenScrape", warns),
            breaker: breaker);

        await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.Equal(1, src.Calls);
        await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.Equal(1, src.Calls);           // still tripped

        now = now.AddMinutes(6);
        await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.Equal(2, src.Calls);           // reopened, and leaked once more
    }

    // ⚠ THE DICTIONARY MUST NOT GROW WITHOUT BOUND. Every window that ever hung would otherwise leave a
    // permanent entry in a server designed to run for weeks — and because the OS RECYCLES HWND values, a
    // stale entry can mis-trip the breaker for an unrelated window that reuses the handle.
    [Fact]
    public void Expired_entries_are_pruned_so_the_breaker_does_not_grow_without_bound()
    {
        var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var breaker = new CaptureCircuitBreaker(TimeSpan.FromMinutes(5), () => now);

        for (int i = 1; i <= 50; i++) breaker.Trip(new IntPtr(i));
        Assert.Equal(50, breaker.TrackedCount);

        now = now.AddMinutes(6);          // every existing entry is now expired
        breaker.Trip(new IntPtr(9999));   // pruning happens on write
        Assert.Equal(1, breaker.TrackedCount);
        Assert.True(breaker.IsTripped(new IntPtr(9999)));
        Assert.False(breaker.IsTripped(new IntPtr(1)));
    }

    // ⚠⚠ THE BREAKER MUST NOT SMUGGLE A DEAD TARGET PAST THE GUARDS. A hung window trips the breaker,
    // then minimizes. Short-circuiting straight to the scrape skips canonical steps 4-5, so the scrape
    // photographs the rectangle the window USED to occupy and returns whatever is now behind it -- as a
    // success. That is the exact defect item 8 exists to remove, reintroduced by the containment added
    // for a different problem.
    [Fact]
    public async Task A_tripped_breaker_still_refuses_a_window_that_minimized()
    {
        var W = new Rectangle(0, 0, 400, 300);
        var src = FakeWindowImageSource.TimesOut();
        var breaker = new CaptureCircuitBreaker(TimeSpan.FromMinutes(5), () => DateTime.UtcNow);
        bool minimized = false;
        var c = new WindowCaptureCoordinator(
            (_, _) => Task.FromResult(new CaptureGeometry(W, Array.Empty<Rectangle>(), false, false, null,
                Array.Empty<MaskEscalationEntry>(), W, new IntPtr(0xBEEF), false)),
            src, new CaptureRetryOptions(1, 50),
            w2Probe: _ => W, minimizedProbe: _ => minimized,
            scrape: (b, m, mw, s, warns) => new CaptureResult(Array.Empty<byte>(), b.X, b.Y, b.Width,
                                                             b.Height, 1.0, 0, "screenScrape", warns),
            breaker: breaker);

        // First capture times out and trips the breaker.
        await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.Equal(1, src.Calls);

        // Now the window minimizes. The breaker is tripped, so PrintWindow is skipped -- but the target
        // is gone from the screen and the scrape must NOT run.
        minimized = true;
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0));
        Assert.Equal(ToolErrorCode.ElementNotActionable, ex.Code);
        Assert.Equal(1, src.Calls);   // still short-circuited; it refused rather than scraping
    }

    // ⚠⚠ THE BREAKER MUST BOUND CONCURRENT CAPTURES, NOT ONLY SERIALIZED ONES. Trip() runs AFTER a
    // timeout elapses, so overlapping requests for the same hung window would all read IsTripped as
    // false, all call Acquire, all block, and all leak -- N threads and N bitmaps for one window,
    // defeating the containment this class exists to provide.
    [Fact]
    public async Task Overlapping_captures_of_one_hung_window_cost_ONE_acquisition_not_N()
    {
        var W = new Rectangle(0, 0, 400, 300);
        // Blocks until released, so all three requests genuinely overlap.
        var gate = new System.Threading.ManualResetEventSlim(false);
        int calls = 0;
        var src = new FakeWindowImageSource(_ =>
        {
            System.Threading.Interlocked.Increment(ref calls);
            gate.Wait(5000);
            return null;                       // then reports a timeout
        });

        var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var breaker = new CaptureCircuitBreaker(TimeSpan.FromMinutes(5), () => now);
        var c = new WindowCaptureCoordinator(
            (_, _) => Task.FromResult(new CaptureGeometry(W, Array.Empty<Rectangle>(), false, false, null,
                Array.Empty<MaskEscalationEntry>(), W, new IntPtr(0xBEEF), false)),
            src, new CaptureRetryOptions(1, 50),
            w2Probe: _ => W, minimizedProbe: _ => false,
            scrape: (b, m, mw, s, warns) => new CaptureResult(Array.Empty<byte>(), b.X, b.Y, b.Width,
                                                             b.Height, 1.0, 0, "screenScrape", warns),
            breaker: breaker);

        var first = c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        // Let the first acquisition outlive the whole timeout budget, so it is KNOWN stuck.
        while (System.Threading.Volatile.Read(ref calls) == 0) await Task.Yield();
        now = now.AddMilliseconds(500);

        var second = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        var third  = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);

        Assert.Equal("screenScrape", second.Result.CaptureMethod);
        Assert.Equal("screenScrape", third.Result.CaptureMethod);
        Assert.Equal(1, System.Threading.Volatile.Read(ref calls));   // ONE acquisition, not three

        gate.Set();
        await first;
    }

    [Fact]
    public async Task A_different_window_is_unaffected()
    {
        var src = FakeWindowImageSource.TimesOut();
        var breaker = new CaptureCircuitBreaker(TimeSpan.FromMinutes(5), () => DateTime.UtcNow);
        int hwnd = 1;
        var c = new WindowCaptureCoordinator(
            (_, _) => Task.FromResult(new CaptureGeometry(W, Array.Empty<Rectangle>(), false, false, null,
                Array.Empty<MaskEscalationEntry>(), W, new IntPtr(hwnd), false)),
            src, new CaptureRetryOptions(1, 50),
            w2Probe: _ => W, minimizedProbe: _ => false,
            scrape: (b, m, mw, s, warns) => new CaptureResult(Array.Empty<byte>(), b.X, b.Y, b.Width,
                                                             b.Height, 1.0, 0, "screenScrape", warns),
            breaker: breaker);

        await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        hwnd = 2;
        await c.CaptureAsync(new WindowHandle("w2"), null, CaptureScope.Window, 0);
        Assert.Equal(2, src.Calls);   // the breaker is PER-HWND
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~CaptureCircuitBreakerTests"`
Expected: FAIL — `CaptureCircuitBreaker` does not exist.

- [ ] **Step 3: Write the breaker**

Append to `src/FlaUI.Mcp.Core/Perception/WindowCaptureCoordinator.cs`:

```csharp
/// <summary>Remembers windows whose PrintWindow acquisition timed out, and routes subsequent captures of
/// those windows straight to the scrape for a cooldown.
///
/// ⚠ THIS CONTAINS; IT DOES NOT RECLAIM. Each blocked call keeps its thread, its HDC, its GDI bitmap and
/// its managed bitmap permanently -- a blocked Win32 call cannot be cancelled, so nothing in-process can
/// take them back. What this bounds is the MULTIPLIER: N captures of a hung window cost ONE leak instead
/// of N. Operator ratification of 2026-08-21 accepted that trade explicitly; the out-of-process worker
/// that would actually reclaim is filed as ROADMAP debt.</summary>
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
```

- [ ] **Step 4: Wire it into the coordinator**

Add a `CaptureCircuitBreaker? breaker = null` constructor parameter stored as `_breaker`, then:

Before the `ScreenCapture.CaptureWindow` call:

```csharp
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
```

In the `TimedOut` arm, before returning:

```csharp
                case CaptureOutcomeKind.TimedOut:
                    _breaker?.Trip(geo.NativeWindowHandle);
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~CaptureCircuitBreakerTests"`
Expected: PASS — 3 passed.

- [ ] **Step 6: Prove the gate is non-vacuous with a logic mutant**

Change `IsTripped` to `=> false`.
Expected: `A_window_that_timed_out_is_not_retried_through_PrintWindow_during_the_cooldown` FAILS with `src.Calls == 5` — five leaks instead of one. **Revert.**

- [ ] **Step 7: File the out-of-process worker as ROADMAP debt**

Append to `ROADMAP.md`:

```markdown
### 17. A hung-window `PrintWindow` capture leaks permanently — the containments bound it, nothing reclaims it

`PrintWindow` sends `WM_PRINT` synchronously to the target, so a target whose message loop is blocked
blocks the call, and a blocked Win32 call cannot be cancelled. Item 8 ships two CONTAINMENTS — a dedicated
background thread so the leak is a thread rather than a CLR threadpool slot, and a per-HWND circuit
breaker so N captures of a hung window cost one leak rather than N. **Neither reclaims anything:** the
blocked call keeps its thread, its HDC, its GDI bitmap and its managed bitmap until the process exits.

The fix that DOES reclaim is running the acquisition in a sacrificial out-of-process worker terminated on
timeout, letting the OS take the handles back. It costs IPC, bitmap serialization across a process
boundary, child-process lifetime management, and a second DPI-aware CLR process that must be on the right
desktop and session. Deliberately not built in item 8 — see that spec's ratification item 3, where the
operator accepted the containments and staged this.

Build it if the containments prove insufficient in practice.
```

- [ ] **Step 8: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/WindowCaptureCoordinator.cs test/FlaUI.Mcp.Tests/Perception/CaptureCircuitBreakerTests.cs ROADMAP.md
git commit -m "feat(capture): per-HWND circuit breaker; file the out-of-process worker as ROADMAP item 17"
```

---
