using System;
using System.Diagnostics;
using System.IO;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class ProcessRunnerTests
{
    [Fact]
    public void Captures_stdout_and_exit_code()
    {
        var r = ProcessRunner.Run("cmd.exe", new[] { "/c", "echo", "hello" }, null, TimeSpan.FromSeconds(30));

        Assert.Equal(0, r.Code);
        Assert.Contains("hello", r.Output);
    }

    [Fact]
    public void Reports_the_process_exit_code()
    {
        var r = ProcessRunner.Run("cmd.exe", new[] { "/c", "exit", "3" }, null, TimeSpan.FromSeconds(30));
        Assert.Equal(3, r.Code);
    }

    [Fact]
    public void Missing_executable_reports_NotFound_minus_one()
    {
        var r = ProcessRunner.Run("definitely-not-a-real-exe-xyzzy", Array.Empty<string>(), null, TimeSpan.FromSeconds(5));
        Assert.Equal(ProcessRunner.NotFound, r.Code);
    }

    // THE HAZARD THIS FILE EXISTS FOR. A hidden process that blocks on stdin would hang Setup
    // forever, and the user would never get the product. The runner must kill it and move on.
    [Fact]
    public void A_hung_process_is_killed_and_reports_TimedOut_rather_than_blocking()
    {
        var sw = Stopwatch.StartNew();

        var r = ProcessRunner.Run("cmd.exe", new[] { "/c", "pause" }, null, TimeSpan.FromSeconds(2));

        sw.Stop();
        Assert.Equal(ProcessRunner.TimedOut, r.Code);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"runner blocked for {sw.Elapsed} — the timeout did not fire");
    }

    [Fact]
    public void Runs_in_the_given_working_directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-cwd-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        var r = ProcessRunner.Run("cmd.exe", new[] { "/c", "cd" }, dir, TimeSpan.FromSeconds(30));

        Assert.Equal(0, r.Code);
        Assert.Contains(Path.GetFileName(dir), r.Output);
    }

    [Fact]
    public void A_null_working_directory_inherits_the_current_one()
    {
        var r = ProcessRunner.Run("cmd.exe", new[] { "/c", "cd" }, null, TimeSpan.FromSeconds(30));

        Assert.Equal(0, r.Code);
        Assert.Contains(Path.GetFileName(Directory.GetCurrentDirectory()), r.Output);
    }

    // On failure the REASON is usually on stderr; a warning that says only "exited 1" is useless to
    // the user who has to fix it.
    [Fact]
    public void On_failure_stderr_is_kept_so_the_reason_survives()
    {
        var r = ProcessRunner.Run("cmd.exe", new[] { "/c", "echo boom 1>&2 && exit 4" }, null, TimeSpan.FromSeconds(30));

        Assert.Equal(4, r.Code);
        Assert.Contains("boom", r.Output);
    }

    // ...but on SUCCESS the output must stay clean: `claude plugin list --json` output is parsed,
    // and a stray stderr line (a deprecation notice, say) would corrupt the JSON.
    [Fact]
    public void On_success_stderr_is_excluded_so_json_output_stays_parseable()
    {
        var r = ProcessRunner.Run("cmd.exe", new[] { "/c", "echo warning: noisy 1>&2 && echo [] " }, null, TimeSpan.FromSeconds(30));

        Assert.Equal(0, r.Code);
        Assert.DoesNotContain("noisy", r.Output);
        Assert.Contains("[]", r.Output);
    }
}
