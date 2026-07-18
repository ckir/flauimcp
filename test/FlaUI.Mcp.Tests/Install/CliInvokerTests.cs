using System.Collections.Generic;
using FlaUI.Mcp.Server.Install;
using Xunit;

public class CliInvokerTests
{
    [Fact]
    public void Present_cli_routed_through_cmd_slashC_with_discrete_args()
    {
        var calls = new List<(string file, string[] args, string? cwd)>();
        var invoker = new CliInvoker(
            run: (file, args, cwd) => { calls.Add((file, args, cwd)); return new RunResult(0, "ok"); },
            resolve: _ => @"C:\tools\agy.cmd"); // present, resolved in C:\tools

        var r = invoker.Invoke("agy", "plugin", "install", @"C:\App With Space\plugin");

        Assert.Equal(0, r.Code);
        var (file, args, cwd) = Assert.Single(calls);
        Assert.Equal("cmd.exe", file);
        // Discrete elements — never one concatenated string; path stays its own element (quoted by .NET).
        Assert.Equal(new[] { "/C", "agy", "plugin", "install", @"C:\App With Space\plugin" }, args);
        // cwd is the resolved CLI's directory so cmd finds the shim even off-PATH (plan-review R2).
        Assert.Equal(@"C:\tools", cwd);
    }

    [Fact]
    public void Absent_cli_returns_NotFound_without_running()
    {
        var ran = false;
        var invoker = new CliInvoker(
            run: (_, _, _) => { ran = true; return new RunResult(0, ""); },
            resolve: _ => null); // absent

        var r = invoker.Invoke("claude", "plugin", "list");

        Assert.False(ran);
        Assert.Equal(ProcessRunner.NotFound, r.Code);
    }
}
