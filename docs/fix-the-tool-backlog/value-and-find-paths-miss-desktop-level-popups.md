# value-and-find-paths-miss-desktop-level-popups — two perception paths root at `win` and cannot see a desktop-level popup

- **Captured:** 2026-07-29 (found by measurement during the SP1 `wait_for` contract consult, not by dogfooding)
- **Regression test:** none yet — see **Test-gen note**, the flavour question must be measured first
- **Trait:** `Category=Desktop` (console-only — needs a real window and a live popup)

## The inconsistency, stated exactly

`PopupFinder.SearchRoots` exists because a popup can live in one of two places
(`PopupFinder.cs:21-25`): a **desktop-level** child (Win32 `#32768` menus, older WPF `HwndWrapper`
hosts) or a **direct child of the owner window** (WPF/.NET 10+ context menus, `CT=Window cls=Popup`).
Its own doc comment says it is "shared by the query path and the action path".

Two perception paths do not use it and search bare `win` instead:

| Path | Root | Sees desktop-level popup? |
|---|---|---|
| `desktop_snapshot`, `wait_for` `exists`/`gone`/`enabled` | `BuildModelAsync` — `win` + `FindOwnerPopups` (`PerceptionManager.cs:424`, `:429`) | **yes** |
| `wait_for` `valueEquals` | `EvaluateSelectorValueAsync` — `win.FindAllDescendants` (`PerceptionManager.cs:558-571`) | **no** |
| `desktop_find` (unscoped) | `FindAsync` — `root = win` (`PerceptionManager.cs:467-469`) | **no** |

Every other perception and action path routes through `SearchRoots` (`PerceptionManager.cs:53`, `:67`,
`:85`, `:104`, `:230`, `:251`, `:273`, `:642`, `:644`; `WatchService.cs:70`; `WatchPump.cs:202`).
These two are the only ones that do not, which reads as oversight rather than design.

**Consequence.** For a desktop-level popup, `desktop_snapshot` shows a menu item under
`[Active Overlays]` and `wait_for until=exists` satisfies on it, while `desktop_find` returns no match
for the same element and `wait_for until=valueEquals` never satisfies. Two tools disagree about
whether the same element is there, with no diagnostic pointing at popup hosting as the reason.

## Steps to Reproduce

**Unverified — the popup flavour decides whether this reproduces, and that has not been measured.**
For a WPF context menu hosted as a window-child, `win.FindAllDescendants` *does* reach it, so the
TestApp fixture's `MenuTarget` menu (`MainWindow.xaml:94-103`) probably does **not** reproduce this.
The repro needs an app whose menu is a genuine desktop-level `#32768` host.

Intended sequence, against such an app:

1. Open the window and right-click (or otherwise open) its menu.
2. `desktop_snapshot` → the menu item appears under `[Active Overlays]`.
3. `desktop_wait_for` `until = "exists"` on that item's `automationId` → **satisfied**.
4. `desktop_find` with the same `automationId`, unscoped → **no match**.
5. `desktop_wait_for` `until = "valueEquals"` on the same item → **never satisfied**.

Steps 3 and 4 disagreeing is the defect.

## Code-level Mitigation

Route both paths through the same search roots the rest of the codebase uses.

- `EvaluateSelectorValueAsync` (`PerceptionManager.cs:558-571`): iterate
  `PopupFinder.SearchRoots(win, desktop)` and take the first `NotOffscreen` match across all roots,
  rather than searching `win` alone. `SearchRoots[0]` is `win`, so window matches keep winning first
  and existing behaviour is preserved.
- `FindAsync` (`PerceptionManager.cs:467-469`): when `scopeRef` is empty, search across
  `SearchRoots(win, desktop)` instead of `win` alone. Note this path mints durable refs and reports
  `TotalMatches`/`IsTruncated`, so the per-root results must be concatenated **in root order** before
  the `max` cap is applied, or truncation would silently prefer window matches over popup ones.

**Watch the dedup hazard.** `SnapshotEngine` prunes a window-child popup from the main walk so it
appears exactly once, under `[Active Overlays]` — `PopupGraftingTests.cs:34-37` pins that. Neither
path above has such pruning, so for a **window-child** popup the naive fix would return the element
twice, once via `win` and once via the popup root. De-duplicate by `RuntimeId` across roots.

## Test-gen note

No runnable repro is generated yet because the first question is empirical, not expressible as an
assertion: **which popup flavours actually land at the desktop level on this OS build?** Measure that
against a real `#32768` host before writing the test; if desktop-level menus turn out to be
unreachable on Windows 11 26200, the `find` half may be latent rather than live, and the fix is still
correct but the severity drops. The dedup hazard above is testable immediately either way, using the
existing `MenuTarget` fixture.
