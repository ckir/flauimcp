// test/FlaUI.Mcp.Tests/Vision/TextWaiterTests.cs
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Vision;
using Xunit;

namespace FlaUI.Mcp.Tests.Vision;

public class TextWaiterTests
{
    [Fact]
    public async Task Returns_satisfied_true_as_soon_as_a_probe_finds_it()
    {
        int calls = 0;
        var r = await TextWaiter.WaitAsync(
            probe: () => Task.FromResult(++calls >= 2), // found on the 2nd probe
            timeoutMs: 5000, minIntervalMs: 10);
        Assert.True(r.Satisfied);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Returns_satisfied_false_on_timeout_not_an_error()
    {
        var r = await TextWaiter.WaitAsync(
            probe: () => Task.FromResult(false), // never found
            timeoutMs: 60, minIntervalMs: 10);
        Assert.False(r.Satisfied); // DATA, not a throw
    }

    [Fact]
    public async Task Enforces_the_minimum_interval_between_probes()
    {
        int calls = 0;
        var sw = Stopwatch.StartNew();
        await TextWaiter.WaitAsync(
            probe: () => { calls++; return Task.FromResult(false); },
            timeoutMs: 250, minIntervalMs: 100);
        sw.Stop();
        // With a 100ms floor and a 250ms budget, at most ~3 probes (0ms, ~100ms, ~200ms) — never a tight spin.
        Assert.True(calls <= 4, $"expected throttled probes, got {calls}");
    }
}
