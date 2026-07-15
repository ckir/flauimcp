# flaui-autotrain — design review history (appendix)

Provenance archive for `2026-07-15-flaui-mcp-autotrain-design.md`. **Not needed to implement** — the
actionable spec stands alone. Kept for the "why" behind the load-bearing decisions.

## A. The Fork C/E negotiation (fix-the-tool route)

1. **agy challenged** the backlog-file-first lean: in a repo you own, a markdown file is easily ignored;
   prefer turning a deterministic flaw into a failing test (Test-Driven Curation). It named the **single
   biggest risk** — the *Workaround Trap* (institutionalizing a driver workaround in the SKILL.md instead of
   fixing the C# defect) — and its mitigation, an aggressive "is this a driver adaptation to a fixable
   defect?" classifier.
2. **Negotiation.** Claude raised three reservations: (1) console-only quirks can't run headless; (2)
   hallucinated auto-generated tests are toxic; (3) the classifier, not test-primacy, carries the
   anti-workaround load. agy **folded on (2)**, **held on (1)/(3)** with a sharper mechanism: don't use
   `Skip` — use `[Trait("Category","Desktop")]` + CI `--filter "Category!=Desktop"` so the test is *runnable
   on demand*, not permanently skipped.
3. **Closing amendment.** A plain `[Fact]` for a *headless* live defect would break the green CI gate
   (Desktop tests dodge CI only because CI already filters them out). Fix: apply agy's CI-filter trick to the
   headless case via a new `Category=KnownDefect` trait. agy confirmed.
4. **Consumer-review revision.** The Confidence-Gate/markdown-only outcome was later replaced by the
   always-emit test-skeleton (`NotImplementedException` body) — see §C (C3), which dissolved this fork's
   remaining tension entirely.

(agy volunteered an NUnit `[Ignore(...)]` attribute; the repo is **xUnit**, so the mechanism is
`[Fact(Skip=…)]`/`[Trait(…)]` — a reminder to verify framework specifics the peer states in passing.)

## B. Adversarial panel review (2 rounds → GREEN)

Round 1 = solo panel (`relentless-adversarial-auditor`) + agy escalation.

| # | Sev | Finding | Source | Fold |
|---|---|---|---|---|
| H1 | high | Curate freely edits the 300+-line hand-authored SKILL.md — no bounded region. | solo (Axiom) | machine-owned `AUTOTRAIN:GROWTH` region, regenerated wholesale; floor untouched. |
| H2 | high | "Empty the inbox" destroys a 1-observation heuristic before ≥2 → never promotes; + non-idempotent drain + capture-during-drain race. | agy (Cascade, top) + solo | Finish: remove only fully-processed entries; accumulators stay; exact-line removal ⇒ idempotent + race-safe. |
| H3 | high | Self-judged Confidence Gate + Workaround classifier gameable (misclassify a C# bug as "OS anomaly"). | solo + agy (Mechanism Gamer) | mechanical two-block routing gate (Steps-to-Reproduce **and** Code-level Mitigation). |
| M1 | med | Offline curate can't run a console-only probe → promotion stalls. | agy (Axiom) | console-tier assumptions promote only if pre-stamped `verified live`; curate never blocks. |
| M2 | med | Testless backlog entry has no trait to strip → retirement dead-ends. | agy (Cascade) | *Superseded by C3* — every entry is now test-backed (skeleton), so no dead-end. |
| M3 | med | Untrusted dogfooded app content flows capture → SKILL.md (injection). | solo + agy (Boundary) | capture hygiene + anti-poisoning circuit-breaker. |
| M4 | med | `flaui-learn` collides with the already-active `agy-learn`. | solo (Activation) | hard-scope frontmatter to flaui-mcp/`desktop_*`; separate loops. |
| M5 | med | Hooks: undefined thresholds, bash date-parsing, SessionStart hijack, unpinned invocation. | solo + agy | dumb hooks, `${CLAUDE_PROJECT_DIR}`+bash pinned, non-hijacking nudge. |
| L1 | low | `nature` conflated OS-behavior with our-defect. | solo (Axiom) | `nature` = reproducibility; `audience` = ownership. |
| L2 | low | `KnownDefect` magic string had no single source of truth. | solo (Protocol) | canonical-spelling note. |
| L3 | — | `driving-notes` memory path/format unspecified. | solo (Literal) | Superseded by C4 — memory removed entirely. |

Round 2 (solo, fresh seats Resource Vampire + Blindspot Auditor) found one substantive fold-introduced bug —
wholesale GROWTH regeneration would delete prior wisdom unless based on the *existing* GROWTH content (folded)
— plus enhancement-only residue. Severity floor → **GREEN** (no round-3 escalation).

## C. Consumer-ergonomics review (agy + Claude as executing agents)

*Would we actually run this loop mid-work, or quietly skip it?* Five changes, both agents agreed:

| # | Operator dread | Change |
|---|---|---|
| C1 | 3-field schema + tags is too much ceremony live → capture skipped & forgotten. | capture = zero-friction raw line; curate tags offline. |
| C2 | Draining the whole inbox in one turn → timeout / abandonment. | curate strictly incremental (≤3/run). |
| C3 | Nobody writes a correct UI xUnit repro offline → Confidence Gate abused ~95%. | always emit a failing beacon; low-confidence → `NotImplementedException` skeleton. Dissolves M2. |
| C4 | A side `driving-notes` memory is a dead surface nobody recalls. | removed — single surface (SKILL.md GROWTH). Narrows Fork B. |
| C5 | `SessionStart` curate-nudge hijacks the fresh prompt. | nudge → `Stop` (task-boundary), self-throttled once/session. |

## D2. Second adversarial panel — on the edited spec (agy escalation)

Run after the consumer + efficiency edits. agy independently caught 5 defects the edits introduced (and
notably **reversed its own** earlier `NotImplementedException` idea):
- **Stagnant Puddle / Schrödinger's Threshold** (highest-sev): raw entries lack a state marker, so incremental
  ≤3 re-reads sub-threshold orphans forever + can't cluster globally while chunking locally. → **two-section
  inbox** (`## Pending` raw + `## Accumulating` counted singletons); clustering scoped to Accumulating.
- **Mandatory Amnesia:** hard cap + killed memory ⇒ evicted proven rule evaporates. → eviction order
  compress → graduate-to-floor → drop-only-if-low-leverage; never silent.
- **TODO-Test Fallacy:** blind-throw skeleton asserts nothing. → three tiers (real / known-trigger partial
  repro / honest markdown-only); "nothing rots" softened to "visible + routed, not self-healing".
- **Stateless Bash Blindness:** sentinel needs `session_id` from hook stdin JSON; per-session gitignored file.

Disposition: all 5 folded; stopped at the round cap to hand back to the user (continue-or-ship).

## D3. Curate simplification → panel GREEN (rounds 4–5)

Round 4 (agy) found 5 more edge-cases in the two-section/parked state machine (all in the same hotspot).
Rather than patch again, curate was **radically simplified**: the mechanical ≥2-sighting counter (redundant
with the anti-poisoning judgment gate) was removed, collapsing the retained-state machine entirely →
**flat `## Pending` inbox that monotonically drains; terminal promote/route/drop decision per entry by
judgment; verification is a non-blocking `(unverified)` marker, not a gate.** Round 5 (agy, on the simplified
spec) = **GREEN clean confirm — "ship it."** Residual non-blocking notes: LLM-laziness (accepted trade — a
lazy curate that over-drops beats a broken state machine); one §3.3 "stamp" holdover (fixed). This is the
shipping design.

## D. Context-efficiency review (agy + Claude)

Both consumer agents, on token cost: hard `MAX_GROWTH_LINES ≈ 30` cap (soft "distil later" is an LLM
failure mode); `flaui-learn` SKILL.md ≤ ~10 lines; fold `verify.md` into the curate skill (one fewer file);
strip this process-history out of the actionable spec (this appendix). Do **not** compress the curate
triage matrix / Workaround-Trap classifier / `[Trait]` rules — that procedural logic must stay explicit.
