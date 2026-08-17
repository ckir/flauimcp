using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;
using FlaUI.Core.Definitions;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>Pins for the three defects the FINAL AGY-CAPSTONE found in already-green code (L1, L2, L4).
///
/// All three had passed the full headless suite, the full Desktop suite AND the Task-12 source sweep, so
/// these facts exist to make sure the same holes cannot re-open quietly.</summary>
public class CapstoneFixTests
{
    private static SnapshotNode Node(string name, Sensitivity s) =>
        new("e1", 0, "", ControlType.Edit, "aid", name, new Rectangle(0, 0, 10, 10),
            true, true, false, false, s, false, new[] { 1 }, new string[0], "");

    // ---------------- L2: fail-closed when identity is unreadable ----------------

    /// <summary>⚠ HONEST SCOPE. This pins the PROVENANCE half of the L2 fix, not its trigger. Making the
    /// trigger fire needs a UIA provider whose AutomationId read THROWS while its value read succeeds, and
    /// no fixture can stage that — the same limitation RedactionOracleTests and PasswordRedactionTests
    /// already document for DEF-1. What is pinned here is that the outcome is REPRESENTABLE and reports
    /// itself honestly, which is what a future reader needs in order not to collapse it back into Rule.</summary>
    [Fact]
    public void An_unreadable_identity_is_a_redaction_with_its_own_provenance()
    {
        Assert.True(Sensitivity.UnreadableIdentity.Redact);
        Assert.Equal(RedactionSource.Unreadable, Sensitivity.UnreadableIdentity.Source);

        // NOT "rule:..." — inventing a rule name the operator never wrote sends them hunting for a rule
        // that does not exist. NOT "os" — no password flag was ever set.
        Assert.Equal("unreadable", ElementContent.RedactedBy(Sensitivity.UnreadableIdentity));
    }

    /// <summary>The three sources must stay mutually distinguishable ON THE WIRE. Collapsing any two makes
    /// an over-redaction undebuggable, which is the stated reason Sensitivity carries a Source at all.</summary>
    [Fact]
    public void Every_redaction_source_reports_a_distinct_wire_provenance()
    {
        var rule = new Sensitivity(true, RedactionSource.Rule, "acct");

        Assert.Equal("os", ElementContent.RedactedBy(Sensitivity.OsPassword));
        Assert.Equal("rule:acct", ElementContent.RedactedBy(rule));
        Assert.Equal("unreadable", ElementContent.RedactedBy(Sensitivity.UnreadableIdentity));
        Assert.Null(ElementContent.RedactedBy(Sensitivity.Visible));
    }

    /// <summary>An unreadable-identity node renders as redacted AND is marked apart from an operator rule,
    /// so an operator can tell "my rule fired" from "I could not read this element".</summary>
    [Fact]
    public void An_unreadable_identity_node_renders_with_its_own_marker()
    {
        var text = SnapshotEngine.Render(
            new SnapshotModel(new[] { Node("secret", Sensitivity.UnreadableIdentity) }),
            new SnapshotOptions());

        Assert.Contains("\"[REDACTED]\"", text);
        Assert.Contains("redacted:unreadable", text);
        Assert.DoesNotContain("redacted:rule:", text);
    }

    /// <summary>DEFAULT PATH, unchanged by the L2 fix: an OS password still carries NO marker. Adding one
    /// would change the rendered line of every password field in every existing install.</summary>
    [Fact]
    public void The_os_password_line_is_still_unmarked_after_the_L2_fix()
    {
        var text = SnapshotEngine.Render(new SnapshotModel(new[] { Node("secret", Sensitivity.OsPassword) }),
                                         new SnapshotOptions());
        Assert.Contains("\"[REDACTED]\"", text);
        Assert.DoesNotContain("redacted:", text);
    }

    // ---------------- L4: version-skewed state files must not become immortal ----------------

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "flaui-capstone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Writes a state file with an arbitrary stateVersion, bypassing ServerStateFile.Write (which
    /// always stamps the CURRENT version). A skewed file cannot otherwise be produced by this build.</summary>
    private static void WriteRaw(string dir, int stateVersion, int pid, DateTime startUtc)
        => File.WriteAllText(Path.Combine(dir, $"{pid}.json"), JsonSerializer.Serialize(new
        {
            stateVersion,
            pid,
            processStartTimeUtc = startUtc,
            redactionRulesPath = (string?)null,
            redactionRulesSha256 = (string?)null
        }));

    /// <summary>A pid that is definitely gone: start a process, wait for it to exit, reuse its id.</summary>
    private static int DeadPid()
    {
        var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        return p.Id;
    }

    /// <summary>THE L4 DEFECT. Skew forced Liveness to Unknown, and PruneDead only ever deleted Dead — so a
    /// state file written by a PREVIOUS schema version could never be removed, and check-redaction-rules
    /// (which reports 4 whenever any instance is Unknown) would have been pinned at exit 4 permanently
    /// after the first StateVersion bump, with manual deletion the only escape.
    ///
    /// ⚠ stateVersion 0 stands in for "any schema OLDER than this build". That direction matters: an older
    /// file was written by our OWN history, so its pid means what we think it means. The opposite direction
    /// is pinned below and must stay untouched.</summary>
    [Fact]
    public void An_OLDER_version_file_whose_process_is_dead_is_pruned()
    {
        var dir = TempDir();
        try
        {
            WriteRaw(dir, stateVersion: 0, pid: DeadPid(), startUtc: DateTime.UtcNow.AddMinutes(-5));
            Assert.Single(Directory.GetFiles(dir));

            ServerStateFile.PruneDead(dir);

            Assert.Empty(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>THE DIRECTION THAT MUST NOT BE PRUNED, and the reason the first draft of this fix was wrong.
    /// A file whose stateVersion is NEWER than ours was written by a server we cannot interpret — its `pid`
    /// need not even be a process id — so a dead-looking pid is not evidence of anything. Deleting it would
    /// destroy a newer server's state.
    ///
    /// This is the case the pre-existing pin
    /// (ServerStateFileTests.Prune_removes_only_dead_instances_and_never_unknown_ones) already protected;
    /// it caught the over-aggressive draft. Pinned again here, from the fix's own side, so the asymmetry
    /// cannot be flattened back into "prune any skew".</summary>
    [Fact]
    public void A_NEWER_version_file_is_never_pruned_even_with_a_dead_pid()
    {
        var dir = TempDir();
        try
        {
            WriteRaw(dir, stateVersion: 999, pid: DeadPid(), startUtc: DateTime.UtcNow.AddMinutes(-5));

            ServerStateFile.PruneDead(dir);

            Assert.Single(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>ACCEPTANCE CONSTRAINT (b) — the half that must NOT regress. A live server's file survives
    /// pruning even when its schema version is unrecognisable. Uses THIS process as the live one, so the
    /// pid and start time are genuine rather than simulated.</summary>
    [Fact]
    public void An_OLDER_version_file_whose_process_is_ALIVE_is_kept()
    {
        var dir = TempDir();
        try
        {
            var me = Process.GetCurrentProcess();
            WriteRaw(dir, stateVersion: 0, pid: me.Id, startUtc: me.StartTime.ToUniversalTime());

            ServerStateFile.PruneDead(dir);

            Assert.Single(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>THE FAIL-SAFE. A future schema that renames or drops pid deserializes it to 0, which means
    /// "cannot tell" — and cannot-tell must never delete. Without this guard the L4 fix would itself become
    /// a way to destroy state files it does not understand, which is the defect it exists to avoid.</summary>
    [Fact]
    public void A_file_with_no_usable_pid_is_left_alone()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "future.json"),
                              """{"stateVersion":2,"somethingElseEntirely":"no pid here"}""");

            ServerStateFile.PruneDead(dir);

            Assert.Single(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>Garbage is not ours to delete either — the pre-existing posture, pinned so the rewritten
    /// PruneDead does not quietly become more destructive than the version it replaced.</summary>
    [Fact]
    public void An_unparseable_file_is_left_alone()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "garbage.json"), "this is not json at all {{{");

            ServerStateFile.PruneDead(dir);

            Assert.Single(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}
