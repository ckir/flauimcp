# AGY-CAPSTONE ledger

One row per capstone run, recorded before the plan or implementation is declared complete. Evidence must
be independently checkable — the fold commits, or the review transcript. `none` is not a permitted value:
a clean first round still produces a transcript.

| date | range | rounds | verdict | evidence |
|---|---|---|---|---|
| 2026-08-23 | `92e6f35..6e3f95e` (`release.ps1` `-NoPush`) | 6 | GREEN — human-adjudicated | fold commits `cca1cc5`, `a336086`, `638723e`, `2f0890a`, `a3c4dbf`, `6e3f95e`; seam briefs (one per round) at `.clavity/seams/release-nopush-capstone.md`; measurement probes at `.clavity/scratch/release-nopush/` |

## 2026-08-23 — `-NoPush` on the release orchestrator

**16 findings folded, 1 rejected by measurement, 7 mutations that survived the suite and now do not.**
Suite 101 → 108 tests. Every finding verified by measurement before folding; every gate proved with a
logic mutant, red at its own assertion.

The method that produced the best findings, and the reason this ran to six rounds:

> **Ask the peer for a mutation the suite SURVIVES, not for an opinion about coverage.** A seat given
> *"state an exact edit that changes real behaviour and that all N tests survive; I will apply it"*
> returned a confirmed survivor in **all three rounds it was seated**. The peer generates coverage
> hypotheses well and judges coverage facts badly, because it cannot run the suite — so ask for the
> falsifiable artefact and run it yourself.

The three worst, each measured rather than reasoned:

- **R4 — polarity.** `if ($NoPush)` → `if (-not $NoPush)` survived all 104 tests, on *both* guards. That
  inverts the safety switch: `-NoPush` would publish and an ordinary run would not. Every assertion
  checked that the condition *mentions* `$NoPush`, and `-not $NoPush` contains `$NoPush`.
- **R6 — termination.** `exit 0` → `return` survived all 106 tests. `return` only leaves the function, so
  control falls back into the script body and cuts a **new release on top of the half-finished one**.
- **R1 — the adjacent path.** `-NoPush` governed only one of the script's two push sites; the other still
  offered `[P]ush the existing commit+tag now` on a run invoked with `-NoPush`.

Recurring lessons worth carrying to the next capstone:

- **A green verdict is not the whole report.** Round 3's seats all returned "clean, stop" while the real
  defect sat in the BELOW-FLOOR list. Read the lens answers and the below-floor list first.
- **A fold spawns the next defect.** Rounds 2-6 each found something created by the previous round's fix.
  Round 6's was caught by the driver's own gates rather than the peer.
- **When a gate fires on a benign change, fix what the gate MEANS.** Round 6's `-WhatIf` fix added a
  second `if ($NoPush)`; the gates were right to fire and wrong in what they said, and were re-aimed at
  "an `if ($NoPush)` that GOVERNS A PUSH" rather than loosened.
- **The peer's line numbers were wrong in every round that produced one**, while the content was right.
  Keep the direction, re-derive the specifics.
