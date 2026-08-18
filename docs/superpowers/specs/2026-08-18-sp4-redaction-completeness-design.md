# SP4 - redaction completeness (design)

Status: DESIGN, approved section-by-section 2026-08-18. Branch point: `a76092d` (SP3 merged).

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
- **The `ProcessIdentity` strong-type refactor** (compile-time impossibility of placeholder values).
  Logged as follow-up during SP3; additive to safety, not deadline-bound.

## 3. A1 - the pixel path

### The rule

> A redact-worthy element must contribute a usable mask rect. If it cannot, escalate to its parent. If
> escalation would reach the window root, REFUSE the capture.

### Why one rule and not a taxonomy

The tempting design branches on *why* a rect is missing (threw / element vanished / zero-size).
Deliberately rejected: distinguishing those requires catching provider-specific exception types, and
provider-specific behaviour is the recurring source of defects in this codebase. A peer proposal to treat
a vanished element as safe-to-skip was explicitly marked as inference by the peer and is **not** adopted.

### Behaviour, all cases

| Case | Behaviour |
|---|---|
| rect reads, non-empty | masked precisely (unchanged from today) |
| rect read THROWS | escalate to parent; `maskEscalations` += 1 |
| element vanished mid-capture | identical to throws - escalate. No special case. |
| rect reads as `0x0` / empty | identical to throws - escalate. A provider unable to report bounds may return zeros rather than throw. |
| parent rect also unusable | escalate again, up the ancestor chain |
| escalation reaches the window root | **REFUSE**: `ToolException(RedactionUnmaskable)`. No image is returned. |
| element is not redact-worthy | unchanged; no rect collected, no escalation |
| no redact-worthy elements at all | unchanged; `maskEscalations` = 0 |

### Why refusal, not a black image

Escalating to the window root and masking it returns a *successful* all-black screenshot. That is worse
than an error: an agent will hallucinate contents or loop on it, whereas a refusal forces a strategy
change. (This failure mode was named by the design peer against its own preferred option.)

### Contracts

- **`desktop_screenshot` metadata gains `maskEscalations`** (integer, ALWAYS present, `0` when none).
  Before: `{bounds,dpiScale,scaleApplied,redactions}`. After:
  `{bounds,dpiScale,scaleApplied,redactions,maskEscalations}`. Additive, but included now because
  over-redaction the caller cannot see is undebuggable - the same property `redactedBy` provides on the
  text paths.
- **NEW `ToolErrorCode.RedactionUnmaskable`.** Rejected alternative: reuse `CaptureUnavailable` (already
  used for a locked/disconnected desktop). Rejected because a script would then have to scrape text to
  distinguish "desktop unavailable" from "cannot mask safely" - the same output-contradicts-code defect
  SP3 fixed in `check-redaction-rules`. Adding an enum member is additive and free pre-1.0.

### Testability seam

The escalation DECISION is extracted into a pure function - inputs: the element's own rect-or-null plus
its ancestor rects-or-null in order; output: a mask rect, or a refusal signal. It touches no UIA and is
headless-unit-testable. The UIA-touching code shrinks to "read a rect, hand it to the decision".

WARNING - consequence for the ledger: A1 therefore does **not** join the accepted-boundary list, and
`docs/coverage-debt.md` entry **AB-3**'s compensation must be re-validated during this increment, per
that file's own rule that a changed compensation reopens the entry.

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
2. **No new field for the element name.** `GetFocusedElementAsync` already returns `descriptor`, rendered
   through `SnapshotEngine.Render` and therefore already classified. The raw value was redundant as well
   as leaky.
3. **The sweep allowlist entry for `ResolveFocusedWindowAsync` is DELETED, not reworded** - after the fix
   the member performs no UIA content read and needs no exemption.

### Contract

`desktop_get_focused_element` shape is unchanged: `{ ref, descriptor, window:{ handle, title, pid } }`.
**`window.title`'s VALUE changes meaning** - from the focused element's Name to the owning window's
title. Breaking; therefore pre-v1.0.

### Stated limitation

This replaces an unclassified element name with an unclassified window title. That is not a new exposure
(`desktop_list_windows` already publishes titles unclassified) but it is not zero either: an application
that draws its own title bar natively still exposes that text as ordinary element content. Making both
surfaces consistent is what allows "should window titles be redactable" to be asked once, later, as the
deferred additive `titlePattern` work.

## 5. A2 - the stats rename

`desktop_snapshot_stats` emits `redacted` (OS-password nodes only, kept for back-compat during SP3) and
`redactedCount` (all redacted nodes). Two same-rooted names with different meanings mislead by default.

**Change:** `redacted` becomes **`osPasswordCount`**. `redactedCount` is already unambiguous and keeps
its name and meaning. C# member `SnapshotStats.Redacted` becomes `OsPasswordCount`.

Before: `{snapshotId,total,interactive,offscreen,redacted,redactedCount,byControlType}`

After: `{snapshotId,total,interactive,offscreen,osPasswordCount,redactedCount,byControlType}`

**Back-compat:** breaking, deliberately, and only free before v1.0.0. No alias is kept - an alias would
preserve exactly the ambiguity being removed.

**Pin preserved:** the existing assertion is mutually discriminating (`Redacted == 1` while
`RedactedCount == 2`), so any implementation conflating the counters fails. Only the identifier changes.

## 6. A6 - the token literal, and a rule that keeps it fixed

Three executable sites hold a bare redaction-token literal instead of `ElementContent.RedactedToken`:
`PerceptionManager.cs:210`, `SnapshotDiff.cs:27`, `SnapshotEngine.cs:188`. (All other occurrences are
comments or tool-description prose and are out of scope.)

**Honest value:** existing egress tests assert the literal, so a typo at one site would already fail some
test. This removes a coupling; it does not close a leak. Stated plainly rather than oversold.

**What makes it durable:** add a rule to `RedactionSurfaceInventoryTests` forbidding a bare redaction-token
literal in `src/` outside `ElementContent`. Reintroducing the coupling then fails the sweep. The guard is
a RULE, not a written justification - deliberately, given that two written justifications in that same
file have now proved false.

## 7. A3, A4 - dispositions (no code)

- **A3** becomes tracked debt in `docs/coverage-debt.md`: the DEF-3 SELECTOR path has no specific fact
  and rests on 21 regression tests. Its sibling half (an element genuinely named with the redaction
  token) is already ledgered as AB-2.
- **A4** becomes a ROADMAP-tracked release-note action: DEF-3 was reachable in the DEFAULT shipping
  configuration (OS password flag, no rules), so every prior install leaked the password-field handle
  set - not only SP3 rule adopters. Written into `CHANGELOG.md` when v1.0.0 is tagged.

## 8. Testing

**One standard for the increment:** every behaviour changed here gets a pin proven NON-VACUOUS by a
temporary LOGIC MUTANT (flip a boolean, drop a conditional - never a signature break), and the SPECIFIC
new test must be the one that goes red. A suite returning non-zero is not sufficient evidence.

| Item | Pin | Mutant that must turn it red |
|---|---|---|
| A1 | headless unit tests over the pure escalation function: precise mask, one escalation, escalation-to-root refusal, zero-size treated as unusable | drop the escalate branch; drop the root-refusal branch |
| A1 | Desktop fact: an ordinary capture reports `maskEscalations == 0` | hard-code the counter to a constant |
| A5 | Desktop fact: focus an element whose Name differs from the window title; assert `window.title` equals the WINDOW title and NOT the element name | revert the source to the UIA `Name` read |
| A2 | existing mutually-discriminating counter assertion, renamed | swap the two counters |
| A6 | the new sweep rule | plant a bare token literal in `src/` and confirm the sweep fails |

## 9. Gates

Headless + Desktop + PopupGrafting all green at one SHA (`0 skipped` on both Desktop halves), then
AGY-CAPSTONE over the SP4 range until a round is GREEN, then AGY-TEST-AUDIT. Same sequence SP3 completed.

## 10. Binding constraints

1. **No content may reach the wire unclassified.** Every change here either routes a value through the
   decision or removes the value.
2. **Fail closed, but never silently.** Where withholding degrades output (A1's escalation), the caller
   must be able to see that it happened.
3. **The section 4.4 default path.** An install with NO rules configured must be byte-identical on every
   path this increment touches, except where a wire field is deliberately renamed (A2) or re-meaninged
   (A5).
4. **No pre-existing test may be weakened.** A rename or a construction fix forced by a signature change
   is allowed; changing an assertion, an expected string or an expected count is not.

## 11. Exhaustiveness self-audit (run by the author, completed)

1. **Placeholders.** None. No TBD, no "decide later", no unowned question. Every item in section 2 has a
   disposition in sections 3-7.
2. **Contracts named exactly.** Both wire shapes are quoted before/after from the current source
   (`SnapshotTools.cs:116`, `ScreenshotTools.cs:52`), not from memory. The new error code and the new
   metadata field are named, typed, and their always-present/default behaviour stated.
3. **Cases and edges.** A1 enumerates eight states including the two that produce no change. A5 states
   the outcome for the classified path (`descriptor`) as well as the changed one. A2 states the no-alias
   decision explicitly.
4. **Requirement coverage.** Each of A1-A6 maps to exactly one section; the two disposition-only items
   are marked as such so they are not mistaken for code work.
5. **Known gaps, flagged rather than closed here:**
   - (a) the exact ancestor-walk API for A1's escalation (TreeWalker vs a cached parent) is an
     IMPLEMENTATION choice, resolved in the plan, not the spec;
   - (b) whether any existing consumer inside this repo reads `stats.redacted` must be re-grepped when
     the plan is written, because the plan - not the spec - is where file and line citations must be
     verified against live code.
