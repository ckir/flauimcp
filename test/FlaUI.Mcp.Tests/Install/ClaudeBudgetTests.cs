using System;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class ClaudeBudgetTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(120);

    [Fact]
    public void First_call_gets_the_full_default_timeout()
        => Assert.Equal(ProcessRunner.DefaultTimeout, CliRouter.BudgetedTimeout(Budget, TimeSpan.Zero));

    [Fact]
    public void Near_the_deadline_the_timeout_shrinks_to_what_remains()
        => Assert.Equal(TimeSpan.FromSeconds(10), CliRouter.BudgetedTimeout(Budget, TimeSpan.FromSeconds(110)));

    [Fact]
    public void At_or_past_the_deadline_it_short_circuits_to_null()
    {
        Assert.Null(CliRouter.BudgetedTimeout(Budget, Budget));
        Assert.Null(CliRouter.BudgetedTimeout(Budget, TimeSpan.FromSeconds(130)));
    }

    [Fact]
    public void Mid_budget_never_exceeds_the_per_call_default()
        => Assert.Equal(ProcessRunner.DefaultTimeout, CliRouter.BudgetedTimeout(Budget, TimeSpan.FromSeconds(50)));

    [Fact]  // the exact `remaining == DefaultTimeout` boundary: `remaining < DefaultTimeout` is false → full default
    public void At_exactly_one_default_remaining_it_returns_the_full_default()
        => Assert.Equal(ProcessRunner.DefaultTimeout, CliRouter.BudgetedTimeout(Budget, TimeSpan.FromSeconds(90)));
}
