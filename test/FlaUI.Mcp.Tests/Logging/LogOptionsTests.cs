// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

public class LogOptionsTests
{
    [Fact] public void Logging_is_on_at_info_by_default()
    {
        var o = ServerOptions.FromArgs(new string[0]);
        Assert.False(o.NoLog);
        Assert.Equal(LogLevel.Information, o.LogLevel);
    }
    [Fact] public void No_log_flag_disables_it() => Assert.True(ServerOptions.FromArgs(new[] { "--no-log" }).NoLog);
    [Theory]
    [InlineData("--log-level=debug", LogLevel.Debug)]
    [InlineData("--log-level=info", LogLevel.Information)]
    [InlineData("--log-level=warning", LogLevel.Warning)]
    [InlineData("--log-level=garbage", LogLevel.Information)]
    [InlineData("--log-level=", LogLevel.Information)]
    public void Log_level_parses_with_info_fallback(string arg, LogLevel expected) =>
        Assert.Equal(expected, ServerOptions.FromArgs(new[] { arg }).LogLevel);

    [Fact] public void Flag_names_never_carry_values()
    {
        Assert.Equal(new[] { "--redaction-rules", "--log-level", "--overlay" },
            HostFacts.FlagNames(new[] { "--redaction-rules", @"C:\Users\me\rules.json", "--log-level=debug", "--overlay" }));
    }

    [Fact] public void Host_facts_never_throw_and_use_known_words()
    {
        Assert.Contains(HostFacts.Session(), new[] { "console", "rdp", "unknown" });
        Assert.Contains(HostFacts.DpiAwareness(), new[] { "unaware", "system", "permonitor", "permonitorv2", "unknown" });
        Assert.True(HostFacts.DpiScale() > 0);
    }
}
