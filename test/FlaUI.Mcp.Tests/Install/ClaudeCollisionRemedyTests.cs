using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class ClaudeCollisionRemedyTests
{
    private static string TempState()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-state-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    // A faithful model of the `claude plugin` CLI as MEASURED 2026-07-16. The remedy's correctness
    // rests on these EXACT semantics, so the fake reproduces them rather than letting a test hand-pick
    // exit codes it wishes were true:
    //   - `list --json` from cwd C: every installed row appears (id/scope/projectPath are global), but
    //     a LOCAL row's `enabled` is resolved against C — it reads its true state ONLY when C is its own
    //     projectPath, and reads false from anywhere else (THE bug the remedy must survive). A user
    //     row's `enabled` is global.
    //   - `disable id --scope s` from cwd: turns the target off and exits 0 if it was on; exits 1
    //     ("already disabled") and writes nothing if it was already off.
    //   - `enable id --scope s` from cwd: turns it on and exits 0 even for an id that does not exist
    //     (measured: enable does not validate), which is why restore must check presence first.
    private sealed class FakeClaude
    {
        // True per-target state. resolutionDir is "" for user scope, else the projectPath.
        private readonly Dictionary<string, bool> _state = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string id, string scope, string? pp)> _rows = new();
        public readonly List<(string[] args, string? cwd)> Calls = new();
        public bool ListFails;
        public string? RawListOverride;
        // cwd (or "" for null) -> forced disable exit code, WITHOUT mutating state. Models a genuine
        // failure (permissions, timeout) as distinct from the benign already-disabled exit 1.
        public readonly Dictionary<string, int> ForceDisableCode = new();
        // cwds at which `list --json` fails — used to model a re-read that cannot complete, so the
        // "disable failed AND state unverifiable" branch is reachable. Only the re-read passes a cwd.
        public readonly HashSet<string> FailListAtCwd = new();

        private static string ResDir(string scope, string? pp) =>
            scope.Equals("user", StringComparison.OrdinalIgnoreCase) ? "" : (pp ?? "");
        private static string Key(string id, string scope, string resDir) =>
            $"{id}|{scope}|{resDir}".ToLowerInvariant();

        public FakeClaude Install(string id, string scope, string? pp, bool enabled)
        {
            _rows.Add((id, scope, pp));
            _state[Key(id, scope, ResDir(scope, pp))] = enabled;
            return this;
        }

        public RunResult Run(string file, string[] args, string? cwd)
        {
            Calls.Add((args, cwd));
            if (args.Length >= 2 && args[0] == "plugin" && args[1] == "list")
            {
                if (ListFails) return new RunResult(ProcessRunner.NotFound, "");
                if (cwd is not null && FailListAtCwd.Contains(cwd)) return new RunResult(ProcessRunner.NotFound, "");
                if (RawListOverride is not null) return new RunResult(0, RawListOverride);
                return new RunResult(0, ListJsonFrom(cwd));
            }
            if (args.Length >= 5 && args[0] == "plugin" && args[1] == "disable")
            {
                if (ForceDisableCode.TryGetValue(cwd ?? "", out var forced)) return new RunResult(forced, "");
                var key = Key(args[2], args[4], ResDir(args[4], cwd));
                if (_state.TryGetValue(key, out var on) && on) { _state[key] = false; return new RunResult(0, ""); }
                return new RunResult(1, $"Plugin \"{args[2]}\" is already disabled");   // message NOT parsed by the remedy
            }
            if (args.Length >= 5 && args[0] == "plugin" && args[1] == "enable")
            {
                _state[Key(args[2], args[4], ResDir(args[4], cwd))] = true;    // succeeds even for a fictitious id
                return new RunResult(0, "");
            }
            return new RunResult(0, "");
        }

        // `enabled` AS `list --json` would report it from `cwd`: user rows read their global state; a
        // local row reads its true state only when listed from its own project, else false.
        private string ListJsonFrom(string? cwd)
        {
            var items = _rows.Select(r =>
            {
                bool enabled = r.scope.Equals("user", StringComparison.OrdinalIgnoreCase)
                    ? _state.GetValueOrDefault(Key(r.id, r.scope, ""))
                    : string.Equals(cwd ?? "", r.pp ?? "", StringComparison.OrdinalIgnoreCase)
                        && _state.GetValueOrDefault(Key(r.id, r.scope, r.pp ?? ""));
                var pp = r.pp is null ? "" : $", \"projectPath\": {JsonStr(r.pp)}";
                return $"{{ \"id\": {JsonStr(r.id)}, \"scope\": {JsonStr(r.scope)}, \"enabled\": {(enabled ? "true" : "false")}{pp} }}";
            });
            return "[" + string.Join(",", items) + "]";
        }

        private static string JsonStr(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        public IEnumerable<(string[] args, string? cwd)> Disables =>
            Calls.Where(c => c.args.Length >= 2 && c.args[0] == "plugin" && c.args[1] == "disable");
    }

    // dirExists defaults to always-true: the fakes use synthetic projectPaths that do not exist on the
    // test machine, and only the deleted-project test cares about the guard.
    private static ClaudeCollisionRemedy Remedy(FakeClaude cli, string state, Func<string, bool>? dirExists = null)
        => new(cli.Run, state, dirExists ?? (_ => true));

    // Guard the guard: prove the fake actually reproduces the measured CWD-resolution bug, so the
    // regression test below is not vacuously green against a fake that lists the true state everywhere.
    [Fact]
    public void The_fake_reproduces_the_cwd_resolution_bug()
    {
        var cli = new FakeClaude().Install("flaui-mcp@flaui-mcp", "local", @"C:\Proj", enabled: true);
        Assert.Contains("\"enabled\": false", cli.Run("claude", new[] { "plugin", "list", "--json" }, null).Output);         // global: WRONG
        Assert.Contains("\"enabled\": true", cli.Run("claude", new[] { "plugin", "list", "--json" }, @"C:\Proj").Output);    // own cwd: right
    }

    [Fact]
    public void No_marketplace_copy_means_no_disable_and_no_marker()
    {
        var cli = new FakeClaude().Install("other@thing", "user", null, true);
        var s = TempState();

        var warning = Remedy(cli, s).Apply();

        Assert.Null(warning);
        Assert.Empty(cli.Disables);
        Assert.Empty(CollisionMarker.Read(s));
    }

    [Fact]
    public void An_enabled_user_scope_copy_is_disabled_at_user_scope_with_no_cwd()
    {
        var cli = new FakeClaude().Install("flaui-mcp@flaui-mcp", "user", null, true);
        var s = TempState();

        Remedy(cli, s).Apply();

        var d = Assert.Single(cli.Disables);
        Assert.Equal(new[] { "plugin", "disable", "flaui-mcp@flaui-mcp", "--scope", "user" }, d.args);
        Assert.Null(d.cwd);
        Assert.Equal(new DisabledEntry("flaui-mcp@flaui-mcp", "user", null), Assert.Single(CollisionMarker.Read(s)));
    }

    // THE REGRESSION THAT MOTIVATES THE WHOLE MECHANISM. A local copy that is genuinely ENABLED at its
    // own project reads `enabled: false` from the global list (Setup's cwd has no .claude). A design
    // that trusted the global `enabled` would skip it and leave the collision live. Mutation-as-detector
    // disables it anyway, because it acts at the project's own cwd. This test FAILS against the old
    // read-based Apply and PASSES against the mutation-based one.
    [Fact]
    public void A_local_copy_the_global_list_wrongly_reports_disabled_is_still_disabled()
    {
        var cli = new FakeClaude().Install("flaui-mcp@flaui-mcp", "local", @"C:\Proj", enabled: true);
        var s = TempState();

        Remedy(cli, s).Apply();

        var d = Assert.Single(cli.Disables);
        Assert.Equal(@"C:\Proj", d.cwd);
        Assert.Equal(new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\Proj"), Assert.Single(CollisionMarker.Read(s)));
    }

    // THE CWD-BINDING CONTRACT. `claude plugin disable` has no flag to target another project, and
    // --scope local writes the CURRENT directory's .claude/settings.local.json. Fired from Setup's
    // cwd it would litter a stray settings file and disable nothing.
    [Fact]
    public void A_local_scope_copy_is_disabled_from_its_own_project_path()
    {
        var cli = new FakeClaude().Install("flaui-mcp@flaui-mcp", "local", @"C:\Projects\MyCode", enabled: true);
        var s = TempState();

        Remedy(cli, s).Apply();

        var d = Assert.Single(cli.Disables);
        Assert.Equal(new[] { "plugin", "disable", "flaui-mcp@flaui-mcp", "--scope", "local" }, d.args);
        Assert.Equal(@"C:\Projects\MyCode", d.cwd);
    }

    [Fact]
    public void Every_entry_is_disabled_at_its_own_scope_and_its_own_project()
    {
        var cli = new FakeClaude()
            .Install("flaui-mcp@flaui-mcp", "local", @"C:\a", enabled: true)
            .Install("flaui-mcp@flaui-mcp", "project", @"C:\b", enabled: true)
            .Install("flaui-mcp@flaui-mcp", "user", null, enabled: true);
        var s = TempState();

        Remedy(cli, s).Apply();

        var d = cli.Disables.ToList();
        Assert.Equal(3, d.Count);
        Assert.Equal(new[] { @"C:\a", @"C:\b", null }, d.Select(x => x.cwd).ToArray());
        Assert.Equal(new[] { "local", "project", "user" }, d.Select(x => x.args[4]).ToArray());
        Assert.Equal(3, CollisionMarker.Read(s).Count);
    }

    // R1 + R5. Already disabled with no marker => the USER did this. Never re-record it, and SAY so —
    // the correct rule here is invisible, and an unreported decision is indistinguishable from a bug.
    // Note: mutation-as-detector still ATTEMPTS the disable (a no-op exit 1), then re-reads to confirm
    // it is off; the invariant is "nothing recorded + a warning", not "no disable attempted".
    [Fact]
    public void An_already_disabled_copy_with_no_marker_is_left_alone_and_reported()
    {
        var cli = new FakeClaude().Install("flaui-mcp@flaui-mcp", "user", null, enabled: false);
        var s = TempState();

        var warning = Remedy(cli, s).Apply();

        Assert.Empty(CollisionMarker.Read(s));
        Assert.NotNull(warning);
        Assert.Contains("already disabled", warning);
    }

    // The R1 hazard itself: a repair or minor-version re-install must not rewrite the marker. The
    // entry is already off and we already have its record, so the re-read confirms it and we stay silent.
    [Fact]
    public void A_reinstall_over_our_own_disable_leaves_the_marker_untouched()
    {
        var cli = new FakeClaude().Install("flaui-mcp@flaui-mcp", "user", null, enabled: false);
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "user", null) });
        var before = File.ReadAllText(CollisionMarker.PathIn(s));

        var warning = Remedy(cli, s).Apply();

        Assert.Null(warning);
        Assert.Equal(before, File.ReadAllText(CollisionMarker.PathIn(s)));
    }

    [Fact]
    public void A_newly_enabled_entry_is_merged_into_an_existing_marker()
    {
        var cli = new FakeClaude()
            .Install("flaui-mcp@flaui-mcp", "local", @"C:\a", enabled: false)
            .Install("flaui-mcp@flaui-mcp", "local", @"C:\b", enabled: true);
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\a") });

        Remedy(cli, s).Apply();

        Assert.Equal(2, CollisionMarker.Read(s).Count);   // C:\a preserved, C:\b merged in
    }

    // We only record what we actually transitioned. A GENUINE disable failure (exit 1 with the entry
    // still enabled) must be reported and NOT recorded — recording it would make uninstall "restore" a
    // plugin that was never disabled by us. Distinguished from already-disabled by the re-read.
    [Fact]
    public void A_genuinely_failed_disable_is_reported_and_NOT_recorded()
    {
        var cli = new FakeClaude().Install("flaui-mcp@flaui-mcp", "user", null, enabled: true);
        cli.ForceDisableCode[""] = 1;   // user-scope cwd is "" — fail the disable without turning it off
        var s = TempState();

        var warning = Remedy(cli, s).Apply();

        Assert.NotNull(warning);
        Assert.Contains("could not disable", warning);
        Assert.Empty(CollisionMarker.Read(s));
    }

    [Fact]
    public void A_timed_out_disable_is_reported_and_NOT_recorded()
    {
        var cli = new FakeClaude().Install("flaui-mcp@flaui-mcp", "user", null, enabled: true);
        cli.ForceDisableCode[""] = ProcessRunner.TimedOut;
        var s = TempState();

        var warning = Remedy(cli, s).Apply();

        Assert.NotNull(warning);
        Assert.Contains("timed out", warning);
        Assert.Empty(CollisionMarker.Read(s));
    }

    // The defensive third branch of the exit-1 handler: disable genuinely fails (exit 1, entry still
    // enabled) AND the re-read cannot complete. We must warn and record NOTHING — recording an
    // unconfirmed transition would make uninstall "restore" (enable) a plugin we never disabled.
    [Fact]
    public void A_failed_disable_whose_state_cannot_be_reread_is_reported_and_NOT_recorded()
    {
        var cli = new FakeClaude().Install("flaui-mcp@flaui-mcp", "local", @"C:\p", enabled: true);
        cli.ForceDisableCode[@"C:\p"] = 1;    // genuine failure, entry stays enabled
        cli.FailListAtCwd.Add(@"C:\p");        // ...and the re-read at its own cwd cannot complete
        var s = TempState();

        var warning = Remedy(cli, s).Apply();

        Assert.NotNull(warning);
        Assert.Contains("could not verify", warning);
        Assert.Empty(CollisionMarker.Read(s));
    }

    [Fact]
    public void One_failed_disable_does_not_stop_the_others()
    {
        var cli = new FakeClaude()
            .Install("flaui-mcp@flaui-mcp", "local", @"C:\a", enabled: true)
            .Install("flaui-mcp@flaui-mcp", "user", null, enabled: true);
        cli.ForceDisableCode[@"C:\a"] = 1;   // the local one fails; the user one still succeeds
        var s = TempState();

        var warning = Remedy(cli, s).Apply();

        Assert.Equal(2, cli.Disables.Count());                             // both attempted
        Assert.NotNull(warning);
        Assert.Equal(new DisabledEntry("flaui-mcp@flaui-mcp", "user", null), Assert.Single(CollisionMarker.Read(s)));
    }

    // A non-user entry whose project directory is gone cannot load the plugin, and disable would have
    // nowhere valid to run from. Skip it — do not attempt the disable, do not record it.
    [Fact]
    public void A_row_whose_project_directory_is_gone_is_skipped()
    {
        var cli = new FakeClaude().Install("flaui-mcp@flaui-mcp", "local", @"C:\gone", enabled: true);
        var s = TempState();

        var warning = Remedy(cli, s, dirExists: p => p != @"C:\gone").Apply();

        Assert.Empty(cli.Disables);
        Assert.Empty(CollisionMarker.Read(s));
        Assert.NotNull(warning);
        Assert.Contains("no longer exists", warning);
    }

    [Fact]
    public void A_missing_claude_cli_is_reported_and_disables_nothing()
    {
        var cli = new FakeClaude { ListFails = true };
        var s = TempState();

        var warning = Remedy(cli, s).Apply();

        Assert.NotNull(warning);
        Assert.Empty(cli.Disables);
        Assert.Empty(CollisionMarker.Read(s));
    }

    [Fact]
    public void Unparseable_list_output_is_reported_and_disables_nothing()
    {
        var cli = new FakeClaude { RawListOverride = "<html>not json</html>" };
        var s = TempState();

        var warning = Remedy(cli, s).Apply();

        Assert.NotNull(warning);
        Assert.Empty(cli.Disables);
    }

    // agy panel round 2: a non-empty array that parses to ZERO of our rows (e.g. a future claude
    // release renames the `id` field) must surface as a warning, NOT be read as "no collisions" and
    // silently skipped — that would leave a live collision entirely unreported.
    [Fact]
    public void A_list_that_parses_to_no_rows_but_is_not_an_empty_array_is_reported_not_skipped()
    {
        var cli = new FakeClaude { RawListOverride = """[ { "pluginId": "flaui-mcp@flaui-mcp", "scope": "user", "enabled": true } ]""" };
        var s = TempState();

        var warning = Remedy(cli, s).Apply();

        Assert.NotNull(warning);
        Assert.Empty(cli.Disables);
    }

    // agy panel round 2: when the marker cannot be written, the summary must NOT promise a restore it
    // cannot deliver — the record-failure warning already says the opposite, and joining both is
    // self-contradictory to the one human who reads it.
    [Fact]
    public void When_the_marker_cannot_be_written_the_summary_does_not_promise_a_restore()
    {
        var cli = new FakeClaude().Install("flaui-mcp@flaui-mcp", "user", null, enabled: true);
        var s = TempState();
        Directory.CreateDirectory(CollisionMarker.PathIn(s));   // a DIRECTORY where the marker file must go

        var warning = Remedy(cli, s).Apply();

        Assert.NotNull(warning);
        Assert.Contains("will not re-enable it automatically", warning);              // the honest part
        Assert.DoesNotContain("they will be re-enabled if you uninstall", warning);   // the promise is withheld
    }

    // R5 (goal 6): two list rows resolve to the SAME entry (differ only in path casing, which the fake
    // treats as one target). We disable it via the first row; the second row's disable is then a no-op
    // exit 1 and the re-read shows it off. Without Concat(justDisabled) the marker (still empty at this
    // point) does not contain it, so it is wrongly blamed on the user. With Concat it is recognized as
    // our own disable and stays silent.
    [Fact]
    public void A_second_row_for_an_entry_we_just_disabled_is_not_blamed_on_the_user()
    {
        var cli = new FakeClaude()
            .Install("flaui-mcp@flaui-mcp", "local", @"C:\Proj", enabled: true)
            .Install("flaui-mcp@flaui-mcp", "local", @"c:\proj", enabled: true);   // same target, case-variant path
        var s = TempState();

        var warning = Remedy(cli, s).Apply();

        Assert.DoesNotContain("assuming you did", warning ?? "");
        Assert.Single(CollisionMarker.Read(s));   // recorded exactly once
    }

    [Fact]
    public void The_inventory_is_enumerated_with_json_and_no_cwd()
    {
        var cli = new FakeClaude();
        Remedy(cli, TempState()).Apply();

        var list = Assert.Single(cli.Calls);
        Assert.Equal(new[] { "plugin", "list", "--json" }, list.args);
        Assert.Null(list.cwd);          // enumeration is global; only remediation is CWD-bound
    }

    // Round-2 AB (Critical): when Record RECOVERS from a corrupt marker it returns null (it DID record),
    // so Apply must still PROMISE the re-enable. The promise is suppressed only on a genuine record
    // failure — a corrupt-but-recovered marker is not one.
    [Fact]
    public void A_corrupt_marker_recovered_during_apply_still_promises_the_re_enable()
    {
        var cli = new FakeClaude().Install("flaui-mcp@flaui-mcp", "user", null, enabled: true);
        var s = TempState();
        File.WriteAllText(CollisionMarker.PathIn(s), "{ torn half-written");   // a Corrupt marker is present

        var warning = Remedy(cli, s).Apply();

        Assert.NotNull(warning);
        Assert.Contains("they will be re-enabled if you uninstall", warning);
        Assert.Equal(new DisabledEntry("flaui-mcp@flaui-mcp", "user", null), Assert.Single(CollisionMarker.Read(s)));
    }
}
