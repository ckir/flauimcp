// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class PerceptionManagerShouldFixTests
{
    [Fact]
    public void IsPassword_read_that_throws_is_treated_as_password_fail_closed()
    {
        Assert.True(RedactionPolicy.IsPasswordOrFailClosed(() => throw new System.Runtime.InteropServices.COMException()));
        Assert.True(RedactionPolicy.IsPasswordOrFailClosed(() => true));
        Assert.False(RedactionPolicy.IsPasswordOrFailClosed(() => false));
    }
}
