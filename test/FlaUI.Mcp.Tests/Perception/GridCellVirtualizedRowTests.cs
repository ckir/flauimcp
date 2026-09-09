using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>Regression test for the silent-wrong-row defect, now FIXED.
///
/// `desktop_get_grid_cell`'s row index addresses the grid as UIA reports it, which on a virtualized list is
/// the REALIZED rows, not the backing collection. Measured on a 500-file Explorer folder scrolled to ~450:
/// row 1 returned "item-449.txt" — a real filename from the wrong position, with no error. Nothing in the
/// success payload let a caller notice, because the dimensions were computed and then only ever surfaced in
/// the GridCellOutOfRange message.
///
/// The fix puts RowCount/ColumnCount on the SUCCESS path. This test pins the property that makes the defect
/// detectable: on a folder of KnownFileCount items, a virtualized grid reports a RowCount far smaller, so a
/// caller comparing the two learns its absolute index is meaningless here.
///
/// Desktop-gated: needs a real Explorer window with a live virtualized list. The repo's WPF fixture has no
/// virtualized grid, and inflating that shared fixture would perturb the tree-shape assertions other tests
/// make against it.</summary>
[Trait("Category", "Desktop")]
public class GridCellVirtualizedRowTests
{
    private const int FileCount = 500;

    [Fact]
    public async Task Grid_dimensions_on_the_success_path_reveal_a_virtualized_index_space()
    {
        var folder = Path.Combine(Path.GetTempPath(), "flaui-gridcell-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(folder);
        for (var i = 0; i < FileCount; i++)
        {
            File.WriteAllText(Path.Combine(folder, $"item-{i:D3}.txt"), "row " + i);
        }

        var expectedTitle = Path.GetFileName(folder) + " - File Explorer";
        using var dispatcher = new AutomationDispatcher();
        using var windows = new WindowManager(dispatcher);
        WindowHandle? handle = null;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = folder, UseShellExecute = true })?.Dispose();

            for (var attempt = 0; attempt < 20 && handle is null; attempt++)
            {
                await Task.Delay(500);
                try { handle = await windows.OpenByTitleAsync(expectedTitle); }
                catch (Exception) { /* not up yet; keep polling within the attempt budget */ }
            }

            Assert.True(handle is not null, $"arrange failed: no window titled '{expectedTitle}' appeared");

            var perception = new PerceptionManager(windows, new RefRegistry(), new SnapshotCache());
            var snapshot = await perception.SnapshotAsync(handle.Value, new SnapshotOptions
            {
                InteractiveOnly = false,
                IncludeOffscreen = true,
                FullProperties = true,
            });

            var listLine = snapshot.Tree.Split('\n').FirstOrDefault(l => l.Contains("List \"Items View\""));
            Assert.True(listLine is not null, "arrange failed: no List \"Items View\" node in the Explorer snapshot");
            var listRef = listLine!.TrimStart().Split(']')[0].TrimStart('[');

            // Row 0 is deliberately NOT the probe: absolute index 0 and realized index 0 coincide there, so
            // reading it confirms nothing about which index space is in play. Read a row that certainly
            // exists in the grid's own space, and inspect the dimensions it reports.
            var cell = await perception.GetGridCellAsync(handle.Value, listRef, 0, 0, 4000);

            Assert.True(cell.RowCount > 0 && cell.ColumnCount > 0,
                $"expected real dimensions on the success path, got {cell.RowCount}x{cell.ColumnCount}");

            // THE POINT: the folder holds FileCount items, and the grid reports far fewer, because it counts
            // only realized rows. Before the fix a caller could not see this at all — which is exactly how an
            // absolute row number returned the wrong file in silence.
            Assert.True(cell.RowCount < FileCount,
                $"expected a virtualized grid to report FEWER rows than the {FileCount} files present, got " +
                $"RowCount={cell.RowCount}. If Explorer ever realizes every row up front this assertion is " +
                "wrong about the fixture, not about the tool.");

            // And the dimensions must agree with what an out-of-range call reports, so the two paths cannot
            // drift apart and leave the success path lying.
            var ex = await Assert.ThrowsAsync<FlaUI.Mcp.Core.Errors.ToolException>(
                () => perception.GetGridCellAsync(handle.Value, listRef, FileCount - 1, 0, 4000));
            Assert.Equal(FlaUI.Mcp.Core.Errors.ToolErrorCode.GridCellOutOfRange, ex.Code);
            Assert.Contains($"{cell.RowCount}x{cell.ColumnCount}", ex.Message);
        }
        finally
        {
            if (handle is not null)
            {
                try { await windows.CloseAsync(handle.Value); }
                catch (Exception) { /* a left-open window must not mask the assertion above */ }
            }
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception) { /* temp cleanup is best-effort */ }
        }
    }
}
