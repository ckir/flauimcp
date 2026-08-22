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
                                     1.0, masks.Count, "screenScrape", warns),
               // ⚠⚠ BOTH OF THESE ARE MANDATORY ON ANY TEST THAT CAN REACH A FALLBACK, and omitting them
               // is not a silent under-test -- it is a hard refusal. `ScrapeAsync` FAILS CLOSED on each:
               // an absent `denylistedVisible` or `desktopMasks` throws `CaptureUnavailable` rather than
               // proceeding, because an optional dependency whose absence is indistinguishable from a
               // pass is exactly the defect that made the denylist guard inert for a whole panel round.
               //
               // MEASURED: this helper originally wired neither, and SIX of the eleven tests below died
               // with "the denylist guard is not wired" before any assertion ran. The guards are correct;
               // the helper was the defect. Do not "fix" a future failure here by relaxing either guard.
               denylistedVisible: () => Task.FromResult(false),
               desktopMasks: () => Task.FromResult(new DesktopMaskSet(
                   Array.Empty<Rectangle>(), Array.Empty<MaskEscalationEntry>(), Array.Empty<string>())));

    [Fact]
    public async Task A_static_window_captures_on_the_first_attempt()
    {
        var w = new Rectangle(0, 0, 400, 300);
        // ⚠ `Varied()`, NOT `Solid()`. `Solid` paints one colour plus a 4x4 marker, and
        // `UniformCanvasDetector` samples a 64x64 GRID -- whose sample points step ~6px across a 400px
        // window and so never land on a 4x4 marker. A `Solid` window is therefore UNIFORM as far as the
        // detector is concerned, and this capture emits `uniformCanvas`, which is correct behaviour and
        // would make the empty-warnings assertion below fail. A healthy window is not a blank one.
        var c = Make(Walk(Geo(w, w)), FakeWindowImageSource.Varied(Color.White), _ => w);
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
        // `Varied`, not `Solid` -- see the note in the first test: a `Solid` window reads as uniform to
        // the 64x64 grid and would emit `uniformCanvas`, defeating the empty-warnings assertion.
        var c = Make(Walk(Geo(stale, stale, mask), Geo(settled, settled, mask)),
                     FakeWindowImageSource.Varied(Color.White), _ => settled);
        var r = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.Equal("printWindow", r.Result.CaptureMethod);
        Assert.Empty(r.Result.CaptureWarnings);   // it settled, so nothing about it is stale
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

        // ⚠ THE NAME SAYS "fresh masks" AND NOTHING USED TO CHECK IT. `Make` injects a `desktopMasks`
        // delegate returning an EMPTY set while the walk's geometry carries one mask, so the painted
        // count distinguishes them: 0 means the fresh desktop set was used, 1 means the stale target set
        // was. Without this, swapping `desk.Rects` for `geo.MaskRects` left both this test and its
        // element-scope twin green. *(AGY-TEST-AUDIT, gap 1 residual.)*
        Assert.Equal(0, r.Result.Redactions);
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
                b.X, b.Y, b.Width, b.Height, 1.0, m.Count, "screenScrape", warns); },
            // Same two mandatory fallback dependencies as `Make` -- this test builds the coordinator
            // directly, so it does not inherit them. See the note in `Make`.
            denylistedVisible: () => Task.FromResult(false),
            desktopMasks: () => Task.FromResult(new DesktopMaskSet(
                Array.Empty<Rectangle>(), Array.Empty<MaskEscalationEntry>(), Array.Empty<string>())));

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
