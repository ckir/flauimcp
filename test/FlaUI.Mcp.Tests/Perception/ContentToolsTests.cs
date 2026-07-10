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

    [Fact]
    public async Task Grid_select_selects_a_cell_without_error()
    {
        using var d = new AutomationDispatcher();
        using var w = new WindowManager(d);
        var (mgr, handle, gridRef) = await SnapshotAndFindGridAsync(w, new RefRegistry());
        var ok = await mgr.RunOnRefActionAsync(handle, gridRef,
            el => { Interactor.GridSelect(el, 1, 0); return true; }, 4000);
        Assert.True(ok);
    }

    [Fact]
    public async Task Grid_select_out_of_range_throws_GridCellOutOfRange()
    {
        using var d = new AutomationDispatcher();
        using var w = new WindowManager(d);
        var (mgr, handle, gridRef) = await SnapshotAndFindGridAsync(w, new RefRegistry());
        var ex = await Assert.ThrowsAsync<FlaUI.Mcp.Core.Errors.ToolException>(
            () => mgr.RunOnRefActionAsync(handle, gridRef, el => { Interactor.GridSelect(el, 0, 99); return true; }, 4000));
        Assert.Equal(FlaUI.Mcp.Core.Errors.ToolErrorCode.GridCellOutOfRange, ex.Code);
    }

    [Fact]
    public async Task Get_text_reads_full_truncates_and_redacts()
    {
        using var d = new AutomationDispatcher();
        using var w = new WindowManager(d);
        var refs = new RefRegistry();
        var mgr = new PerceptionManager(w, refs, new SnapshotCache());
        var handle = await w.OpenByPidAsync(_app.Process.Id);
        var snap = await mgr.SnapshotAsync(handle, new SnapshotOptions { InteractiveOnly = false, IncludeOffscreen = true, FullProperties = true });
        string RefFor(string aid) => RefForAid(snap.Tree, aid);

        var doc = await mgr.GetTextAsync(handle, RefFor("TextDoc"), selectionOnly: false, maxLength: 10000, fromEnd: false, 4000);
        Assert.Contains("line one", doc.Text);
        Assert.False(doc.Truncated);
        Assert.False(doc.IsPassword);
        Assert.Null(doc.TruncatedFrom);

        var trunc = await mgr.GetTextAsync(handle, RefFor("TextDoc"), selectionOnly: false, maxLength: 4, fromEnd: false, 4000);
        Assert.True(trunc.Truncated);
        Assert.Equal(4, trunc.Text.Length);
        Assert.Equal("tail", trunc.TruncatedFrom);

        var pwd = await mgr.GetTextAsync(handle, RefFor("Secret"), selectionOnly: false, maxLength: 10000, fromEnd: false, 4000);
        Assert.Equal("[REDACTED]", pwd.Text);
        Assert.True(pwd.IsPassword);
        Assert.DoesNotContain("hunter2", pwd.Text);
        Assert.Null(pwd.TruncatedFrom);
    }

    [Fact]
    public async Task Get_text_fromEnd_reads_tail_and_reports_truncatedFrom()
    {
        using var d = new AutomationDispatcher();
        using var w = new WindowManager(d);
        var refs = new RefRegistry();
        var mgr = new PerceptionManager(w, refs, new SnapshotCache());
        var handle = await w.OpenByPidAsync(_app.Process.Id);
        var snap = await mgr.SnapshotAsync(handle, new SnapshotOptions { InteractiveOnly = false, IncludeOffscreen = true, FullProperties = true });
        string RefFor(string aid) => RefForAid(snap.Tree, aid);

        // TestApp's TextDoc is "line one\nline two\nline three" (28 chars) — see MainWindow.xaml.
        var head = await mgr.GetTextAsync(handle, RefFor("TextDoc"), selectionOnly: false, maxLength: 4, fromEnd: false, 4000);
        Assert.True(head.Truncated);
        Assert.Equal("tail", head.TruncatedFrom);
        Assert.Equal("line", head.Text); // byte-identical to today's default head read

        var tail = await mgr.GetTextAsync(handle, RefFor("TextDoc"), selectionOnly: false, maxLength: 4, fromEnd: true, 4000);
        Assert.True(tail.Truncated);
        Assert.Equal("head", tail.TruncatedFrom);
        Assert.Equal("hree", tail.Text); // last 4 chars of "...line three"

        // Full-length reads (no truncation) are equivalent regardless of fromEnd.
        var full = await mgr.GetTextAsync(handle, RefFor("TextDoc"), selectionOnly: false, maxLength: 10000, fromEnd: true, 4000);
        Assert.False(full.Truncated);
        Assert.Null(full.TruncatedFrom);
        Assert.Contains("line three", full.Text);
    }
}
