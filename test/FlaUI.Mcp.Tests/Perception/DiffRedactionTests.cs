using System.Linq;
using FlaUI.Core.Definitions;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>Headless pins for INV-5 in the diff projection: <see cref="SnapshotDiff.Compute"/> must
/// render an IsPassword element's Name as "[REDACTED]" in added/removed/changed output (matching the
/// snapshot render at SnapshotEngine.cs:131), never the raw name — a diff must not become a name-oracle
/// that plain snapshot already redacts.</summary>
public class DiffRedactionTests
{
    private const string Secret = "hunter2-NEVER-LEAK";

    private static SnapshotNode N(string @ref, ControlType ct, string aid, string name,
        bool isPassword, bool enabled, params int[] rid)
        => new(@ref, 0, "", ct, aid, name, System.Drawing.Rectangle.Empty,
               enabled, false, false, isPassword, false, rid, System.Array.Empty<string>(), "");

    private static SnapshotModel Model(params SnapshotItem[] nodes) => new(nodes);

    [Fact]
    public void Added_password_node_name_is_redacted_not_the_secret()
    {
        var baseline = Model(N("e1", ControlType.Window, "win", "W", false, true, 1));
        var current = Model(
            N("e1", ControlType.Window, "win", "W", false, true, 1),
            N("e2", ControlType.Edit, "pwd", Secret, isPassword: true, enabled: true, 2));

        var d = SnapshotDiff.Compute("w1:1", baseline, "w1:2", current);

        var addedPwd = Assert.Single(d.Added, a => a.AutomationId == "pwd");
        Assert.Equal("[REDACTED]", addedPwd.Name);
        Assert.DoesNotContain(d.Added, a => a.Name.Contains(Secret));
    }

    [Fact]
    public void Removed_password_node_name_is_redacted_not_the_secret()
    {
        var baseline = Model(
            N("e1", ControlType.Window, "win", "W", false, true, 1),
            N("e2", ControlType.Edit, "pwd", Secret, isPassword: true, enabled: true, 2));
        var current = Model(N("e1", ControlType.Window, "win", "W", false, true, 1));

        var d = SnapshotDiff.Compute("w1:1", baseline, "w1:2", current);

        var removedPwd = Assert.Single(d.Removed, a => a.AutomationId == "pwd");
        Assert.Equal("[REDACTED]", removedPwd.Name);
        Assert.DoesNotContain(d.Removed, a => a.Name.Contains(Secret));
    }

    [Fact]
    public void Changed_password_node_reports_redacted_name_in_both_was_and_now()
    {
        // Same identity (stable RuntimeId), Enabled flips -> a "changed" entry. Was/Now Name must both
        // redact so a diff can never surface the password element's raw name via the state snapshot.
        var baseline = Model(N("e2", ControlType.Edit, "pwd", Secret, isPassword: true, enabled: true, 7));
        var current = Model(N("e2", ControlType.Edit, "pwd", Secret, isPassword: true, enabled: false, 7));

        var d = SnapshotDiff.Compute("w1:1", baseline, "w1:2", current);

        var changed = Assert.Single(d.Changed);
        Assert.Equal("[REDACTED]", changed.Was.Name);
        Assert.Equal("[REDACTED]", changed.Now.Name);
        Assert.DoesNotContain(Secret, changed.Was.Name);
        Assert.DoesNotContain(Secret, changed.Now.Name);
    }

    [Fact]
    public void Non_password_node_keeps_its_raw_name()
    {
        // Redaction must be targeted: a normal element's Name is untouched.
        var baseline = Model(N("e1", ControlType.Window, "win", "W", false, true, 1));
        var current = Model(
            N("e1", ControlType.Window, "win", "W", false, true, 1),
            N("e2", ControlType.Button, "ok", "Save", isPassword: false, enabled: true, 2));

        var d = SnapshotDiff.Compute("w1:1", baseline, "w1:2", current);

        var addedOk = Assert.Single(d.Added, a => a.AutomationId == "ok");
        Assert.Equal("Save", addedOk.Name);
    }
}
