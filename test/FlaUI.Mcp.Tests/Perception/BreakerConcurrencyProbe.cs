// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>AGY-CAPSTONE round 1, finding 4. Pins the MEASURED reality that concurrent captures arriving
/// inside the first timeout budget are NOT bounded by the circuit breaker.
///
/// ⚠ This asserts CURRENT behaviour, not desired behaviour. If someone fixes the design — a per-HWND
/// gate, or diverting on a suspicion threshold below the budget — this test SHOULD go red, and the fixer
/// should update it deliberately rather than discover the change by accident. That is why it is pinned.
///
/// ⚠⚠ THE THREAD POOL IS PRE-GROWN AND THE REQUESTS RUN VIA Task.Run, BOTH FOR CORRECTNESS OF THE
/// MEASUREMENT, and both were learned by getting it wrong. Calling CaptureAsync inline blocks the loop
/// (CaptureWindow is synchronous and the first await completes synchronously) and reports 1 acquisition;
/// leaving the pool at its default starves the later requests and reports 2. Neither number was about
/// the breaker at all. With both confounds removed it is 5 of 5.</summary>
public class BreakerConcurrencyProbe
{
    private static readonly Rectangle W = new(0, 0, 400, 300);

    [Fact]
    public async Task Concurrent_requests_inside_the_timeout_budget_are_NOT_bounded()
    {
        // ⚠ THE BUDGET MUST DWARF THE LAUNCH LOOP. This was 200 ms, and under full-suite load the loop below
        // (nominally 5 x 20 ms) MEASURED 536-645 ms: `Task.Delay(20)` stretched to 60-300 ms, the first
        // acquisition timed out and TRIPPED the breaker, and every later request was correctly diverted -
        // "only 1 of 5". That is the breaker working, not the defect this test pins; the premise "all arrive
        // inside one budget" had silently failed. Failed 6 of 12 runs under synthetic CPU load at 200 ms.
        // *(2026-11-08 triage of the flake.)*
        const int TimeoutMs = 3000;
        // ⚠ Pre-grow the pool. Each request BLOCKS a thread inside CaptureWindow, so without this the
        // pool starves and later requests are simply never scheduled - which would look exactly like the
        // breaker diverting them.
        ThreadPool.GetMinThreads(out var w0, out var c0);
        ThreadPool.SetMinThreads(Math.Max(w0, 32), c0);
        var gate = new ManualResetEventSlim(false);
        int acquireCalls = 0;

        var src = new FakeWindowImageSource(_ =>
        {
            Interlocked.Increment(ref acquireCalls);
            gate.Wait(10_000);   // simulate a hung target: blocks well past the timeout
            return null;
        });

        // THE REAL CLOCK, exactly as production wires it.
        var breaker = new CaptureCircuitBreaker(TimeSpan.FromMinutes(5), () => DateTime.UtcNow);
        var c = new WindowCaptureCoordinator(
            (_, _) => Task.FromResult(new CaptureGeometry(W, Array.Empty<Rectangle>(), false, false, null,
                Array.Empty<MaskEscalationEntry>(), W, new IntPtr(0xBEEF), false)),
            src, new CaptureRetryOptions(1, TimeoutMs),
            w2Probe: _ => W, minimizedProbe: _ => false,
            scrape: (b, m, mw, s, warns) => new CaptureResult(Array.Empty<byte>(), b.X, b.Y, b.Width,
                                                             b.Height, 1.0, 0, "screenScrape", warns),
            breaker: breaker,
            isHungProbe: _ => true,
            denylistedVisible: () => Task.FromResult(false),
            desktopMasks: () => Task.FromResult(new DesktopMaskSet(
                Array.Empty<Rectangle>(), Array.Empty<MaskEscalationEntry>(), Array.Empty<string>())));

        // Fire FIVE overlapping requests, staggered inside the timeout budget - the exact scenario the
        // guard exists for: they arrive before any timeout has been recorded.
        var tasks = new List<Task>();
        var loop = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 5; i++)
        {
            // ⚠ Task.Run: CaptureWindow is SYNCHRONOUS and blocks, and CaptureAsync's first await
            // completes synchronously, so calling it inline blocks this loop and creates NO overlap.
            tasks.Add(Task.Run(() => c.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 0)));
            await Task.Delay(20);        // nominally 5 x 20ms = 100ms, far inside the budget
        }

        var inflight = Volatile.Read(ref acquireCalls);
        var loopMs = loop.ElapsedMilliseconds;

        // ⚠ RELEASE FIRST, THEN AWAIT. This read `await Task.WhenAll(tasks); gate.Set();` -- and since
        // every request is blocked INSIDE the gate, WhenAll could not complete until the gate's own 5000ms
        // timeout expired, making `gate.Set()` dead code and the test a flat 5 seconds. MEASURED at
        // exactly 5s before this change. *(AGY-CAPSTONE round 2, finding 4.)*
        gate.Set();
        await Task.WhenAll(tasks);

        // The PREMISE, checked rather than assumed: a failure here is scheduler starvation, not the breaker.
        Assert.True(loopMs < TimeoutMs / 2,
            $"the launch loop took {loopMs} ms against a {TimeoutMs} ms budget, so later requests may have " +
            "arrived after a timeout tripped the breaker - the machine is too loaded for this probe to measure");

        // MEASURED 5 of 5 - every overlapping request starts its own acquisition.
        //
        // ⚠ ASSERTED AS >= 4, NOT > 1, AND THE DIFFERENCE MATTERS. `> 1` was too weak to do the job this
        // test exists for: a partial fix that bounded the count to 2 or 3 would still satisfy it, so the
        // fix would land silently and this test would keep asserting a defect that no longer had that
        // shape. >= 4 says "essentially unbounded" while still tolerating a scheduling wobble.
        // *(AGY-CAPSTONE round 2, finding 4.)*
        Assert.True(inflight >= 4,
            $"only {inflight} of 5 overlapping requests started their own acquisition - if the concurrent " +
            "guard has been FIXED, update this test to assert the new bound rather than deleting it");
    }
}
