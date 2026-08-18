# SP4 - redaction completeness (design)

Status: DESIGN. Approved section-by-section 2026-08-18; adversarial panel rounds 1-4 folded (section 12); PANEL CLOSED.
Branch point: `a76092d` (SP3 merged).

## 0. The settled design, with no history

Six anomalies captured during SP3 and verified by measurement at triage fold into SP4, which lands
**before v1.0.0**. The increment closes two content leaks, fixes one mislabelled wire field, renames one
misleading wire field, removes one coupling, and dispositions two non-code items.

**The organizing principle, which decides every scope question below:** *do everything now that becomes a
BREAKING change after v1.0.0; defer everything that stays ADDITIVE.* A field rename and a field whose
meaning changes are breaking. A new optional rule field is additive and can wait.

## 1. Goal

No content reaches the wire without passing the redaction decision, and no wire field claims to be
something it is not - before the v1.0.0 contract freezes both.

## 2. Scope

### In

| Id | Item | Kind |
|---|---|---|
| A1 | pixel-mask rect collection fails open when `BoundingRectangle` is unreadable | leak |
| A5 | `window.title` carries the focused ELEMENT's unclassified Name | leak + mislabel |
| A2 | `redacted` / `redactedCount` are two same-rooted names with different meanings | wire footgun |
| A6 | the redaction token is a bare literal at three egress sites | coupling |
| A3 | the DEF-3 selector path has no specific pin | coverage debt (disposition only) |
| A4 | DEF-3 was reachable in the DEFAULT shipping config | release note (disposition only) |

### Out, stated so a review does not re-raise it

- **`titlePattern` (a rule field targeting window titles).** Additive, therefore not deadline-bound.
  Window titles are *already* published unclassified by `desktop_list_windows` and always have been; this
  increment does not change that, and section 4 makes the two surfaces consistent so the question can be
  asked once, coherently, later.
- **Tracked item H1** (process homogeneity is assumed, not proven). Unchanged by this increment.
- **A general sweep of every remaining wire field for classification.** Unbounded, and overlaps H1.
- **The `ProcessIdentity` strong-type refactor.** Logged as follow-up during SP3; additive, not
  deadline-bound.

## 3. A1 - the pixel path

### The rule

> A redact-worthy element must contribute a usable mask rect. If it cannot, escalate to its parent. If
> escalation would reach the window root, REFUSE the capture.

### Why one rule and not a taxonomy

The tempting design branches on *why* a rect is missing (threw / element vanished / zero-size).
Deliberately rejected: distinguishing them multiplies exception-type handling on the one path whose whole
job is to withhold, and one auditable rule is worth more here than a precise one.

**The cost of that choice, stated rather than implied (panel round 1, Cascade Analyst):** an element that
vanished mid-capture is very likely no longer painted, so escalating masks a parent region that holds no
secret. On an animating or transitioning UI this produces occasional black boxes over benign content.
That is accepted deliberately: a teardown-race element may still be painted, and withholding is the point
of the feature. **It is a real usability cost on a common transition, not a free choice.**

### Behaviour, all cases

| Case | Behaviour |
|---|---|
| rect reads, non-empty | masked precisely (unchanged from today) |
| rect read THROWS | escalate to parent; count the element in `maskEscalations` |
| element vanished mid-capture | identical to throws - escalate. No special case. See the cost note above. |
| rect reads as `0x0` / empty | identical to throws - escalate. A provider unable to report bounds may return zeros rather than throw. |
| parent rect also unusable | escalate again, up the ancestor chain |
| **obtaining the PARENT itself throws** | treat as "no usable ancestor remains" - REFUSE, exactly as reaching the root does. A dying subtree must not silently drop out of the mask set. |
| escalation reaches the window root | **REFUSE**: `ToolException(RedactionUnmaskable)`. No image is returned. |
| **the walk hits its depth cap without reaching a usable ancestor** | REFUSE, identically. Guards against a provider with a structural cycle hanging the single query STA. |
| element is not redact-worthy | unchanged; no rect collected, no escalation |
| no redact-worthy elements at all | unchanged; `maskEscalations` = 0 |

### Escalation depth is NOT uniform, and the refusal path is not rare

The rect walk enumerates `rootEl.FindAllDescendants()`, so an element's parent may be the window root
itself. Consequence, which the implementer must not be surprised by:

- **Deep trees (WPF, Electron, modern XAML):** a redact-worthy element usually sits many levels down, so a
  single unreadable rect degrades gracefully - a local container is masked and a usable screenshot is
  still returned.
- **Flat trees (classic Win32 dialogs), where controls are direct children of the window:** the parent IS
  the root, so **one** unreadable rect refuses the whole capture on the FIRST escalation step.

A1 is therefore graceful degradation on modern apps and a hard refusal on legacy ones. That disparity is
intended - refusing beats leaking - but it must be documented in the operator manual, because "screenshots
of this one old app always fail" is otherwise an unexplainable report.

### Why refusal, not a black image

Escalating to the window root and masking it returns a *successful* all-black screenshot. That is worse
than an error: an agent will hallucinate contents or loop on it, whereas a refusal forces a strategy
change.

### Contracts

- **`desktop_screenshot` metadata gains `maskEscalations`** (integer, ALWAYS present, `0` when none).
  Before: `{bounds,dpiScale,scaleApplied,redactions}`. After:
  `{bounds,dpiScale,scaleApplied,redactions,maskEscalations,escalated}`.
  **Semantics, stated exactly:** `maskEscalations` counts ELEMENTS whose own rect was unusable and whose
  mask was therefore taken from an ancestor - NOT the number of levels climbed. One element climbing three
  levels counts as `1`.
- **`escalated`** (array of objects, always present, empty when none). One entry per escalating element:
  `{ automationId, controlType }`.
  **Why a diagnostic at all:** a bare count is not one - an operator seeing a giant black box and
  `maskEscalations: 2` cannot tell which control has the broken provider.
  **Why these two fields, and why they are safe:** both are already treated as NON-content by this
  codebase. A redacted element deliberately stays findable by `automationId` AND by `controlType`
  (BC-1 targetability), and `FindMatch` already publishes both for redacted elements. Emitting them
  leaks nothing redaction withholds.
  ⚠ **`controlType` is present because `automationId` ALONE is a blindspot** (panel round 2, Blindspot
  Auditor - HIGH). Many elements legitimately have no `automationId` - this repo's own fixture depends on
  that fact - so an id-only diagnostic degrades to `["", "", ""]` on exactly the legacy and flat-tree UIs
  where A1's refusal path fires hardest. An element with no `automationId` contributes an empty string
  for that field and still reports its `controlType`.
  **The element's Name is NEVER emitted here** - that would leak the identity of the very thing the mask
  exists to hide.
- **NEW `ToolErrorCode.RedactionUnmaskable`.** Rejected alternative: reuse `CaptureUnavailable` (already
  used for a locked/disconnected desktop). Rejected because a script would then have to scrape text to
  distinguish "desktop unavailable" from "cannot mask safely" - the same output-contradicts-code defect
  SP3 fixed in `check-redaction-rules`. Adding an enum member is additive and free pre-1.0.
  The refusal message names the `automationId` and `controlType` of what could not be masked, never Names.

### Testability seam

The escalation DECISION is extracted into a pure function; the UIA-touching code shrinks to "read a rect,
hand it to the decision".

⚠ **Its ancestor input is a LAZY ACCESSOR, not a materialized list** (panel round 2, Axiom Breaker -
HIGH). An earlier draft said "the element's own rect-or-null plus its ancestor rects-or-null in order",
which is a real contradiction: supplying a complete ordered list forces the caller to pre-fetch EVERY
ancestor of EVERY redact-worthy element before the decision runs - a cross-process COM cost on a path
already dominated by property traffic - while the alternative (aborting inside the UIA code when a
parent fetch throws) moves the refusal decision OUT of the function and breaks the seam it exists to
create.

**Requirements on that input, NOT a signature** (panel round 3, Axiom Breaker - an earlier draft pinned a
concrete `Func<int, Rect?>` delegate here; that was over-specification, and worse, it would have FORBIDDEN
the memoized walker the cost requirement below now demands. The spec mandates the properties; the plan
picks the shape):

1. **Lazy.** Ancestor level `n+1` is requested only if level `n` was unusable. No pre-fetch.
2. **Failure is indistinguishable from absence.** Any failure to obtain an ancestor rect - including the
   PARENT FETCH itself throwing - presents to the decision as "no rect at this level". That is what makes
   the case table's parent-fetch refusal reachable without the decision knowing anything about UIA.
3. **Injectable.** Tests supply a fake ancestor source and never touch UIA; production supplies the
   adapter that walks the real tree.

### Cost and termination (panel round 3, Resource Vampire)

Two bounds are REQUIRED, not optional:

- **Memoize resolved ancestors per capture.** If a container's bounds read is broken, every one of its
  redact-worthy children escalates through the SAME chain. Without memoization the walk repeats identical
  cross-process parent lookups once per child and masks the identical parent rect N times, turning an O(N)
  pass into O(N x depth).
  ⚠ **The cache key must NOT be the element object itself** (panel round 4, State Corruptor, raised as its
  own weakest assumption). Keying a dictionary on `AutomationElement` invokes its `GetHashCode`, which
  fetches `RuntimeId` cross-process - so a cache added to REMOVE COM traffic would generate a burst of it,
  inverting the optimisation. Key on an already-materialised identity instead.
  ⚠ Memoization also has a correctness benefit beyond cost, and it is the stronger argument: without it,
  two siblings failing at different moments query the same ancestor's bounds at different times, so a
  scrolling container gets two MISALIGNED masks. A per-capture cache locks one coherent rect for the whole
  pass.
  ⚠ *Severity, honestly:* the peer named this itself as the weakest part of its own answer - the OS caches
  structural tree queries, so the real latency penalty may be small. Memoization is required because it is
  nearly free and the capture path is already ~90% property traffic, NOT because a storm is proven.
- **Cap the walk.** An uncapped ancestor climb on a provider with a structural cycle never terminates, and
  this runs on the single query STA - a hang there wedges every subsequent query, not just this capture.
  The walk therefore stops at a fixed maximum depth and treats exhaustion exactly as reaching the root:
  REFUSE. This bound is about termination, not tuning; it must not be removed for being "unreachable in
  practice".

WARNING - consequence for the ledger: A1 therefore does **not** join the accepted-boundary list, and
`docs/coverage-debt.md` entry **AB-3**'s compensation must be re-validated during this increment, per that
file's own rule that a changed compensation reopens the entry.

## 4. A5 - the focused-element / title split

### The defect, measured

`WindowManager.ResolveFocusedWindowAsync` (`:614`, `:623`) does
`var focused = _automation.FocusedElement();` then reads `focused.Properties.Name.ValueOrDefault`.
That is the focused **element's Name** - element content - surfaced on the wire as `window.title` by
`SnapshotTools` (`:116`), with no classifier touchpoint.

WARNING: the Task-12 sweep flagged this site and was silenced by an allowlist entry reading *"window
TITLE via Properties.Name, not element content"*. **That justification is false**: it IS element content.
This is the second false allowlist reason found; unlike the 26 pasted ones, this was reasoned and still
wrong.

### The fix

1. `window.title` becomes the **real window title** via Win32 `GetWindowText(hwnd)` - the same call
   `EnumTopLevel` already uses. Preferred over a UIA read for the reason this codebase documents
   elsewhere: a UIA property read on the query STA can block with no timeout; `GetWindowText` cannot.
2. **No new field for the element name.** `GetFocusedElementAsync` already returns `descriptor` (rendered
   through `SnapshotEngine.Render`, therefore classified) AND `ref` (`SnapshotTools.cs:116`), which is the
   machine-readable, directly actionable identifier. The raw Name was redundant as well as leaky.
3. **The sweep allowlist entry for `ResolveFocusedWindowAsync` is REWRITTEN, not deleted.**

   ⚠ An earlier draft said DELETED, "because after the fix the member performs no UIA content read". That
   was **self-contradictory the moment fix step 4 was added** (panel round 4, Literal Implementer -
   CRITICAL): the fallback below reads the window root's `Name`, which IS a UIA content read. Following
   section 8a literally would have turned the build red at the allowlist-deletion step, with no way to
   land the work incrementally.

   The entry stays, with a reason that is true and narrow:
   *"identity reader - reads the WINDOW ROOT's Name, which IS the window's title, and only as a fallback
   when the Win32 caption is empty. NOT the focused element's Name, which is what the false version of
   this reason described."*

   ⚠⚠ **This reason has the same SHAPE as the false one it replaces, and that similarity is a hazard, not
   a coincidence.** The removed text read *"window TITLE via Properties.Name, not element content"* and
   was false because the element being read was the FOCUSED ELEMENT. The new text is true because the
   element being read is the WINDOW ROOT. The distinction is the whole justification, so a future reviewer
   must check WHICH element the member reads before honouring this exemption - not merely that a
   plausible sentence is present. Two reasons in this file have already turned out false.
4. **Empty or failed `GetWindowText` falls back to the WINDOW ROOT element's `Name`** (panel round 3,
   Dependency Cynic - CRITICAL). Only if that is also empty does `title` become an empty string.

   ⚠ **That fallback MUST be exception-guarded** (panel round 4, Cascade Analyst - HIGH). Reading
   `Name` off the root is a cross-process COM call, and on an unresponsive or tearing-down window it
   throws. Unguarded, a harmless captionless window would crash `desktop_get_focused_element` outright -
   reintroducing precisely the unhandled-UIA failure mode that moving to `GetWindowText` was meant to
   escape. A throw here degrades to an empty title, exactly as an empty read does.

   *Why the fallback exists:* some frameworks draw their own title bar, leaving the Win32 caption empty
   while the visible title exists only in the UIA tree. Returning empty there would silently blind the
   agent to those windows' titles - trading one information loss for another.

   ⚠ *Why the root's Name is NOT a return to the defect this section fixes:* the bug is that we read the
   FOCUSED ELEMENT's Name and called it a title. The WINDOW ROOT's Name **is** the window's title - it is
   the correct source, merely a slower one. The fallback is ordered second precisely because a UIA read on
   the query STA can block on an unresponsive window while `GetWindowText` cannot.

   ⚠ *Measured, and it narrows the peer's claim:* the peer asserted UWP/WinUI HWND captions are
   "completely empty". On this machine `GetWindowTextW` returned real captions for `SystemSettings`,
   `ApplicationFrameHost` and `SecHealthUI`. So the Win32 path works for hosted UWP at least. Note also a
   SELECTION EFFECT that makes this hard to sample: `EnumTopLevel` skips captionless windows
   (`WindowManager.cs:394`), so `desktop_list_windows` can never show one - absence of empty titles there
   is not evidence they do not exist. The fallback is therefore a safety net for custom-chrome apps, not
   the primary path.

### Contract, and the fact that this break is SILENT

`desktop_get_focused_element` keeps its shape: `{ ref, descriptor, window:{ handle, title, pid } }`.
**`window.title`'s VALUE changes meaning** - from the focused element's Name to the owning window's title.

⚠ **Both panels flagged this as the spec's worst risk, and they are right about the mechanism:** unlike
A2's rename - which breaks an old consumer loudly and immediately - this change sails through
deserialization and silently returns different data. An agent waiting on `window.title == "Submit"` does
not fail; it waits forever.

**Decision (operator, 2026-08-18): keep the field name, make the break loud OUT-OF-BAND.** `title` is the
correct name for a window title; renaming it would surrender the right name permanently in order to
manufacture a compile-time break. Instead, the change is announced where consumers actually learn:

1. a **BREAKING** entry in `CHANGELOG.md` naming the old and new meanings explicitly;
2. `docs/agent-contract.md` updated at the `desktop_get_focused_element` row;
3. the tool's own `[Description]` string, which is what an agent reads at runtime.

Residual risk, accepted and recorded: a script hard-coded against the old value fails silently, once.

### Stated limitation

This replaces an unclassified element name with an unclassified window title. Not a new exposure
(`desktop_list_windows` already publishes titles unclassified) but not zero either: an application drawing
its own title bar natively still exposes that text as ordinary element content. Making both surfaces
consistent is what allows "should window titles be redactable" to be asked once, later, as the deferred
additive `titlePattern` work.

## 5. A2 - the stats rename

`desktop_snapshot_stats` emits `redacted` (OS-password nodes only, kept for back-compat during SP3) and
`redactedCount` (all redacted nodes). Two same-rooted names with different meanings mislead by default.

**Change:** `redacted` becomes **`osPasswordCount`**. `redactedCount` is already unambiguous and keeps its
name and meaning. C# member `SnapshotStats.Redacted` becomes `OsPasswordCount`.

Before: `{snapshotId,total,interactive,offscreen,redacted,redactedCount,byControlType}`

After: `{snapshotId,total,interactive,offscreen,osPasswordCount,redactedCount,byControlType}`

**Back-compat:** breaking, deliberately, and only free before v1.0.0. No alias is kept - an alias would
preserve exactly the ambiguity being removed. Unlike A5 this break is LOUD: a consumer reading `redacted`
gets a missing field immediately.

**Pin preserved:** the existing assertion is mutually discriminating (`Redacted == 1` while
`RedactedCount == 2`), so any implementation conflating the counters fails. Only the identifier changes.

## 6. A6 - the token literal, and a rule that keeps it fixed

Three executable sites hold a bare redaction-token literal instead of `ElementContent.RedactedToken`:
`PerceptionManager.cs:210`, `SnapshotDiff.cs:27`, `SnapshotEngine.cs:188`.

⚠ **CORRECTION from panel round 1 - the original text of this section was wrong.** It claimed every other
occurrence was "comments or tool-description prose and out of scope". **Four `[Description(...)]`
attributes in `ContentTools.cs`, `FindTools.cs`, `SnapshotTools.cs` and `WatchTools.cs` contain the
literal, and an attribute argument is EXECUTABLE assembly metadata, not prose.** A naive text rule would
flag them and break the build on day one.

**Therefore the rule is AST-aware, allowlist-INDEPENDENT, and the attributes are refactored:**

1. The sweep rule ignores comment/trivia occurrences. The existing sweep is already Roslyn-based
   (`Microsoft.CodeAnalysis.CSharp` is referenced by the test project), so this is available rather than
   new machinery.
2. ⚠⚠ **The rule MUST NOT run through the existing `AllowedMembers` suppression** (panel round 2,
   Activation Auditor - CRITICAL, and verified). The existing walker suppresses findings on sanctioned
   members: `if (_allowed.ContainsKey(CurrentKey)) continue;` at `RedactionSurfaceInventoryTests.cs:454`
   and `... return;` at `:567`. **All the sites this rule targets are already allowlisted members** -
   `SnapshotEngine.FormatNode` (`:60`), `SnapshotDiff.ShownName` (`:64`), `PerceptionManager.FindAsync`
   (`:69`), `PerceptionManager.ResolveSelectorOnSta` (`:70`). A rule reusing that infrastructure would be
   **silently dead on arrival on exactly the sites it exists to catch.**
   The two concerns are orthogonal and must stay separate: the allowlist exempts a member from the
   CONTENT-READ rules (it is a sanctioned egress accessor); it says nothing about whether that member may
   hard-code the token string. The new rule therefore evaluates every `src/` syntax node regardless of
   `AllowedMembers`, with its own exemption set containing exactly one entry: `ElementContent`.
3. The four `[Description]` attributes are refactored to concatenate `ElementContent.RedactedToken`.
   Valid because it is a `const string`, and a `const + const` concatenation is a compile-time constant,
   which is what an attribute argument requires.
4. Only then does "no bare token literal in `src/` outside `ElementContent`" hold as a rule.

**Honest value, and the limit of the guard:** existing egress tests assert the literal, so a typo at one
site would already fail some test - this removes a coupling, it does not close a leak. And the rule is
**bypassable by construction**: writing the token as a concatenation or a second constant defeats it. It
catches accident, not intent. Stated so it is never mistaken for a guarantee.

## 7. A3, A4 - dispositions (no code)

- **A3** becomes tracked debt in `docs/coverage-debt.md`: the DEF-3 SELECTOR path has no specific fact and
  rests on 21 regression tests. Its sibling half (an element genuinely named with the redaction token) is
  already ledgered as AB-2.
- **A4** becomes a ROADMAP-tracked release-note action: DEF-3 was reachable in the DEFAULT shipping
  configuration (OS password flag, no rules), so every prior install leaked the password-field handle set
  - not only SP3 rule adopters. Written into `CHANGELOG.md` when v1.0.0 is tagged, alongside A5's BREAKING
  entry.

## 8. Testing

**One standard for the increment:** every behaviour changed here gets a pin proven NON-VACUOUS by a
temporary LOGIC MUTANT (flip a boolean, drop a conditional - never a signature break), and the SPECIFIC
new test must be the one that goes red. A suite returning non-zero is not sufficient evidence.

| Item | Pin | Mutant that must turn it red |
|---|---|---|
| A1 | headless unit tests over the pure escalation function: precise mask, one escalation, escalation-to-root refusal, parent-fetch failure refusal, zero-size treated as unusable | drop the escalate branch; drop the root-refusal branch |
| A1 | `maskEscalations` counts ELEMENTS not levels: one element climbing two levels reports `1` | make the counter increment per level |
| A1 | `escalated` carries `automationId` + `controlType` and NEVER the element Name | swap either field to emit Name |
| A1 | an escalating element with NO `automationId` still reports its `controlType` | drop controlType from the entry |
| A1 | Desktop fact: an ordinary capture reports `maskEscalations == 0` and empty `escalated` | hard-code the counter to a constant |
| A6 | ⚠ the new rule FIRES on an allowlisted member - plant a bare token literal inside `SnapshotDiff.ShownName` (which IS in `AllowedMembers`) and confirm the sweep fails | route the new rule through the `_allowed.ContainsKey` suppression; the test must go red |
| A5 | Desktop fact: focus an element whose Name differs from the window title; assert `window.title` equals the WINDOW title and NOT the element name | revert the source to the UIA `Name` read |
| A5 | a window with no caption yields an empty `title`, not an error | make the empty case throw |
| A2 | existing mutually-discriminating counter assertion, renamed | swap the two counters |
| A6 | the new sweep rule, AFTER the attribute refactor | plant a bare token literal in `src/` and confirm the sweep fails; confirm a literal inside a COMMENT does not fail it |

## 8a. Implementation ORDER - a constraint, not a preference

The spec says WHAT; this says in what sequence, because two of these items make the build fail if done
backwards (panel round 3, Mechanism Gamer). Every item below is gated on the one before it.

1. **Source fixes first.** A5's `GetWindowText` change and A1's escalation land before any guard is
   touched.
2. **The A6 attribute refactor**, converting the four `[Description]` attributes to const-concatenate the
   token.
3. **Test rules second.** Add A6's allowlist-independent sweep rule only now - activating it before step 2
   breaks the build on those attributes.
4. **Allowlist REWRITES last** - and note this step changed at panel round 4. It was "delete the
   `ResolveFocusedWindowAsync` entry"; that is now a REWRITE, because fix step 4 keeps a UIA read on that
   member (the window-root fallback). **Deleting it at any point in this sequence turns the build red**,
   which is exactly what an implementer following the old wording would have discovered the hard way.

The general rule behind all four: **a guard may only be tightened after the thing it guards is already
correct.** Tightening first produces a red build that looks like a regression and is actually just
sequencing.

⚠ **And the corollary round 4 exposed:** before tightening a guard, re-read what the guarded code does
AFTER all of this increment's other folds, not as it was when the guard change was first written. The
delete-the-entry instruction was correct when authored and became wrong two folds later, in the same
document, without anyone editing that sentence.

## 9. Gates

Headless + Desktop + PopupGrafting all green at one SHA (`0 skipped` on both Desktop halves), then
AGY-CAPSTONE over the SP4 range until a round is GREEN, then AGY-TEST-AUDIT. Same sequence SP3 completed.

## 10. Binding constraints

1. **No content may reach the wire unclassified.** Every change here either routes a value through the
   decision or removes the value.
2. **Fail closed, but never silently.** Where withholding degrades output (A1's escalation), the caller
   must be able to see that it happened AND identify what caused it, without that diagnostic itself
   leaking withheld content.
3. **The section 4.4 default path.** An install with NO rules configured must be byte-identical on every
   path this increment touches, EXCEPT the three deliberate wire changes: the A2 rename, the A5 re-meaning,
   and A1's additive `maskEscalations` / `escalated` metadata (which appear on every capture, including
   default-path ones).
4. **No pre-existing test may be weakened.** A rename or a construction fix forced by a signature change
   is allowed; changing an assertion, an expected string or an expected count is not.

## 11. Exhaustiveness self-audit (run by the author)

1. **Placeholders.** None. Every item in section 2 has a disposition in sections 3-7.
2. **Contracts named exactly.** Wire shapes quoted before/after from current source
   (`SnapshotTools.cs:116`, `ScreenshotTools.cs:52`). New error code, new metadata fields, their types,
   always-present behaviour and empty-case values are all stated.
3. **Cases and edges.** A1 enumerates nine states including parent-fetch failure and the two no-change
   cases. A5 states the empty-caption case and the classified path. A2 states the no-alias decision.
4. **Requirement coverage.** Each of A1-A6 maps to exactly one section; disposition-only items are marked.
5. **Known gaps, flagged rather than closed here:** (a) the exact ancestor-walk API (TreeWalker vs cached
   parent) is an IMPLEMENTATION choice for the plan; (b) whether any in-repo consumer reads
   `stats.redacted` must be re-grepped when the plan is written, since the plan is where file and line
   citations get verified against live code.

## 12. Adversarial panel - round 1 ledger (folded; do NOT re-raise)

Solo panel (Axiom Breaker, Cascade Analyst, Protocol Pedant, Literal Implementer, Mechanism Gamer,
Blindspot Auditor, State Corruptor, Resource Vampire, Boundary Smuggler) plus an agy escalation round.
Dropped seat: Dependency Cynic - the increment introduces no new library or toolchain.

**Folded:**
- Escalation depth is not uniform; flat Win32 trees refuse on step 1 (section 3, new subsection).
- Constraint 3 omitted A1's own metadata change (section 10, corrected).
- `GetWindowText` empty/failure unspecified (section 4, fix step 4).
- Parent-fetch failure unspecified (section 3, case table).
- `maskEscalations` semantics ambiguous - now counts ELEMENTS, not levels (section 3, contracts).
- A bare count is not a diagnostic - `escalated` added (round 2 extended it to carry `controlType` too), never Name (section 3).
- The A6 rule would have broken the build on four `[Description]` attributes (section 6, corrected).
- The A6 rule is bypassable by concatenation - stated as catching accident, not intent (section 6).
- A5's break is silent where A2's is loud - resolved out-of-band (section 4, operator decision).
- The vanished-element over-redaction cost is now stated, not implied (section 3).

**Refuted by measurement, recorded so it is not re-raised:**
- *"Removing the element Name destroys the only machine-readable identifier."* False:
  `SnapshotTools.cs:116` returns `@ref = f.Ref`, which is directly actionable. The narrower point - the
  element's Name now requires parsing `descriptor` - survives at MEDIUM and is accepted, since `ref` plus
  `desktop_get_text` retrieves it through a classified path.

**Not adopted:** the peer's "16 sites" count for the token literal was not verified and is not carried;
the material claim (attributes are executable metadata) was verified and folded.

### Round 2 (rotation seat: Activation Auditor) - folded; do NOT re-raise

Two seats reported NO NEW FINDINGS (Cascade Analyst, Protocol Pedant), which is the protocol working
rather than a thin round.

- **CRITICAL, and the best finding of the review: the A6 rule would have been DEAD ON ARRIVAL.** The
  existing sweep suppresses on allowlist membership (`:454`, `:567`), and every site A6 targets is an
  allowlisted member. Verified by reading both suppression sites and all four entries. Section 6 now
  requires the rule to be allowlist-INDEPENDENT, with its own one-entry exemption set, plus a test that
  plants a literal inside an allowlisted member specifically to prove the rule still fires there.
  *Why the seat caught it:* Activation Auditor was rotated in precisely because round 1 found a trigger
  problem; it hunts guards that cannot fire, and found one.
- **HIGH: the pure-function seam contradicted lazy evaluation.** "Ancestor rects-or-null in order" forces
  either a pre-fetch of every ancestor of every redact-worthy element (a COM cost on the hottest path) or
  a refusal decided inside the UIA code, which breaks the seam. Resolved with a lazy accessor
  (`Func<int, Rect?>`) whose null return covers a throwing parent fetch.
- **HIGH: `automationId` alone is a blindspot.** Many elements have none - this repo's own fixture
  depends on it - so the diagnostic degraded to `["", "", ""]` on exactly the legacy/flat-tree UIs where
  A1 refuses hardest. `escalated` now carries `controlType` too; both are non-content by BC-1.

### Round 3 (rotation seats: Resource Vampire, Dependency Cynic) - folded; do NOT re-raise

- **CRITICAL: relying solely on `GetWindowText` can blind the agent** where a framework draws its own
  title bar and the visible title exists only in the UIA tree. Folded as a fallback to the WINDOW ROOT's
  Name (not the focused element's - the root's Name IS the title).
  ⚠ The peer's claim was TOO STRONG and is narrowed in section 4: `GetWindowTextW` returned real captions
  for `SystemSettings`, `ApplicationFrameHost` and `SecHealthUI` on this machine. Also recorded there: a
  SELECTION EFFECT (`EnumTopLevel` skips captionless windows) means the listing cannot be used as evidence
  either way.
- **HIGH: the ancestor walk needs two bounds.** Memoize resolved ancestors per capture (N children of one
  broken container otherwise repeat the same chain N times); and CAP the walk, because a cyclic provider
  tree would hang the single query STA - a hang there wedges every later query, not just this capture.
  The peer named the COM-storm half as the weakest part of its own answer (OS caching may absorb it); the
  cap is required for TERMINATION regardless.
- **MEDIUM, and it caught a contradiction I introduced in round 2:** the concrete
  `Func<int, Rect?>` signature was over-specification that would have FORBIDDEN the memoized walker the
  cost requirement now demands. Replaced with three properties (lazy, failure-as-absence, injectable) and
  the shape left to the plan. The exact mutant wording in section 8 was likewise trimmed toward intent.
- **MEDIUM: implementation ORDER is a constraint** - now section 8a. A guard may only be tightened after
  the thing it guards is already correct; doing either A5's or A6's guard first produces a red build that
  looks like a regression and is only sequencing.
- **Peer transparency, recorded:** it did not examine A3/A4 or re-verify A2's rename this round, and named
  its own COM-storm severity as its weakest claim. Both stated without being pressed.

### Round 4, FINAL (rotation seats: State Corruptor, Boundary Smuggler) - folded

- **CRITICAL, and it was a contradiction ROUND 3 introduced:** fix step 3 said DELETE the
  `ResolveFocusedWindowAsync` allowlist entry "because the member performs no UIA content read", while fix
  step 4 - added one round earlier - makes it read the window root's `Name`, which IS a UIA content read.
  Following section 8a literally would turn the build red at the deletion step with no incremental
  landing. The entry is now REWRITTEN rather than deleted, with a reason that is true and narrow, plus an
  explicit warning that this reason has the same SHAPE as the false one it replaces and must be checked
  against WHICH element is read.
  *The sentence was correct when written and became wrong two folds later without being edited.* Section
  8a now carries that as a standing corollary.
- **HIGH: the new UIA fallback was unguarded.** Reading `Name` off the root is a cross-process COM call
  that throws on an unresponsive or tearing-down window - so a harmless captionless window could crash
  `desktop_get_focused_element`, reintroducing the exact failure mode A5 moved to `GetWindowText` to
  escape. Now degrades to an empty title.
- **Memoization cache key trap**, raised by the seat as its OWN weakest assumption and folded on that
  basis: keying the cache on `AutomationElement` invokes `GetHashCode`, which fetches `RuntimeId`
  cross-process - a cache added to remove COM traffic would generate it instead.
- **Cleared, with reasoning worth keeping:** memoization is neutral-to-BENEFICIAL for staleness (it stops
  two siblings failing at different moments from producing two misaligned masks of one scrolling
  container); and `escalated` leaks nothing, because `desktop_find` already publishes `automationId` and
  `controlType` globally for redacted elements, so the screenshot path surfaces nothing the query path
  does not.
- **Peer transparency:** it again skipped A3/A4 and A2, and named its memoization-key assumption as its
  weakest point - which turned out to be the most actionable item in the round.
