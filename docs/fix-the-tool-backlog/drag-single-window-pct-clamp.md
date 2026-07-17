# drag-single-window-pct-clamp — desktop_drag single-window pct space + [0,1] clamp blocks cross-app drag

- **Captured:** 2026-07-17 (via flaui-autotrain)
- **Regression test:** `FlaUI.Mcp.Tests.Interaction.DragCrossWindowClampTests.PctToPhysical_hard_clamps_a_cross_window_endpoint_making_cross_app_drag_inexpressible`
- **Trait:** `Category=KnownDefect` (headless)

## Steps to Reproduce
1. Arrange Explorer (left half) and Notepad (right half) via `Win+Left`/`Win+Right`.
2. `desktop_drag window=<explorer>` with `startXPct`/`startYPct` on a file, `endXPct=1.445`
   `endYPct=0.55` — a point in the right-half Notepad window expressed as a fraction of the SOURCE
   (Explorer) window's bounds.
3. Returns `InvalidArguments`: `"Coordinate fractions must be in [0,1]; got (1.445,0.55)."`
4. Because both fractions are hard-clamped to `[0,1]` (`CoordinateMath.PctToPhysical`,
   `src/FlaUI.Mcp.Core/Interaction/CoordinateMath.cs:12`) and both drag endpoints share ONE `window`
   handle (`src/FlaUI.Mcp.Server/Tools/InputTools.cs:470-489`), a drop point in ANOTHER window is
   inexpressible even when the two windows are perfectly tiled side-by-side. Cross-app drag-and-drop is
   structurally impossible with the current tool.

## Code-level Mitigation
Add an absolute-screen-coordinate drag mode to `DesktopDrag` (`src/FlaUI.Mcp.Server/Tools/InputTools.cs`) —
physical-pixel start/end points bypassing `CoordinateMath.PctToPhysical` entirely — OR add separate
`startWindow`/`endWindow` handle parameters, each resolving its own pct space via
`ResolveWindowPctAsync`, so a drag can cross window boundaries. Keep the existing `[0,1]` clamp for the
single-window pct form (it is correct for same-window drags); the fix is an additional mode, not removing
the clamp.
