using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Tests.Perception;   // FakeWindowImageSource lives there
using Xunit;

namespace FlaUI.Mcp.Tests.Capture;

/// <summary>Task 25 Step 4b. Times the fallback's desktop mask walk, which grew expensive across panel
/// rounds 6-9 and which nobody had measured.
///
/// ⚠⚠ DUAL-TRAIT ON PURPOSE, so it runs in NEITHER gate. The headless filter excludes `Category=Desktop`
/// and the Desktop filter excludes `Category=Measurement`, so this is reachable only by naming it. A
/// timing probe that needs a live desktop must not be able to fail a release gate on a busy machine, and
/// `Measurement` alone would NOT have kept it out of the headless run — measured earlier in this project,
/// a Measurement-only test still ran there (970 -> 978).
///
/// ⚠ THERE IS NO PASS/FAIL THRESHOLD, DELIBERATELY. The walk cannot be bounded without scraping a PARTIAL
/// mask set, which is the leak it exists to close. The honest options are "pay it" or "refuse", so the
/// number informs the tool description's wording and an operator decision, not a gate.</summary>
[Trait("Category", "Desktop")]
[Trait("Category", "Measurement")]
public class FallbackCostMeasurement : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public FallbackCostMeasurement(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Measure_the_fallback_end_to_end()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());

        // Characterise the desktop, so the numbers below can be read in context rather than as absolutes.
        var windows = await mgr.ListWindowsAsync();
        var visible = windows.Count;

        // Warm up: the first UIA walk pays one-off binding costs that are not part of a steady-state
        // fallback. Same reasoning as the GDI gate's warm-up in Task 23.
        await perception.AllMaskRectsAsync();
        await perception.DenylistedWindowsVisibleAsync();

        var sw = Stopwatch.StartNew();
        await perception.AllMaskRectsAsync();
        var walkMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        await perception.DenylistedWindowsVisibleAsync();
        await perception.DenylistedWindowsVisibleAsync();
        var denyMs = sw.Elapsed.TotalMilliseconds;

        // THE FALLBACK, END TO END. The timeout is staged with a source that reports a timeout
        // immediately rather than a really-hung window: what is being measured is the DESKTOP WALK the
        // fallback performs, which is identical either way, and a real hang would leak a blocked thread
        // plus 3 GDI objects for the lifetime of the hung process (measured in Phase 0, risk 2b). The
        // wait for the timeout itself is a known constant, added below rather than paid here.
        var coordinator = new WindowCaptureCoordinator(
            (h, r) => perception.ResolveWindowCaptureGeometryAsync(h, r),
            FakeWindowImageSource.TimesOut(),
            CaptureRetryOptions.Default,
            denylistedVisible: () => perception.DenylistedWindowsVisibleAsync(),
            desktopMasks: () => perception.AllMaskRectsAsync());

        sw.Restart();
        var outcome = await coordinator.CaptureAsync(handle, null, CaptureScope.Window, 0);
        var fallbackMs = sw.Elapsed.TotalMilliseconds;

        Assert.Equal("screenScrape", outcome.Result.CaptureMethod);

        // THE BOOKEND'S COST (Task 25 Step 5). The bookend walk repeats the TARGET-window geometry
        // walk once per successful capture that found masks -- not the desktop walk above. An EMPTY
        // mask set skips it entirely, which is what keeps the common case free
        // (pinned by An_empty_mask_set_performs_no_second_walk).
        await perception.ResolveWindowCaptureGeometryAsync(handle, null);   // warm
        sw.Restart();
        await perception.ResolveWindowCaptureGeometryAsync(handle, null);
        var targetWalkMs = sw.Elapsed.TotalMilliseconds;

        var budget = CaptureRetryOptions.Default.TimeoutMs;
        Assert.Fail(
            $"MEASUREMENT (not a failure) | visibleWindows={visible} | " +
            $"AllMaskRectsAsync={walkMs:F0}ms | 2xDenylistedWindowsVisibleAsync={denyMs:F0}ms | " +
            $"fallbackExcludingTimeoutWait={fallbackMs:F0}ms | " +
            $"oneTimeoutWait={budget}ms | endToEnd~={fallbackMs + budget:F0}ms | " +
            $"bookendExtraWalk={targetWalkMs:F0}ms");
    }
}
