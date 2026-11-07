// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System;
using System.IO;
using System.Linq;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Tests.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

public class InputDecisionLoggingTests
{
    private const string Sid = "S-1-5-21-1";
    private static DateTime Now => new(2030, 1, 1, 0, 0, 30, DateTimeKind.Utc);

    private static (InputGuard, CapturingLogger<InputGuard>) Build(InputLease? lease)
    {
        var env = new FakePlatformEnvironment { CanDeliver = true, ForegroundRoot = nint.Zero };
        var log = new CapturingLogger<InputGuard>();
        var guard = new InputGuard(new RecordingSyntheticInput(env), env, new LeaseStub(lease),
            new ActionBudget(60, 60), new InputAudit(TextWriter.Null),
            currentSid: Sid, isElevated: false, allowElevation: false, clock: () => Now, logger: log);
        return (guard, log);
    }

    [Fact]
    public void A_refusal_logs_allowed_false_with_its_code()
    {
        var (g, log) = Build(null);
        Assert.Throws<ToolException>(() => g.KeyType("hi", new(nint.Zero, 0, "notepad", "Notepad")));
        var ev = log.Inner.Single("input.decision");
        Assert.False((bool)ev.Fields.Single(f => f.Key == "allowed").Value!);
        Assert.Equal("InputNotLeased", ev.Fields.Single(f => f.Key == "code").Value);
        Assert.Equal("type", ev.Fields.Single(f => f.Key == "action").Value);
    }

    [Fact]
    public void An_allowed_input_logs_the_remaining_budget()
    {
        var (g, log) = Build(new InputLease(new DateTime(2030, 1, 1, 0, 1, 0, DateTimeKind.Utc), Sid, Array.Empty<string>()));
        g.KeyType("hi", new(nint.Zero, 0, "notepad", "Notepad"));
        var ev = log.Inner.Single("input.decision");
        Assert.True((bool)ev.Fields.Single(f => f.Key == "allowed").Value!);
        Assert.Equal(59, ev.Fields.Single(f => f.Key == "budgetLeft").Value);
    }

    [Fact]
    public void Remaining_counts_down_and_prunes_expired_hits()
    {
        var b = new ActionBudget(3, 60);
        Assert.Equal(3, b.Remaining(1, Now));
        b.TryConsume(1, Now, default);
        Assert.Equal(2, b.Remaining(1, Now));
        Assert.Equal(3, b.Remaining(1, Now.AddSeconds(61)));
    }

    private sealed class LeaseStub(InputLease? lease) : ILeaseProvider
    {
        public InputLease? Read(out DateTime lastWriteUtc) { lastWriteUtc = default; return lease; }
    }
}
