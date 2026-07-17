using FlaUI.Core.Definitions;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>Headless pin: <see cref="SnapshotDiff.Compute"/> must report a selection-only state change
/// (SelectionItemPattern IsSelected flip) in Changed. Otherwise a visionless driver that runs
/// desktop_select and then verifies via desktop_snapshot_diff sees zero changes and wrongly concludes the
/// select failed. Regression guard for the NodeState-missing-Selected defect (SnapshotNode carried Selected
/// but NodeState/State did not, so the diff equality check ignored selection).</summary>
public class SnapshotDiffSelectionTests
{
    // Mirrors DiffRedactionTests' N(...) positional ctor, but exposes Selected. Positional order:
    // ref, depth, indent, ct, aid, name, rect, enabled, focusable, focused, selected, isPassword, offscreen, rid, patterns, help.
    private static SnapshotNode Node(string @ref, string aid, bool selected, params int[] rid)
        => new(@ref, 0, "", ControlType.ListItem, aid, aid, System.Drawing.Rectangle.Empty,
               true, false, false, selected, false, false, rid, System.Array.Empty<string>(), "");

    private static SnapshotModel Model(params SnapshotItem[] nodes) => new(nodes);

    [Fact]
    public void Selection_only_change_is_reported_by_the_diff()
    {
        // Same identity (stable RuntimeId), everything equal except Selected flips false -> true.
        var baseline = Model(Node("e1", "ItemA", selected: false, 5));
        var current = Model(Node("e1", "ItemA", selected: true, 5));

        var d = SnapshotDiff.Compute("w1:1", baseline, "w1:2", current);

        var changed = Assert.Single(d.Changed);
        Assert.Equal("e1", changed.Ref);
        Assert.False(changed.Was.Selected);
        Assert.True(changed.Now.Selected);
    }

    [Fact]
    public void No_state_change_yields_no_changed_entry()
    {
        // Guard the other direction: an identical selected node across baseline/current is NOT a change.
        var baseline = Model(Node("e1", "ItemA", selected: true, 5));
        var current = Model(Node("e1", "ItemA", selected: true, 5));

        var d = SnapshotDiff.Compute("w1:1", baseline, "w1:2", current);

        Assert.Empty(d.Changed);
    }
}
