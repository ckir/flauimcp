# A1a — make the Category=Desktop suite hermetically green

**Date:** 2026-07-28
**Status:** approved (design); implementation plan not yet written
**Gates:** ROADMAP A1a, which after the deletion of A1b and A2 is the whole v1.0 gate.

## Problem

A full leased-console run measured **Failed 6 / Passed 102 / Total 108**. The ROADMAP claimed A1a
needed only validation; that claim was stale. This is repair work.

All six were re-run in isolation and still failed, so none is cross-test interference. Each cause
below was established by MEASUREMENT — driving the shipped v0.19.1 server against the TestApp — not
by reading code alone. Measurements are ground truth for this spec.

| # | Test | Measured cause |
|---|------|----------------|
| 1 | `Watch.DesktopWakeTests.Waking_hydrates_the_tree_while_held` | `expected the tree to hydrate while held (>100 nodes), got 16`. Cold VS Code → PASS (15 s); immediate re-run → FAIL (8 s). VS Code is single-instance **per `--user-data-dir`**, so a second launch attaches to the existing process and `LaunchAppAsync` returns early; the fixed `await Task.Delay(1500)` then measures before AXMode hydration. Not hermetic: fails whenever any VS Code is already open. |
| 2 | `Presence.PresenceDesktopTests.Real_idle_source_reports_active_right_after_input` | Expected `Active`, got `Nearby`. `PresenceDesktopTests.cs:12-15` reads the real OS-wide `GetLastInputInfo` clock and asserts a **human** typed < 60 s ago. It synthesises no input of its own. |
| 3 | `Server.WaitForStableTests.Structure_is_stable_despite_a_live_ticker` | Arithmetic, not flakiness. One full walk of the 94-node window costs **P ≈ 3.0 s** (quiet=1 ⇒ elapsed 9585 ≈ 3P+250; quiet=500 ⇒ 12531 ≈ 4P+500). `needed = ceil(500/250) = 2` ⇒ 3 identical polls **plus** a final confirming `SnapshotModelForWaitAsync` walk ⇒ a ~12.5 s floor against the test's **5000 ms** timeout. Unreachable even with a perfectly static tree; goes `stable:true` at 12531 ms when given 25000 ms. |
| 4 | `Perception.FindTests.Find_truncates_and_reports_totalMatches` | `Assert.Single` on an EMPTY collection. |
| 5 | `Perception.FindTests.Find_by_name_contains_matches_substring` | `Matches.Count >= 2` was false. Both: the ListBoxItems' UIA **Name is their Content** — `"A" "B" "C" "NamedOnly"` — so `name contains "Item"` + controlType `ListItem` matches **nothing**. Confirmed live: `find(controlType=ListItem)` → 4 matches, all `isOffscreen:false`, un-culled. The tests confuse AutomationId with Name and have been wrong since authored; `Category=Desktop` is deliberately not a CI job, so nothing caught it. |
| 6 | `Server.WaitForTests.Delayed_control_becomes_satisfied_with_a_ref` | `satisfied` was false. The appended `DelayedLabel` genuinely exists at `bounds [176,1000,560,16]`, `isOffscreen:FALSE`, while the window's UIA rect is `@{156,156,600,788}` — cull edge y=944. `SnapshotEngine.cs:66-69` culls any element not intersecting the window rect; waits apply it, `desktop_find` does not. Measured directly: `desktop_wait_for(...DelayedLabel, exists)` → `{"satisfied":false}` while `desktop_find(automationId=DelayedLabel)` **finds it**. |

### The defect behind #6 is the fixture, not just the test

`MainWindow.xaml` is a single vertical `StackPanel` whose content sums to ~890 px. `Height="880"` was
clamped by this machine's work area to a **788 px** window. Consequences already visible in a
snapshot: the tree ends at `e90`, the **second `DupRow` GroupBox is entirely culled**, and
`Row1Btn`'s Text survives on a 2 px sliver. So the ambiguity fixtures are partly invisible to every
snapshot test, and the overflow is **screen-size dependent** — the exact class of non-hermeticity
this work has to remove.

### Two side-findings (not defects to fix here)

- `IncludeText_on_a_live_ticker_times_out_unstable` **passes for the wrong reason**: it asserts a
  timeout, so it would pass even if the tree never changed. False assurance. Repaired under #3.
- A 94-node WPF window costs ~3.0 s per full walk (~1200 uncached cross-process UIA property reads).
  Every default 5 s wait budget in the product therefore affords only 1–2 polls. **Filed, deferred.**

## Constraints (user decisions — binding)

1. **Green first, then decide each.** Tests and test-fixture changes only; no `src/` changes. Product
   findings are filed and decided separately, later.
2. **Hermetic.** Green on any dev machine — including one with VS Code already open and a short work
   area. Not "green here with documented preconditions".
3. **Longer suite runtime accepted.** Generous absolute budgets are fine; the suite is a deliberate
   local gate, not a CI job.
4. **Do not bake current semantics into assertions.** Fixes must leave the deferred `wait_for`/`find`
   decision cheap to make later — so nothing may assert *about* culling in either direction.

## Approach: A2 — fix the fixture's root defect once, then repair each test

Restructure `MainWindow.xaml` from one ~890 DIP column into a **multi-column `Grid`** whose tallest
column fits inside the minimum supported client height, and clamp the window's height to the work
area so OS clamping cannot silently shrink it. This removes #6, restores the hidden `DupRow`
coverage, and makes every Desktop test screen-size independent. Then repair #1–#5 individually.

(Round 3: this was written as a *two*-column split at ~450 DIP per column. Corrected arithmetic in
D1 puts the minimum supported client height at **432 DIP**, so two columns does not fit and the
column count is a measured outcome — at least three — not a fixed part of the approach.)

### Alternatives considered and rejected

- **A1, per-test repair only.** Its #6 fix (`RootPanel.Children.Insert(0, ...)`) dodges the overflow
  rather than removing it; the fixture would still overflow on a short screen, violating constraint 2.
- **A3, hermeticity harness** (fixture base enforcing preconditions, `WaitBudget` helper). Most work
  up front and its payoff is the unattended runner — a ROADMAP item the user deleted.
- **agy's constraint-inversion for #6** (`Task.Delay` + `desktop_find` instead of `wait_for`).
  Rejected: it deletes the only coverage of `wait_for(exists)` on a dynamically-added control.
- **agy's fix for #4/#5** (retarget the query to `"A"`). Rejected as measurably wrong: ordinal
  `contains "A"` matches only the item named `A` (`NamedOnly` has no capital A) — 1 match, failing
  both `Count >= 2` and `TotalMatches >= 2`.
- **Retargeting #4/#5 to Buttons** (`contains "Items"` + `Button`, proven live by a passing sibling).
  Workable, but agy's objection stands: it drops name-matching coverage of ListItems specifically.

## Design

### D1 — Fixture restructure (`test/FlaUI.Mcp.TestApp/MainWindow.xaml`)

Multi-column `Grid` (at least three — see the budget below); every existing control keeps its
`x:Name`, `AutomationId`, `Content`, and its containing `GroupBox`/`Canvas`/`Border` parent. Only
column placement changes.

**Window sizing (revised — panel rounds 1 and 2).** A reduced *literal* `Height` is not hermetic: a
work area's size **in DIPs shrinks as display scaling rises**, so any fixed DIP height eventually
exceeds it. The window therefore **clamps its height** to `SystemParameters.WorkArea` at runtime
rather than declaring one.

Round 2 corrections to that fold:

- **Clamp both dimensions with `Min`, never *bind* either (revised round 5).** *Binding* width to the
  work area is unsafe: `SpatialOffscreenButton` sits at `Canvas.Left="5000"` and
  `OffscreenCullTests.cs:45` asserts it is spatially culled — an assertion that holds only while the
  window is narrower than the sentinel, and that inverts above ~5080 DIP on a very wide primary
  monitor. But leaving width a pure constant is equally unsafe: round 5 showed a ~950 DIP constant
  overflows the declared floor machine horizontally. The correct form is
  `Min(designConstant, WorkArea.<dimension>)` in **both** axes — it can only ever shrink the window,
  so it never approaches the sentinel, and it never exceeds the screen. The design constants stay
  far below 5000 DIP.
  **Stated assumption (round 6): `SystemParameters.WorkArea` reports the *primary* monitor only**
  (it wraps `SPI_GETWORKAREA`), so on a multi-monitor setup it can describe a different display from
  the one hosting the window. This is tolerable **only because the design constants (~894 × ~460
  DIP) already fit the declared floor machine unaided** — the clamp is a belt-and-braces guard, not
  the mechanism the design depends on, and `Min` can only shrink. If the constants ever grow to
  where the clamp becomes load-bearing, this must switch to querying the hosting monitor
  (`Screen.FromHandle`-equivalent) instead.
- **Clamping does not by itself make the content fit — a minimum supported client height must be
  declared.** "No literal pixel constant is load-bearing" was wrong: the content's own height is
  that constant, merely implicit. The clamp only stops the OS from silently shrinking the window;
  if the content is taller than the clamped client area it still overflows and still gets culled.
  The originally proposed two-column split is **insufficient** on realistic hardware:

  The Windows taskbar is 48 physical px at 100% and **scales with DPI, so it is 48 DIP at every
  scale** — an earlier draft of this table divided it by the scale factor on one row only, which
  understated the 150% case by 16 DIP and produced the wrong conclusion. Corrected (round 3):

  | Machine | Screen DIP height | − 48 taskbar ⇒ WorkArea | − ~32 title/border | Client DIP | vs ~450 DIP of content |
  |---|---|---|---|---|---|
  | 1920×1080 @200% | 540 | 492 | ~460 | **460** | +10 DIP — a 2% margin |
  | 1366×768 @150% | 512 | 464 | ~432 | **432** | **−18 DIP — does not fit** |

  So the plan must (a) design against a **minimum supported client height of 432 DIP** — the
  1366×768 @150% budget — and (b) size the content to clear it with real margin. At that floor **two
  columns is not sufficient**: three columns, or genuine content compression, is required rather
  than optional. D7 makes a violation fail loudly at runtime instead of silently culling. The exact
  split is measured in the plan; the *budget* is fixed here.
- **A `ScrollViewer` is not an option.** It would leave scrolled-out controls reporting
  `IsOffscreen=false` with collapsed or clipped bounding rects — destroying the deterministic
  separation `OffscreenCullTests.cs:12-50` pins between the `IsOffscreenBehavior` branch and the
  spatial-bounds branch.

**Default column assignment (round 4 — decided here, verified in the plan).** Deferring this was a
fake measurement: the controls' heights do not change when they move columns, and the total is
already known. Heights below are **estimates read off the XAML plus default WPF metrics**, not live
measurements — the plan confirms them and rebalances only if a column exceeds the 432 DIP budget.

These estimates depend on the machine's default font and theme metrics, which vary; that exposure is
not eliminated by arithmetic, which is precisely why D7 asserts containment **at runtime** rather
than trusting the table. A column that overflows on some machine fails loudly there instead of
silently culling.

| Column | Controls | Est. DIP |
|---|---|---|
| 1 — **`RootPanel`** (the append target) | `Input`, `Secret`, `TextDoc` (60), `Grid` (100), `OkButton`, `Status`, `RebuildItemsButton`, `ClearItemsButton` | ~340 |
| 2 | `ItemList` (~180 after D5), `OffscreenButton`, the clipped `Canvas` + `SpatialOffscreenButton`, `Check`, `Exp`, `FocusReveal`, `RevealedLabel` | ~315 |
| 3 | `ModalButton`, `FreezeButton`, `DelayRevealButton`, `Ticker`, the `Border`/`MenuTarget` (36), `DupHost`, both `DupRow` GroupBoxes | ~320 |

Column 1 is `RootPanel` deliberately: it is the shallowest column, leaving ~90 DIP of slack below its
last child for `DelayRevealButton_Click`'s runtime append to land inside the window.

**The budget is two-dimensional (round 5).** Fixing the vertical overflow at ~300 DIP per column
implied a window near ~950 DIP wide — which **overflows the declared floor machine horizontally**:
1366 physical px at 150% is only **910 DIP** of screen, ~894 DIP of client. That would push the
rightmost column off-screen and cull it, re-creating the exact defect this section exists to remove,
on the exact hardware the budget names. The full floor-machine budget is therefore:

> **~894 DIP wide × 432 DIP high of client area** (1366×768 @150%).

Three columns must total ≤ ~890 DIP including margins — roughly **290 DIP per column**, against the
~576 DIP the single-column layout had. Two consequences the plan must respect: no control may
require more than ~290 DIP of width, and **narrowing a column can make it taller** (wrapping
content), so the split is verified in *both* dimensions, never height alone.

Invariants that must survive the move, each already load-bearing for a passing test:
- `OffscreenButton` keeps `AutomationProperties.IsOffscreenBehavior="Offscreen"`.
- `SpatialOffscreenButton` stays inside the clipped `Canvas Height="1"` at `Canvas.Left="5000"`, so
  it remains IsOffscreen=false but spatially outside — the bounding-box backstop fixture.
  **Derived invariant (round 2): the window's width must stay far below 5000 DIP.** The sentinel
  offset and the window width are coupled; `OffscreenCullTests.cs:45` breaks if the window ever
  grows wide enough to contain the sentinel.
- **`RootPanel` remains a vertical `StackPanel`.** The multi-column `Grid` *contains* the columns; it
  is not itself `RootPanel`. `MainWindow.xaml.cs:81` calls `RootPanel.Children.Add(tb)` to append
  `DelayedLabel`. If `RootPanel` became the `Grid`, that call would silently place the label in cell
  (0,0) on top of `Input` — it compiles, it runs, and it quietly changes the layout contract the
  code-behind depends on. The column named `RootPanel` must also keep enough reserved vertical slack
  below its last child for that runtime append to land inside the window.
- `DupHost` / the two `DupRow` GroupBoxes stay `GroupBox` (a bare panel has no UIA peer and could
  not be resolved as an ancestor scope).
- The `MenuTarget` `ContextMenu` with `MenuAlpha` / `MenuBeta` is unchanged.
- `DelayRevealButton_Click` still appends to a panel with vertical room inside the window.

### D2 — #1 `DesktopWakeTests`

- **Resolve the VS Code path; stop hard-coding it.** `DesktopWakeTests.cs:23` pins
  `C:\Program Files\Microsoft VS Code\Code.exe`. A user-local install
  (`%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe`) or no install at all fails immediately —
  which by itself defeats constraint 2. Probe the machine-wide path, then the user-local path, then
  `PATH`; if none resolves, fail with an actionable message naming what to install. (Panel round 1.)
- `LaunchAsync` passes `--user-data-dir <fresh temp dir> --new-window` to `LaunchAppAsync`, forcing a
  genuinely separate VS Code instance regardless of ambient VS Code.
- **Suppress the first-run experience.** A pristine `--user-data-dir` is not a clean tree — it is the
  first-run tree: workspace-trust prompts, welcome/release-notes tabs, and extension toasts steal
  focus and change the node count, which is exactly what the `> 100` assertion measures. Pass flags
  that disable workspace trust, welcome/release notes, telemetry, and extensions. This trades the
  single-instance race for a first-run race unless done. (Panel round 1; raised independently by
  both panels.)
- Replace `await Task.Delay(1500)` with poll-until-hydrated: poll `StatsByWindowAsync` until
  `Total > WokenNodeFloor` or a ~20 s deadline elapses; assert after the loop so a real failure still
  reports the measured node count. The deadline must accommodate a first-run launch, which is slower
  than the 8–15 s measured on a warm profile.
  **Why a bespoke loop rather than `WaitCoordinator` (round 5, acknowledging the divergence).** The
  repo centralises UIA waits in `WaitCoordinator.cs`, and diverging from that silently would be a
  defect. The divergence is deliberate: `WaitCoordinator` expresses `exists`/`gone`/`enabled` on a
  selector and tree-stability, and has **no node-count predicate**; adding one is a `src/` change,
  which constraint 1 forbids. It also polls through its own culled `PollOptions` walk, whereas the
  assertion under repair is stated in terms of `StatsByWindowAsync` — polling on a different call
  than the one asserted would measure a different thing. Bespoke loop, stated reason.
- **Teardown ordering.** Kill the VS Code PID tree *first*, then delete the temp profile, then retry
  deletion once — Electron GPU/renderer processes hold file locks, and a swallowed
  `UnauthorizedAccessException` silently leaks hundreds of MB per failed run. Place all profiles
  under one parent directory that the suite sweeps on start, so a leak is self-healing rather than
  cumulative. (Panel round 1.)
- **`Kill()` does not wait — `WaitForExit` before touching the disk (round 4).**
  `Process.Kill(entireProcessTree: true)` signals termination and returns immediately while the OS
  unwinds handles asynchronously, so deleting the profile straight afterwards races the very
  file-lock release it is waiting on, and a single immediate retry loses the same race. The repo
  already knows this: `TestAppFixture.cs:40-41` follows its `Kill` with `WaitForExit(3000)` and
  documents why. `DesktopWakeTests.cs:46` — the code D2 modifies — does **not**.
  **But `WaitForExit` on the root is not enough for Electron (round 6).** `Process.WaitForExit()`
  blocks on the single handle it is called on, so it returns as soon as the root `Code.exe` dies
  while the GPU/network/renderer children — the processes actually holding the SQLite locks inside
  `--user-data-dir` — are still unwinding. The `TestAppFixture` precedent is sound for a
  single-process WPF app and insufficient here. Wait on the enumerated child PIDs as well, **and**
  make the deletion itself a bounded retry loop with backoff rather than one immediate retry: the
  observable to poll is "the directory deleted", not "a handle we believe we enumerated correctly".
  **Enumerate the children BEFORE killing (round 7).** Querying the process tree *after*
  `Kill(entireProcessTree: true)` returns nothing useful — the parent is already gone and the
  children are terminating or reparented, so the teardown would wait on an empty set, delete
  immediately, hit the dying children's locks and leak anyway. Snapshot the child PIDs first, then
  kill, then wait on the recorded set. The backoff-delete loop is the backstop for whatever the
  enumeration still misses.
- **The sweep must not delete a live profile (round 2, both panels).** A blind sweep-on-start races
  a concurrent or hung run: assembly-level parallelization is disabled, but *process*-level
  concurrency is not, and yanking the SQLite DBs out from under a running VS Code crashes its
  renderer, fails the other run, and leaks the PID tree the failed teardown never reaped. Name each
  profile directory after its owning PID and sweep only entries whose owner process no longer
  exists. **A naked PID is not a liveness test (round 3):** Windows recycles PIDs aggressively, so
  once VS Code exits, an unrelated process can inherit that number and
  `Process.GetProcessById(pid)` will succeed — the sweep would then skip the garbage profile
  forever, reinstating the leak it exists to prevent. Record the owner's `Process.StartTime`
  alongside the PID and require **both** to match before treating a profile as live; anything else
  is sweepable. **Add an age backstop (round 4):** a profile older than a few hours is swept
  regardless of owner liveness, so no single mechanism — a recycled PID, or an orphaned `Code.exe`
  that is genuinely still running — can shield garbage on disk indefinitely.
- **Three tests share `LaunchAsync`.** `Waking_hydrates_the_tree_while_held`,
  `Closing_the_window_auto_releases_its_wake`, and `Release_removes_the_wake_from_the_registry` all
  call it, so every D2 change applies three times per run and six times across the two consecutive
  runs of verification step 2. Runtime is accepted by constraint 3; the profile-leak surface is the
  reason the PID-named sweep above is not optional.
- The opaque-baseline assert (`< 30` nodes) is unchanged; instance isolation is what makes it sound.
- **Instrument the pid/window pairing — #1's root cause is not fully closed (round 2).** The spec's
  stated cause is "single-instance ⇒ `LaunchAppAsync` returns early ⇒ the fixed `Delay(1500)`
  measures too soon". The measurement does not fully support that: the warm re-run failed in 8 s
  with `got 16` **after the `< 30` opaque-baseline assert had already passed**, which proves a window
  *was* resolved and *was* opaque. So a handle was obtained; what failed was hydration, not
  resolution. `WindowManager.cs:400,424-432` shows `LaunchAppAsync` ignores pre-existing PIDs and
  only accepts the launched PID or a **new** same-named PID, so it cannot latch onto an ambient
  window — but it can still return a `(handle, pid)` pair whose `pid` is a transient launcher while
  the window belongs elsewhere, and `WakeAsync(handle.Id, pid)` would then wake a process that does
  not own the window under measurement. That reproduces `got 16` exactly.

  Consequence: replacing `Delay(1500)` with a 20 s poll would turn a fast red into a slow red
  without fixing anything; the `--user-data-dir --new-window` isolation is the part that actually
  matters. The plan must therefore log the launched PID, the resolved window's **owning** PID, and
  the node count at each poll. Treat #1's root cause as reopened until that instrumentation runs.

  **Stage the PID assert behind the measurement (round 3).** The guard is *expected* to hold under
  D2's own design: the test launches `C:\...\Microsoft VS Code\Code.exe` directly — the Electron
  main binary, not the `bin\code.cmd` CLI shim — and a fresh `--user-data-dir` scopes single-instance
  detection to a profile no other process owns, so the launched process becomes the main process and
  owns the window. It would *not* hold if that isolation ever failed (flag typo, profile collision,
  a `--user-data-dir` the build rejects), which is exactly the silent failure the guard exists to
  expose. Sequence it accordingly: log both PIDs first, confirm they match on a real run, and only
  then promote it to a hard assert. Adding the assert before measuring would risk codifying a
  permanent red.

### D3 — #2 `PresenceDesktopTests`

Primary: synthesise input from the test itself (it runs under the lease the suite already requires),
then assert `IdleActivity.Bucket(...) == Activity.Active`.

**The mechanism is not free choice (panel round 1).** `GetLastInputInfo` reports the last *low-level
hardware* input event. A UIA `InvokePattern` call or a WPF `RaiseEvent` does not touch that clock, so
either would produce a test that still fails. The synthesis must be Win32 `SendInput` — the same path
the `PopupGrafting` tests already use. Naming this is load-bearing: the earlier draft left it to the
implementer to guess, and two of the three plausible guesses are silently wrong.

**The synthesised input must be null-effect (round 2, Boundary Smuggler).** `SendInput` delivers to
whatever window holds focus — during a Desktop run that may be the TestApp, the VS Code instance D2
just launched, or the developer's own editor. A stray keystroke can corrupt another test's window
state or type into the user's work. Synthesise the **smallest event that moves the
`GetLastInputInfo` clock and nothing else**: a zero-delta `MOUSEEVENTF_MOVE`, verified by
measurement to move the idle clock. Do not synthesise a character keystroke.

**Adopt the repo's lease convention.** Lease-dependent Desktop tests use `[SkippableFact]` with
`Skip.If(InputLocked(), ...)` (`InputToolsTests.cs:64-67`). D3 makes `PresenceDesktopTests`
lease-dependent for the first time, so it must use the same guard — otherwise the test merely
trades "fails unless a human just typed" for "fails unless a human granted a lease", which is a
different non-hermeticity, not a repair. This makes D3 lease-dependent **by design**; that is
consistent with constraint 2 only because the whole suite already requires a lease (see Standing
constraints), and it is recorded here rather than left implicit.

Fallback if synthesis proves unreliable:
split into a lease-free pure-logic test of `IdleActivity.Bucket` over injected values plus a
real-source sanity assert (`idle >= 0`). **The fallback is strictly weaker** — it stops testing the
integration — so it is a fallback, not a preference.

### D4 — #3 `WaitForStableTests`

- `Structure_is_stable_despite_a_live_ticker`: timeout 5000 → 25000; `quietMs`/`pollIntervalMs`
  unchanged at 500/250. Justification recorded in-test: floor is ~4 × walk cost.
- `IncludeText_on_a_live_ticker_times_out_unstable`: additionally assert that the same window with
  `includeText:false` **does** go stable, so the test can only pass when the ticker is genuinely the
  cause of instability. Both under the raised budget.

### D5 — #4/#5 `FindTests`

- Add two `ListBoxItem`s with Content `ItemOne` / `ItemTwo` (AutomationIds `ItemD` / `ItemE`) and
  raise the ListBox `Height` so all six items are laid out. (Round 2: the earlier justification —
  "the existing 4 items occupy y=473..553 in a box ending at y=591, ~38 px of slack" — measured the
  *single-column* layout that D1 deletes, so it is stale by construction. The conclusion stands; the
  height is derived by measurement against the post-D1 layout in the plan.)
- Both tests keep their original `FindQuery(null, "Item", "contains", "ListItem", false)` and their
  ListItem coverage; only the fixture gains matching elements.
- **Update `RebuildItemsButton_Click` to recreate all six items (round 2).** `MainWindow.xaml.cs:36`
  hard-codes `("ItemA","A"), ("ItemB","B"), ("ItemC","C")`, so after D5 a Rebuild click would
  permanently drop `ItemOne`/`ItemTwo` and any later `contains "Item"` query in the same app
  instance would return 0. Measurement says this cannot bite *today* — every `FindTests` `[Fact]`
  constructs its own `TestAppFixture` (`FindTests.cs:27,48,63,79,…`), so the rebuilding test cannot
  contaminate the querying ones. But that is a **fixture-lifetime** argument, and the risk table
  originally justified it by *which query the rebuilding test runs*, which would be unsafe for the
  classes that do share one app (`WaitForTests`, `AmbiguousResolutionTests` both use
  `IClassFixture<TestAppFixture>`). Keeping the handler consistent with the declared contents
  removes the trap instead of relying on a lifetime detail holding forever.

### D6 — #6 `WaitForTests`

Fixed by D1: once the fixture fits, `DelayedLabel` is inside the window rect and the cull no longer
hides it. Nothing is asserted about culling in either direction, satisfying constraint 4.

**But the budget must be raised too (round 2).** `WaitForTests.cs:38` waits with `timeoutMs=5000`,
`pollIntervalMs=500`, against a measured walk cost of **P ≈ 3.0 s**. `WaitCoordinator.cs:92-99`
checks the deadline only *after* an unsatisfied poll, so the wait cannot return false before one
full walk completes — which is the only reason a 5000 ms budget survives at all today. The label
appears 600 ms after the invoke, i.e. after poll #1 has already enumerated `RootPanel`'s children,
so satisfaction lands on poll #2 at ~6.5 s. That succeeds only while **one walk stays under 5 s**;
D1 restores the previously culled second `DupRow` and D5 adds two `ListItem`s, so the node count —
and therefore P — goes **up**, and any slower dev machine crosses the line. A budget that holds only
while a UIA walk stays under 5 s is a machine-speed assumption, which is exactly what constraint 2
forbids. Raise this timeout into the same generous class as D4; constraint 3 permits it. Cost: one
number.

### D7 — Fixture-integrity regression guard (new test)

**Revised after panel round 1 — the single-control probe was compliance theatre.** Asserting that
`Row2Btn` appears in a default snapshot fails in two ways: after D1 moves the layout to multiple
columns `Row2Btn` is no longer the bottom-most control, and a developer could overflow any column
the guard does not name while it stayed green. It also asserted *about culling*, in tension with constraint 4.

Instead, assert containment geometrically: the root `Grid`'s `BoundingRectangle` is fully inside
**the window's UIA `BoundingRectangle`** — the same rectangle `SnapshotEngine.cs:65-70` binds
`cullBounds` to. That is a property of the fixture's layout, not of the cull, so it stays valid
whichever way the deferred `wait_for`/`find` decision goes, and it cannot be gamed by adding
controls to whichever column the guard does not happen to name.

**Round 5 — assert on the column *children*, not the root `Grid`; the container form is a WPF
tautology.** A `Grid` hosted as a clamped `Window`'s content is arranged to the client area whatever
its content does: its `ActualHeight`/`ActualWidth` — and therefore its UIA `BoundingRectangle` — equal
the client area by construction, so "the Grid is inside the window" is true even when fifty children
are overflowing and being culled. As written, the guard could never fail, which would have made the
entire 894 × 432 DIP budget unenforced at runtime.

The guard must therefore assert containment on the elements that actually overflow: **for every
descendant of every column `StackPanel` — excluding the exempt subtree below — the bottom and right
edges must lie inside the window's UIA `BoundingRectangle`.**

**The sentinel is explicitly exempt (round 6).** `SpatialOffscreenButton` sits at
`Canvas.Left="5000"` *by design* — it is the fixture that proves the spatial cull works, and
`OffscreenCullTests.cs:45` depends on it being outside the window. A containment guard that walked
descendants naively would find it at x≈5080, compare it against a ~894 DIP client width, and fail
**permanently**, forbidding the exact overflow column 2 exists to contain. The clipped sentinel
`Canvas` and everything beneath it is therefore **named as exempt**.

**Descendants, not direct children (round 7 — correcting round 6).** Round 6 also restricted the
guard to *direct* children, on the grounds that this incidentally excluded the sentinel. That was a
second, unnecessary belt and it **reinstated the tautology one level down**: `DupHost` and the two
`DupRow` `GroupBox`es are direct children of column 3, and a `GroupBox` in a stretching vertical
`StackPanel` is arranged to the column's width whatever its content does. Widen the buttons inside
its horizontal `StackPanel` and they overflow and get culled while the `GroupBox`'s own rect stays
neatly inside — green guard, hemorrhaging fixture. The sentinel exemption was the real fix all
along; the direct-children restriction only broke the guard, so it is reverted. A `StackPanel` arranges children at their desired size
and lets them overflow the clip, and UIA reports those overflowing rects faithfully — proven by the
original `DelayedLabel` measurement at `bounds [176,1000,…]` against a window ending at y=944. So
per-child edges are meaningful where the container's are not. Covering every column closes the
"overflow the column the guard does not name" gap that killed the round-1 version.

**Round 2 — say "UIA `BoundingRectangle`", never "client bounds".** The earlier wording invited
subtracting `SystemParameters.WindowNonClientFrameThickness` to build a true client box. That is the
wrong oracle in both directions: the window's UIA rect includes the non-client frame and the Win11
drop-shadow aura, so a tighter client box yields **false RED** — D7 failing on a Grid that overflows
into the border while the cull's own `IntersectsWith` still includes it — and it would reimport the
DIP→physical conversion round 1 removed. The guard must use the cull's own rectangle so the guard
and the mechanism it protects can never disagree.

## Implementation order (round 7)

The sections are not independent, and the wrong order produces a window in which the suite is
permanently red and therefore un-bisectable. Land them in this order:

1. **D4 and D6 — the budget raises, first.** Pure widening: they cannot break a passing test, and
   they immunise the suite against the walk-cost inflation D1 and D5 are about to cause. Landing
   D1/D5 first would push `P` up while D6 still carries its one-walk 5000 ms margin, turning #6 red
   for a reason unrelated to the change under test.
2. **D3 — `PresenceDesktopTests`.** Independent of the fixture; land it while the fixture is stable.
3. **D2 — `DesktopWakeTests`,** instrumentation before repair: log the launched PID, the resolved
   window's owning PID and the per-poll node count, run it, *then* apply the repair and promote the
   PID guard to an assert. Independent of the fixture.
4. **D1 — the fixture restructure.** The disruptive step, landed alone so the full-suite re-run
   attributes any fallout to it and nothing else.
5. **D5 — the added `ListItem`s.** Its ListBox height is derived from the post-D1 layout, so it
   cannot be sized before D1 exists.
6. **D7 — the containment guard.** Cannot be written until D1's columns exist; landing it last means
   it locks in a layout already proven green rather than blocking the work that creates it.
7. **Full verification**, including the `PopupGrafting` half.

**Verification step 1 is not fully executable until D2 lands.** Its `Code.exe` kill is scoped by
`--user-data-dir`, and that flag is only injected by D2 — pre-D2 orphans carry no such marker and
would survive a scoped sweep. Until step 3 completes, sweep VS Code orphans manually; the scoped
form becomes the standing rule from D2 onward, when there is finally an ambient instance worth
protecting.

## Verification

The gate is the **full 108-test suite**, not the 6 — D1 touches the fixture every Desktop test shares.

1. Kill orphaned `testhost.exe` / `FlaUI.Mcp.TestApp.exe` before and after every pass, plus **any
   `Code.exe` whose command line points at the suite's own profile parent directory**. (Round 4:
   omitting VS Code entirely created a permanent cascade — an aborted run orphans the isolated
   instance, and because that process is genuinely alive the PID+`StartTime` sweep in D2 correctly
   judges its profile "live" and skips it **forever**; the leaked process shields the leaked
   profile. Round 5: but killing `Code.exe` *by bare process name* is mutually destructive with step
   2, which requires an ambient VS Code deliberately left open — step 1 would assassinate the very
   precondition step 2 exists to test, and the suite would then "prove" hermeticity in a pristine
   environment it had silently created for itself. Scope the kill by `--user-data-dir`, never by
   name.)
2. `dotnet test -c Release --filter "Category=Desktop&FullyQualifiedName!~PopupGrafting"` green
   **twice consecutively, with VS Code deliberately open** — this is the hermeticity proof for #1.
3. Post-D5 measurement: `find(controlType=ListItem)` returns **6** matches, all `isOffscreen:false`
   — confirms WPF has not virtualized the added items out of the UIA tree.
4. Post-D1 measurement: a default snapshot contains `Row2Btn` (also permanently pinned by D7).
5. `dotnet test --filter "FullyQualifiedName~PopupGrafting"` — the synthetic-input half, physical
   console only; never yet run. **This must PASS for A1a to be green** (panel round 1: the earlier
   draft listed it as a step without requiring its result, so A1a could have been declared green
   while PopupGrafting failed). If it surfaces failures unrelated to this spec, they are reported to
   the user as a scope decision — not silently excluded from the gate.
6. **Capture full failure output**, not grep-filtered output. The previous session lost failure #1's
   assert message to a narrow grep filter and had to re-run the whole suite to recover it.
7. **A skip is not a pass (round 3).** Several Desktop tests are `[SkippableFact]` guarded by
   `Skip.If(InputLocked())`, and D3 adds another. xUnit exits 0 on a skipped test, so a lease-less
   run would report a clean exit while silently bypassing every assertion that matters — a green
   gate achieved by redefining a hostile environment as an excused absence. The gate therefore
   requires the run to be made **under an active lease** and to report **0 skipped** among
   `Category=Desktop`. If any test legitimately cannot run, it is enumerated and reported to the
   user as an exception, never absorbed into "green".

## Risks

| Risk | Mitigation |
|---|---|
| D1 changes UIA depth-first traversal order, breaking anything pinning element order or ref numbers (raised by agy) | Grep found no positional/index pinning in `test/`. Real gate is the full 108-test re-run, not the grep. |
| WPF virtualizes the two added ListItems out of the UIA tree (raised by agy) | Verification step 3 measures it directly. If virtualized, fall back to retargeting the query at `contains "Items"` + `Button`, accepting the loss of ListItem name coverage. |
| `RebuildItemsButton_Click` recreates only 3 items (`ItemA/B/C`), so after a rebuild the `contains "Item"` query would find 0 | **Justification corrected in round 2.** The safe reason is fixture *lifetime*, not query targets: every `FindTests` `[Fact]` builds its own `TestAppFixture`, so no rebuild can contaminate another test's app. D5 additionally updates the handler to recreate all six. |
| **D1 changes the window's geometry, and several passing Desktop tests are geometry-sensitive** (round 2) | `OffscreenCullTests.cs:45` is coupled to the `Canvas.Left="5000"` sentinel (addressed by the width invariant in D1); `InputToolsTests.cs:76` clicks the window at (0.5,0.5); `DesktopFindTextTests` OCRs the whole window and takes the *top* match for "Rebuild Items", so enlarging the window changes the OCR input set. All three read live rects rather than hard-coded coordinates, so none is expected to break — but the round-1 risk table considered only traversal order and ref numbers, never geometry. The full 108-test re-run is the gate. |
| D1 and D5 inflate the node count, raising the per-walk cost P for **every** wait in the suite (round 2) | **Reworded in round 4 — this contradicted D4.** The budgets ARE decided: 25000 ms in D4, the same class in D6. Post-D1 the plan **re-measures P and confirms 25000 still clears ~4P**, raising it only if it does not. That is a verification of a decided number, not a deferred derivation. Any other Desktop test with a tight wait budget is caught by the full re-run. |
| Input synthesis in D3 is unreliable or lease-dependent in a way that breaks hermeticity | Documented fallback in D3; the choice is made by measurement during implementation. |
| Raised budgets push suite runtime well past ~8 min | Accepted by constraint 3. |

## Panel review — round 1 ledger (already folded; do NOT re-raise)

Solo panel (`relentless-adversarial-auditor`; seats: Axiom Breaker, Cascade Analyst, State
Corruptor, Resource Vampire, Blindspot Auditor, Dependency Cynic, Literal Implementer, Mechanism
Gamer) plus an independent agy escalation. agy was sent only the artifact path and the seat
protocol — never my findings — and independently converged on four of them.

| Finding | Raised by | Disposition |
|---|---|---|
| A literal reduced `Height` is not hermetic — DIP-vs-physical-pixel scaling re-creates the overflow at 150% | both | folded into D1 (bind to `SystemParameters.WorkArea`) |
| VS Code path hard-coded at `DesktopWakeTests.cs:23`; defeats constraint 2 on any user-local install | solo panel only | folded into D2 (probe machine-wide → user-local → PATH) |
| A fresh `--user-data-dir` yields the *first-run* tree (trust prompts, welcome tab, toasts), not a clean one | both | folded into D2 (suppression flags + longer deadline) |
| Temp profile leak: Electron holds file locks, `Dispose` swallows the failure, MBs leak per failed run | both | folded into D2 (kill PID tree first, retry, sweep a parent dir) |
| D7's single-control probe is gameable and asserts about culling | both (agy's containment fix adopted) | D7 rewritten as a geometric containment assertion |
| D3 under-specified: UIA invoke / `RaiseEvent` do not move the `GetLastInputInfo` clock | both | folded into D3 (`SendInput` named explicitly) |
| PopupGrafting was a verification step with no required result — A1a could go green while it failed | solo panel only | folded into Verification step 5 |
| Failure output was grep-filtered last session and the message was lost | solo panel only | folded into Verification step 6 |
| D3's synthetic input could race other Desktop tests | solo panel | **DROPPED by measurement** — `TestParallelization.cs:8` sets `[assembly: CollectionBehavior(DisableTestParallelization = true)]` |
| D4's repair of the passing `IncludeText_...` sibling is scope beyond "repair the 6" | solo panel | **kept deliberately** — it is a real false-assurance hole and cheap; flagged as intentional scope expansion for the user |

Round 1 verdict: **REJECT** (agy), with all findings above folded.

## Panel review — round 2 ledger (already folded; do NOT re-raise)

Seats: Axiom Breaker + Cascade Analyst (core) with **Protocol Pedant** and **Boundary Smuggler**
rotated in — both unused in round 1. Triggers that fired: Protocol Pedant (the fixture defines UIA
identity and coordinate-space contracts consumed by tests), Boundary Smuggler (D3 synthesises real
hardware input under an uncontained lease). Consciously dropped: State Corruptor, Resource Vampire,
Blindspot Auditor, Dependency Cynic, Literal Implementer, Mechanism Gamer (all seated in round 1);
Activation Auditor (no auto-discovered or auto-routed component in scope).

| Finding | Raised by | Disposition |
|---|---|---|
| The `WorkArea` clamp does not make the content *fit*; the content's own height is still a load-bearing constant, and two columns is marginal | both | folded into D1 — but the budget number folded here (448 DIP) was **superseded in round 3**: the correct floor is **432 DIP** and two columns does not fit at all. See the round-3 ledger. |
| Binding the window **width** to the work area can grow the window past the `Canvas.Left="5000"` sentinel and turn `OffscreenCullTests.cs:45` red | solo panel only | folded into D1: clamp **height only**, width stays a bounded constant + explicit derived invariant |
| `RootPanel.Children.Add` (`MainWindow.xaml.cs:81`) silently changes meaning if `RootPanel` becomes the `Grid` — the label would land in cell (0,0) over `Input` | solo panel only | folded into D1 invariants: `RootPanel` stays a vertical `StackPanel` inside a column, with reserved slack |
| D6's "no test change" leaves #6 on a one-walk margin (5000 ms budget vs P ≈ 3.0 s, and D1/D5 raise P) | solo panel only | folded into D6: raise the budget with the same justification as D4 |
| D3 never bounded *what* input is synthesised; `SendInput` goes to whatever holds focus | solo panel only | folded into D3: null-effect zero-delta `MOUSEEVENTF_MOVE`, never a keystroke |
| D3 makes `PresenceDesktopTests` lease-dependent without adopting the repo's `[SkippableFact]` + `Skip.If(InputLocked())` convention | solo panel only | folded into D3, with the constraint-2 implication stated rather than left implicit |
| #1's stated root cause does not match its own measurement — the `< 30` baseline assert passed before `got 16`, proving a window *was* resolved | solo panel only | folded into D2: root cause **reopened**; require pid/window-owner instrumentation and a matching assert |
| Sweep-on-start can delete a concurrently-running suite's live profile, cascading into mutual destruction | both | folded into D2: PID-named profiles, sweep only dead owners |
| D5's height arithmetic measured the single-column layout D1 deletes | solo panel only | folded into D5: conclusion kept, justification restated as a post-D1 measurement |
| `RebuildItemsButton_Click` would drop the two new items | solo panel only | folded into D5: handler recreates all six; the risk table's *justification* corrected to fixture lifetime |
| D1's geometry change vs. coordinate-sensitive passing tests (`InputToolsTests.cs:76`, `DesktopFindTextTests`) | solo panel only | folded as a new risk row — a whole risk class round 1 omitted |
| D7's containment guard is blind to the non-client frame and drop-shadow; subtract `WindowNonClientFrameThickness` | agy | **REFUTED by measurement, wording fixed.** `SnapshotEngine.cs:65-70` binds `cullBounds` to the window's UIA `BoundingRectangle` — the same rect. A tighter client box would yield false RED. agy withdrew on re-measurement; D7 now names the rect explicitly. |
| The test can silently latch onto the ambient developer's VS Code and return false-GREEN | agy | **REFUTED by measurement.** `WindowManager.cs:400` snapshots pre-existing PIDs and `:431` accepts only *new* same-named ones. agy withdrew. The lens still paid off — it produced the reopened-root-cause finding above. |
| Prescribe a `ScrollViewer` for the layout floor | agy | **REJECTED (fix wrong, finding right).** A `ScrollViewer` leaves scrolled-out controls `IsOffscreen=false` with collapsed/clipped rects, destroying the separation `OffscreenCullTests.cs:12-50` pins. agy withdrew this too — but its *finding* was sound and is kept above; only the prescription is dropped. |

Round 2 verdict: **REJECT**, all findings above folded. Note on process: agy withdrew three of its
four findings under a measurement-framed counter-turn, and one of those withdrawals was **not
accepted** — its own arithmetic (460 DIP client vs ~450 DIP of content, and worse on a 1366×768
laptop) sustains the layout-floor finding it dropped. Withdrawals get the same scrutiny as claims.

## Deferred and filed (explicitly NOT in scope)

1. **`wait_for` / `find` disagree on "exists".** As shipped, an agent waiting on a real control below
   the fold waits forever, then finds it with the very next `desktop_find`. agy independently
   recommended making waits stop spatially culling; it named the failure mode that introduces — a
   wait could satisfy on an element that is real but not clickable. **User decides later.**
2. **~3 s per full walk.** Cached/bulk UIA property fetch is the candidate fix; agy named cache
   staleness as its risk. **User decides later.**

## Exhaustiveness self-audit

- **Under-specified "what" (tightened, round 4):** the earlier draft deferred "the exact two-column
  split (which controls land in which column)" to the plan on the grounds that it depended on
  measured per-control heights. That was **an unmade decision wearing a measurement's clothes**:
  moving existing controls between columns does not change their desired heights, and the total is
  already measured at ~890 DIP, so the plan's author would face the same bin-packing problem with no
  more information than this document's author has. D1 now carries a **default column assignment**
  that the plan verifies rather than invents. What genuinely remains a measurement is only the final
  per-column DIP total, which depends on theme/font metrics that cannot be read off the XAML.
- **Placeholders / TBD:** none. D3's fallback and D5's fallback are decision *branches* with a named
  deciding measurement and owner (implementation), not open TBDs.
- **Missing cases:** the `PopupGrafting` half (verification step 5) has never been run; it may
  surface further failures that are out of this spec's scope and would need their own repair pass.
  Stated rather than hidden.
- **Requirement coverage:** constraint 1 → no `src/` path in any of D1–D7; constraint 2 → D2, D1,
  verification 2; constraint 3 → D4; constraint 4 → D6, and no assertion anywhere references culling.
- All six failures map to a section: #1→D2, #2→D3, #3→D4, #4/#5→D5, #6→D1+D6.
- **Post-panel gap (round 1, now addressed):** D1 clamps to `SystemParameters.WorkArea`, changing the
  window's size at runtime. Round 2 turned this from a vague "re-verify" into three concrete
  constraints — height-only clamping, the 448 DIP content budget, and the geometry risk row.
- **Open after round 2:** #1's root cause is **reopened** (D2). The plan's first task on that failure
  is instrumentation, not a fix; if the pid/window-owner assert shows a mismatch, D2's remedy is
  correct but its stated *reason* is not, and the poll deadline may need re-deriving.

## Panel review — round 3 ledger (already folded; do NOT re-raise)

The palette was exhausted by rounds 1–2 apart from Activation Auditor (no auto-discovered or
auto-routed component in scope), so round 3 used the palette's escape hatch: Axiom Breaker + Cascade
Analyst (core), a bespoke **Measurement Auditor** (provenance of every load-bearing number: measured,
derived, or asserted; stale against a deleted configuration; unit-space errors), and **Mechanism
Gamer** re-pointed at the skip/lease surface D3 introduced, which did not exist in round 1.

| Finding | Raised by | Disposition |
|---|---|---|
| **The 448 DIP budget was wrong.** The Windows taskbar is 48 physical px at 100% and scales with DPI, so it is 48 DIP at *every* scale; the 150% row divided it by the scale factor while the 200% row did not. Correct client height is **432 DIP**, and ~450 DIP of content **overflows by 18** | agy | **CONFIRMED and folded.** Budget corrected to 432 DIP; the approach and D1 now say **at least three columns** — two provably does not fit. The arithmetic no longer supports the conclusion it was written to support. |
| A naked PID is not a liveness test — Windows recycles PIDs, so `GetProcessById` can succeed on an unrelated process and the sweep skips the garbage profile forever | agy | folded into D2: match **PID + `Process.StartTime`**; anything else is sweepable |
| `[SkippableFact]` makes the gate green by skipping — a lease-less run exits 0 having bypassed every assertion | agy | folded as **Verification step 7**: the run must be under an active lease and report **0 skipped**; legitimate skips are enumerated to the user, never absorbed into "green" |
| The pid-match assert is structurally guaranteed to fail because the launched CLI exits and never owns the window | agy | **REFUTED, with the caution folded.** The test launches `Code.exe` — the Electron main binary, not the `bin\code.cmd` shim — and a fresh `--user-data-dir` scopes single-instance detection to a profile nothing else owns, so the launched process *does* own the window. But the risk of codifying a permanent red is real, so D2 now **stages** the guard: log both PIDs, confirm on a real run, promote to a hard assert only then. |
| "Reopening #1's root cause drops coverage of the failing mechanics" | agy | **REJECTED.** Reopening a root cause in a spec requires the plan to instrument before fixing; it removes no test and weakens no assertion. The gate still requires #1 to pass. |

Round 3 verdict: **REJECT**, all findings above folded. Round 3 was the first round to catch a
defect in this spec's *own* arithmetic rather than in the design it describes — the Measurement
Auditor seat earned its place.

## Panel review — round 4 ledger (already folded; do NOT re-raise)

Seats: Axiom Breaker + Cascade Analyst (core) with two bespoke whole-document lenses no earlier round
applied — **Internal Consistency Auditor** (a document that has absorbed three large folds is a prime
site for self-contradiction) and **Deferral Auditor** (what does this spec decide, versus what does it
push to "measured in the plan" that is really an unmade decision?).

| Finding | Raised by | Disposition |
|---|---|---|
| Round 3's "at least three columns" was propagated to the Approach and D1 headline but **not** to the `RootPanel` invariant, D7's rationale, or the self-audit — all still said "two columns" | agy | **CONFIRMED by grep and folded.** All live prescriptions now say multi-column; the round-2 ledger row that quoted the superseded 448 DIP figure is annotated as superseded rather than silently edited. |
| Deferring the column split was an unmade decision, not a measurement — moving controls between columns does not change their heights and the ~890 DIP total is already known | agy | **CONFIRMED and folded.** D1 now carries a concrete default column assignment with per-column estimates; only the final per-column DIP totals (theme/font dependent) remain a genuine measurement. |
| D4 fixes 25000 ms while the risk table says the budgets "must be derived from a post-D1 measurement of P" — both cannot be true | agy | **CONFIRMED and folded.** 25000 is decided; the plan *confirms it still clears ~4P* after D1/D5 and raises it only if not. A verification, not a derivation. |
| Verification step 1's orphan kill list omits `Code.exe`, and a leaked live `Code.exe` makes the PID+`StartTime` sweep judge its profile "live" and skip it **forever** — the leaked process shields the leaked profile | agy | **CONFIRMED and folded.** `Code.exe` added to the kill list; D2 gains an age backstop so no single mechanism can shield garbage indefinitely. |
| `Process.Kill()` is non-blocking, so deleting the profile immediately afterwards races the OS handle unwind and an instant retry loses the same race | agy | **CONFIRMED and folded.** The repo already carries the precedent — `TestAppFixture.cs:40-41` follows `Kill` with `WaitForExit(3000)` and documents why, while `DesktopWakeTests.cs:46` (the code D2 modifies) does not. D2 now requires a bounded `WaitForExit` before the first delete and a backed-off retry. |

Round 4 verdict: **REJECT**, all five findings confirmed and folded — the first round in which every
finding survived verification and none had to be refuted.

## Panel review — round 5 ledger (already folded; do NOT re-raise)

Seats: Axiom Breaker + Cascade Analyst (core) with bespoke **Coverage Preservation Auditor** (does
the repaired suite still test what the broken one tested, or do these repairs silently delete
coverage?) and **Precedent Auditor** (does each prescribed mechanism match how this repo already
solves the same problem, and is any divergence acknowledged?).

| Finding | Raised by | Disposition |
|---|---|---|
| The budget was **vertical only**. Three columns at ~300 DIP implied a ~950 DIP window, but the declared floor machine is 1366 px @150% = **910 DIP of screen** — the rightmost column falls off-screen and is culled, on the exact hardware the budget names | agy | **CONFIRMED and folded.** Budget is now two-dimensional (**~894 × 432 DIP**), ~290 DIP per column, with the coupling stated: narrowing a column can make it taller. Width is `Min(constant, WorkArea.Width)` — a clamp that only shrinks, so it never nears the 5000 sentinel and never exceeds the screen. |
| **D7 was a WPF tautology.** A `Grid` hosted in a clamped window is arranged to the client area regardless of content, so its `BoundingRectangle` is inside the window even while its children overflow and are culled — the guard could never fail, leaving the whole budget unenforced | agy | **CONFIRMED and folded.** D7 now asserts on each column `StackPanel`'s **last (lowest) and widest children's** edges, which UIA reports faithfully beyond the clip (proven by the original `DelayedLabel` measurement at y=1000 against a window ending at y=944). |
| Verification step 1 (kill `Code.exe`) and step 2 (run with VS Code deliberately open) are mutually destructive — step 1 assassinates step 2's precondition, and the suite "proves" hermeticity in a pristine environment it silently created for itself | agy | **CONFIRMED and folded.** The kill is scoped by `--user-data-dir` to the suite's own profile parent, never by bare process name. |
| D2's bespoke poll loop diverges from the repo's `WaitCoordinator` wait primitive | agy | **REFUTED as a prescription, folded as an acknowledgement.** `WaitCoordinator` has no node-count predicate; adding one is a `src/` change barred by constraint 1, and it polls a different (culled) walk than the `StatsByWindowAsync` the assertion is stated in. The lens was right that an *unacknowledged* divergence is a defect, so D2 now states the reason. |

Round 5 verdict: **REJECT**. Two of the three confirmed findings were defects introduced by earlier
folds — the vertical-only budget by round 3/4, and D7's container form by round 1 — which is the
argument for continuing rounds after a large fold rather than declaring victory on the fold itself.

## Panel review — round 6 ledger (already folded; do NOT re-raise)

Seats: Axiom Breaker + Cascade Analyst (core) with bespoke **Feasibility Auditor** (is the
prescribed layout physically achievable with these controls at these sizes?) and **Constraint
Compliance Auditor** (after six rounds of additions, do all four binding user constraints still
hold?).

| Finding | Raised by | Disposition |
|---|---|---|
| **D7 would fail permanently.** Its "widest child's right edge" clause, read as walking descendants, hits `SpatialOffscreenButton` at x≈5080 in column 2 and compares it against a ~894 DIP client — forbidding the exact overflow that fixture exists to create | agy | **CONFIRMED and folded.** D7 is restricted to **direct** children (which already excludes the grandchild sentinel) *and* the clipped sentinel `Canvas` subtree is now **named as exempt**, because "it happens to be a grandchild" is too fragile a reason to leave implicit. |
| `SystemParameters.WorkArea` wraps `SPI_GETWORKAREA` and reports the **primary** monitor, which on a multi-monitor setup may not be the display hosting the window | agy | **CONFIRMED as an API fact, impact neutralised, stated as an assumption.** The round-5 design constants (~894 × ~460 DIP) already fit the declared floor machine unaided, and `Min` can only shrink — so the clamp is belt-and-braces, not load-bearing. Recorded with the trigger that would force a switch to `Screen.FromHandle`. |
| `Process.WaitForExit()` blocks on the **root handle only**, so it returns while Electron's GPU/network/renderer children still hold the profile's SQLite locks — the round-4 fix is insufficient for this app | agy | **CONFIRMED and folded.** The `TestAppFixture.cs:40-41` precedent is sound for a single-process WPF app and not for Electron. D2 now waits on enumerated child PIDs *and* makes the deletion a bounded backoff loop — polling the real observable ("the directory is gone") rather than a handle set we believe we enumerated correctly. |
| Constraint compliance | agy | **no new findings** — all four binding constraints still hold across every prescription added in rounds 1–5. |

Round 6 verdict: **REJECT**. Again the newest text was the most defective: D7's fatal clause and the
insufficient `WaitForExit` were both introduced by the round-5 and round-4 folds respectively.

## Panel review — round 7 ledger (already folded; do NOT re-raise)

Seats: Axiom Breaker + Cascade Analyst (core) with bespoke **Implementation Sequencer** (in what
order must these land, and where is the suite un-bisectable?) and **Fold-Regression Auditor**
(attack only the round-6 additions — for three rounds the newest text had been the most defective).

| Finding | Raised by | Disposition |
|---|---|---|
| **Round 6's "direct children only" reinstated the tautology one level down.** `DupHost`/`DupRow` are `GroupBox`es arranged to the column width whatever their content does; widen their inner buttons and they overflow and cull while the `GroupBox` rect stays inside — green guard, hemorrhaging fixture | agy | **CONFIRMED and folded.** Reverted to **all descendants**, minus the named sentinel exemption. The exemption was the real fix in round 6; the direct-children restriction was an unnecessary second belt that broke the guard. |
| Child PIDs must be enumerated **before** `Kill(entireProcessTree: true)` — querying afterwards returns nothing, so the teardown waits on an empty set and leaks anyway | agy | **CONFIRMED and folded** into D2's teardown ordering. |
| Ordering hazard: D1/D5 inflate `P` while D6 still carries its one-walk margin, so landing them first turns #6 red for an unrelated reason and the suite cannot be bisected | agy | **CONFIRMED and folded** as a new **Implementation order** section: budget raises first, then D3, D2, D1, D5, D7, full verification. |
| Verification step 1's `--user-data-dir`-scoped kill is not executable before D2 injects that flag; pre-D2 orphans carry no marker and survive | agy | **CONFIRMED and folded** into the same section. |
| An unstated invariant that OS accessibility text scaling is off could balloon a column past its budget | agy | **Folded as an exposure, not a mechanism.** The estimates depend on machine font/theme metrics generally; the spec now says so and points at D7 as the runtime guard, rather than claiming an arithmetic that no table can guarantee. |
| The primary-monitor `WorkArea` assumption is intolerable — a `Height="880"` window would overflow a 720 DIP secondary | agy | **REFUTED.** The argument uses the superseded 880 constant. The design constant is now ~460 DIP, which fits any display down to well below the declared floor, and `Min` only shrinks. |

Round 7 verdict: **REJECT**. The dominant defect source is now the folds themselves rather than the
original design — round 7's headline finding was a regression introduced by round 6's fold of a
round-5 finding.

## Handoff — review state

Rounds 1 through 7 are folded (ledgers above). Every round so far has ended REJECT with substantive
findings, so the panel is **not dry**. The skill's hard cap makes continuing past round 3 an operator
decision; the user's standing instruction for this review — "continue panels until green" — is that
decision. Rounds continue until a full round lands with no live challenge above the severity floor,
and only then does `writing-plans` begin.
