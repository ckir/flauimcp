using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

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
    public async Task Selected_list_item_state_is_exposed_by_snapshot_and_find()
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

        // ItemA IS selected now (SelectionItemPattern.IsSelected == true) and both read surfaces must
        // confirm it: the snapshot state braces carry a "selected" token, and the FindMatch carries
        // IsSelected == true.
        Assert.Contains("selected", itemLine);
        Assert.True(match.IsSelected);

        // And the real desktop_find WIRE response must carry isSelected end-to-end (FindTools projects
        // FindMatch into its own DTO — the internal field alone is not enough for a driver to read it).
        var findTools = new FindTools(perception);
        var findJson = await findTools.DesktopFind(handle.Id, automationId: "ItemA");
        var compact = findJson.Replace(" ", string.Empty);
        Assert.Contains("isSelected", findJson);
        Assert.DoesNotContain("isSelected\":false", compact); // ItemA is selected -> must serialize true
    }
}
