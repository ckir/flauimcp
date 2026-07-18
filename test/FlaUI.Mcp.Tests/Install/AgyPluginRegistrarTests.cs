using System.Collections.Generic;
using FlaUI.Mcp.Server.Install;
using Xunit;

public class AgyPluginRegistrarTests
{
    private static (AgyPluginRegistrar reg, List<string[]> calls) Present()
    {
        var calls = new List<string[]>();
        var invoker = new CliInvoker(
            run: (_, args, _) => { calls.Add(args); return new RunResult(0, ""); },
            resolve: _ => @"C:\tools\agy.cmd");
        return (new AgyPluginRegistrar(invoker), calls);
    }

    [Fact]
    public void Register_runs_idempotent_uninstall_then_install_with_staging_dir()
    {
        var (reg, calls) = Present();
        var r = reg.Register(@"C:\App\plugin");

        Assert.Equal(AgentChange.Created, r.Change);
        Assert.Equal(2, calls.Count);
        Assert.Equal(new[] { "/C", "agy", "plugin", "uninstall", "flaui-mcp" }, calls[0]); // swallow
        Assert.Equal(new[] { "/C", "agy", "plugin", "install", @"C:\App\plugin" }, calls[1]);
    }

    [Fact]
    public void Register_skips_and_reports_NotFound_when_agy_absent()
    {
        var invoker = new CliInvoker(run: (_, _, _) => new RunResult(0, ""), resolve: _ => null);
        var r = new AgyPluginRegistrar(invoker).Register(@"C:\App\plugin");
        Assert.Equal(AgentChange.NotFound, r.Change);
    }

    [Fact]
    public void Register_fails_when_install_exits_nonzero()
    {
        var calls = new List<string[]>();
        var invoker = new CliInvoker(
            run: (_, args, _) => { calls.Add(args); return args[3] == "install" ? new RunResult(1, "boom") : new RunResult(0, ""); },
            resolve: _ => @"C:\tools\agy.cmd");
        var r = new AgyPluginRegistrar(invoker).Register(@"C:\App\plugin");
        Assert.Equal(AgentChange.Failed, r.Change);
        Assert.Contains("boom", r.Detail);
    }
}
