# release.ps1 — one-command release automation (design)

**Status:** approved design (brainstorming), pending implementation plan.
**Date:** 2026-07-18.
**Model:** clavity's `scripts/release.ps1`, adapted for a single-product repo.
**Design reviewed by:** agy (two rounds, release-engineering lens) + Claude; converged.

## Goal

Make cutting a new flaui-mcp release **effortless — one command** (`scripts/release.ps1`), without
degrading the hand-curated `CHANGELOG.md`. The script owns the mechanical + deterministic work; a headless
LLM drafts the one genuinely non-mechanical step (changelog prose); the human does a light editorial review.

## Current release flow (what exists today)

- **Version** lives in 3 files that must stay in sync:
  - `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj` — `<Version>X.Y.Z</Version>`
  - `installer/flaui-mcp.iss` — `#define AppVersion "X.Y.Z"`
  - `plugins/flaui-mcp/.claude-plugin/plugin.json` — `"version": "X.Y.Z"`
- **`.github/workflows/ci.yml`** — per-push gate: `dotnet build -c Release` + `dotnet test` (non-Desktop) + scaffolder smoke.
- **`.github/workflows/release.yml`** — fires on a `v*` tag push: build → test → publish self-contained exe →
  Inno installer (ISCC) → checksums → GitHub Release via `softprops/action-gh-release` (currently **auto-generated notes**).
- **Tags:** `vX.Y.Z`. **Commits:** strictly conventional (`feat`/`fix`/`docs`/`chore` + scopes).
- **`CHANGELOG.md`:** hand-curated Keep-a-Changelog prose (`## [X.Y.Z] - date`, `### Added`/`### Fixed`/`### Changed`,
  explanatory bullets — NOT commit-subject dumps).
- Releasing today is manual: bump 3 files, write CHANGELOG, commit, `git tag vX.Y.Z`, push tag.

## The design

### One-command flow (fail-fast; human reviews only viable releases)

```
1. Preconditions   on master · clean working tree (untracked OK) · git fetch origin --tags
2. Compute version parse conventional commits since last v* tag →
                   feat=minor · fix=patch · breaking=(minor while <1.0) → vX.Y.Z
3. Fast gate       dotnet build -c Release · dotnet test (non-Desktop) ·
                   3-file version-sync check · plugin-snapshot drift check
4. Draft body      SCRIPT writes the "## [X.Y.Z] - YYYY-MM-DD" heading itself (version + date
                   are deterministic); claude -p (headless) generates ONLY the ### body from the
                   commit range + diff, matching the existing style.
5. Review (human)  accept / regenerate / edit / abort  ← light editorial pass on the body
6. Confirm cut     "Cut release vX.Y.Z — commit, tag, and push to origin? [y/N]"  (default N; -y skips)
7. Commit + push   prepend CHANGELOG · sync 3 version files · commit "chore(release): vX.Y.Z"
                   · git tag vX.Y.Z · git push --atomic origin master vX.Y.Z
                   · print the GitHub Actions run URL to watch CI
8. CI publishes    release.yml (tag-triggered) builds installer + Release; release BODY
                   is the top CHANGELOG section (release.yml change), not auto-notes.
```

### Source-of-truth split (the central decision)

| Concern | Owner | Why |
|---|---|---|
| Version **number** | the **script** (deterministic conventional-commit math) | never let an LLM do SemVer math (hallucination risk) |
| Changelog **prose** | **`claude -p`** (constrained to the computed version + existing style) | prose synthesis is where an LLM excels; keeps quality without human blank-page authoring |
| **Review** | the **human** | light accept/edit pass is the quality shield |

This gives all three at once: no thinking about the number, curated-quality changelog, one command.

### Why this ordering

The **gate runs before** the LLM/human step, so the human reviews prose only once the release is locally
proven viable. After "accept," only deterministic git ops + **one `git push --atomic`** remain. `--atomic`
makes the server reject BOTH the branch and the tag if either fails — no "commit landed but tag didn't"
limbo, and a failed push leaves local state clean for a simple re-run. This eliminates the entire class of
half-finished-release state that clavity's push-then-tag sequence works hard to recover from.

## Components

Pure PowerShell (no `just` dependency; repo already uses standalone `.ps1`):

- **`scripts/release.ps1`** — thin orchestrator: preconditions → compute → gate → draft → review → commit/push.
- **`scripts/lib/release-lib.ps1`** — testable pure functions (no side effects where avoidable):
  - `Get-NextVersion` — commits since last tag → `{Version, Level, Commits, NonConventional}` (or "nothing").
  - `Get-VersionsInSync` / `Set-ProjectVersion` — read/verify/write the 3 version files. The pre-bump sync
    check asserts the 3 files **agree with each other** (drift among them → refuse until fixed). It does NOT
    require them to equal the last tag — a legitimately *ahead* state exists (version bumped + merged but not
    yet tagged/released, e.g. the files read 0.16.2 today while the last tag is v0.16.1). If the files' version
    differs from the **computed** version, warn and let the human confirm or `-Version`-override rather than
    hard-failing. `Set-ProjectVersion` then rewrites all 3 to the chosen version.
  - `Get-ChangelogPrompt` — build the constrained `claude -p` prompt from the commit range + diff + style exemplar.
  - `Add-ChangelogSection` — prepend the assembled `## [X.Y.Z] - YYYY-MM-DD` block above the first existing
    `## [` entry; if a heading for the target version ALREADY exists (a manual stub), warn + abort rather than
    prepending a duplicate.
  - `Get-TopChangelogSection` — extract the top section (used by release.yml for the release body).
  - `Invoke-Gate` — build + test + version-sync + plugin-drift, returns pass/fail + detail.
- **`scripts/release.Tests.ps1`** — Pester tests for the pure functions (repo already has `*.Tests.ps1`).
- Reuses **`scripts/build-plugin.ps1`** for the plugin-snapshot drift check.
- **`.github/workflows/release.yml`** — one edit: source the GitHub Release body from the top CHANGELOG section.

## Version computation (precise rules)

- Range: commits in `<last v* tag>..HEAD`. If no `v*` tag exists, all of history.
- Level = highest of: **breaking** (`feat!`/`fix!`/any `!` type, or `BREAKING CHANGE:` in a commit body),
  **feat** (→ minor), **fix** (→ patch).
- **Pre-1.0 rule:** while the current version is `0.y.z`, a **breaking** change bumps **minor** (not major) —
  do not jump to 1.0 by accident.
- Non-`feat`/`fix`/breaking commits (docs, chore, refactor, test, ci, build) do NOT drive a version bump and
  are surfaced as a warning (like clavity), not versioned.
- **Nothing to release:** if the range has no `feat`/`fix`/breaking commit → print "nothing to release" and
  exit 0. Overridable by an explicit `-Version X.Y.Z` or `-Bump {major|minor|patch}` escape hatch.

## Changelog drafting (the LLM step)

- **Heading is script-owned, body is LLM-owned.** The script computes the heading `## [X.Y.Z] - YYYY-MM-DD`
  (version from commit math, date from `Get-Date -f 'yyyy-MM-dd'`) — the LLM never touches version or date, so
  neither can hallucinate. `claude -p` generates ONLY the `### Added`/`### Fixed`/`### Changed` body.
- Invocation: `claude -p` (print/headless mode) with `--model` (default **`haiku`** — fast/cheap, the human
  review is the quality backstop; overridable via `-Model`). The exact flag spelling (`-p`, `--model`, output
  capture) is verified against the installed `claude` CLI at implementation time (plan Step-0).
- Prompt (constructed by `Get-ChangelogPrompt`) contains: the commit list, the diff context, and the **last
  ~2 existing CHANGELOG entries as a style exemplar**, instructing: output ONLY the `### Added`/`### Fixed`/
  `### Changed` body (no heading, no commit subjects, no surrounding chatter) — explanatory prose bullets.
- Body captured to a temp file keyed by the target version. **On re-run, if a temp draft for this version
  already exists, offer to RESUME it** (skip the LLM call, reuse the human's prior edits) rather than blindly
  regenerating and destroying that work.
- **Review prompt:** `Accept / Regenerate / Edit / Abort`. Edit opens `$EDITOR` on the temp body; Regenerate
  re-invokes `claude -p` (explicitly discarding the current draft); Accept assembles heading+body and prepends
  to `CHANGELOG.md`; Abort exits 0 with nothing written (temp draft left for a later resume).

## Guards / error handling

- **LLM timeout or error:** enforce a strict timeout on the `claude -p` call (default ~120s); on failure DO
  NOT crash the release — fall back to opening `$EDITOR` on an empty `### Added/Fixed/Changed` **body-only**
  template (like the LLM path — the heading is still assembled at Accept, never baked into the buffer, so it
  can't be duplicated) for the human to fill in (graceful degrade to a manual write).
- **Zero-commit release:** if the commit range is empty (e.g. `-Version` override when HEAD == last tag), the
  LLM has nothing to summarize and would confabulate — detect the empty range and skip `claude -p` entirely,
  dropping straight into the empty body-only template.
- **Context blowout** (huge release): if the diff exceeds a size threshold (heuristic default: diff > ~150 KB
  or > ~40 commits), degrade the prompt context from `git log -p` to `git log --stat` + commit bodies so the
  LLM stays coherent and within limits.
- **Malformed LLM markdown:** the human review is the primary shield; `Get-TopChangelogSection`'s extraction
  is kept tolerant of minor formatting drift (anchors on the `## [` header lines).
- **Accepted-body check:** the heading is script-owned (can't drift), but the human edits the body — so before
  committing, verify the accepted body is non-empty and has at least one `###` section (an aborted/blank edit
  must not produce a heading-only release entry). On failure, **loop back to the Review prompt** with the
  validation error so the human can Edit and fix — never crash-exit and eject them from the flow.
- **Half-finished prior release:** if `HEAD` is a `chore(release):` commit not on `origin/master`, OR the
  target tag already exists locally but not on the remote (a previous run committed/tagged but the atomic push
  failed), reconcile rather than blindly re-running. **Before offering to re-push an existing tag, assert it
  points at HEAD** (`git rev-parse vX.Y.Z` == `git rev-parse HEAD`) — a post-failure `commit --amend` orphans
  the tag onto a dangling hash; if they diverge, refuse the re-push and offer reset instead. Otherwise offer to
  re-push the existing commit + tag `--atomic`, or reset them for a clean re-cut. Never silently duplicate.

## CLI flags

- `-Help` / `-H` / `-?` — print usage (flags, the flow, examples) and exit 0. No side-effects, no git/LLM calls.
- `-WhatIf` — run preconditions + compute + gate, print the target version + gate result + the prompt-context
  that WOULD be sent to `claude -p`. Does NOT call the LLM (a dry-run must incur no paid/slow side-effect) and
  makes no writes (no commit/tag/push).
- `-Yes` / `-y` — non-interactive/unattended: auto-accept the LLM changelog draft AND skip the final
  "Cut release?" confirmation. (Supersedes a separate accept-draft flag — one switch turns off all prompts.)
- `-Version X.Y.Z` / `-Bump {major|minor|patch}` — override the computed version/level (escape hatch).
- `-Model <name>` — override the `claude -p` model.

## release.yml change

The `Create GitHub Release` step sources its **body** from the top CHANGELOG section instead of GitHub's
auto-generated notes. To keep the extraction single-sourced (no divergent inline `awk`/`sed` in YAML), the
workflow reuses the SAME PowerShell function via pwsh — e.g.
`pwsh -c ". scripts/lib/release-lib.ps1; Get-TopChangelogSection | Set-Content release-body.md"` — then passes
`release-body.md` to `softprops/action-gh-release` as `body_path`. The windows-latest runner already has pwsh.

## Testing

- **Pester unit tests** (`scripts/release.Tests.ps1`) on the pure lib functions: version math (feat/fix/
  breaking/pre-1.0/nothing-to-release), 3-file version-sync detection, changelog prepend + top-section
  extraction, prompt construction shape.
- The gate, `claude -p`, and push are integration shell-outs — smoke-tested via `-WhatIf`, not unit-mocked.

## Non-goals / out of scope

- No changes to `release.yml`'s build/publish steps beyond the release-body source.
- No multi-crate/serial machinery, retracted-serial ledger, or roster-drift checks (clavity monorepo needs
  they do not apply to a single-product repo).
- No local installer (ISCC) build in the gate — that stays in `release.yml` on the tag.
- Does not run the Desktop/synthetic-input test suites (headless-incompatible; same as CI).

## Operational note (first use)

Today the 3 version files read **0.16.2** (installer rework — bumped + merged to master, but **not tagged**),
while the last tag is **v0.16.1**. So on the first run the tool computes from commits since `v0.16.1`. Decide:
release the pending work **as 0.16.2** (run with `-Version 0.16.2`), or let it roll into the computed next
version. Cleanest is to tag/release v0.16.2 first, then the tool's normal flow resumes from a files==last-tag baseline.

## Success criteria

1. `scripts/release.ps1` on a clean master cuts a release end-to-end: computes the version, drafts + lets you
   review the changelog, gates locally, commits `chore(release)`, and `--atomic` pushes branch + tag.
2. The pushed tag triggers `release.yml`, which publishes the installer/exe and a GitHub Release whose body is
   the curated CHANGELOG section.
3. A partial push failure leaves the remote clean (both refs rejected) and the local changelog draft intact.
4. `-WhatIf` previews the full plan (version + gate + draft) with zero writes.
5. Pester tests cover the pure version/changelog functions and pass in CI.
