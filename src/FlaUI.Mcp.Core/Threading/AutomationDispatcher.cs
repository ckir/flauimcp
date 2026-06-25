using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Threading;

/// <summary>
/// Marshals all UIA/COM work onto dedicated STA threads. A read/query context that
/// must stay responsive, and a separate action context for potentially-blocking calls
/// (InvokePattern.Invoke can block until a modal it opened is dismissed). Action calls
/// use a timeout: past it they surface ACTION_BLOCKED_PENDING while the worker stays
/// parked on the pending COM call, leaving the query context live.
/// </summary>
public sealed class AutomationDispatcher : IDisposable
{
    private readonly StaThreadContext _query = new("uia-query-sta");
    private readonly StaThreadContext _action = new("uia-action-sta");

    public Task<T> RunQueryAsync<T>(Func<T> func) => _query.RunAsync(func);
    public Task RunQueryAsync(Action action) => _query.RunAsync(action);

    public async Task<T> RunActionAsync<T>(Func<T> func, int timeoutMs)
    {
        var work = _action.RunAsync(func);
        var done = await Task.WhenAny(work, Task.Delay(timeoutMs));
        if (done != work)
            throw new ToolException(
                ToolErrorCode.ActionBlockedPending,
                "Action did not return within the timeout; it likely opened a modal dialog.",
                suggestedRecovery: "snapshot the window to see the dialog, then act on it");
        return await work; // observe exceptions if it actually completed
    }

    public Task RunActionAsync(Action action, int timeoutMs)
        => RunActionAsync(() => { action(); return true; }, timeoutMs);

    public void Dispose()
    {
        _query.Dispose();
        _action.Dispose();
    }
}
