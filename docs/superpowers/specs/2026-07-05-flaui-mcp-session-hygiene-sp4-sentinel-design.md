# FlaUI.Mcp — Session-Hygiene SP4: Session Sentinel (PARKED / conditional) — Design

**Date:** 2026-07-05
**Status:** Draft — **PARKED. Conditional on an SP2 finding.** Do NOT build unless the trigger below fires.
**Sibling specs:** SP1 (audit), SP2 (harden), SP3 (chaos harness).

Design spec, not a plan. Written now (at the user's request to spec the whole staged shape) but
deliberately gated — this is the one piece with a real safety tension, so it must not be built
speculatively.

## 1. What it is
A low-frequency background watchdog (a hosted service, alongside the existing WatchPump/WakeService)
that autonomously *heals* an **unambiguously agent-caused, unambiguously broken** human session — the
"Desktop Immune System" idea from the AGY-FIRST divergent pass. It decouples the invariant from the
tools entirely: tools just mutate; the Sentinel restores the baseline if the session drops below it.

## 2. Why it is PARKED (the trigger gate)
The tool-level healer (SP2) fixes the *synchronous* orphan. A Sentinel is only justified if a
**residual asynchronous re-orphan** actually reproduces — e.g. a tool restores foreground and returns,
then the target process finishes dying ~50ms later and the OS re-orphans focus (the async-race the
AGY-FIRST pass flagged). 

**TRIGGER (build SP4 only if):** after SP2 ships, a console repro shows the session re-breaking *after*
a tool has already returned successfully (i.e. the synchronous healer is provably insufficient). If SP2
+ its spin-wait-for-collapse holds under the SP3 chaos harness, **SP4 is dropped**, not built.

## 3. The safety tension (the reason it can't be built casually)
The Sentinel acts on the human's session **with no input lease held** — the product's core promise is
"synthetic input only under a human-held lease". Firing `SetForegroundWindow`/`KeyUp` outside a lease is
a direct tension with that promise. It is only defensible under **all** of these guards:

1. **Unambiguous damage only — foreground-orphan ONLY.** Heal *only* the one state that is strictly
   worse-than-baseline and cannot be a legitimate human choice:
   - `GetForegroundWindow()` is `NULL` or a 0×0/invisible window (true orphan) — never when a real
     titled window is foreground (that might be the human's deliberate choice).
   - **Modifier-healing is DROPPED (AGY-AFTER panel, Win32-correctness seat).** `GetAsyncKeyState`/
     `GetKeyboardState` return the *merged* physical+synthetic keyboard state — there is no supported
     out-of-process way to distinguish a synthetic *stuck* modifier from the human *deliberately
     holding* one, so a Sentinel `KeyUp` would routinely corrupt real human input. Moreover SP1 already
     proved the synthetic teardown is atomic (`KeyChord`/paced type ship balanced down+up), so no
     stranded modifier is produced in the first place. The Sentinel therefore never touches the
     keyboard — foreground restoration only.
2. **Agent-attributable window only.** Heal an orphan only within a bounded interval after the agent's
   last action (the agent plausibly caused it); do not "fix" foreground the human orphaned themselves.
3. **Restorative, not directive.** Healing actions must be idempotent and only ever *restore* a valid
   baseline (activate a real top-level; release a stuck key) — never open, type, click, or navigate.
4. **Audited.** Every Sentinel heal emits an audit line (like synthetic input does), so the human can
   see exactly what the immune system did.
5. **Off by default / opt-in**, mirroring the overlay posture — it is a safety-net, not a default
   behavior, and never runs headless.

## 4. Non-goals
- Not a general watchdog for arbitrary desktop state — only the three invariant dimensions.
- Not a replacement for SP2's synchronous healer — a complement that only earns its place if the async
  residual is real.
- No clipboard healing (the Sentinel does not own the clipboard; `desktop_paste_text` already restores).

## 5. Decision record
This spec exists so the design is on record, but the **default outcome is DROP**. Reopen only on the
SP2 trigger. The user retains the final call; if the async residual never reproduces, SP4 stays parked
permanently and the effort ends at SP3.
