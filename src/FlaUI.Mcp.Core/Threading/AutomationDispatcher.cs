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

    private readonly StaThreadContext _query = new("uia-query-sta");
    private int _pendingActions;

    public Task<T> RunQueryAsync<T>(Func<T> func) => _query.RunAsync(func);
    public Task RunQueryAsync(Action action) => _query.RunAsync(action);

    public Task<T> RunActionAsync<T>(Func<T> func, int timeoutMs)
    {
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
