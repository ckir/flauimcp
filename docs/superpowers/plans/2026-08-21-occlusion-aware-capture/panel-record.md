> **Part of the [Occlusion-Aware Window Capture implementation plan](../2026-08-21-occlusion-aware-capture.md).** Read that index first — it carries the REQUIRED-SUB-SKILL directive, the five things that would be undone by re-deriving from the panel record, and the repo rules that fail the build if ignored.

## AGY-AFTER panel over this plan — round 1

Brief `.clavity/seams/item8-plan-panel.md`; report `.clavity/scratch/item8-plan-panel/agy-round1.md`.
Seats: Type-Flow Auditor, Literal Implementer, Mechanism Gamer. **Verdict: RED.** Five findings, of which
**three were folded and two were REFUTED BY MEASUREMENT.**

**Folded:**

1. **The bookend walk compared ABSOLUTE mask rectangles, so a pure window MOVE tripped it.** Mask rects
   are absolute screen coordinates; dragging the window between the two walks shifts every one of them, so
   a harmless drag read as a total relayout — retried, then REFUSED. The design states in three separate
   places that a pure move is harmless, so the comparison contradicted it directly. Now normalised against
   each walk's own window origin, with `A_pure_window_move_between_the_two_walks_does_not_trip_the_bookend`
   pinning it. **This was the first defect found in §2.5, which is the least-reviewed idea in the design.**
2. **The metadata sweep's regex matched COMMENTED-OUT code**, so the tripwire was defeated by typing two
   slashes — *the exact defect item 12 shipped, reappearing inside the very test written to prevent it.*
   MEASURED: `\bcaptureMethod\s*=` matches `// captureMethod = result.CaptureMethod,`. Comments are now
   stripped before matching. **The severity here is not the regex; it is that an anti-gaming guard was
   authored, reviewed, and shipped in a plan while being trivially gameable.**
3. **The sweep reads SOURCE, never the runtime shape.** A serialization-level defect would pass both
   halves. Task 24 gains a runtime assertion over the JSON the tool actually emits — placed there because
   it is the only task with a live desktop.

**Refuted by measurement, do NOT re-raise:**

4. **"Task 21 provides no logic mutants and stops at Step 6."** FALSE. Task 21 has **Step 7, "Prove the
   gates are non-vacuous with two logic mutants"**, followed by Step 8's commit. Verified by reading the
   task.
5. **"The plan invents a DEF-2 defect it did not verify: `ScreenshotTools.cs:49` already passes
   `desk.Rects`."** The line does pass `desk.Rects` — and so does the plan. The `// DEF-2: this passed
   Array.Empty<Rectangle>()` comment is **pre-existing source at `ScreenshotTools.cs:42-45`**, written in
   the PAST TENSE, recording a defect that was fixed. The plan reproduces it verbatim because preserving
   an existing comment is correct. The peer read a historical note as a present-tense claim.

**Already fixed before the report arrived:** the Literal Implementer's finding that Task 20 references
`CaptureAuditSignal` before Task 21 creates it. The driver's own solo pass caught it in `c5bc0b2`;
independent agreement is worth recording even though there was nothing left to fold.

⚠ **The round is RED and the panel is NOT closed.** Three folds spawn their own edges — the corrected
bookend comparison in particular is new code that has been reviewed by nobody. A further round should be
run against the folded plan before Task 1 begins.

## AGY-AFTER panel over this plan — round 2

Brief `.clavity/seams/item8-plan-panel-r2.md`; report `.clavity/scratch/item8-plan-panel/agy-round2.md`.
Seats rotated onto uncovered ground: Fold Auditor (round 1's edits only), Cascade Analyst, Resource
Vampire. **Verdict: RED.** Plus a driver solo pass that ran before the peer's report returned.

**Folded — from the peer:**

1. **The abandoned acquisition thread leaked an undisposed managed `Bitmap`.** If a hung target eventually
   processes `WM_PRINT`, the abandoned thread finishes `Render()` and allocates a `Bitmap` wrapping GDI+
   resources for a caller that returned long ago. Those accumulate against the process's 10,000-handle GDI
   ceiling, reclaimed only when the GC gets round to finalizing them. **The thread now disposes its own
   output when the caller has given up.** This does not make the accepted leak go away — what is held
   inside a still-blocked call is unreachable either way — it removes the one resource that becomes
   reclaimable afterwards and was being dropped anyway.
2. **Task 20's step ordering was broken for a literal implementer.** Step 3 expected a test to be RED that
   Step 6 had not yet written, while Step 6's own header claimed it was "already written in Step 2". Both
   are now correct: Step 2 writes two literal tests, Step 6 adds the real tripwire, and Step 3 says so.

**Folded — from the driver's solo pass, before the peer's report arrived:**

3. **The bookend walk was unguarded** — a throwing confirmation discarded a good image already in hand and
   introduced a terminal refusal on element scope. *(The peer's Cascade Analyst found this independently.)*
4. **The circuit breaker's dictionary was unbounded**, and because the OS recycles `HWND` values a stale
   entry could mis-trip an unrelated window. *(The peer flagged the growth as a secondary finding.)*
5. **The circuit breaker smuggled a dead target past the target-state guards.** Short-circuiting to the
   scrape skipped canonical steps 4-5, so a hung window that tripped the breaker and then MINIMIZED was
   scraped at the rectangle it used to occupy — returning a photograph of whatever was now behind it, as a
   success. **That is the exact defect item 8 exists to remove, reintroduced by the containment added for a
   different problem.** Steps 4-5 are now `ScreenCapture.GuardTargetState`, called by both paths.

**REFUTED BY MEASUREMENT — do NOT re-raise:**

6. **"A pure move leaves masks spatially offset inside the image, so `MaskSetsMatch` approving a move
   returns a leaking image."** FALSE, and traced on the spec's own numbers: window `(-8,-8,1936,1036)` →
   `(92,92,…)`, element `(100,200,300,50)`, mask `(110,210,50,20)`. The crop yields `absolute =
   (100,200,300,50)` and the mask paints at `(10,10)` — exactly its true offset inside the element. The
   claim assumes `c.Absolute` is `W2`-anchored; it is **`W1`-anchored**, which is §1's rule 1 and what
   panel round 7 verified by hand. Masks and the rectangle that translates them come from the same
   observation, which is the entire point of that rule.
7. **"Task 24's `OccludedCaptureTests.cs` must be invented from scratch — namespace, usings, class
   declaration and the `_app` fixture must all be guessed."** FALSE. Task 24 gives the complete file:
   `namespace FlaUI.Mcp.Tests.Capture`, `public class OccludedCaptureTests : IClassFixture<TestAppFixture>`,
   the `_app` field and its constructor. The instruction the finding quotes does not appear in the plan.
8. **"The plan asserts `PerceptionPolicy.cs:48` reads `string.IsNullOrWhiteSpace(processName) || …`."**
   FALSE. The plan mentions `PerceptionPolicy` **zero times**.
9. **"`Autosound` being declared and parsed in `ServerOptions.cs` is unverified."** It is verified:
   `ServerOptions.cs:11` declares `bool Autosound = false` and `:18` parses `args.Contains("--autosound")`.
   The plan also instructs the engineer to read that file rather than copy an idiom.

⚠ **Round 2 is RED and the panel is NOT closed.** Five folds, and three of them are new code — the
extracted `GuardTargetState`, the guarded bookend, and the thread handoff. In this project a fix has
carried its own defect in a large fraction of rounds.

⚠ **The pattern across both rounds is worth stating for whoever runs round 3: every defect so far has been
in a GUARD, not in the happy path.** The bookend contradicting the move rule, the anti-gaming sweep being
gameable, the breaker smuggling a dead target past the guards, the containment leaking the thing it was
containing. The common case has been correct throughout; the machinery added to protect it has not.

## AGY-AFTER panel over this plan — round 3

Brief `.clavity/seams/item8-plan-panel-r3.md`; report `.clavity/scratch/item8-plan-panel/agy-round3.md`.
Seats: Fold Auditor (round 2's edits only), State Corruptor, Axiom Breaker. **Verdict: RED.**

**Folded:**

1. **The circuit breaker bounded only SERIALIZED captures.** `Trip()` runs *after* a timeout elapses, so
   three overlapping requests for the same hung window all read `IsTripped` as false, all call `Acquire`,
   all block, and all leak — three threads and three bitmaps for one window, defeating the containment the
   class exists to provide. Added an IN-FLIGHT register and `AnotherAcquisitionIsStuck(hwnd, budget)`.
   ⚠ It deliberately does **not** divert merely because another acquisition is in flight: two agents
   capturing the same HEALTHY window concurrently is ordinary and those calls finish in milliseconds, so
   diverting them would reintroduce occlusion for a window that was working fine. Only an acquisition that
   has already outlived the whole timeout budget counts as evidence of a hang. *(State Corruptor.)*
2. **A degenerate `W2` threw while a degenerate `W1` was a retryable transient — the same physical
   condition, terminal or recoverable purely according to which of two reads milliseconds apart caught
   it.** That defeated the retry loop for exactly the animating window it was built for. `CaptureOutcome`
   gains a fourth case, `TargetTransient`; `GuardTargetState` now raises only the genuinely terminal
   conditions (destroyed, minimized) and each caller routes degeneracy into the loop. *(Axiom Breaker —
   the seat's first outing, and it found a contradiction three rounds had walked past.)*
3. **The acquisition-thread handoff race.** *Already folded at `3433cf0` by the driver's own solo pass
   before this report arrived; the peer's Fold Auditor found it independently.* Setting `abandoned` alone
   narrows the window without closing it — the thread can publish into `result` between `Join` expiring
   and the caller taking the lock. Both orderings are now covered.

**Also folded this round, from the driver's solo Axiom Breaker pass — and it is leak-class:**

4. **A bookend exhaustion was routed through `OnResizeExhausted`, which FALLS BACK TO THE SCRAPE for
   element scope.** That is correct for a RESIZE, where the failure is a geometry mismatch and the masks
   may be perfectly fine. It is wrong for a bookend mismatch, which is *direct evidence the mask set
   moved*: falling back would scrape the window and paint rects we have just PROVEN are stale, producing
   an under-redacted image. **A bookend exhaustion now refuses on BOTH scopes**, which is what the spec's
   §4 table said all along — the code contradicted it and the spec was right.

**REFUTED BY MEASUREMENT — do NOT re-raise:**

5. *"The plan asserts `AutomationDispatcher.cs:61` is `_query.RunAsync(func)` with no timeout…"* The plan
   mentions `AutomationDispatcher` **zero times**. (The claim is also TRUE of the real file, which makes
   the pre-existing comment it came from accurate.) **This is the THIRD round running in which a direct
   answer attributed to the plan an assertion the plan does not make** — grep every quoted claim.

⚠ **Round 3 is RED. Four folds, three of them new code:** the in-flight register, the `TargetTransient`
case, and the bookend's own terminal outcome.

⚠ **The guards-only pattern held for a third round** — every one of the four is in protective machinery.
But note the shift: rounds 1-2 found guards that were WRONG, round 3 found guards that were INCONSISTENT
WITH EACH OTHER. `W1` versus `W2` degeneracy, and bookend-exhaustion versus resize-exhaustion, are both
"two guards for the same class of condition that disagree". That is a different lens and it is not
exhausted — round 4 should enumerate every PAIR of guards and ask whether they agree.

## AGY-AFTER panel over this plan — round 4

Brief `.clavity/seams/item8-plan-panel-r4.md`; report `.clavity/scratch/item8-plan-panel/agy-round4.md`.
Seats: Guard-Consistency Auditor (bespoke), Protocol Pedant, Blindspot Auditor. **Verdict: RED.**

**Folded — two are leak-class:**

1. **⚠ LEAK. Element scope with a NON-EMPTY mask set fell back to the scrape on resize exhaustion.** A
   resize reflows the WINDOW's layout regardless of which scope asked, so painting pre-resize mask rects
   onto a post-resize scrape under-redacts exactly as it would for window scope — while window scope
   refuses for precisely that reason. The fallback was defended as "restoring current behaviour", which is
   the pre-existing-defect defence this project does not accept. **Element scope with masks now refuses
   too.** ⚠ The regression argument for the fallback survives intact because it was always about a
   different population: spinners, progress dialogs and expanding windows carry nothing to mask, so they
   still fall back. *(Guard-Consistency Auditor.)*
2. **⚠ LEAK. The circuit-breaker path bypassed the resize guard.** Skipping the seam also skips
   `if (w1.Size != w2.Size)`, so a hung window that RECOVERED and resized during the cooldown was scraped
   with stale `W1` masks and never checked. The breaker path now runs the resize check as well as
   `GuardTargetState`. *(Guard-Consistency Auditor.)*
3. **A window-scope FALLBACK SCRAPE that came back one colour emitted nothing** — recreating the exact
   defect the `uniformCanvas` widening was folded in to close. The TWO-STAGE detector genuinely cannot run
   on a scrape, but the FIRST stage needs no second operand. A window-scope fallback now emits
   `uniformCanvas`. ⚠ An ELEMENT-scope fallback still emits nothing, **deliberately**: there the captured
   region is the element, so the code's text would claim the whole window rendered as one colour — a
   statement the tool never measured. That gap is stated rather than papered over. *(Protocol Pedant. The
   spec has been corrected too — it said "a fallback scrape gets neither".)*
4. **`MaskSetsMatch` compared by INDEX.** Its justification was that the walk is deterministic so a
   reordering is itself evidence the tree changed. Wrong twice: UIA enumeration order across two walks is
   not a guarantee this repo owns, and **a reordering with identical geometry is not a reflow.** The
   question is whether the masks still cover the same regions, which does not depend on walk order. Now
   order-insensitive, removing a false-refusal mode. *(Direct answer 1 — correctly identified as the most
   likely surviving defect.)*

**Also folded this round, from the driver's solo Guard-Consistency pass:**

5. **The empty-crop refusal was terminal on the FIRST occurrence, while a degenerate `W1` was retryable.**
   Both are "UIA reported a bad rectangle for a frame". The design's own text calls the empty crop
   "pathological rather than impossible, this repo's mask-escalation machinery exists because **UIA does
   report inconsistent rectangles**" — which argues for absorbing it, not refusing it. Now a retryable
   transient carrying its own reason, so the terminal message is not the false "no renderable area".
   ⚠ **The peer had this in its BELOW-FLOOR list**, discarded as "rare and likely terminal". Reading the
   below-floor list first has now produced a real finding in this repository several times.

**Not folded:**

6. *"The audit signal floods the operator with false positives on visible background windows."* **Already
   argued in the plan**, in those words: the trigger over-signals deliberately, it never MISSES a covered
   window, computing true occlusion needs a hit-test this server does not do, **and that is exactly why it
   is OFF BY DEFAULT.** The finding proposes no better trigger. Recorded rather than folded.
7. *"Task 17's three call sites are unverified."* Verified this session by `grep -rn`.

⚠ **Round 4 is RED. Five folds, and the two leak-class ones were BOTH "a guard that exists on one path
and not on the adjacent one".** That is the same lens as round 3 and it is still producing.

## AGY-AFTER panel over this plan — rounds 5 and 6

Briefs `.clavity/seams/item8-plan-panel-r{5,6}.md`; reports `.clavity/scratch/item8-plan-panel/agy-round{5,6}.md`.

### Round 5 — **the first clean seats**

Seats: Fold Auditor, Guard-Consistency (2nd), Resource Vampire (2nd). **Verdict: RED**, but **two of three
seats returned "no new findings"** — the first clean result of the review. The Fold Auditor traced the
order-insensitive `MaskSetsMatch` on duplicates, a gained mask and a swap and confirmed it; the Resource
Vampire audited every new allocation and exit and found nothing.

- **⚠ LEAK, folded: fallback scrapes had no denylist guard.** A fallback takes raw desktop pixels of the
  target's rect while the mask set was walked for the target ONLY, so a credential window overlapping the
  target was photographed unmasked. `ScreenshotTools.cs:38` refuses a full-desktop capture outright for
  exactly this, and that guard sits inside `if (string.IsNullOrEmpty(window))`. **Sixth finding of one
  shape.** ⚠ Stated in the fold: the exposure is PRE-EXISTING and item 8 NARROWS it — after item 8 the
  primary path renders only the target, so overlapping windows are structurally absent.
- **Folded from the driver's solo pass:** the breaker emitted `scrapeFallbackTargetUnresponsive` — which
  tells the agent in the PRESENT TENSE not to click the target — on the strength of a timeout up to five
  minutes old. It now asks `IsHungAppWindow` and resets on recovery.
- Also folded from the solo pass: **the OCR path had no degenerate-window guard**, so a window degenerate
  at walk time and valid at capture time was OCR'd with an EMPTY mask set — returning redacted text as
  plaintext. The peer's Seat 2 found this independently.

### Round 6 — **four findings, and the headline one made round 5's fix inert**

Seats: Fold Auditor, Guard-Consistency (3rd), Literal Implementer (2nd). **Verdict: RED.** Seat 1 clean.

1. **⚠⚠ `denylistedVisible` WAS NEVER WIRED IN DI, AND THE GUARD FAILED OPEN.** Round 5 added the
   parameter; Phase 6's DI construction did not pass it; the parameter is optional; the guard read
   `if (_denylistedVisible is not null && …)`. **So the entire round-5 security fix did nothing in
   production while every test still passed.** Now: the seam **fails closed** (an unwired guard throws
   rather than skipping), and DI passes it. *A guard whose absence is indistinguishable from a pass is
   worse than no guard, because it reads as protection.*
2. **⚠ LEAK: a fallback scrape used the TARGET's mask set.** The image contains every window overlapping
   the target, none of which the target's walk knew about. It now uses the **desktop** mask set — which
   also makes `unmaskedProcesses` honest, where the window path had hardcoded it empty on the comment
   "empty on that branch is therefore the truth". True of a PrintWindow capture; false of a scrape.
3. **`SafeWindowRect` returned the ELEMENT's rect when the window rect read failed**, and the resize guard
   compares that against a `GetWindowRect` of the WINDOW — guaranteeing a mismatch, burning the whole
   retry budget, and refusing every element-scope capture whose window read happened to fail. It now
   returns `default`, which the degeneracy check turns into a retryable transient. **Signalling failure
   beats substituting a plausible wrong value.**
4. **`Reset` was defined and never called** while the prose claimed "a recovered window RESETS the breaker
   and takes the normal path". Now called from `HungOrReset`.

⚠ **Rounds 5-6 produced three more leak-class findings, all the same shape.** The count is now **nine**
findings of "a guard that exists on one path and stops at the adjacent one" — and round 6 added a new
sibling: *a guard that exists but was never connected.* Both are invisible to tests that only exercise the
path the guard is on.

### Round 7 — **the first round with no leak**

Seats: Fold Auditor, Guard-Consistency (4th), Protocol Pedant (2nd). **Verdict: RED**, three findings —
and **none of them leak-class**, which is the first time in seven rounds.

1. **A COMPILE ERROR, in the driver's own round-7 fold.** That fold added `Escalations` to
   `WindowCaptureOutcome`, making it a four-argument record, and updated Task 18's two copies of the
   `Completed` arm while missing **Task 17's**. `phase-5-coordinator.md:395` passed two arguments. Caught
   with an exact file:line quote.
2. **A shadowed guard.** `OnResizeExhaustedAsync` had two consecutive refusals throwing the same code with
   the same message — one qualified by `scope == CaptureScope.Window`, one not. The scope condition was
   **dead**: the second caught everything the first did. Collapsed to one, with the reachability argument
   written down: window-scope-with-empty-masks never arrives here (it warns and continues to the crop), so
   the only callers are window-with-masks, element-with-masks, and element-without.
3. **The tool description had gone FALSE in two places**, and it is read by every agent on every call.
   It said `'screenScrape'` means full-desktop — but window and element fallbacks emit it too. And it said
   resize exhaustion "falls back to a scrape (element scope)", which **round 4 made false** when
   element-scope-with-masks began refusing. Both rewritten; the first now tells a caller to *check*
   `captureMethod` rather than infer it from the scope requested.

**Direct answer: not ready, because of the compile error.** Nothing else was named.

⚠ **The shift is worth recording: rounds 1-6 produced eleven leak-class findings; round 7 produced
none.** What it found instead was a mechanical slip, a redundancy and stale prose — the residue of six
rounds of edits rather than defects in the design. That is what convergence looks like here, and it is the
first evidence of it beyond a seat going quiet.

### Round 8 — Seat 1 clean; the review turns on itself

Seats: Fold Auditor, **Completeness Sweep**, **Adversary of the Reviewer** (the last two never used before).
**Verdict: RED.**

**Seat 1 confirmed round 7's reachability claim** by tracing it: `CaptureWindow` returns `Resized` only
for element scope or window-scope-with-masks, so window-scope-with-empty-masks returns `Completed` and
continues to the crop — it never reaches `OnResizeExhaustedAsync`. The collapsed guard preserves exact
behaviour. It also confirmed the rewritten tool description matches every path.

**Folded:**

1. **The spec's §4 row scoped the resize refusal to WINDOW scope** while the plan had refused ELEMENT
   scope too since round 4. **A table asserting completeness that scopes a row more narrowly than the code
   is the same defect as omitting a row** — and this is the same table that went stale three times.
2. **The index omitted `FlaUI.Mcp.Core.csproj`** (Task 9 adds `InternalsVisibleTo`), and its `ROADMAP.md`
   cell implied a task adds item 18. ⚠ **Item 18 is not "lost to the void" as the report's direct answer
   claimed — it is committed at `ROADMAP.md:651`.** The index cell was misleading; the item is safe.
3. **⚠⚠ `_desktopMasks` was FAIL-OPEN — the identical shape that made the denylist guard inert.** An
   optional constructor parameter whose absence silently reverts to the TARGET's mask set, which is
   exactly the leak the desktop walk closed. **Once was a mistake; twice would be a pattern.** Now throws.
   *(Found by the driver while checking the premise of Seat 3's argument below — the argument only holds
   if this is wired, and nothing guaranteed that.)*

**⚠ NOT FOLDED — ESCALATED TO THE OPERATOR. Seat 3 argues one of the review's own folds is now obsolete,
and it is RIGHT on the mechanics.**

> Round 4 refused element-scope-with-masks on resize exhaustion because masks measured at `T1` could
> misalign with pixels scraped at `T2`. **Round 6 then changed `ScrapeAsync` to discard the target's masks
> and perform a FRESH desktop walk at `T2`** — so the fallback now acquires its image and its mask set
> together. The `T1`/`T2` skew the round-4 refusal existed to prevent **no longer exists on that path**,
> and the refusal now needlessly kills a fallback the design originally justified as preventing a total
> outage on animating windows.

The mechanics check out. It is escalated rather than folded because **it challenges ratification item 2**,
which the operator settled explicitly: *window scope retries then refuses on exhaustion, and never falls
back, because "a scrape reproduces a mask failure rather than fixing it".* Round 6 made that premise false.
Only the operator may reverse it, and the same argument would apply to window scope as to element scope.

**Refuted:** the direct answer's claim that item 18 would be "lost to the void" (committed at
`ROADMAP.md:651`), and that `PerceptionManager.cs:1199`'s rethrow was unverified (measured this session).
