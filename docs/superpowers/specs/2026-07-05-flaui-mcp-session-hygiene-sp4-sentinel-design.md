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

## 6. Outcome — RETIRED (2026-07-05)

**The trigger (§2) was tested and did NOT fire → SP4 is DROPPED.** Rather than build the full SP3 chaos
harness to answer the one gating question, a **disposable spike** interrogated it directly (user's call;
agy-concurred): a throwaway console probe referencing `WindowManager` drove the *real* shipped hygiene
paths and, for **100 operations (50× `CloseAsync`, 50× `WindowTransformAsync(minimize)`)**, sampled
`GetForegroundWindow()` every 25 ms across a **1500 ms observation window AFTER each tool returned** —
precisely the async-re-orphan the Sentinel exists to catch (a target that dies ~50 ms late and
re-orphans focus).

**Result: zero async re-orphans. Not one settled-orphan final state, and not a single transient orphan
sample, across all 100 trials (0 skipped).** For **fast-dying targets** the SP2 synchronous
spin-wait-for-collapse healer holds and no residual asynchronous orphan reproduces.

**KNOWN BLIND SPOT — the probe did not exercise SP2's actual defeat condition (AGY-AFTER, folded):**
`charmap` destroys its HWND almost instantly — well within SP2's **500 ms** spin-wait
(`RestoreForegroundAfterCollapse`, `deadline = UtcNow.AddMilliseconds(500)`). So these 100 trials proved
the *common, fast-teardown* case is clean but structurally **could not** reproduce the one scenario the
Sentinel exists for: an app whose HWND takes **> ~500 ms** to be destroyed on **close**. There the
spin-wait expires while the closing window still holds the foreground-lock; the healer's
`SetForegroundWindow(fallback)` can then silently no-op, and when the window finally dies the OS's
(unreliable, background-process-gated) auto-activation *can* leak an orphan — the exact async residual
SP4 targets. This is a **real, unmitigated blind spot**, not a proven-safe path. (Minimize is
near-instant — `SetWindowVisualState` — so the >500 ms concern is specific to slow-closing apps, e.g. a
heavy Electron app or one doing a long shutdown; an app that pops a "save changes?" dialog is a
*different* case — the close simply doesn't complete — not this leak.)

**Honest scope of the evidence (not overclaimed):** 100 trials against one fast-dying app (`charmap`) at
one console with the driver idle. *Strong empirical evidence* that the async residual does not occur for
ordinary fast-teardown apps — **not** a proof across app types, and explicitly silent on the >500 ms
slow-close blind spot above.

**Decision — SP4 RETIRED on YAGNI, blind spot on record.** Retiring is still the right call: the leak is
unobserved in practice and speculatively building a lease-less Sentinel (with its §3 safety tension) for
it is the YAGNI violation this program set out to avoid. **Reopen ONLY if a real field report shows the
session re-orphaning *after* a tool returned** — the >500 ms slow-close path above is the *named,
expected* trigger for that report. If certainty is ever wanted without waiting for the field, the cheaper
follow-up is not SP4 but a targeted slow-teardown probe, or simply lifting SP2's 500 ms spin-wait ceiling
for close. The probe was throwaway and has been deleted (never committed to the repo).

**Program close-out:** SP1 (audit) ✅ · SP2 (fix the one gap) ✅ shipped v0.11.2 + console-smoked ·
SP3 (chaos harness) remains an on-the-shelf design, unbuilt (YAGNI — its trigger, a second hygiene gap,
never fired) · SP4 (Sentinel) RETIRED here. The session-hygiene effort ends.
