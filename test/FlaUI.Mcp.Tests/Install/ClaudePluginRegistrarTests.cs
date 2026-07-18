using System.Collections.Generic;
using FlaUI.Mcp.Server.Install;
using Xunit;

public class ClaudePluginRegistrarTests
{
    // A fake that records argv and answers `plugin list` with a provided body.
    private static CliInvoker Fake(List<string[]> calls, string listBody, int listCode = 0)
        => new CliInvoker(
            run: (_, args, _) =>
            {
                calls.Add(args);
                var isList = System.Array.IndexOf(args, "list") >= 0;
                return isList ? new RunResult(listCode, listBody) : new RunResult(0, "");
            },
            resolve: _ => @"C:\tools\claude.cmd");

    [Fact]
    public void Register_runs_remove_add_uninstall_install_then_readback()
    {
        var calls = new List<string[]>();
        // read-back shows the plugin present AND enabled
        var reg = new ClaudePluginRegistrar(Fake(calls, "flaui-mcp@flaui-mcp-marketplace  enabled"));
        var r = reg.Register(@"C:\App\plugin");

        Assert.Equal(AgentChange.Created, r.Change);
        Assert.Equal(new[] { "/C", "claude", "plugin", "marketplace", "remove", "flaui-mcp-marketplace" }, calls[0]);
        Assert.Equal(new[] { "/C", "claude", "plugin", "marketplace", "add", @"C:\App\plugin", "--scope", "user" }, calls[1]);
        Assert.Equal(new[] { "/C", "claude", "plugin", "uninstall", "flaui-mcp" }, calls[2]);
        Assert.Equal(new[] { "/C", "claude", "plugin", "install", "flaui-mcp@flaui-mcp-marketplace", "--scope", "user" }, calls[3]);
        Assert.Contains(calls, c => System.Array.IndexOf(c, "list") >= 0); // read-back happened
    }

    [Fact]
    public void Register_fails_when_readback_shows_disabled()
    {
        var calls = new List<string[]>();
        var reg = new ClaudePluginRegistrar(Fake(calls, "flaui-mcp@flaui-mcp-marketplace  Disabled"));
        var r = reg.Register(@"C:\App\plugin");
        Assert.Equal(AgentChange.Failed, r.Change); // present-but-not-active is NOT success
    }

    [Fact]
    public void Register_fails_when_readback_absent()
    {
        var calls = new List<string[]>();
        var reg = new ClaudePluginRegistrar(Fake(calls, "some-other-plugin  enabled"));
        var r = reg.Register(@"C:\App\plugin");
        Assert.Equal(AgentChange.Failed, r.Change);
    }

    [Fact]
    public void Register_skips_when_claude_absent()
    {
        var reg = new ClaudePluginRegistrar(new CliInvoker((_, _, _) => new RunResult(0, ""), _ => null));
        Assert.Equal(AgentChange.NotFound, reg.Register(@"C:\App\plugin").Change);
    }
}
