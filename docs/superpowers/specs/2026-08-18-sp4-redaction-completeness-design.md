# SP4 - redaction completeness (design)

Status: DESIGN. Approved section-by-section 2026-08-18; adversarial panel round 1 folded (section 12).
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
  `{bounds,dpiScale,scaleApplied,redactions,maskEscalations,escalatedIds}`.
  **Semantics, stated exactly:** `maskEscalations` counts ELEMENTS whose own rect was unusable and whose
  mask was therefore taken from an ancestor - NOT the number of levels climbed. One element climbing three
  levels counts as `1`.
- **`escalatedIds`** (array of strings, always present, empty when none): the `automationId` of each
  escalating element. **Rationale, and why this is safe:** a bare count is not a diagnostic - an operator
  seeing a giant black box and `maskEscalations: 2` cannot tell which control has the broken provider.
  `automationId` is already treated as NON-content by this codebase (a redacted element stays findable by
  `automationId`, and `FindMatch` already publishes it), so emitting it leaks nothing that redaction
  withholds. **The element's Name is NEVER emitted here** - that would leak the identity of the very thing
  the mask exists to hide. An element with no `automationId` contributes an empty string.
- **NEW `ToolErrorCode.RedactionUnmaskable`.** Rejected alternative: reuse `CaptureUnavailable` (already
  used for a locked/disconnected desktop). Rejected because a script would then have to scrape text to
  distinguish "desktop unavailable" from "cannot mask safely" - the same output-contradicts-code defect
  SP3 fixed in `check-redaction-rules`. Adding an enum member is additive and free pre-1.0.
  The refusal message names the `automationId`s that could not be masked, never their Names.

### Testability seam

The escalation DECISION is extracted into a pure function - inputs: the element's own rect-or-null plus
its ancestor rects-or-null in order; output: a mask rect, or a refusal signal. It touches no UIA and is
headless-unit-testable. The UIA-touching code shrinks to "read a rect, hand it to the decision".

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
3. **The sweep allowlist entry for `ResolveFocusedWindowAsync` is DELETED, not reworded** - after the fix
   the member performs no UIA content read and needs no exemption.
4. **Empty or failed `GetWindowText`** yields an empty string. A window legitimately without a caption is
   a normal state, not an error, and must not fail the call.

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

**Therefore the rule is AST-aware, and the attributes are refactored:**

1. The sweep rule ignores comment/trivia occurrences. The existing sweep is already Roslyn-based
   (`Microsoft.CodeAnalysis.CSharp` is referenced by the test project), so this is available rather than
   new machinery.
2. The four `[Description]` attributes are refactored to concatenate `ElementContent.RedactedToken`.
   Valid because it is a `const string`, and a `const + const` concatenation is a compile-time constant,
   which is what an attribute argument requires.
3. Only then does "no bare token literal in `src/` outside `ElementContent`" hold as a rule.

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
| A1 | `escalatedIds` carries `automationId` and NEVER the element Name | swap the field to emit Name |
| A1 | Desktop fact: an ordinary capture reports `maskEscalations == 0` and empty `escalatedIds` | hard-code the counter to a constant |
| A5 | Desktop fact: focus an element whose Name differs from the window title; assert `window.title` equals the WINDOW title and NOT the element name | revert the source to the UIA `Name` read |
| A5 | a window with no caption yields an empty `title`, not an error | make the empty case throw |
| A2 | existing mutually-discriminating counter assertion, renamed | swap the two counters |
| A6 | the new sweep rule, AFTER the attribute refactor | plant a bare token literal in `src/` and confirm the sweep fails; confirm a literal inside a COMMENT does not fail it |

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
   and A1's additive `maskEscalations` / `escalatedIds` metadata (which appear on every capture, including
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
- A bare count is not a diagnostic - `escalatedIds` added, `automationId` only, never Name (section 3).
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
