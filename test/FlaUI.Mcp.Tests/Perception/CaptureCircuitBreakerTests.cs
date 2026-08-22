using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Errors;
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
            breaker: breaker,
            isHungProbe: _ => true,
            denylistedVisible: () => Task.FromResult(false),
            desktopMasks: () => Task.FromResult(new DesktopMaskSet(
                Array.Empty<Rectangle>(), Array.Empty<MaskEscalationEntry>(), Array.Empty<string>())));

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
            breaker: breaker,
            isHungProbe: _ => true,
            denylistedVisible: () => Task.FromResult(false),
            desktopMasks: () => Task.FromResult(new DesktopMaskSet(
                Array.Empty<Rectangle>(), Array.Empty<MaskEscalationEntry>(), Array.Empty<string>())));

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
            breaker: breaker,
            isHungProbe: _ => true,
            denylistedVisible: () => Task.FromResult(false),
            desktopMasks: () => Task.FromResult(new DesktopMaskSet(
                Array.Empty<Rectangle>(), Array.Empty<MaskEscalationEntry>(), Array.Empty<string>())));

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

    // ⛔⛔ THIS TEST IS SEQUENTIAL, NOT CONCURRENT, AND ITS NAME SAID OTHERWISE. It was called
    // `Overlapping_captures_of_one_hung_window_cost_ONE_acquisition_not_N`, under a comment claiming it
    // proved the breaker "must bound CONCURRENT captures, not only SERIALIZED ones".
    //
    // The requests below do not overlap. `CaptureAsync` has no true async yield before `source.Acquire`
    // -- `_walk` returns an already-completed `Task.FromResult` and `CaptureWindow` is synchronous -- so
    // `var first = c.CaptureAsync(...)` BLOCKS this thread inside the fake's `gate.Wait(5000)` and does
    // not return until it finishes. `second` and `third` therefore run strictly AFTER `first` completed,
    // and they divert because `Trip()` has already run and `IsTripped` is true.
    //
    // MEASURED: making `AnotherAcquisitionIsStuck` return a constant `false` leaves ALL SEVEN tests in
    // this file green. Nothing here exercises the concurrent guard at all.
    // *(AGY-CAPSTONE round 3, finding 1.)*
    //
    // What it DOES prove, which is worth keeping: SEQUENTIAL captures of a window that has already timed
    // out are bounded to one acquisition. For the concurrent reality see `BreakerConcurrencyProbe`, which
    // measures five overlapping requests producing FIVE acquisitions under the production clock.
    [Fact]
    public async Task Sequential_captures_after_a_timeout_cost_ONE_acquisition_not_N()
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
            breaker: breaker,
            isHungProbe: _ => true,
            denylistedVisible: () => Task.FromResult(false),
            desktopMasks: () => Task.FromResult(new DesktopMaskSet(
                Array.Empty<Rectangle>(), Array.Empty<MaskEscalationEntry>(), Array.Empty<string>())));

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
            breaker: breaker,
            isHungProbe: _ => true,
            denylistedVisible: () => Task.FromResult(false),
            desktopMasks: () => Task.FromResult(new DesktopMaskSet(
                Array.Empty<Rectangle>(), Array.Empty<MaskEscalationEntry>(), Array.Empty<string>())));

        await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        hwnd = 2;
        await c.CaptureAsync(new WindowHandle("w2"), null, CaptureScope.Window, 0);
        Assert.Equal(2, src.Calls);   // the breaker is PER-HWND
    }

    // ⚠⚠ THE RECOVERY PATH, AND IT WAS COMPLETELY UNTESTED UNTIL THIS TEST EXISTED.
    //
    // MEASURED: deleting `_breaker?.Reset(hwnd)` from `WindowCaptureCoordinator.HungOrReset` -- the whole
    // recovery mechanism -- left the full headless suite GREEN at 1036/1036. Nothing anywhere caught it.
    //
    // That gap is not academic: an AGY-AFTER panel round already caught this exact member being DEAD once
    // before. The comment on `HungOrReset` records it -- *"`Reset` existed and was never called until
    // round 6 -- the prose claimed 'a recovered window RESETS the breaker and takes the normal path'
    // while nothing performed the reset."* Review caught it that time; without this test, nothing would
    // catch it regressing.
    //
    // The gap was OPENED by the fix that made this suite runnable. Every other test here passes
    // `isHungProbe: _ => true`, because the pinned plan block wired no probe at all and the real
    // `IsHungAppWindow` P/Invoke against a synthetic handle returns FALSE -- which made `HungOrReset`
    // reset the breaker on every check and defeated the diversion the tests exist to prove. Forcing the
    // probe TRUE fixed that and, in doing so, removed the only route any test had to the false branch.
    //
    // Why the state assertion rather than a method assertion: with the Reset deleted, `HungOrReset` still
    // returns false, so the capture still takes the PrintWindow path and `CaptureMethod` is identical
    // either way. What differs is that the breaker keeps a stale entry for a window that recovered -- and
    // per `Trip`'s own comment, "HWNDs are recycled by the OS, so a stale entry can also mis-trip the
    // breaker for an unrelated window that happens to reuse the handle value."
    [Fact]
    public async Task A_recovered_window_RESETS_the_breaker_rather_than_serving_out_the_cooldown()
    {
        var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        int calls = 0;
        // Times out ONCE -- tripping the breaker -- then renders normally, as a recovered window would.
        var src = new FakeWindowImageSource(size =>
        {
            if (++calls == 1) return null;
            var b = new Bitmap(size.Width, size.Height);
            using var g = Graphics.FromImage(b);
            using var bg = new SolidBrush(Color.White);
            g.FillRectangle(bg, 0, 0, b.Width, b.Height);
            using var fg = new SolidBrush(Color.Black);
            g.FillRectangle(fg, 0, 0, b.Width / 2, b.Height);
            return b;
        });

        var breaker = new CaptureCircuitBreaker(TimeSpan.FromMinutes(5), () => now);
        bool hung = true;
        var c = new WindowCaptureCoordinator(
            (_, _) => Task.FromResult(Geo()), src, new CaptureRetryOptions(1, 50),
            w2Probe: _ => W, minimizedProbe: _ => false,
            scrape: (b, m, mw, s, warns) => new CaptureResult(Array.Empty<byte>(), b.X, b.Y, b.Width,
                                                             b.Height, 1.0, 0, "screenScrape", warns),
            breaker: breaker,
            isHungProbe: _ => hung,
            denylistedVisible: () => Task.FromResult(false),
            desktopMasks: () => Task.FromResult(new DesktopMaskSet(
                Array.Empty<Rectangle>(), Array.Empty<MaskEscalationEntry>(), Array.Empty<string>())));

        var first = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);
        Assert.Equal("screenScrape", first.Result.CaptureMethod);
        Assert.True(breaker.IsTripped(new IntPtr(0xBEEF)), "the timeout must trip the breaker");

        // The OS now reports the window as healthy again, WELL INSIDE the five-minute cooldown.
        hung = false;
        var second = await c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0);

        Assert.Equal("printWindow", second.Result.CaptureMethod);
        Assert.False(breaker.IsTripped(new IntPtr(0xBEEF)),
            "a recovered window must be FORGOTTEN, not left tripped until the cooldown expires");
        Assert.Equal(0, breaker.TrackedCount);
    }
}
