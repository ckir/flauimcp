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

Restructure `MainWindow.xaml` from one ~890 px column into a **two-column `Grid`** (~450 px per
column) and drop the declared window `Height` below any plausible work area, so clamping cannot
occur. This removes #6, restores the hidden `DupRow` coverage, and makes every Desktop test
screen-size independent. Then repair #1–#5 individually.

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

Two-column `Grid`; every existing control keeps its `x:Name`, `AutomationId`, `Content`, and its
containing `GroupBox`/`Canvas`/`Border` parent. Only column placement changes.

**Window sizing (revised — panel rounds 1 and 2).** A reduced *literal* `Height` is not hermetic: a
work area's size **in DIPs shrinks as display scaling rises**, so any fixed DIP height eventually
exceeds it. The window therefore **clamps its height** to `SystemParameters.WorkArea` at runtime
rather than declaring one.

Round 2 corrections to that fold:

- **Height only; the width stays a bounded design constant.** Binding *width* to the work area is
  unsafe: `SpatialOffscreenButton` sits at `Canvas.Left="5000"` and `OffscreenCullTests.cs:45`
  asserts it is spatially culled. That assertion holds only while the window rect is narrower than
  the sentinel. A work-area-bound width on a wide primary monitor shrinks that margin, and above
  ~5080 DIP it inverts and turns a passing test red. See the added invariant below.
- **Clamping does not by itself make the content fit — a minimum supported client height must be
  declared.** "No literal pixel constant is load-bearing" was wrong: the content's own height is
  that constant, merely implicit. The clamp only stops the OS from silently shrinking the window;
  if the content is taller than the clamped client area it still overflows and still gets culled.
  The two-column split is **marginal, not comfortable**, on realistic hardware:

  | Machine | Screen DIP height | − taskbar | − title/border | Client DIP | vs ~450 DIP of content |
  |---|---|---|---|---|---|
  | 1920×1080 @200% | 540 | 492 | ~460 | **460** | +10 DIP — a 2% margin |
  | 1366×768 @150% | 512 | 480 | ~448 | **448** | **−2 DIP — does not fit** |

  So the plan must (a) declare the **minimum supported client height** the fixture is designed
  against — 448 DIP, the 1366×768 @150% budget — and (b) size the content to clear it with real
  margin, splitting into three columns or otherwise compressing if two do not. D7 makes a violation
  fail loudly at runtime instead of silently culling. The exact split is measured in the plan; the
  *budget* is fixed here.
- **A `ScrollViewer` is not an option.** It would leave scrolled-out controls reporting
  `IsOffscreen=false` with collapsed or clipped bounding rects — destroying the deterministic
  separation `OffscreenCullTests.cs:12-50` pins between the `IsOffscreenBehavior` branch and the
  spatial-bounds branch.

Invariants that must survive the move, each already load-bearing for a passing test:
- `OffscreenButton` keeps `AutomationProperties.IsOffscreenBehavior="Offscreen"`.
- `SpatialOffscreenButton` stays inside the clipped `Canvas Height="1"` at `Canvas.Left="5000"`, so
  it remains IsOffscreen=false but spatially outside — the bounding-box backstop fixture.
  **Derived invariant (round 2): the window's width must stay far below 5000 DIP.** The sentinel
  offset and the window width are coupled; `OffscreenCullTests.cs:45` breaks if the window ever
  grows wide enough to contain the sentinel.
- **`RootPanel` remains a vertical `StackPanel`.** The two-column `Grid` *contains* the columns; it
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
- **Teardown ordering.** Kill the VS Code PID tree *first*, then delete the temp profile, then retry
  deletion once — Electron GPU/renderer processes hold file locks, and a swallowed
  `UnauthorizedAccessException` silently leaks hundreds of MB per failed run. Place all profiles
  under one parent directory that the suite sweeps on start, so a leak is self-healing rather than
  cumulative. (Panel round 1.)
- **The sweep must not delete a live profile (round 2, both panels).** A blind sweep-on-start races
  a concurrent or hung run: assembly-level parallelization is disabled, but *process*-level
  concurrency is not, and yanking the SQLite DBs out from under a running VS Code crashes its
  renderer, fails the other run, and leaks the PID tree the failed teardown never reaped. Name each
  profile directory after its owning PID and sweep only entries whose owner process no longer
  exists.
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
  the node count at each poll, and assert the two PIDs match — so a regression here surfaces as
  "wrong process" rather than a misleading node count. Treat #1's root cause as reopened until that
  instrumentation runs.

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
`Row2Btn` appears in a default snapshot fails in two ways: after D1 moves the layout to two columns
`Row2Btn` is no longer the bottom-most control, and a developer could overflow the *other* column
while the guard stayed green. It also asserted *about culling*, in tension with constraint 4.

Instead, assert containment geometrically: the root `Grid`'s `BoundingRectangle` is fully inside
**the window's UIA `BoundingRectangle`** — the same rectangle `SnapshotEngine.cs:65-70` binds
`cullBounds` to. That is a property of the fixture's layout, not of the cull, so it stays valid
whichever way the deferred `wait_for`/`find` decision goes, and it cannot be gamed by adding
controls to whichever column the guard does not happen to name.

**Round 2 — say "UIA `BoundingRectangle`", never "client bounds".** The earlier wording invited
subtracting `SystemParameters.WindowNonClientFrameThickness` to build a true client box. That is the
wrong oracle in both directions: the window's UIA rect includes the non-client frame and the Win11
drop-shadow aura, so a tighter client box yields **false RED** — D7 failing on a Grid that overflows
into the border while the cull's own `IntersectsWith` still includes it — and it would reimport the
DIP→physical conversion round 1 removed. The guard must use the cull's own rectangle so the guard
and the mechanism it protects can never disagree.

## Verification

The gate is the **full 108-test suite**, not the 6 — D1 touches the fixture every Desktop test shares.

1. Kill orphaned `testhost.exe` / `FlaUI.Mcp.TestApp.exe` before and after every pass.
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

## Risks

| Risk | Mitigation |
|---|---|
| D1 changes UIA depth-first traversal order, breaking anything pinning element order or ref numbers (raised by agy) | Grep found no positional/index pinning in `test/`. Real gate is the full 108-test re-run, not the grep. |
| WPF virtualizes the two added ListItems out of the UIA tree (raised by agy) | Verification step 3 measures it directly. If virtualized, fall back to retargeting the query at `contains "Items"` + `Button`, accepting the loss of ListItem name coverage. |
| `RebuildItemsButton_Click` recreates only 3 items (`ItemA/B/C`), so after a rebuild the `contains "Item"` query would find 0 | **Justification corrected in round 2.** The safe reason is fixture *lifetime*, not query targets: every `FindTests` `[Fact]` builds its own `TestAppFixture`, so no rebuild can contaminate another test's app. D5 additionally updates the handler to recreate all six. |
| **D1 changes the window's geometry, and several passing Desktop tests are geometry-sensitive** (round 2) | `OffscreenCullTests.cs:45` is coupled to the `Canvas.Left="5000"` sentinel (addressed by the width invariant in D1); `InputToolsTests.cs:76` clicks the window at (0.5,0.5); `DesktopFindTextTests` OCRs the whole window and takes the *top* match for "Rebuild Items", so enlarging the window changes the OCR input set. All three read live rects rather than hard-coded coordinates, so none is expected to break — but the round-1 risk table considered only traversal order and ref numbers, never geometry. The full 108-test re-run is the gate. |
| D1 and D5 inflate the node count, raising the per-walk cost P for **every** wait in the suite (round 2) | D4's and D6's budgets must be derived from a **post-D1** measurement of P, not the pre-D1 3.0 s. Any other Desktop test with a tight wait budget is caught by the full re-run. |
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
| The `WorkArea` clamp does not make the content *fit*; the content's own height is still a load-bearing constant, and two columns is marginal (+10 DIP at 1080p@200%, **−2 DIP at 1366×768@150%**) | both | folded into D1: declare a **448 DIP minimum client height** budget, size content to clear it, split further if needed |
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

- **Under-specified "what":** the exact two-column split (which controls land in which column) and
  the exact reduced window `Height` are **deliberately left to the plan** — they depend on measured
  per-control heights after the move. Every behavioural contract that must survive the move is
  enumerated in D1.
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

## Handoff — review state

Rounds 1 and 2 are folded (ledgers above). Round 2 seated Protocol Pedant and Boundary Smuggler for
the first time and produced substantive findings in every seat, so the panel is **not dry** — under
the skill's hard cap, round 3 is an operator decision. The user's standing instruction for this
review is "continue panels until green", which is that decision: round 3 proceeds, rotating in seats
still unused across both rounds.
