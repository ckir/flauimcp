# FlaUI.Mcp — Session-Hygiene Invariant (SP1: audit + contract) — Design

**Date:** 2026-07-05
**Status:** Draft (SP1 of a staged effort)
**Origin:** Consumer-driven. While driving the server, the agent (Claude, the primary consumer)
identified that the scariest failure class for a "hands" tool is **collateral damage to the human's
live session** — the just-shipped v0.11.1 fix (closing the foreground window orphaned the human's
physical keyboard) is one instance of a broader class.

## 1. Problem & invariant

**Invariant (the goal):** *No flaui-mcp tool leaves the human's interactive session — foreground
window, keyboard focus, and input-teardown state (no stranded modifier key) — worse than it found it,
except for the specific, declared change that is the tool's purpose.*

Two properties make this hard, and both are load-bearing constraints on the design:

- **It is a BEHAVIORAL property, not a static one.** "Focus was restored", "no modifier left down",
  "foreground not stolen" cannot be detected by reflecting over the tool surface (unlike the existing
  `ToolReadOnlyInvariantTests`, which statically fences every mutating tool through `GuardWrite`).
- **Some tools change foreground/session state ON PURPOSE.** `desktop_focus_window` and
  `desktop_launch_app` are *supposed* to change the foreground. "Leave it as you found it" is wrong for
  them. The invariant therefore needs a **per-tool policy**, not a blanket rule.

**Hard ceiling (must be stated, not papered over):** `SetForegroundWindow` from a *background* process
is gated by the Windows foreground-lock — the same lock that caused the original bug. No mechanism in
this design can *guarantee* foreground restoration; the lock only relaxes at specific moments (e.g. the
previously-foreground window is destroyed). Every restoration/rollback in this effort is therefore
**best-effort and self-healing**, never a hard guarantee. Designs that assume guaranteed rollback
(e.g. a transactional "COMMIT/ROLLBACK the foreground" middleware) are rejected on this basis.

## 2. Scope

This is **SP1** of a staged effort. SP1 delivers the *audit*, the *invariant*, and the *declarative
session-delta contract* — the forward-writable design artifact. It does **not** itself change tool
behavior (that is SP2, sized by SP1's findings).

Staged roadmap:

- **SP1 (this spec):** Audit every session-touching tool; define the session-delta contract; produce
  the findings table.
- **SP2 (fix the gaps):** Harden the tools the audit flags, via a shared healer primitive
  (generalize `WindowManager.RestoreForegroundAfterClose`), each honoring its declared delta.
- **SP3 (prove it):** A Desktop-category snapshot+chaos test harness that asserts each tool's *actual*
  session delta ⊆ its *declared* delta. **Runs on a real console only** — the dev/CI box is headless
  RDP-only, so this can never be a CI build gate; it is a console/smoke gate.

  **SP2/SP3 interleave (AGY-AFTER panel, release-ops seat — TDD):** do not build all of SP2 before any
  of SP3. The `SessionStateSnapshot` + `AssertWithinDeclared` *core* of SP3 lands **with SP2**, used to
  RED-test the minimize orphan first (mirroring the v0.11.1 console repro), which SP2 then turns green.
  SP3's *chaos tier* (the `DesktopChaosMonkey` stress layer + full per-tool coverage) follows. This
  keeps the TDD discipline without blocking a **live** keyboard-orphan fix behind the whole test-infra
  build.
- **SP4 (parked, safety-gated):** An optional post-lease-release "Session Sentinel" that autonomously
  heals *unambiguous* agent-caused damage (foreground is null / a modifier is down that no human
  pressed). **Parked** until the audit proves it is needed — it acts on the human's session with no
  lease held, which is in tension with the product's core promise, so it must clear a safety bar first.

**Non-goals (YAGNI):** caret/selection restoration; clipboard hygiene beyond what `desktop_paste_text`
already does; a general AOP transaction/rollback middleware; the ergonomics footguns
(refs-tree-order, RDP-can't-drive-synthetic surfacing) — real, but a separate follow-up.

## 3. The session-delta contract (SP1's deliverable design)

Each mutating tool declares the session-state changes it is *allowed* to leave behind. This encodes the
per-tool policy in one place, doubles as documentation, and (in SP3) becomes the oracle the chaos
harness asserts against. Dimensions of a session delta:

| Dimension | Values | Meaning |
|---|---|---|
| `Foreground` | `Preserve` \| `MayChange` \| `MustRestoreOnCollapse` | Preserve = must end on the same foreground it started; MayChange = changing foreground IS the purpose; MustRestoreOnCollapse = if this tool *destroys/minimizes* the foreground window it must hand foreground to a valid fallback. |
| `Modifiers` | `Preserve` | No synthetic modifier key may be left in the down state on any exit path (incl. abort/exception). |
| `Clipboard` | `Preserve` \| `MayChange` | Preserve = must restore any borrowed clipboard; MayChange = overwriting is the purpose. |

The contract is expressed as declarative metadata attached to each tool (exact encoding — attribute vs.
a registry table — is a **plan-level** decision, deferred to writing-plans; the encoding must be
readable by the SP3 harness).

## 4. Audit findings (grounded against current `src/`)

Classification of every session-touching site found by sweeping `.Focus()`, `.SetForeground()`,
`SetForegroundWindow`, `.Close()`, `SetWindowVisualState`, `KEYEVENTF_KEYUP`, and clipboard writes:

| Tool / site | File | Declared delta | Status |
|---|---|---|---|
| `desktop_close_window` | `WindowManager.CloseAsync` | Foreground = `MustRestoreOnCollapse` | ✅ **Fixed (v0.11.1)** — restores foreground after closing the foreground window. |
| **`desktop_window_transform` (minimize)** | `Interactor` `SetWindowVisualState(Minimized)` | Foreground = `MustRestoreOnCollapse` | 🔴 **GAP — the twin of the close bug.** Minimizing the *foreground* window orphans keyboard focus the same way closing it did; no restoration today. **Primary SP2 fix.** |
| `desktop_window_transform` (maximize / restore) | `Interactor` `SetWindowVisualState` Normal/Maximized | Foreground = `MayChange` | ✅ OK — the window stays foreground; no orphan. |
| `desktop_focus_window` | `WindowManager.FocusAsync` (`w.Focus(); w.SetForeground()`) | Foreground = `MayChange` | 🟡 **Verify** — purpose is to change foreground, so no orphan; but `SetForeground` may *silently no-op* under foreground-lock with no signal to the agent. Low severity; SP2 should confirm/report success. |
| `desktop_launch_app` | `WindowManager.LaunchAppAsync` | Foreground = `MayChange` | ✅ OK by design (new window comes foreground). |
| `desktop_type` / `desktop_set_caret` / `desktop_select_text_range` | `InputTools` `el.Focus()` | Foreground = `MayChange`, Modifiers = `Preserve` | ✅ OK — focuses the target within its window (the purpose); no teardown residue. |
| `desktop_click` / `desktop_invoke` etc. | `Interactor` `el.Focus()` / Invoke | Foreground = `MayChange` | ✅ OK — focuses/acts on target; no orphan. |
| `desktop_key` (chord) | `Win32SyntheticInput.KeyChord` | Modifiers = `Preserve` | ✅ OK — modifier-down…key…key-up…modifier-up ship in ONE atomic `SendInput` batch; re-verify throws *before* any key goes down, so no partial/stranded modifier. **Invariant to pin, not a gap.** |
| `desktop_type` (paced) | `UnicodeKeyTyper` / `UnicodeKeyInput.Groups` | Modifiers = `Preserve` | ✅ OK — each char group is a balanced down+up atomic send; a mid-type abort leaves whole chars, never a half-pressed key. |
| `desktop_paste_text` | `PasteFlow` | Clipboard = `Preserve` | ✅ OK — already borrows+restores clipboard on confirmed consumption. |
| `desktop_clipboard_set` | `InputTools.SetClipboardAsync` | Clipboard = `MayChange` | ✅ OK by design (overwriting the clipboard is the purpose). |

**Audit verdict:** exactly **one live behavioral gap** — `desktop_window_transform minimize` orphaning
the foreground window — plus **one low-severity observability gap** (`focus_window` silent no-op). The
modifier-teardown and clipboard surfaces are already sound; SP2/SP3 *pin* them with the contract + tests
so a future tool cannot regress them, rather than fixing anything today.

## 5. What this means for SP2

- **Fix:** apply the `RestoreForegroundAfterClose` healer (generalized to a shared
  `SessionForegroundGuard.RestoreForegroundAfterCollapse(hwnd)`) to the minimize path when it minimizes
  the foreground window. Same mechanism: capture `wasForeground` before, spin-wait for the window to
  leave the foreground, hand foreground to the next Z-ordered top-level.
- **Observability:** have `focus_window` report whether it actually gained foreground (best-effort;
  surfaces the lock ceiling instead of hiding it).
- **Pin:** encode the declared deltas and add targeted behavioral tests for the two changed tools.

The exact healer signature, the minimize-vs-close difference (a minimized window is *not destroyed*, so
the "spin-wait for `!IsWindow`" tell differs — wait for it to leave foreground / go minimized instead),
and the contract encoding are **plan-level** and belong in writing-plans, authored against the real
code at that time.

## 6. Open decisions carried to the plan

1. **Healer generalization shape** — extract a shared `SessionForegroundGuard` vs. keep two call
   sites. (Lean: extract, since minimize + close now share it.)
2. **Minimize "collapsed" tell** — a minimized window still exists (`IsWindow` true) but is no longer a
   valid foreground; the wait condition differs from close's `!IsWindow`. Needs the real minimize path.
3. **Contract encoding** — attribute vs. central registry table (must be machine-readable for SP3).
4. **SP4 Sentinel go/no-go** — decide after SP2 whether any residual (async re-orphan after a tool
   returns) actually reproduces; only then justify the lease-less healer.
5. **Mouse-cursor dimension (AGY-AFTER R2, data-model seat)** — synthetic click moves the *physical*
   cursor (`SendInput` ABSOLUTE|MOVE) and leaves it there; that is a real session mutation not in the
   current 3 dimensions. Deferred candidate: whether to add a `Mouse` dimension (`GetCursorPos` capture +
   restore) is genuinely debatable — many automation users *expect* the cursor to move — so it is
   recorded, not adopted. Revisit if it proves disruptive.
6. **Fail-closed contract enforcement (AGY-AFTER R2, maintainability seat)** — IF the declarative delta
   contract is built, a new tool that omits the metadata must FAIL the build, not silently bypass
   hygiene. Add a reflection test (alongside `ToolReadOnlyInvariantTests`) asserting every mutating tool
   type carries a declared delta. (Contingent on decision #7.)

## 7. THE SCOPE DECISION (AGY-AFTER R2, cost/scope seat — CHALLENGE-RIGHT; user decides)

The panel's sharpest challenge, which I (the author) largely agree with: the declarative delta contract
+ chaos harness may be **over-engineered for what SP1 actually found** — exactly ONE live behavioral gap
(`minimize` orphans the foreground) plus one low-severity observability gap. Two paths:

- **(A) Minimal / YAGNI (recommended by the skeptic seat and the author):** extract the shared
  `SessionForegroundGuard`, fix `minimize` with it, add `focus_window` observability, and write ONE
  targeted Desktop test (`Minimize_ForegroundWindow_RestoresFocus`, mirroring the v0.11.1 smoke). Ship
  it as a small hotfix (~v0.11.2). **Defer** the declarative contract, the chaos harness (SP3), and the
  Sentinel (SP4) until a *second* real hygiene gap materializes. SP1 remains as the durable audit
  record that says "one gap, now fixed; the rest is sound."
- **(B) Full staged (SP1→SP3, SP4 parked):** build the contract + harness now as an investment so
  future tools are held to the invariant automatically and can't regress it.

**Author's recommendation: (A).** The audit is the valuable artifact; it proved the surface is
overwhelmingly healthy. Building generalized enforcement infrastructure for a single edge case is the
YAGNI violation I flagged when scoping. Keep the contract/harness specs (SP2 §2.4, SP3) on the shelf as
a ready design, and pull them off it the moment a second gap appears. **User's call.**
