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

        // ⚠ THIS CONSTRUCTION DELIBERATELY WIRES NEITHER `denylistedVisible` NOR `desktopMasks`, unlike
        // the one above, and that is load-bearing rather than an oversight: the bookend refusal fires
        // BEFORE `ScrapeAsync` is ever entered, so the fallback dependencies are genuinely unreachable
        // here -- which `Assert.False(scraped)` is what proves. Do not add them.
        //
        // ⚠ But read the failure carefully if this test ever goes red. Should the bookend STOP firing,
        // the capture would fall through to the scrape and die on the fail-closed wiring guard, so the
        // test would report `CaptureUnavailable` ("the denylist guard is not wired") instead of the
        // absent `RedactionUnmaskable`. That message would be describing the SECOND consequence of the
        // defect, not the defect. The real failure is always "the bookend did not refuse".
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
