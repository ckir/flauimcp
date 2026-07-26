# Agent adoption — make reaching for the tool the default move

**Status:** design, approved 2026-07-26 (user delegated approval to the driver as the consuming agent).
**Motivating ROADMAP items:** AA1 (activation) + AA2 (skill-freshness). B4 explicitly out of scope.

## 1. Problem

The product is installed, complete, and dogfooded — and the consuming agent does not use it.

Observed live, 2026-07-26: a capable consuming agent (Claude) had the server installed and a 340-line
driving skill available, and still (a) asked the **human** to eyeball another app's console instead of
looking itself, and (b) when it did drive, gave up on an already-solved problem (finding a CLI agent in
a generic-titled Windows Terminal tab).

The user's reframe, which supersedes the ROADMAP's framing:

> *"The problem I'm trying to resolve is that you have installed a powerful tool and you never use it."*

And the correction that sets the scope:

> *"It's not only look. There are many other tools."*

So this is not a perception problem. Non-use spans the surface — perceiving screen state, clicking
dialogs, typing into apps, reading background consoles. The general anti-pattern that covers all of it:

> **The agent is about to ask the human to LOOK AT, or OPERATE, a desktop application on its behalf.**

## 2. Consumer's diagnosis — why the agent doesn't reach for it

Authored by the consuming agent introspecting on its own behaviour. This is the evidence base for the
whole design; every mechanism below traces to a numbered item here.

1. **Deferred = invisible.** The `desktop_*` tools are never in the tool list — ~49 names inside a wall
   of ~120 deferred names, loadable only via `ToolSearch`. Recall-only, and recall needs a cue.
2. **The cue never fires.** At the decision point the thought is *"is X running?" / "what's on screen?"*.
   The trained reflexes for that are `Get-Process`, read a log, or **ask the human** — and asking the
   human feels socially normal, not like a failure. Nothing marks the moment as an error.
3. **The skill gates on the wrong verb.** `description: Use when driving or dogfooding this ... server`
   matches only once the agent has ALREADY decided to drive. Intent at the real decision moment is
   phrased as a QUESTION, never as the MECHANISM.
4. **Cost asymmetry.** A `ToolSearch` round-trip + a 340-line skill + a time-lease concept + destructive
   `SendInput` warnings, versus one cheap sentence to the human.
5. **No local norm.** In this repo especially, asking a human to eyeball a screen should be nearly
   unthinkable — the repo IS the eyes. Nothing encodes that.

Item 2 is the binding constraint (independently identified by the driver and by agy). Items 1, 4 and 5
are what make the wrong branch cheaper once the cue fails to fire; item 3 is what stops the recovery.

## 3. Measured ground truth

Each of these was measured, not inferred. Two of them killed a proposed approach.

**G1 — the product cannot un-defer itself.** The server exposes **49** `[McpServerTool]`s. Deferral is a
CLIENT-side decision driven by total tool volume across all connected servers. There is no MCP protocol
affordance to declare a tool eager, and no client setting controlling it was found in
`~/.claude/settings.json`. The only product-side lever is shrinking the 49-tool surface below an
undocumented client threshold shared with every other installed server. **Unactionable.**

**G2 — the failure is non-reading, not staleness.** The load-bearing trap (*"a terminal tab title is the
launcher, not the program — read every candidate"*) is in the skill's **shipped seed** at
`plugins/flaui-mcp/skills/driving-flaui-mcp/SKILL.md:252`, above and outside the machine-owned
`AUTOTRAIN:GROWTH` region (lines 314–340). The agent did not drive on a stale growth region — it did not
read the skill at all. **Any fix premised on "keep the skill fresher" aims at a cause that did not occur.**

**G3 — the documented load line is BROKEN, and fails at the worst possible moment.** `SKILL.md:25`
instructs:

```
ToolSearch "select:mcp__flaui-mcp__desktop_list_windows,...,mcp__flaui-mcp__desktop_input_status"
```

Under plugin registration the tools are actually named `mcp__plugin_flaui-mcp_flaui-mcp__desktop_*`.
Executed verbatim, that line returns **`No matching deferred tools found`** (measured this session).

Two candidate fixes were measured; the second wins:

- A prefix-agnostic **keyword** query (`ToolSearch "desktop list windows snapshot accessibility tree"`)
  resolves — but **imprecisely**: it returned `list_windows`, `snapshot`, `wake_accessibility` and
  `list_wakes`, and did **not** return `open_window` or `get_text`, which the read-only recipe needs.
  Rejected: a load line that silently omits recipe tools reproduces the first-contact failure.
- A **`select:` listing BOTH name forms** resolves correctly and precisely. Measured: `select:` silently
  ignores names that do not match and returns those that do, so a query naming both
  `mcp__flaui-mcp__desktop_get_text` and `mcp__plugin_flaui-mcp_flaui-mcp__desktop_get_text` returns
  exactly the right tool under either registration mode, with no noise. **This is the chosen form.**

This is the most damaging defect in the set, because of *where* it sits: the agent that overcomes every
other barrier and finally does the right thing is punished with a failure on first contact. It converts
one correct instinct into evidence that the tool doesn't work.

**G4 — the skill is registered TWICE, and BOTH copies are live.** Both
`.claude/skills/driving-flaui-mcp/SKILL.md` and `plugins/flaui-mcp/skills/driving-flaui-mcp/SKILL.md` are
git-tracked and currently byte-identical, despite `0369549` describing the `.claude/skills` dir as
retired. Measured: the consuming agent's available-skills list shows **two separately registered entries**
— bare `driving-flaui-mcp` (the `.claude/skills` copy) and `flaui-mcp:driving-flaui-mcp` (the plugin
copy). This is not a dormant leftover; it is a live duplicate that the agent can match either way.

This is the literal "two copies, one goes stale" hazard AA2 was written about, and it is currently only
latent because the two files happen to be identical. **Consequences for this spec:** every skill edit
below MUST land in both copies, and the plan MUST resolve the duplication outright (delete the retired
copy, or generate one from the other) rather than leaving two independently-editable live skills.

**G5 — `jq` is a real distribution dependency.** The existing hook shape pipes through `jq`, which
resolves on the maintainer's machine only via a portable toolchain (`/c/!PORTABLES/!BIN/jq`, 1.8.1 —
measured) and is **not** present on a stock Windows host. **User decision, 2026-07-26: `jq` is promoted
to a declared prerequisite** rather than engineered around — see §5.2.

## 4. Approaches considered

Design forks were negotiated with the agy peer across three rounds (AGY-FIRST), then decided by the
driver under explicit user delegation ("you are the consumer and expert").

| Approach | Verdict |
| --- | --- |
| **Prime, don't detect** — SessionStart norm + description rewrite + tool-description hoisting | **Adopted, upgraded.** agy's habituation critique forced the upgrade from *signpost* to *payload* (§5.2). |
| **Intercept the wrong move** — `Stop` hook scanning the agent's closing message for the anti-pattern | **Rejected** (§6.1). Proposed by the driver, killed by agy on two grounds the driver accepts. |
| **Un-defer the sense / shrink the surface** — agy's Round-1 headline | **Rejected** — G1 makes it unactionable; agy withdrew it, naming *Capability Amputation* as its failure mode. |
| **Hard interlock** — deny `Get-Process`/`AskUserQuestion` when desktop-shaped | **Rejected** — agy withdrew it, naming *Deadlock by False Positive*. `Get-Process` is legitimate in many non-desktop contexts. |

Findings folded from agy, in its own terms:

- **Commitment Lock-in.** A hook that fires *after* the agent emitted the ask cannot extinguish the
  behaviour; autoregressive consistency turns it into an "ask → nag → apologize → comply" tax loop.
- **Semantic Discard.** One false positive on a legitimate question teaches the agent to globally
  down-weight the hook as an unreliable context source, destroying it for the cases it would get right.
  **Therefore precision dominates recall for anything that inspects agent output.**
- **LLM banner blindness.** Invariant `SessionStart` preamble gets attention-down-weighted over
  repeated sessions. A *signpost* decays. This is why M1 injects a payload, not a reminder.
- **Cost-collapse beats interruption** (*"pave the road rather than build a wall"*): if the injected text
  carries the ready-to-run load line and recipe, taking the right path becomes computationally cheaper
  than generating the sentence that asks the human. This is the strongest single lever.

agy invoked CHALLENGE-RIGHT to argue no product-side fix was possible and it belonged in consumer
config. It **conceded** on two concrete objections: (O1) this product *ships* consumer config — it is a
Claude Code plugin that already wires hooks — so "agent config" and "product" are the same action here,
except the plugin version installs for every user; (O2) three candidate mechanisms need no text-parsing
of agent output and so have zero false-positive surface.

## 5. Chosen design

Four mechanisms. **None inspects agent output; all have zero false-positive surface.**

### 5.1 M0 — fix the broken load line (fixes G3)

Highest priority, smallest change. Replace the single-prefix `select:` form with a **dual-prefix
`select:`** listing both `mcp__flaui-mcp__desktop_<tool>` and
`mcp__plugin_flaui-mcp_flaui-mcp__desktop_<tool>` for each tool in the line, so it resolves under both
plugin and direct registration.

- Applies to **both** copies of `SKILL.md:25` (G4), and to the M1 payload (§5.2).
- Contract: the documented load line MUST resolve **every tool it names** under plugin registration —
  precision matters as much as resolution, per the keyword-form rejection in G3.
- Rationale: `select:` ignores unmatched names (measured), so naming both forms is registration-agnostic
  *without* the imprecision of a keyword query.
- The plan must state what happens if a THIRD registration prefix ever appears (e.g. a differently-named
  marketplace install). Recommended: treat the load line as a known-fragile surface and cover it with
  the acceptance check in §7 (criterion 1) rather than assuming two forms are exhaustive forever.

### 5.2 M1 — SessionStart payload injection (fixes 1, 4, 5)

New `SessionStart` entry in `plugins/flaui-mcp/hooks/hooks.json` → new
`plugins/flaui-mcp/scripts/flaui-activation.sh`, following the shape of the existing
`flaui-curate-nudge.sh` (stdin JSON → `jq -n` → `hookSpecificOutput.additionalContext`).

**It injects a payload, not a signpost.** Required content, in this order — the anti-pattern rule comes
FIRST because it is the genuinely novel instruction:

1. The negative norm: do not ask the human to look at or operate a desktop app; do not infer UI state
   from `Get-Process`.
2. The trigger list, covering the whole surface: see what's on screen · check an app is running/
   responding · read a background terminal or console tab · click/type/fill a dialog in a GUI app ·
   confirm a change landed in the real app.
3. The **ready-to-run load line** (keyword form per M0).
4. A **3-line read-only recipe**: `desktop_list_windows(includeHandles:true)` → `desktop_snapshot wN` →
   `desktop_get_text wN eN`, plus the fact that read-only perception needs **no lease** and cannot
   disturb the user.
5. One line pointing at the `driving-flaui-mcp` skill for input (type/click/drag), which does need a lease.

6. **A fallback for load-line failure.** If the load line returns `No matching deferred tools found`,
   the payload must tell the agent to retry with a keyword `ToolSearch` (e.g. `desktop window snapshot`)
   and use whatever names come back. Without this, a future prefix change re-creates G3 *inside the very
   mechanism built to prevent it*, and the agent cascades straight back to asking the human. This is what
   makes M1 self-healing against registration drift rather than merely correct today.

**Hard requirements:**

- **Gate on the server being INSTALLED — a file test, not a reachability probe.** Injecting a rule for
  tools that do not exist is worse than silence: it manufactures exactly the first-contact failure G3
  describes. The gate is `test -f "$LOCALAPPDATA/Programs/FlaUI.Mcp/flaui-mcp.exe"` (path from
  `installer/flaui-mcp.iss:11` + `:5`; measured present on the maintainer's box). **Deliberately NOT a
  reachability or registration check** — a bash hook has no cheap synchronous access to the client's MCP
  registry, so requiring "reachable" would be an impossible constraint. Installed-but-not-registered is
  an accepted false-positive: it is rare, and its cost (one stale hint) is far below the cost of a probe
  that is slow or hangs at session start.
- **`jq` is a declared prerequisite** (G5, user decision). The plan must add it to the installer's
  prerequisite check and to `.claude/recommended-tools.json`, so a missing `jq` surfaces at install time
  rather than as a silently dead hook.
- **Fail LOUDLY, not silently.** The original "silent on all paths" requirement is withdrawn: it
  engineered an undebuggable failure. Correct behaviour — silent on the *negative gate* (server not
  installed: nothing to say), but **diagnosable on error** (missing `jq`, malformed JSON, unreadable
  input). The plan must also surface activation-hook health in the existing `flaui-mcp status` output, so
  "why is the hint not appearing?" is answerable without reading hook source.
- **Cheap.** A file test plus a static string; no network, no process spawn beyond `jq`.
- **Bounded length.** Hard budget: **≤ 15 lines / ≤ 1200 characters** of injected text.
- **Correct event name.** The emitted JSON must set `hookSpecificOutput.hookEventName` to
  **`"SessionStart"`**. Called out because the script this one is modelled on emits `"Stop"`, and a
  copy-paste implementation will ship the wrong value.

**Honest limitation — M1 is the weakest of the four mechanisms, and the spec does not pretend otherwise.**
`SessionStart` text is the *oldest* content in the context window, so it is among the first evicted under
compaction — and the motivating failure explicitly involved a truncated/compacted context. A payload
degrades better than a signpost (a skimmed payload still leaves an executable line behind; a skimmed
signpost leaves nothing), but neither survives eviction. **This is precisely why M3 is non-negotiable:**
tool descriptions re-enter context every time a tool loads, so they are the only mechanism here that is
immune to compaction. M1 raises the floor early in a session; M3 holds it late.

### 5.3 M2 — monologue-matched skill frontmatter (fixes 3)

Rewrite the `description:` in **both** copies (G4) from mechanism-shaped to question-shaped and
whole-surface, so the matcher fires at the decision point rather than after the agent has committed to
driving. It must cover perception AND actuation AND background-console reading, and must name the
anti-pattern of delegating desktop observation to the human.

**Correction — broadening is NOT free.** An earlier draft of this spec claimed skill matching carries no
false-positive penalty. That is withdrawn: it contradicted this spec's own folded *Semantic Discard*
finding. A spuriously-matched skill loads **340 lines** into a scarce context window, adds latency, and
injects off-task desktop semantics — and repeated irrelevant loads train the agent to discount the skill,
which is the same failure Semantic Discard describes for hooks. **Precision dominates recall here too.**

Two bounded constraints follow:

- **Do NOT embed the literal token `Get-Process` in the frontmatter.** The matcher indexes it heavily, so
  an agent legitimately checking a background web server's process would drag in this 340-line desktop
  skill. Express the anti-pattern semantically (delegating desktop observation to a human; inferring UI
  state indirectly) without the token that over-indexes.
- The description must be **decision-point-shaped but bounded** — the triggers named in §5.2 item 2, not
  an open invitation on any mention of "screen" or "running".

### 5.4 M3 — hoist trap-class facts into tool `[Description]`s (fixes G2)

Tool descriptions are the only surface that is unconditionally in-context once a tool loads, and that
cannot be held stale or truncated. This is the sole mechanism addressing *driving correctly once
activated*; M0–M2 all address activation.

**Selection rule — a fact earns a slot only if ALL FOUR hold.** This exists to stop the descriptions
becoming a second copy of the skill:

1. The **obvious default assumption is wrong** (it is a trap, not merely useful information).
2. Acting on the wrong assumption **fails silently** — a plausible-looking wrong answer, not an error.
3. It is **specific to this tool**, not general driving advice.
4. It fits in **one sentence**.

Confirmed initial member (the observed failure): a Windows Terminal tab title is the **launcher, not the
program** — a bare `cmd.exe`/`PowerShell` tab may host a running CLI agent; enumerate and read every
candidate. Applies to `desktop_list_windows` and `desktop_read_terminal_tab`.

The plan must audit the remaining 47 tools against the four-part rule and admit only what passes; the
expected yield is small (single digits).

**Enforced by a structural invariant test**, modelled on the existing
`test/FlaUI.Mcp.Tests/Server/ToolReadOnlyInvariantTests.cs` (reflects over every `[McpServerTool]`):
assert each required trap fact is present in its named tool's `[Description]`, and assert a per-
description length budget so the hoisting cannot silently metastasize.

### 5.5 Cross-agent parity

M0, M2 and M3 are agent-agnostic — the skill and the MCP tool descriptions are consumed by both Claude
and agy. **M1 is Claude-Code-specific** (`SessionStart` hooks).

**Decision (not deferred): ship M1 for Claude Code only in this increment, and record the agy gap in the
ROADMAP.** Rationale: M0/M2/M3 — three of the four mechanisms, including the compaction-immune one —
already reach agy unchanged, so agy is not left on the old path. Building a second hook integration for a
different agent runtime doubles the surface for the weakest mechanism (§5.2). The ROADMAP entry is the
deliverable, not a placeholder.

## 6. Explicitly rejected

### 6.1 The contingent `Stop`-hook interlock

Proposed by the driver, and dropped. Killed by **Commitment Lock-in** (fires after the ask is already
emitted → trains a tax loop, not extinction) and **Semantic Discard** (a single false positive on a
legitimate question permanently discredits the hook). Regex over natural-language prose cannot reach the
precision this requires. agy's constraint, accepted: if it were ever shipped it would need a ~0%
false-positive rate, i.e. it could only trigger on deterministic tool invocations — at which point it is
a different mechanism, not this one.

### 6.2 AA2's "read me fresh before driving" gate

The ROADMAP proposed a prominent banner at the top of the skill telling the agent to re-read the skill.
Dropped as **circular**: per G2 the agent did not open the skill at all, so an instruction *inside* the
skill cannot fire. M3 replaces it — it puts the load-bearing facts where reading is not optional.

### 6.3 Shrinking the tool surface to defeat deferral

Rejected per G1 (unactionable, client-side threshold) and agy's *Capability Amputation* — destroying real
capability to game a client-side UX heuristic.

## 7. Acceptance criteria

**Mechanically verifiable in CI (gate on these):**

1. **Load-line derivability test** — the headline gate. A test parses the `ToolSearch` line out of
   `SKILL.md`, extracts the comma-separated tool names, and asserts every extracted name is derivable
   from the server's **live `[McpServerTool]` reflection data** as either `mcp__flaui-mcp__` + tool name
   or `mcp__plugin_flaui-mcp_flaui-mcp__` + tool name. *An earlier draft's criterion — "run it verbatim
   and confirm it resolves" — is withdrawn as compliance theater:* `ToolSearch` is a client-internal
   command with no CI equivalent, so that check would be satisfied by a human ticking a box once and
   would rot at the next prefix change. The reflection test is a real gate, and it additionally catches
   tool **renames**, which the manual check never would.
2. No occurrence of the single-prefix `select:mcp__flaui-mcp__` form remains in any tracked file.
3. G4 is resolved: exactly ONE live registration of the skill remains, or a test asserts the two copies
   are byte-identical. Two independently-editable live skills is a failing state.
4. `flaui-activation.sh`: emits well-formed JSON with `hookEventName == "SessionStart"` when the exe
   file-gate passes; emits nothing when it fails; emits a **diagnosable error** when `jq` is missing or
   input is malformed; injected text is ≤ 15 lines / ≤ 1200 characters.
5. The M3 invariant test passes: every required trap fact is present in its named tool's `[Description]`,
   and **no description exceeds 1200 characters** (the current longest, `desktop_read_terminal_tab`, is
   the practical ceiling — the plan must measure it and set the budget at or just above it, so hoisting
   cannot silently metastasize).
6. `jq` is declared in the installer prerequisite check and `.claude/recommended-tools.json`.
7. The repo's default gate stays green with zero new warnings.

**Not mechanically verifiable — stated honestly rather than faked:** whether the agent actually reaches
for the tool. The observational signal (agy's, accepted): in a fresh session needing desktop context, a
`ToolSearch` for the desktop tools occurs with **no preceding** ask-the-human or `Get-Process` attempt.
This is a manual dogfooding gate, not a CI check, and it is the only signal that measures the actual goal.

## 8. Out of scope

- **B4** (`desktop_list_terminal_processes` / WT tab-discovery cost) — genuine feature work; unrelated to
  adoption. Stays backlog.
- **Resolving G4** (deleting or generating the duplicate skill copy) — flagged here, decided in the plan;
  this spec only requires that edits land in both copies.
- Any change to the 49-tool surface, per §6.3.

## 9. References — verified against the tree at design time (2026-07-26)

- `plugins/flaui-mcp/skills/driving-flaui-mcp/SKILL.md` — `:20` frontmatter `description`; `:25` broken
  load line (G3); `:252` the seed trap-table row (G2); `:314–340` `AUTOTRAIN:GROWTH` region.
- `.claude/skills/driving-flaui-mcp/SKILL.md` — tracked, byte-identical duplicate (G4).
- `plugins/flaui-mcp/hooks/hooks.json` — currently one `Stop` hook; `SessionStart` unused.
- `plugins/flaui-mcp/scripts/flaui-curate-nudge.sh` — the hook shape M1 follows.
- `src/FlaUI.Mcp.Server/Tools/WindowTools.cs:23` — `desktop_list_windows` description (M3 target).
- `src/FlaUI.Mcp.Server/Tools/ContentTools.cs:86` — `desktop_read_terminal_tab` description (M3 target).
- `test/FlaUI.Mcp.Tests/Server/ToolReadOnlyInvariantTests.cs` — the invariant-test model for M3.
- Negotiation record: `.clavity/seams/agent-adoption-activation.md` and
  `.clavity/seams/agent-adoption-round2.md` — **local scratch, `.clavity` is gitignored**, so these are
  not available to a reader of the repo. The substance that matters is reproduced in §4 and §10; the
  files are cited only for the maintainer's own machine.

## 10. Panel ledger — folded, do NOT re-raise

Adversarial panel round 1 (solo `relentless-adversarial-auditor` panel + agy escalation). Findings folded
into the sections above; recorded here so later rounds hunt new defect-classes.

| # | Finding | Seat(s) | Fold |
| --- | --- | --- | --- |
| 1 | "Gate on the server being *reachable*" is impossible cheaply from a bash hook | Axiom Breaker (agy) | Scope corrected by measurement: requirement changed to an *installed-on-disk* file test (§5.2). agy's impossibility claim conflated reachable with installed. |
| 2 | Payload assumes the load line succeeds; no error path → cascades back to asking the human | Cascade Analyst (agy) | §5.2 item 6 — mandatory keyword-`ToolSearch` fallback; makes M1 self-healing vs prefix drift. |
| 3 | Headline acceptance check is un-automatable compliance theater | Mechanism Gamer (both panels, independently) | §7 criterion 1 replaced with the reflection-derivability test. |
| 4 | "Broadening the skill trigger is free" contradicts the spec's own Semantic Discard fold | Activation Auditor (driver); Q-A (agy) | Claim withdrawn in §5.3; precision-dominates-recall now applies to the skill description too. |
| 5 | Embedding literal `Get-Process` in frontmatter causes chronic over-triggering | Activation Auditor (agy) | §5.3 — token explicitly forbidden; anti-pattern expressed semantically. |
| 6 | "Silent on all paths" engineers an undebuggable failure | Blindspot Auditor (both panels) | §5.2 — silent only on the negative gate; diagnosable on error; `flaui-mcp status` reports hook health. |
| 7 | `jq` is an unstated distribution dependency | Dependency Cynic (agy) | G5 + §5.2 + §7 criterion 6 — promoted to a declared prerequisite (user decision). |
| 8 | Copying the `Stop` hook ships the wrong `hookEventName` | Protocol Pedant (driver) | §5.2 — `"SessionStart"` pinned explicitly with the rationale. |
| 9 | M1 is weakest under compaction — the exact condition of the motivating failure | Axiom Breaker (driver) | §5.2 — limitation stated plainly; M3 identified as the compaction-immune mechanism and therefore non-negotiable. |
| 10 | Unspecified length budgets are trivially satisfiable | Mechanism Gamer (driver) | §7 criteria 4–5 — pinned at ≤ 15 lines / ≤ 1200 chars. |
| 11 | Architectural decisions deferred to "the plan" | Literal Implementer (agy) | §5.5 agy-parity decided in-spec; §5.1 prefix-drift resolved by criterion 1. The 47-tool audit remains plan-level as enumeration work, not a design decision. |
| 12 | `.claude/skills` duplicate may be dead or live — undetermined | Dependency Cynic (driver) | Measured live: two separately registered skills. G4 upgraded from "flag" to a blocking acceptance criterion (§7 criterion 3). |

**Round 1 verdict:** NOT GREEN — 12 findings folded, including two internal contradictions in the spec's
own reasoning (#3, #4).
