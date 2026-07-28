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

**Window sizing (revised — panel round 1).** A reduced *literal* `Height` does not achieve
hermeticity, it only swaps one hardware assumption for another: WPF sizes in DIPs while the cull
compares physical pixels, so at 150% scaling a 700-DIP window is 1050 px and overflows a 1080p work
area again. Instead the window binds its size to `SystemParameters.WorkArea` at runtime (or is
clamped to it in code-behind), and the two-column split gives the content enough headroom to fit
inside that measured area at any scale. No literal pixel constant is load-bearing.

Invariants that must survive the move, each already load-bearing for a passing test:
- `OffscreenButton` keeps `AutomationProperties.IsOffscreenBehavior="Offscreen"`.
- `SpatialOffscreenButton` stays inside the clipped `Canvas Height="1"` at `Canvas.Left="5000"`, so
  it remains IsOffscreen=false but spatially outside — the bounding-box backstop fixture.
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
- The opaque-baseline assert (`< 30` nodes) is unchanged; instance isolation is what makes it sound.

### D3 — #2 `PresenceDesktopTests`

Primary: synthesise input from the test itself (it runs under the lease the suite already requires),
then assert `IdleActivity.Bucket(...) == Activity.Active`.

**The mechanism is not free choice (panel round 1).** `GetLastInputInfo` reports the last *low-level
hardware* input event. A UIA `InvokePattern` call or a WPF `RaiseEvent` does not touch that clock, so
either would produce a test that still fails. The synthesis must be Win32 `SendInput` — the same path
the `PopupGrafting` tests already use. Naming this is load-bearing: the earlier draft left it to the
implementer to guess, and two of the three plausible guesses are silently wrong.

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
  raise the ListBox `Height` 120 → 180. The existing 4 items occupy y=473..553 in a box ending at
  y=591 — only ~38 px of slack, so two more items require the extra height.
- Both tests keep their original `FindQuery(null, "Item", "contains", "ListItem", false)` and their
  ListItem coverage; only the fixture gains matching elements.

### D6 — #6 `WaitForTests`

No test change. Fixed by D1. Nothing is asserted about culling in either direction, satisfying
constraint 4.

### D7 — Fixture-integrity regression guard (new test)

**Revised after panel round 1 — the single-control probe was compliance theatre.** Asserting that
`Row2Btn` appears in a default snapshot fails in two ways: after D1 moves the layout to two columns
`Row2Btn` is no longer the bottom-most control, and a developer could overflow the *other* column
while the guard stayed green. It also asserted *about culling*, in tension with constraint 4.

Instead, assert containment geometrically: the root `Grid`'s `BoundingRectangle` is fully inside the
window's client bounds. That is a property of the fixture's layout, not of the cull, so it stays
valid whichever way the deferred `wait_for`/`find` decision goes, and it cannot be gamed by adding
controls to whichever column the guard does not happen to name.

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
| `RebuildItemsButton_Click` recreates only 3 items (`ItemA/B/C`), so after a rebuild the `contains "Item"` query would find 0 | Confirm no test rebuilds and then runs that query. `Find_minted_ref_re_resolves_after_tree_mutation` rebuilds but queries `automationId=ItemB`, so it is unaffected. Resolved in the plan. |
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

Round 1 verdict: **REJECT** (agy), with all findings above folded. A round 2 with a rotated seat has
**not** been run — see the handoff note at the end.

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
- **Post-panel gap:** D1 now depends on binding to `SystemParameters.WorkArea`, which changes the
  window's size at runtime. Any test that assumes a fixed 600×880 window must be re-verified — this
  is a NEW risk introduced by the round-1 fold, resolved in the plan, not here.

## Handoff — review state

Panel round 1 is folded (ledger above). **Round 2 has not been run**: it would rotate in a seat not
yet used (e.g. Protocol Pedant over the UIA-contract implications of the fixture move, or State
Corruptor over the runtime window-resize binding). The round-1 fold introduced at least one new
surface — the `WorkArea` binding — that a fresh seat has not yet attacked, so round 2 is warranted
before implementation begins rather than optional.
