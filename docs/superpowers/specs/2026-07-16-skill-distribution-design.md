# Spec — bundled driving-skill distribution (v0.15.0)

**Date:** 2026-07-16
**Status:** draft — **round 1 panelled (agy, REJECT / 7 findings); 6 folded, 1 rejected.** See
[Review ledger](#review-ledger).
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

**Where `.claude-plugin/plugin.json` comes from — must be decided by the plan, not guessed.** The
`SKILL.md` is embedded; the Claude manifest is not, and no code writes one today. Options: embed it
as a second resource, or generate it in code as `AgyConfigWriter.DeploySkill()` does for the peer.
**Prefer generating it** — the version must track the assembly at runtime (`AgyConfigWriter.cs:37-38`
already derives a 3-part semver from `Assembly.GetName().Version`), which a static embedded file
cannot do without a build step. Whichever is chosen, it must be **stated**, not inferred.

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

Bundling makes skew impossible: one installer transaction writes both, from the same build.

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

**Install.** `flaui-mcp install --agent claude` (and `--agent all`) deploys the skill, mirroring the
agy path. Per `a373265`, a deploy failure is a **warning on the agent's result, not a throw**: the
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

**Remedy — recommended, owner: user, at plan time.** *Detect and warn*, not auto-remove:
- **Detect** the marketplace copy during install; if present, report it as a `Warning` on the claude
  `AgentResult` (the channel `a373265` already built), so it lands in `install.log` and in
  `flaui-mcp status`, and print the exact `claude plugin uninstall flaui-mcp@flaui-mcp --scope user`.
- **Do not auto-remove.** The user installed it deliberately by following the README; silently
  uninstalling a plugin the user chose is a bigger violation than the duplicate it fixes, and it is
  irreversible from the installer's side.
- If the marketplace is **retired** (open decision below), the warning is still required for
  everyone upgrading *from* v0.14.x — retiring it removes the future audience, not the existing one.

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
