# AGY-CAPSTONE ledger

One row per capstone run, recorded before the plan or implementation is declared complete. Evidence must
be independently checkable — the fold commits, or the review transcript. `none` is not a permitted value:
a clean first round still produces a transcript.

| date | range | rounds | verdict | evidence |
|---|---|---|---|---|
| 2026-08-23 | `92e6f35..6e3f95e` (`release.ps1` `-NoPush`) | 6 | GREEN — human-adjudicated | fold commits `cca1cc5`, `a336086`, `638723e`, `2f0890a`, `a3c4dbf`, `6e3f95e`; seam briefs (one per round) at `.clavity/seams/release-nopush-capstone.md`; measurement probes at `.clavity/scratch/release-nopush/` |
| 2026-08-23 | `125473c..0970c90` (`release.ps1` changelog-prompt budget) | 5 | GREEN — human-adjudicated | fold commits `9cddaaa`, `dd5e99c`, `e278a07`, `6f393fa`, `0970c90`; seam briefs (one per round) at `.clavity/seams/release-prompt-budget-capstone.md`; measurement probes at `.clavity/scratch/release-nopush/` |

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

## 2026-08-23 — the changelog-prompt budget (`release.ps1`)

**15 findings folded, 2 refuted by measurement, 5 mutations that survived the suite and now do not.**
Suite 108 → 133 tests. Ran because cutting v1.0.0 was impossible: `claude -p` refused the draft at
~236,523 tokens against a 200,000 limit. Same range now measures ~17,730 and the drafter returns a body
that the extractor accepts.

**The defect class that dominated, and how it finally closed.** Four of the five rounds found the same
shape — *a guard applied to one member of a category, with the category never enumerated*:

- the stat was bounded; the commit list and style exemplar were not (round 3);
- the three budgets this branch added were validated; the two original thresholds were not (round 4);
- the diff and commit list were named as untrusted; the exemplar, equally repo-derived, was not (round 4);
- the surrogate-safe cut existed twice and only one copy was asserted (round 5).

Rounds 3 and 4 fixed instances. Round 5 fixed it structurally by extracting one `Limit-TextToBudget` and
gating that the implementation stays unique — **there is no longer a set to enumerate.** Deriving each
round's seats from the previous round's defect class, rather than rotating generic personas, is what found
it three times running.

**Two lessons about the reviewer's own tests, both earned the hard way:**

- **Five gates were vacuous, every one caught by a mutant rather than by reading:** `Should -Throw` alone
  where the buggy path also throws · `Find` where `FindAll` was needed to count · block text whose
  COMMENTS named the token it forbade · a fixed 400-character window that matched a heading outside its
  subject · an attribute's PRESENCE instead of the range it enforces.
- **A mutant that fails to apply reports "survived" having changed nothing.** It happened twice — a stale
  anchor, then a `\r\n` literal that had `\n → \r\n` applied to it. The runner now verifies the apply
  succeeded before believing a survivor.

**What the review uncovered that was bigger than the branch:** the drafter is an **agent with filesystem
access**, not a text completion (`claude -p --safe-mode` read `ROADMAP.md` on request; a prompt of 4,000
synthetic commit subjects produced a changelog describing the real work). Filed as **ROADMAP 26** — a fork
for the operator, not a fix. It also invalidated a qwen-versus-claude comparison run earlier the same day.
