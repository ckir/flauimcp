using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Threading;

/// <summary>
/// Marshals UIA/COM work onto STA threads. Reads run on a single long-lived query STA that
/// must stay responsive. Each ACTION runs on its OWN transient STA thread with its own COM
/// state, so a potentially-blocking call (InvokePattern.Invoke parks until a modal it opened
/// is dismissed) can be abandoned on timeout (ACTION_BLOCKED_PENDING) WITHOUT starving the
/// next action — a single shared action thread would deadlock there. An Interlocked cap bounds
/// parked-thread accumulation from a runaway/injected loop until Phase 4's full action budget.
/// </summary>
public sealed class AutomationDispatcher : IDisposable
{
    private const int MaxPendingActions = 5;

    /// <summary>Upper bound on a caller-supplied action timeout. 60s is 7.5x the largest default that
    /// actually reaches this dispatcher (8000, desktop_read_terminal_tab). The 45000 default on
    /// desktop_wait_for_foreground is NOT relevant here — that tool runs its own bounded wait via Task.Run
    /// and never enters RunActionAsync.</summary>
    internal const int MaxActionTimeoutMs = 60_000;

    /// <summary>Bound a caller-supplied action timeout. ASYMMETRIC ON PURPOSE, and the asymmetry is the whole
    /// design: a NEGATIVE budget has no valid interpretation, so it is REFUSED; an over-large one has an
    /// obvious one ("wait as long as you are allowed"), so it is CLAMPED and disclosed.
    ///
    /// WHY THIS EXISTS (docs/fix-the-tool-backlog/negative-timeout-disables-the-sta-watchdog.md): a caller
    /// -1 is Timeout.Infinite, so `Task.WhenAny(work, Task.Delay(-1))` degraded to awaiting `work` alone —
    /// removing the ONLY bound on a UIA call parked against an unresponsive window. And -1 was never the only
    /// dangerous value: Task.Delay accepts any non-negative int, so int.MaxValue (~24 days) parks the
    /// watchdog just as effectively and throws nothing. A lower-bound guard alone does not close this.
    ///
    /// BLAST RADIUS, which is why it is a refusal and not a shrug: the slot is held from the Interlocked
    /// increment in RunActionAsync until the worker thread's finally, which never runs while the call is
    /// parked. MaxPendingActions is 5, so FIVE such calls exhaust the pool and every subsequent action throws
    /// TooManyPendingActions for the life of the process — a denial of the entire action surface, reachable
    /// from five tool calls, by a client the threat model already assumes may be prompt-injected.
    ///
    /// DO NOT refactor this to reuse WaitForForeground.ClampTimeout. That helper maps INVALID input to the
    /// MAXIMUM (`requestedMs > 0 && <= HardCapMs ? requestedMs : HardCapMs`), which is benign for a bounded
    /// foreground wait and exactly wrong here: it would turn `-1` into a 60-second parked slot instead of an
    /// instant refusal. Same problem, opposite correct answer.
    ///
    /// 0 is deliberately left alone: it fails FAST (an immediate ActionBlockedPending), which is degenerate
    /// but is the opposite of the hang this guard exists to prevent.</summary>
    internal static int ClampActionTimeout(int requestedMs)
    {
        if (requestedMs < 0)
            throw new ToolException(
                ToolErrorCode.InvalidArguments,
                $"timeoutMs must not be negative (got {requestedMs}); -1 is Timeout.Infinite, which would "
                + "remove the only bound on a blocked UIA call.",
                suggestedRecovery: $"pass a positive timeout in ms (server-capped to {MaxActionTimeoutMs})");

        return Math.Min(requestedMs, MaxActionTimeoutMs);
    }

    private readonly StaThreadContext _query = new("uia-query-sta");
    private int _pendingActions;

    public Task<T> RunQueryAsync<T>(Func<T> func) => _query.RunAsync(func);
    public Task RunQueryAsync(Action action) => _query.RunAsync(action);

    public Task<T> RunActionAsync<T>(Func<T> func, int timeoutMs)
    {
        // BEFORE the slot is taken and the thread is started: a refusal here costs nothing, whereas
        // validating inside AwaitWithTimeout would already have spawned a worker and be holding a slot.
        timeoutMs = ClampActionTimeout(timeoutMs);

        if (Interlocked.Increment(ref _pendingActions) > MaxPendingActions)
        {
            Interlocked.Decrement(ref _pendingActions);
            throw new ToolException(
                ToolErrorCode.TooManyPendingActions,
                $"Too many actions ({MaxPendingActions}) are blocked pending; refusing to start another.",
                suggestedRecovery: "snapshot the window(s) and dismiss the pending modal dialog(s), then retry");
        }

        // Per-action STA thread. Completes its TCS via TrySet* only (never Set*) so a late
        // wake-up after abandonment cannot double-complete; the catch-all keeps a delayed
        // throw off the background thread (an unhandled one would crash the whole server).
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.TrySetResult(func()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
            finally { Interlocked.Decrement(ref _pendingActions); }
        })
        { Name = "uia-action-sta", IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return AwaitWithTimeout(tcs.Task, timeoutMs);
    }

    public Task RunActionAsync(Action action, int timeoutMs)
        => RunActionAsync(() => { action(); return true; }, timeoutMs);

    private static async Task<T> AwaitWithTimeout<T>(Task<T> work, int timeoutMs)
    {
        var done = await Task.WhenAny(work, Task.Delay(timeoutMs));
        if (done != work)
        {
            // Abandon the parked thread but observe its eventual fault so it never surfaces as
            // an unobserved task exception when the modal is finally dismissed.
            _ = work.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
            throw new ToolException(
                ToolErrorCode.ActionBlockedPending,
                "Action did not return within the timeout; it likely opened a modal dialog.",
                suggestedRecovery: "snapshot the window to see the dialog, then act on it");
        }
        return await work; // observe exceptions if it actually completed
    }

    public void Dispose() => _query.Dispose();
}
