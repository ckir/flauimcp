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

    // ---------------- S1 (round 3): the snapshot walk was still failing OPEN ----------------

    /// <summary>ROUND 3, finding S1 — the L2 fix was only HALF applied, and the missing half was the
    /// biggest wire surface of all.
    ///
    /// `SnapshotEngine.Build` never calls `ElementContent.Classify`; it calls the classifier DIRECTLY with
    /// `aid`/`name` locals that were read through a swallowing `Safe(...)`. So on the snapshot path a
    /// throwing identity read still produced "", still matched no rule, and still emitted the element
    /// Visible — exactly the hole L2 closed everywhere else. Fixed by reading those two identities with a
    /// throw-reporting overload and failing closed when rules are configured.
    ///
    /// ⚠ HONEST SCOPE, same as the L2 pin above: no fixture can make a UIA property read throw on demand,
    /// so what is pinned here is the DEFAULT-PATH invariant that the fix must not disturb — the branch is
    /// gated on HasRules, so a walk with no rules must render byte-identically to before. The fail-closed
    /// branch itself is verified by reading, and is documented at the call site.</summary>
    [Fact]
    public void The_snapshot_default_path_is_unchanged_by_the_S1_fix()
    {
        var visible = SnapshotEngine.Render(new SnapshotModel(new[] { Node("Username", Sensitivity.Visible) }),
                                            new SnapshotOptions());
        Assert.Contains("\"Username\"", visible);
        Assert.DoesNotContain("[REDACTED]", visible);
        Assert.DoesNotContain("redacted:", visible);
    }

    /// <summary>ROUND 4, finding Q-h2 — an over-redaction my own S1 fix introduced.
    ///
    /// The eager snapshot walk fails closed on an unreadable identity, but it must only do so when a rule
    /// COULD apply to that process. Gating on HasRules meant that configuring any rule for app A caused a
    /// benign control in unrelated app B to be withheld the moment one of its property reads threw — the
    /// operator sees [REDACTED] on Notepad because they wrote a rule for their billing system.
    ///
    /// `RedactionRule.Matches` short-circuits on the process predicate before touching an identity thunk,
    /// so the LAZY path was already immune; only the eager walk needed this. Pinned at the classifier,
    /// which is where the predicate now lives and where it is directly testable.</summary>
    [Fact]
    public void A_process_scoped_rule_cannot_apply_to_an_unrelated_process()
    {
        var scoped = SensitivityClassifier.ForRules(new[]
        {
            new RedactionRule("billing", processName: "Contoso.Billing", global: false,
                              automationId: "Acct", automationIdPattern: null, namePattern: null)
        });

        Assert.True(scoped.HasRules);                            // rules DO exist...
        Assert.False(scoped.CouldMatchProcess("notepad"));        // ...but none can apply here
        Assert.True(scoped.CouldMatchProcess("Contoso.Billing")); // and they do apply there
        Assert.True(scoped.CouldMatchProcess("CONTOSO.BILLING")); // process match is case-insensitive
    }

    /// <summary>The other direction: a GLOBAL rule opts into a machine-wide blast radius by design, so it
    /// could match anywhere and the eager walk must fail closed everywhere. Without this fact an
    /// implementation that simply answered "false" would pass the fact above.</summary>
    [Fact]
    public void A_global_rule_could_apply_to_any_process()
    {
        var global = SensitivityClassifier.ForRules(new[]
        {
            new RedactionRule("any-secret", processName: null, global: true,
                              automationId: null, automationIdPattern: null, namePattern: "(?i)secret")
        });

        Assert.True(global.CouldMatchProcess("notepad"));
        Assert.True(global.CouldMatchProcess("anything-at-all"));
        Assert.True(global.CouldMatchProcess(null));
    }

    /// <summary>No rules ⇒ nothing could ever match, so the eager fail-closed can never fire on the
    /// default path (spec §4.4). This is the invariant Q-h1 asked about, pinned rather than reasoned.</summary>
    [Fact]
    public void With_no_rules_nothing_could_match_any_process()
    {
        Assert.False(SensitivityClassifier.OsOnly.HasRules);
        Assert.False(SensitivityClassifier.OsOnly.CouldMatchProcess("notepad"));
        Assert.False(SensitivityClassifier.OsOnly.CouldMatchProcess(null));
    }

    /// <summary>ROUND 5, finding Q-j2 — the PROCESS identity was the third unknowable, and it fell open.
    /// `RedactionRule.Matches` asks `!Global &amp;&amp; !string.Equals(ProcessName, processName)`, and
    /// `string.Equals("Contoso.Billing", null)` is FALSE — so an unidentifiable process made every
    /// process-scoped rule silently NOT match, and the content went out in the clear. Unknown was being
    /// read as "does not apply".</summary>
    [Fact]
    public void An_unknown_process_cannot_rule_out_a_process_scoped_rule()
    {
        var scoped = SensitivityClassifier.ForRules(new[]
        {
            new RedactionRule("billing", processName: "Contoso.Billing", global: false,
                              automationId: "Acct", automationIdPattern: null, namePattern: null)
        });

        Assert.True(scoped.CannotEvaluateFor(null));   // unknowable, so it must withhold
        Assert.True(scoped.CannotEvaluateFor(""));
        Assert.True(scoped.CannotEvaluateFor("   "));
        Assert.True(scoped.CouldMatchProcess(null));   // and the eager walk sees it as "could match"

        Assert.False(scoped.CannotEvaluateFor("notepad")); // a KNOWN name is answerable, not unknowable
    }

    /// <summary>The anti-over-redaction half. A GLOBAL rule applies regardless of process, so an unknown
    /// process name makes nothing unknowable and must NOT trigger a fail-closed — withholding there would
    /// cost real content for zero security gain. Without this fact, an implementation that simply returned
    /// true whenever the name was empty would pass the fact above.</summary>
    [Fact]
    public void An_unknown_process_is_not_unknowable_when_every_rule_is_global()
    {
        var global = SensitivityClassifier.ForRules(new[]
        {
            new RedactionRule("any-secret", processName: null, global: true,
                              automationId: null, automationIdPattern: null, namePattern: "(?i)secret")
        });

        Assert.False(global.CannotEvaluateFor(null));
        Assert.True(global.CouldMatchProcess(null));  // still could match — because it is GLOBAL, not unknown
    }

    /// <summary>Default path: no rules ⇒ nothing is unknowable, so an unknown process changes nothing.</summary>
    [Fact]
    public void With_no_rules_an_unknown_process_is_not_unknowable()
    {
        Assert.False(SensitivityClassifier.OsOnly.CannotEvaluateFor(null));
        Assert.False(SensitivityClassifier.OsOnly.CouldMatchProcess(null));
    }

    // ---------------- round 6: the sentinel that hid an unidentifiable process ----------------

    /// <summary>ROUND 6 — the placeholder was a LEAK. `WindowManager.SafeProcessName(int)` returned the
    /// literal string "unknown" when it could not resolve a process. "unknown" is not in the denylist, so
    /// `DenylistedWindowsVisibleAsync` asked `IsDenied("unknown")`, got false, and a FULL-DESKTOP CAPTURE
    /// proceeded over a credential-store window whose process merely could not be named. The fail-closed
    /// handling never reached that path because the placeholder had already disguised "could not
    /// determine" as a determined answer.
    ///
    /// The sentinel is gone; the listing reports null and policy fails closed. This pins the policy end of
    /// that: an unidentifiable process must be DENIED, whatever spelling it arrives in.</summary>
    [Fact]
    public void An_unidentifiable_process_is_denied_but_a_plausible_placeholder_is_not_special()
    {
        Assert.True(PerceptionPolicy.IsDenied(null));   // the real answer now
        Assert.True(PerceptionPolicy.IsDenied(""));

        // ⚠ And "unknown" is NOT quietly treated as unidentifiable: it is an ordinary string, so a process
        // genuinely called that is served normally. The fix removed the placeholder at its SOURCE rather
        // than teaching policy to recognise one — otherwise the next placeholder reopens the hole.
        Assert.False(PerceptionPolicy.IsDenied("unknown"));
    }

    /// <summary>⚠ THIS PINS A CRASH I ALMOST SHIPPED. Making the listing's process name nullable feeds null
    /// into `MultiplexerHint.For`, whose `Multiplexers` set uses `StringComparer.Ordinal` — and that
    /// comparer's `GetHashCode(null)` THROWS, so `HashSet.Contains(null)` raises ArgumentNullException
    /// rather than returning false.
    ///
    /// A design consult asserted it "returns false, cleanly omitting the terminal hint". It does not.
    /// Believing that would have crashed desktop_list_windows for every window with an unresolvable
    /// process — turning a redaction fix into an outage on the most-called tool in the server.</summary>
    [Fact]
    public void The_multiplexer_hint_survives_an_unidentifiable_process()
    {
        var ex = Record.Exception(() => FlaUI.Mcp.Core.Windows.MultiplexerHint.For(null));
        Assert.Null(ex);                                                            // must not throw
        Assert.Null(FlaUI.Mcp.Core.Windows.MultiplexerHint.For(null));              // and no hint
        Assert.NotNull(FlaUI.Mcp.Core.Windows.MultiplexerHint.For("WindowsTerminal")); // still recognises
    }

    /// <summary>A window whose process cannot be identified must never be claimed to BE the app just
    /// launched — we cannot confirm it. Behaviour was already correct; pinned because the signature became
    /// nullable and a future "helpful" null-coalesce here would silently match the wrong window.</summary>
    [Fact]
    public void An_unidentifiable_window_is_not_the_app_we_launched()
    {
        Assert.False(FlaUI.Mcp.Core.Windows.LaunchedWindowMatcher.IsExpectedApp("notepad", null));
        Assert.True(FlaUI.Mcp.Core.Windows.LaunchedWindowMatcher.IsExpectedApp("notepad", "notepad"));
    }

    /// <summary>ROUND 8 — pins a behaviour change I made while claiming not to.
    ///
    /// `Process.GetProcessById(0)` does NOT throw: it returns the System Idle Process, name "Idle". So the
    /// pre-consolidation resolver reported a pid-0 window as process "Idle", and `IsDenied("Idle")` is
    /// false — it was SERVED. With the `pid &lt;= 0` guard it resolves to null, which is DENIED.
    ///
    /// Both I and the reviewing peer asserted that GetProcessById(0) throws. It does not. The claim was
    /// only settled by running it, which is why this fact exists: null here is a DECISION about an
    /// unattributable window, not an incidental consequence of an exception someone assumed.</summary>
    [Fact]
    public void A_pid_of_zero_is_unattributable_not_the_Idle_process()
    {
        // The framework fact the old code accidentally depended on — asserted so that if a future runtime
        // changes it, this reads as a deliberate decision rather than a coincidence that stopped holding.
        Assert.Equal("Idle", System.Diagnostics.Process.GetProcessById(0).ProcessName);

        // ...and the decision: we do not attribute a window to the Idle process, which owns no windows.
        Assert.Null(FlaUI.Mcp.Core.Perception.ProcessIdentity.OfPid(0));
        Assert.Null(FlaUI.Mcp.Core.Perception.ProcessIdentity.OfPid(-1));

        // A real pid still resolves — this must not have become "deny everything".
        Assert.NotNull(FlaUI.Mcp.Core.Perception.ProcessIdentity.OfPid(System.Environment.ProcessId));
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
