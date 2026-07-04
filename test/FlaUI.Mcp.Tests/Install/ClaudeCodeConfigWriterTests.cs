using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class ClaudeCodeConfigWriterTests
{
    [Fact]
    public void Install_invokes_claude_mcp_remove_then_add_with_the_exe()
    {
        // Install is remove-then-add so re-registration is idempotent and can change the args of
        // an existing entry (`claude mcp add` fails on a duplicate name). The remove is best-effort.
        var calls = new List<(string file, string[] args)>();
        var w = new ClaudeCodeConfigWriter((file, args) => { calls.Add((file, args)); return 0; });

        var r = w.Install(@"C:\x\flaui-mcp.exe");

        Assert.Equal(AgentChange.Created, r.Change);
        Assert.Equal(2, calls.Count);
        Assert.Equal("claude", calls[0].file);
        Assert.Equal(new[] { "mcp", "remove", "--scope", "user", "flaui-mcp" }, calls[0].args);
        Assert.Equal("claude", calls[1].file);
        Assert.Equal(new[] { "mcp", "add", "--scope", "user", "flaui-mcp", "--", @"C:\x\flaui-mcp.exe" }, calls[1].args);
    }

    [Fact]
    public void Install_with_args_appends_them_to_the_add_command()
    {
        var calls = new List<(string file, string[] args)>();
        var w = new ClaudeCodeConfigWriter((file, args) => { calls.Add((file, args)); return 0; });

        var r = w.Install(@"C:\x\flaui-mcp.exe", new[] { "--overlay", "--overlay-ms=800" });

        Assert.Equal(AgentChange.Created, r.Change);
        Assert.Equal(2, calls.Count);
        Assert.Equal(
            new[] { "mcp", "add", "--scope", "user", "flaui-mcp", "--", @"C:\x\flaui-mcp.exe", "--overlay", "--overlay-ms=800" },
            calls[1].args);
    }

    [Fact]
    public void Uninstall_invokes_claude_mcp_remove()
    {
        var calls = new List<string[]>();
        var w = new ClaudeCodeConfigWriter((_, args) => { calls.Add(args); return 0; });
        w.Uninstall();
        Assert.Equal(new[] { "mcp", "remove", "--scope", "user", "flaui-mcp" }, Assert.Single(calls));
    }

    [Fact]
    public void Install_reports_NotFound_when_runner_signals_missing_cli()
    {
        var w = new ClaudeCodeConfigWriter((_, _) => -1); // -1 == claude CLI unavailable
        var r = w.Install(@"C:\x\flaui-mcp.exe");
        Assert.Equal(AgentChange.NotFound, r.Change);
    }
}
