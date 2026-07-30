using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>Teardown for the Windows Terminal windows the Desktop tests mint.
///
/// DEFECT THIS EXISTS TO FIX (reported by the maintainer, 2026-07-30): repeated Desktop-suite runs left
/// orphaned terminal windows on the operator's desktop, which they had to close by hand. The old teardown was
///     try { await windows.CloseAsync(win); } catch { }
///     try { if (proc is { HasExited: false }) proc.Kill(); } catch { }
/// — it REQUESTED a close and never checked one happened, and both failures were swallowed. A cleanup you do
/// not verify is a wish, not a teardown.
///
/// WHAT THE CLOSE ACTUALLY DOES, measured rather than assumed: `w.Close()` DOES take, but WT tears the window
/// down ASYNCHRONOUSLY — it has to reap the child `cmd.exe` processes first — so the window outlives the call
/// by a noticeable margin. The first version of this helper checked for survivors IMMEDIATELY after the close
/// and consequently failed BOTH tests with a "LEAKED" error while the windows were merely mid-teardown; they
/// were gone moments later. (It also blamed WT's "close all tabs?" confirmation, which the evidence does not
/// support — the windows closed without anyone dismissing anything.) Hence the POLL below: the assertion is
/// "gone within a bounded wait", not "gone this instant".
///
/// The accumulation the maintainer actually saw therefore comes from runs whose teardown never executed at
/// all — an interrupted suite, a killed test host, a timeout — which is exactly what SweepStaleAsync is for.
///
/// WHY WE STILL DO NOT KILL THE PROCESS: WT uses a single-process-multi-window model, so the process hosting
/// our window is very likely the SAME one hosting the operator's own terminals. Killing it to tidy up a test
/// would destroy the human's work — categorically worse than the leak. We only ever touch windows whose title
/// carries the per-run GUID marker this suite generated, so we can never close a window we did not create.
///
/// NOT VERIFIED ON A PHYSICAL CONSOLE YET: authored while the machine was on RDP, where the Desktop suite
/// cannot run (SendInput does not deliver over RDP). The sweep and the verification are lease-free and use no
/// synthetic input, so they should run anywhere — but nobody has watched them work. Do that before trusting
/// this file.</summary>
internal static class WtTestWindow
{
    /// <summary>Every marker this suite has minted, so a run can sweep leftovers from an EARLIER run that
    /// was interrupted before its teardown (a killed test host, a timed-out suite). Prefix-matching means a
    /// crashed run's windows get collected by the next run rather than accumulating forever.</summary>
    internal const string MarkerPrefix = "FlaUi";

    /// <summary>Close the window we minted and PROVE it is gone. Throws if it is not — the whole point is
    /// that this failure must be loud. Safe to call when the test body has already failed: it only throws
    /// when the close genuinely did not take, which is itself worth surfacing.</summary>
    internal static async Task CloseAndVerifyAsync(WindowManager windows, WindowHandle win, string marker)
    {
        try { await windows.CloseAsync(win); } catch { /* fall through to the verify + retry below */ }

        // WT's teardown is asynchronous, so poll rather than sampling once. 15s matches the discovery poll
        // these tests already use for the launch; tearing a window down is strictly easier than starting one.
        var survivors = await AwaitGoneAsync(windows, marker, TimeSpan.FromSeconds(15));
        if (survivors.Count == 0) return;

        // Still there after the wait: re-issue the close against a FRESH handle (the original may be stale)
        // and give it one more bounded wait before declaring a genuine leak.
        foreach (var title in survivors)
        {
            try
            {
                var h = await windows.OpenByTitleAsync(title);
                await windows.CloseAsync(h);
            }
            catch { /* best effort — the assertion below is what reports the outcome */ }
        }

        survivors = await AwaitGoneAsync(windows, marker, TimeSpan.FromSeconds(10));
        if (survivors.Count > 0)
            throw new InvalidOperationException(
                $"Desktop-suite teardown LEAKED {survivors.Count} Windows Terminal window(s) onto the "
                + $"operator's desktop: {string.Join(", ", survivors)}. They were minted by this test "
                + $"(marker '{marker}') and must not be left behind, and they did not close within 25s "
                + $"across two close attempts. Do NOT 'fix' this by killing the WindowsTerminal process — "
                + $"it hosts the human's terminals too.");
    }

    /// <summary>Close any window left over from an EARLIER run of this suite. Called at test start so an
    /// interrupted run cannot accumulate windows across runs. Never throws: leftovers are not this test's
    /// failure, and a sweep that fails the run it is trying to clean up would be its own defect.</summary>
    internal static async Task SweepStaleAsync(WindowManager windows)
    {
        try
        {
            foreach (var title in await SurvivorsAsync(windows, MarkerPrefix))
            {
                try
                {
                    var h = await windows.OpenByTitleAsync(title);
                    await windows.CloseAsync(h);
                }
                catch { /* best effort */ }
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>Poll until no window carries the marker, or the budget runs out. Returns the survivors —
    /// empty means the close took.</summary>
    private static async Task<List<string>> AwaitGoneAsync(WindowManager windows, string marker, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        var survivors = await SurvivorsAsync(windows, marker);
        while (survivors.Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
            survivors = await SurvivorsAsync(windows, marker);
        }
        return survivors;
    }

    private static async Task<List<string>> SurvivorsAsync(WindowManager windows, string marker)
    {
        try
        {
            var all = await windows.ListWindowsAsync(includeBounds: false, includeHandles: false);
            return all.Where(w => w.Title is not null && w.Title.Contains(marker, StringComparison.Ordinal))
                      .Select(w => w.Title!)
                      .ToList();
        }
        catch { return new List<string>(); }
    }
}
