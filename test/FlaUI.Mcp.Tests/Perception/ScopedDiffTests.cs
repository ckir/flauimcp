using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class ScopedDiffTests
{
    private static SnapshotNode Node(string @ref, int depth, ControlType ct, string aid, string name, params int[] rid)
        => new(@ref, depth, new string(' ', depth * 2), ct, aid, name,
               System.Drawing.Rectangle.Empty, true, false, false, false, false, false, rid,
               System.Array.Empty<string>(), "");

    private static ElementDescriptor Desc(ControlType ct, string aid, string name, params int[] rid)
        => new(rid, ct, aid, name, null, System.Array.Empty<int>());

    [Fact]
    public void Subtree_returns_the_scope_node_and_its_descendants_only()
    {
        var model = new SnapshotModel(new SnapshotItem[]
        {
            Node("e1", 0, ControlType.Window, "win", "W", 1),
            Node("e2", 1, ControlType.Pane, "panel", "P", 2),      // scope
            Node("e3", 2, ControlType.Button, "ok", "OK", 3),      // descendant
            Node("e4", 2, ControlType.Edit, "box", "B", 4),        // descendant
            Node("e5", 1, ControlType.Button, "cancel", "X", 5),   // sibling of scope - excluded
        });

        var sub = SnapshotDiff.Subtree(model, Desc(ControlType.Pane, "panel", "P", 2));
        var aids = sub.Nodes.Select(n => n.AutomationId).ToList();
        Assert.Equal(new[] { "panel", "ok", "box" }, aids);
    }

    [Fact]
    public void Subtree_missing_scope_returns_empty()
    {
        var model = new SnapshotModel(new SnapshotItem[] { Node("e1", 0, ControlType.Window, "win", "W", 1) });
        var sub = SnapshotDiff.Subtree(model, Desc(ControlType.Pane, "ghost", "G", 9));
        Assert.Empty(sub.Nodes);
    }

    [Fact]
    public void Subtree_stops_at_a_shallower_or_equal_depth_node()
    {
        var model = new SnapshotModel(new SnapshotItem[]
        {
            Node("e1", 0, ControlType.Window, "win", "W", 1),
            Node("e2", 1, ControlType.Pane, "a", "A", 2),   // scope (depth 1)
            Node("e3", 2, ControlType.Button, "a1", "A1", 3),
            Node("e4", 1, ControlType.Pane, "b", "B", 4),   // depth 1 == scope depth => boundary
            Node("e5", 2, ControlType.Button, "b1", "B1", 5),
        });
        var sub = SnapshotDiff.Subtree(model, Desc(ControlType.Pane, "a", "A", 2));
        Assert.Equal(new[] { "a", "a1" }, sub.Nodes.Select(n => n.AutomationId));
    }
}

// append inside test/FlaUI.Mcp.Tests/Perception/ScopedDiffTests.cs
public class ScopedDiffDesktopTests
{
    [Fact]
    [Trait("Category", "Desktop")]
    public async Task Scoped_diff_reports_a_change_inside_the_scope_subtree()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new FlaUI.Mcp.Core.Threading.AutomationDispatcher();
        using var mgr = new FlaUI.Mcp.Core.Windows.WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var handle = await mgr.OpenByPidAsync(app.Process.Id);

        var baseline = await perception.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });
        var listRef = RefLineHelper.RefFor(baseline.Tree, "ItemList"); // the List subtree

        // Mutate the list (rebuild items) then diff ONLY that subtree.
        await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            win.FindFirstDescendant(cf => cf.ByAutomationId("RebuildItemsButton"))!.AsButton().Invoke();
            return true;
        });
        await Task.Delay(300);

        var d = await perception.DiffAsync(handle, baseline.SnapshotId, listRef);
        // The rebuilt list items surface as added/removed within the scoped subtree.
        Assert.True(d.Added.Count > 0 || d.Removed.Count > 0 || d.Changed.Count > 0);
    }
}
