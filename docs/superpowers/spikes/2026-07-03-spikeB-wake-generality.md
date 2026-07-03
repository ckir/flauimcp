# Spike β — wake generality & minimal held registration

**Date:** 2026-07-03 · **Gates:** Phase 9 Tasks 3–5 (Prong A, the accessibility wake). **Method:** live dogfood of the installed FlaUI.Mcp server (`desktop_snapshot_stats` for node counts, `desktop_watch`/`desktop_unwatch` for the held registration) against a launched VS Code window (PID 31168, handle `w4`).

## DECISION (feeds Task 4)

- **Minimal held event kind = `StructureChanged` — CONFIRMED sufficient ALONE.** `WakeService.WakeKinds = { WatchEventKind.StructureChanged }` (matches the plan default). No need for `focus_changed` (the prior VS Code spike had used `[structure_changed, focus_changed]`; β proves structure_changed by itself wakes).
- **Use the HELD registration** (a live `IUiaEventSource.Register` handle), not a one-shot activation. A one-shot `WM_GETOBJECT`/read transiently activates AXMode but does not reliably HOLD it once reads stop (see "release" below) — so hold a registration for the interaction duration, exactly as §4/Task 4 designs.
- **Generality: high confidence on mechanism grounds, measured on one fixture.** AXMode is a **Chromium platform behavior** (renderer accessibility activates when any UIA/AT client advises), not a VS-Code-specific quirk — every Electron/CEF/WebView2 host shares it. Empirically measured this run on VS Code (Electron) only. `ms-teams` (new Teams, WebView2/Edge-Chromium) is installed as a second candidate but was not separately measured (avoids a sign-in rabbit-hole). Generality risk is therefore **noted but low**.

## Measurements

| Step | Action | `total` nodes | Notes |
|---|---|---|---|
| 1 | `desktop_snapshot_stats w4` (opaque) | **14** | Window:1, Pane:10, Button:3 — window frame + native Min/Restore/Close + empty Panes. Textbook zero-UIA. |
| 2 | `desktop_watch w4 ["structure_changed"]` then stats | **231** | Fully hydrated: TreeItem:32 (file tree), MenuBar:1, MenuItem:8, ToolBar:11, Tab/TabItem, Document:1, Edit:2, StatusBar:1, Button:40, Group:89, Text:15. **StructureChanged-only registration IS the wake trigger.** |
| 3 | `desktop_unwatch s4` then stats | **237** | **Did NOT collapse.** Still fully hydrated after release. |
| 4 | stats again (seconds later, no other UIA activity between) | **237** | Still hydrated. |

## Finding on release / re-collapse (refines the prior spike + spec §4)

The prior VS Code wake spike reported the tree "COLLAPSED back to 15 empty nodes" immediately after `desktop_unwatch`. **This run did NOT reproduce an immediate collapse** — the tree stayed at 237 nodes after release and remained hydrated across subsequent reads. Interpretation: **Chromium keeps AXMode warm after the last AT disconnects, and an active UIA client read (each `snapshot_stats` walk is one) re-advises / keeps AXMode active.** So the tree re-collapses only **lazily, on genuine idle with no UIA client touching it** — not instantly on release. (The prior spike likely measured after such an idle gap.)

**Design impact: none on the chosen approach.** Holding a live registration guarantees hydration for its duration — validated. The lazy re-collapse means:
- The `wakeable` hint (Task 6) keys off *class + collapsed node count*; a window that was recently woken may briefly still read as hydrated (not wakeable) even after release — acceptable (the agent just snapshots it directly).
- `desktop_release_accessibility` frees the held handle (and the wake accounting) so it stops *keeping* the tree awake; it does not promise instant collapse, and the tool description should not claim one. (Task 5's `desktop_release_accessibility` description says the tree "re-collapses to opaque after release" — soften to "the wake is no longer held; Chromium re-collapses the tree lazily once idle." — a doc nuance for Task 5/12, not a code change.)
- Task 5's Desktop regression test should assert **hydration WHILE HELD** (14 → 200+) as the load-bearing assertion; a post-release collapse assertion (if kept) must allow an idle settle or be treated as best-effort, since collapse is lazy.

## Bottom line for Task 4

`WakeService` registers `[StructureChanged]` via the existing `IUiaEventSource.Register` seam with a null sink and HOLDS the `IDisposable`. That alone keeps an opaque Chromium/Electron tree hydrated (14 → 231 measured). Auto-release on `WindowManager.WindowInvalidated` frees the handle on window close (§9). No lighter one-shot activation is relied upon.
