using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class ContentToolsTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public ContentToolsTests(TestAppFixture app) => _app = app;

    private async Task<(PerceptionManager mgr, WindowHandle handle, string gridRef)> SnapshotAndFindGridAsync(WindowManager w, RefRegistry refs)
    {
        var mgr = new PerceptionManager(w, refs, new SnapshotCache());
        var handle = await w.OpenByPidAsync(_app.Process.Id);
        var snap = await mgr.SnapshotAsync(handle, new SnapshotOptions { InteractiveOnly = false, IncludeOffscreen = true, FullProperties = true });
        var gridRef = RefForAid(snap.Tree, "Grid");
        return (mgr, handle, gridRef);
    }

    // Find the e-ref for a control by its AutomationId. Match the AID TOKEN, not a bare substring
    // ("Grid" alone would also match the DataGrid ControlType). STATE-VERIFY the exact FullProperties
    // AID rendering against RefResolutionTests.cs / the SnapshotEngine renderer before relying on the
    // "aid=" token; if the renderer emits a different marker, match that one.
    private static string RefForAid(string tree, string aid)
    {
        var line = tree.Split('\n').First(l => l.Contains($"aid={aid}"));
        return line.TrimStart().Split(']')[0].TrimStart('[');
    }

    [Fact]
    public async Task Get_grid_cell_reads_a_known_cell_value()
    {
        using var d = new AutomationDispatcher();
        using var w = new WindowManager(d);
        var (mgr, handle, gridRef) = await SnapshotAndFindGridAsync(w, new RefRegistry());
        var cell = await mgr.GetGridCellAsync(handle, gridRef, 0, 0, 4000);
        Assert.Equal("r0c0", cell.Value);
        Assert.False(cell.IsPassword);
    }

    [Fact]
    public async Task Get_grid_cell_out_of_range_throws_GridCellOutOfRange()
    {
        using var d = new AutomationDispatcher();
        using var w = new WindowManager(d);
        var (mgr, handle, gridRef) = await SnapshotAndFindGridAsync(w, new RefRegistry());
        var ex = await Assert.ThrowsAsync<FlaUI.Mcp.Core.Errors.ToolException>(
            () => mgr.GetGridCellAsync(handle, gridRef, 99, 0, 4000));
        Assert.Equal(FlaUI.Mcp.Core.Errors.ToolErrorCode.GridCellOutOfRange, ex.Code);
    }
}
