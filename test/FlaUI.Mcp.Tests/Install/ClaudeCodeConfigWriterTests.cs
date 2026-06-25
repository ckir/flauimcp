using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class ClaudeCodeConfigWriterTests
{
    [Fact]
    public void Install_invokes_claude_mcp_add_with_the_exe()
    {
        var calls = new List<(string file, string[] args)>();
        var w = new ClaudeCodeConfigWriter((file, args) => { calls.Add((file, args)); return 0; });

        var r = w.Install(@"C:\x\flaui-mcp.exe");

        Assert.Equal(AgentChange.Created, r.Change);
        var (file, args) = Assert.Single(calls);
        Assert.Equal("claude", file);
        Assert.Equal(new[] { "mcp", "add", "flaui-mcp", "--", @"C:\x\flaui-mcp.exe" }, args);
    }

    [Fact]
    public void Uninstall_invokes_claude_mcp_remove()
    {
        var calls = new List<string[]>();
        var w = new ClaudeCodeConfigWriter((_, args) => { calls.Add(args); return 0; });
        w.Uninstall();
        Assert.Equal(new[] { "mcp", "remove", "flaui-mcp" }, Assert.Single(calls));
    }

    [Fact]
    public void Install_reports_NotFound_when_runner_signals_missing_cli()
    {
        var w = new ClaudeCodeConfigWriter((_, _) => -1); // -1 == claude CLI unavailable
        var r = w.Install(@"C:\x\flaui-mcp.exe");
        Assert.Equal(AgentChange.NotFound, r.Change);
    }
}
