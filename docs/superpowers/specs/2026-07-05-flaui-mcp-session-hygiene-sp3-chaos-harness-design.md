# FlaUI.Mcp — Session-Hygiene SP3: Delta snapshot + chaos harness — Design

**Date:** 2026-07-05
**Status:** Draft (SP3 — depends on SP2's declared-delta metadata)
**Sibling specs:** SP1 (audit + contract), SP2 (harden the gaps).

Design spec, not a line-level plan.

## 1. Goal

Prove — empirically, not by static analysis — that each mutating tool's **actual** session delta is a
subset of its **declared** delta (SP1 §3). This is the enforcement mechanism for a behavioral property
that reflection cannot see (SP1 §1).

## 2. Why a test harness, not a reflection guard
`ToolReadOnlyInvariantTests` works because "routes through GuardWrite" is statically visible. "Left the
human's foreground/modifiers no worse" is only observable by *running* the tool against a real desktop
and measuring OS state before/after. So SP3 is a **behavioral test harness**, and — because it needs a
real interactive desktop — it is **Category=Desktop**, i.e. a console/smoke gate, **never a CI build
gate** (the dev/CI box is headless RDP-only; documented constraint, not a defect).

## 3. Components

### 3.1 `SessionStateSnapshot`
A cheap, pure-Win32 capture of the human-session state the invariant protects:
- `Foreground` = `GetForegroundWindow()`.
- `Modifiers` = a bitmask of `GetAsyncKeyState` for the synthetic-relevant modifiers (Shift/Ctrl/Alt/Win).
- `Clipboard` = the current clipboard text held **in-memory for the duration of the test only**,
  compared by equality. **No hashing, no logging of the payload** (AGY-AFTER panel, security seat: a
  hash of a short high-entropy secret — a 2FA code, a short password — is brute-forceable if a failing
  test logs "expected hash X got Y"). On mismatch the harness logs only a hardcoded boolean message
  (`"FAIL: clipboard was mutated"`), never the before/after value.
`snapshot.Diff(other)` yields a `SessionDelta` (which dimensions changed).

### 3.2 Delta assertion
`AssertWithinDeclared(before, after, declaredDelta)`:
- `Foreground` changed but declared `Preserve` → FAIL (unless declared `MayChange`/`MustRestoreOnCollapse`
  and the after-foreground is a *valid* top-level, not null/0x0 — the orphan condition).
- A modifier that is down in `after` but not `before` → FAIL (stranded modifier).
- Clipboard hash changed but declared `Preserve` → FAIL.

### 3.3 `DesktopChaosMonkey` (optional, opt-in per test)
A background thread that, while a tool runs, injects adversarial OS churn to prove the tool's hygiene
survives real-world interference: briefly steal foreground to a scratch window, toggle CapsLock, write
junk to the clipboard. Bounded, deterministic-seeded, and torn down (itself hygienic) after the tool
returns. **Opt-in** — not every tool test needs chaos; it is the stress tier.

### 3.4 xUnit harness wiring
A fixture/attribute that, for a tool under test: snapshot → (optional) start chaos → invoke tool →
stop chaos → **bounded post-tool OBSERVATION WINDOW** → snapshot → `AssertWithinDeclared`. The declared
delta is read from SP2's metadata so the oracle and the tool stay in sync.

**Observation, not a blind sleep (AGY-AFTER R2, determinism seat):** a fixed `Thread.Sleep(200)` is
flaky — under load a teardown may exceed it and the snapshot catches an intermediate orphaned state as a
false failure. But the goal here is unusual: we are watching for a *late degradation* (the async
re-orphan), so we cannot early-return on "state looks good". The design is a bounded window (default
~200ms, tunable, hard cap ~2s) that **samples the session state repeatedly across the window** and fails
if it observes an orphan at ANY sample — deterministic (proceeds the instant it sees the bad state)
without being blind to a late one.

**The stability wait is load-bearing (AGY-AFTER panel, general-adversarial seat):** snapshotting the
instant a tool returns is blind to an *asynchronous* re-orphan — e.g. a target process that finishes
dying ~50ms after `CloseAsync` returns and re-kills the foreground. Without the wait, SP3 would falsely
pass exactly the failure class SP4 exists to catch. With the wait, SP3 *is* the SP4 trigger detector: if
a tool passes the synchronous check but fails after the stability wait, that is the reproduction that
un-parks SP4 (SP4 §2). The wait duration is tunable; the default must exceed typical process-teardown.

## 4. Scope / non-goals
- Modifiers dimension covers only the synthetic modifier set, not arbitrary keys (SP1 found teardown
  sound; this pins it).
- No attempt to run under headless CI. If a future always-connected Desktop CI lane exists (backlog),
  SP3 can join it; until then it is a documented console/smoke gate.
- The chaos monkey never touches real user data destructively (clipboard is saved+restored by the
  monkey itself; foreground steal targets only its own scratch window).

## 5. Acceptance
- Running the harness on a real console: every mutating tool passes `AssertWithinDeclared` for its
  declared delta; the two SP2-changed tools pass *with* chaos enabled (minimize still restores
  foreground even when the monkey steals focus mid-op).
- A deliberately-broken tool (declared `Preserve`, actually orphans) is caught by the harness (a
  self-test proving the harness has teeth).

## 6. Open decisions carried to the plan
1. Chaos monkey default: off (opt-in per test) vs. on for the two hygiene-critical tools.
2. Clipboard hash algorithm + whether to skip the clipboard dimension when a password/secure field is
   involved (avoid capturing/hashing sensitive content — align with existing redaction posture).
3. Whether `SessionStateSnapshot` lives in Core (reusable by SP4's Sentinel) or in the test project only.
