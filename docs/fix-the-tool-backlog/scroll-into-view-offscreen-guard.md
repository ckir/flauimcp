# scroll-into-view-offscreen-guard — scroll_into_view wrongly refuses off-screen elements

- **Captured:** 2026-07-17 (via flaui-autotrain)
- **Regression test:** `FlaUI.Mcp.Tests.Perception.ScrollIntoViewOffscreenGuardTests.ScrollIntoView_wrongly_refuses_an_offscreen_element_ElementNotActionable`
- **Trait:** `Category=Desktop` (console-only)

## Steps to Reproduce
1. Locate an off-screen list/tab item (e.g. a Notepad/WT tab beyond the visible strip, or a virtualized
   list row below the fold; a `AutomationProperties.IsOffscreenBehavior="Offscreen"` element such as
   TestApp's `OffscreenButton` reproduces the same UIA state deterministically).
2. `desktop_select` the item — fails `ElementNotActionable` ("Element is off-screen; cannot act on it
   reliably."), suggesting recovery `"desktop_scroll_into_view then retry"`.
3. `desktop_scroll_into_view` the SAME element — the suggested recovery — ALSO fails
   `ElementNotActionable` with the identical message, even though UIA `ScrollItemPattern.ScrollIntoView`
   is specifically designed to realize off-screen items.
4. Root cause: both tools funnel through `PerceptionManager.RunOnRefActionAsync`/`RunOnSelectorActionAsync`
   (`src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs:62-92`), which applies ONE shared
   `el.Properties.IsOffscreen` preflight guard to every action — including `Interactor.ScrollIntoView`
   — before the action-specific logic ever runs.

## Code-level Mitigation
Do not apply the generic off-screen actionability guard when the action is `Interactor.ScrollIntoView`
(`src/FlaUI.Mcp.Core/Interaction/Interactor.cs:70-74`). Either give `DesktopScrollIntoView`
(`src/FlaUI.Mcp.Server/Tools/InteractionTools.cs:112-118`) its own resolve path that skips the
`IsOffscreen` preflight in `RunOnRefActionAsync`/`RunOnSelectorActionAsync`, or add a parameter to those
methods (e.g. `bool skipOffscreenGuard`) that `DesktopScrollIntoView` passes `true`. `ScrollItemPattern` is
valid — and commonly required — on off-screen elements; let it execute. (Interim driver recoveries exist:
`desktop_get_grid_cell` by index for details-view grids, or focus+keyboard for tabs — but the tool itself
should not block the pattern it exists to expose.)
