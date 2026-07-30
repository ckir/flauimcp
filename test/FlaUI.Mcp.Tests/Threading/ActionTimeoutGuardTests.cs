using System;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Threading;
using Xunit;

namespace FlaUI.Mcp.Tests.Threading;

/// <summary>Regression pins for `negative-timeout-disables-the-sta-watchdog`.
///
/// THE DEFECT: `AwaitWithTimeout` bounds a UIA call with `Task.WhenAny(work, Task.Delay(timeoutMs))`. A
/// caller-supplied -1 is Timeout.Infinite, so the delay never completes and WhenAny degrades to awaiting
/// `work` alone — removing the ONLY bound on a call parked against an unresponsive window. -1 was never the
/// only dangerous value: Task.Delay takes any non-negative int, so int.MaxValue (~24 days) parks it just as
/// well and throws nothing.
///
/// WHY IT MATTERS MORE THAN ONE HUNG CALL: the action slot is held from RunActionAsync's Interlocked
/// increment until the worker's finally, which never runs while parked. MaxPendingActions is 5, so five such
/// calls exhaust the pool and every later action throws TooManyPendingActions for the life of the process.
///
/// TESTED AT THE HELPER, NOT THE TOOL SURFACE, deliberately: the backlog file notes that a tool-surface
/// repro would HANG by construction — an unfixed watchdog cannot fail fast — so a test driving the tool
/// could never go red, only never finish. `ClampActionTimeout` is a pure function, so these are headless,
/// instant, and pin the exact boundary rather than a timing artifact.</summary>
public class ActionTimeoutGuardTests
{
    [Theory]
    [InlineData(-1)]          // Timeout.Infinite — the filed defect
    [InlineData(-2)]          // every other negative: previously an ArgumentOutOfRangeException, i.e. wrong but loud
    [InlineData(int.MinValue)]
    public void A_negative_action_timeout_is_refused_not_clamped(int requested)
    {
        var ex = Assert.Throws<ToolException>(() => AutomationDispatcher.ClampActionTimeout(requested));

        Assert.Equal(ToolErrorCode.InvalidArguments, ex.Code);
        // Refusing rather than clamping is the point: clamping a negative to the cap would hand the caller a
        // 60-second parked action slot instead of an instant, named error.
        Assert.Contains("negative", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(int.MaxValue)]   // ~24 days — legal for Task.Delay, and the half the filed report missed
    [InlineData(600_000)]
    [InlineData(AutomationDispatcher.MaxActionTimeoutMs + 1)]
    public void An_over_large_action_timeout_is_clamped_not_refused(int requested)
    {
        // Clamped, NOT refused: unlike a negative, an over-large budget has an obvious valid reading —
        // "wait as long as you are allowed" — and clamp-and-disclose is this codebase's existing idiom for
        // an out-of-range duration (WaitForForeground.ClampTimeout + "server-capped" in the description).
        Assert.Equal(AutomationDispatcher.MaxActionTimeoutMs, AutomationDispatcher.ClampActionTimeout(requested));
    }

    [Theory]
    [InlineData(0)]      // degenerate but FAILS FAST — the opposite of the hang this guard prevents
    [InlineData(1)]
    [InlineData(8_000)]  // the largest default that actually reaches this dispatcher
    [InlineData(AutomationDispatcher.MaxActionTimeoutMs)]
    public void An_in_range_action_timeout_passes_through_untouched(int requested)
    {
        Assert.Equal(requested, AutomationDispatcher.ClampActionTimeout(requested));
    }

    /// <summary>The cap must stay above every default that routes through this dispatcher, or a shipped tool
    /// would be silently clamped below its own documented budget. 8000 is the largest such default
    /// (desktop_read_terminal_tab); 45000 belongs to desktop_wait_for_foreground, which runs its own bounded
    /// wait via Task.Run and never enters RunActionAsync.</summary>
    [Fact]
    public void The_cap_leaves_headroom_above_every_dispatcher_bound_default()
    {
        Assert.True(AutomationDispatcher.MaxActionTimeoutMs > 8_000,
            $"cap {AutomationDispatcher.MaxActionTimeoutMs} must exceed the largest dispatcher-bound default (8000)");
    }
}
