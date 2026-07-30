using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>Teardown for the Windows Terminal windows the Desktop tests mint.
///
/// DEFECT THIS EXISTS TO FIX (reported by the maintainer, 2026-07-30): repeated Desktop-suite runs left
/// orphaned terminal windows on the operator's desktop, which they had to close by hand. The old teardown
/// requested a close, never checked one happened, and swallowed both failures. A cleanup you do not verify is
/// a wish, not a teardown.
///
/// WHAT THE CLOSE ACTUALLY DOES, measured rather than assumed: `w.Close()` DOES take, but WT tears the window
/// down ASYNCHRONOUSLY — it reaps the child `cmd.exe` processes first — so the window outlives the call. An
/// earlier version of this helper sampled for survivors IMMEDIATELY after the close and failed both tests
/// with a bogus "LEAKED" error while the windows were merely mid-teardown. (It also blamed WT's "close all
/// tabs?" confirmation; the evidence does not support that — the windows closed with nobody dismissing
/// anything.) Hence the bounded POLL: the assertion is "gone within a wait", not "gone this instant".
/// The accumulation the maintainer saw therefore comes from runs whose teardown never ran at all — an
/// interrupted suite, a killed test host, a timeout — which is what <see cref="SweepStaleAsync"/> is for.
///
/// THREE SAFETY PROPERTIES, each added because an AGY-CAPSTONE round found the code violating it:
/// 1. **Process-filtered.** Matching is `ProcessName == "WindowsTerminal"` AND title-carries-marker. Title
///    substring ALONE would have closed the operator's own windows: an editor showing `TerminalTabE2ETests.cs`
///    has "FlaUi" nowhere near it, but an editor showing a file under a `FlaUi…` path, or a browser tab, can
///    easily contain the prefix. Closing a human's editor to tidy a test is categorically worse than the leak.
/// 2. **Never throws from a finally.** The leak assertion is a SEPARATE call the tests make AFTER their
///    try/finally, so a genuine assertion failure inside the test body can never be masked by a teardown
///    exception, and the `proc.Kill()` line still runs.
/// 3. **"Cannot tell" is not "gone".** Enumeration failures return null, not an empty list. Treating a
///    transient COM fault as "no survivors" would report success while leaking — the exact false-green this
///    helper exists to prevent.
///
/// WHY WE STILL DO NOT KILL THE PROCESS: WT is single-process-multi-window, so the process hosting our window
/// is very likely the same one hosting the operator's terminals. Killing it would destroy the human's work.</summary>
internal static class WtTestWindow
{
    /// <summary>Prefix shared by every marker this suite mints (`FlaUiE2E…`, `FlaUiList…`), so a run can
    /// collect leftovers from an EARLIER run that died before its teardown.</summary>
    internal const string MarkerPrefix = "FlaUi";

    /// <summary>The ONLY process whose windows this helper will ever close.</summary>
    private const string OwnerProcess = "WindowsTerminal";

    private static readonly TimeSpan CloseBudget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryBudget = TimeSpan.FromSeconds(10);

    /// <summary>Best-effort close, safe to call from a `finally`: it NEVER throws. Pair it with
    /// <see cref="AssertNoLeakAsync"/> after the try/finally to get the assertion.</summary>
    internal static async Task CloseAsync(WindowManager windows, WindowHandle win, string marker)
    {
        try { await windows.CloseAsync(win); } catch { /* verified separately */ }

        if ((await AwaitGoneAsync(windows, marker, CloseBudget)) is { Count: > 0 } survivors)
        {
            // Re-issue against a FRESH handle — the original may be stale — then wait again.
            foreach (var title in survivors)
            {
                try { await windows.CloseAsync(await windows.OpenByTitleAsync(title)); }
                catch { /* best effort */ }
            }
            await AwaitGoneAsync(windows, marker, RetryBudget);
        }
    }

    /// <summary>Fail the test if this run's window is still on the operator's desktop. Call AFTER the
    /// try/finally, never inside it: throwing from a `finally` replaces any exception already in flight,
    /// which would mask the real assertion failure the test was reporting.</summary>
    internal static async Task AssertNoLeakAsync(WindowManager windows, string marker)
    {
        var survivors = await SurvivorsAsync(windows, marker);
        if (survivors is null)
            throw new InvalidOperationException(
                $"Desktop-suite teardown could not ENUMERATE windows, so it cannot prove marker '{marker}' "
                + "was cleaned up. Treated as a failure on purpose: reporting success here is how a leak "
                + "goes unnoticed.");

        if (survivors.Count > 0)
            throw new InvalidOperationException(
                $"Desktop-suite teardown LEAKED {survivors.Count} Windows Terminal window(s) onto the "
                + $"operator's desktop: {string.Join(", ", survivors)}. Minted by this test (marker "
                + $"'{marker}') and not closed within {CloseBudget.TotalSeconds + RetryBudget.TotalSeconds}s "
                + "across two attempts. Do NOT 'fix' this by killing the WindowsTerminal process — it hosts "
                + "the human's terminals too.");
    }

    /// <summary>Close any window left by an EARLIER run of this suite, so leftovers cannot accumulate across
    /// runs. Never throws — leftovers are not the current test's failure, and a sweep that reds the run it is
    /// cleaning would be its own defect.</summary>
    internal static async Task SweepStaleAsync(WindowManager windows)
    {
        try
        {
            foreach (var title in await SurvivorsAsync(windows, MarkerPrefix) ?? new List<string>())
            {
                try { await windows.CloseAsync(await windows.OpenByTitleAsync(title)); }
                catch { /* best effort */ }
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>Poll until nothing carries the marker, or the budget expires. A failed enumeration is
    /// "unknown", NOT "gone": it keeps polling rather than reporting a clean desktop it could not observe.</summary>
    private static async Task<List<string>> AwaitGoneAsync(WindowManager windows, string marker, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (true)
        {
            var survivors = await SurvivorsAsync(windows, marker);
            if (survivors is { Count: 0 }) return survivors;
            if (DateTime.UtcNow >= deadline) return survivors ?? new List<string>();
            await Task.Delay(250);
        }
    }

    /// <summary>Windows this SUITE created that carry <paramref name="marker"/>. Returns null when the
    /// desktop could not be enumerated — the caller must not read that as "none".</summary>
    private static async Task<List<string>?> SurvivorsAsync(WindowManager windows, string marker)
    {
        try
        {
            var all = await windows.ListWindowsAsync(includeBounds: false, includeHandles: false);
            return all
                // PROCESS FILTER FIRST — this is what makes the title match safe. Without it, any window
                // whose title happens to contain the marker (an editor, a browser tab, a file manager) is a
                // close candidate.
                .Where(w => string.Equals(w.ProcessName, OwnerProcess, StringComparison.Ordinal))
                .Where(w => w.Title is not null && w.Title.Contains(marker, StringComparison.Ordinal))
                .Select(w => w.Title!)
                .ToList();
        }
        catch { return null; }
    }
}
