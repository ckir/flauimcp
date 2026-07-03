using System;
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class ActionBudgetTests
{
    private static DateTime T(int s) => new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(s);

    [Fact]
    public void Allows_up_to_limit_then_refuses_within_window()
    {
        var b = new ActionBudget(maxPerWindow: 3, windowSeconds: 60);
        var lease = T(0);
        for (int i = 0; i < 3; i++) Assert.True(b.TryConsume(window: (nint)1, now: T(i), leaseWriteUtc: lease));
        Assert.False(b.TryConsume((nint)1, T(3), lease));        // 4th within 60s → refused
    }

    [Fact]
    public void Window_slides_so_old_actions_expire()
    {
        var b = new ActionBudget(3, 60);
        var lease = T(0);
        for (int i = 0; i < 3; i++) b.TryConsume((nint)1, T(i), lease);
        Assert.True(b.TryConsume((nint)1, T(61), lease));        // first three aged out
    }

    [Fact]
    public void New_lease_write_resets_the_budget()
    {
        var b = new ActionBudget(3, 60);
        for (int i = 0; i < 3; i++) b.TryConsume((nint)1, T(i), T(0));
        Assert.True(b.TryConsume((nint)1, T(3), leaseWriteUtc: T(2)));   // lease re-granted → reset
    }

    [Fact]
    public void Budget_is_per_window()
    {
        var b = new ActionBudget(1, 60);
        Assert.True(b.TryConsume((nint)1, T(0), T(0)));
        Assert.True(b.TryConsume((nint)2, T(0), T(0)));          // different window, own budget
        Assert.False(b.TryConsume((nint)1, T(0), T(0)));
    }

    [Fact]
    public void HasFreeSlot_peeks_without_consuming_then_reflects_exhaustion()
    {
        var b = new ActionBudget(maxPerWindow: 2, windowSeconds: 60);
        Assert.True(b.TryConsume((nint)1, T(0), leaseWriteUtc: T(0)));   // observe lease@T0 + 1 hit (1 slot left)
        Assert.True(b.HasFreeSlot((nint)1, T(0), leaseWriteUtc: T(0)));  // 1 slot free; peek does not consume
        Assert.True(b.HasFreeSlot((nint)1, T(0), leaseWriteUtc: T(0)));  // still free (proves no consume)
        Assert.True(b.TryConsume((nint)1, T(0), leaseWriteUtc: T(0)));   // consume the 2nd slot
        Assert.False(b.HasFreeSlot((nint)1, T(0), leaseWriteUtc: T(0))); // exhausted
    }

    [Fact]
    public void HasFreeSlot_reports_free_after_a_fresh_lease_write_even_when_the_window_was_exhausted()
    {
        var b = new ActionBudget(maxPerWindow: 1, windowSeconds: 60);
        Assert.True(b.TryConsume((nint)1, T(0), leaseWriteUtc: T(0)));   // exhaust the single slot under lease@T0
        Assert.False(b.HasFreeSlot((nint)1, T(1), leaseWriteUtc: T(0))); // same lease -> still exhausted
        Assert.True(b.HasFreeSlot((nint)1, T(1), leaseWriteUtc: T(2)));  // fresh unlock (newer write) -> budget considered free
    }
}
