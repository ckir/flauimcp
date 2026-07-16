# Spec — bundled driving-skill distribution (v0.15.0)

**Date:** 2026-07-16
**Status:** ⛔ **BLOCKED — not implementable as written.** 5 agy panel rounds, all REJECT; every
finding dispositioned. R1 7 (6 folded) · R2 4 (2 folded, 2 **rejected on measurement**) · R3 5 (all
valid) · R4 5 (all valid) · R5 7 (all valid). 28 findings, 25 valid.

**Round 5's blocker is RESOLVED: detection is feasible.** `claude plugin list --json` exists and
returns `{id, scope, enabled, …}` — measured. What remains is **not** a feasibility question but three
decisions, all with named owners:
1. ⚠ **Detection mechanism (USER, AGY-FIRST):** adopt `--json` + widen the runner, vs read Claude's
   config directly. Narrow, both viable, evidence in place. *(An unauthorised implementation of the
   first exists at `agy-attempt`/`c5e447d` — reference only; see Prior art.)*
2. 🚩 **R6 — uninstall destroys its own observability** (the exe is deleted; `--purge-data` deletes the
   log), so restore warnings die as written. **Genuinely unresolved** — settle with the marker's home.
3. ⚠ **The `ops-manual.md:22-24` promise** that detect-and-disable breaks (owner: user).

Honest read on the review itself: rounds 1–2 hit the *design*; rounds 3–4 hit *mechanics and doc
coherence*; round 5 went back to the *design* and found a hole all four earlier rounds missed. The
review is **not converging** — GREEN was not reachable by another round, and chasing it produced this
finding instead, which is worth more.
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
| Marketplace copy | **OPEN** — see [The marketplace copy](#the-marketplace-copy) | user deferred |

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

- **On install:** if the marketplace copy is present, run
  `claude plugin disable flaui-mcp@flaui-mcp --scope user`, and report it as a `Warning` on the
  claude `AgentResult` (the channel `a373265` already built), so it reaches `install.log` and
  `flaui-mcp status`.

🚩 **BLOCKER — this remedy needs a capability the writer does not have. Round 5's best finding; the
plan CANNOT be written until it is resolved.** Every branch above requires *reading* Claude's state:
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

That supplies **all three** reads the remedy needs, from one call:
| Need | Field |
|---|---|
| Is the marketplace copy present? | `id == "flaui-mcp@flaui-mcp"` |
| Is it **already disabled**? (the pre-flight read **R1**'s write-once marker requires) | `enabled` |
| At which scope? | `scope` |

**This corrects an earlier draft of this section**, which grouped "capture stdout" with "parse
human-readable output → brittle across versions". Those are not the same thing: `--json` is a
**documented machine-readable contract**, so it is simultaneously the **cheapest and the most
robust** option — the earlier risk table was wrong and is withdrawn.

**Still required:** widen the runner from `Func<string,string[],int>` (`ClaudeCodeConfigWriter.cs:13`)
to also return stdout. This is the one place the change reaches beyond a new writer.

**The remaining fork — narrow, but still open and USER-OWNED (AGY-FIRST):**
- **(a)** Adopt `--json` + widen the runner. Keeps the "only touch the stable CLI" principle the
  writer was built on. Cost: the shared runner's signature changes.
- **(b)** Read Claude's config file directly (honoring `CLAUDE_CONFIG_DIR`). Abandons that principle
  for an undocumented file schema — though `AgyConfigWriter` already does exactly this for the peer,
  and `JsoncFile` exists.

⚠ **INCONCLUSIVE — do not assert either way:** whether `--json` also lists **skills-dir** plugins. A
check found no `@skills-dir` ids, but the profile had **none installed at the time**, so the test was
**vacuous, not negative**. Re-measure with one present. It does not gate the remedy, which needs only
the *marketplace* id — but `flaui-mcp status` reads the filesystem directly and must not be built on
the assumption either way.

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

**R5 — every restore decision must be logged, including the negative branch.** "Found it disabled
with no marker → the user did this → don't touch it" is the correct rule and an **invisible** one. A
user whose plugin stays disabled has no way to learn why, and a maintainer debugging it is guessing.
**Contract:** the skip and its reason are recorded in `install.log`. The whole point of `a373265` was
that an unreported decision is indistinguishable from a bug.

🚩 **R6 — but at UNINSTALL time that observability channel does not survive. Round 5; unresolved.**
Uninstall **deletes the exe** (`docs/ops-manual.md:45-47`: "removes the files"), so `flaui-mcp status`
— the reader for all of this — **no longer exists** immediately afterwards. And `--purge-data` deletes
`install.log` itself. So **every warning the restore path emits during uninstall is destroyed at the
moment it is written**: the R2/R5 observability mandate is void exactly when it matters most (the user
whose plugin wasn't restored has no product left to ask). The plan must put uninstall-time warnings
somewhere that outlives the uninstall — the log is not it. **UNRESOLVED; it interacts with the marker's
home (R3/R4) and must be settled with them.**

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
reason.)* *(Also unverified: which file `disable` writes at user scope. The `--scope local`/`project`
mapping was measured; user scope was not.)*

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

⚠ **`docs/ops-manual.md:22-24` states a PRODUCT PROMISE that detect-and-disable BREAKS.** It says
uninstall "performs **targeted key removal** — it only deletes FlaUI.Mcp's own entries and leaves
your other settings intact." **Disabling `flaui-mcp@flaui-mcp` mutates an entry that is the USER's,
not ours.** This is not a stale sentence to refresh — it is a guarantee the chosen remedy violates,
and it outranks the other doc fixes. The plan must **either** narrow the promise explicitly ("with
one exception: a conflicting flaui-mcp plugin we detect is disabled, reversibly, and restored on
uninstall") **or** revisit the remedy. Silently breaking a documented guarantee is not an option.
*(Found by me while verifying the panel's ops-manual finding; the panel found the table, not the promise.)*

| Doc | Line | Falsified how |
|---|---|---|
| `docs/ops-manual.md` | 27 | The "What the installer changes" table gives Claude Code as *only* "Registers the `flaui-mcp` MCP server". Mechanism A also writes `~/.claude/skills/flaui-mcp/`, disables a conflicting plugin, and drops a marker in `<data-dir>`. |
| `docs/ops-manual.md` | 22-24 | The "targeted key removal / leaves your other settings intact" promise — see above. |
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
| Namespace collision: a user has both the marketplace copy and the bundled copy | Resolved if the marketplace is retired (#1); otherwise the installer must detect and warn |
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
