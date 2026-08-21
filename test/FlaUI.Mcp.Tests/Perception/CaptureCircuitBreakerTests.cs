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
}
