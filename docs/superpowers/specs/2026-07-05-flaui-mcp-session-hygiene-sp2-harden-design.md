# FlaUI.Mcp — Session-Hygiene SP2: Harden the gaps — Design

**Date:** 2026-07-05
**Status:** Draft (SP2 — depends on SP1 audit)
**Sibling specs:** SP1 `2026-07-05-flaui-mcp-session-hygiene-design.md` (audit + invariant + contract).

This is a **design spec**, not a line-level plan. The line-level plan is authored in writing-plans
against the real code when SP2 starts, so the exact signatures/line numbers below are illustrative
intent, not commitments.

## 1. Goal

Close the live gaps SP1's audit found, each tool honoring its declared session delta, and *pin* the
already-sound surfaces so a future tool cannot regress them. No new tools; no behavior change to any
tool whose declared delta is already met.

## 2. Work items (from SP1 findings)

### 2.1 Extract a shared foreground healer (`SessionForegroundGuard`)
Generalize the v0.11.1 `WindowManager.RestoreForegroundAfterClose` into one reusable primitive so close
and minimize share it (open decision #1 in SP1 → resolved here: **extract**).

- Contract: `RestoreForegroundAfterCollapse(IntPtr collapsedHwnd, Func<bool> hasCollapsed)` — best-effort,
  bounded wait for `hasCollapsed()` (the "the previously-foreground window has stopped being a valid
  foreground" tell), then `SetForegroundWindow(PickForegroundFallback(EnumTopLevel(), collapsedHwnd))`.
- `PickForegroundFallback` is reused unchanged (already pure + tested).
- The two callers differ only in `hasCollapsed`:
  - **close:** `() => !IsWindow(collapsedHwnd)` (window destroyed).
  - **minimize:** `() => GetForegroundWindow() != collapsedHwnd` (window still exists but yielded
    foreground) — see 2.2.
- Never throws (best-effort); runs on the query STA; pure Win32 (no UIA).
- **STA caveat (AGY-AFTER R2, concurrency seat):** the bounded wait runs ON the single query STA. This
  is proven acceptable for the *close* case — v0.11.1 ships a `Thread.Sleep(25)` bounded loop there and
  smoked clean, because the window's destruction happens on the *target's* thread, not ours, so our STA
  blocking only delays other queued queries (fine for a terminal op). Minimize collapses on the target's
  thread too, so the same reasoning holds. The plan must nonetheless **validate on real hardware** that
  the STA spin does not stall the minimize/DWM transition; if it does, switch that wait to a pumping
  yield (`await Task.Delay`) rather than a synchronous sleep.

### 2.2 Fix `desktop_window_transform minimize` (the primary gap)
When minimize targets the **foreground** window, it orphans focus like close did.

- Before minimizing: capture `wasForeground = GetForegroundWindow() == hwnd`.
- Minimize via the existing `SetWindowVisualState(Minimized)` path.
- If `wasForeground`: `SessionForegroundGuard.RestoreForegroundAfterCollapse(hwnd, () => GetForegroundWindow() != hwnd)`.
- Maximize/Normal are unaffected (window stays foreground).
- **Design fork carried to plan (SP1 open #2):** minimizing does not destroy the window, so the
  "collapsed" tell is "left the foreground", not `!IsWindow`. There is a subtlety to validate on real
  hardware: does the OS move foreground off a just-minimized window on its own (making the wait resolve),
  or must the guard also nudge it? The plan validates this against the real minimize path; if the OS does
  not yield foreground, the guard proceeds after the bounded wait and picks a fallback regardless
  (best-effort — same ceiling as close).

### 2.3 `desktop_focus_window` observability (low-severity)
`FocusAsync` calls `w.Focus(); w.SetForeground()` but does not report whether foreground was actually
gained; under the foreground-lock this can silently no-op.

- After the attempt, read back `GetForegroundWindow() == hwnd` and surface it (e.g. a `foregroundGained`
  boolean on the result), so the agent sees the lock ceiling instead of assuming success.
- No throw (a failed foreground grab is not a tool failure).
- **Wire-contract caveat (AGY-AFTER panel, API seat):** whether this is truly non-breaking depends on
  the tool's *current* return shape. The plan MUST first check what `desktop_focus_window` returns
  today: if it already returns a structured/JSON object, add the field additively; if it returns a
  plain prose string, do NOT silently change the shape to an object (that breaks rigid string parsers) —
  either append the signal in a parse-safe way or make the shape change a deliberate, documented
  decision. Verified against real code at plan time.

### 2.4 Encode the declared deltas + pin
- Attach each mutating tool's declared session delta (SP1 §3 dimensions) as machine-readable metadata
  (encoding = SP1 open #3, resolved in the SP2 plan; must be readable by SP3's harness).
- Add targeted **behavioral** tests for the two changed tools (minimize-restores-foreground,
  focus-reports-gain). These are **Desktop-category** (need a real console) — the pure
  `PickForegroundFallback` stays headless-covered as today.

## 3. Non-goals
- No Session Sentinel (SP4).
- No chaos harness (SP3).
- No change to modifier-teardown or clipboard paths — SP1 found them sound; they are only *pinned* by
  the delta metadata + (in SP3) the harness.

## 4. Constraints
- **Foreground-lock ceiling:** restoration is best-effort; a hung/uncooperative desktop may still leave
  the fallback un-activated. Documented, not fixed.
- **Headless CI:** the new behavioral tests are Desktop-only; verified at a console smoke, mirroring the
  v0.11.1 validation.

## 5. Acceptance
- `desktop_window_transform minimize` on the foreground window: keyboard focus lands on a valid fallback
  window (console smoke, same protocol as v0.11.1: type → minimize → keyboard survives).
- `desktop_focus_window` reports whether it gained foreground.
- Shared `SessionForegroundGuard` used by both close and minimize; `PickForegroundFallback` unchanged.
- Headless suite still green; new Desktop tests pass at console.
