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
`Stop` hook has never reached an installed *user* either.

**G8 — a FOURTH registration path exists, and it works. Two round-3/4 conclusions are hereby retracted.**
*(Measured 2026-07-27, when the `Stop` hook fired live during this very review.)*

`.claude/settings.json` (tracked, 11 lines) registers hooks **directly**, bypassing the plugin entirely:

```json
"SessionStart": [ { "matcher": "startup|clear|compact",
    "hooks": [ { "command": "bash \"${CLAUDE_PROJECT_DIR}/.claude/hooks/flaui-learn-reminder.sh\"" } ] } ],
"Stop":         [ { "hooks": [ { "command": "bash \"${CLAUDE_PROJECT_DIR}/.claude/hooks/flaui-curate-nudge.sh\"" } ] } ]
```

Those scripts live in `.claude/hooks/` — a **third** copy of the nudge script, byte-identical to
`plugins/flaui-mcp/scripts/` today (measured) but guarded by no test.

**Retracted:**

1. **"No hook in this repo has ever run" (G7 consequence) is FALSE.** The `Stop` nudge fired during this
   review and wrote its session sentinel; the `SessionStart` learn-reminder fired at this session's
   compaction. Hooks work for the maintainer — through `.claude/settings.json`, not the plugin.
2. **"The prototype has never worked — CRLF + `jq`" (round-4 finding #27) is FALSE.** The live script is
   **CRLF** (measured) and runs correctly: Git Bash tolerates the `\r` here, and `jq` resolves via the
   maintainer's portable toolchain. Task 1's LF pinning remains justified as **portability hygiene** for
   a script about to ship to arbitrary machines — WSL bash and other shells are far less forgiving — but
   it is **not** a bug fix, and nothing downstream may claim the hook is currently broken.

**What survives unchanged:** G6 and G7's narrow claims. `PluginArtifactWriter` still stages four files;
the staging dir still contains no hooks; `plugins/flaui-mcp/` is still registered nowhere. **Installed
users still receive no hooks at all** — which is the finding M1 actually depends on.

**What this ADDS to the design:** a working, tracked `SessionStart` hook whose matcher is
`"startup|clear|compact"` — precisely the multi-source registration §5.2 requires — already exists in
this repo. M1 should copy that proven shape rather than invent one.

**G7 — the split brain: `plugins/flaui-mcp/` is registered NOWHERE.**
*(Round 3. This corrects round 2's own wording, which said the hooks "work for someone running Claude
Code inside the repo checkout". Measured — they work for nobody, including the maintainer.)*

| | |
| --- | --- |
| Registered marketplace | `flaui-mcp-marketplace` → `%LOCALAPPDATA%\Programs\FlaUI.Mcp\plugin` (`~/.claude/settings.json:162-167`) |
| That directory contains | `.claude-plugin/marketplace.json` · `.mcp.json` · `plugin.json` · `skills/driving-flaui-mcp/SKILL.md` — **four files, no hooks, no scripts** |
| Repo's `.claude-plugin/marketplace.json` | names marketplace `flaui-mcp`, source `./plugins/flaui-mcp` — **not present in `known_marketplaces.json`** |

Three consequences, each correcting something earlier in this spec:

1. The `flaui-mcp:driving-flaui-mcp` skill the agent sees comes from the **staging dir**, not from
   `plugins/flaui-mcp/`. So **`plugins/flaui-mcp/skills/driving-flaui-mcp/SKILL.md` is registered
   nowhere** — G4's table called it "repo-checkout registration only"; it is neither shipped nor live.
   The two live registrations are the `.claude/skills` copy and the *staged* copy.
2. **Measured in the field:** the shipped skill at
   `%LOCALAPPDATA%\Programs\FlaUI.Mcp\plugin\skills\driving-flaui-mcp\SKILL.md` **contains the broken
   single-prefix load line right now.** G3 is not theoretical; it is live on the installed machine.
3. Editing `plugins/flaui-mcp/` — which is what §5.2 literally instructs for M1 — changes nothing for
   anyone until the packaging fix lands. *(Round 7 correction: an earlier version of this bullet added
   "a maintainer would have to register the repo marketplace manually to see their own hook fire." That
   is **false** — per G8 the maintainer's hooks fire through `.claude/settings.json`, which needs no
   marketplace registration at all. The surviving point is narrower and unaffected: a hook that fires
   for the maintainer via `.claude/settings.json` still says nothing about the shipped artifact, because
   the shipped artifact contains no hooks.)*

**Consequence:** M1 is undeliverable as specified. Shipping hooks is a **precondition** of M1, not an
implementation detail — see the hard requirements in §5.2 and criteria 7–9 in §7.

**The propagation chain M0/M2 must travel** (nothing in the spec gated this before round 3):

```
edit .claude/skills/…/SKILL.md → rebuild exe (csproj:8 embeds it) → ship installer
  → user runs `flaui-mcp install` → PluginArtifactWriter.Generate() rewrites the staging dir
  → ClaudePluginRegistrar: `claude plugin marketplace add` + `plugin install`
```

Every skill change therefore requires a **rebuild and a reinstall** to reach anyone. The plan must add a
staleness signal to `flaui-mcp status` (installed `plugin.json` version vs running assembly version), or
users will silently run an old skill against a new server.

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

A new `SessionStart` entry in the plugin's `hooks/hooks.json`, whose command is the **`activation-payload`
verb on `flaui-mcp.exe`** (decided in round 4, below). It reuses the *output shape* of the existing
`flaui-curate-nudge.sh` — `hookSpecificOutput.additionalContext` — and nothing else about it.

*(Earlier drafts of this section specified a `flaui-activation.sh` script. Round 4 replaced it; that name
should not appear anywhere in the plan.)*

**Those repo paths are the build input, not a live plugin.** Per G7, `plugins/flaui-mcp/` is registered
nowhere; editing it changes nothing until the packaging requirement below makes it the embedded source of
the staged artifact. Writing the hook without that is writing a file no runtime will ever read.

#### Decided in round 4 — the hook command is the PRODUCT'S OWN EXECUTABLE, not a shell script

The fork was put to agy neutrally, with no lean disclosed. Both panels chose the executable independently.

| | Option A — `bash …/flaui-activation.sh` | **Option B — `flaui-mcp.exe activation-payload`** |
| --- | --- | --- |
| Needs `bash` on a stock Windows host | yes | **no** |
| Needs `jq` | (removed, but the shape invites it) | **no** |
| CRLF / exec-bit hazards through packaging | yes — a real risk once it ships, though **not** a current breakage (G8) | **none — nothing is extracted** |
| Install gate | a `test -f` for the exe | **the exe running IS the gate** |
| Payload kept correct over time | a string in a shipped script | **a compiled constant, reachable by the criterion-1 reflection test** |

The absolute exe path is written into `hooks.json` at install time by the same generator that already
writes it into `.mcp.json` — a proven mechanism, not a new one. This also means `hooks.json` is
**generated** rather than embedded, so the byte-identity/CRLF requirements below apply only to whatever
scripts remain (ideally none).

**agy's argument against its own choice, and why it does not stand.** agy warned that if unregistration
fails, a `SessionStart` hook pointing at a deleted absolute exe path fires every session, and that Option
A degrades better because its script "gracefully runs a `test -f` first". Measured, the asymmetry is not
real: the script would live in the same deleted plugin tree, so `${CLAUDE_PLUGIN_ROOT}/scripts/…` is
equally gone and `bash` equally fails to start. **The orphaned-hook hazard is symmetric**, and it is
therefore not a reason to prefer A.

The residual hazard is real but narrower than agy framed it, and it is **not** the PATH/elevation story
(refuted in §10): it materialises only when `Unregister()` returns `NotFound` — e.g. the user removes
Claude Code *before* uninstalling this product (`ClaudePluginRegistrar.cs:57-59`). The plan must
(a) confirm the client's behaviour when a hook command's binary is missing, and (b) require that failure
mode to be loud-but-non-blocking. Since the hook ships *inside* the plugin, removing the plugin removes
the hook — orphaning requires unregistration to fail specifically.

**It injects a payload, not a signpost.** Required content, in this order — the anti-pattern rule comes
FIRST because it is the genuinely novel instruction:

1. The negative norm: do not ask the human to look at or operate a desktop app; do not infer UI state
   from `Get-Process`.
2. The trigger list, covering the whole surface: see what's on screen · check an app is running/
   responding · read a background terminal or console tab · click/type/fill a dialog in a GUI app ·
   confirm a change landed in the real app.

   **The payload must split this list by lease requirement.** *(Round 4 — the trigger list and the
   safety framing contradicted each other.)* `desktop_read_terminal_tab` is declared
   `[McpServerTool(Destructive = true)]` (`ContentTools.cs:86`) — it *switches* tabs and restores them,
   and is blocked in `--read-only-mode`. So "read a background terminal tab", which item 2 advertises,
   is **not** covered by item 4's "read-only, no lease, cannot disturb the user". A payload that lists
   the trigger and then blankets the whole list in the read-only reassurance tells the agent it may
   switch a user's terminal tabs without a lease. Items 2 and 4 must be explicitly zoned: perception
   (`list_windows` / `snapshot` / `get_text`) is lease-free; **background-tab reading and everything in
   item 5 require a lease.**
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
- **The hook must actually SHIP (G6/G7) — a precondition, not a detail.** `PluginArtifactWriter` must
  additionally stage `hooks/hooks.json` and `scripts/`. Because this also fixes the *existing*
  undelivered `Stop` curate-nudge hook, the plan must treat "plugin hooks are distributed" as its own
  task with its own test, sequenced **before** the M1 hook is written.

- **Staged by EMBEDDING, not by re-authoring — fidelity, not presence.** *(Round 3; the driver's
  Distribution Realist seat and agy's Q3/Q4 converged.)* The hooks and scripts must be embedded as
  resources from the repo tree — exactly as `csproj:8` already does for `SKILL.md` — and extracted
  byte-for-byte. They must **not** be re-authored as C# string literals in `PluginArtifactWriter`, which
  is the path of least resistance and would create a third independently-drifting source of truth on top
  of G4's pair. The repo tree must be the strict build input.

  Two concrete failure modes this closes, both of which would pass a presence-only test:
  - **Line endings.** This repo actively converts LF→CRLF on checkout, so a CRLF `.sh` is the *default*
    outcome, not an edge case, and the extraction must pin LF.

    *Round 7 correction — do not overstate this.* An earlier version claimed CRLF "breaks
    `#!/usr/bin/env bash` at the shebang". Measured (G8), that is **wrong twice over**: the live CRLF
    script runs correctly under Git Bash, and the hook invokes `bash "<script>"` explicitly, so the
    shebang is never consulted. The real risk is narrower but still worth pinning: CRLF tolerance is
    **interpreter-specific**, and bare `bash` on Windows resolves to the WSL launcher before Git Bash —
    a far less forgiving path that the maintainer's machine happens never to exercise.
  - **Drift.** A staged copy authored separately from the tested copy means the maintainer tests one
    artifact and ships another.

- **M1 must NOT depend on `jq`.** *(Round 2.)* `jq` is required by `flaui-curate-nudge.sh` because that
  hook **parses** stdin to derive a session id. M1 parses nothing: its gate is a file test and its payload
  is a compile-time constant. Under the Option-B decision it emits that JSON from C# using the standard
  .NET serializer, taking no dependency on `jq` — or on any shell — at all. This deletes an entire failure
  class from the mechanism that runs at every session start, on every machine. *(An earlier draft said
  "pre-escaped literal JSON via a here-doc"; that was written for the rejected script implementation and
  is meaningless in C#.)*

  `jq` remains a declared prerequisite per G5 and the user's decision — the `Stop` hook still needs it —
  but M1's correctness must not rest on the prerequisite check having been honoured. The plan must add it
  to the installer's prerequisite check and to `.claude/recommended-tools.json` (both of which are
  **net-new**; see §7 criterion 6).

- **Never exit non-zero — restated for Option B.** *(Round 5.)* The rule was written for a script and
  said "exit 0 on every path". Under Option B the dangerous case is one the hook cannot handle at all:
  if the executable is **missing**, the hook never runs and the client sees a spawn failure, so no
  in-process `exit 0` can help. Split the requirement:
  - **When it runs:** the verb exits 0 on every path, diagnostics to stderr. *(Round 6 correction: the
    original justification — "a non-zero exit would brick every session" — is disproven by the experiment
    below. The requirement stands on the weaker but real ground of hygiene: a failing hook prints a
    visible error block, and one per session forever is its own kind of harm.)*
  - **When it cannot run — RESOLVED BY EXPERIMENT, 2026-07-26.** This was the spec's one blocking
    unknown. Measured directly: an isolated project was given a `SessionStart` hook whose command pointed
    at a non-existent executable (`C:/Nonexistent/Path/flaui-mcp-missing.exe`), validated as well-formed
    settings JSON, and a headless session was run against it. **The session started normally, the model
    responded, and the process exited 0.** A hook command that cannot be spawned is therefore
    **non-blocking**.

    Corroborating evidence from interactive mode, observed in the session that produced this revision: a
    `PreCompact` hook emitting schema-invalid JSON produced a visible error block and the session
    continued unaffected. So hook failures are **surfaced but never fatal**, in both modes.

    **Consequence:** the orphaned-hook hazard is downgraded from *session-bricking* to *cosmetic noise*
    — a warning the user may see each session until they remove the stale registration. That is a real
    but minor cost, and it no longer counts against the Option-B decision. The residual work is a UX
    question (does the warning repeat every session?), not a shipping blocker.

- **The payload must not interpolate anything read from stdin.** It is a constant. M1 has no reason to
  read stdin at all, and the hook shape it is modelled on does — so an implementer following that shape
  will add a read that is both unnecessary and an untrusted-input path into the agent's own context
  window. The invariant is testable: no stdin-derived value may appear in the emitted `additionalContext`.
- **Fail LOUDLY, not silently.** The original "silent on all paths" requirement is withdrawn: it
  engineered an undebuggable failure. Correct behaviour — silent on the *negative gate* (server not
  installed: nothing to say), but **diagnosable on error** (missing `jq`, malformed JSON, unreadable
  input). The plan must also surface activation-hook health in the existing `flaui-mcp status` output, so
  "why is the hint not appearing?" is answerable without reading hook source.
- **Cost — restated in round 5, because the Option-B decision made the old wording false.** The previous
  requirement read *"a file test plus a static string; no network, no process spawn beyond `jq`"*. Option B
  **is** a process spawn, of a 140 MB single-file .NET executable, at every session start — including
  every compaction. The requirement is therefore rewritten as a **budget**, not a prohibition:

  - The `activation-payload` verb must return **before** any MCP/DI/server initialisation — the earliest
    possible branch in the CLI router, alongside `--version`.
  - No network, no filesystem work beyond emitting the constant.
  - **`SessionStart` hooks DO block the first turn — measured, 2026-07-26.** An isolated project was given
    a `SessionStart` hook that slept 5 seconds and then injected a unique marker string; the model's very
    first response confirmed the marker was already in its context. The client therefore **waits** for
    hook output before generating the first turn.

    This settles two things at once. There is **no race** in which the agent commits to asking the human
    before the payload lands — the mechanism is sound. But it also means the hook's runtime is **added
    latency the user waits through at every session start and every compaction**, which is precisely why
    the early-return requirement above is load-bearing rather than tidiness.

  **Measured 2026-07-27 under low load — 10 samples, CPU 45% → 65%:**

  | | ms |
  | --- | --- |
  | min | **227** |
  | **median** | **313** |
  | max | 401 |

  This supersedes the earlier run, which recorded **100% CPU** throughout (median ~1.5 s, worst 3.3 s) and
  was recorded as a lower bound rather than a design figure. For comparison under the heavy-load
  conditions, Git Bash ~1.4 s and Windows PowerShell 5.1 ~1.4–3.1 s — the exe led throughout, which is the
  load-independent part of that result.

  **Design conclusion (SUPERSEDED — see the direct measurement below): a warm activation hook costs
  roughly a third of a second**, and `SessionStart` blocks the first turn, so the user waits it out at
  every session start, resume, clear and compaction. That is acceptable and needs no design escalation,
  but it is not free — it is the reason the early-return requirement above is mandatory rather than
  tidiness. *(The ~⅓ s figure came from the `--version` proxy. Measuring the real verb put it at ~0.4 s;
  the "acceptable, no escalation" judgement survives, the number does not.)*

  **Two caveats, stated rather than rounded away.** (1) 45–65% CPU is *low load*, not idle. An earlier
  draft called the figures "conservative" on the assumption that idle is strictly faster. **That was
  asserted, not reasoned** — two mechanisms pull in opposite directions:

  - *Contention* inflates the measurement: other work steals CPU from the spawn.
  - *Power states* deflate it: at 45–65% the CPU is already in C0 and clock-boosted, whereas from deep
    idle a spawn pays C-state exit and a frequency ramp. (Raised by agy's Measurement Skeptic seat.)

  The mechanism is real, but the **magnitude settles it**: this spec's own data shows contention
  dominating by an order of magnitude — the same command measured **~1.5 s median at 100% CPU** versus
  **313 ms at 45–65%**, a 5× swing from load alone. C-state exit is microseconds and frequency ramp is
  single-digit milliseconds, against a ~300 ms spawn dominated by image loading, host startup and JIT.
  So a true idle figure should land **at or modestly below 313 ms** — but that is now stated as a
  reasoned expectation, not as "conservative by construction", and it remains unmeasured.
  (2) **Cold start is still unmeasured at low load** — the binary was warm in the file cache throughout,
  and re-creating a genuine cold start needs a reboot. The only cold figure available is ~3.9 s, taken at
  100% CPU, and it should be treated as a heavily pessimistic outlier rather than the expected first-run
  cost. Measure it opportunistically after the next reboot.

  *Measured against the `--version` verb, which shares the `activation-payload` verb's code path exactly:
  both are early-return cases in the same switch that print one line before any MCP/DI initialisation.*

  **⚠️ THE PROXY INFERENCE ABOVE IS REFUTED — measured against the REAL verb, 2026-07-27.**

  Once `activation-payload` existed it was measured directly, interleaved round-robin with the proxy and
  with `print-config`, 12 samples each, at 22–33% CPU:

  | verb | min | median | max | Δ vs `--version` |
  | --- | --- | --- | --- | --- |
  | `--version` | 217 | **251** | 316 | — |
  | `print-config` | 258 | **292** | 406 | +41 |
  | **`activation-payload`** | 337 | **425** | 506 | **+174** |

  **The two verbs do NOT cost the same.** The real verb is ~1.7× the proxy, so the 313 ms figure
  understates the true blocking cost. `print-config` — which also serializes JSON but has nothing to do
  with the payload — carries only +41 ms of that gap, so generic `System.Text.Json` initialisation
  explains under a quarter of it. **The remaining ~133 ms is unattributed and would need profiling.**
  Recorded as an open question rather than guessed at.

  **Revised design conclusion: a warm activation hook costs roughly 0.4 s**, not a third of a second,
  and `SessionStart` blocks the first turn — so that is what a user waits at every start, resume, clear
  and compaction. Still acceptable, still no design escalation, but ~35% worse than this spec previously
  documented. If it ever needs reducing, the measured candidate is serializer initialisation
  (a source-generated `JsonSerializerContext`), worth ~40 ms of the 174.

  **Two methodology lessons, both learned by getting this wrong first:**
  1. *The measuring agent shares the CPU.* Step 2b's "abort unless load is in single digits" is
     **unsatisfiable on this machine** — with the driver fully idle for 20 minutes the floor never fell
     below ~25–32%. A gate that can never open is not a safeguard; it blocked this reading three times
     before the condition itself was questioned. Measure at the achievable floor and record it.
  2. *Measure verbs INTERLEAVED, never in blocks.* A first attempt ran them in sequence while background
     load fell 61%→30%, making whichever verb ran first look ~200 ms slower; that contaminated figure was
     briefly reported as fact. Round-robin distributes drift equally across all verbs.

  ▶ **Still owed, opportunistic:** a cold-start reading after the next reboot. The only cold figure is
  ~3.9 s at 100% CPU and remains a heavily pessimistic outlier.

- **Load-independent finding that strengthens Option B.** A hook command of the form `bash "…"` does not
  have a determinate interpreter on Windows. Measured on this machine, bare `bash` resolves **first** to
  `C:\WINDOWS\system32\bash.exe` — the **WSL** launcher — with `C:\Program Files\Git\bin\bash.exe` only
  third in `PATH`. WSL bash sees a different filesystem and would receive a Windows path it cannot
  interpret as intended. So Option A's real defect is not merely "bash may be absent" but "*which* bash
  is unknowable from the hook command", which no amount of packaging care fixes. This conclusion does not
  depend on timing and survives the load caveat.
- **Bounded length.** Hard budget: **≤ 15 lines**, and **≤ 1100 characters of PROSE**, excluding the
  `ToolSearch` load line.

  **Revised during execution (2026-07-27) after measurement — the original "≤ 1200 characters" total was
  unmeetable by this spec's own content.** The payload as designed measures 1434 characters, of which the
  load line alone is **456** (5 tools × 2 registration prefixes) and is irreducible: dropping either
  prefix form reinstates G3, the defect M0 exists to fix. Compressing the prose to fit 1200 landed on
  *exactly* 1200 with zero headroom, which is brittle by construction — the next word added would force
  deleting a safety rule.

  The instrument was wrong, not just the number. A raw character count conflates **mechanical API
  verbosity** with **conceptual bloat**: the load line is 456 characters expressing exactly one concept
  ("load these tools"). The line count is the real anti-creep guard, and the prose count now measures the
  only part that can actually bloat. The load line cannot grow unbounded — §7 criterion 4's allow-list
  caps it at the five named tools.

  Measured on landing: prose **973** characters (headroom 127), **8** lines. Rejected alternative: raising
  the total to 1500. It would have re-created the same failure the first time a sixth tool joined the
  allow-list, because prose would again be cut to pay for a tool name.
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

**Fifth criterion, added in round 4 — the trap must live in THIS TOOL'S OWN RETURNED DATA**, not in the
target application's behaviour generally. Without it, rule 3 ("specific to this tool") admits every quirk
of every app the tools can drive, and the descriptions become an unbounded encyclopedia that exhausts the
length budget the moment a second application needs driving notes. Raised by agy as *domain-boundary
corruption*; accepted, with the boundary drawn here rather than by dropping the mechanism. App-specific
driving lore stays in the skill's growth region; only "this tool's output will mislead you in this exact
way" earns a description slot.

Confirmed initial member (the observed failure): a Windows Terminal tab title is the **launcher, not the
program** — a bare `cmd.exe`/`PowerShell` tab may host a running CLI agent; enumerate and read every
candidate. Applies to `desktop_list_windows` and `desktop_read_terminal_tab`. It passes the fifth
criterion: the misleading artifact *is* the title string these tools return.

**Round 4 — the uncomfortable measurement M3 has to answer.** `desktop_list_windows` **already carries a
Windows Terminal hint** (`WindowTools.cs:23`): *"A Hint field may accompany multiplexer windows (e.g.
Windows Terminal) noting the listing shows only the active tab."* And `desktop_read_terminal_tab` already
says *"enumerate tabs with desktop_snapshot first"* (`ContentTools.cs:86`). **The motivating failure
happened anyway, with both of those already in place.**

This does not kill M3, but it refutes the framing that placement alone is sufficient — which is the claim
§5.4 opened with. The existing text states a *limitation* ("the listing shows only the active tab") and a
*procedure* ("enumerate first"); neither says the thing that would have prevented the failure: **the title
tells you nothing about what is running inside — read every candidate before concluding it is absent.**

Two consequences the plan must carry:

- Hoisted facts must be written as **actionable imperatives with the wrong default named**, not as passive
  notes. "X may occur" does not change behaviour; "do not conclude Y from X — do Z" does.
- The invariant test cannot assert mere keyword presence (that would already pass today on
  `desktop_list_windows`). It must assert the **specific imperative clause**, so the test fails on the
  current text rather than green-lighting it.

M3 remains the only mechanism addressing correct driving once activated, so it stays — but its expected
effect is now stated as *contingent on how the fact is phrased*, not on where it sits.

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
ROADMAP.**

**Round 5 — the original rationale was invalid and the decision is re-argued from scratch.** It read:
*"building a second hook integration doubles the surface for the weakest mechanism (§5.2)."* Round 2's
finding #16 retracted "weakest mechanism", so §5.5 was resting on a premise the document had already
withdrawn — a stale cross-reference deciding a real question. Caught by agy's Axiom Breaker and Q1.

The decision survives, but on different and narrower grounds:

- **agy has no hook surface at all.** `AgyConfigWriter.cs:32` states it directly: *"agy has no hooks."*
  This is not a cost trade-off, as the old rationale implied — there is no equivalent integration to
  build. M1 is unavailable for agy, not declined for agy.
- **The other three mechanisms genuinely do reach agy.** Measured: `AgyConfigWriter.DeploySkill()`
  (`:36-52`) writes **the same embedded seed resource** into agy's plugin dir. So M0 (load line) and M2
  (frontmatter) ship to agy through the same build input as Claude, and M3 rides in the tool descriptions.
- **What agy actually loses** is therefore only the session-start priming. The ROADMAP entry should record
  that specific gap — "agy gets M0/M2/M3, has no session-start injection point" — not a vague parity TODO.

**Refuted while folding this.** agy's Parity Auditor claimed M2 "silently fails to serve the second agent
runtime" and that agy users would be "permanently stranded on the broken skill frontmatter". Measured
false: `AgyConfigWriter.DeploySkill()` deploys the identical embedded resource
(`FlaUI.Mcp.Server.seed.driving-flaui-mcp.SKILL.md`) that `PluginArtifactWriter.WriteSkill()` uses. Both
runtimes are fed from the same build input, so an M0/M2 fix reaches both or neither. The stranding claim
is wrong; the hook-parity gap it was attached to is real and is recorded above.

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
4. **The `activation-payload` verb** — *not* a `flaui-activation.sh` script, which the round-4 decision
   removed from the design: emits well-formed JSON with `hookEventName == "SessionStart"`, serialized by
   the standard .NET serializer; **exits 0 on every path** (diagnostics to stderr); **invokes no `jq`,
   no shell**, and reads no stdin; returns before any MCP/DI initialisation; injected text is ≤ 15 lines
   and ≤ 1100 characters of **prose excluding the load line** (revised from a 1200-character total during
   execution — see §5.2 "Bounded length"), and every tool name in it is allow-listed per §5.2 item 6.
5. The M3 invariant test passes: every required trap fact is present in its named tool's `[Description]`,
   and **no description exceeds 1500 characters**, so hoisting cannot silently metastasize.

   **Measurement performed during execution (2026-07-27); this criterion's earlier guess was wrong
   twice.** The spec originally named `desktop_read_terminal_tab` "the current longest" and set 1200. It
   is not the longest — it is 754. The driver then re-guessed via a source scan and claimed
   `desktop_type` (1229); that was also wrong, because a text scan of `[McpServerTool…Description("…")]`
   matched only **38 of the 49** tools, silently missing every multi-line attribute form.

   **Measure by reflection over `[McpServerTool]`, never by scanning source.** Ground truth across all
   49 tools: the ceiling is **`desktop_watch` at 1403 characters**, then `desktop_type` 1229,
   `desktop_paste_text` 983, `desktop_find_text` 930. Budget set to **1500**, just above the measured
   ceiling, per this criterion's own rule. The two descriptions M3 amends land at 812 and 888.
6. `jq` is declared in the installer prerequisite check and `.claude/recommended-tools.json`. **Both are
   net-new construction, not edits** — measured in round 2: `.claude/recommended-tools.json` does not
   exist, and `installer/flaui-mcp.iss` has no prerequisite-check mechanism at all (its only `Check:` is
   `NeedsAddPath`). The plan must size this as building a check, not extending one.
7. **Plugin hooks and scripts are distributed (G6/G7).** A test asserts that the staging dir produced by
   `PluginArtifactWriter.Generate()` contains `hooks/hooks.json` and every script it references, and that
   the hook commands resolve under `${CLAUDE_PLUGIN_ROOT}` as staged. Without this, every other M1
   criterion can pass while M1 reaches nobody.
8. **Fidelity, not presence.** *(Round 3, corrected in round 4.)* For any script that still ships, a test
   asserts it is byte-identical to its repo-tree source **normalized to LF**, and that the staged file
   contains no CRLF. Criterion 7 alone is satisfiable by writing hardcoded strings from C#, which passes
   CI, works in the maintainer's manual test, and ships something else.

   *Round-4 correction:* the round-3 wording demanded byte-identity to the repo-tree source **and** no
   CRLF — **jointly unsatisfiable**, because the working-tree source is CRLF today
   (`plugins/flaui-mcp/scripts/flaui-curate-nudge.sh` measured as CRLF; the repo has **no
   `.gitattributes`**). A test writer would have had to fail the build permanently or quietly drop the
   byte-identity half. Both panels caught this independently. The plan must also add a `.gitattributes`
   pinning `*.sh eol=lf`.

   *Round 7 correction:* an earlier version said this criterion "may end up vacuous under Option B,
   because nothing ships". That contradicted §8, which scoped the `Stop` curate-nudge **script** back in.
   A `.sh` therefore always ships and the criterion is **never vacuous** — it always has
   `flaui-curate-nudge.sh` to validate. Option B removes the shell dependency from **M1**, not from the
   plugin as a whole.
9. **The propagation chain is gated end to end.** A test drives `PluginArtifactWriter.Generate()` into a
   temp dir and asserts the result is a well-formed plugin (manifest + skill + hooks + scripts). The
   `claude plugin install` step itself has no CI equivalent and is **not** claimed as gated — it stays a
   manual install-smoke step, named as such in the plan rather than assumed. Recording this honestly is
   the point: round 3 found that *nothing* connected the build input to the installed artifact.
10. The repo's default gate stays green with zero new warnings.

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
- ~~**Distributing the flaui-learn/flaui-curate skills**~~ — **SCOPED BACK IN, round 6.** This was listed
  as adjacent work in §9 and excluded here. That is no longer tenable: §5.2 makes shipping the plugin's
  hooks a precondition, and the hooks being shipped include the existing `Stop` curate-nudge, whose entire
  function is to tell the agent to run the `flaui-curate` skill. Shipping a hook that nags for a skill the
  user does not have is a broken, self-contradicting UX — and G6's fix creates that state the moment it
  lands, because today the hook is inert **for installed users**. *(Round 7: the unqualified "the hook is
  inert" survived the retraction of its premise — per G8 it fires for the maintainer via
  `.claude/settings.json`. The conclusion is unaffected: it is inert for the users G6 is about.)*

  The plan must therefore either (a) stage `flaui-learn`/`flaui-curate` alongside `driving-flaui-mcp`, or
  (b) not stage the curate-nudge hook. **(a) is recommended** — it also makes `plugin.json`'s existing
  description ("plus the flaui-learn/flaui-curate autotrain loop") true, which it currently is not.
  Raised by the Reversal Advocate seat; this is exactly the class of scope decision that a round-3
  precondition silently invalidated.
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
| 10 | Unspecified length budgets are trivially satisfiable | Mechanism Gamer (driver) | §7 criteria 4–5 — pinned at ≤ 15 lines / ≤ 1200 chars. **Superseded during execution:** the 1200 total was unmeetable by the spec's own payload (measured 1434, of which 456 is the irreducible load line). Now ≤ 15 lines / ≤ 1100 chars of prose excluding the load line — see §5.2 "Bounded length". The finding stands; only the instrument changed. |
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

### Round 3 — bespoke seat: Distribution Realist (the standard palette was exhausted in rounds 1–2)

Solo panel + agy escalation. Round 3 corrected round 2's own G6 wording, and again found the defect was
worse than the previous round had recorded.

| # | Finding | Seat(s) | Fold |
| --- | --- | --- | --- |
| 21 | **G7 — split brain.** The registered marketplace points at `%LOCALAPPDATA%\…\FlaUI.Mcp\plugin` (4 files); the repo's own `.claude-plugin/marketplace.json` is not registered at all. So `plugins/flaui-mcp/` is live for **nobody**, not merely "repo-checkout only", and `plugins/flaui-mcp/skills/…/SKILL.md` is registered nowhere. | Distribution Realist (driver); Axiom Breaker + Distribution Realist (agy) — convergent | New G7 with the registration table; §5.2 opening corrected to say the repo paths are build input, not a live plugin. |
| 22 | **G3 confirmed in the field.** The shipped skill on the installed machine contains the broken single-prefix load line right now. | Distribution Realist (driver) | G7 consequence 2 — recorded as measured, not theoretical. |
| 23 | Nothing connects the build input to the installed artifact. The edit→build→install→register chain has no gate at any link. | Q1 (agy); driver | New criterion 9, including the honest admission that `claude plugin install` has no CI equivalent and stays a manual smoke step. |
| 24 | **Presence ≠ fidelity.** Criterion 7 is satisfiable by re-authoring hooks as C# string literals: CI passes, the maintainer's manual test passes, and a different artifact ships. CRLF on extraction would also break the shebang silently. | Q4 (agy); Distribution Realist (driver) | §5.2 — hooks/scripts must be **embedded** from the repo tree and extracted byte-for-byte with LF pinned; new criterion 8 asserts byte-identity and no CRLF. |
| 25 | Every skill change needs a rebuild **and** a reinstall to reach anyone, with no staleness signal. | Distribution Realist (driver) | G7 — propagation chain written out; `status` must compare installed `plugin.json` version against the running assembly version. |

**Round 3 verdict:** NOT GREEN — 5 findings folded.

### Round 4 — bespoke seat: Operator Realist; Mechanism Gamer re-seated on criteria 7–9 only

| # | Finding | Seat(s) | Fold |
| --- | --- | --- | --- |
| 26 | **Criterion 8 was jointly unsatisfiable** — byte-identity to a CRLF source AND no CRLF. The working tree is CRLF and the repo has no `.gitattributes`. | Mechanism Gamer (driver + agy, convergent) | Criterion 8 — identity against the **LF-normalized** source; `.gitattributes` pinning `*.sh eol=lf` added to the plan. |
| 27 | ~~**The prototype M1 is modelled on has never worked.**~~ **RETRACTED in round 7 — see G8.** The claim was that `flaui-curate-nudge.sh` is CRLF (so bash chokes on `\r`), depends on `jq`, and was never registered. Measured: it **runs correctly** and fires for the maintainer via `.claude/settings.json`. | Operator Realist (driver); Q2 (agy) | The Option-B decision it fed **survives on independent grounds** (bash is non-deterministic on Windows — finding #34 — and Option B has no shell dependency at all). Nothing downstream may cite this row as live. |
| 28 | **M1's trigger list contradicts its own safety framing.** Item 2 advertises "read a background terminal tab"; item 4 blankets the list in "read-only, no lease, cannot disturb the user". But `desktop_read_terminal_tab` is `Destructive = true` and switches tabs. | Axiom Breaker (driver) | §5.2 item 2 — the list is now zoned by lease requirement. |
| 29 | **M3's confirmed trap fact is ALREADY partly shipped and did not prevent the motivating failure.** `WindowTools.cs:23` carries a Windows Terminal hint; `ContentTools.cs:86` already says "enumerate tabs first". | Operator Realist (driver) | §5.4 — the "placement is sufficient" framing withdrawn; hoisted facts must be actionable imperatives naming the wrong default, and the invariant test must assert the specific clause so it **fails on today's text** instead of green-lighting it. |
| 30 | M3's selection rule admits every quirk of every drivable app — an unbounded encyclopedia against a fixed budget. | Axiom Breaker / Q4 (agy) | §5.4 — fifth criterion: the trap must live in **this tool's own returned data**. App lore stays in the skill. |
| 31 | Design fork: shell script vs. product executable as the hook command. | Both panels, independently → **Option B** | §5.2 — decided in-spec with the comparison table. agy's counter-argument (orphaned hook favours A) **refuted**: the script lives in the same deleted tree, so the hazard is symmetric. |

**Round 4 verdict:** NOT GREEN — 6 findings folded, including one design fork decided. Two of the six
(#28, #29) are defects in mechanisms rounds 1–3 had left unexamined.

### Round 5 — bespoke seat: Parity Auditor; focused on §5.3 and §5.5, the least-reviewed sections

| # | Finding | Seat(s) | Fold |
| --- | --- | --- | --- |
| 32 | **§5.5's parity decision rested on a retracted premise** — it justified excluding agy by calling M1 "the weakest mechanism", which round 2 finding #16 had already withdrawn. | Axiom Breaker / Q1 (agy) | §5.5 re-argued from scratch. Decision stands, grounds replaced: `AgyConfigWriter.cs:32` — *"agy has no hooks"* — so M1 is **unavailable** for agy, not declined. ROADMAP entry sharpened to the specific gap. |
| 33 | **Option B broke three of §5.2's own requirements** — "no process spawn", "the gate is a `test -f`", and "never exit non-zero". | Cascade Analyst / Q2 (agy) | §5.2 — cost requirement rewritten as a measured budget; the gate restated (the exe running *is* the gate); the exit rule split into "when it runs" vs. "when it cannot be spawned", the latter being a blocking unknown the plan must resolve empirically. |
| 34 | **`bash` has no determinate meaning in a hook command on Windows.** Bare `bash` resolves first to `C:\WINDOWS\system32\bash.exe` (WSL), with Git Bash only third in `PATH`. | Cascade Analyst (driver) | §5.2 — recorded as a load-independent argument strengthening the Option-B decision. Option A's defect is not "bash may be absent" but "*which* bash is unknowable". |
| 35 | Hook latency was asserted, never measured; and the measurements taken were contaminated. | Parity Auditor (driver); user | §5.2 — indicative figures recorded **with an explicit non-authoritative caveat** (the machine was under heavy load at measurement time, flagged by the user), plus a requirement to re-measure idle and to determine whether `SessionStart` blocks the first turn. |

**Round 5 verdict:** NOT GREEN — 4 findings folded. Note that #33 and #34 are both consequences of
round 4's own decision: each round has now corrected its predecessor.

### Round 6 (final) — bespoke seat: Reversal Advocate, attacking §6 and §8

| # | Finding | Seat(s) | Fold |
| --- | --- | --- | --- |
| 36 | **Stale Option-A residue.** Three places still specified the rejected script: §5.2's opening named `flaui-activation.sh`, §5.2 told the implementer to emit JSON "via a here-doc" (meaningless in C#), and criterion 4 gated the script by name. | Axiom Breaker / Q4 (agy) | All three rewritten to the `activation-payload` verb. This was edit debt from rounds 2–3 that the round-4 decision orphaned — not a design defect, but the single most likely place an implementer would have built the wrong thing. |
| 37 | **§8 scoped OUT a hard dependency of work scoped IN.** Shipping the plugin's hooks (a §5.2 precondition) ships the `Stop` curate-nudge, whose only job is to tell the agent to run `flaui-curate` — a skill §8 declined to distribute. | Reversal Advocate / Q2 (agy) | §8 — curate/learn distribution **scoped back in**, with staging them recommended over dropping the hook. Also makes `plugin.json`'s existing description true. |
| 38 | The "never exit non-zero" rule was justified by a session-bricking risk that measurement disproved. | Q3 (agy) | §5.2 — requirement kept, rationale downgraded to hygiene (avoiding a visible error block every session). |
| 39 | **No latency race.** `SessionStart` hooks block the first turn — measured with a 5-second sleeping hook whose marker was present in the model's first response. | Cascade Analyst (agy) — **refuted**, but productively | §5.2 — the race is disproven, *and* the same measurement converts the early-return requirement from tidiness into a load-bearing constraint, since the user waits through the hook. |

**Q1 result — §6 survives.** All three rejected approaches were re-examined against everything learned in
rounds 2–5, and every rejection premise still holds: the `Stop`-hook interlock still fires after
commitment, AA2's gate is still circular under G2, and tool-surface shrinking is still unactionable under
G1. No reversal warranted. §6 is the only section to come through a dedicated adversarial seat unchanged.

### Round 8 — bespoke seat: Fold Integrity Auditor → **GREEN**

Seated to test the review's own recurring failure: **every prior round created new debt in the act of
folding the previous round's findings.** Round 8 asked whether round 7 did it again.

The driver's solo pass found four — all of them round-7 fold debt, all in the plan:

| # | Finding | Fold |
| --- | --- | --- |
| 48 | The Task 1 identity-test comment still said `plugins/.../scripts/` "is the copy that gets EMBEDDED and shipped" — the exact claim round 7 reversed | Rewritten: `.claude/hooks/` is the embedded build input; the twin is what the test guards |
| 49 | Task 1's file list and Steps 2–3 checked and normalized only the `plugins/` twin — leaving the *shipped* bytes unnormalized | Both copies now listed, checked and renormalized, live copy first |
| 50 | The `.gitattributes` comment asserted `CRLF breaks them (exit 0\r → numeric argument required)` — the retracted claim, restated as fact in code | Rewritten to interpreter-specific portability risk |
| 51 | Task 1's commit message and file list omitted the live copy | Corrected |

Those four were folded **before** the agy escalation, which then reviewed the corrected documents.

**agy's independent pass: `no new findings` from all three seats**, Q5 `none`, verdict GREEN. Its Q1, Q2
and Q4 answers directly confirm the four fixes above landed consistently across both documents.

**Round 8 verdict: GREEN.** Stated precisely rather than flattered: the *escalation* was clean across
three seats; the driver's solo seat found four items first, and they were documentation-consistency
defects — comments, file lists and a commit command — with no design or contract impact. The seat seated
specifically to catch fold debt found only fold debt, and the independent model found none after it.

**Review closed at 51 findings across 8 rounds.**

### Round 7 (re-green) — bespoke seats: Retraction Auditor, Measurement Skeptic

Required by the operator after G8 and the latency measurement changed the artifact. Scope was bound to
the new/changed material only; settled design was explicitly closed.

| # | Finding | Seat(s) | Fold |
| --- | --- | --- | --- |
| 40 | G7 consequence 3 claimed a maintainer must register the repo marketplace to see a hook fire | Retraction Auditor (driver) | Corrected — `.claude/settings.json` needs no marketplace. The surviving point is narrower: a maintainer-side hook says nothing about the shipped artifact. |
| 41 | The Option A/B table called CRLF hazards "live today" | Retraction Auditor (driver) | Softened to a shipping risk, not a current breakage. |
| 42 | §5.2 claimed CRLF "breaks the shebang" | Retraction Auditor (driver) | Wrong twice over: the live CRLF script runs, **and** the hook invokes `bash "<script>"` so the shebang is never consulted. Restated as interpreter-specific risk (WSL bash). |
| 43 | Round-4 ledger row #27 still asserted "the prototype has never worked" | Retraction Auditor (driver) | Struck through and marked RETRACTED, noting Option B survives on independent grounds (#34). |
| 44 | **Criterion 8's "may end up vacuous under Option B" contradicts §8**, which scoped the curate-nudge *script* back in — so a `.sh` always ships and the criterion is never vacuous | Axiom Breaker (agy) | Criterion 8 corrected. Option B removes the shell dependency from **M1**, not from the plugin. |
| 45 | §8's "today the hook is inert" survived the retraction of its premise | Retraction Auditor (agy) | Qualified to "inert **for installed users**" — the conclusion is unaffected. |
| 46 | Calling the latency figures "conservative" was **asserted, not reasoned**: from deep idle a spawn pays C-state exit and frequency ramp, so idle is not automatically faster | Measurement Skeptic (agy) | Mechanism accepted, conclusion **rejected on magnitude**: this spec's own data shows load dominating 5× (1.5 s @ 100% vs 313 ms @ 45–65%), against microsecond C-state exits. Restated as a reasoned expectation, not a construction. |
| 47 | The plan embedded `plugins/.../scripts/` — the copy registered nowhere — rather than the live copy the maintainer executes | Q4/Q5 (agy) | Plan Task 3 now embeds `.claude/hooks/flaui-curate-nudge.sh`, mirroring how the skill's build input is handled. |

**Round 7 verdict:** NOT GREEN — 8 findings folded. Six are retraction debt (consequences that outlived
the premise G8 removed), which is exactly what the bespoke seat was seated to find; two are substantive
(#44's contradiction, #47's wrong build input).

**Negotiation record for #46.** agy's mechanism is correct and worth keeping in the document, but its
conclusion — "the figures are optimistic" — does not follow: it never weighed the two effects. Contention
is measured here at ~1.2 s of swing; C-state exit and clock ramp are microseconds-to-milliseconds against
a ~300 ms spawn. Folded as a correction to the *reasoning*, not a reversal of the *number*.

**Round 6 verdict:** NOT GREEN — 4 findings folded, but the character has changed. Three of the four are
**edit debt** (stale text, a stale rationale, a scope line invalidated by a later round) rather than
defects in the design. The design itself drew one finding, and the section deliberately targeted for
reversal survived intact.

**REFUTED BY MEASUREMENT (round 5) — do not re-raise.** agy's Parity Auditor claimed M2 never reaches agy
users, who would be "permanently stranded". False: `AgyConfigWriter.DeploySkill()` (`:36-52`) deploys the
**same embedded seed resource** as the Claude path, so M0/M2 reach both runtimes from one build input.

**RE-RAISED AND REFUTED A SECOND TIME — do not raise again.** agy's round-4 Cascade Analyst and Q3/Q5 all
rest on the elevated-installer PATH premise that the round-3 ledger already recorded as refuted, and which
the payload it was sent restated as refuted. It is wrong for the same measured reason
(`installer/flaui-mcp.iss:13` is `PrivilegesRequired=lowest`; `CliResolver.cs:41-43` falls back to
`%APPDATA%\npm`). The *orphaned-hook* consequence it hangs on that premise is real on its own narrower
footing and is folded above; the premise is not.

**REFUTED BY MEASUREMENT — do not re-raise.** agy's Q5 headline was "silent plugin registration failure
due to installer PATH isolation: InnoSetup runs elevated, so `claude` on the user's npm PATH is invisible,
and `ClaudePluginRegistrar` silently skips." Both legs fail: `installer/flaui-mcp.iss:13` sets
`PrivilegesRequired=lowest`, so the installer never elevates; and `CliResolver` (`:6-8`, `:41-43`)
already falls back to `%APPDATA%\npm` and userprofile dirs precisely so PATH absence cannot break
resolution. The residual case — Claude Code installed *after* flaui-mcp — is already handled and
reported by `InstallStatus.DescribeClaudeSkill`, which tells the user to re-run `install --agent claude`.

**Negotiation record for #13.** agy named the right defect from the wrong evidence: it cited
`ClaudeSkillDeployer.cs:33-51` as "the sole mechanism that writes the plugin on install". Measured, that
class has **zero call sites** — it is dead code from the retired skill-directory model. The live path is
`PluginArtifactWriter` → `ClaudePluginRegistrar` → `claude plugin marketplace add`. The conclusion
survived the correction and was folded; the cited mechanism did not. This is the second time in this
review that agy has built a confident structural claim on an unverified architectural assumption and been
right about the consequence anyway — the finding is worth taking seriously, the citation is not worth
trusting.
