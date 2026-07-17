using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class ClaudeCollisionRestoreTests
{
    private static string TempState()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-state-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class FakeCli
    {
        public string ListJson = "[]";
        public int EnableCode;
        public readonly List<(string[] args, string? cwd)> Calls = new();

        public RunResult Run(string file, string[] args, string? cwd)
        {
            if (args.Length >= 2 && args[0] == "plugin" && args[1] == "list")
                return new RunResult(0, ListJson);
            Calls.Add((args, cwd));
            return new RunResult(EnableCode, "");
        }
    }

    [Fact]
    public void With_no_marker_nothing_is_enabled()
    {
        var cli = new FakeCli { ListJson = """[ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "enabled": false } ]""" };

        var warning = new ClaudeCollisionRemedy(cli.Run, TempState()).Restore();

        Assert.Null(warning);
        Assert.Empty(cli.Calls);   // never re-enable a plugin the USER disabled
    }

    [Fact]
    public void A_recorded_user_entry_is_re_enabled_and_the_marker_consumed()
    {
        var cli = new FakeCli { ListJson = """[ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "enabled": false } ]""" };
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "user", null) });

        var warning = new ClaudeCollisionRemedy(cli.Run, s).Restore();

        Assert.Null(warning);
        var c = Assert.Single(cli.Calls);
        Assert.Equal(new[] { "plugin", "enable", "flaui-mcp@flaui-mcp", "--scope", "user" }, c.args);
        Assert.Null(c.cwd);
        Assert.False(File.Exists(CollisionMarker.PathIn(s)), "R7: the marker must be consumed");
    }

    [Fact]
    public void A_recorded_local_entry_is_re_enabled_from_its_project_path()
    {
        var cli = new FakeCli
        {
            ListJson = """[ { "id": "flaui-mcp@flaui-mcp", "scope": "local", "enabled": false, "projectPath": "C:\\Projects\\MyCode" } ]"""
        };
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\Projects\MyCode") });

        // dirExists: _ => true — the synthetic projectPath represents an EXISTING project. The
        // round-1 deleted-project guard would otherwise skip it, because the fake path is not a real
        // directory on the test machine (that guard runs the default Directory.Exists).
        new ClaudeCollisionRemedy(cli.Run, s, _ => true).Restore();

        var c = Assert.Single(cli.Calls);
        Assert.Equal(@"C:\Projects\MyCode", c.cwd);
    }

    [Fact]
    public void Every_recorded_entry_is_restored_at_its_own_scope_and_project()
    {
        var cli = new FakeCli
        {
            ListJson = """
            [
              { "id": "flaui-mcp@flaui-mcp", "scope": "local", "enabled": false, "projectPath": "C:\\a" },
              { "id": "flaui-mcp@flaui-mcp", "scope": "local", "enabled": false, "projectPath": "C:\\b" },
              { "id": "flaui-mcp@flaui-mcp", "scope": "user",  "enabled": false }
            ]
            """
        };
        var s = TempState();
        CollisionMarker.Record(s, new[]
        {
            new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\a"),
            new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\b"),
            new DisabledEntry("flaui-mcp@flaui-mcp", "user", null),
        });

        // dirExists: _ => true — C:\a and C:\b are synthetic paths standing in for EXISTING projects;
        // without this the round-1 deleted-project guard skips them (they are not real directories) and
        // only the user-scope entry is restored.
        new ClaudeCollisionRemedy(cli.Run, s, _ => true).Restore();

        Assert.Equal(new[] { @"C:\a", @"C:\b", null }, cli.Calls.Select(c => c.cwd).ToArray());
    }

    // R2: the user may have uninstalled the plugin after we disabled it. Enabling something that is
    // gone throws or orphans a reference in their settings.
    [Fact]
    public void An_entry_whose_plugin_is_gone_is_not_enabled_and_is_reported()
    {
        var cli = new FakeCli { ListJson = "[]" };                       // they removed it themselves
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "user", null) });

        var warning = new ClaudeCollisionRemedy(cli.Run, s).Restore();

        Assert.Empty(cli.Calls);
        Assert.NotNull(warning);
        Assert.Contains("no longer installed", warning);
        Assert.False(File.Exists(CollisionMarker.PathIn(s)), "a moot marker must still be consumed");
    }

    // A failed restore must degrade to a warning and never throw: cleanup must not derail uninstall.
    [Fact]
    public void A_failed_enable_is_reported_with_a_manual_recourse_and_never_throws()
    {
        var cli = new FakeCli
        {
            ListJson = """[ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "enabled": false } ]""",
            EnableCode = 1,
        };
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "user", null) });

        var warning = new ClaudeCollisionRemedy(cli.Run, s).Restore();

        Assert.NotNull(warning);
        Assert.Contains("could not re-enable", warning);
        Assert.Contains("claude plugin enable flaui-mcp@flaui-mcp --scope user", warning);   // the user can fix it
    }

    // R7's failure mode, stated as a test: a marker that survives its uninstall would later
    // re-enable a plugin the user had deliberately disabled.
    [Fact]
    public void The_marker_is_consumed_even_when_a_restore_fails()
    {
        var cli = new FakeCli
        {
            ListJson = """[ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "enabled": false } ]""",
            EnableCode = 1,
        };
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "user", null) });

        new ClaudeCollisionRemedy(cli.Run, s).Restore();

        Assert.False(File.Exists(CollisionMarker.PathIn(s)));
    }

    [Fact]
    public void Restoring_an_entry_the_user_already_re_enabled_is_harmless()
    {
        var cli = new FakeCli { ListJson = """[ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "enabled": true } ]""" };
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "user", null) });

        var warning = new ClaudeCollisionRemedy(cli.Run, s).Restore();

        Assert.Null(warning);
        Assert.Single(cli.Calls);   // enable is idempotent; the marker is consumed either way
        Assert.False(File.Exists(CollisionMarker.PathIn(s)));
    }

    // If we cannot even see what is installed, we cannot restore — and we must KEEP the marker. It
    // is still an accurate record of a plugin we disabled and have not put back; consuming it here
    // would strand the user's plugin disabled with no record anywhere that we did it. (R7's
    // stale-marker hazard is about surviving a SUCCESSFUL consume — this is not one.)
    [Fact]
    public void A_missing_claude_cli_at_restore_time_KEEPS_the_marker_and_reports_a_manual_recourse()
    {
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\proj") });

        // dirExists: _ => true so the recorded project counts as present and its recourse line is emitted
        // (the round-2 fix omits deleted projects from the recourse; here the project still exists).
        var warning = new ClaudeCollisionRemedy((_, _, _) => new RunResult(ProcessRunner.NotFound, ""), s, _ => true).Restore();

        Assert.NotNull(warning);
        Assert.True(File.Exists(CollisionMarker.PathIn(s)), "consuming the marker here loses the record forever");
        Assert.Contains("still disabled", warning);
        Assert.Contains("claude plugin enable flaui-mcp@flaui-mcp --scope local", warning);
        Assert.Contains(@"C:\proj", warning);          // they need to know WHERE to run it
    }

    // agy panel round 2: the CLI-missing early return must ALSO skip deleted-project entries in its
    // manual-recourse text — otherwise it tells the user to `cd` into a directory that is gone.
    [Fact]
    public void The_manual_recourse_omits_entries_whose_project_directory_is_gone()
    {
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\gone") });

        var warning = new ClaudeCollisionRemedy((_, _, _) => new RunResult(ProcessRunner.NotFound, ""), s, p => p != @"C:\gone").Restore();

        Assert.NotNull(warning);
        Assert.DoesNotContain(@"C:\gone", warning);      // no impossible "run from C:\gone"
        Assert.Contains("no longer exist", warning);      // says why there is nothing to do
        Assert.True(File.Exists(CollisionMarker.PathIn(s)), "cannot verify => must KEEP the marker");
    }

    // Symmetric to Apply's deleted-project guard (agy panel, round 1). A project deleted after we
    // disabled the copy but before uninstall: we must NOT try to enable at a dead cwd (Process.Start
    // throws Win32Exception on a missing working directory, which surfaces as a failed run and would
    // otherwise print an impossible "run it from <deleted path>" recourse), must not crash, and must
    // still consume the marker. The stale row can still appear in `list --json`, so the presence
    // check alone does not cover this.
    [Fact]
    public void A_recorded_entry_whose_project_directory_is_gone_is_skipped_not_run()
    {
        var cli = new FakeCli
        {
            ListJson = """[ { "id": "flaui-mcp@flaui-mcp", "scope": "local", "enabled": false, "projectPath": "C:\\gone" } ]"""
        };
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\gone") });

        var warning = new ClaudeCollisionRemedy(cli.Run, s, p => p != @"C:\gone").Restore();

        Assert.Empty(cli.Calls);                                 // never spawned `enable` at a dead cwd
        Assert.NotNull(warning);
        Assert.Contains("no longer exists", warning);
        Assert.DoesNotContain("run it from", warning);           // no impossible recourse
        Assert.False(File.Exists(CollisionMarker.PathIn(s)));    // R7: marker still consumed
    }

    // THE FULL ROUND TRIP — the property that actually matters to a user.
    [Fact]
    public void Install_then_uninstall_leaves_the_users_plugin_exactly_as_it_was_found()
    {
        var s = TempState();
        var enabled = true;
        var calls = new List<string[]>();
        RunResult Run(string f, string[] a, string? cwd)
        {
            if (a[0] == "plugin" && a[1] == "list")
                return new RunResult(0, $$"""[ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "enabled": {{(enabled ? "true" : "false")}} } ]""");
            calls.Add(a);
            // Faithful: disable of an already-off entry is a no-op exit 1 (measured); of an on entry, exit 0.
            if (a[1] == "disable") { if (!enabled) return new RunResult(1, "already disabled"); enabled = false; return new RunResult(0, ""); }
            if (a[1] == "enable") { enabled = true; return new RunResult(0, ""); }
            return new RunResult(0, "");
        }

        new ClaudeCollisionRemedy(Run, s).Apply();
        Assert.False(enabled, "install must disable the colliding copy");

        new ClaudeCollisionRemedy(Run, s).Restore();

        Assert.True(enabled, "uninstall must put the user's plugin back");
        Assert.Equal(new[] { "disable", "enable" }, calls.Select(c => c[1]).ToArray());
        Assert.False(File.Exists(CollisionMarker.PathIn(s)));
    }

    [Fact]
    public void A_corrupt_marker_at_uninstall_warns_with_recourse_and_keeps_the_file()
    {
        var cli = new FakeCli();
        var s = TempState();
        File.WriteAllText(CollisionMarker.PathIn(s), "{ torn half-written");   // Corrupt

        var warning = new ClaudeCollisionRemedy(cli.Run, s).Restore();

        Assert.NotNull(warning);
        Assert.Contains("unreadable", warning);
        Assert.Contains("claude plugin list", warning);
        Assert.Contains(ClaudeCollisionRemedy.MarketplaceId, warning);
        Assert.Empty(cli.Calls);                                   // never tried to enable
        Assert.True(File.Exists(CollisionMarker.PathIn(s)), "a corrupt marker must be kept, not silently deleted");
    }

    [Fact]
    public void A_future_version_marker_at_uninstall_warns_and_keeps_the_file()
    {
        var cli = new FakeCli();
        var s = TempState();
        File.WriteAllText(CollisionMarker.PathIn(s), """{ "version": 2, "disabled": [] }""");

        var warning = new ClaudeCollisionRemedy(cli.Run, s).Restore();

        Assert.NotNull(warning);
        Assert.Contains("newer flaui-mcp", warning);
        Assert.Empty(cli.Calls);
        Assert.True(File.Exists(CollisionMarker.PathIn(s)));
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("corrupt")]
    [InlineData("future")]
    [InlineData("present")]
    public void The_bak_sweep_runs_on_every_restore_branch(string kind)
    {
        var cli = new FakeCli { ListJson = """[ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "enabled": false } ]""" };
        var s = TempState();
        var stale = CollisionMarker.PathIn(s) + ".bak-20200101000000";
        File.WriteAllText(stale, "old");

        switch (kind)
        {
            case "absent":  break;                                                             // no marker file
            case "corrupt": File.WriteAllText(CollisionMarker.PathIn(s), "{ torn"); break;
            case "future":  File.WriteAllText(CollisionMarker.PathIn(s), """{ "version": 2, "disabled": [] }"""); break;
            case "present": CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "user", null) }); break;
        }

        new ClaudeCollisionRemedy(cli.Run, s).Restore();

        Assert.False(File.Exists(stale), $"the stale .bak must be swept on the '{kind}' branch");
    }

    [Fact]
    public void A_timed_out_re_enable_is_reported_as_timed_out_not_minus_two()
    {
        var cli = new FakeCli
        {
            ListJson = """[ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "enabled": false } ]""",
            EnableCode = ProcessRunner.TimedOut,
        };
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "user", null) });

        var warning = new ClaudeCollisionRemedy(cli.Run, s).Restore();

        Assert.NotNull(warning);
        Assert.Contains("timed out", warning);
        Assert.DoesNotContain("-2", warning);
    }

    [Fact]
    public void A_not_found_re_enable_is_reported_as_could_not_be_run_not_minus_one()
    {
        var cli = new FakeCli
        {
            ListJson = """[ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "enabled": false } ]""",
            EnableCode = ProcessRunner.NotFound,
        };
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "user", null) });

        var warning = new ClaudeCollisionRemedy(cli.Run, s).Restore();

        Assert.NotNull(warning);
        Assert.Contains("could not be run", warning);
        Assert.DoesNotContain("-1", warning);
    }

    // And the inverse property: what we never disabled, we never enable.
    [Fact]
    public void A_plugin_the_user_disabled_is_still_disabled_after_install_then_uninstall()
    {
        var s = TempState();
        var enabled = false;                       // the user disabled it themselves, before we ever ran
        var calls = new List<string[]>();
        RunResult Run(string f, string[] a, string? cwd)
        {
            if (a[0] == "plugin" && a[1] == "list")
                return new RunResult(0, $$"""[ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "enabled": {{(enabled ? "true" : "false")}} } ]""");
            calls.Add(a);
            // Faithful: the user already disabled it, so our detector's disable is a no-op exit 1.
            if (a[1] == "disable") { if (!enabled) return new RunResult(1, "already disabled"); enabled = false; return new RunResult(0, ""); }
            if (a[1] == "enable") { enabled = true; return new RunResult(0, ""); }
            return new RunResult(0, "");
        }

        new ClaudeCollisionRemedy(Run, s).Apply();
        new ClaudeCollisionRemedy(Run, s).Restore();

        Assert.False(enabled, "we re-enabled a plugin the USER had disabled");
        Assert.DoesNotContain(calls, c => c[1] == "enable");   // the invariant: never re-enable what we did not disable
        Assert.Empty(CollisionMarker.Read(s));                 // and nothing was recorded to restore
    }

    [Fact]
    public void A_present_marker_with_only_malformed_entries_is_consumed_and_returns_null()
    {
        var cli = new FakeCli();
        var s = TempState();
        // A structurally valid v1 marker whose only entry is malformed (id is a number) -> ReadState
        // classifies it Present with zero usable entries. Nothing to restore; it must not survive uninstall.
        File.WriteAllText(CollisionMarker.PathIn(s), """{ "version": 1, "disabled": [ { "id": 1 } ] }""");

        var warning = new ClaudeCollisionRemedy(cli.Run, s).Restore();

        Assert.Null(warning);
        Assert.Empty(cli.Calls);
        Assert.False(File.Exists(CollisionMarker.PathIn(s)), "an all-malformed marker should be consumed, not left forever");
    }
}
