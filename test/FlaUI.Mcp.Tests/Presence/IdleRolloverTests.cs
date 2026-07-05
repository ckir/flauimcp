using FlaUI.Mcp.Core.Presence;
using Xunit;

namespace FlaUI.Mcp.Tests.Presence;

public class IdleRolloverTests
{
    [Fact]
    public void Normal_case_now_after_last()
        => Assert.Equal(5000u, IdleMath.Compute(now: 10_000, last: 5_000));

    [Fact]
    public void Wrap_case_now_wrapped_past_uint_max()
    {
        // last input 1000 ticks before the 32-bit wrap; now 2000 ticks after it. A naive widened signed
        // (now - last) underflows to ~4.29e9; unchecked 32-bit wraparound gives the true small idle.
        // (uint.MaxValue is itself the LAST value before the wrap to 0, so "1000 ticks before wrap" = MaxValue - 999.)
        uint last = uint.MaxValue - 999; // 1000 ticks before the wrap to 0
        uint now = 2000;                  // 2000 ticks after wrap
        Assert.Equal(3000u, IdleMath.Compute(now, last)); // 1000 + 2000 = 3000 ms
    }

    [Fact]
    public void Equal_ticks_zero_idle()
        => Assert.Equal(0u, IdleMath.Compute(42, 42));
}
