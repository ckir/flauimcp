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

/// <summary>Tier-2 partial repro for docs/fix-the-tool-backlog/grid-cell-indexes-realized-rows.md
///
/// desktop_get_grid_cell's row index addresses the REALIZED set of a virtualized list, not the backing
/// list, so an absolute row number returns a plausible WRONG row with no error. Measured live 2026-09-09:
/// with the viewport scrolled to ~450 in a 500-file folder, row 1 read back "item-449.txt".
///
/// This arranges the folder, opens it, and invokes the tool at a row that CANNOT be realized (499 of 500,
/// with nothing scrolled). The correct post-fix behaviour is not asserted yet — the mitigation in the
/// backlog is to surface RowCount/ColumnCount on the SUCCESS path so a caller can see the index space is
/// not the one it assumed, and there is no field to assert until that lands. So this ends in Assert.Fail
/// carrying what was observed.
///
/// Desktop-gated: needs a real Explorer window with a live virtualized list. The repo's WPF fixture has no
/// virtualized grid, and inflating that shared fixture to hundreds of rows would perturb the tree-shape
/// assertions other tests make against it.</summary>
[Trait("Category", "Desktop")]
public class GridCellVirtualizedRowTests
{
    private const int FileCount = 500;

    [Fact]
    public async Task Get_grid_cell_row_index_addresses_only_realized_rows()
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

            // Explorer hands the folder to a shell process and may take a moment to paint a titled window.
            for (var attempt = 0; attempt < 20 && handle is null; attempt++)
            {
                await Task.Delay(500);
                try
                {
                    handle = await windows.OpenByTitleAsync(expectedTitle);
                }
                catch (Exception)
                {
                    // Not up yet; keep polling until the attempt budget runs out.
                }
            }

            if (handle is null)
            {
                Assert.Fail($"grid-cell-indexes-realized-rows: arrange failed - no window titled '{expectedTitle}' appeared.");
            }

            var perception = new PerceptionManager(windows, new RefRegistry(), new SnapshotCache());
            var snapshot = await perception.SnapshotAsync(handle.Value, new SnapshotOptions
            {
                InteractiveOnly = false,
                IncludeOffscreen = true,
                FullProperties = true,
            });

            var listLine = snapshot.Tree.Split('\n').FirstOrDefault(l => l.Contains("List \"Items View\""));
            if (listLine is null)
            {
                Assert.Fail("grid-cell-indexes-realized-rows: arrange failed - no List \"Items View\" node in the Explorer snapshot.");
            }

            var listRef = listLine.TrimStart().Split(']')[0].TrimStart('[');

            // The shipped rule this backlog retires claimed a details-view [Grid] returns off-screen rows
            // WITHOUT scrolling. If that were true, the last row of a 500-item folder would read back here.
            var lastRow = FileCount - 1;
            string observed;
            try
            {
                var cell = await perception.GetGridCellAsync(handle.Value, listRef, lastRow, 0, 4000);
                observed = $"row {lastRow} returned '{cell.Value}' (the rule's premise would make this 'item-{lastRow:D3}.txt')";
            }
            catch (FlaUI.Mcp.Core.Errors.ToolException ex) when (ex.Code == FlaUI.Mcp.Core.Errors.ToolErrorCode.GridCellOutOfRange)
            {
                observed = $"row {lastRow} threw GridCellOutOfRange ('{ex.Message}') - the grid exposes only realized rows, "
                         + $"so the off-screen catch-22 is NOT solved by get_grid_cell";
            }

            Assert.Fail($"grid-cell-indexes-realized-rows: observed {observed}; correct behavior not asserted yet - see backlog");
        }
        finally
        {
            if (handle is not null)
            {
                try
                {
                    await windows.CloseAsync(handle.Value);
                }
                catch (Exception)
                {
                    // Leaving a window open must not mask the finding this test exists to report.
                }
            }

            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception)
            {
                // Temp cleanup is best-effort.
            }
        }
    }
}
