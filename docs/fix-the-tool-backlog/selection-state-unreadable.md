# selection-state-unreadable — Selection state (IsSelected) not exposed by snapshot/find

- **Captured:** 2026-07-17 (via flaui-autotrain)
- **Regression test:** `FlaUI.Mcp.Tests.Server.SelectionStateTests.Selected_list_item_state_is_not_exposed_by_snapshot_or_find`
- **Trait:** `Category=Desktop` (console-only)

## Steps to Reproduce
1. `desktop_select` a ListItem in an Explorer "Items View" list (or any UIA `ListItem` that advertises
   `[SelectionItem]`, e.g. TestApp's `ItemList`/`ItemA`).
2. `desktop_snapshot` the list — the element's state braces show only `{enabled,focusable,focused}`; there
   is no `selected` token even though the item is now selected.
3. `desktop_find` the same item — the match object exposes only `hasFocus`/`isEnabled`/`isOffscreen`; there
   is no `isSelected` field.
4. The container itself advertises `[Selection]` (`SelectionPattern`), but neither tool surfaces its
   `GetSelection()` either. A multi-select (e.g. anchor + Shift+Down) cannot be confirmed by any read tool.

## Code-level Mitigation
In `SnapshotEngine.FormatNode`/the snapshot-node build path (`src/FlaUI.Mcp.Core/Perception/SnapshotEngine.cs`),
read `el.Patterns.SelectionItem.PatternOrDefault?.IsSelected.ValueOrDefault` when the pattern is present and
add it as a `"selected"` state token alongside `enabled`/`focusable`/`focused`. In
`src/FlaUI.Mcp.Core/Perception/FindQuery.cs`, add an `IsSelected` field to the `FindMatch` record and
populate it the same way in `PerceptionManager`'s find path. Optionally also expose the container's
`SelectionPattern.GetSelection()` as a convenience (e.g. a `desktop_get_grid_cell`-adjacent read), but the
per-item state token is the minimum fix.
