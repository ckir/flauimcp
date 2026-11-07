// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.Threading;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Threading;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

public class StaContextFlowTests
{
    private static readonly AsyncLocal<string?> Probe = new();

    [Fact]
    public async Task An_AsyncLocal_set_by_the_caller_is_visible_inside_queued_STA_work()
    {
        using var ctx = new StaThreadContext("flow-sta");
        Probe.Value = "c-0000abcd";
        var seen = await ctx.RunAsync(() => Probe.Value);
        Assert.Equal("c-0000abcd", seen);
    }

    [Fact]
    public async Task Each_work_item_sees_its_own_callers_value()
    {
        using var ctx = new StaThreadContext("flow-sta2");
        Probe.Value = "first";
        var a = ctx.RunAsync(() => Probe.Value);
        Probe.Value = "second";
        var b = ctx.RunAsync(() => Probe.Value);
        Assert.Equal("first", await a);
        Assert.Equal("second", await b);
    }
}
