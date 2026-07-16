# Spec — bundled driving-skill distribution + global observation inbox

> ## ⛔ SUPERSEDED 2026-07-16 — DO NOT IMPLEMENT
> Replaced by **`2026-07-16-skill-distribution-design.md`**. Kept only as the review record.
> This draft is **not green** and contains claims verified false:
> - "the marketplace has no audience by construction" — false as stated (see the CORRECTION below);
> - "mirrors `DeploySkill()` exactly" — false (`ClaudeCodeConfigWriter` has no file I/O to extend);
> - `--contributor` specified with `OptionValue` (valued) shape when it is a boolean (`HasFlag`).
>
> Its **global observation inbox** half is **CUT** (7 nails, 2 models — `2026-07-15-panel-findings.md`
> TIER 3). Read that findings file before reusing anything here.

**Date:** 2026-07-15
**Status:** ⛔ SUPERSEDED — review record only
**Supersedes:** the marketplace-only distribution decision (Fork 1B) from `2026-07-15-flaui-mcp-plugin-packaging.md`

## Problem

Two defects, one root cause.

1. **The shipped v0.14.0 plugin does not load.** `plugins/flaui-mcp/.claude-plugin/plugin.json`
   declares `"hooks": "./hooks/hooks.json"`. Claude Code auto-loads `hooks/hooks.json` by
   convention, so the manifest key re-references an already-loaded file → duplicate-file →
   **hard load failure**. Every user following `README.md:161-162` gets zero skills and zero
   hooks. `claude plugin validate` **passes** on this manifest (schema-valid, load-broken).
   Verified fix: delete the key — the Stop hook still loads via convention.
2. **Nobody would get it anyway.** The plugin is marketplace-only and requires two manual
   commands. Meanwhile `AgyConfigWriter.Install()` (`AgyConfigWriter.cs:72`) **unconditionally**
   calls `DeploySkill()` for the agy peer. The two agents are treated asymmetrically, and the
   asymmetry favours the peer over Claude Code.

**Root cause:** the driving skill was modelled as an optional add-on. It is not. The MCP server
exposes *capability*; the skill carries the *model of the world that capability acts on* plus the
judgment to abstain — environment ontology (a peer agent lives in a WindowsTerminal `TabItem`, not
its own window), negative knowledge (`SKILL.md:223`: prefer the programmatic channel, don't
tab-flip), cross-tool sequencing (`desktop_select` not `desktop_click`; re-snapshot after a switch),
and traps. None of that is expressible in a tool schema. `SKILL.md:253` records that shipping the
tools without this knowledge already caused a real incident — it motivated the entire v0.13.0
terminal-tab-reading feature. **Shipping the tools without the skill ships the trap.**

## Decisions and their provenance

| Decision | Chosen | Recommended by |
|---|---|---|
| Delivery mechanism | **A** — installer bundles to `~/.claude/skills/flaui-mcp/` | claude + agy (converged) |
| Stranger payload | `driving-flaui-mcp` only | agy (CHALLENGE-RIGHT), accepted |
| Contributor payload | `+ flaui-learn`, behind an installer checkbox | claude (option D), agy: "Deal" |
| Curator payload | never shipped — repo-only | agy; already true in-repo |
| Inbox location | **global** `~/.flaui-mcp/observations.md` | claude; agy: "strongly agree" |
| Marketplace copy | **OPEN** — see Open Decisions | user deferred to implementation |

The user's initial position was to bundle the full 3-skill payload to everyone, citing a dual role
(developer *and* daily user). agy challenged this; the challenge was accepted **because the global
inbox dissolves it** — the dual role's real requirement is capture *wherever he drives the tools*,
which the checkbox + global inbox deliver without imposing the curation loop on strangers.

## Design

### Delivery mechanism (A)

The installer writes plugin files from **embedded resources** to `~/.claude/skills/flaui-mcp/`,
which Claude Code auto-loads as `flaui-mcp@skills-dir` (scope: user). This mirrors
`AgyConfigWriter.DeploySkill()` exactly — one mental model for both agents.

**Verified live:** a full plugin (skills + hooks) at `~/.claude/skills/<name>/` reports
`Status: ✔ loaded`, correct component inventory, no marketplace and no network involved.

**Why not marketplace-driven install (B):** *version skew*. The driving skill names specific tools
and specific contracts (`desktop_read_terminal_tab { window, tabIndex, fromEnd:true }`,
`restoreConfidence`, `SinkInterlocked`). It is documentation **of a particular server build**. The
server updates via a Windows installer (manual, infrequent); a marketplace plugin updates via Claude
(automatic, frequent). Decoupling them lets a user on server 0.14.0 drift onto skill 0.16.0
describing tools their binary lacks. A drifted skill is **worse than no skill** — it is confidently
wrong. Bundling makes skew structurally impossible: one installer transaction writes both.

The installer runs **unelevated** (`installer/flaui-mcp.iss:13`, `PrivilegesRequired=lowest`,
`{localappdata}` target, HKCU-only), so `~` resolves to the real user's profile. Confirmed
empirically: the v0.14.0 agy seed landed in `C:\Users\user\.gemini\`, not an administrator profile.

### Payload gates

| Recipient | Payload | Gate |
|---|---|---|
| Everyone | `driving-flaui-mcp` | unconditional — `flaui-mcp install --agent all` |
| Contributor | `+ flaui-learn` | `[Tasks]` checkbox → `flaui-mcp install --agent all --contributor` |
| Repo only | `flaui-curate`, `flaui-learn-reminder`, `flaui-curate-nudge` | **ship nothing** — already live via `.claude/` |

The `[Tasks]` checkbox is **unchecked by default**, labelled for contributors. `installer/flaui-mcp.iss:27`
already has a `[Tasks]` section (`addtopath`), so the machinery exists.

**The repo-only tier requires no work.** `.claude/settings.json` already wires
`SessionStart → .claude/hooks/flaui-learn-reminder.sh` and `Stop → .claude/hooks/flaui-curate-nudge.sh`,
and `.claude/skills/` already holds all three skills at project scope. `plugins/flaui-mcp/hooks/hooks.json`
is a **snapshot duplicate** of machinery the repo already runs. This tier is satisfied by *deleting*
the duplicate, not by building anything.

### Both agents

`--contributor` deploys `flaui-learn` to **both** agents — `~/.claude/skills/flaui-mcp/skills/flaui-learn/`
and `~/.gemini/config/plugins/flaui-mcp/skills/flaui-learn/`. The maintainer drives both; a
Claude-only capture path would break the parity established in v0.14.0. (agy's payload spec omitted
the peer entirely; this closes that gap.)

### Global inbox

`flaui-learn` appends to **`~/.flaui-mcp/observations.md`** regardless of the current project.

This closes the **one-way valve**: today `flaui-learn` writes to
`$CLAUDE_PROJECT_DIR/.claude/flaui-mcp/observations.md`, so a general truth noticed while working
in an unrelated project curates (USER mode) into *that project's* growth file and never reaches the
product. The insights most worth harvesting — earned in varied, out-of-repo work — were exactly the
ones that could never flow back.

`~/.flaui-mcp/` is **existing infrastructure**: it holds `generic-mcp.json` today, and the
uninstaller already prompts to remove it (`flaui-mcp.iss:85`).

`flaui-curate`, running in the repo (MAINTAINER mode), drains the **global** inbox and promotes into
the repo's `driving-flaui-mcp/SKILL.md` GROWTH region.

### Contracts

**Observation line format** (changed — adds provenance, per agy: a global inbox strips *where* an
insight came from):

```
- [YYYY-MM-DD] (<project-dir-basename>) <one plain-English line, author's own words>
```

- Appended under the `## Pending` heading; flat list; monotonic drain — unchanged.
- `<project-dir-basename>`: basename of `$CLAUDE_PROJECT_DIR` only, **not** the full path (keeps the
  inbox free of machine-specific paths). Literal `-` when unavailable.
- Date only, no clock time — `flaui-learn` must stay fast and cheap.
- The existing prohibition stands: never paste raw app-screen text (untrusted); no tagging or
  curation at capture time.

**Inbox file:** created by the installer under `--contributor` with the same header the repo inbox
uses. `flaui-learn` creates-if-missing as a fallback.

**CLI:** new `--contributor` flag on `install`. `CliRouter.cs:16` already parses `--agent`
(`agy|claude|generic|all`); `--contributor` follows the same shape and is documented in the usage
text at `CliRouter.cs:185`.

**Uninstall:** `RemoveSkill()`-equivalent removes `~/.claude/skills/flaui-mcp/` (parity with the
existing agy path). The inbox at `~/.flaui-mcp/observations.md` is **user data**: removed only under
the existing `--purge-data` prompt, never by a plain uninstall.

**Upgrade (e.g. 0.14 → 0.15):** skill files are overwritten (they are versioned product, not user
state). The inbox is never touched. Re-install is idempotent.

**Migration:** none required. The repo inbox's `## Pending` section is currently **empty** (verified),
so no observations are stranded by moving to the global path.

## Consequences and cleanups

1. **`flaui-curate`'s USER mode becomes dead code.** `flaui-curate/SKILL.md:11-18` detects
   MAINTAINER vs USER mode structurally so the skill can run outside the repo. If it never ships
   outside the repo, the whole `§ USER promote` branch is unreachable. Recommend deleting it (~half
   the file, ~3.3k on-invoke tokens). This is a **one-way door**: if the curator is ever shipped to
   users, it must come back.
2. **The PowerShell rewrite of the Stop hook becomes optional.** Both reviewers wanted it because
   `bash`+`jq` on a stranger's stock Windows box is a bad bet. If the hook never ships, it runs only
   on a developer machine where Git Bash is a safe assumption. **Not** dropped from the plan
   silently — downgraded from *required* to *nice-to-have*, with the rationale recorded here.
3. **The snapshot script** (`build(plugin)`, regenerates `plugins/` from `.claude/` sources) becomes
   redundant if the marketplace is retired; if it is kept, it must also learn the new gating so the
   marketplace copy doesn't ship the curator.
4. **`README.md:306`'s "disable the plugin in this repo" maintainer note becomes load-bearing.**
   Once the installer bundles at user scope, this repo has two `driving-flaui-mcp` skills (bundled +
   project). The note is now the only thing keeping MAINTAINER mode authoritative. It must be
   re-verified against the skills-dir mechanism, which may need a different disable incantation than
   a marketplace plugin does.
5. `.claude/flaui-mcp/graduation-candidates.md` also lives in the repo autotrain dir. It is a
   curation artifact, so it stays repo-local and is **out of scope** — flagged so the plan does not
   silently relocate it alongside the inbox.

## Open decisions

| # | Decision | Owner | Resolved where |
|---|---|---|---|
| 1 | **Retire the marketplace copy** (`plugins/` + `.claude-plugin/marketplace.json`) or keep it as a fixed driving-only path? | user | at implementation — user explicitly deferred |
| 2 | Delete `flaui-curate`'s USER mode, or keep it dormant? | user | implementation plan |
| 3 | Rewrite the Stop hook in PowerShell now, or leave it as bash? | user | implementation plan |

**On #1 — the argument for retiring**, recorded so the deferred decision has its evidence: the
marketplace has no audience *by construction*. You cannot use the skill without the server, and every
path to obtaining the server (`setup.exe`, bare `flaui-mcp.exe`, `install.ps1`) runs
`flaui-mcp install`, which bundles the skill. The only user the marketplace serves is someone who
registers the exe in `.claude.json` by hand without ever running the install verb. **Whichever way
#1 goes, the `hooks`-key fix ships** — the marketplace manifest is public and must not stay broken.

> ⚠ **CORRECTION 2026-07-16 — the paragraph above states a FUTURE property in the PRESENT TENSE, and
> the rewrite must not inherit it.** "every path … runs `flaui-mcp install`, **which bundles the
> skill**" is true only *once mechanism A exists*. It does not today: `AgyConfigWriter.DeploySkill()`
> bundles the skill for **agy only**, and `ClaudeCodeConfigWriter` does zero file I/O. So **today the
> marketplace is the sole documented way a Claude Code user gets the skill** — `README.md:158-163`
> instructs exactly that (`/plugin marketplace add ckir/flauimcp` → `/plugin install
> flaui-mcp@flaui-mcp`), and `.claude-plugin/marketplace.json` sources it from `./plugins/flaui-mcp`
> **in the repo**. Every Claude Code user who follows the README hits the hard load failure.
> The "no audience" framing made a live, publicly-documented breakage look victimless — it is why the
> claim propagated into the execution memory as a reason not to worry. **Retiring the marketplace is
> defensible only AFTER mechanism A ships AND the README is rewritten; the two must land together or
> the documented path breaks a second time.**
> Two further measured facts the rewrite needs:
> - **`.github/workflows/release.yml` has ZERO plugin/marketplace references** — the plugin is
>   delivered by **git (master)**, never by `setup.exe`. A release does not fix or ship it; a merge
>   does. Any plan that "ships the plugin fix in the next release" is wrong on the mechanism.
> - Claude Code caches plugins **by version** (`~/.claude/plugins/cache/flaui-mcp/flaui-mcp/0.14.0/`
>   observed live), so changing a plugin's content without bumping its version can leave existing
>   users on the cached broken copy.
> *Found by the agy peer (2026-07-15 release-scope consult), which read the README while both my own
> panel and I carried the assumption unchecked. Verified by measurement before folding.*

## Risks

| Risk | Mitigation |
|---|---|
| `skills-dir` auto-load is lesser-documented than marketplace install and could change under us | Pin to a tested Claude Code version; the smoke test below is the tripwire. Fallback is B. |
| Namespace collision if a user adds the marketplace copy *and* has the bundled copy | Resolved by Open Decision #1 if retired; otherwise the installer should detect and warn |
| Bundled user-scope skill shadows the repo's project skill for the maintainer | `README.md:306` note (see Consequence 4) — must be re-verified, not assumed |
| Stranger's inbox rots uncurated | Not shipped to strangers — `flaui-learn` is contributor-gated |
| Losing out-of-band skill updates: fixing a typo now needs an installer release | Accepted — the price of lockstep, taken deliberately |

## Testing

- **Unit:** `--contributor` on/off produces the correct file set for both agents; uninstall removes
  skills but preserves the inbox; `--purge-data` removes the inbox; re-install is idempotent.
- **The gate that would have caught this bug:** `claude plugin validate` passes on a load-broken
  manifest, so validation is not a load test. The smoke must **install and assert loaded** — from a
  directory *outside* this repo (inside it, project-scope skills mask the result, which is precisely
  why v0.14.0 shipped broken and looked fine). Assert `Status: ✔ loaded` and the expected component
  inventory.
- **Install smoke:** extend the existing pre-push smoke (which covers the agy seed surviving
  single-file publish) to cover the Claude payload for both checkbox states.

## Out of scope

- Any path for **stranger** observations to reach the product (a contribution channel). Strangers
  don't capture; if that changes, this is the first thing to design.
- Relocating `graduation-candidates.md`.
- Changing `flaui-curate`'s promote/route/drop triage, the anti-poisoning gate, or the GROWTH region
  format.
- The v1.0 / A1b hardware question tracked in `ROADMAP.md`.
