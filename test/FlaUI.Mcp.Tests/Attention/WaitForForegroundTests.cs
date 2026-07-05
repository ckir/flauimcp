using FlaUI.Mcp.Core.Attention;
using Xunit;

namespace FlaUI.Mcp.Tests.Attention;

public class WaitForForegroundTests
{
    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(45000, 45000)]
    [InlineData(300000, 45000)]   // over cap → clamped
    [InlineData(0, 45000)]        // 0 / absent → the cap (default)
    [InlineData(-5, 45000)]       // garbage → cap
    public void Timeout_is_capped_at_45s(int requested, int expected)
        => Assert.Equal(expected, WaitForForeground.ClampTimeout(requested));

    [Fact]
    public void Single_waiter_gate_rejects_a_concurrent_second_waiter()
    {
        var gate = new WaitForForeground.WaiterGate();
        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());   // MaxConcurrentWaiters = 1
        gate.Exit();
        Assert.True(gate.TryEnter());
    }
}
