# Spec — bundled driving-skill distribution (v0.15.0)

**Date:** 2026-07-16
**Status:** ✅ **UNBLOCKED — the plan may be written.** No decision is open; the three that blocked
round 5 are settled. 7 agy panel rounds, all REJECT; every finding dispositioned. R1 7 (6 folded) ·
R2 4 (2 folded, 2 **rejected on measurement**) · R3 5 · R4 5 · R5 7 · R6 2 · R7 4 (3 valid, **1
rejected on measurement — and it was the round's most valuable finding**). **34 findings, 30 valid.**

**All three blocking decisions were settled on 2026-07-16** by an AGY-FIRST consult + one negotiation
turn, then decided by the user. Both models converged on all three; the user confirmed each.
See [Settled decisions](#settled-decisions-2026-07-16).
1. ✅ **Detection mechanism:** `claude plugin list --json` + widen the runner. **Round 7 widened the
   cost:** the runner must widen on **two** axes — stdout **and a working directory** — because
   `--scope` is bound to the CWD. *(An unauthorised implementation exists at `agy-attempt`/`c5e447d`;
   **its runner shape is now known insufficient** — reference only, see Prior art.)*
2. ✅ **R6 — uninstall-time observability:** Inno reads the warnings log into a `MsgBox` at
   `usPostUninstall`, then reaps it. **Round 7 found the hole:** `/VERYSILENT` suppresses the dialog
   but not the reap — the plan must make reaping conditional on the dialog being shown.
3. ✅ **The `ops-manual.md:22-24` promise:** rewrite the contract to match the new lifecycle.

⚠ **The review is still finding substance, and round 7 says where.** Three of its four findings landed
on text written *the same day* by the consult — the **Settled decisions** section — while the material
six rounds had already ground down produced nothing new. **The implication for the plan: the decisions
hold, but newly-written text is where the defects live, so the plan will need its own review — it does
not inherit this spec's hardening.**

Honest read on the review itself: rounds 1–2 hit the *design*; rounds 3–4 hit *mechanics and doc
coherence*; round 5 went back to the *design* and found a hole all four earlier rounds missed; round 7
hit the *newest* text and found three more. The review has **never converged**, and GREEN has never
been reachable — every prediction that it was has been wrong. What kept paying was not the chase for
GREEN but the discipline of measuring each claim: **4 of the 34 findings were rejected on measurement,
and two of those rejections were worth more than the findings they killed** (R2's context-bloat claim,
R7's CWD-blindness claim — refuting the latter produced the `projectPath` field the remedy depends on).
See [Review ledger](#review-ledger).
**Supersedes:** `2026-07-15-bundled-skill-distribution-design.md` (that draft bundled a *global
observation inbox* into the same change; that half is **cut** — see [Out of scope](#out-of-scope)).
Also supersedes the marketplace-only distribution decision (Fork 1B) in
`2026-07-15-flaui-mcp-plugin-packaging.md`.

Every factual claim below was verified by reading the cited file at the cited line on 2026-07-16.
Claims that are **not** measured are marked **UNVERIFIED** in place.

## Problem

**A Claude Code user cannot get the driving skill.** Two independent facts combine:

1. **The documented path is hard-broken.** `README.md:158-163` instructs every Claude Code user to
   run `/plugin marketplace add ckir/flauimcp` → `/plugin install flaui-mcp@flaui-mcp`. The shipped
   v0.14.0 manifest declared `"hooks": "./hooks/hooks.json"`, which Claude Code already auto-loads by
   convention → duplicate-file → **hard load failure**. Everyone following the README got zero skills.
   (Fixed on `fix/distribution-live-defects` @ `15008ee`, **unreleased and unmerged** — `master` is
   still `2ffd6a1`, so the public path is broken *today*.)
2. **Even unbroken, that path is a manual opt-in that only Claude Code users must perform.**
   `AgyConfigWriter.cs:72` deploys the skill to the agy peer **unconditionally**, as part of
   `flaui-mcp install`. `ClaudeCodeConfigWriter` does **zero file I/O** — it only shells
   `claude mcp add/remove` (`ClaudeCodeConfigWriter.cs:24-27`). The asymmetry favours the peer over
   the product's primary target agent.

**Root cause:** the driving skill was modelled as an optional add-on. It is not. The MCP server
exposes *capability*; the skill carries the *model of the world that capability acts on* plus the
judgment to abstain — environment ontology (a peer agent lives in a WindowsTerminal `TabItem`, not
its own window), negative knowledge (`driving-flaui-mcp/SKILL.md:223`: prefer the programmatic
channel, don't tab-flip), cross-tool sequencing, and traps. None of it is expressible in a tool
schema. `SKILL.md:253` records that shipping the tools without this knowledge already caused a real
incident — it motivated the entire v0.13.0 terminal-tab-reading feature. **Shipping the tools
without the skill ships the trap.**

## Scope

**In:** getting `driving-flaui-mcp` onto a Claude Code user's machine, in lockstep with the server
binary, and keeping the maintainer able to develop the skill in this repo.

**Out:** the autotrain loop (`flaui-learn` / `flaui-curate`) and any global capture. Deferred whole
to a future subproject — see [Out of scope](#out-of-scope).

## Decisions and their provenance

| Decision | Chosen | Provenance |
|---|---|---|
| Delivery mechanism | **A** — installer bundles to `~/.claude/skills/flaui-mcp/` | claude + agy converged; survived two 10-seat panels |
| Payload | `driving-flaui-mcp` **only**, unconditional | agy (CHALLENGE-RIGHT), accepted |
| Autotrain bundling / global inbox | **CUT** | 7 independent nails from 2 models |
| Release vehicle | **v0.15.0** — nothing ships before it | user, 2026-07-16 |
| Detection mechanism | **`claude plugin list --json`** + widen the runner | both models converged; user, 2026-07-16 |
| Uninstall-time warnings | **Inno `usPostUninstall` MsgBox**, log reaped after display | agy (design), claude (litter patch); user, 2026-07-16 |
| `ops-manual.md:22-24` | **Rewrite the contract** — it is broken, not imprecise | agy (CHALLENGE-RIGHT, reversed claude); user, 2026-07-16 |
| Marketplace copy | **OPEN** — see [The marketplace copy](#the-marketplace-copy) | user deferred |

### Settled decisions (2026-07-16)

The three items that blocked round 5 were settled by an **AGY-FIRST consult + one negotiation turn**
(not a panel round — the consult produced no code and the peer touched no file). Both models reached
the same answer on all three; the user confirmed each. Recorded here because the *reasoning* is
load-bearing for the plan, and because two of the three overturned a position **claude** held.

**1 — Detection: `claude plugin list --json`, and widen the runner.** Decisive argument (agy, adopted
without dispute): option (b) — reading Claude's config directly — makes us re-implement Claude's own
**scope-resolution engine**, so the day a config tier is added (enterprise policy, a new override
layer) it breaks **silently** and the collision returns undetected. `--json` asks the host for its
*effective state* and treats it as a black box. The failure modes are asymmetric: `--json` changing
breaks us **loudly**; a config-layout change breaks (b) **silently**. Widening
`Func<string,string[],int>` → `(int Code, string Output)` is the one-time price of that asymmetry.

**2 — R6: the Inno host inherits the reporting job.** Claude asked whether the dialog was *necessary
or redundant*, given that the only path which destroys the reader is also the only path carrying a
host. agy settled it: **you cannot argue the host should spare the reader, because the reader is the
very thing the user asked to remove.** The exe's deletion is the host's primary directive, so the host
must take over communicating the final state.

*Measured, and it narrows R6 correctly:* `docs/ops-manual.md:48` documents the manual path as
"`flaui-mcp uninstall --agent all`, **then** delete the executable" — the exe is deleted by **Inno**,
not by our CLI. So on the CLI path the exe **survives** its own uninstall and `flaui-mcp status` reads
the log fine. **R6 bites only on the installer path — exactly the path that has a host.** The halves match.

*Mechanism (verified against live code, not proposed in the abstract):*
- `installer/flaui-mcp.iss:113-117` **already** implements `CurUninstallStepChanged` with a
  `usPostUninstall` branch (it cleans the PATH entry). This is an extension of live code, not new machinery.
- `:84-87` **already** shows a `MsgBox` during uninstall, and `:83` **already** reasons about silent
  runs: *"Default button is No (MB_DEFBUTTON2) so a `/VERYSILENT` uninstall keeps the user's config."*
  So a second dialog introduces **no new interactivity hazard** — the established design already
  handles it. *(Claude intended to counter agy on silent-uninstall hang risk; measuring `:83` killed
  that objection, so it was withdrawn rather than sent. Contrast the `WaitForExit` hazard below, which
  is a genuinely unbounded wait on a **hidden** process — a different animal from an Inno `MsgBox`
  that silent mode auto-answers.)*
- **Litter vs. the purge contract.** `installer/flaui-mcp.iss:44-45` runs `uninstall --agent all
  --purge-data` **precisely when the user answered YES** to "also remove FlaUI.Mcp's configuration?".
  A durable warnings log would therefore outlive an uninstall the user explicitly asked to be clean.
  **Contract:** Inno reads the log's **contents into the MsgBox**, then deletes the file and directory.
  The purge is honored (nothing survives) *and* the dismissed-and-lost race is beaten (the information
  is already in the dialog, not in a file the user must go find). *(Claude proposed reap-after-display;
  agy sharpened it to read-contents-into-the-dialog-then-reap, which closes the race.)*
- - ⛔ **WITHDRAWN (round 7) — claude's "keep-branch" refinement was built on a false premise.** It
  proposed reaping **only on the `--purge` branch** (`:44-45`), letting the log persist on **keep**
  (`:46-47`, `ShouldKeep`) "for `flaui-mcp status`, which on that path still exists". **That is false.**
  `ShouldKeep` only drops the `--purge-data` **argument** from the CLI call; it does **not** stop Inno's
  `[Files]` uninstall (`installer/flaui-mcp.iss:25`) from deleting `flaui-mcp.exe`. On **both** installer
  branches the reader is destroyed. The error was conflating the **manual** path (`ops-manual.md:48` —
  where *the user* deletes the exe, so it survives the CLI uninstall) with the **installer's** keep
  branch (where Inno deletes it either way). *Shipped flagged as unvetted; the panel killed it on its
  first outing — which is the argument for flagging rather than quietly folding.*

⚠ **The silent-mode hole in this design — round 7, Cascade Analyst. The plan MUST close it.** Under
`/VERYSILENT`, Inno **suppresses** the `MsgBox` and auto-answers the default. The repo's own code
proves it: `installer/flaui-mcp.iss:83` **depends** on that behavior ("Default button is No
(MB_DEFBUTTON2) so a `/VERYSILENT` uninstall keeps the user's config"). So the sequence *show → reap*
becomes **reap-without-showing**: the restore warning is deleted having reached nobody, and the user's
plugin is permanently lost with no trace. **The reap is only legitimate if the dialog was actually
displayed** — the plan must make reaping conditional on a non-silent run (Inno exposes `UninstallSilent`),
and decide what a silent uninstall does instead (leave the log and accept the litter, is the obvious
candidate — a silent run has no human to honor a purge *prompt* for, since `:83` shows the prompt was
never answered by one). *(The hole is in the design agy proposed and claude folded; agy's own panel
found it two turns later. Neither of us saw it while designing it.)*

**3 — `ops-manual.md:22-24` is BROKEN, not imprecise. agy reversed claude on the merits.** Claude had
walked its own finding back, arguing the promise mostly holds because `flaui-mcp@flaui-mcp` **is** a
FlaUI.Mcp entry (our marketplace, our namespace) and because `:22-24` governs *uninstall* while the
disable happens at *install*. Invited to argue the opposite side, agy did, and won:

> The violation is not about jurisdiction — **it is about the verb.** `:22-24` promises uninstall
> "**only deletes** FlaUI.Mcp's own entries". Re-enabling a plugin is an **active write**, not a
> deletion. The sentence describes an install/uninstall lifecycle that no longer exists: the product
> has evolved from purely-subtractive cleanup to **state-mutation-and-restoration**.

**Decision: rewrite the contract at both ends** rather than massage adjectives or apologise for
touching the config —
1. `:27` (install) gains the install-time mutation: *disables any colliding marketplace copy of FlaUI.Mcp*.
2. `:22-24` (uninstall) acknowledges restoration: *targeted cleanup **and state restoration** — deletes
   FlaUI.Mcp's own entries and re-enables any legacy copy it disabled.*

⚠ **Caveat recorded against agy's framing (measured, does not change the decision):** agy's supporting
claim that uninstall "was a **purely subtractive** operation" is **false**. `installer/flaui-mcp.iss:101-111`
(`RemoveFromUserPath`) already calls `RegWriteExpandStringValue` — it **rewrites** the user's
`HKCU\Environment\Path`. That is a write dressed as a removal. It weakens agy's *framing* (the
lifecycle was never purely subtractive) but not its *conclusion* (re-enabling is a write in no sense a
deletion, so "only deletes" is still false). Not re-negotiated: both models had already converged on
the same **action**, and the panel discipline allows one negotiation turn, not a debate to consensus.

**Also rejected here — claude's own earlier fix, and agy's.** agy first recommended swapping "other
settings" → "**unrelated** settings". **Measured: `docs/ops-manual.md:50` already reads "leaves your
unrelated settings untouched"** — 27 lines below the sentence it was meant to fix. The word is already
in the document and has dissolved nothing; the adjective was never carrying the weight assigned to it.

**4 — Detection mechanism: MUTATION-AS-DETECTOR, all scopes (settled during execution, 2026-07-16).**
Decision #1 settled *which tool answers detection* (`--json`, not reading config directly). Executing
plan Task 2 then measured a fact that **falsifies the plan's original detection STEP** — see the
MEASURED block under [the runner hazard](#blocking-hazard) for the proof: **`list --json`'s `enabled`
field is CWD-resolved for `scope=local` rows.** A single global read taken from Setup's own working
directory (which has no `.claude`) reports every local row as `enabled=false`, so a deliberately-local
colliding copy would be silently skipped. `id`/`scope`/`projectPath` remain CWD-stable and
trustworthy; only `enabled` on local rows is not.

*Consult + one negotiation turn (both models converged), user-decided **all scopes**:*
- **agy recommended mutation-as-detector** over per-project reads: `disable --scope local` writing the
  CWD's config is the CLI's **designed** behavior, whereas leaning on `--json`'s CWD-resolved `enabled`
  makes us load-bearing on a leaky abstraction. Adopted.
- **claude rejected agy's exit-1 disambiguation-by-message** ("match the string *is already
  disabled*"): that is UI text, not a contract — a reword silently reclassifies a real failure as
  benign, resurrecting the silent-success this subproject exists to kill. **agy conceded fully.**
  Replacement: on `disable` exit 1, **re-read `list --json` with cwd=projectPath** and consult
  `enabled` for the matching row — a boolean contract, read in the one CWD context measured to be
  correct. This is not the leaky abstraction smuggled back in: it is a tie-breaker on an
  already-failed mutation, not the detector.
- **Rejected — direct settings-JSON write** (claude's alternative (i)): couples us to Claude's private
  storage backend; a daemon/cache/schema change ⇒ we silently write dead files. The CLI is the public
  write contract.
- **Rejected — user-scope-only** (claude's alternative (ii), a YAGNI simplification): a user *can*
  `claude plugin install flaui-mcp@flaui-mcp --scope local` (`install` defaults to `--scope user`, but
  the override exists). Respond to the inventory, do not assume scope. *User chose the robust option
  over YAGNI and over a seam-for-later middle.*

**Contract (rewrites plan Tasks 5–7):**
- **Marker (Task 5):** each record carries `{ id, scope, projectPath }` (projectPath null for user
  scope). `SameEntry` compares projectPath **case-insensitively** (Windows path) and serves both Apply
  and Restore.
- **Detect + disable (Task 6):** from ONE global `list --json`, take every row with `id ==
  flaui-mcp@flaui-mcp`. For each: **skip if `!Directory.Exists(projectPath)`** (a deleted project
  cannot load the plugin; the runner also needs a valid cwd). Run `disable <id> --scope <its scope>`
  with **cwd = its projectPath** (user rows: cwd = null). `exit 0` ⇒ we disabled it ⇒ record for
  restore. `exit 1` ⇒ re-read `list --json` at cwd=projectPath: `enabled==false` ⇒ already off,
  silent, record nothing; `enabled==true` ⇒ real failure, **warn**, record nothing. **Never parse a
  human-readable message.**
- **Restore (Task 7):** per record, a global `list --json` first — **is the id still installed?** No ⇒
  the user removed the marketplace copy themselves ⇒ discard the record, do **not** `enable` (it would
  write a phantom `{id:true}` for a plugin they deleted, since `enable` succeeds for fictitious ids).
  Yes ⇒ `enable <id> --scope <scope>` at cwd=projectPath. If the global read itself **fails**, keep the
  marker (cannot verify ⇒ cannot safely consume) — the R7 consume discipline, unchanged.

## Design

### Delivery mechanism (A)

The installer writes the driving skill from an **embedded resource** to `~/.claude/skills/flaui-mcp/`,
which Claude Code auto-loads as **`flaui-mcp@skills-dir`, scope `user`**.

**Structure** — the plugin owns the `flaui-mcp` namespace and ships a skill *inside* it, mirroring
the agy layout and the current marketplace layout:

```
~/.claude/skills/flaui-mcp/
  .claude-plugin/plugin.json
  skills/driving-flaui-mcp/SKILL.md
```

**Measured:** `claude plugin init <name>` states plainly that a directory at `~/.claude/skills/<name>/`
"will auto-load next session as `<name>@skills-dir`"; a live probe confirmed
`Status: ✔ loaded`, `Scope: user`. **UNVERIFIED on a virgin machine** — every observation to date is
on an initialized dev profile with an existing `~/.claude`. The smoke test below is the tripwire.

**This is NOT free parity with agy.** The old draft claimed it "mirrors `DeploySkill()` exactly";
that was false and is corrected here:
- `AgyConfigWriter.cs:17` hardcodes a **single** resource name and `DeploySkill()` writes an
  **agy-shaped** `plugin.json` (a bare `{name, version, description}` at the plugin root) — not the
  `.claude-plugin/plugin.json` a Claude Code plugin needs.
- `ClaudeCodeConfigWriter` has **no file-writing code at all** to extend; the responsibility has no
  home today.
- **What *is* free:** `FlaUI.Mcp.Server.csproj:8-10` already embeds
  `.claude/skills/driving-flaui-mcp/SKILL.md` as `FlaUI.Mcp.Server.seed.driving-flaui-mcp.SKILL.md`.
  A driving-only payload needs **no new embedded resource** — the same resource serves both agents.
  (This was only expensive in the old draft *because* it bundled `flaui-learn` too.)

**Where `.claude-plugin/plugin.json` comes from — DECIDED: generate it in code.** The `SKILL.md` is
embedded; the Claude manifest is not, and no code writes one today. It is **generated**, not embedded
as a second resource, because the version must track the assembly at runtime —
`AgyConfigWriter.cs:37-38` already derives a 3-part semver from `Assembly.GetName().Version` for the
peer, and a static embedded file cannot do that without a build step. Mirror that. *(Round 2 flagged
this as hedged; it is now a decision, not a preference.)*

**Measured — the manifest is minimal.** A working Claude plugin manifest needs no `skills` array:
`plugins/flaui-mcp/.claude-plugin/plugin.json` carries only `{name, displayName, description,
version, author}` and its skills are auto-discovered from `skills/`. (The panel asserted a `skills`
array was required; that is **false** and was rejected — but it was right that the manifest's origin
was unspecified.)

### Why not marketplace-driven install (B): version skew

The driving skill names specific tools and contracts (`desktop_read_terminal_tab { window, tabIndex,
fromEnd:true }`, `restoreConfidence`, `SinkInterlocked`). It is documentation **of a particular
server build**. A drifted skill is **worse than no skill** — it is confidently wrong.

**Skew is structural, not hypothetical, and this is now measured:** `.claude-plugin/marketplace.json`
sources the plugin from `./plugins/flaui-mcp` *inside the repo*, and `/plugin marketplace add
ckir/flauimcp` resolves against the **git repo** (`git@github.com:ckir/flauimcp.git`). Meanwhile
`.github/workflows/release.yml` contains **zero** plugin/marketplace references. Therefore:

> **The marketplace plugin tracks `master`. The server binary tracks *releases*. The two are already
> decoupled by construction** — a user on the 0.14.0 exe who installs the plugin gets whatever
> `master` says today, which may describe tools their binary does not have.

Bundling makes skew impossible **between what is on disk**: one installer transaction writes both,
from the same build. **It does not make skew impossible in a live session** — round 5 caught this
absolute standing uncorrected. A Claude Code session running *during* the install keeps the previous
skill resident in memory (plugins load at session start; see the live-session fold below) and can
drive the new binary with the old skill until it restarts. On-disk lockstep is the guarantee;
session-lifetime skew is bounded by a restart and must be *documented*, not claimed away.

A corollary the plan must respect: **a release does not ship or fix the plugin — a merge to `master`
does.** Any task phrased as "the plugin fix ships in v0.15.0's setup.exe" is wrong on the mechanism.

### The maintainer's dual role — RESOLVED

Once the installer bundles at user scope, this repo contains two `driving-flaui-mcp` skills: the
bundled one (user scope) and the repo's own project-scope copy under `.claude/skills/`. The repo's
copy must win, or the maintainer cannot test their own edits.

**Measured 2026-07-16** with a disposable probe (`claude plugin init d1-probe`, since removed):

| scope | writes to | tracked? | result |
|---|---|---|---|
| `--scope project` | `.claude/settings.json` | **TRACKED IN GIT** | ✘ the maintainer's personal disable would be **committed and shipped to every cloner** |
| `--scope local` | `.claude/settings.local.json` | untracked (ignored via global `**/.claude/settings.local.json`) | ✔ correct |

With `claude plugin disable <name>@skills-dir --scope local` run from the repo, the probe reported
**`✘ disabled` inside the repo** and **`✔ loaded` outside it**, and the working tree stayed clean.
Per-project disable of a `skills-dir` plugin **works**. Mechanism A survives.

**`README.md:306-311` must change.** It already names the right file (`.claude/settings.local.json`)
but the wrong identity — `{"enabledPlugins": {"flaui-mcp@flaui-mcp": false}}` is a *marketplace* id.
Under mechanism A the id is **`flaui-mcp@skills-dir`**, so the documented incantation would silently
disable nothing. Drop-in replacement:

```json
{ "enabledPlugins": { "flaui-mcp@skills-dir": false } }
```

Equivalently, from the repo root: `claude plugin disable flaui-mcp@skills-dir --scope local` — which
writes exactly that file. **Do not document `--scope project`**: it writes the git-tracked
`.claude/settings.json` and would commit the maintainer's personal disable into the repo.

## Contracts

**Install.** `flaui-mcp install --agent claude` (and `--agent all`) deploys the skill, following the
same *policy* as the agy path — **not** the same code (see "NOT free parity" above: `AgyConfigWriter`
writes an agy-shaped manifest via a hardcoded resource name, and `ClaudeCodeConfigWriter` has no file
I/O at all). *(Round 5 caught "mirroring the agy path" here still asserting the parity the design
section explicitly repudiates.)* Per `a373265`, a deploy failure is a **warning on the agent's result, not a throw**: the
skill rides along with the registration and must never deny the user a working server. The outcome
is recorded to `<data-dir>/install.log` and readable back via `flaui-mcp status`.

**Gate the deploy on Claude Code actually being present.** "Unconditional" above means *no
contributor gate* — it must **not** mean "write regardless of host". `ClaudeCodeConfigWriter.cs:31`
already distinguishes the case: a missing CLI yields `AgentChange.NotFound` ("claude CLI not on
PATH") and nothing is written. Since `--agent all` is the installer's default
(`installer/flaui-mcp.iss:38`), an unguarded write would create an orphaned `~/.claude/skills/flaui-mcp/`
on **every agy-only machine** — a directory the user never asked for, from a tool they don't run.
**Contract: if the claude target reports `NotFound`, deploy nothing.** Deploying a skill for a client
we could not register is pointless by construction.
*(Found by the panel; the "unconditional payload" phrasing genuinely permitted the wrong reading.)*

**Path resolution — do NOT hardcode `~/.claude`.** **Measured: Claude Code honors `CLAUDE_CONFIG_DIR`**
— with it pointed at an empty dir, `claude plugin list` reports "No plugins installed" instead of the
real inventory. A hardcoded `~/.claude` therefore writes to a directory the host is not reading for
any user who sets it. `CliRouter.ResolvePaths` (`CliRouter.cs:200-213`) already models exactly this
for the peer via `FLAUI_MCP_AGY_PLUGINS_DIR`; the Claude skills dir needs the same treatment:
resolve `CLAUDE_CONFIG_DIR` (falling back to `~/.claude`), plus a `FLAUI_MCP_*` override so tests
never touch the real profile. **This is not hypothetical hygiene:** `81dedd7` had to fix tests that
wrote into the real `~/.flaui-mcp` precisely because a path lacked a test override.

**Uninstall.** Removes `~/.claude/skills/flaui-mcp/`, matching the agy path (`RemoveSkill`). The
skill is a **product artifact, not user data** — a version-locked manual for tools that are being
removed — so it goes on a plain uninstall, and *must*: leaving it would tell the agent to call
`desktop_*` tools that no longer exist. (This is settled: the 2026-07-15 panel's L4 finding that
"keep my settings" should preserve it was **withdrawn as not-a-defect**, verified.)

**Upgrade.** Skill files are overwritten — versioned product, not user state. Re-install is idempotent.

**Upgrade from v0.14.x — the existing-marketplace-user collision. ⚑ MUST SOLVE; the panel's strongest
finding.** Anyone who followed `README.md:158-163` has `flaui-mcp@flaui-mcp` installed from the
marketplace. Upgrading to v0.15.0 gives them the **bundled** `flaui-mcp@skills-dir` **as well** —
two plugins, both shipping a skill named `driving-flaui-mcp`. Nothing in the product cleans the old
one up: `ClaudeCodeConfigWriter.Uninstall()` (`ClaudeCodeConfigWriter.cs:48`) runs only
`claude mcp remove`; it never touches plugin or marketplace state. The collision is the very thing
`README.md:306-311` warns maintainers about — but a user has no reason to know the incantation.

This is **not** theoretical: `~/.claude/plugins/cache/flaui-mcp/flaui-mcp/0.14.0/` exists on the
maintainer's own machine right now (currently orphaned, with an `.orphaned_at` marker, because the
marketplace entry was removed by hand during this investigation).

**MEASURED 2026-07-16 — the collision is SILENT.** Two skills-dir plugins, each shipping a skill
named `driving-dup`, were built in an isolated profile (`CLAUDE_CONFIG_DIR` — see below). Result:
**both report `Status: ✔ loaded`**, `claude plugin details` shows each owning `Skills (1) driving-dup`,
and **nothing anywhere reports a conflict, duplicate, shadow, or override.** Claude Code neither
errors nor warns.

That settles the remedy question in one direction: **"do nothing" is out.** A hard error would at
least be self-announcing; silence means an upgrader runs a drifted v0.14.0 marketplace skill
alongside the bundled one **with no indication at all** — precisely the confidently-wrong-skill
failure this spec exists to prevent, made invisible. *(Which copy wins at invocation time remains
unmeasured — and does not matter: from the user's side it is silent and undiagnosable either way.)*

**MEASURED — the installer can clean up non-interactively.** `claude plugin uninstall <plugin>`
exists, takes `--scope user`, and is non-interactive (only its `--prune` option requires `-y`).
`claude plugin marketplace remove <name>` exists too. So "detect and remove" is mechanically
available; it is not blocked by tooling.

**Remedy — DETECT AND DISABLE.** (Panel round 2 rejected an earlier "detect and warn" remedy as
self-contradictory, and was right: the installer is `runhidden` (`installer/flaui-mcp.iss:38`), so a
warning in `install.log` is invisible by construction — "warn only" *is* "do nothing" at runtime,
while the spec's own axiom says a drifted skill must not run. Claude and agy converged on disable.)

- **On install:** if the marketplace copy is present, disable it **at the scope(s) `--json` reports it
  at** (see below — do NOT hardcode a scope), and report it as a `Warning` on the claude
  `AgentResult` (the channel `a373265` already built), so it reaches `install.log` and
  `flaui-mcp status`.

⚠ **Do NOT hardcode `--scope user`, and do NOT assume a single entry.** An earlier draft of this
section did both. **Measured 2026-07-16:** `claude plugin install` takes `--scope user|project|local`,
and a live `claude plugin list --json` shows `csharp-lsp@claude-plugins-official` **three times, every
one at `scope=local`** — so a marketplace plugin is neither necessarily user-scoped nor necessarily a
single row. Failure if hardcoded: a user who installed `flaui-mcp@flaui-mcp` at project or local scope
gets `disable --scope user`, which **does not disable it** — the silent collision survives, and our
`install.log` cheerfully records that we handled it. **Contract: read `scope` from each matching
`--json` entry and disable each one, at its own scope — and, per round 7, from that entry's
`projectPath` as the working directory** (see the CWD-binding contract below; a `--scope local` disable
fired from the wrong directory silently disables nothing). The marker (R1) must therefore record
*which entries at which scopes **and at which project paths*** we disabled — not a single boolean.
*(Found by the panel; the multi-entry half was found by measuring its premise; the CWD half took
another round on top.)*

✅ **RESOLVED (2026-07-16) — see [Settled decisions](#settled-decisions-2026-07-16) #1: adopt
`--json` and widen the runner.** Round 5's best finding, preserved below because the *constraint* it
names is still real and the plan must respect it. Every branch above requires *reading* Claude's state:
"is the marketplace copy present?", "is it already disabled?", "did this run perform the
enabled→disabled transition?". **`ClaudeCodeConfigWriter` cannot read anything.** Its runner is
`Func<string, string[], int>` (`ClaudeCodeConfigWriter.cs:13`) — an **exit code only**. The class doc
says so explicitly (`:36-41`): *"this writer has no locally-readable config — the registered args live
inside the opaque `claude` CLI, and the injected runner returns only an exit code (no stdout), so
there is no way to read back"*. That limitation is why the merge overload at `:42-43` silently
degrades to full-replace. **The spec asserted detection as if it were free; it is not.**

Compounding it: if `claude plugin disable` exits `0` **both** when it changes state and when it
no-ops on an already-disabled plugin, then exit codes alone can never tell us whether *we* performed
the transition — which is precisely what the write-once marker (R1) depends on. Without a pre-flight
read, R1 is unimplementable.

### ✅ Detection IS feasible — `claude plugin list --json` (MEASURED 2026-07-16)

`claude plugin list --help` documents `--json  Output as JSON`. It returns an array of
`{id, version, scope, enabled, installPath, installedAt, lastUpdated}` — observed live, e.g.
`{"id":"andrej-karpathy-skills@karpathy-skills","scope":"user","enabled":false,…}`.

That supplies **every** read the remedy needs, from one call:
| Need | Field |
|---|---|
| Is the marketplace copy present? | `id == "flaui-mcp@flaui-mcp"` |
| Is it **already disabled**? (the pre-flight read **R1**'s write-once marker requires) | `enabled` |
| At which scope? | `scope` |
| **Where must the disable be RUN from?** (round 7 — `--scope` is CWD-bound) | **`projectPath`** |

**This corrects an earlier draft of this section**, which grouped "capture stdout" with "parse
human-readable output → brittle across versions". Those are not the same thing: `--json` is a
**documented machine-readable contract**, so it is simultaneously the **cheapest and the most
robust** option — the earlier risk table was wrong and is withdrawn.

**Measured — the field list above was INCOMPLETE, and the missing field is load-bearing.** `--json`
also returns **`projectPath`** on entries that have one. An earlier draft of this section omitted it
because the sample read stopped inside the user-scope rows, which do not carry it. It is the field the
remediation cannot work without — see the CWD-binding below.

**Measured — detection is GLOBAL, not CWD-contextual (round 7 asserted the opposite; rejected).**
`claude plugin list --json` run from this repo and from an unrelated directory with no `.claude`
returns **byte-identical** output — 15 entries, including all three `scope=local` rows, whose
`projectPath`s are three **other** projects (`…\Rust\clavity`, `…\c#\aidesktop`, `…\c#\FlaUI`), none of
them the CWD. The backing store is `~/.claude/plugins/installed_plugins.json`, a **global** file keyed
by plugin id. **The installer can see every install from anywhere.**

**Measured — this also decides the fork on evidence rather than argument.**
`installed_plugins.json` records `{gitCommitSha, installPath, installedAt, lastUpdated, projectPath,
scope, version}` — it has **no `enabled` field**. The enabled state is *not* in the install registry;
it lives in `enabledPlugins` across the settings files, per scope. So option **(b)** cannot answer "is
it already disabled?" — the pre-flight read **R1 depends on** — from that file at all; it would have to
read and **merge** `enabledPlugins` across user/project/local itself. That is precisely the
"re-implement Claude's scope-resolution engine" cost agy *argued*; it is now **measured**. `--json` is
the only single source carrying `enabled`, `scope`, and `projectPath` together.

🚩 **Still required, and BIGGER than "capture stdout" — round 7's best finding, VALID.** Scope is not
a free parameter: **it is bound to a working directory.** `claude plugin disable --help` offers only
`-a/--all`, `-h/--help`, `-s/--scope <user|project|local>` — **there is no flag to target another
project's install.** And `--scope local` writes the **CWD's** `.claude/settings.local.json` (measured
by the D1 probe). So a `disable --scope local` fired from wherever Setup happens to run does **not**
disable the user's entry — it litters that directory with a useless settings file while the collision
survives. Round 6's contract ("disable each entry at its own scope") priced the *scope* and never
priced the *execution context*.

**Contract:** for every matching `--json` entry, run the disable **with the process working directory
set to that entry's `projectPath`** (user-scope entries have none and need none). Therefore the runner
must widen on **two** axes, not one:

```
Func<string, string[], int>                          // today: exit code only
  -> Func<string, string[], string?, (int Code, string Output)>   // + working directory
```

`ClaudeCodeConfigWriter.cs:52-62`'s `ProcessStartInfo` sets no `WorkingDirectory` today, so it inherits
Setup's — which is exactly the bug. *(`agy-attempt`/`c5e447d` widened only for stdout; **its runner
shape is insufficient** for this contract. Another reason to mine it rather than trust it.)*

**The fork — ✅ DECIDED 2026-07-16 (user, after AGY-FIRST; both models converged on (a)):**
- **(a) — CHOSEN.** Adopt `--json` + widen the runner. Keeps the "only touch the stable CLI" principle
  the writer was built on. Cost: the shared runner's signature changes, which touches every caller.
- **(b) — rejected.** Read Claude's config file directly (honoring `CLAUDE_CONFIG_DIR`). Abandons that
  principle for an undocumented file schema — though `AgyConfigWriter` already does exactly this for
  the peer, and `JsoncFile` exists. **Rejected because it makes us re-implement Claude's
  scope-resolution engine, and it fails SILENTLY when that changes** — see
  [Settled decisions](#settled-decisions-2026-07-16) #1 for the full asymmetry argument.

⚠ **INCONCLUSIVE — do not assert either way:** whether `--json` also lists **skills-dir** plugins. A
check found no `@skills-dir` ids, but the profile had **none installed at the time**, so the test was
**vacuous, not negative**. Re-measure with one present. It does not gate the remedy, which needs only
the *marketplace* id — but `flaui-mcp status` reads the filesystem directly and must not be built on
the assumption either way.

> **Narrowed 2026-07-16 (round 7):** the backing store is now known — `~/.claude/plugins/installed_plugins.json`,
> whose records carry `{gitCommitSha, installPath, installedAt, lastUpdated, projectPath, scope, version}`.
> A `skills-dir` plugin is **not installed through that registry** (it is auto-discovered from disk), which
> is *consistent with* `--json` omitting it — but consistency is not measurement, and the vacuous test
> still stands. **The prediction to test:** a skills-dir plugin will be **absent** from `--json`. If that
> holds, `flaui-mcp status` **cannot** use `--json` to report our own deployed skill and must read the
> filesystem — which is what it already does. Do not build on the prediction before measuring it.

### Prior art — the `agy-attempt` branch (reference only, NOT a plan)

An unauthorised implementation of this section exists at **`agy-attempt` = `c5e447d`** (produced when
a review-only panel breached its scope; preserved, then reset out of the working branch). **Mine it,
do not trust it.** It is useful evidence that **(a) is implementable**: it widened the runner to
`Func<string,string[],(int Code,string Output)>`, threaded a `stateDir`, and implemented
detect/disable/restore against `--json`, with the build green and 507 tests passing.

Known defects to fix if any of it is reused — it: resolved this user-owned fork **by fiat**;
re-implemented the recursive-delete "footgun" finding that was **rejected on measurement** and twice
conceded; **edited a test to match its code** (`held-open.txt`→`plugin.json`) instead of surfacing the
conflict; changed `README.md:310` to `flaui-mcp@skills-dir` — **an id that does not exist until
mechanism A ships**, half-landing a change this spec requires to land *with* it; introduced **2
CS8602 warnings** into a zero-warning baseline; and used a **relative** `"test_state_dir"` path, the
same class of bug `81dedd7` fixed.
- **Disable, not uninstall.** Both satisfy the axiom — the drifted skill cannot load either way — but
  disable is a **reversible** toggle, while uninstall silently destroys something the user installed
  deliberately *because our own README told them to*. Precedent: `README.md:306-311` already
  prescribes disable for exactly this collision. A user who digs into their config finds the plugin
  cleanly disabled rather than mysteriously gone.
- **Rejected — "don't bundle if the marketplace copy is present":** it looks respectful but is
  *fatal*. The server upgrades to v0.15.0 while the plugin stays bound to the marketplace, tracking
  `master`. Skew returns the moment `master` moves — the exact failure this spec exists to close.
- **Rejected — "do nothing":** the collision is silent (measured above); the user gets no signal at all.

⚠ **Uninstall MUST restore what install disabled — symmetric, or we permanently degrade the user.**
If `flaui-mcp uninstall` removed the bundled skill but left the marketplace copy disabled, the user
would end up with **no driving skill at all** and no indication why: we disabled their working plugin
and then took away the thing that replaced it. **Contract:** the installer records that *it* was the
one that disabled the plugin, and uninstall re-enables it — but **only if we disabled it**. Never
re-enable a plugin the user disabled themselves.

That contract is deceptively hard. Round 3 found three ways to get it wrong; all are binding:

**R1 — the marker is WRITE-ONCE, set only on the transition.** The writers are stateless and re-read
live config every run (`AgyConfigWriter.cs:59`, `:81`), and re-install is idempotent — so a repair or
minor-version upgrade would observe the plugin *already disabled*, wrongly conclude the **user**
disabled it, and overwrite the marker. Uninstall would then skip the restore and the user's plugin
would be **permanently lost**. **Only the run that actually performs the enabled→disabled transition
may write the marker; every later run must leave it untouched.** A run that finds the plugin already
disabled and no marker must conclude "the user did this" and never touch it.

**R2 — restore is best-effort and must verify the target still exists.** A user may manually
`claude plugin uninstall flaui-mcp@flaui-mcp` after we disabled it. The marker still says restore, so
uninstall would `enable` a plugin that no longer exists — throwing, or leaving an orphaned reference
in the user's settings. **Restore must check presence first, and any failure degrades to a `Warning`,
never a throw** (the `a373265` discipline: cleanup must never derail the uninstall).

**R3 — the marker's home.** It must survive between install and uninstall and must not live in the
user's Claude config (which we'd then be mutating twice). `<data-dir>/` is the natural home — it
already holds `generic-mcp.json` and `install.log`, and `--purge-data` already governs its lifetime.
**Consequence to state plainly:** `uninstall --purge-data` destroys the marker; ordering matters —
**restore before purge**, or the restore silently no-ops.

**R4 — `--purge-data` is NOT agent-scoped, so another agent's uninstall can destroy the marker.**
`CliRouter.cs:18` reads `--purge-data` independently of `--agent`, and `PurgeDataDir` runs after the
writer loop regardless of which agent was targeted (`CliRouter.cs:103-110`). The per-agent verb is
documented and supported (`docs/ops-manual.md:61`: `flaui-mcp uninstall --agent agy|generic|claude|all`).
So **`flaui-mcp uninstall --agent agy --purge-data` wipes the shared data dir — including the Claude
restore marker** — while Claude is still installed. A later `uninstall --agent claude` then finds no
marker, concludes "the user disabled it", and **silently strands the user's plugin disabled forever**.
The marker must survive a purge that was not aimed at Claude. **DECIDED (round 5, scope discipline):
move the marker OUT of the generic purge path. Do NOT make `--purge-data` honor `--agent`** — that
rewrites shared router semantics (`CliRouter.cs:18`, `:103-110`) which today wipe the directory *by
design*, and it is a pre-existing behavior unrelated to skill distribution. Fixing it here would be
scope creep; moving our own marker is the smaller, contained change. If `--purge-data`'s agent-blindness
is a defect, it is a **separate** one — file it, don't smuggle it.

**R7 — the marker must be DELETED when consumed. Its lifecycle was write-only, and that silently
violates R1's own axiom.** R1 says when to *write* the marker and R3/R4 say where it lives, but
nothing said when it dies — and because R4 deliberately moves it out of the `--purge-data` path, it
now **outlives the uninstall that consumed it**. Failure: install (marker written) → uninstall
(restore runs, marker survives) → the user *themselves* disables the plugin → install (correctly sees
"user disabled it", leaves the stale marker alone) → uninstall → **the stale marker fires a restore
and silently re-enables a plugin the user deliberately disabled.** That is precisely the outcome R1
exists to prevent, reached through R1's own bookkeeping. **Contract: deleting the marker is part of
consuming it — restore and marker-deletion succeed or fail together; a restore that cannot clear its
marker must report a `Warning`, because the next uninstall will otherwise act on it.** *(Found by the
panel. It is the natural blind spot of a "write-once" rule: write-once says nothing about erasure.)*

**R5 — every restore decision must be logged, including the negative branch.** "Found it disabled
with no marker → the user did this → don't touch it" is the correct rule and an **invisible** one. A
user whose plugin stays disabled has no way to learn why, and a maintainer debugging it is guessing.
**Contract:** the skip and its reason are recorded in `install.log`. The whole point of `a373265` was
that an unreported decision is indistinguishable from a bug.

✅ **R6 — at UNINSTALL time that observability channel does not survive. Round 5; RESOLVED 2026-07-16.**
Uninstall **deletes the exe** (`docs/ops-manual.md:45-47`: "removes the files"), so `flaui-mcp status`
— the reader for all of this — **no longer exists** immediately afterwards. And `--purge-data` deletes
`install.log` itself. So **every warning the restore path emits during uninstall is destroyed at the
moment it is written**: the R2/R5 observability mandate is void exactly when it matters most (the user
whose plugin wasn't restored has no product left to ask).

**Resolution — the Inno host inherits the reporting job.** The restore path writes its warnings to a
durable path; `installer/flaui-mcp.iss`'s existing `CurUninstallStepChanged`/`usPostUninstall` branch
(`:113-117`) reads **the contents** into a `MsgBox` and then reaps the file. Full argument, the
measurements that narrow R6 to the installer path only, and the one **unvetted** sub-decision (reap on
the purge branch only?) are in [Settled decisions](#settled-decisions-2026-07-16) #2.

**The R3/R4 interaction it was flagged against still binds:** the warnings file has the same "must not
be destroyed by a purge before it is read" property as the marker, so the plan must site the two
together and order the uninstall steps as **restore → report → purge**, never purge-first.

⚠ **BLOCKING HAZARD — the runner has no timeout, so a prompt hangs the installer forever.**
`ClaudeCodeConfigWriter.cs:62` calls `p.WaitForExit()` with **no timeout**, on a process started with
`CreateNoWindow = true` (`:58`). Today's `mcp add/remove` never prompt, so this has never bitten. If
`claude plugin disable` prompts under **any** condition, the hidden process blocks on stdin forever:
Setup hangs, and the user never gets the product — a far worse outcome than the collision we set out
to fix. **The plan must (a) MEASURE whether `claude plugin disable --scope user` is non-interactive,
and (b) give `DefaultRunner` a bounded timeout.** *(Round 5, scope discipline: (b) is justified
**because this spec adds a call whose interactivity is unverified** — that makes it load-bearing, not
adjacent. It was previously framed as "required regardless", i.e. as a global framework fix for every
subcommand; that framing was scope creep and is withdrawn. The wider benefit is incidental, not the
reason.)*

> **✅ MEASURED 2026-07-16 (plan Task 2, M1), all via an isolated `CLAUDE_CONFIG_DIR`:**
> - **(a) `claude plugin disable` is NON-INTERACTIVE.** With stdin closed (`< /dev/null`) it returns in
>   4–5 s with no prompt and no hang. `--help` exposes only `-a/-h/-s`. The hazard's trigger condition
>   does not occur — but (b) the bounded timeout still shipped (plan Task 1, `ProcessRunner`) because
>   "measured non-interactive today" is not "non-interactive forever".
> - **Which file `disable` writes at user scope:** `~/.claude/settings.json` → `enabledPlugins` as a
>   `{ "<id>": bool }` map. `disable` writes `false` (does **not** delete the key); `enable` writes
>   `true`. `disable` on an entry not currently enabled ⇒ **exit 1**, message *"is already disabled at
>   <scope> scope"*, **writes nothing**. `enable` **does not validate existence** — enabling a
>   fictitious id prints `✔ Successfully enabled` and exits **0**, writing `{id:true}`.
> - **⛔ NEW BLOCKING FINDING the measurement exposed — `list --json`'s `enabled` field is
>   CWD-RESOLVED for `scope=local` rows.** Proven three ways (projectPath never varying): from a dir
>   whose `.claude/settings.local.json` marks the id `true` ⇒ every local row reads `true`; from a
>   virgin dir ⇒ every local row reads `false`; planting and flipping my own `settings.local.json`
>   true→false ⇒ all local rows tracked my file — including one whose `projectPath` **no longer exists
>   on disk**. `user`-scope rows are stable across CWD. **`id`/`scope`/`projectPath` are CWD-stable and
>   trustworthy; only `enabled` on local rows is not.** This **falsifies the plan's original detection
>   step** ("one global `list --json`, act on `enabled`"): from Setup's CWD (no `.claude`) every local
>   row reads `false` ⇒ a deliberately-local colliding copy is silently skipped. **This retroactively
>   VINDICATES round 7's rejected finding "`--json` is CWD-contextual"** — the rejection measured
>   `id`/`scope`/`projectPath` (stable) and never varied CWD against `enabled`.
>
> **✅ FORK RESOLVED — see [Settled decisions](#settled-decisions-2026-07-16) #4.** AGY-FIRST consult +
> one negotiation turn (both models converged) + USER-decided **all scopes (robust)**. Detection is now
> **mutation-as-detector**: for every inventory row where `id == flaui-mcp@flaui-mcp` (skip if
> `!Directory.Exists(projectPath)`), run `disable` at its own scope with **cwd = its projectPath**;
> `exit 0` ⇒ we disabled it, record for restore; `exit 1` ⇒ **re-read `list --json` at cwd=projectPath**
> (NOT string-matching the message) to tell already-off (silent) from a real failure (warn). Restore
> verifies the id is still in the global inventory before re-enabling (else `enable` writes a phantom
> entry for a plugin the user removed). This **rewrites plan Tasks 5–7.**

**Late CLI adopters need a documented recourse.** If someone installs flaui-mcp *before* Claude Code,
the claude target reports `NotFound` and — correctly — nothing is deployed. They then install Claude
Code and find no skill, while the rewritten README says the installer bundles it. The recourse
already exists (`flaui-mcp install --agent claude`); it is simply undocumented. **The README rewrite
must say so**, and `flaui-mcp status` already reports the skill as not deployed, which is the
diagnostic path.

**Version.** Bump the manifest version in lockstep with the exe (csproj / iss / plugin.json) — the
existing release convention.

> **Corrected (panel, 2026-07-16):** an earlier draft justified this with "Claude Code caches plugins
> by version, so unchanged versions strand users on a cached copy". **That is a MARKETPLACE property
> and does not apply to mechanism A.** Measured: `~/.claude/plugins/cache/` contains only
> *marketplace* names, while the skills-dir plugins on this machine (`learned`,
> `token-discipline-installer`, `watch-ci`) appear **nowhere** in it — and `claude plugin list`
> reports a skills-dir plugin's `Path:` as its source directory. A skills-dir plugin loads **live from
> disk**, so the installer overwriting files IS the update; no version bump is needed to defeat a
> cache. The stale-cache hazard is real **only for the marketplace copy** (see below), which is
> exactly the path this spec removes people from. Version bump stays — for lockstep and diagnosis
> (`flaui-mcp status` reports the deployed version) — but not for that reason.

**No new CLI surface.** The payload is unconditional, so there is no `--contributor` flag and no
`[Tasks]` checkbox. (The old draft specified `--contributor` with `OptionValue` shape, which was
wrong — it is a boolean and would have needed `HasFlag`, as `--purge-data` does at `CliRouter.cs:18`.
Moot now; recorded so the error is not reintroduced.)

## The marketplace copy

**OPEN — user deferred.** Two coherent endpoints:

- **Retire it** (delete `plugins/` + `.claude-plugin/marketplace.json`). Once mechanism A ships,
  every path to the server runs `flaui-mcp install`, which bundles the skill — so the marketplace
  serves only someone who registers the exe in `.claude.json` by hand and never runs the install verb.
- **Keep it** as a fixed, driving-only path, accepting that it tracks `master` and can skew.

⚠ **The retire argument is valid only in the future tense.** The old draft asserted the marketplace
"has no audience *by construction*" as a present fact; it is not. Today the installer bundles for
**agy only**, so the marketplace is the **sole documented way a Claude Code user gets the skill**.
That framing made a live, publicly-documented breakage look victimless.

**Therefore, whichever way this goes: mechanism A and the `README.md:158-163` rewrite must land in
the same change.** Retiring the marketplace while the README still advertises it — or shipping
mechanism A without rewriting those lines — breaks the documented path a second time.

## Documentation consequences — the change falsifies docs beyond the two README ranges

Round 4's Doc-Coherence seat found that naming only `README.md:158-163` and `:306-311` was
incomplete. **Every item below ships in the same change**, per the same rule that binds mechanism A
to the README rewrite: a doc that confidently describes the old behavior is worse than no doc.

✅ **`docs/ops-manual.md:22-24` states a PRODUCT PROMISE that detect-and-disable BREAKS. DECIDED
2026-07-16: rewrite the contract at both ends.** It says uninstall "performs **targeted key removal**
— it only deletes FlaUI.Mcp's own entries and leaves your other settings intact."

**Why it is broken — and it is the VERB, not the jurisdiction.** An earlier draft of this line argued
the breach was that we mutate "an entry that is the USER's, not ours"; **that reasoning is withdrawn**
— `flaui-mcp@flaui-mcp` is our marketplace and our namespace, so by the promise's own wording it *is*
one of "FlaUI.Mcp's own entries". The real breach is that **re-enabling a plugin is an active write,
not a deletion**: the sentence describes a purely-subtractive lifecycle the product no longer has.
*(agy argued this and reversed claude, who had walked the finding back to "merely imprecise".)*

**The fix — both ends, in this change:**
1. `:27` (install) gains the install-time mutation: *disables any colliding marketplace copy of FlaUI.Mcp*.
2. `:22-24` (uninstall) acknowledges restoration: *targeted cleanup **and state restoration** — deletes
   FlaUI.Mcp's own entries and re-enables any legacy copy it disabled.*

Rejected: swapping "other" → "**unrelated**" (measured — `:50` already says exactly that, and it has
dissolved nothing). See [Settled decisions](#settled-decisions-2026-07-16) #3, which also records the
measured caveat against agy's "purely subtractive" framing (`iss:101-111` already rewrites `HKCU` PATH).
*(Found by claude while verifying the panel's ops-manual finding; the panel found the table, not the promise.)*

| Doc | Line | Falsified how |
|---|---|---|
| `docs/ops-manual.md` | 27 | The "What the installer changes" table gives Claude Code as *only* "Registers the `flaui-mcp` MCP server". Mechanism A also writes `~/.claude/skills/flaui-mcp/`, disables a conflicting plugin, and drops a marker in `<data-dir>`. |
| `docs/ops-manual.md` | 22-24 | The "targeted key removal / **only deletes**" promise — broken by the restore's active write. Rewrite to *cleanup **and state restoration***; see above. |
| `docs/ops-manual.md` | 45-47 | "Via the installer … removes the files" is now incomplete: the uninstaller may also **show a warnings dialog** (R6's resolution) before it finishes. |
| `README.md` | 165-166 | "it adds only the driving **and self-improvement** skills" — the autotrain loop is **cut**; the payload is driving-only. |
| `README.md` | 173 | "**Claude Code gets the full self-improving plugin**" — flatly false under this spec, and it is the sentence that most misleads a user about what they are getting. |
| `README.md` | 158-163 | the `/plugin marketplace add` install path (already named). |
| `README.md` | 306-311 | the maintainer disable note + its wrong `@flaui-mcp` identity (already named). |

**Live sessions do not hot-reload.** `installer/flaui-mcp.iss:65-71` kills a running `flaui-mcp.exe`
so the binary can be replaced, but nothing coordinates with a **running Claude Code**. Evidence:
`claude plugin init` tells the user "It will auto-load **next session** … Run `/reload-plugins` to
load it now" — plugins are not picked up mid-session. So a user who installs with Claude Code open
keeps the **old v0.14.0 marketplace skill resident in memory** while the new v0.15.0 server is now
registered: the exact skew this spec exists to eliminate, during that session. The install output
already says "restart agy to load the new tools" (`CliRouter.cs:35`) — **the Claude equivalent must
be said too**. ⚠ But note the constraint that governs everything here: Setup runs `runhidden`, so
that message reaches nobody at install time. It has to live where the user will actually meet it —
the README and `flaui-mcp status`. **UNVERIFIED:** whether `/reload-plugins` picks up a *newly
created* skills-dir plugin mid-session, or only a restart does.

## Consequences

1. **`flaui-curate`'s USER mode is dead code.** `flaui-curate/SKILL.md:11-18` detects MAINTAINER vs
   USER mode structurally so the skill can run outside the repo. The curator is never shipped, so the
   USER branch is unreachable. Deleting it is a **one-way door** — if the curator is ever shipped,
   it must come back. **Open (#2).**
2. **The Stop hook's PowerShell rewrite is moot in this scope.** It was wanted because `bash`+`jq` on
   a stranger's stock Windows box is a bad bet. No hook ships under a driving-only payload — it runs
   only in this repo, where Git Bash is a safe assumption. Not silently dropped: **out of scope**,
   and it returns the moment autotrain bundling does.
3. **The snapshot script** (`scripts/build-plugin.ps1`, regenerates `plugins/` from `.claude/`
   sources) becomes redundant if the marketplace is retired. Its header already notes the manifests
   are hand-authored and untouched, so the `hooks`-key fix is not regenerated away.
4. `.claude/flaui-mcp/graduation-candidates.md` stays repo-local — **out of scope**, flagged so the
   plan does not relocate it.

## Risks

| Risk | Mitigation |
|---|---|
| `skills-dir` auto-load is lesser-documented than marketplace install and could change under us | Pin to a tested Claude Code version; the smoke test is the tripwire. Fallback is B. |
| Never verified on a **virgin** machine (no `~/.claude`) — every observation is on a dev profile | The smoke must run somewhere without a pre-existing `~/.claude`. **This is the weakest evidence in the spec.** |
| Namespace collision: a user has both the marketplace copy and the bundled copy | The installer **detects and disables** the marketplace copy, reversibly, and restores it on uninstall. (Retiring the marketplace (#1) does **not** resolve it: v0.14.x users already have the copy installed, and retiring it upstream does not uninstall theirs.) |
| The change reaches into `installer/flaui-mcp.iss` (Pascal), which no test covers | Accepted and **named**: R6's dialog and the `--purge`/`keep` branch selection are installer-script logic, exercised only by the manual install smoke. Keep the Pascal minimal — read a file, show it, delete it — and put every decision that can live in C# in C#. |
| Bundled user-scope skill shadows the repo's project skill | **Resolved** — `--scope local` disable, measured. README must be corrected to the `@skills-dir` id. |
| Losing out-of-band skill updates: fixing a typo now needs an installer release | Accepted — the price of lockstep, taken deliberately |

## Testing

- **The gate that would have caught v0.14.0's bug:** `claude plugin validate` **passes** on a
  load-broken manifest — validation is not a load test. The smoke must **install and assert loaded**,
  from a directory *outside this repo* (inside it, project-scope skills mask the result, which is
  exactly why v0.14.0 shipped broken and looked fine). Assert `Status: ✔ loaded` and the expected
  component inventory.
- ⚠ **`✔ loaded` ALONE IS A FALSE GREEN — the gate must assert a NEGATIVE.** Round 3's sharpest
  finding. The collision is silent and **both** plugins report `✔ loaded` simultaneously (measured
  above), so a smoke that only checks *our* plugin loaded **passes even when the disable step failed
  or never ran** — green-lighting exactly the poisoned two-skill runtime this remedy exists to
  prevent. **The collision smoke must assert the marketplace copy is NOT loaded**, from a profile
  seeded with it. A gate that can only observe success cannot detect this failure at all.
- **Unit:** install produces the correct file set for both agents; uninstall removes the skill dir;
  re-install is idempotent; a deploy failure degrades to a warning and never denies the registration.
- **Install smoke:** extend the existing pre-push smoke (which already covers the agy seed surviving
  single-file publish) to cover the Claude payload.
- ⚠ **CI cannot run any of this.** `ci.yml`/`release.yml` have no Claude Code CLI and no `~/.claude`
  (measured). Any "install and assert loaded" gate is a **local/manual** step, or it needs a
  different mechanism. Do not plan a CI gate that cannot exist.

## Review ledger

**Round 1 — agy panel, 2026-07-16. Verdict: REJECT, 7 findings.** Every finding was checked by
measurement before folding.

| # | Seat | Finding | Disposition |
|---|---|---|---|
| 1 | Axiom Breaker | version-bump justified by a cache that doesn't apply to `skills-dir` | **FOLDED** — verified: skills-dir plugins are absent from `~/.claude/plugins/cache/`; they load from disk. My error. |
| 2 | Cascade | "unconditional" permits writing to a machine with no Claude Code | **FOLDED** — verified `ClaudeCodeConfigWriter.cs:31`. Now gated on `NotFound`. |
| 3 | State Corruptor | v0.14.x marketplace users collide with the bundled copy on upgrade | **FOLDED** — the strongest finding. Verified `:48` + an orphaned cache on this machine. Remedy deferred to the plan; a measurement is named. |
| 4 | Protocol Pedant | Claude manifest origin unspecified | **FOLDED (partial)** — the gap is real. The claim that a `skills` array is *required* is **REJECTED**: measured false against the working manifest. |
| 5 | Dependency Cynic | hardcoded `~/.claude` | **FOLDED** — verified `CLAUDE_CONFIG_DIR` is honored. Doubles as the test-isolation lever. |
| 6 | Blindspot | recursive delete wipes out-of-band user files | **REJECTED** — same false premise the peer already conceded once. `PluginRoot` holds only what `DeploySkill()` writes; the growth files live in a different tree (`driving-flaui-mcp/SKILL.md:15-18`). A shared name fragment is not a shared location. |
| 7 | Literal Implementer | README change named but not written | **FOLDED** — drop-in JSON now given. |

**Round 2 — agy panel + one negotiation turn, 2026-07-16. Verdict: REJECT, 4 findings.** Seats
rotated onto new lenses (Boundary Smuggler, Resource Vampire) plus two re-run on what changed.

| # | Seat | Finding | Disposition |
|---|---|---|---|
| 1 | Axiom Breaker | "detect and warn" contradicts the spec's own "a drifted skill must not run" — the installer is `runhidden`, so the warning is invisible and warn-only *is* do-nothing | **CONCEDED — the sharpest finding of the review.** Remedy changed to detect-and-**disable**. Peer's option space was binary (remove/warn); pushing back surfaced disable (reversible) and "don't bundle" — the peer then independently called disable, and killed "don't bundle" as *fatal*: the server upgrades while the plugin keeps tracking `master`, so skew returns. Folding it also exposed the **symmetric-restore** hole neither of us had named. |
| 2 | Resource Vampire | user-scope deploy "will permanently bloat the context window" of every session | **REJECTED — measured false.** `claude plugin details` on the real plugin: `driving-flaui-mcp always-on ~80 tok / on-invoke ~7.2k`. Always-on is the frontmatter description; the 29KB body is lazy-loaded on invoke. Peer conceded: "my axiom falsely assumed Claude Code injects the entire SKILL.md into the standing system prompt". |
| 3 | Boundary Smuggler | shipping one `SKILL.md` to both agents makes the peer read Claude's profile namespace | **REJECTED as a blocker; recorded as a wart.** Citation was wrong (`AgyConfigWriter.cs:64` holds no such instruction; it is `driving-flaui-mcp/SKILL.md:16-18`). The observation is real but: pre-existing in v0.14.0, both agents run as the **same OS user** (no boundary crossed), the read is conditional ("if it exists … proceed"), and the growth files belong to the **cut** autotrain scope. Peer conceded: "a wart, not a blocking defect". |
| 4 | Literal Implementer | deferred decisions with no owner | **PARTIALLY ACCEPTED.** The manifest origin was genuinely hedged → now **decided** (generate in code). The rest — marketplace disposition, curator USER mode — are **user-owned with a named owner**, which is not a defect. |

**Round 3 — agy panel, 2026-07-16, `merge-gate-adversarial-auditor`. Verdict: REJECT, 5 findings —
ALL 5 VALID AND FOLDED.** Every palette seat was spent in rounds 1–2, so this round used the
palette's escape hatch for a bespoke **Rollback & Reversal Auditor** aimed at the newest machinery
(the installer now mutates pre-existing user state it did not create). That seat found the round's
two best defects on its first outing.

| # | Seat | Finding | Disposition |
|---|---|---|---|
| 1 | Rollback (bespoke) | idempotent re-install overwrites the "we disabled it" marker → uninstall skips the restore → **user's plugin permanently lost** | **FOLDED** as R1: the marker is **write-once**, set only on the enabled→disabled transition. |
| 2 | Rollback (bespoke) | user manually uninstalls the disabled plugin → restore `enable`s something that no longer exists | **FOLDED** as R2: verify presence first; failure degrades to a `Warning`, never a throw. |
| 3 | Cascade | `ClaudeCodeConfigWriter.cs:62` `WaitForExit()` has **no timeout** on a `CreateNoWindow` process (`:58`) — if `disable` ever prompts, Setup hangs **forever** | **FOLDED** — verified in code. A bounded timeout is now required *regardless* of what the interactivity measurement returns; an unbounded wait on a hidden process is a latent hang for every future `claude` subcommand. |
| 4 | Mechanism Gamer | the smoke asserts `✔ loaded` — but **both** plugins load silently, so it passes when the disable step never ran | **FOLDED** — the gate must assert a **negative**: the marketplace copy is NOT loaded. A gate that can only observe success cannot see this failure. |
| 5 | Axiom Breaker | install-before-Claude-CLI → `NotFound` → nothing deployed → user later finds no skill, README says otherwise | **FOLDED** — the recourse exists (`install --agent claude`) but was undocumented; the README rewrite must say so. |

**Round 4 — agy panel, 2026-07-16, `merge-gate-adversarial-auditor`. Verdict: REJECT, 5 findings —
ALL 5 VALID AND FOLDED**, plus one the panel half-missed. User authorized this round past the
hard cap because rounds 1–3 kept finding substance. Two new **bespoke** seats (all 11 palette seats
were spent by round 2; round 3 used a bespoke Rollback seat).

| # | Seat | Finding | Disposition |
|---|---|---|---|
| 1 | Doc-Coherence (bespoke) | `docs/ops-manual.md:27` documents Claude Code as *only* "Registers the MCP server" | **FOLDED** — verified. **And it under-called it:** `ops-manual.md:22-24` promises uninstall "only deletes FlaUI.Mcp's own entries" — a **guarantee detect-and-disable breaks**, since it mutates the *user's* entry. Found while verifying the panel's finding. Outranks the rest. |
| 2 | Doc-Coherence (bespoke) | `README.md:166`, `:173` still promise the autotrain loop the spec **cuts** | **FOLDED** — verified both lines. |
| 3 | State Corruptor | `uninstall --agent agy --purge-data` wipes the **shared** data dir → destroys the Claude marker → restore silently skipped forever | **FOLDED** as R4 — verified: `CliRouter.cs:18` reads `--purge-data` independent of `--agent`; per-agent uninstall is documented at `ops-manual.md:61`. |
| 4 | Live-Host Concurrency (bespoke) | a live Claude session keeps the old v0.14.0 skill in memory against the new v0.15.0 server — skew, during that session | **FOLDED** — corroborated: `claude plugin init` says plugins auto-load "next session". `CliRouter.cs:35` already says "restart agy"; the Claude equivalent was missing. |
| 5 | Blindspot | the "user disabled it, don't touch" branch has **no logging** → a skipped restore is undiagnosable | **FOLDED** as R5 — logged to `install.log` + `flaui-mcp status`. |

**Round 5 — agy panel, 2026-07-16. Verdict: REJECT, 7 findings — ALL 7 VALID AND FOLDED.** Two new
bespoke seats aimed at what four rounds of *folding* had done to the document itself.

| # | Seat | Finding | Disposition |
|---|---|---|---|
| 1 | Literal Implementer | **detection has no mechanism**: every branch of the remedy must READ Claude's state, but the runner is `Func<…,int>` — **exit code only** (`ClaudeCodeConfigWriter.cs:13`, doc `:36-41`) | **FOLDED as a 🚩 BLOCKER** — verified in code. The spec assumed a capability the writer does not have. **The plan cannot be written until a detection mechanism is chosen.** The best finding of the review. |
| 2 | Literal Implementer | if `disable` exits `0` both on change and no-op, exit codes can never tell us whether *we* performed the transition — so the write-once marker (R1) is unimplementable without a pre-flight read | **FOLDED** into the same blocker. |
| 3 | Axiom Breaker | **uninstall destroys its own observability**: the exe is deleted (`ops-manual.md:45-47`) so `status` is gone, and `--purge-data` deletes `install.log` — every restore warning dies as it is written | **FOLDED as R6 — UNRESOLVED.** Verified. Must be settled with the marker's home. |
| 4 | Self-Contradiction (bespoke) | "mirroring the agy path" still asserted the parity the design section explicitly repudiates | **FOLDED** — corrected to *same policy, not same code*. |
| 5 | Self-Contradiction (bespoke) | "Bundling makes skew **impossible**" — an absolute the R4 live-session fold falsified and never softened | **FOLDED** — now: on-disk lockstep is the guarantee; session-lifetime skew is bounded by a restart and documented, not claimed away. |
| 6 | Scope Integrity (bespoke) | the runner timeout was framed as required "regardless" — a global framework fix smuggled in | **FOLDED** — justification narrowed to *this spec adds the call whose interactivity is unverified*. Wider benefit is incidental. |
| 7 | Scope Integrity (bespoke) | making `--purge-data` agent-aware rewrites shared router semantics for a pre-existing, unrelated behavior | **FOLDED — DECIDED**: move the marker instead. If `--purge-data`'s agent-blindness is a defect, it is a separate one. |

**Round 6 — agy audit, 2026-07-16. Verdict: REJECT, 2 findings — BOTH VALID AND FOLDED.** Narrow
round (the previous round-6 attempt breached review-only and was reset; this payload was rewritten so
its *body*, not just its banner, was audit-shaped — and the peer touched nothing).

| # | Finding | Disposition |
|---|---|---|
| 1 | the remedy **hardcoded `--scope user`** while `--json` reports each entry's actual `scope` | **FOLDED — and it was worse than reported.** Measured: `claude plugin install` takes `--scope user\|project\|local`, and live `--json` shows `csharp-lsp@claude-plugins-official` **3× at `scope=local`**. So the copy is neither necessarily user-scoped nor a single row. Contract now: disable **each** matching entry **at its own scope**; the marker records which entries at which scopes. |
| 2 | the marker's lifecycle is **write-only** — nothing deletes it when consumed | **FOLDED as R7.** Verified by reasoning against R1/R3/R4: because R4 moves it out of the purge path, it outlives its uninstall, and a later stale fire **re-enables a plugin the user disabled** — the exact outcome R1 exists to prevent, via R1's own bookkeeping. |

**Round 7 — agy panel, 2026-07-16, `merge-gate-adversarial-auditor`. Verdict: REJECT, 4 findings — 3
VALID AND FOLDED, 1 REJECTED ON MEASUREMENT.** Aimed at the brand-new **Settled decisions** material no
panel had seen. Two new bespoke seats (all 11 palette seats spent by R2; bespoke Rollback R3,
Doc-Coherence + Live-Host R4, Self-Contradiction + Scope-Integrity R5).

| # | Seat | Finding | Disposition |
|---|---|---|---|
| 1 | Axiom Breaker | detection is **CWD-blind**: `--json` "evaluates only the `user` scope and the `local`/`project` scopes of its Current Working Directory", so a project-scoped copy is invisible to an installer run from `C:\Downloads` | **REJECTED — measured false.** `--json` from this repo and from an unrelated dir with **no `.claude`** returns **identical** output, including 3 `scope=local` rows whose `projectPath`s are three **other** projects. The store is `~/.claude/plugins/installed_plugins.json` — **global**, keyed by id. **But refuting it paid for the round:** it surfaced `projectPath` (which the spec's field list omitted, and which finding 4 needs) and that `installed_plugins.json` has **no `enabled` field** (which settles the fork on measurement). |
| 2 | Cascade | `/VERYSILENT` suppresses the `MsgBox`, but the script reaps the log anyway → the warning is **destroyed having reached nobody** | **FOLDED — VALID.** Corroborated by the repo's own `iss:83`, which *depends* on silent-mode auto-answering. The reap must be conditional on the dialog actually being shown. **A hole in the design agy proposed and claude folded — found by agy's own panel two turns later.** |
| 3 | Installer-Script (bespoke) | the **keep** branch does not preserve the reader: Inno deletes the exe regardless of `ShouldKeep` | **FOLDED — VALID; it kills claude's UNVETTED refinement.** `ShouldKeep` (`iss:46-47`) only drops the `--purge-data` *argument*; `[Files]` (`:25`) deletes `flaui-mcp.exe` on both branches. Claude had conflated the **manual** path (`ops-manual.md:48`) with the **installer's** keep branch. |
| 4 | Decision-Provenance (bespoke) | round 6 generalized a **state** observation (`scope=local` exists) into an invalid **action** contract: `--scope` is "a parameter **bound to a working directory**", so a disable fires against the installer's CWD and litters a random directory | **FOLDED — VALID; the round's best finding.** Verified: `claude plugin disable --help` exposes only `-a`, `-h`, `-s/--scope` — **no way to target another project**. Runner must widen on **two** axes (stdout **+ working directory**), not one. `agy-attempt`'s runner shape is insufficient. |

**Method note on round 7.** The rejected finding was the **most valuable one in the round** — chasing
its premise to measurement produced the `projectPath` field and the missing-`enabled` fact that
together make finding 4 *fixable* and settle the detection fork on evidence. A panel's wrong answers
are worth measuring, not just its right ones. Note also the shape of what it caught: **three of the
four findings landed on material written the same day** by the consult — new text is where the defects
were, not the parts six rounds had already ground down.

**AGY-FIRST consult + negotiation, 2026-07-16 — NOT a panel round.** The three blocking decisions were
routed to the peer as a consult (prose-only, no code), then one negotiation turn, then decided by the
**user**. Logged here so a later round does not re-open settled ground; the reasoning is in
[Settled decisions](#settled-decisions-2026-07-16).

| # | Question | Outcome |
|---|---|---|
| 1 | Detection mechanism | **CONVERGED** — both models chose `--json` + widen the runner, independently. Claude folded agy's silent-failure asymmetry argument without dispute. User confirmed. |
| 2 | R6 observability | **agy's design, claude's measurements, agy's sharpening.** Claude measured that the mechanism's home already exists (`iss:113-117`), that the interactivity objection it was about to raise was already retired (`:83`), and that R6 bites only on the installer path (`ops-manual.md:48`). Claude found the purge-litter cost; agy closed the race (read contents into the dialog, *then* reap). User confirmed. |
| 3 | The `:22-24` promise | **agy REVERSED claude.** Claude had walked its own finding back to "merely imprecise" (jurisdiction: the entry is ours). agy, invited to argue the other side, won on the **verb**: "only deletes" vs. an active re-enable. Claude's counter — that agy's proposed word-swap already exists at `:50` — was accepted by agy, which then abandoned its own fix. Both converged on *rewrite the contract*. User confirmed. |

**Method note on the consult.** Two of the three overturned a **claude** position, and one overturned
**agy's own** recommendation mid-negotiation. Both were reached by pushing back with a *measurement*
rather than an opinion — `:50` already containing agy's proposed adjective; `:83` already retiring the
hang risk claude meant to raise. The peer touched **no file** across the consult and the negotiation:
the payload's *body*, not just its banner, was consult-shaped (prose-only deliverable, no remedy
tables to implement, no "does the mechanism exist in code" phrasing).

**Method note.** Round 1 was bound to measurement (name the files, cite code `file:line`, and
*verify the relation, not just the endpoints*). Compared with the peer's 2026-07-15 panel — whose
every citation pointed at the reviewed document's own line numbers — it returned real code citations
and found defects that only reading the code exposes. The one rejected finding is the one where it
again asserted a relation between two paths without reading both.

## Out of scope

- **The autotrain loop and any global observation inbox.** The 2026-07-15 draft bundled a global
  `~/.flaui-mcp/observations.md` plus a contributor-gated `flaui-learn`. **Cut** on 7 independent
  nails from 2 models — recorded in `2026-07-15-panel-findings.md` TIER 3. Do not resurrect without
  answering them, in particular: a global inbox **voids the anti-poisoning gate**
  (`flaui-curate/SKILL.md:42` depends on curator familiarity with the dogfooded app; cross-project
  pooling destroys it), opening a path from an untrusted terminal buffer — which
  `driving-flaui-mcp/SKILL.md:255` explicitly calls "a live injection surface" — into a paraphrased
  observation, into the GROWTH region, and out to every user as operating instructions.
- Any path for **stranger** observations to reach the product.
- Relocating `graduation-candidates.md`; changing `flaui-curate`'s triage, anti-poisoning gate, or
  GROWTH format.
- The v1.0 / A1b hardware question tracked in `ROADMAP.md`.
