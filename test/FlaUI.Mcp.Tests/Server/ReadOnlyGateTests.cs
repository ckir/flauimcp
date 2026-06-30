using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

public class ReadOnlyGateTests
{
    [Fact]
    public async Task GuardWrite_blocks_when_read_only()
    {
        var json = await ToolResponse.GuardWrite(new ServerOptions(ReadOnly: true, AllowElevation: false),
            () => Task.FromResult("UNREACHED"));
        Assert.Contains("WriteBlockedReadOnly", json);
    }

    [Fact]
    public async Task GuardWrite_runs_the_body_when_not_read_only()
    {
        var json = await ToolResponse.GuardWrite(new ServerOptions(ReadOnly: false, AllowElevation: false),
            () => Task.FromResult("{\"ok\":true}"));
        Assert.Contains("ok", json);
    }

    [Theory]
    [InlineData(new[] { "--read-only-mode" }, true)]
    [InlineData(new string[0], false)]
    public void ServerOptions_parses_the_flag(string[] args, bool expected)
        => Assert.Equal(expected, ServerOptions.FromArgs(args).ReadOnly);
}
