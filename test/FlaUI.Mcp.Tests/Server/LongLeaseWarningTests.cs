// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using FlaUI.Mcp.Server.Lease;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

public class LongLeaseWarningTests
{
    [Theory]
    [InlineData(30, false, false, LeaseWarningDecision.NoWarning)]   // short → frictionless even non-interactive
    [InlineData(60, false, false, LeaseWarningDecision.NoWarning)]   // threshold is > 60, so exactly 60 is fine
    [InlineData(120, true, false, LeaseWarningDecision.ProceedWithLoggedWarning)]  // flag acks non-interactively
    [InlineData(120, false, true, LeaseWarningDecision.ProceedWithLoggedWarning)]  // TTY: prompt handled by caller
    [InlineData(120, false, false, LeaseWarningDecision.RefuseNeedsAck)]           // no TTY, no flag → refuse
    public void Decide(int minutes, bool flag, bool tty, LeaseWarningDecision expected)
        => Assert.Equal(expected, LeaseWarning.Decide(minutes, flag, tty));

    [Fact]
    public void Warning_text_discloses_no_sandbox_and_names_the_minutes()
    {
        var t = LeaseWarning.Text(999);
        Assert.Contains("999", t);
        Assert.Contains("NO sandboxing", t);
        Assert.Contains("I understand", t);
    }
}
