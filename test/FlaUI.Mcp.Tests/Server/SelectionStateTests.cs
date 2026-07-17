using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

// fix-the-tool: docs/fix-the-tool-backlog/selection-state-unreadable.md
[Trait("Category", "Desktop")]
public class SelectionStateTests
{
    private static string RefFor(string tree, string aid)
    {
        foreach (var line in tree.Split('\n'))
            if (line.Contains("aid=" + aid))
            { int lb = line.IndexOf('['), rb = line.IndexOf(']'); return line.Substring(lb + 1, rb - lb - 1); }
        throw new Xunit.Sdk.XunitException($"no ref for aid={aid}");
    }

    [Fact]
    public async Task Selected_list_item_state_is_not_exposed_by_snapshot_or_find()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var tools = new InteractionTools(perception, mgr, new ServerOptions(ReadOnly: false, AllowElevation: false));
        var handle = await mgr.OpenByPidAsync(app.Process.Id);

        var snap = await perception.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });
        var itemARef = RefFor(snap.Tree, "ItemA");

        // Arrange: select ItemA via the real desktop_select tool path (UIA SelectionItemPattern.Select).
        Assert.DoesNotContain("error", await tools.DesktopSelect(handle.Id, itemARef));

        // Invoke: re-snapshot and re-find the now-selected item, exactly as a driver would to confirm
        // the selection landed.
        var afterSelect = await perception.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });
        var itemLine = System.Array.Find(afterSelect.Tree.Split('\n'), l => l.Contains("aid=ItemA"))!;

        var findResult = await perception.FindAsync(handle,
            new FindQuery("ItemA", null, "eq", null, false), max: 20, scopeRef: null);
        var match = Assert.Single(findResult.Matches);

        // KNOWN DEFECT: ItemA IS selected now (SelectionItemPattern.IsSelected == true) but neither read
        // surfaces it — the snapshot state braces only ever emit {enabled,focusable,focused}, and
        // FindMatch only carries IsEnabled/HasFocus/IsOffscreen. There is no way to confirm the selection.
        Assert.Fail("selection-state-unreadable: ItemA was selected via desktop_select, but the snapshot " +
            $"state braces (line: \"{itemLine.Trim()}\") show no \"selected\" token, and the desktop_find " +
            $"match (aid={match.AutomationId}, isEnabled={match.IsEnabled}, hasFocus={match.HasFocus}, " +
            $"isOffscreen={match.IsOffscreen}) has no isSelected field — selection state is unreadable via " +
            "either tool; correct behavior not asserted yet — see " +
            "docs/fix-the-tool-backlog/selection-state-unreadable.md");
    }
}
