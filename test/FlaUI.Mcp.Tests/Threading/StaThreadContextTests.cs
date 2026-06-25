using System.Threading;
using FlaUI.Mcp.Core.Threading;
using Xunit;

namespace FlaUI.Mcp.Tests.Threading;

public class StaThreadContextTests
{
    [Fact]
    public async Task Runs_work_on_an_STA_thread()
    {
        using var ctx = new StaThreadContext("test-sta");
        var state = await ctx.RunAsync(() => Thread.CurrentThread.GetApartmentState());
        Assert.Equal(ApartmentState.STA, state);
    }

    [Fact]
    public async Task Marshals_all_work_to_the_same_single_thread()
    {
        using var ctx = new StaThreadContext("test-sta");
        var id1 = await ctx.RunAsync(() => Environment.CurrentManagedThreadId);
        var id2 = await ctx.RunAsync(() => Environment.CurrentManagedThreadId);
        Assert.Equal(id1, id2);
    }

    [Fact]
    public async Task Propagates_exceptions_to_the_caller()
    {
        using var ctx = new StaThreadContext("test-sta");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ctx.RunAsync<int>(() => throw new InvalidOperationException("boom")));
    }
}
