# Agent adoption — make reaching for the tool the default move

**Status:** design, approved 2026-07-26 (user delegated approval to the driver as the consuming agent).
Revised after adversarial panel round 2 — see §10. Round 2 corrected two round-1 ground truths, one of
them inverted (G4), and added G6, which is a precondition for M1 shipping at all.
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

**G4 — the skill is registered TWICE, and the copy this spec called "retired" is the one that SHIPS.**
*(Corrected in panel round 2 — the round-1 reading was backwards.)*

Both `.claude/skills/driving-flaui-mcp/SKILL.md` and `plugins/flaui-mcp/skills/driving-flaui-mcp/SKILL.md`
are git-tracked and currently byte-identical (md5 `29af5418…`, measured). The consuming agent's
available-skills list shows **two separately registered entries** — bare `driving-flaui-mcp` and
`flaui-mcp:driving-flaui-mcp`. But the two copies do **not** have equal standing:

| Copy | Role | Reaches installed users? |
| --- | --- | --- |
| `.claude/skills/driving-flaui-mcp/SKILL.md` | **Build input.** `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj:8` embeds this exact path as `FlaUI.Mcp.Server.seed.driving-flaui-mcp.SKILL.md`. `PluginArtifactWriter.WriteSkill()` (`:111`) reads that resource and writes it into the install staging dir, which `ClaudePluginRegistrar` then feeds to `claude plugin marketplace add`. | **YES — this is the canonical shipped copy.** |
| `plugins/flaui-mcp/skills/driving-flaui-mcp/SKILL.md` | Repo-checkout skill registration only. Nothing packages it. | **No.** |

Two further measurements:

- **`ClaudeSkillDeployer.Deploy()` has zero call sites** (measured across `src/`). The old skill-directory
  deployment model is fully retired; the class and its embedded-resource consumer are dead code. The
  `0369549` commit message that led round 1 to label `.claude/skills` "retired" was describing the *status
  reporting*, not the build graph.
- The csproj `Include` is a **literal path, not a glob**.

**Consequences — these replace the round-1 conclusions:**

1. Every skill edit below MUST land in **both** tracked copies (unchanged).
2. The M0/M2 edits only reach installed users through the **`.claude/skills` copy → rebuild → reinstall**
   chain. A user who updates the plugin without updating the exe keeps the broken load line.
3. **Acceptance criterion 3's "delete the retired copy" is unsafe as written** and is corrected in §7: the
   `.claude/skills` copy cannot be deleted without also editing `csproj:8-10`, and deleting it while
   leaving the csproj alone breaks the build. Deleting it *and* the csproj entry would silently stop
   shipping the skill to users. The duplication must be resolved by making one copy **generated from** the
   other, not by deletion.

**G6 — the plugin's hooks and scripts are NOT distributed. M1 as specified reaches 0% of installed users.**
*(Round 2. Raised independently by agy and confirmed by measurement — though via a different code path than
agy cited: agy blamed the dead `ClaudeSkillDeployer`; the live path is `PluginArtifactWriter`.)*

`PluginArtifactWriter.Generate()` (`src/FlaUI.Mcp.Server/Install/PluginArtifactWriter.cs:80-86`) writes
exactly four artifacts into the install staging dir:

```
.mcp.json · plugin.json · .claude-plugin/marketplace.json · skills/driving-flaui-mcp/SKILL.md
```

There is **no `hooks/` and no `scripts/`**. The installed plugin therefore carries no hooks at all.

This is not a hazard introduced by M1 — it is already true today: the existing `flaui-curate-nudge.sh`
`Stop` hook has never reached an installed user either. It works only for someone running Claude Code
inside the repo checkout, which is why it appeared to work during dogfooding.

**Consequence:** M1 is undeliverable as specified. Shipping hooks is a **precondition** of M1, not an
implementation detail — see the new hard requirement in §5.2 and criterion 8 in §7.

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
3. The **ready-to-run load line** — the dual-prefix `select:` form per M0. *(Round 2: an earlier draft
   said "keyword form per M0", contradicting §5.1, which explicitly **rejected** the keyword form. The
   `select:` form is the chosen one, here and everywhere.)*
4. A **3-line read-only recipe**: `desktop_list_windows(includeHandles:true)` → `desktop_snapshot wN` →
   `desktop_get_text wN eN`, plus the fact that read-only perception needs **no lease** and cannot
   disturb the user.
5. One line pointing at the `driving-flaui-mcp` skill for input (type/click/drag), which does need a lease.

6. **A fallback for load-line failure — constrained to a named allow-list.** If the load line returns
   `No matching deferred tools found`, the payload tells the agent to retry with a keyword `ToolSearch`
   (e.g. `desktop window snapshot`) **and to use only tools whose bare names appear in the recipe's
   allow-list — `desktop_list_windows`, `desktop_open_window`, `desktop_snapshot`, `desktop_get_text`,
   `desktop_input_status` — discarding every other name the search returns.** If the allow-listed tools
   are not among the results, the agent must say the tool is unavailable, NOT substitute a similar name.

   **Round 2 — why the unconstrained form was dangerous.** The earlier wording ("use whatever names come
   back") composed badly with item 4's "read-only, no lease, cannot disturb the user". Measured this
   session, the keyword query `desktop window snapshot` returned 8 tools, of which **three mutate**:
   `desktop_close_window`, `desktop_focus_window`, `desktop_window_transform`. It also did **not** return
   `desktop_get_text`. So the composed instructions handed the agent a window-closing tool under a banner
   promising it could not disturb the user, while omitting a tool the recipe needs. Raised independently
   by the driver's Boundary Smuggler seat and agy's Cascade Analyst seat.

   The fallback is kept rather than deleted because it is the **only** defence against prefix-algorithm
   drift — a failure mode the CI gate in §7 provably cannot catch (see criterion 1's scope note).

**Hard requirements:**

- **Gate on the server being INSTALLED — a file test, not a reachability probe.** Injecting a rule for
  tools that do not exist is worse than silence: it manufactures exactly the first-contact failure G3
  describes. The gate is `test -f "$LOCALAPPDATA/Programs/FlaUI.Mcp/flaui-mcp.exe"` (path from
  `installer/flaui-mcp.iss:11` + `:5`; measured present on the maintainer's box). **Deliberately NOT a
  reachability or registration check** — a bash hook has no cheap synchronous access to the client's MCP
  registry, so requiring "reachable" would be an impossible constraint. Installed-but-not-registered is
  an accepted false-positive: it is rare, and its cost (one stale hint) is far below the cost of a probe
  that is slow or hangs at session start.
- **The hook must actually SHIP (G6) — this is a precondition, not a detail.** `PluginArtifactWriter`
  must additionally stage `hooks/hooks.json` and `scripts/`, or M1 exists only in the repo checkout.
  Because this also fixes the *existing* undelivered `Stop` curate-nudge hook, the plan must treat
  "plugin hooks are distributed" as its own task with its own test, sequenced **before** the M1 hook is
  written — building M1 first would produce a mechanism that demonstrably works for the maintainer and
  for nobody else, which is the most expensive kind of green.

- **M1 must NOT depend on `jq`.** *(Round 2.)* `jq` is required by `flaui-curate-nudge.sh` because that
  hook **parses** stdin to derive a session id. M1 parses nothing: its gate is a file test and its payload
  is a compile-time constant. It must therefore emit **pre-escaped literal JSON** via a here-doc and take
  no dependency on `jq` at all. This deletes an entire failure class from the mechanism that runs at every
  session start, on every machine.

  `jq` remains a declared prerequisite per G5 and the user's decision — the `Stop` hook still needs it —
  but M1's correctness must not rest on the prerequisite check having been honoured. The plan must add it
  to the installer's prerequisite check and to `.claude/recommended-tools.json` (both of which are
  **net-new**; see §7 criterion 6).

- **Never exit non-zero.** The hook must `exit 0` on every path, writing diagnostics to stderr. A
  `set -euo pipefail` script that dies on a missing dependency runs at *session start*; if a non-zero exit
  is treated as fatal by the client, a missing tool would brick every session in the project. Failing
  loudly must never mean failing closed.

- **The payload must not interpolate anything read from stdin.** It is a constant. M1 has no reason to
  read stdin at all, and the hook shape it is modelled on does — so an implementer following that shape
  will add a read that is both unnecessary and an untrusted-input path into the agent's own context
  window. The invariant is testable: no stdin-derived value may appear in the emitted `additionalContext`.
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

**Limitation — restated after round 2, because the round-1 version was wrong.**

Round 1 recorded: *"`SessionStart` text is the oldest content in the window, so it is among the first
evicted under compaction — and the motivating failure involved a compacted context."* The eviction half is
true. The conclusion drawn from it was not.

**Measured, live, in the session that produced this revision: `SessionStart` hooks re-fire on compaction.**
The compaction that ran mid-review emitted `SessionStart:compact hook success: flaui-autotrain: …` from
this repo's own sibling hook. `SessionStart` fires on **startup, resume, clear, and compact** — so the
event that evicts the payload is the same event that re-injects it.

**Consequences:**

- M1 must be registered for **all** `SessionStart` sources, not just startup. If the plan filters on
  `source`, it must filter *in* `compact` and `resume` — the highest-value firings, since those are
  exactly the moments the agent has just lost its context.
- M1 is **self-healing under compaction**, which was the single property round 1 said it lacked. It is no
  longer accurate to call it the weakest mechanism on that ground.
- M3 remains non-negotiable, but for a different and narrower reason than round 1 gave: not "M1 dies at
  compaction and M3 doesn't", but that **M3 is the only mechanism that addresses driving *correctly* once
  activated** (§5.4). M0–M2 all address activation. That is the real division of labour.

The honest residual weakness is different and smaller: between eviction and the next compaction boundary,
there is a window in which the payload is gone and nothing re-injects it. M3 covers that window for any
agent that has already loaded a tool; nothing covers it for an agent that has not.

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

1. **Load-line derivability test** — a test parses **every** `ToolSearch` load line in **every** tracked
   artifact that carries one (both `SKILL.md` copies **and** the M1 hook script's payload), extracts the
   comma-separated tool names, and asserts each is derivable from the server's **live `[McpServerTool]`
   reflection data** as either `mcp__flaui-mcp__` + tool name or `mcp__plugin_flaui-mcp_flaui-mcp__` +
   tool name.

   *An earlier draft's criterion — "run it verbatim and confirm it resolves" — stays withdrawn as
   compliance theater.* But round 2 also **narrows what this test can honestly claim**, per agy's
   Mechanism Gamer seat: the test hardcodes the same prefix algorithm the spec assumes, so it validates
   the spec against itself on that axis. **If the client's prefixing algorithm changes, this test still
   passes and the load line still breaks.** What it does gate, genuinely, is tool **renames** and typos —
   the drift that actually happens in this repo. Prefix-algorithm drift is uncoverable in CI, and is
   covered instead by M1's allow-listed fallback (§5.2 item 6). Neither mechanism alone is sufficient;
   the criterion is written down with its true scope rather than an inflated one.

   *Scope note:* criterion 2's negative check does not subsume this. A payload naming
   `desktop_snapshsot` contains no single-prefix form and would pass criterion 2 while being broken.
2. No occurrence of the single-prefix `select:mcp__flaui-mcp__` form remains in any tracked file.
3. **G4 is resolved without breaking the build.** *(Corrected in round 2.)* `.claude/skills/…/SKILL.md` is
   the build input at `csproj:8` and the source of the copy shipped to users, so "delete the retired
   copy" is not available. Acceptable resolutions: (a) one copy is **generated from** the other by the
   build, with a test asserting they match; or (b) both remain tracked and a test asserts they are
   byte-identical. Deleting either copy without a corresponding csproj change is a failing state, and so
   is two independently-editable live skills.
4. `flaui-activation.sh`: emits well-formed JSON with `hookEventName == "SessionStart"` when the exe
   file-gate passes; emits nothing when it fails; **exits 0 on every path** (diagnostics to stderr);
   **invokes no `jq`** and reads no stdin; injected text is ≤ 15 lines / ≤ 1200 characters, and every
   tool name in it is allow-listed per §5.2 item 6.
5. The M3 invariant test passes: every required trap fact is present in its named tool's `[Description]`,
   and **no description exceeds 1200 characters** (the current longest, `desktop_read_terminal_tab`, is
   the practical ceiling — the plan must measure it and set the budget at or just above it, so hoisting
   cannot silently metastasize).
6. `jq` is declared in the installer prerequisite check and `.claude/recommended-tools.json`. **Both are
   net-new construction, not edits** — measured in round 2: `.claude/recommended-tools.json` does not
   exist, and `installer/flaui-mcp.iss` has no prerequisite-check mechanism at all (its only `Check:` is
   `NeedsAddPath`). The plan must size this as building a check, not extending one.
7. **Plugin hooks and scripts are distributed (G6).** A test asserts that the staging dir produced by
   `PluginArtifactWriter.Generate()` contains `hooks/hooks.json` and the referenced scripts, and that the
   hook commands resolve under `${CLAUDE_PLUGIN_ROOT}` as staged. Without this criterion every other M1
   criterion can pass while M1 reaches nobody.
8. The repo's default gate stays green with zero new warnings.

**Not mechanically verifiable — stated honestly rather than faked:** whether the agent actually reaches
for the tool. The observational signal (agy's, accepted): in a fresh session needing desktop context, a
`ToolSearch` for the desktop tools occurs with **no preceding** ask-the-human or `Get-Process` attempt.
This is a manual dogfooding gate, not a CI check, and it is the only signal that measures the actual goal.

## 8. Out of scope

- **B4** (`desktop_list_terminal_processes` / WT tab-discovery cost) — genuine feature work; unrelated to
  adoption. Stays backlog.
- **Resolving G4** — no longer open-ended. Round 2 closed the design fork: deletion is unavailable
  (`csproj:8`), so the plan chooses between *generate-one-from-the-other* and *both-tracked-plus-identity-
  test*, per criterion 3. Edits must land in both copies regardless.
- **Distributing the flaui-learn/flaui-curate skills** — the adjacent packaging gap noted in §9. G6's fix
  must not entrench it, but closing it is separate work.
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

Added in round 2 (all measured, 2026-07-26):

- `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj:8-10` — embeds `.claude/skills/driving-flaui-mcp/SKILL.md`
  as `FlaUI.Mcp.Server.seed.driving-flaui-mcp.SKILL.md`. Literal path, not a glob (G4).
- `src/FlaUI.Mcp.Server/Install/PluginArtifactWriter.cs:80-86` — `Generate()`; the four staged artifacts
  (G6). `:111` — `WriteSkill()` reads the embedded resource.
- `src/FlaUI.Mcp.Server/Install/ClaudePluginRegistrar.cs:23-43` — the live install path
  (`marketplace add` → `plugin install` → read-back).
- `src/FlaUI.Mcp.Server/Install/ClaudeSkillDeployer.cs` — dead code, zero call sites (G4).
- `src/FlaUI.Mcp.Server/Install/CliRouter.cs:33` — the `status` verb §5.2 extends.
- `installer/flaui-mcp.iss:34` — the only `Check:` in the installer (criterion 6).
- Round-2 panel payload: `.clavity/seams/agent-adoption-round3-panel.md` — local scratch, gitignored.

**Adjacent defect, NOT folded (out of scope, worth a ROADMAP entry):** `plugin.json` advertises "plus the
flaui-learn/flaui-curate autotrain loop", but `PluginArtifactWriter.WriteSkill()` stages only
`driving-flaui-mcp`. The autotrain skills do not ship to installed users either. Same root cause as G6,
different symptom; it is not this spec's problem to fix, but the plan should not "fix" packaging in a way
that silently entrenches it.
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

### Round 2 — rotation seats: Boundary Smuggler, State Corruptor, Resource Vampire

Solo panel + agy escalation. Round 2 went looking for defect-classes round 1 never seated, and found that
**two of round 1's own ground truths were wrong** — one of them inverted.

| # | Finding | Seat(s) | Fold |
| --- | --- | --- | --- |
| 13 | **G6 — the plugin ships no hooks or scripts.** `PluginArtifactWriter.Generate()` stages only `.mcp.json`, `plugin.json`, `marketplace.json`, `SKILL.md`. M1 would reach 0% of installed users; the existing `Stop` hook already does. | Axiom Breaker (agy, via a mis-cited path); confirmed by driver measurement | New G6; hard requirement in §5.2; new criterion 7. Packaging sequenced BEFORE the M1 hook. |
| 14 | **G4 was backwards.** `.claude/skills/…/SKILL.md` is the build input (`csproj:8`) and the source of the shipped copy — not the retired one. `ClaudeSkillDeployer.Deploy()` has zero call sites. | State Corruptor (driver); Q4 (agy) | G4 rewritten with the role table; criterion 3 corrected — deletion is off the table, generation-with-test replaces it. |
| 15 | **The fallback hands the agent mutating tools under a "cannot disturb the user" banner.** Measured: the keyword query returns `desktop_close_window`, `desktop_focus_window`, `desktop_window_transform`, and omits `desktop_get_text`. | Boundary Smuggler (driver); Cascade Analyst (agy) — independent convergence | §5.2 item 6 — fallback constrained to a five-tool allow-list; substitution forbidden. |
| 16 | **`SessionStart` re-fires on compaction** (measured live this session). Round 1's "M1 is weakest because compaction evicts it" was wrong. | State Corruptor (driver) — agy could not determine | §5.2 limitation rewritten; M1 registered for all sources; M3's rationale narrowed to its real one. |
| 17 | §5.2 item 3 said "keyword form per M0" — the form §5.1 explicitly rejected. | Axiom Breaker (driver) | §5.2 item 3 — corrected to the dual-prefix `select:` form. |
| 18 | Criterion 1 validates the spec's prefix assumption against a test hardcoding the same assumption — passes green while the load line breaks. | Mechanism Gamer (agy) | Criterion 1 — claim narrowed honestly to renames/typos; prefix drift explicitly delegated to the M1 fallback, which is why #15 fixes the fallback rather than deleting it. |
| 19 | M1 needs no `jq`: its gate is a file test and its payload a constant. Depending on `jq` imports a failure class into a hook that runs at every session start. | Cascade Analyst (driver) | §5.2 — M1 emits literal JSON, no `jq`, no stdin read; `exit 0` on every path so a missing dependency cannot brick session start. |
| 20 | Criterion 6 gates two things that do not exist — `.claude/recommended-tools.json`, and any installer prerequisite-check mechanism. | Literal Implementer (driver); Q3 (agy) | Criterion 6 — flagged as net-new construction so the plan sizes it correctly. |

**Round 2 verdict:** NOT GREEN — 8 findings folded, two of them corrections to round-1 ground truth
(#14 inverted G4; #16 reversed M1's stated weakness). Resource Vampire: no new findings.

**Negotiation record for #13.** agy named the right defect from the wrong evidence: it cited
`ClaudeSkillDeployer.cs:33-51` as "the sole mechanism that writes the plugin on install". Measured, that
class has **zero call sites** — it is dead code from the retired skill-directory model. The live path is
`PluginArtifactWriter` → `ClaudePluginRegistrar` → `claude plugin marketplace add`. The conclusion
survived the correction and was folded; the cited mechanism did not. This is the second time in this
review that agy has built a confident structural claim on an unverified architectural assumption and been
right about the consequence anyway — the finding is worth taking seriously, the citation is not worth
trusting.
