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

_None._

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
