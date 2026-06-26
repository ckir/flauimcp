using System.Threading;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Threading;
using Xunit;

namespace FlaUI.Mcp.Tests.Threading;

public class AutomationDispatcherTests
{
    [Fact]
    public async Task Query_and_action_run_on_distinct_STA_threads()
    {
        using var d = new AutomationDispatcher();
        var q = await d.RunQueryAsync(() => Environment.CurrentManagedThreadId);
        var a = await d.RunActionAsync(() => Environment.CurrentManagedThreadId, timeoutMs: 1000);
        Assert.NotEqual(q, a);
    }

    [Fact]
    public async Task Blocking_action_throws_ActionBlockedPending_after_timeout()
    {
        using var d = new AutomationDispatcher();
        using var gate = new ManualResetEventSlim(false);
        var ex = await Assert.ThrowsAsync<ToolException>(
            () => d.RunActionAsync(() => { gate.Wait(); return 0; }, timeoutMs: 100));
        Assert.Equal(ToolErrorCode.ActionBlockedPending, ex.Code);
        gate.Set(); // release the parked worker so Dispose can join
    }

    [Fact]
    public async Task Query_context_stays_responsive_while_an_action_is_blocked()
    {
        using var d = new AutomationDispatcher();
        using var gate = new ManualResetEventSlim(false);
        // Start a blocking action (do not await to completion yet).
        var blocked = Assert.ThrowsAsync<ToolException>(
            () => d.RunActionAsync(() => { gate.Wait(); return 0; }, timeoutMs: 100));
        // The query context must still answer quickly.
        var answered = await d.RunQueryAsync(() => 42);
        Assert.Equal(42, answered);
        await blocked;
        gate.Set();
    }

    [Fact]
    public async Task Action_dispatcher_caps_in_flight_actions()
    {
        using var d = new AutomationDispatcher();
        using var gate = new ManualResetEventSlim(false);
        // Fill the cap (5) with parked actions; each throws ActionBlockedPending but stays parked.
        var parked = new List<Task>();
        for (int i = 0; i < 5; i++)
            parked.Add(Assert.ThrowsAsync<ToolException>(
                () => d.RunActionAsync(() => { gate.Wait(); return 0; }, timeoutMs: 100)));
        await Task.WhenAll(parked); // all 5 timed out (parked), slots still held

        var ex = await Assert.ThrowsAsync<ToolException>(
            () => d.RunActionAsync(() => 0, timeoutMs: 1000));
        Assert.Equal(ToolErrorCode.TooManyPendingActions, ex.Code);

        gate.Set(); // release the 5 parked threads so they unwind and free their slots
    }
}
