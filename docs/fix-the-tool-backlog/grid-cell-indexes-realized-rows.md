# grid-cell-indexes-realized-rows — desktop_get_grid_cell silently returns the wrong row on a virtualized list

- **Captured:** 2026-09-09 (via flaui-autotrain)
- **Regression test:** `FlaUI.Mcp.Tests.Perception.GridCellVirtualizedRowTests.Get_grid_cell_row_index_addresses_only_realized_rows`
- **Trait:** `Category=Desktop` (console-only — needs a real Explorer window with a virtualized list)

## Steps to Reproduce

Measured live 2026-09-09 against Explorer's details view on a folder of 500 files
(`item-000.txt` … `item-499.txt`):

1. Open the folder in Explorer; `desktop_find controlType:List` → the `Items View` list ref.
2. `desktop_find controlType:ListItem max:600` → only **19** items are realized (`item-000..018`);
   the status bar still reports 500 items.
3. Scroll the realized window down to ~450 (8 × `desktop_scroll amount:50`). Re-find: the realized set is
   now **21** rows — `item-000` remains anchored and `isOffscreen:true`, plus `item-449..468`.
4. `desktop_get_grid_cell(row: 0, col: 0)` → `"item-000.txt"`.
5. **`desktop_get_grid_cell(row: 1, col: 0)` → `"item-449.txt"`, NOT `"item-001.txt"`.** A real-looking
   filename from the wrong position, returned with **no error**.
6. `desktop_get_grid_cell(row: 499, col: 0)` → `GridCellOutOfRange, "Cell (499,0) is outside the 21x4 grid."`

So the row index addresses the **realization window**, not the list. `RowCount` tracks realized items
(19 → 21 as you scroll), never the 500-row backing list.

**Why this matters more than an ordinary limitation:** step 5 is a *silent* wrong answer, the worst failure
shape — the caller gets a plausible filename and no signal. And testing at `row 0` (step 4) *falsely
confirms* correct behaviour, because absolute index 0 and realized index 0 coincide; only `row 1` or deeper
discriminates.

⚠ This **contradicts the shipped GROWTH rule** that said a details-view `[Grid]` "returns off-screen rows
WITHOUT scrolling" and was "the recovery for the off-screen catch-22". It is not: step 6 shows the catch-22
is not solved. The rule survived because of a grain of truth — a realized-but-offscreen row (`item-000`,
`isOffscreen:true`) *is* readable. That rule is retired by the same curate run that filed this.

## Code-level Mitigation

The underlying indexing is Explorer's UIA provider behaviour (a virtualized DirectUIHWND exposes only
realized items through `GridPattern`), and no C# change can make `GetItem(450, 0)` return `item-450.txt`.
What our code CAN remove is the **silence**, which is the actual harm:

**Return the grid's dimensions on the SUCCESS path of `desktop_get_grid_cell`, not only in the error text.**
`GridCellOutOfRange` already computes and reports `"outside the 21x4 grid"`, so `RowCount`/`ColumnCount` are
available at that call site. Add them to the success payload (e.g. `rowCount`, `columnCount` alongside
`value`/`controlType`/`automationId`). A caller asking for row 450 of a folder it believes holds 500 items
then sees `rowCount: 21` in the very same response and can tell immediately that the index space is not the
one it assumed.

Optionally stronger (decide when implementing): also surface the resolved row's own identity (the owning
`ListItem`'s `AutomationId` — Explorer numbers these `"0"`, `"449"`, …) so a caller can verify it got the row
it asked for. The cell's own `automationId` is `System.ItemNameDisplay` for every row and is therefore
useless for this check.

The tool description should also stop implying absolute addressing, and say the index is into the realized
set.
