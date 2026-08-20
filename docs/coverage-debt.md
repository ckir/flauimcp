# Coverage debt and accepted boundaries

Rolling, committed record maintained by the **agy-test-audit** discipline. It holds only what must
persist between audits:

- **Tracked debt** — verified coverage gaps an owner chose to defer. Closed gaps are REMOVED.
- **Accepted-boundary ledger** — behaviours deliberately not covered through this harness because they
  are untestable without brittle mocks AND otherwise compensated. These are do-not-re-raise.

⚠ Each accepted boundary records its **specific compensation + a code anchor**. A future audit must
**re-validate that the compensation still exists** before honouring the do-not-re-raise. An entry whose
compensation has vanished is promoted back to a live gap.

⚠ This file cannot prune itself. A routine diff-scoped audit never sees deleted code, so a periodic
manual whole-tree pass is needed to drop orphaned entries.

---

## Tracked debt (deferred gaps)

### A3 — the DEF-3 SELECTOR path has no specific fact
**Gap:** DEF-3 (a redacted element is withheld from NAME search, so a selector cannot be used as a
password-field locator oracle) is pinned on the find and wait paths. The SELECTOR path rests on 21
regression tests that would each fail for other reasons first, so no single fact says "the selector honours
DEF-3".
**Why deferred:** the direct pin needs a visible fixture element literally named `[REDACTED]`, which risks
the layout-coupled fixture guards `MainWindow.xaml` warns about (D7 / offscreen-cull / column-height
invariants). Its sibling half — an element GENUINELY named the token must still be matchable — is already
ledgered as AB-2 for the same reason.
**Owner:** post-v1.0. Captured during SP3, dispositioned in SP4 (spec §7).

The 2026-08-18 audit of `sp3-per-field-redaction` found two verified gaps and the owner scoped both to
**close now**; neither was deferred. See the accepted boundaries below for what was ruled out of scope
rather than deferred.

---

## Accepted-boundary ledger (do NOT re-raise)

### AB-1 — A throwing UIA property read cannot be staged
**Behaviour:** the fail-CLOSED branches that fire when `IsPassword` / `AutomationId` / `Name` THROW.
**Why not covered:** no fixture can make a conformant WPF provider throw on demand; the same limitation
`PasswordRedactionTests` has documented since before SP3.
**Compensation + anchor:** the fail-closed PRIMITIVE is pinned headlessly —
`RedactionPolicy.IsPasswordOrFailClosed` (`PerceptionManagerShouldFixTests`); and the fail-closed
OUTCOME's provenance is pinned in `CapstoneFixTests.An_unreadable_identity_is_a_redaction_with_its_own_provenance`.

### AB-2 — An element genuinely named `[REDACTED]`
**Behaviour:** such an element must still be matchable by name (the anti-over-redaction half of DEF-3).
**Why not covered on the desktop path:** the WPF fixture contains no element literally named that, and
adding one risks the layout-coupled fixture guards `MainWindow.xaml` warns about (D7 / offscreen-cull /
column-height invariants).
**Compensation + anchor:** pinned headlessly —
`WaitNameOracleDef3Tests.A_visible_element_genuinely_named_the_token_is_still_matched`.

### AB-3 — `ProcessIdentity.OfElement`'s HWND fallback  *(audit 2026-08-18, gap 3)*
**Behaviour:** recovering the pid via `NativeWindowHandle` when the UIA `ProcessId` read answers 0.
**Why not covered:** requires an element whose provider reports pid 0 while holding a valid window
handle. `TestAppFixture` always supplies a healthy positive pid, and faking `AutomationElement` means
mocking a concrete UIA-backed type — brittle by construction.
**Compensation + anchor:** the pid-rejection half IS pinned —
`CapstoneFixTests.A_pid_of_zero_is_unattributable_not_the_Idle_process` asserts `OfPid(0)` and
`OfPid(-1)` are null and that a real pid resolves. ⚠ **Honest limit:** that pins the DESTINATION of the
fallback, not the fallback itself. If the fallback block were deleted, no test goes red.
**Re-validated 2026-08-20 (SP4):** anchor confirmed present at `CapstoneFixTests.cs:263`. Entry stands.
A1's injectable-seam pattern was NOT extended to `ProcessIdentity.OfElement` — that is the out-of-scope
`ProcessIdentity` strong-type follow-up (SP4 spec §2), not this increment.

### AB-4 — `ElementContent.Read.Absent` assignment on an unreadable name  *(audit 2026-08-18, gap 4)*
**Behaviour:** `absent = true` when the name read returns null or throws.
**Why not covered at the assignment:** same fake-`AutomationElement` problem as AB-3.
**Compensation + anchor:** the CONSUMER is pinned — `WatchPayloadBuilderTests` asserts
`Assert.Null(p.Name)` (`:61`), i.e. the null-vs-`""` wire contract that `Absent` exists to preserve. A
regression that broke the contract at the consumer end is caught; one confined to the assignment is not.

### AB-5 — Timing and cost figures
**Behaviour:** walk-cost and process-homogeneity measurements.
**Why not asserted:** a timing threshold on a shared, contended machine is a flaky test, not a
measurement. They measure and assert no policy.
**Compensation + anchor:** carried as `[Trait("Category", "Measurement")]`, excluded from the routine
gates by design and runnable on demand; figures recorded in `ROADMAP.md` under item 9.

### AB-6 — the window-title UIA fallback and its catch  *(SP4/A5)*
**Behaviour:** in `WindowManager.ResolveFocusedWindowAsync`, reading the window ROOT's `Name` when the
Win32 caption is empty, and degrading to an empty title when that read THROWS.
**Why not covered:** needs a live window whose caption is empty AND whose provider throws on demand — the
same limitation as AB-1, plus a selection effect that makes even the empty-caption half hard to sample:
`EnumTopLevel` skips captionless windows (`WindowManager.cs:394`), so `desktop_list_windows` can never show
one, and their absence there is not evidence they do not exist.
**Compensation + anchor:** both halves are DESKTOP pins in `RedactionOracleTests` —
`An_unreadable_window_caption_answers_null_rather_than_throwing` asserts `WindowTitle` ANSWERS rather than
throwing, which is the precondition for the fallback ever running, and
`Focused_element_reports_the_WINDOW_title_not_the_element_name` pins the primary path.
⚠ **The plan called the first of these a HEADLESS pin in a class named `WindowTitleTests`. It is neither.**
Measured during execution: every file in this repo that constructs a `WindowManager` carries
`[Trait("Category", "Desktop")]`, and the headless filter is what the CI runner executes — so a headless
version would have risked a red CI on `master` to save a Desktop slot. The fact was moved into
`RedactionOracleTests` for that reason. Corrected here so the ledger names a test that exists.
⚠ **Honest limit:** if the fallback block or its `catch` were deleted, no test goes red on a machine whose
windows all have captions.

### AB-9 — an unbindable window is skipped, and now SAYS SO  *(SP4/A1, pre-existing; RESOLVED in SP4)*
**Behaviour:** on the full-desktop path, `AllMaskRectsAsync` catches a failure to resolve a window's
geometry and skips that window; the capture proceeds and photographs it.
**Why it matters:** an ELEVATED window (an admin terminal, Task Manager) cannot be bound by a
non-elevated UIA client at all, so `ResolveWindowCaptureGeometryAsync` throws BEFORE reaching the mask
walk. Failing to prove a window is safe would otherwise become, silently, a guarantee that its pixels are
captured.
**Resolution (operator-decided 2026-08-20):** the skip STAYS, but it is no longer SILENT. `DesktopMaskSet`
carries `UnmaskedProcesses` and `desktop_screenshot` metadata carries `unmaskedProcesses` — always present,
empty when nothing was skipped. A non-empty list means the image is NOT fully redacted.
**Why not a refusal:** MEASURED — the catch clause cannot distinguish a window UIA cannot BIND from one
that CLOSED mid-enumeration; both surface as the same COM failures. Refusing would therefore fire on
ordinary UI churn as well as on every elevated Task Manager, admin terminal, regedit and MMC snap-in, which
is near-total loss of availability on a developer desktop. That same indistinguishability is why the field
is named for the CONSEQUENCE ("contributed no masks") rather than for a cause the code cannot establish.
**Compensation + anchor:** `ScreenshotTools` still refuses full-desktop capture outright when a DENYLISTED
credential window is visible, and — VERIFIED at `PerceptionManager.cs:1154-1158` —
`DenylistedWindowsVisibleAsync` enumerates by PROCESS (`ListWindowsAsync` + `PerceptionPolicy.IsDenied`),
never by UIA binding, so that guard still fires for a denylisted window that is elevated. The
`unmaskedProcesses` field is presence-pinned by
`RedactionOracleTests.An_ordinary_window_capture_reports_no_mask_escalations`.
⚠ **Honest limit:** the POPULATED case is not pinned — it needs a live unbindable window, which no fixture
can stage (the AB-1 limitation). And reporting is not protecting: those windows' pixels are still in the
image. UIA could never read their contents, so no redaction ever applied to them; SP4 makes that visible,
not false.

### AB-8 — escalation assumes an ancestor encloses its descendants  *(SP4/A1)*
**Behaviour:** when an element's own bounds are unreadable, the mask taken from its ancestor is assumed to
cover the pixels the element was painting.
**Why not covered, and why not FIXED:** a child can paint outside its parent's bounds via a negative
margin, absolute/canvas positioning, or a render transform. The check cannot be written, in tests or in
production, because escalation runs precisely when the element's own extent is the unavailable quantity.
**Compensation + anchor:** none, and that is the entry's point. The limit is stated in
`MaskEscalation`'s own type doc so it is read by anyone touching the mechanism, and the diagnostic path
(`maskEscalations` + `escalated`) tells an operator which control escalated, so a leak of this shape is at
least attributable after the fact.
⚠ **Honest limit:** an escalated mask is best-effort. A1 closes the fail-OPEN hole where an unreadable rect
contributed NO mask at all; it does not promise the substituted mask is pixel-complete.

### AB-7 — `AncestorRectSource`'s root bound  *(SP4/A1)*
**Behaviour:** the ancestor climb stops AT its search root and never climbs past it. Reaching the WINDOW
root yields no rect, which becomes a refusal; reaching a POPUP root offers that popup's rect, which
`MaskEscalation` then accepts or rejects geometrically. (This entry described a blanket refusal at every
root until the popup fork and the geometric rule replaced it — the behaviour changed three times during
review, so read the code, not this sentence, if they ever disagree.)
**Why not covered:** needs a live element that fails to report bounds while its ancestors succeed. No
fixture can stage one — the AB-1 limitation.
**Compensation + anchor:** the decision's half of the contract is pinned headlessly by
`MaskEscalationTests.A_source_that_reports_the_root_as_usable_would_mask_instead_of_refusing`, which
asserts that a source still answering at the root turns a refusal into a mask — i.e. it states exactly what
the source owes the decision.
**Second uncovered half:** whether the root is treated as REFUSE (window) or as a usable mask (popup) is
an adapter decision too. The decision function only ever sees "a rect" or "no rect", so both branches look
identical from the headless side.
⚠ **Honest limit, and it is the sharp one:** if the root check were deleted from `AncestorRectSource`, no
test goes red. **This is not hypothetical — it is what happened.** The plan's first draft had no root bound
at all, every headless fact passed, and the guarantee was dead: `GetParent` succeeds at the window root and
the root has valid bounds, so escalation would have returned a successful all-black screenshot. It was
caught by an adversarial panel, not by the suite. Treat this entry as a standing warning about what a
faked ancestor source can hide.

### AB-10 — the dying-element cases are MEASURED, not asserted  *(SP4/A1)*
**Behaviour:** what a dead element answers, which decides whether A1 escalates or refuses.
**Why not asserted:** the outcome is an OS/provider behaviour, not a contract this repo owns. A test that
fails the suite when UIA changes its mind would be a liability, so both cases are recorded as
`[Trait("Category", "Measurement")]` in `StaleElementParentMeasurementTests` and excluded from the routine
gates.
**What was measured (2026-08-20, physical console):**
· process KILLED → `BoundingRectangle`, `RuntimeId` and `GetParent` ALL throw `COMException 0x80040201`,
  so no ancestor resolves and A1 REFUSES.
· element REMOVED while the app stays alive — the frequent case — → all three ANSWER; the rect is
  `{0,0,0,0}`, `GetParent` returns the container, and escalation SUCCEEDS.
**Why it matters:** the common case over-masks a container, which is exactly the cost the spec signed off
on; refusal is confined to whole-process death. This closed the plan's OPEN #1 with no code change.
⚠ **Honest limit:** measured on one Windows build against WPF. A different framework or OS version may
answer differently, and nothing in the suite would notice.

### AB-11 — a POPUP whose enumeration throws is skipped, and its pixels are captured  *(SP4/A1)*
**Behaviour:** in `ResolveWindowCaptureGeometryAsync`'s mask walk, a search root at `rootIndex > 0` — a
popup, tooltip or context menu — whose `FindAllDescendants()` throws is skipped with `catch { continue; }`.
The capture then proceeds and photographs that popup's pixels with no mask set for it.
**Why it is this way, deliberately:** this is decision D3, made by the operator. The walk is STRICT on
`roots[0]` (the window — a failure there is the TARGET dying, and it REFUSES) and LENIENT on popups,
because a tooltip or menu closing mid-scan is an ordinary UI event and must not fail the window's own
capture. `FindAsync` (`:583-596`) and `EvaluateSelectorValueAsync` (`:745-760`) already draw this same
line.
**The residual leak, stated plainly because it was not written down until a capstone round named it:** the
`continue` cannot distinguish a popup that CLOSED from one that is still painted but whose provider
glitched. In the second case the popup is on screen, holds possibly-redact-worthy content, and is captured
in the clear. The measured behaviour in AB-10 makes the benign case likely — a popup that closed leaves an
element that ANSWERS with a zero-area rect rather than throwing — which means a THROW here is more likely
to be the glitch case than the closed case. That cuts against the leniency, and it is why this is ledgered
rather than left implicit.
**Compensation + anchor:** partial. `roots[0]` is strict, so the window's own content is never skipped this
way, and a DENYLISTED window still refuses the whole capture before any of this runs. There is no pin for
the popup-throws path: staging a popup whose enumeration throws on demand is the AB-1 limitation.
**Why not fixed in SP4:** the fix proposed at capstone — check Win32 `IsWindow` on the popup and refuse if
it is still alive — requires `PopupFinder.SearchRoots` to yield HWNDs alongside its `AutomationElement`s,
which changes a shared API used by paths outside the mask walk. That is an increment, not a detail.
⚠ **Honest limit:** A1 closes the fail-open where a redact-worthy ELEMENT contributed no mask. It does not
close the fail-open where an entire popup SUBTREE was never walked.

### AB-12 — bare `catch` blocks still swallow CRITICAL failures repo-wide  *(SP4 capstone round 4)*
**Behaviour:** SP4 filtered `OutOfMemoryException` / `OperationCanceledException` out of every catch on the
redaction paths it touched, and out of `ToolResponse.Guard` / `GuardImage`. **MEASURED: 108 bare catches
remain across more than 20 files** in `src/` — `Interaction`, `Watch`, `Session`, `TerminalTabReader`,
`DpiHelper`, `RefRegistry`, `WindowManager` and others. Any of them can still swallow a critical.
**Why not fixed here:** the capstone peer reported "19" and listed only the files adjacent to SP4. The real
count is 108, which makes this a repo-wide refactor touching input, watch, session and geometry code that
this increment never opened. Doing it as a tail-end change to a redaction branch would be exactly the
unreviewed sprawl the branch has otherwise avoided.
**Compensation + anchor:** partial and specific. Every catch on the SP4 pixel path — the mask walk's four
former bare catches, the three converting catches, the blanket conversion, `AllMaskRectsAsync`, and both
`ToolResponse` boundaries — now excludes criticals. So a critical raised anywhere in the redaction flow
propagates rather than being laundered into a redaction verdict or an argument error.
⚠ **Honest limit:** a critical raised in code SP4 did not touch is still swallowed, and the process still
carries on. Tracked as ROADMAP item 13.
⚠ **Also rejected at that round, and recorded so it is not re-proposed:** filtering
`StaThreadContext.RunAsync`'s catch. It does not swallow — it marshals the exception to the caller via
`tcs.SetException`, and after this branch that reaches the caller uncaught. Filtering it would kill the
single shared query STA thread AND leave the caller's `TaskCompletionSource` never completed, so the caller
would hang forever on a dead dispatcher. That is strictly worse than delivering the exception.
