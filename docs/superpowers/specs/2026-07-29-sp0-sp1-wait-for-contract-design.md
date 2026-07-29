# SP0 + SP1 — tripwires, then the `wait_for` contract

**Status:** design approved 2026-07-29. Awaiting spec review, then `writing-plans`.
**Release:** the first two phases of the fixes + opportunistic-hardening increment after `v0.20.0`.
**Constraint that does NOT apply here:** the A1a work was tests-only. **This release may touch `src/`.**

## Goal

Remove a contradiction between `desktop_wait_for` and `desktop_find` about whether an element exists,
without changing what a wait costs per poll, and without touching `desktop_snapshot`'s behaviour.

## Phases in this spec

| | Scope | Ships / reverts |
|---|---|---|
| **SP0** | Item 6 — JSON-shape tripwires, incl. a pin on `wait_for`'s **current** result shape | alone |
| **SP1** | Defects 1 + 2, `P4 + latch`, the additive `includeOffscreen` param, the two-path popup fix | alone, after SP0 |

Out of scope, deliberately: `wait_for_stable` (items 3 and the culled scope root) → **SP2**. Everything
in SP3/SP4. Feature work (ROADMAP Track B) → a separate brainstorm.

---

## Measured ground truth

Every claim below was read off the code this session. Cite these, do not re-derive them.

**The single flag gates two independent filters.**
`SnapshotOptions.IncludeOffscreen` controls both `SnapshotEngine.cs:65` (drop when the UIA
`IsOffscreen` property is true) and `SnapshotEngine.cs:66-70` (drop when the element's rect does not
`IntersectsWith` the window's rect). They answer different questions. Conflating them is what made
the defect hard to reason about.

**The spatial cull prunes a subtree.** `SnapshotEngine.cs:69` `return`s rather than recursing, so it
is doing real cost work on any app that lays out large subtrees outside the window without setting
`IsOffscreen`. Removing it unconditionally is not free.

**Its stated purpose is cost, not semantics.** `WaitCoordinator.cs:12` — *"culled
(IncludeOffscreen=false) to bound per-poll cost."*

**Nothing pins the cull for waits.** `OffscreenCullTests.cs` exercises `SnapshotAsync` only, in both
cull tests. Changing `wait_for` breaks no existing test.

**`find` filters neither.** `FindAsync` (`PerceptionManager.cs:497-535`) reports `offscreen` as a
field rather than acting on it. `EvaluateSelectorValueAsync:560` *does* drop `IsOffscreen`. So
"agree with `find`" and "agree with `valueEquals`" are different targets — earlier analysis conflated
them and reached the wrong design twice.

**`gone` is a false-positive channel.** `WaitCoordinator.cs:89` — `"gone" => match is null`. A
spatially culled element reports *gone*. This is the more dangerous half of the defect: a stalled
`exists` blocks, but a false `gone` hands control back on a wrong belief.

**Two paths are popup-blind.** `BuildModelAsync` passes owner-popups into the walk
(`PerceptionManager.cs:424`, `:429`); `EvaluateSelectorValueAsync:558-571` and `FindAsync:467-469`
root at bare `win`. Filed as
`docs/fix-the-tool-backlog/value-and-find-paths-miss-desktop-level-popups.md`.

---

## SP0 — tripwires

No behaviour change. Tool-level tests asserting that fields survive the anonymous projections in the
tool layer.

1. `truncatedFrom` and `Hint` survive projection (ROADMAP item 6 as filed).
2. **`wait_for`'s current result shape is pinned** — `satisfied`, `ref`, `elapsedMs`, `snapshotId`,
   **including that a timeout emits `"ref":null,"snapshotId":null` rather than omitting them.** That
   null-emission is the convention SP1's new fields follow, so it must be pinned, not assumed.

Reason for (2): SP1 adds fields to that result. Pinning the shape first means a later regression
surfaces as a failed assertion instead of a field that quietly stopped being emitted. Same
instrument-before-repairing move that paid off in A1a's T3.

**Two layers, because one is not achievable.** An earlier draft said "headless, plain `[Fact]`, default
CI scope". That is impossible for the tool layer: `DesktopWaitFor` and the `get_text` tools are
instance methods needing a live `WindowHandle`, and `PerceptionManager` is a concrete class with no
interface to fake. So:

- **Layer 1 — headless, plain `[Fact]`, default CI scope.** Pin the *record* shapes: `WaitForResult`
  (`WaitCoordinator.cs:7`) and `TextReadResult` (`PerceptionManager.cs:698`) carry the expected
  properties. Catches a field deleted from the record. Cheap and CI-gated.
- **Layer 2 — `Category=Desktop`.** Call the real tool method against the TestApp fixture and assert
  on the returned JSON keys. This is the layer that actually catches projection drift, which is the
  failure mode item 6 names. It runs in the Desktop gate — the same gate that validates SP1 — not in
  CI.

Layer 1 alone would be false comfort: the projection is where fields get dropped, and the record test
cannot see the projection.

---

## SP1 — the `wait_for` contract

### The mechanism: cull stays for polling, the unculled walk runs at the decision point

**Per-poll** cost is unchanged, so the purpose stated at `WaitCoordinator.cs:12` is preserved. The
expensive walk fires on a transition, never on every tick.

**It is not free, and the exact cost must be stated honestly:** `gone` pays **one extra unculled walk
at the moment it would return satisfied** — including on the first poll of the common successful case
(waiting for a dialog that has already closed). That is one additional walk per `gone` call, not per
poll, and it is the price of removing a silent false positive. `exists`/`enabled` pay one extra walk
per *failed* call and nothing on success.

**`gone`** — when the culled walk reports absent, run one confirmation walk with the spatial cull
disabled *before* returning satisfied.
- Still present but culled → **not satisfied**, keep polling.
- Absent from the unculled walk → satisfied, as today.

**Race.** The tree can change between the culled walk and the confirmation walk, so an element
re-inserted in that gap yields one wasted poll interval. Accepted deliberately: a false negative that
clears on the next tick is safe, where today's false positive hands control back permanently wrong.

**The latch is mandatory, not an optimisation.** Without it, a permanently culled element re-runs the
double walk on every poll — precisely the defect's own scenario. Once a confirmation walk has found
the element present-but-culled, that wait loop switches to the unculled walk for its remaining polls:
one walk per poll, not two, and only after the expensive case has been proven to apply.

Latch correctness, traced: if the element later moves back inside the window, the unculled walk still
finds it → still not gone → correct. If it is destroyed, the unculled walk finds nothing → gone →
correct. The latch is per-call state; it does not outlive the wait.

**`exists` / `enabled`** — semantics unchanged. On **timeout only**, once per call, run one unculled
diagnostic walk.

**Finding the element in the unculled walk is NOT sufficient to blame geometry.** The diagnostic emits
`outsideWindowBounds` only when it has **measured the geometry directly**:

```
!windowBounds.IntersectsWith(elementBounds)
```

Both rects are already in hand from the diagnostic walk — `windowBounds` from the depth-0 node,
`elementBounds` from the matched node. Otherwise the field is omitted and a bare `satisfied: false`
is returned.

**Test the condition, do not infer it from two walks.** An earlier draft required "absent from the
culled walk AND present in the unculled walk", which is wrong twice over:

- It cannot distinguish culling from lateness. An element that renders *inside* the window in the gap
  between the final culled poll and the diagnostic walk satisfies both halves — absent from the
  stale culled state, present in the fresh unculled one — and gets blamed on geometry while sitting
  on screen.
- For `enabled`, an in-window element with `Enabled: false` is present in *both* walks, so any
  presence-keyed rule risks reporting a geometry failure for a disabled control.

The direct intersection test handles both: a late element intersects the window rect and emits
nothing; a disabled in-window element intersects and emits nothing; only a genuinely out-of-bounds
element emits the token. Blaming the wrong cause confidently is worse than the bare timeout this
feature exists to replace.

**Skip the extra walk when the poll walk is already unculled.** If the caller passed
`includeOffscreen: true`, the polling walk carries no spatial cull, so the confirmation and diagnostic
walks are byte-identical to it and must be bypassed. Without this guard the caller who explicitly
opted into the heavier walk pays for it twice.

### The flag split

`SnapshotOptions` gains `CullToWindowBounds` (bool, **default `true`** = today's behaviour).
`SnapshotEngine.cs:66` becomes conditional on it in addition to `!IncludeOffscreen`.

- `SnapshotTools` continues to set `IncludeOffscreen` from the caller's parameter. When it is true,
  both filters are off exactly as today — `IncludeOffscreen` still gates line 65 *and* line 66, so
  `desktop_snapshot` is bit-identical and `OffscreenCullTests` stays green.
- **The polling walk is unchanged** — it keeps both filters, exactly as today.
- Only the **confirmation and diagnostic** walks set `CullToWindowBounds = false`, and they set that
  flag **alone**, keeping the `IsOffscreen` filter.

**Why the walk is kept rather than swapped for a targeted UIA lookup.** Popup coverage was the
original reason, and SP1's own popup fix removes it — once `SearchRoots` is wired in, a targeted
lookup sees menus too. The justification that survives is threefold:

1. **The satisfy path needs a model regardless.** `WaitCoordinator.cs:94-96` mints the returned `ref`
   and `snapshotId` from a real snapshot. A targeted lookup cannot produce either.
2. **The walk carries the security floor** — the deny-list guard and `IsPassword` name redaction —
   and the popup dedup/grafting. A second lookup path would have to re-implement all of it.
3. **Blast radius.** Restructuring the poll loop in the same phase that changes its semantics is
   exactly what the risk-based decomposition exists to avoid.

Swapping the poll to a targeted lookup is a legitimate follow-up once SP1 has landed and the cost
measurement below exists. It is **not** in SP1.

### The additive parameter

`desktop_wait_for` gains `includeOffscreen`, mirroring the parameter `desktop_snapshot` already
exposes and carrying the same meaning (both filters off). **Default is today's behaviour**, so no
existing caller changes.

This is what closes the defect's headline symptom. `P4 + latch` alone explains why a wait on a culled
element fails; the parameter lets a caller who knows what they are doing actually perform it.

### Wire contract

Additive only. Existing fields keep their names, types and meanings.

| Field | When |
|---|---|
| `unsatisfiedBecause` | **closed set**, non-null only on an unsatisfied result whose cause was determined |
| `elementBounds` | non-null with `unsatisfiedBecause` |
| `windowBounds` | non-null with `unsatisfiedBecause` |

`unsatisfiedBecause` is a **closed set of tokens, not free text** — an undefined string cannot be
asserted on and would be an un-pinnable contract. SP1 defines exactly one token,
`outsideWindowBounds`. Adding a token later is additive, and SP0's tripwire is what will catch a
token that stops being emitted.

**The three fields are emitted as `null`, NOT omitted.** This corrects an earlier draft that demanded
structural absence. `desktop_wait_for` is projected through an **anonymous** object
(`SnapshotTools.cs:70`), so `[JsonIgnore]` on `WaitForResult` would never be consulted — the record is
not what gets serialized. And `ToolResponse.cs:12` builds its `JsonSerializerOptions` with no
`DefaultIgnoreCondition`, so **this tool already emits `"ref":null,"snapshotId":null` on every
timeout today.** Emitting null for the new fields matches the shape callers already receive; demanding
absence would mean conditionally constructing two different anonymous types for no gain, and would
make the new fields behave unlike the two nullable fields already beside them.

`elementBounds` and `windowBounds` are **`int[]` in `{X, Y, Width, Height}` order** — the shape
`FindMatch.Bounds` declares (`FindQuery.cs:28`) and `desktop_find` builds at
`PerceptionManager.cs:534` (`new[] { b.X, b.Y, b.Width, b.Height }`), passed through unchanged at
`FindTools.cs:42`. Same convention as `DesktopEventPayload.cs:17` and `FindTextTools.cs:70`.

Note the repo has a **second, different** rect shape — the `{x,y,w,h}` *object* at
`ScreenshotTools.cs:47` and `:62`. That one is for capture geometry, not element bounds. Use the
array. Do not introduce a third.

**Both rects come from the model already in hand — no extra UIA call, no signature change.**
`SnapshotEngine.cs:74` always includes the depth-0 root node, and `SnapshotModel.Nodes`
(`SnapshotNode.cs:34`) exposes it, so `windowBounds` is the **depth-0 node's rect** and
`elementBounds` is the matched node's. Wait polls use `PollOptions`, which sets no `RootRef`, so
depth-0 is the window itself rather than a scope root. Do **not** re-read `BoundingRectangle` off the
window to get this — that would be a redundant cross-thread COM call for a value already walked.

`satisfied`, `ref`, `elapsedMs`, `snapshotId` are untouched — SP0 pins them.

**These fields must be added to the anonymous projection at `SnapshotTools.cs:70`, not only to the
record.** That projection dropping fields is the exact failure mode SP0 exists to police; a field
added to `WaitForResult` alone would never reach the wire, because the anonymous object — not the
record — is what `ToolResponse.Ok` serializes.

### Error handling for the added walks

The confirmation and diagnostic walks are **best-effort and must degrade, never escalate**. They run
on a window that may have been closed, denied, or torn down mid-wait, and `BuildModelAsync` throws in
those cases (`PerceptionManager.cs:420-423` among others).

- A confirmation walk that throws → treat as "could not confirm" and **return the culled walk's
  answer**, i.e. today's behaviour. A `gone` that was about to satisfy still satisfies.
- A diagnostic walk that throws → return the bare `satisfied: false`, omitting the three new fields.
- Neither may convert a completed wait into a thrown `ToolException`. A wait that reached its answer
  must return it.

### The popup fix

Both paths route through `PopupFinder.SearchRoots(win, desktop)` instead of bare `win`. Two hazards,
both recorded in the backlog entry:

- **Root-order concatenation before the `max` cap** in `FindAsync`, or truncation silently prefers
  window matches over popup ones.
- **`RuntimeId` dedup across roots.** `SnapshotEngine` prunes a window-child popup from the main walk
  so it appears exactly once (`PopupGraftingTests.cs:34-37` pins that); these paths have no such
  pruning, so the naive fix returns a window-child popup element twice.

  **The dedup key is `int[]`, which makes the obvious implementation silently wrong.** A
  `HashSet<int[]>` with the default comparer tests **reference** equality, so it would never dedup and
  the bug would look like the fix simply not working. Compare element-wise. `SnapshotEngine.RidEqual`
  (`SnapshotEngine.cs:184`) already does exactly this but is **`private`** — the plan must either
  promote it to `internal` and reuse it, or supply an `IEqualityComparer<int[]>`. It may **not** leave
  the choice to the implementer, and it may not duplicate the logic.

- **Per-root exception isolation is mandatory.** Both paths today wrap their single-root search in one
  broad catch-to-empty (`PerceptionManager.cs:489-493`, and the `try` around
  `EvaluateSelectorValueAsync`'s `by` switch at `:563`). Putting a multi-root loop *inside* that
  existing block means one transient `ElementNotAvailableException` — a tooltip closing mid-search —
  discards every valid match from the main window and every other popup, turning a transient popup
  fault into a total lookup failure. **Wrap each root's search in its own try/catch and continue to
  the next root on failure.** A root that throws contributes nothing; it must not zero the others.

**Measure before writing the test.** Whether a WPF window-child menu reproduces the blindness at all
is an empirical question (`PopupFinder.cs:21-25`); the TestApp `MenuTarget` fixture probably does
**not**, because a window-child popup *is* a descendant of `win`. Determine which popup flavours land
at the desktop level on this OS build first. The dedup hazard is testable immediately either way
against the existing fixture.

---

## Behaviour changes to disclose

1. **`gone` changes for exactly one class of element: `IsOffscreen == false` AND rect outside the
   window.** That is the defect's real shape and what `SpatialOffscreenButton` reproduces. Such an
   element returns `gone` instantly today and blocks under SP1.

   **It does NOT change for an element reporting `IsOffscreen == true`.** The confirmation walk keeps
   the `IsOffscreen` filter at `SnapshotEngine.cs:65`, so a hidden element is dropped by that walk
   too, reports absent, and still satisfies `gone` immediately — as today, and deliberately: every
   predicate treats `IsOffscreen` as not-present, and SP1 does not touch that. A toast that a
   well-behaved framework marks `IsOffscreen=true` while animating away is therefore **unaffected**.
   A caller wanting that element to keep the wait alive must pass `includeOffscreen: true`.

   State this plainly in the tool description. The distinction is invisible from the outside and a
   caller cannot be expected to infer which of the two filters their element trips.
2. **`exists` and `enabled` are unchanged by default.** Only the failure *message* changes.

## Testing

| What | Tier | Category |
|---|---|---|
| SP0 shape tripwires | headless | plain `[Fact]`, default CI scope |
| `gone` no longer satisfies on a culled element | live | `Desktop` — uses `SpatialOffscreenButton` |
| The latch does not re-run the double walk | live | `Desktop` — see **latch observability** below |
| `exists` timeout returns reason + both rects | live | `Desktop` |
| `includeOffscreen: true` satisfies on a culled element | live | `Desktop` |
| `enabled` timing out on a disabled **in-window** element does NOT emit `outsideWindowBounds` | live | `Desktop` — the false-blame case |
| `includeOffscreen: true` + `gone` runs no confirmation walk | live | `Desktop` — walk counter, as below |
| `desktop_snapshot` output is unchanged by the flag split | live | `Desktop` — `OffscreenCullTests` must stay green untouched |
| Popup dedup across roots | live | `Desktop` — existing `MenuTarget` fixture |

### Latch observability

The latch test cannot assert on wall-clock — the suite already learned that lesson in A1a, where a
timing-shaped assertion masked a real teardown defect for two rounds. It also cannot assert on a walk
counter that does not exist today.

`WaitCoordinator` must therefore expose the walk count for a completed wait. Preferred: an internal
counter on the coordinator, incremented per `BuildModelAsync` call and readable by the test through
`InternalsVisibleTo`, which Core already grants the test project
(`src/FlaUI.Mcp.Core/Properties/AssemblyInfo.cs:2`, verified). The plan must pick the mechanism explicitly
and **must not** settle for a timing proxy.

Expected shape: for a wait against a permanently culled element, walks grow by **one per poll after
the first satisfy-attempt**, never two.

`WaitForCullDefectTests` (currently `Assert.Fail` by design, traits `Desktop` + `KnownDefect`) is
**retired** in SP1: complete the assertion, run `--filter Category=KnownDefect`, and on green strip
the trait and delete `docs/fix-the-tool-backlog/wait-for-cull-disagrees-with-find.md`.

## Open question for the plan, not for this spec

The unculled walk's cost has **not been measured**, and after the latch flips the cost is
*concentrated*, not bounded: every remaining poll of that call does a full unculled walk. A 10s
timeout at a 500ms interval is ~19 of them, each rebuilding the whole out-of-window subtree the
spatial cull exists to prune.

**The plan must measure this before SP1's live tests are called green**, on a fixture with a large
out-of-window subtree, and record the number. Measure walk count and allocation, not wall-clock — a
timing-shaped assertion is what masked a real teardown defect for two rounds in A1a.

If it proves severe, the tuning option is to stop re-walking once the latch has flipped: hold the
resolved element and poll **its** liveness instead, since after the latch the question has narrowed to
"is this specific element still there?". **Do not adopt this blindly — it changes semantics.** A
walk re-matches the *selector* every poll, so an app that destroys and recreates the element under a
new `RuntimeId` currently keeps the wait alive; a held reference would report the original element
gone and satisfy. That may be correct or wrong depending on what `gone` should mean for a recreated
element, and that question is not settled here. Measure first, then decide.

## Provenance

**Design: four rounds of AGY-FIRST consult** (seams `.clavity/seams/wait-for-contract-fork.md` and
`-r3.md`). The peer's first two recommendations were built on a premise it had invented — that the
cull existed to compensate for an unreliable `IsOffscreen` — and it reversed twice before the design
converged. `P4` was synthesised from its own strongest objection ("you cannot have cheap polling *and*
rich diagnostics"), which holds only if the expensive path runs every poll. **The latch is the peer's
catch and it is correct.**

**Artifact: AGY-AFTER panel, GREEN at round 4.** Eighteen findings across a solo panel plus four
escalation rounds; twelve confirmed and folded, six refuted by measurement. Round 4 seated a bespoke
Refutation Auditor to re-check the refutations against the cited lines; all held, and the three
substantive seats returned no new findings.

The two highest-value findings are worth remembering. One was an error in this document's own
disclosure section — it claimed a toast animating off-window would now block `gone`, which is false
because the confirmation walk keeps the `IsOffscreen` filter. The other replaced the diagnostic rule
outright: a two-walk presence inference cannot tell culling from lateness, so the spec now measures
`!windowBounds.IntersectsWith(elementBounds)` directly.

Every factual claim here was verified against the code, not accepted on assertion — six peer findings
were confidently stated and wrong, including one that was in the do-not-re-raise ledger because the
peer itself had refuted it two rounds earlier.

**One correction landed AFTER the GREEN verdict, and it reverses a refutation.** While gathering line
citations for the plan, `SnapshotTools.cs:70` turned out to project `desktop_wait_for` through an
**anonymous** object, so the `[JsonIgnore]`-on-a-typed-record idiom I used to refute the peer's
Protocol Pedant finding does not apply to this tool at all. The peer was closer to right than I was;
my refutation reasoned about the repo's general convention instead of this call site, and the
round-4 Refutation Auditor re-checked my reasoning rather than the call site, so it passed too. The
wire rule is now emit-null, matching what the tool already does for `ref` and `snapshotId`. **A panel
GREEN means no reachable defect was found by reading — it is not a substitute for opening the exact
call site you are about to change.**
