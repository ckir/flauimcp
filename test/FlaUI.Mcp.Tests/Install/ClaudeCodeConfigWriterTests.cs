using System.Collections.Generic;
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
        var calls = new List<(string file, string[] args, string? cwd)>();
        var w = new ClaudeCodeConfigWriter((file, args, cwd) => { calls.Add((file, args, cwd)); return new RunResult(0, ""); });

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
        var calls = new List<(string file, string[] args, string? cwd)>();
        var w = new ClaudeCodeConfigWriter((file, args, cwd) => { calls.Add((file, args, cwd)); return new RunResult(0, ""); });

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
        var w = new ClaudeCodeConfigWriter((_, args, _) => { calls.Add(args); return new RunResult(0, ""); });
        w.Uninstall();
        Assert.Equal(new[] { "mcp", "remove", "--scope", "user", "flaui-mcp" }, Assert.Single(calls));
    }

    [Fact]
    public void Install_reports_NotFound_when_runner_signals_missing_cli()
    {
        var w = new ClaudeCodeConfigWriter((_, _, _) => new RunResult(ProcessRunner.NotFound, ""));
        var r = w.Install(@"C:\x\flaui-mcp.exe");
        Assert.Equal(AgentChange.NotFound, r.Change);
    }

    // The mcp verbs are global (--scope user) and must NOT inherit Setup's working directory as a
    // hidden input. Only the plugin-disable path (Task 6) is CWD-bound, and it passes one explicitly.
    [Fact]
    public void The_mcp_verbs_pass_no_working_directory()
    {
        var cwds = new List<string?>();
        var w = new ClaudeCodeConfigWriter((_, _, cwd) => { cwds.Add(cwd); return new RunResult(0, ""); });

        w.Install(@"C:\x\flaui-mcp.exe");
        w.Uninstall();

        Assert.All(cwds, c => Assert.Null(c));
    }

    // A hung `claude` must not be reported as a successful registration.
    [Fact]
    public void Install_does_not_report_Created_when_the_runner_times_out()
    {
        var w = new ClaudeCodeConfigWriter((_, _, _) => new RunResult(ProcessRunner.TimedOut, ""));
        var r = w.Install(@"C:\x\flaui-mcp.exe");
        Assert.NotEqual(AgentChange.Created, r.Change);
    }

    // The false-success that made this fix necessary: a killed process must never be reported as a
    // completed removal. CliRouter.Report only flags trouble on Failed, so anything softer is silent.
    [Fact]
    public void Uninstall_reports_Failed_when_the_runner_times_out()
    {
        var w = new ClaudeCodeConfigWriter((_, _, _) => new RunResult(ProcessRunner.TimedOut, ""));
        Assert.Equal(AgentChange.Failed, w.Uninstall().Change);
    }

    [Fact]
    public void Uninstall_reports_Failed_when_the_cli_fails()
    {
        var w = new ClaudeCodeConfigWriter((_, _, _) => new RunResult(1, "not logged in"));
        var r = w.Uninstall();
        Assert.Equal(AgentChange.Failed, r.Change);
        Assert.Contains("not logged in", r.Detail);   // the reason must survive into the log
    }

    [Fact]
    public void Install_reports_Failed_when_the_cli_fails()
    {
        var w = new ClaudeCodeConfigWriter((_, _, _) => new RunResult(1, "not logged in"));
        var r = w.Install(@"C:\x\flaui-mcp.exe");
        Assert.Equal(AgentChange.Failed, r.Change);
        Assert.Contains("not logged in", r.Detail);
    }

    [Fact]
    public void A_failure_detail_stays_on_one_line_so_it_cannot_break_the_log_format()
    {
        var w = new ClaudeCodeConfigWriter((_, _, _) => new RunResult(1, "line one\r\nline two"));
        var r = w.Install(@"C:\x\flaui-mcp.exe");
        Assert.DoesNotContain("\n", r.Detail);
        Assert.DoesNotContain("\r", r.Detail);
    }
}
