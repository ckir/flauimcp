# wait-for-cull-disagrees-with-find — `desktop_wait_for` can never satisfy on an element `desktop_find` returns

- **Captured:** 2026-07-29 (via flaui-autotrain)
- **Regression test:** `FlaUI.Mcp.Tests.Perception.WaitForCullDefectTests.Wait_for_and_find_disagree_on_a_spatially_culled_element`
- **Trait:** `Category=Desktop` (console-only — needs a real window and a live UIA tree)

## Steps to Reproduce

The TestApp fixture already ships a permanent, deliberate instance of the condition:
`SpatialOffscreenButton` sits at `Canvas.Left="5000"` inside a clipped 1px `Canvas`, so it keeps a UIA
peer and reports `IsOffscreen=false`, but its bounding rectangle falls entirely outside the window.

1. Launch `FlaUI.Mcp.TestApp` and open its window.
2. `desktop_find` with `automationId = "SpatialOffscreenButton"` → **returns the element**, with bounds.
3. `desktop_wait_for` with `property = "automationId"`, `value = "SpatialOffscreenButton"`,
   `until = "exists"` → **`satisfied: false`**, and it will stay false for any timeout.

The same disagreement was first observed on a *non*-deliberate element: `DelayedLabel` at
`bounds [176,1000,560,16]` with `isOffscreen: false`, against a window whose UIA rect ended at y=944.
`desktop_find` returned it; `desktop_wait_for` never satisfied. That instance was removed by fixing the
fixture's layout overflow, but the underlying tool behaviour is unchanged — the sentinel above
reproduces it permanently.

## Code-level Mitigation

`SnapshotEngine.cs:45-46` binds `cullBounds` to the window root's `BoundingRectangle`, and the walk at
`:66-70` drops any element whose rect does not `IntersectsWith` it. `desktop_wait_for` and
`desktop_snapshot` both go through that walk; `PerceptionManager.FindAsync` does not. Hence the two
answer "does this element exist?" differently.

Either of these removes the contradiction:

1. **Make the existence predicate consistent.** Have `WaitCoordinator`'s `exists` predicate resolve
   through the same path `FindAsync` uses, rather than through the culled snapshot walk. Existence and
   *renderability* are different questions, and `until: "exists"` names the former.
2. **Make the refusal legible.** If the cull stays authoritative for waits, `desktop_wait_for` must not
   report a bare `satisfied: false` when the element was found but culled. Return the reason —
   element resolved, rect outside the window's bounds, these were the two rects. Today the caller sees
   a timeout and reasonably concludes "not appearing yet", so the natural next move is to raise the
   timeout, which can never work.

**Why this matters beyond tidiness:** the failure presents as a *timing* problem and is a *geometry*
one. A consuming agent that waits on a control `desktop_find` can see will block for the full timeout,
every time, and the diagnostic it is handed points away from the cause. Option 2 is the smaller change
and fixes the misdiagnosis even if option 1 is judged too semantically invasive.

**Not decided here.** Which option to take is a product decision — it changes `wait_for`'s contract —
and it is a `src/` change, deliberately out of scope of the tests-only work that surfaced it.
