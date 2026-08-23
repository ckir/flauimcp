# AGY-CAPSTONE ledger

One row per capstone run, recorded before the plan or implementation is declared complete. Evidence must
be independently checkable — the fold commits, or the review transcript. `none` is not a permitted value:
a clean first round still produces a transcript.

| date | range | rounds | verdict | evidence |
|---|---|---|---|---|
| 2026-08-23 | `92e6f35..6e3f95e` (`release.ps1` `-NoPush`) | 6 | GREEN — human-adjudicated | fold commits `cca1cc5`, `a336086`, `638723e`, `2f0890a`, `a3c4dbf`, `6e3f95e`; seam briefs (one per round) at `.clavity/seams/release-nopush-capstone.md`; measurement probes at `.clavity/scratch/release-nopush/` |
| 2026-08-23 | `125473c..0970c90` (`release.ps1` changelog-prompt budget) | 5 | GREEN — human-adjudicated | fold commits `9cddaaa`, `dd5e99c`, `e278a07`, `6f393fa`, `0970c90`; seam briefs (one per round) at `.clavity/seams/release-prompt-budget-capstone.md`; measurement probes at `.clavity/scratch/release-nopush/` |
| 2026-08-23 | `aa62813..0f93bf9` (adopt Pester 6.1.0) | 4 | WAIVED — round-cap, operator adjudicated “merge” | fold commits `a9b745a`, `aebf5a8`, `4a979d7`, `0f93bf9`; seam briefs at `.clavity/seams/pester6-capstone*.md` (rounds 2-4 run via `qwen -p`); mutation probes at `.clavity/scratch/pester6/` |

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
| 2026-08-23 | `4c69d52..a3fd8e4` (tool descriptions state their lease posture) | 2 | ROUND 1 GREEN after one fold; **round 2 peer half SKIPPED-UNREACHABLE (quota)** — merged on operator adjudication | fold commit `a3fd8e4`; briefs at `.clavity/seams/lease-posture-capstone.md` and `-r2.md`; mutation probes at `.clavity/scratch/lease-posture/`; skip logged in `.clavity/agy-marks/skipped.log` |

## 2026-08-23 — adopting Pester 6.1.0 (WAIVED, not GREEN)

**Do not cite this row as capstone-green.** Four rounds ran; every one found a real defect and every
finding was folded, but the operator adjudicated **merge** rather than a fifth round. Round 4's fix,
`0f93bf9`, was therefore **never seen by a peer** — it is verified by driver measurement only (both scans
share the stripper, the false positives are gone, all three real defects are still caught, `total=140`
checked deliberately rather than inferred).

**All four findings were one defect class: the safety net's model of what the text SAYS never matched what
the text IS**, differently each round.

| round | what the scan could not see |
|---|---|
| 1 | anything after a **quoted argument** — the arg scraper stopped at the first quote |
| 2 | any version reference that was not `-RequiredVersion` — 4 **bare** mentions were invisible |
| 3 | **comments**, in the version scan — read as pins, so ordinary prose failed the gate |
| 4 | **comments, in the SWITCH scan** — the sibling scan, eight lines away, still read raw text |

**Round 4 is the sharpest lesson, and it is sharper than it first looked: fixing one scan and not its
sibling IN THE SAME FILE is how the class survives a fix — and round 4's own fix did exactly that,
in the opposite direction.**

⚠⚠ **CORRECTION, 2026-08-23, after this row was first written.** The sentence that stood here said
*"both now read through one shared stripper."* **That was FALSE.** Round 3 (`4a979d7`) added
comment-stripping to the VERSION scan; round 4 (`0f93bf9`) added it to the SWITCH scan and, in the same
edit, **took it back off the version scan** — leaving a correct stripper that the version scan no longer
called. The suite stayed green through all of it, because none of the three scanned files happened to
name another version in a comment.

It was caught by a mutant, not by reading: adding a fourth scanned file (`justfile`) and planting a
version in a COMMENT turned the gate red when it should have stayed green. Fixed by extracting one
`Get-PesterVersionsNamed` that both the live scan and its own unit tests call, plus a **wiring contract**
— every raw read of a live file must pass through something that strips comments — which is the only
guard that would have caught this. Proven by a mutant that reproduces the regression exactly.

**The lesson to carry: a fold that MOVES a fix is indistinguishable from one that ADDS it, if the only
evidence is a green suite.**

**What the adoption itself turned up, and what a naive pin bump would have shipped:** Pester 6 **removed
`-EnableExit`**. Both cockpit gates used it, and it was **measured** to make a *passing* suite exit 1 —
red on green. The gates now set `Run.Exit` on a configuration object rather than `-CI`, which would also
have written `testResults.xml` into the tree. The suite itself needed no changes.

**A failure mode worth remembering past this branch:** comment-based help containing the block-comment
closing delimiter made `pester-pin.Tests.ps1` un-parseable. Three tests vanished and the run reported
**“137 passed, 0 failed”**. Measured afterwards: the **gate is fine** — `Run.Exit` still returns 1 — but
**`FailedCount` is what lies**. Check the file and test counts, not just the failure count.

**Rounds 2-4 were run through `qwen -p`, not the usual peer**, and it found real defects the usual peer
missed. Its verdicts still require measurement before folding — see the reviewer evaluation: recall 2/2 on
a planted defect, precision 0/2 on the clean one.

## 2026-08-23 — tool descriptions must state their own lease posture

**Not a full green: round 1 was clean after one fold, round 2's peer half never ran** — agy's quota was
exhausted, which is logged as `SKIPPED-UNREACHABLE` with **no completion marker**, so the gate re-arms.
The operator adjudicated merge on round 1 plus the second reviewer's round 2. Do not cite this as
capstone-green.

**The one real finding was one I could not have found by reading my own work.** The invariant I added
required a description to *contain the word* "lease". `desktop_click` satisfied it while ending with a
dangling **"Same lease/deny-list/session gates."** — same as what? — and `desktop_key` delegated its
posture to `desktop_type`. Both passed the gate and told a caller nothing. **A gate that accepts a
MENTION does not enforce KNOWLEDGE.** It matters more than it looks because tool schemas are DEFERRED: a
subagent routinely loads one tool without its neighbours, so a cross-reference to another tool's
description is not an answer. Folded by making both explicit and by tightening the gate to match one of
two canonical postures, proven with a mutant that restores agy's exact wording.

**The other finding was refuted, and the refutation is worth keeping.** agy read `desktop_set_caret`'s
*"needs NO input lease"* as a lie, because `CheckTarget` consults the lease for the `shells` capability on
interlocked sinks. But that description already names the carve-out verbatim — *"interlocked shells need
the 'shells' lease cap"* — and `CheckTarget` consults no lease at all for a non-interlocked target. **agy
quoted the clause that resolved the contradiction it was alleging.**

**My own scoping was the weakest instrument in the exercise.** A regex sweep over source found 9 tools
lacking a posture; the reflected test then found **8 more** that my sweep had structurally excluded,
because it only flagged tools missing BOTH clauses and every tool that named `--read-only-mode` while
omitting its lease posture was invisible to it. **A regex over source is not an oracle.** Final split,
measured: 17 no-lease / 6 lease-requiring, all 23 traced to their code path.

**Proven on the wire, not only by unit test.** With the server restarted, `ToolSearch` on
`desktop_toggle`, `desktop_click` and `desktop_close_window` returned descriptions carrying the new
posture verbatim — loading a tool BY NAME now carries its rules with it, with no server instructions
involved, which is exactly the subagent path this work exists to serve.

**Second-reviewer note (3rd consecutive datapoint).** The second reviewer returned "nothing above the
floor" both rounds. In round 1 its lens answer contained agy's real finding — *"a description could game
the gate by including the word 'lease' with the wrong meaning"* — and dismissed it as out of scope. In
round 2 it reached the correct conclusion through a wrong premise, reporting the split as 15/8 and placing
`desktop_set_caret` and `desktop_select_text_range` in the "requires a lease" bucket when both claim the
opposite. **Read its reasoning; never its verdict.**
