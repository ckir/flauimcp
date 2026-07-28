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
| 1 | `Watch.DesktopWakeTests.Waking_hydrates_the_tree_while_held` | `expected the tree to hydrate while held (>100 nodes), got 16`. Cold VS Code → PASS (15 s); immediate re-run → FAIL (8 s), so it is deterministic and fails whenever any VS Code is already open. **⚠ Root cause NOT closed — see D2.** The originally recorded cause (single-instance ⇒ `LaunchAppAsync` returns early ⇒ `Delay(1500)` measures too soon) is not supported by the measurement, because the `< 30` opaque-baseline assert *passed* before `got 16`. Resolution succeeded; hydration failed. D2's first task is instrumentation, not a fix. |
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

Restructure `MainWindow.xaml` from one ~890 DIP column into a **three-column `Grid`** whose tallest
column fits inside the minimum supported client area, and clamp the window to the work area so OS
clamping cannot silently shrink it. This removes #6, restores the hidden `DupRow` coverage, and makes
every Desktop test screen-size independent. Then repair #1–#5 individually. D1 carries the budget
arithmetic and the column assignment.

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

## Standing preconditions (scope boundary on constraint 2)

The `Category=Desktop` suite has **always** required two things this spec does not remove:

- **An active input lease** (`flaui-mcp unlock --minutes N --allow-shells`), which only a human can
  grant. `--allow-shells` is mandatory or the terminal-tab tests fail `SinkInterlocked` for an
  unrelated reason.
- **A real, unlocked console session.** The suite drives live mouse and keyboard, so the user must
  step away or it fights them.

Constraint 2 ("hermetic — green on any dev machine, not green here with documented preconditions")
is therefore scoped: it means **no *additional* machine-specific preconditions**, on top of the two
above, which predate this work. D3 extends the lease dependency to one more test
(`PresenceDesktopTests`), which is the only place this spec touches that boundary at all.

> **Open item for the user.** If constraint 2 was meant to include removing the lease requirement
> itself, D3's primary path cannot satisfy it and its documented fallback (pure-logic test of
> `IdleActivity.Bucket` plus a real-source sanity assert) is the option that can — at the cost of no
> longer testing the integration. This is a user decision, not one to settle here.

## Design

Each section below states the **operative prescription only**. Where an earlier draft prescribed
something different, the correction and its reason live in the panel ledgers at the end of this
document — not inline — so that nothing here reads as a live instruction unless it is one.

### D1 — Fixture restructure (`test/FlaUI.Mcp.TestApp/MainWindow.xaml`)

Restructure the single ~890 DIP `StackPanel` column into a **three-column `Grid`**. Every existing
control keeps its `x:Name`, `AutomationId`, `Content`, and its containing `GroupBox`/`Canvas`/
`Border` parent. Only column placement changes.

**Window sizing.** Set both dimensions to `Min(designConstant, SystemParameters.WorkArea.<axis>)`
in code-behind — a clamp that can only ever *shrink* the window. Never bind either axis to the work
area, and never declare a literal size and leave it unclamped:

- an unclamped literal is not hermetic, because a work area's size *in DIPs shrinks as display
  scaling rises*, so any fixed DIP size eventually exceeds it;
- a *bound* width can grow the window past the `Canvas.Left="5000"` sentinel on a very wide primary
  monitor, inverting `OffscreenCullTests.cs:45`.

`Min` avoids both: it never approaches the sentinel and never exceeds the screen.

*Assumption:* `SystemParameters.WorkArea` wraps `SPI_GETWORKAREA` and reports the **primary**
monitor, which on a multi-monitor setup may not be the display hosting the window. This is tolerable
only because the design constants below already fit the declared floor machine unaided — the clamp
is belt-and-braces, not the mechanism the design depends on. If the constants ever grow to where the
clamp becomes load-bearing, switch to querying the hosting monitor (`Screen.FromHandle`-equivalent).

**The budget is two-dimensional.** The declared minimum supported machine is **1366×768 @150%**:

| | Screen DIP | − 48 DIP taskbar ⇒ WorkArea | − ~32 DIP title/border | **Client** |
|---|---|---|---|---|
| Height | 512 | 464 | ~432 | **432 DIP** |
| Width | 910 | 910 | ~894 | **894 DIP** |

(The Windows taskbar is 48 physical px at 100% and scales with DPI, so it is **48 DIP at every
scale** — it is not divided by the scale factor.)

So: three columns totalling ≤ ~890 DIP wide — roughly **290 DIP per column**, against the ~576 DIP
the single column had — each ≤ 432 DIP tall. Two consequences: no control may require more than
~290 DIP of width, and **narrowing a column can make it taller** (wrapping content), so the split is
verified in *both* dimensions, never height alone.

**Pin the fixture's font metrics at the root.** Set `TextElement.FontFamily` and
`TextElement.FontSize` on the `Window` so every control inherits a fixed metric instead of the host's
theme defaults. Without this the budget is only *probably* satisfied: a different default font, a
custom theme or larger system text inflates every column, and column 1's ~90 DIP of slack is exactly
what `DelayRevealButton_Click`'s runtime append needs — losing it reproduces the `DelayedLabel` cull
that failure #6 exists to fix. Constraint 2 asks for **green** on any dev machine, and a runtime
guard that fails loudly is better than a silent cull but is still not green. Pinning the metrics
makes the geometry deterministic by construction; D7 then guards future *content* growth rather than
host variance.

**Default column assignment.** Heights are estimates from the XAML plus the pinned metrics, not live
measurements. The plan confirms them and rebalances only if a column exceeds budget; D7 is the
runtime backstop either way.

| Column | Controls | Est. DIP |
|---|---|---|
| 1 — **`RootPanel`** (the append target) | `Input`, `Secret`, `TextDoc` (60), `Grid` (100), `OkButton`, `Status`, `RebuildItemsButton`, `ClearItemsButton` | ~340 |
| 2 | `ItemList` (~180: six items at natural height after D5), `OffscreenButton`, the clipped `Canvas` + `SpatialOffscreenButton`, `Check`, `Exp`, `FocusReveal`, `RevealedLabel` | ~315 |
| 3 | `ModalButton`, `FreezeButton`, `DelayRevealButton`, `Ticker`, the `Border`/`MenuTarget` (36), `DupHost`, both `DupRow` GroupBoxes | ~320 |

Column 1 is `RootPanel` deliberately: it is the shallowest, leaving ~90 DIP of slack below its last
child for `DelayRevealButton_Click`'s runtime append to land inside the window.

**Invariants that must survive the move**, each already load-bearing for a passing test:

- `OffscreenButton` keeps `AutomationProperties.IsOffscreenBehavior="Offscreen"`.
- `SpatialOffscreenButton` stays inside the clipped `Canvas Height="1"` at `Canvas.Left="5000"`, so
  it remains `IsOffscreen=false` but spatially outside — the bounding-box backstop fixture.
  **Derived invariant: the window's width must stay far below 5000 DIP.** The sentinel offset and
  the window width are coupled; `OffscreenCullTests.cs:45` breaks if the window ever grows wide
  enough to contain the sentinel.
- **`RootPanel` remains a vertical `StackPanel`.** The `Grid` *contains* the columns; it is not
  itself `RootPanel`. `MainWindow.xaml.cs:81` calls `RootPanel.Children.Add(tb)` to append
  `DelayedLabel`; if `RootPanel` were the `Grid`, that call would silently place the label in cell
  (0,0) on top of `Input` — it compiles, it runs, and it quietly changes the layout contract the
  code-behind depends on.
- `DupHost` and the two `DupRow` containers stay `GroupBox` (a bare panel has no UIA peer and could
  not be resolved as an ancestor scope).
- The `MenuTarget` `ContextMenu` with `MenuAlpha` / `MenuBeta` is unchanged.

**Not an option:** a `ScrollViewer`. It would leave scrolled-out controls reporting
`IsOffscreen=false` with collapsed or clipped bounding rects, destroying the deterministic
separation `OffscreenCullTests.cs:12-50` pins between the `IsOffscreenBehavior` branch and the
spatial-bounds branch.

### D2 — #1 `DesktopWakeTests`

**Root cause is reopened; instrument before repairing.** The spec's original cause — "single-instance
⇒ `LaunchAppAsync` returns early ⇒ the fixed `Delay(1500)` measures too soon" — is not supported by
the measurement: the warm re-run failed in 8 s with `got 16` **after the `< 30` opaque-baseline
assert had already passed**, proving a window *was* resolved and *was* opaque. Resolution succeeded;
hydration failed. `WindowManager.cs:400,424-432` shows `LaunchAppAsync` ignores pre-existing PIDs and
accepts only the launched PID or a **new** same-named one, so it cannot latch onto an ambient window
— but it can return a `(handle, pid)` pair whose `pid` is a transient launcher while the window
belongs elsewhere, and `WakeAsync(handle.Id, pid)` would then wake a process that does not own the
window under measurement. That reproduces `got 16` exactly.

So the first task is instrumentation, not a fix: log the launched PID, the resolved window's
**owning** PID, and the node count at each poll.

**Then the repair:**

- **Resolve the VS Code path.** `DesktopWakeTests.cs:23` pins
  `C:\Program Files\Microsoft VS Code\Code.exe`; a user-local install
  (`%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe`) or none at all fails immediately, which by
  itself defeats constraint 2. Probe machine-wide → user-local → `PATH`; if none resolves, fail with
  an actionable message naming what to install.
- **Isolate the instance.** Pass `--user-data-dir <fresh temp dir> --new-window`, forcing a genuinely
  separate VS Code regardless of ambient VS Code.
- **Suppress the first-run experience.** A pristine `--user-data-dir` is not a clean tree — it is the
  *first-run* tree: workspace-trust prompts, welcome/release-notes tabs and extension toasts steal
  focus and change the node count, which is exactly what the `> 100` assertion measures. Pass flags
  disabling workspace trust, welcome/release notes, telemetry and extensions. Without this, the
  single-instance race is merely traded for a first-run race.
- **Poll until hydrated** instead of `await Task.Delay(1500)`, against an **additive** threshold:
  poll `StatsByWindowAsync` until `after.Total >= before.Total + 100` — the same run's measured
  opaque baseline **plus** the node count hydration is expected to add — or a **20 s** deadline
  elapses. Assert after the loop, reporting *both* numbers, so a real failure distinguishes "never
  hydrated" from "hydrated but short". The deadline must accommodate a first-run launch, slower than
  the 8–15 s measured on a warm profile.

  *Additive, never multiplicative.* Hydration adds a roughly fixed population — the Chromium
  document's accessibility tree — it does not scale the shell's node count. A **relative** threshold
  (`>= 5 × before.Total`) inverts into a false green on exactly the defect this test exists to
  catch: with a small baseline of 3 nodes it demands only 15, and the measured failure state was
  **`got 16`** — so the poll would satisfy on the broken tree, exit, and pass while the app was never
  woken. Anchoring to `before.Total + 100` keeps the original constant's intent (the spike measured
  **14 opaque / 231 woken**, `DesktopWakeTests.cs:17`, i.e. ~217 added) while moving with the
  baseline instead of against it.

  *On VS Code drift:* if a future version hydrates far less, this fails rather than silently
  passing — which is the correct direction. The both-numbers diagnostic is what makes such a drift
  legible on the first failure, and loosening the threshold until a broken tree passes is not an
  acceptable alternative.
- **Promote the PID guard only after measuring.** The guard is *expected* to hold: the test launches
  `Code.exe` — the Electron main binary, not the `bin\code.cmd` CLI shim — and a fresh
  `--user-data-dir` scopes single-instance detection to a profile nothing else owns, so the launched
  process becomes the main process and owns the window. It would not hold if that isolation failed
  (flag typo, profile collision, a rejected `--user-data-dir`), which is exactly the silent failure
  the guard exposes. Log both PIDs, confirm they match on a real run, and only then assert.
- The opaque-baseline assert (`< 30` nodes) is unchanged; instance isolation is what makes it sound.

**Teardown, in this exact order:**

1. **Open and hold `Process` handles for the whole tree *before* signalling termination.** Two traps
   here: querying the tree *after* `Kill(entireProcessTree: true)` returns nothing useful (the parent
   is gone, children are terminating or reparented), and `Process.GetProcessById(pid)` on an
   already-dead child throws `ArgumentException`, which would crash teardown before the delete loop
   ever runs. A handle opened before the kill stays valid after the process exits — raw integer PIDs
   do not.
2. `Kill(entireProcessTree: true)`.
3. **`WaitForExit` on every held handle, not just the root.** `Kill` is non-blocking, and
   `WaitForExit()` blocks only on the handle it is called on — so waiting on the root alone returns
   while Electron's GPU/network/renderer children still hold the SQLite locks inside the profile.
   (`TestAppFixture.cs:40-41` sets the `Kill`-then-`WaitForExit` precedent and documents why; it is
   sufficient for a single-process WPF app and not for Electron.)
4. **Delete the profile in a bounded retry loop with backoff** — the observable to poll is "the
   directory is gone", not "a handle set we believe we enumerated correctly".

**Profile lifecycle — stated as requirements, not as an algorithm.** Each launch gets its own
profile directory under one parent; a directory is retained only while its owning process is
verifiably alive; nothing may leak indefinitely and nothing live may be deleted. **How** to identify
and check owners is the implementation plan's to choose — this spec deliberately stops short of
prescribing file formats, exception types, or call sequences, because doing so is writing C# in
Markdown and it is what turned this section into a source of churn.

What the implementation must demonstrably satisfy — each one a hazard established by measurement or
review, and the reason this list is worth more than a prescribed algorithm:

1. **The directory's identity must be derivable before launch.** `--user-data-dir` is built before
   `Process.Start()`, so anything the OS only assigns afterwards — the PID above all — cannot name
   the directory.
2. **Ownership must survive PID recycling.** Windows reuses PIDs aggressively, so a bare PID match
   can bind a stale directory to an unrelated live process and shield it from sweeping forever.
3. **The liveness check must never throw out of the sweep.** It runs against processes that may have
   exited, may be mid-exit, or may be elevated and un-inspectable by a non-elevated test runner. No
   such failure may abort the sweep — an exception escaping the loop aborts the run and leaks every
   remaining directory.
4. **"Unknown" and "unverifiable" are different, and only one of them is protected.** The
   discriminator is whether an ownership record exists yet:
   - **A record exists but cannot be verified** — the recorded process is gone, is mid-exit, or the
     PID has been recycled onto an elevated process a non-elevated runner cannot inspect. In every
     such case it is provably **not ours** (our own process is inspectable by us), so the directory
     is **dead-owner and sweepable**. Treating these as "unknown" would permanently shield exactly
     the garbage requirement 2 exists to reap.
   - **No record exists yet** — a concurrent run in the window between creating its profile and
     recording who owns it. This is genuinely **unknown**, must **not** be swept on sight, and is
     resolved by age (requirement 5): a record-less directory that is minutes old is a booting run,
     one that is hours old is debris.
5. **A live owner is never swept, however old.** A developer paused on a breakpoint keeps a genuinely
   live VS Code for hours; age may decide how aggressively *dead* profiles are reaped, but it may
   never override liveness. Equally, assembly-level parallelization is disabled while *process*-level
   concurrency is not, so a second `dotnet test` must not be able to delete the first's live profile.
6. **A live orphan must not shield its directory forever.** The one case rules 4 and 5 cannot reach
   is an abandoned but genuinely running `Code.exe`. That is closed from the other side — verification
   step 1's `--user-data-dir`-scoped kill makes the owner dead, after which the ordinary sweep
   collects it.

**Three tests share `LaunchAsync`** (`Waking_hydrates_the_tree_while_held`,
`Closing_the_window_auto_releases_its_wake`, `Release_removes_the_wake_from_the_registry`), so every
change here applies three times per run and six times across the two consecutive runs of
verification step 2. Runtime is accepted by constraint 3; the profile-leak surface is why the sweep
rules above are not optional.

**Why a bespoke poll loop rather than `WaitCoordinator`.** The repo centralises UIA waits in
`WaitCoordinator.cs`, and diverging silently would be a defect. The divergence is deliberate:
`WaitCoordinator` expresses `exists`/`gone`/`enabled` on a selector plus tree-stability and has **no
node-count predicate**; adding one is a `src/` change that constraint 1 forbids. It also polls
through its own culled `PollOptions` walk, whereas the assertion under repair is stated in terms of
`StatsByWindowAsync` — polling a different call than the one asserted would measure a different
thing.

### D3 — #2 `PresenceDesktopTests`

`PresenceDesktopTests.cs:12-15` synthesises no input; it reads the real OS-wide `GetLastInputInfo`
clock and asserts a **human** typed < 60 s ago. Make the test generate its own input, then assert
`IdleActivity.Bucket(...) == Activity.Active`.

- **The mechanism is Win32 `SendInput`** — the same path the `PopupGrafting` tests already use.
  `GetLastInputInfo` reports the last *low-level hardware* input event, so a UIA `InvokePattern`
  call or a WPF `RaiseEvent` does not move that clock and would leave the test failing. Naming this
  is load-bearing: two of the three plausible mechanisms are silently wrong.
- **The input must be null-effect.** `SendInput` delivers to whatever window holds focus — during a
  Desktop run that may be the TestApp, the VS Code instance D2 just launched, or the developer's own
  editor. Synthesise the smallest event that moves the clock and nothing else: a zero-delta
  `MOUSEEVENTF_MOVE`, verified by measurement to move the idle clock. Never a character keystroke.
- **Use the repo's lease convention:** `[SkippableFact]` with `Skip.If(InputLocked(), …)`, as
  `InputToolsTests.cs:64-67` does. See *Standing preconditions* above for the constraint-2 scope
  this touches.

*Fallback if synthesis proves unreliable:* split into a lease-free pure-logic test of
`IdleActivity.Bucket` over injected values plus a real-source sanity assert (`idle >= 0`). This is
**strictly weaker** — it stops testing the integration — so it is a fallback, not a preference.

### D4 — #3 `WaitForStableTests`

- `Structure_is_stable_despite_a_live_ticker`: timeout **5000 → 25000**; `quietMs`/`pollIntervalMs`
  unchanged at 500/250. Record the justification in-test: the floor is ~4 × walk cost, and one walk
  of the 94-node window measured **P ≈ 3.0 s**, giving a ~12.5 s floor against the old 5000 ms
  budget — unreachable by 2.5× even with a perfectly static tree.
- `IncludeText_on_a_live_ticker_times_out_unstable`: additionally assert that the same window with
  `includeText:false` **does** go stable, so the test can only pass when the ticker is genuinely the
  cause of instability. Today it asserts only a timeout, so it would pass even if the tree never
  changed. Both under the raised budget.

### D5 — #4/#5 `FindTests`

The ListBoxItems' UIA **Name is their Content** — `"A" "B" "C" "NamedOnly"` — so
`name contains "Item"` + controlType `ListItem` matches nothing. The tests confuse `AutomationId`
with `Name` and have been wrong since authored.

- Add two `ListBoxItem`s with Content `ItemOne` / `ItemTwo` (AutomationIds `ItemD` / `ItemE`), and
  **remove the `ListBox`'s fixed `Height="120"` entirely** so it flows to its natural desired size.
  A raised-but-still-fixed height would reintroduce exactly the fragility D1 removes: under a custom
  theme or larger system text the items grow while the box stays pinned, the last ones scroll out of
  the UIA tree, and verification step 3 fails. Letting it flow means an unexpectedly tall list makes
  its *column* taller, which D7 catches loudly, instead of silently dropping items. The plan
  confirms no test depends on `ItemList` being scrollable.
- Both tests keep their original `FindQuery(null, "Item", "contains", "ListItem", false)` and their
  ListItem coverage; only the fixture gains matching elements.
- **Update `RebuildItemsButton_Click` to recreate all six items.** `MainWindow.xaml.cs:36` hard-codes
  `("ItemA","A"), ("ItemB","B"), ("ItemC","C")`, so after D5 a Rebuild click would permanently drop
  `ItemOne`/`ItemTwo`. This cannot bite today — every `FindTests` `[Fact]` constructs its own
  `TestAppFixture` (`FindTests.cs:27,48,63,79,…`), so the rebuilding test cannot contaminate the
  querying ones — but that is a **fixture-lifetime** argument, and the classes that share one app
  (`WaitForTests`, `AmbiguousResolutionTests`, both `IClassFixture<TestAppFixture>`) would not be
  protected by it. Keeping the handler consistent with the declared contents removes the trap
  instead of relying on a lifetime detail holding forever.

### D6 — #6 `WaitForTests`

The appended `DelayedLabel` genuinely exists at `bounds [176,1000,560,16]`, `isOffscreen:FALSE`,
while the window's UIA rect is `@{156,156,600,788}` — cull edge y=944. `SnapshotEngine.cs:65-70`
culls any element not intersecting the window rect; waits apply that cull, `desktop_find` does not.
**D1 fixes this**: once the fixture fits, the label is inside the window rect. Nothing is asserted
about culling in either direction, satisfying constraint 4.

**Raise the budget as well.** `WaitForTests.cs:38` waits with `timeoutMs=5000`, `pollIntervalMs=500`
against P ≈ 3.0 s. `WaitCoordinator.cs:92-99` checks the deadline only *after* an unsatisfied poll,
so the wait cannot return false before one full walk completes — which is the only reason 5000 ms
survives today. The label appears 600 ms after the invoke, i.e. after poll #1 has already enumerated
`RootPanel`'s children, so satisfaction lands on poll #2 at ~6.5 s. That works only while **one walk
stays under 5 s**, and D1 restores the previously culled second `DupRow` while D5 adds two
`ListItem`s, so P goes **up**. A budget that holds only while a UIA walk stays under 5 s is a
machine-speed assumption — exactly what constraint 2 forbids. Raise it into D4's class; constraint 3
permits it.

### D7 — Fixture-integrity regression guard (new test)

Assert containment geometrically: **for every descendant of every column `StackPanel`, excluding the
exempt subtree below, the bottom and right edges lie inside the window's UIA `BoundingRectangle`** —
the same rectangle `SnapshotEngine.cs:65-70` binds `cullBounds` to.

Three properties make this the right form, each of which a weaker version failed:

- **The window's UIA `BoundingRectangle`, not a "client" rect.** That rect includes the non-client
  frame and the Win11 drop-shadow aura, and it is what the cull compares against. A tighter client
  box built from `SystemParameters.WindowNonClientFrameThickness` would yield **false RED** — D7
  failing on content that overflows into the border while the cull's own `IntersectsWith` still
  includes it — and would reimport a DIP→physical conversion.
- **Descendants, not the container and not direct children.** A `Grid` hosted in a clamped window is
  arranged to the client area regardless of content, so "the Grid is inside the window" is true even
  while fifty children overflow — an assertion that can never fail. Direct children have the same
  problem one level down: `DupHost` and `DupRow` are `GroupBox`es arranged to the column width
  whatever their content does, so widened inner buttons overflow and cull while the `GroupBox` rect
  stays neatly inside. Only a full descendant walk closes both.
- **The sentinel subtree is exempt.** `SpatialOffscreenButton` sits at `Canvas.Left="5000"` *by
  design* — it is the fixture proving the spatial cull works, and `OffscreenCullTests.cs:45` depends
  on it being outside. A naive descendant walk would find it at x≈5080, compare it against a ~894
  DIP client width, and fail **permanently**. The clipped sentinel `Canvas` and everything beneath
  it is named as exempt.

A `StackPanel` arranges children at their desired size and lets them overflow the clip, and UIA
reports those overflowing rects faithfully — proven by the original `DelayedLabel` measurement at
`bounds [176,1000,…]` against a window ending at y=944. So per-descendant edges are meaningful where
the container's are not.

This is a property of the fixture's layout, not of the cull, so it stays valid whichever way the
deferred `wait_for`/`find` decision goes, and it cannot be gamed by overflowing whichever column a
narrower guard did not name.


## Implementation order

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
   `Code.exe` whose command line points at the suite's own profile parent directory — as a
   process-*tree* kill.** Killing the matching main process alone reparents its Electron GPU,
   renderer and extension-host children to the OS, where they stay alive holding exclusive locks on
   the profile's SQLite databases; the next sweep's delete then fails and both the processes and the
   directory leak permanently. (Round 4:
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
| **Machine font/theme metrics can inflate a column past the 432 DIP budget** — the per-column estimates are arithmetic, and no table can guarantee host defaults | **Primary mitigation: D1 pins `TextElement.FontFamily`/`FontSize` at the fixture root**, removing host font variance from the geometry entirely. D7 is the backstop, catching future *content* growth rather than host variance — a loud failure is better than a silent cull, but pinning is what actually delivers constraint 2's "green on any dev machine". |

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

## Panel review — round 8 ledger (already folded; do NOT re-raise)

Seats: Axiom Breaker + Cascade Analyst (core) with bespoke **Fold-Regression Auditor** (round-7
additions only) and **Cold-Reader Executability Auditor** (judge the document as an implementer with
none of this context).

| Finding | Raised by | Disposition |
|---|---|---|
| **The document had degenerated into a chronological journal of its own corrections.** A cold reader hits the superseded prescription first and the correction paragraphs later: D1 sizing (height-only, then both axes), D2 teardown (kill→delete, then root `WaitForExit`, then child PIDs, then snapshot-before-kill), D7 (root `Grid`, then column children, then descendants, then the exemption). Each superseded line reads as a primary instruction | agy | **CONFIRMED — the most consequential finding of the review, and folded structurally.** All of D1–D7 rewritten to state the operative prescription **once, first and whole**; the "what we thought before and why it was wrong" now lives only in these ledgers. The Problem table's row #1 and the Approach section were corrected the same way. |
| `Process.GetProcessById(pid)` on an already-dead child throws `ArgumentException`, so round 7's "snapshot the child PIDs, then kill, then wait on the recorded set" crashes teardown before the backoff-delete ever runs | agy | **CONFIRMED and folded.** D2 now opens and **holds `Process` handles** during the snapshot, before signalling termination — a held handle stays valid after exit, a raw integer PID does not. |
| D3 cites a "Standing constraints" section that does not exist in this document, and its lease dependency sits awkwardly against constraint 2's "not green here with documented preconditions" | agy | **CONFIRMED and folded.** A **Standing preconditions** section now exists, states the lease and console requirements the suite has always had, and scopes constraint 2 to mean *no additional* preconditions. The residual tension is **surfaced to the user as an open item** rather than settled here — it challenges a constraint the user set. |
| `WokenNodeFloor = 100` is an absolute constant against a volatile third-party UI: if a future VS Code hydrates to 95, the poll burns the full 20 s every run and then fails | agy | **CONFIRMED and folded.** D2 now characterises hydration *relatively* — a large multiple of the same run's measured opaque baseline — and reports both numbers on timeout so "never hydrated" is distinguishable from "hydrated below an absolute floor". |

Round 8 verdict: **REJECT**, but the character of the findings changed: three were defects in how the
document *communicates* rather than in what it prescribes, which is the signal that the design itself
has converged.

## Panel review — round 9 ledger (already folded; do NOT re-raise)

Seats: Axiom Breaker + Cascade Analyst (core) with bespoke **Flattening-Loss Auditor** (did the
round-8 hand rewrite of ~330 lines silently drop a prescription the eight ledgers claim was folded?)
and returning **Cold-Reader Executability Auditor** (did the restructure actually work?).

| Finding | Raised by | Disposition |
|---|---|---|
| D2's rewrite **over-corrected into un-sized placeholders** — "the hydration threshold" and "a large multiple" are not numbers, so a cold implementer cannot write the loop without inventing constants | agy | **CONFIRMED and folded.** The threshold is now `after.Total >= 5 × before.Total`, justified from the spike this test was built on (14 opaque / 231 woken ≈ 15×, `DesktopWakeTests.cs:17`), so 5× sits far below the real ratio and far above any non-hydrated reading. |
| D5's "raise the `ListBox` `Height`" reintroduces the fragility D1 removes — under a custom theme or larger text the items grow while the box stays pinned and the last ones scroll out of the UIA tree | agy | **CONFIRMED and folded.** The fixed `Height="120"` is **removed entirely**; the list flows to its natural size, so an unexpectedly tall list makes its column taller and D7 catches it loudly instead of silently dropping items. |
| The age backstop deletes a **paused debugger's** live profile — a developer on a breakpoint keeps VS Code alive for hours, and a second run would yank its SQLite DBs | agy | **CONFIRMED; folded with a different fix than proposed.** agy wanted liveness as the sole criterion, which reinstates the round-4 orphan-shield hole. Instead the age backstop is demoted to a retention policy on **dead-owner** profiles only — never an override of liveness — and the orphan-shield hole is closed by verification step 1's scoped kill, which makes the owner dead so the ordinary sweep collects it. |
| The accessibility/font-metrics exposure folded in round 7 was **lost** in the rewrite | agy | **REFUTED — it survives**, stated in D1 immediately beside the estimates table it qualifies. But the lens was fair: it is easy to miss where it sits, so a Risks row now cross-references it. |

**Flattening verified independently.** Rather than accept a single spot-check, every distinctive
token from the pre-flatten design (`4312385`) was diffed against the current text: of ~50 load-bearing
markers — from `IsOffscreenBehavior`, `Canvas.Left` and `SPI_GETWORKAREA` through `MOUSEEVENTF_MOVE`,
`entireProcessTree` and `SnapshotEngine.cs:65-70` — the only one absent is the word *"tautology"*,
which labelled a rejected form that D7 now explains without needing the term. 329 → 288 lines with no
substantive loss.

Round 9 verdict: **REJECT**, but every finding was an implementability detail — a missing constant, a
fixed height, a sweep predicate — with none touching the design's soundness.

## Panel review — round 10 ledger (already folded; do NOT re-raise)

Seats: Axiom Breaker + Cascade Analyst (core) with bespoke **End-to-End Outcome Auditor** (the
terminal question: executed exactly as written, does each of the six failures actually go green and
*stay* green?) and **Fold-Regression Auditor** on the round-9 additions. The payload stated the
severity floor strictly and told the peer that a GREEN verdict was a legitimate outcome.

| Finding | Raised by | Disposition |
|---|---|---|
| **The round-9 `5 × baseline` threshold is a false green on the exact defect #1 exists to catch.** Hydration *adds* a roughly fixed population (the Chromium document tree); it does not scale the shell's count. With a 3-node baseline the threshold demands only 15, and the measured failure state was **`got 16`** — the poll would satisfy on the broken tree and pass | agy | **CONFIRMED and folded — this corrects an error introduced by the round-9 fold.** The threshold is now **additive**: `after.Total >= before.Total + 100`, preserving the original constant's intent (spike: 14 opaque / 231 woken ≈ 217 added) while still moving with the baseline. Drift now fails rather than silently passing, which is the correct direction; the both-numbers diagnostic makes it legible. |
| **Naming the profile directory by PID is circular and unimplementable** — `--user-data-dir` must be built before `Process.Start()`, but the PID exists only after | agy | **CONFIRMED and folded.** The directory is named by **GUID**; a **sidecar file** written immediately after launch records the owner's PID and `StartTime`. A directory with no sidecar counts as dead-owner and is swept. |
| Reading `Process.StartTime` on a PID Windows has recycled onto an elevated or SYSTEM process throws `Win32Exception` (access denied), crashing the sweep loop, aborting the run and leaking the directory | agy | **CONFIRMED and folded.** Both failure modes of the liveness check — `ArgumentException` (not running) and `Win32Exception` (access denied) — are caught and treated as dead-owner. |
| Whether the six failures actually go green and stay green | agy | **no new findings** — the seat that asks the terminal outcome question found nothing. |

Round 10 verdict: **REJECT**, but every finding sits inside D2's teardown and threshold mechanics;
the outcome seat is clean, and the design-level seats produced an implementation impossibility rather
than a design flaw.

## Panel review — round 11 ledger (already folded; do NOT re-raise)

Seats: Axiom Breaker + Cascade Analyst (core) with bespoke **Fold-Regression Auditor** (round-10 D2
additions only) and **Abstraction-Level Auditor**, seated on an observed pattern: every round-10
finding was inside D2, and each fix spawned another D2 finding one level lower — threshold, then
naming, then exception handling.

| Finding | Raised by | Disposition |
|---|---|---|
| **D2's sweep had descended from design into implementation** — a spec that dictates file formats and enumerates C# exception types is debugging execution semantics in Markdown, and that is the structural cause of the round-over-round churn | agy | **CONFIRMED and folded — this is the finding that ends the descent.** The profile-lifecycle block is rewritten as **six numbered requirements** the implementation must satisfy, each naming a hazard established by measurement, with the mechanics (identifier scheme, sidecar format, exception types, call order) explicitly left to the plan. |
| TOCTOU: a process alive at `GetProcessById` but exited before `StartTime` is read throws `InvalidOperationException`, which the enumerated catch list misses — crashing the sweep | agy | **CONFIRMED as a real hazard; folded as requirement 3** ("the liveness check must never throw out of the sweep", covering exited, mid-exit and un-inspectable processes) rather than by extending an exception list. Adding a third type would have been one more step down the same staircase. |
| Missing-sidecar race: a concurrent run in the window between creating its profile and writing the sidecar would be classified dead and deleted mid-boot | agy | **CONFIRMED; folded as requirement 4** — "unknown is not dead". |
| Axiom Breaker | agy | **no new findings** |

Round 11 verdict: **REJECT**, but with a qualitative change: one core seat returned clean, and the
two live findings were **evidence for** the abstraction-level diagnosis rather than independent
defects. Both dissolve once the section states requirements instead of an algorithm — which is also
what the repo's spec-vs-plan discipline requires, since a line-level plan may only be authored
against code that already exists, and none of this code does yet.

## Panel review — round 12 ledger (already folded; do NOT re-raise)

Seats: Axiom Breaker + Cascade Analyst (core) with bespoke **Fold-Regression Auditor** (round 11's
six requirements) and returning **End-to-End Outcome Auditor**. The payload additionally ruled
"you did not specify the file format / exception type / call order" out of scope, since round 11
lifted those to the plan deliberately.

| Finding | Raised by | Disposition |
|---|---|---|
| **Round 11's requirements 3 + 4 accidentally re-shielded the garbage requirement 2 exists to reap.** "Any failure to establish ownership means not verifiably ours" plus "unknown is not dead, do not sweep" makes an access-denied check on a PID recycled onto an elevated process *unknown* — hence permanently protected | agy | **CONFIRMED and folded — a regression in my own round-11 rewrite.** Requirement 4 now turns on **whether an ownership record exists**: a record that exists but cannot be verified is provably *not ours* (our own process is inspectable by us) and is sweepable; only a directory with **no record yet** is genuinely unknown, protected on sight, and resolved by age. |
| Verification step 1's `Code.exe` kill must be a process-**tree** kill — killing the main process alone reparents the Electron GPU/renderer/extension-host children, which stay alive holding the profile's SQLite locks, so the next sweep's delete fails and both leak | agy | **CONFIRMED and folded** into verification step 1. |
| **Failure #6 does not go green on a host whose text metrics differ** — column 1's ~90 DIP of slack is precisely what the runtime `DelayedLabel` append needs, and D7 failing loudly is not the same as the test passing, which is what constraint 2 asks for | agy | **CONFIRMED and folded — the fix is better than the guard.** D1 now **pins `TextElement.FontFamily`/`FontSize` at the fixture root**, making the geometry deterministic by construction and removing host font variance from the budget. D7 is demoted to guarding future *content* growth. The Risks row is rewritten accordingly. |
| Axiom Breaker | agy | **no new findings** |

Round 12 verdict: **REJECT**. The End-to-End seat, clean in round 10, found a real outcome defect
once it re-examined the layout budget — vindicating the decision to re-seat it after D2 churned.

## Handoff — review state

Rounds 1 through 12 are folded (ledgers above). Every round so far has ended REJECT with substantive
findings, so the panel is **not dry**. The skill's hard cap makes continuing past round 3 an operator
decision; the user's standing instruction for this review — "continue panels until green" — is that
decision. Rounds continue until a full round lands with no live challenge above the severity floor,
and only then does `writing-plans` begin.
