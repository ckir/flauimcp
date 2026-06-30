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
}
