# flaui-mcp Self-Improving Plugin Packaging — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship flaui-mcp as a Claude Code plugin (keeping the Inno installer) whose `driving-flaui-mcp` skill self-improves per install from the user's own tasks, seeded by the maintainer.

**Architecture:** Three phases. **Phase 1 (repo-side)** makes the existing autotrain skills mode-aware and per-project: unify paths to `.claude/flaui-mcp/`, split `flaui-curate` into MAINTAINER vs USER mode, add per-project + promote-to-global growth, and add a "learned rules" surfacing section to the driving skill. **Phase 2 (Claude Code packaging)** wraps the repo's `.claude/` skills + the Stop hook into a distributable plugin (`plugins/flaui-mcp/`) hosted by a repo-root marketplace, declaring **no** MCP server (Fork 1B: the installer already registers Claude via the exe's absolute path). **Phase 3 (agy packaging)** extends the exe's `install --agent agy` to also deploy the driving skill (static seed, embedded resource) into agy's native plugin dir — agy gets the wisdom but not the loop (no agy plugin hooks).

**Tech Stack:** Markdown skills (Claude Code Skill format), bash hooks (Git Bash on Windows), Claude Code plugin/marketplace manifests (`.claude-plugin/*.json`), a PowerShell snapshot script, Inno Setup (unchanged), GitHub Actions (unchanged).

**Source of truth:** the design spec `docs/superpowers/specs/2026-07-15-flaui-mcp-plugin-packaging-design.md` (agy-reviewed GREEN). When a value looks wrong, the spec wins — surface the conflict, don't silently adapt.

**Methodology note:** this deliverable is skills + config + manifests + one script, not compilable product code, so most tasks end with a **verification step** (a command with expected output, or a review-against-spec check) rather than red/green unit tests. The exceptions (mode-detection behavior) get a scripted check.

**Preconditions:** On branch `feat/plugin-packaging`; the spec is committed (`ea2b573`). All paths are relative to repo root `C:\Users\user\Development\c#\flauimcp`. The current autotrain assets exist at `.claude/autotrain/` and `.claude/skills/{flaui-learn,flaui-curate,driving-flaui-mcp}/` (from the shipped autotrain loop, commit `d869e10` on master).

---

## Phase 1 — Repo-side self-improving redesign

### Task 1: Unify autotrain paths `.claude/autotrain/` → `.claude/flaui-mcp/`

**Files:**
- Rename: `.claude/autotrain/observations.md` → `.claude/flaui-mcp/observations.md`
- Rename: `.claude/autotrain/graduation-candidates.md` → `.claude/flaui-mcp/graduation-candidates.md`
- Modify: `.claude/skills/flaui-learn/SKILL.md` (path in body)
- Modify: `.gitignore` (sentinel pattern)

- [ ] **Step 0: State-verify**

Run:
```
git branch --show-current
ls .claude/autotrain/
grep -n 'autotrain/observations' .claude/skills/flaui-learn/SKILL.md
grep -n 'autotrain/.nudged' .gitignore
```
Expected: branch `feat/plugin-packaging`; `.claude/autotrain/` contains `observations.md` + `graduation-candidates.md`; the learn skill body references `.claude/autotrain/observations.md`; `.gitignore` has `.claude/autotrain/.nudged-*`. If any differ, STOP and report `STATE_MISMATCH`.

- [ ] **Step 1: Move the data files (preserve git history)**

```bash
mkdir -p .claude/flaui-mcp   # git mv fails if the destination dir does not exist
git mv .claude/autotrain/observations.md .claude/flaui-mcp/observations.md
git mv .claude/autotrain/graduation-candidates.md .claude/flaui-mcp/graduation-candidates.md
```

- [ ] **Step 2: Update the flaui-learn skill body path**

In `.claude/skills/flaui-learn/SKILL.md`, replace the single occurrence `.claude/autotrain/observations.md` with `.claude/flaui-mcp/observations.md`. Change nothing else (the body stays ≤10 lines).

- [ ] **Step 3: Update the gitignore sentinel pattern**

In `.gitignore`, replace the line `.claude/autotrain/.nudged-*` with `.claude/flaui-mcp/.nudged-*`, and update the adjacent comment path `see .claude/hooks/flaui-curate-nudge.sh` (unchanged) — only the ignore glob path changes. Result:
```
# flaui-autotrain per-session curate-nudge sentinel (see .claude/hooks/flaui-curate-nudge.sh)
.claude/flaui-mcp/.nudged-*
```

- [ ] **Step 4: Verify and commit**

Run:
```
ls .claude/flaui-mcp/
test -d .claude/autotrain && echo "STILL EXISTS (bad)" || echo "old dir gone (good)"
grep -c 'flaui-mcp/observations' .claude/skills/flaui-learn/SKILL.md
git check-ignore .claude/flaui-mcp/.nudged-probe
awk '/^---$/{n++; next} n>=2' .claude/skills/flaui-learn/SKILL.md | grep -c .
```
Expected: new dir lists both files; old dir gone; learn-skill path count `1`; `git check-ignore` echoes `.claude/flaui-mcp/.nudged-probe`; learn body line count ≤ 10.
```bash
git add -A
git commit -m "refactor(autotrain): unify inbox/queue path to .claude/flaui-mcp/ (packaging prep)"
```

---

### Task 2: Make `flaui-curate` mode-aware (MAINTAINER vs USER)

**Files:**
- Modify: `.claude/skills/flaui-curate/SKILL.md`

**Context:** The current curate skill always edits the real `driving-flaui-mcp/SKILL.md` GROWTH region and routes tool-defects to `docs/fix-the-tool-backlog/` + xUnit tests — correct for the maintainer *in this repo*, wrong for a distributed end user (read-only plugin skill, no repo). Add a mode gate and a USER-mode branch. The existing MAINTAINER behavior is preserved verbatim under the MAINTAINER branch.

- [ ] **Step 0: State-verify**

Run:
```
sed -n '1,12p' .claude/skills/flaui-curate/SKILL.md
grep -n 'Promote → the GROWTH region\|Route → fix-the-tool\|HARD CAP\|graduate' .claude/skills/flaui-curate/SKILL.md
```
Expected: frontmatter `name: flaui-curate` + the intro; the file contains a `## Promote → the GROWTH region of \`driving-flaui-mcp/SKILL.md\`` section, a `## Route → fix-the-tool` section, a `HARD CAP: the GROWTH region must stay ≤ 30 lines` line, and a `graduate` eviction step. If not, STOP and report `STATE_MISMATCH`.

- [ ] **Step 1: Insert the mode-detection section right after the intro**

Immediately after the `**monotonically drains** …` intro paragraph (before `## Run shape`), insert:

```markdown
## Mode — detect BEFORE processing (structural, not folder-name)
Decide your mode by path existence in the current project:
- **MAINTAINER mode** — iff BOTH `docs/fix-the-tool-backlog/` AND `test/FlaUI.Mcp.Tests/` exist (you are in
  the flaui-mcp repo). Promote by editing the real `driving-flaui-mcp/SKILL.md` GROWTH region in place;
  route driver/deterministic tool-defects to `docs/fix-the-tool-backlog/` + xUnit tests. (§ MAINTAINER promote/route.)
- **USER mode** — otherwise. The `driving-flaui-mcp` skill is read-only (shipped in a plugin); NEVER edit it.
  Promote into the project-local growth file; a tool-defect becomes a local known-quirk heuristic (you cannot
  patch C# or run repo tests). (§ USER promote.)
Everything else below (tagging, triage, the anti-poisoning gate, Finish) applies to both modes.
```

- [ ] **Step 2: Retitle the existing promote/route sections as the MAINTAINER branch**

Rename the heading `## Promote → the GROWTH region of \`driving-flaui-mcp/SKILL.md\`` to
`## MAINTAINER promote → the GROWTH region of \`driving-flaui-mcp/SKILL.md\`` and the heading
`## Route → fix-the-tool (driver/deterministic = our C# defect)` to
`## MAINTAINER route → fix-the-tool (driver/deterministic = our C# defect)`. Leave the BODIES of both
sections unchanged. (These now clearly apply only in MAINTAINER mode.)

- [ ] **Step 3: Add the USER-mode promote section immediately after the MAINTAINER route section**

Insert after the MAINTAINER route section (before `## Verify probes`):

```markdown
## USER promote → the project-local growth file (USER mode only)
Write promotions into `<project>/.claude/flaui-mcp/local-growth.md` (create it with a `# flaui-mcp — locally
learned driving rules` header if missing). NEVER touch the read-only `driving-flaui-mcp` skill. One rule per
line, same voice as the seed's GROWTH region.
- A **driver/deterministic** observation that WOULD be a C# defect in the repo is NOT fixable here → record it
  as a local **known-quirk / workaround heuristic** (state the quirk + the driving workaround). No backlog
  file, no test — those are MAINTAINER-only.
- **HARD CAP: `local-growth.md` ≤ 30 lines.** On breach, in order: (1) compress/merge related rules or
  supersede an old one; (2) drop the lowest-leverage rule. There is NO graduate-to-floor step (you cannot edit
  the shipped seed) — the file IS its own editable floor.
- Anti-poisoning gate is unchanged: you are the gate; when in doubt, drop it.
```

- [ ] **Step 4: Verify and commit**

Run:
```
grep -c 'MAINTAINER mode\|USER mode\|MAINTAINER promote\|MAINTAINER route\|USER promote\|local-growth.md' .claude/skills/flaui-curate/SKILL.md
grep -c 'docs/fix-the-tool-backlog/\|test/FlaUI.Mcp.Tests/' .claude/skills/flaui-curate/SKILL.md
```
Expected: first ≥ 6 (all the new headings/markers present); second ≥ 1 (the structural-detection paths present). Review that the MAINTAINER section bodies are byte-unchanged from before.
```bash
git add .claude/skills/flaui-curate/SKILL.md
git commit -m "feat(autotrain): make flaui-curate mode-aware (MAINTAINER edits skill; USER writes local-growth)"
```

---

### Task 3: Add promote-to-global (Hybrid opt-in) to `flaui-curate`

**Files:**
- Modify: `.claude/skills/flaui-curate/SKILL.md`

**Context:** The Hybrid feature: a deliberate, user-invoked action lifts a proven project-local rule into a machine-wide global growth file surfaced in every project. Windows path (flaui-mcp is Windows-only): `%USERPROFILE%\.claude\flaui-mcp\global-growth.md`.

- [ ] **Step 0: State-verify** — confirm Task 2's `## USER promote` section exists: `grep -n 'USER promote →' .claude/skills/flaui-curate/SKILL.md` returns one line. Else STOP `STATE_MISMATCH`.

- [ ] **Step 1: Add the promote-to-global section after `## USER promote`**

```markdown
## Promote to GLOBAL (explicit, user-invoked — Hybrid)
Only when the user explicitly asks to "promote to global" a specific rule. Copy that one rule from a project's
`local-growth.md` into the machine-wide global growth file at `%USERPROFILE%\.claude\flaui-mcp\global-growth.md`
(resolve the Windows user-profile directory, e.g. `C:\Users\<you>\.claude\flaui-mcp\global-growth.md`; create
it with a `# flaui-mcp — global learned driving rules` header if missing). This file is surfaced in EVERY
project, so promote only genuinely cross-project rules. Same **≤ 30-line HARD CAP** (compress/drop on breach)
and anti-poisoning gate apply. Never auto-promote — global compounding is an opt-in the user chooses.
```

- [ ] **Step 2: Verify and commit**

Run: `grep -c 'Promote to GLOBAL\|global-growth.md\|%USERPROFILE%' .claude/skills/flaui-curate/SKILL.md`
Expected: ≥ 3.
```bash
git add .claude/skills/flaui-curate/SKILL.md
git commit -m "feat(autotrain): add explicit promote-to-global (Hybrid) to flaui-curate"
```

---

### Task 4: Add the "learned rules" surfacing section to `driving-flaui-mcp`

**Files:**
- Modify: `.claude/skills/driving-flaui-mcp/SKILL.md`

**Context:** The single seed skill must direct the agent to read the small project + global growth files at drive time (the spec's "Step 0", renamed to avoid clashing with the skill's EXISTING `## Step 0 — load tools`). Place it right after the intro paragraph, BEFORE `## Step 0 — load tools`.

- [ ] **Step 0: State-verify**

Run:
```
sed -n '1,10p' .claude/skills/driving-flaui-mcp/SKILL.md
grep -n '## Step 0 — load tools' .claude/skills/driving-flaui-mcp/SKILL.md
grep -n 'AUTOTRAIN:GROWTH:START' .claude/skills/driving-flaui-mcp/SKILL.md
```
Expected: file opens `# Driving FlaUI.Mcp (live server)` + an intro paragraph, then `## Step 0 — load tools`; the GROWTH markers exist near the end. If not, STOP `STATE_MISMATCH`.

- [ ] **Step 1: Insert the learned-rules section before `## Step 0 — load tools`**

Insert this block immediately BEFORE the line `## Step 0 — load tools`:

```markdown
## Load your learned rules first

Before driving, load your locally-learned rules and treat them as extensions of this manual:
- **This project:** read `.claude\flaui-mcp\local-growth.md` if it exists (project-relative).
- **Your global rules:** read `%USERPROFILE%\.claude\flaui-mcp\global-growth.md` if it exists — resolve your
  Windows user-profile directory (e.g. `C:\Users\<you>\.claude\flaui-mcp\global-growth.md`); do NOT read a
  literal `~` path.
Both are small and maintained automatically by `flaui-curate`. If neither exists, proceed — you are on the
shipped seed only.

```

- [ ] **Step 2: Verify and commit**

Run:
```
grep -n 'Load your learned rules first\|local-growth.md\|%USERPROFILE%' .claude/skills/driving-flaui-mcp/SKILL.md
grep -n 'Load your learned rules first' .claude/skills/driving-flaui-mcp/SKILL.md
awk '/## Load your learned rules first/{f=1} /## Step 0 — load tools/{if(f) print "ORDER_OK"; f=0}' .claude/skills/driving-flaui-mcp/SKILL.md
```
Expected: the section is present with both growth paths and `%USERPROFILE%` (no literal `~`); `ORDER_OK` prints (the section precedes `## Step 0 — load tools`).
```bash
git add .claude/skills/driving-flaui-mcp/SKILL.md
git commit -m "feat(autotrain): driving skill loads project + global learned-growth (Windows-robust paths)"
```

---

### Task 5: Point the repo hook + settings at the unified path

**Files:**
- Modify: `.claude/hooks/flaui-curate-nudge.sh`
- Modify: `.claude/settings.json` (only if it references the old path — verify)

**Context:** The Stop curate-nudge reads the inbox + writes a per-session sentinel; both must use `.claude/flaui-mcp/`. The repo KEEPS the SessionStart learn-reminder (maintainer capture discipline); only the *plugin* omits it (Phase 2).

- [ ] **Step 0: State-verify**

Run:
```
grep -n 'autotrain' .claude/hooks/flaui-curate-nudge.sh
grep -n 'autotrain' .claude/settings.json
```
Expected: the curate-nudge script references `.claude/autotrain/observations.md` and `.claude/autotrain/.nudged-`; settings.json likely has NO `autotrain` path (it references hook script filenames, not data paths) — note what you find. If the hook has no `autotrain` reference, STOP `STATE_MISMATCH`.

- [ ] **Step 1: Update the hook data paths**

In `.claude/hooks/flaui-curate-nudge.sh`, replace both occurrences of `.claude/autotrain/` with `.claude/flaui-mcp/` (the `inbox=` line's `observations.md` and the `sentinel=` line's `.nudged-`). Change nothing else (keep the stdin guards, the `sid` fallback, the JSON emit).

- [ ] **Step 2: Verify the hook still behaves (unified path)**

Run:
```bash
cd "C:/Users/user/Development/c#/flauimcp"
printf '{"session_id":"p1"}' | CLAUDE_PROJECT_DIR="$(pwd)" bash .claude/hooks/flaui-curate-nudge.sh   # inbox exists but may be empty
printf -- '- probe  ·  2026-07-15\n' >> .claude/flaui-mcp/observations.md
OUT=$(printf '{"session_id":"p1"}' | CLAUDE_PROJECT_DIR="$(pwd)" bash .claude/hooks/flaui-curate-nudge.sh)
printf '%s' "$OUT" | jq -e '.hookSpecificOutput.hookEventName=="Stop"'
ls .claude/flaui-mcp/.nudged-* 2>/dev/null
git checkout .claude/flaui-mcp/observations.md && rm -f .claude/flaui-mcp/.nudged-p1
grep -c 'autotrain' .claude/hooks/flaui-curate-nudge.sh
```
Expected: with a pending line the hook emits valid `Stop` `additionalContext` JSON (`true`); a `.nudged-p1` sentinel appears under `.claude/flaui-mcp/`; cleanup restores the inbox; the final `grep -c 'autotrain'` prints `0` (no stale path left).

- [ ] **Step 3: Commit**

```bash
git add .claude/hooks/flaui-curate-nudge.sh .claude/settings.json
git commit -m "refactor(autotrain): repo curate-nudge hook uses unified .claude/flaui-mcp/ path"
```

---

## Phase 2 — Plugin packaging

### Task 6: Snapshot script — build the plugin's skills/hook from the repo source of truth

**Files:**
- Create: `scripts/build-plugin.ps1`

**Context:** The repo's `.claude/skills/` are the single source of truth. The distributable plugin at `plugins/flaui-mcp/` is a **generated artifact**: this script copies the three skills + the curate-nudge hook into it. Committed output, regenerated by the script (never hand-edit `plugins/flaui-mcp/skills/`).

- [ ] **Step 0: State-verify** — `ls .claude/skills/` shows `driving-flaui-mcp`, `flaui-learn`, `flaui-curate`; `ls .claude/hooks/flaui-curate-nudge.sh` exists. Else STOP `STATE_MISMATCH`.

- [ ] **Step 1: Write the snapshot script**

Create `scripts/build-plugin.ps1`:
```powershell
#!/usr/bin/env pwsh
# Regenerates plugins/flaui-mcp/{skills,scripts} from the repo's .claude source of truth.
# The plugin's manifests (.claude-plugin/plugin.json, hooks/hooks.json) are hand-authored and NOT touched.
$ErrorActionPreference = 'Stop'
$root   = Split-Path -Parent $PSScriptRoot
$src    = Join-Path $root '.claude'
$plugin = Join-Path $root 'plugins/flaui-mcp'

$skillDst = Join-Path $plugin 'skills'
if (Test-Path $skillDst) { Remove-Item -Recurse -Force $skillDst }
New-Item -ItemType Directory -Force -Path $skillDst | Out-Null
foreach ($s in 'driving-flaui-mcp','flaui-learn','flaui-curate') {
  Copy-Item -Recurse -Force (Join-Path $src "skills/$s") (Join-Path $skillDst $s)
}

$scriptDst = Join-Path $plugin 'scripts'
New-Item -ItemType Directory -Force -Path $scriptDst | Out-Null
Copy-Item -Force (Join-Path $src 'hooks/flaui-curate-nudge.sh') (Join-Path $scriptDst 'flaui-curate-nudge.sh')

Write-Host "Plugin skills + hook snapshot regenerated under plugins/flaui-mcp/."
```

- [ ] **Step 2: Verify the script runs (dir may not exist yet — Task 7 creates manifests; this creates skills/scripts)**

Run: `pwsh scripts/build-plugin.ps1 && ls plugins/flaui-mcp/skills/ plugins/flaui-mcp/scripts/`
Expected: prints the success line; lists the three skill dirs and `flaui-curate-nudge.sh`.

- [ ] **Step 3: Commit the script only (generated output committed in Task 7)**

```bash
git add scripts/build-plugin.ps1
git commit -m "build(plugin): add snapshot script (regenerates plugin skills/hook from .claude source)"
```

---

### Task 7: Plugin + marketplace manifests (Fork 1B — no MCP server)

**Files:**
- Create: `.claude-plugin/marketplace.json` (repo root)
- Create: `plugins/flaui-mcp/.claude-plugin/plugin.json`
- Create: `plugins/flaui-mcp/hooks/hooks.json`
- Create (generated by Task 6 script): `plugins/flaui-mcp/skills/*`, `plugins/flaui-mcp/scripts/flaui-curate-nudge.sh`

- [ ] **Step 0: State-verify** — `test -f .claude-plugin/marketplace.json && echo EXISTS || echo ABSENT` prints ABSENT; the `plugins/flaui-mcp/skills/` from Task 6 exist. Else STOP `STATE_MISMATCH`.

- [ ] **Step 1: Repo-root marketplace manifest**

Create `.claude-plugin/marketplace.json`:
```json
{
  "name": "flaui-mcp",
  "owner": { "name": "Costas Kirgoussios" },
  "plugins": [
    {
      "name": "flaui-mcp",
      "source": "./plugins/flaui-mcp",
      "description": "Driving skill + self-improving autotrain loop for the flaui-mcp desktop-automation MCP server. Install the server via the installer; this plugin adds the Claude Code skills."
    }
  ]
}
```

- [ ] **Step 2: Plugin manifest (NO `mcpServers` — Fork 1B)**

Create `plugins/flaui-mcp/.claude-plugin/plugin.json`:
```json
{
  "name": "flaui-mcp",
  "displayName": "flaui-mcp driving",
  "description": "Self-improving driving skill for the flaui-mcp desktop-automation MCP server. The server itself is registered by the flaui-mcp installer (via its absolute path); this plugin ships the driving-flaui-mcp skill (seed) plus the flaui-learn/flaui-curate autotrain loop.",
  "version": "0.14.0",
  "author": { "name": "Costas Kirgoussios" },
  "hooks": "./hooks/hooks.json"
}
```
> **Do NOT add an `mcpServers` key.** Fork 1B: the exe self-registers Claude with its absolute path
> (`Environment.ProcessPath`, `src/FlaUI.Mcp.Server/Program.cs:14`). A plugin-declared server would
> double-register and (if it used PATH) break when add-to-PATH is unchecked.

- [ ] **Step 3: Plugin hooks manifest (Stop curate-nudge ONLY — no SessionStart learn-reminder)**

Create `plugins/flaui-mcp/hooks/hooks.json`:
```json
{
  "hooks": {
    "Stop": [
      {
        "hooks": [
          { "type": "command", "command": "bash \"${CLAUDE_PLUGIN_ROOT}/scripts/flaui-curate-nudge.sh\"" }
        ]
      }
    ]
  }
}
```
> The SessionStart learn-reminder is deliberately omitted (it would nag in every unrelated project). The
> curate-nudge is self-gating: silent unless the current project's `.claude/flaui-mcp/observations.md` has
> pending items.

- [ ] **Step 4: Regenerate the snapshot and stage everything**

Run: `pwsh scripts/build-plugin.ps1`
Then verify the tree:
```bash
find plugins/flaui-mcp -type f | sort
test -f .claude-plugin/marketplace.json && echo mp-ok
grep -c 'mcpServers' plugins/flaui-mcp/.claude-plugin/plugin.json    # MUST be 0 (Fork 1B)
```
Expected: the tree lists `.claude-plugin/plugin.json`, `hooks/hooks.json`, `scripts/flaui-curate-nudge.sh`, and `skills/{driving-flaui-mcp,flaui-learn,flaui-curate}/SKILL.md`; `mp-ok`; `mcpServers` count is **0**.

- [ ] **Step 5: Validate the plugin (official tooling)**

Run: `claude plugin validate ./plugins/flaui-mcp`
Expected: validation passes (no schema errors). If `claude plugin validate` is unavailable in this environment, note it and fall back to a JSON lint: `jq -e . .claude-plugin/marketplace.json plugins/flaui-mcp/.claude-plugin/plugin.json plugins/flaui-mcp/hooks/hooks.json`.

- [ ] **Step 6: Commit**

```bash
git add .claude-plugin/marketplace.json plugins/flaui-mcp/
git commit -m "feat(plugin): flaui-mcp plugin + marketplace manifests (Fork 1B, no mcpServers)"
```

---

### Task 8: README — install path + maintainer dogfooding note

**Files:**
- Modify: `README.md`

- [ ] **Step 0: State-verify** — `grep -n 'Install\|installer\|## ' README.md | head -30` to locate the install section. Note the heading structure; do not restructure unrelated content.

- [ ] **Step 1: Add a "Claude Code plugin" subsection to the install docs**

Add, under the existing install section, content stating:
- Install the **server** via the installer as before (it registers the MCP server with Claude Code and agy).
- Then add the **plugin** for the driving + self-improving skills:
  ```
  /plugin marketplace add ckir/flauimcp
  /plugin install flaui-mcp@flaui-mcp
  ```
- Note the plugin declares **no** MCP server (the installer already did that) — it adds only the
  `driving-flaui-mcp` skill and the `flaui-learn`/`flaui-curate` self-improvement loop, which learns
  per-project from your own tasks (with an explicit "promote to global" to share a rule across projects).

- [ ] **Step 2: Add a maintainer note (dogfooding collision)**

Add a short **Maintainers** note (in README or `CONTRIBUTING`/docs): when developing *inside this repo* with the published plugin installed globally, **disable the plugin for this repo** so the repo's live project-scope `driving-flaui-mcp` skill is the single authority (the plugin ships a skill of the same name). Mechanism:
```json
// .claude/settings.local.json
{ "enabledPlugins": { "flaui-mcp@flaui-mcp": false } }
```
(End users never hit this — they have no project-scope copy.)

- [ ] **Step 3: Verify and commit**

Run: `grep -c 'plugin marketplace add\|flaui-mcp@flaui-mcp\|enabledPlugins' README.md`
Expected: ≥ 3.
```bash
git add README.md
git commit -m "docs(plugin): document plugin install path + maintainer dogfooding-disable note"
```

---

### Task 9: End-to-end local validation

**Files:** none created — validates the built plugin against the spec's behaviors.

- [ ] **Step 1: Install the plugin from the local marketplace**

In a Claude Code session (or note as a manual smoke if headless), run:
```
/plugin marketplace add ./
/plugin install flaui-mcp@flaui-mcp
```
Expected: the plugin installs; `driving-flaui-mcp`, `flaui-learn`, `flaui-curate` appear as available skills namespaced under the plugin.

- [ ] **Step 2: Confirm Fork 1B — no double MCP registration**

Confirm the plugin declares no server and Claude's flaui-mcp registration is still the installer's user-scoped one:
```bash
grep -c 'mcpServers' plugins/flaui-mcp/.claude-plugin/plugin.json   # 0
```
Expected: `0`. (The exe's `install --agent claude` — absolute path — remains the sole registrant; no plugin server to collide.)

- [ ] **Step 3: USER-mode smoke (mode detection writes local-growth, not the skill)**

In a scratch directory that is NOT the flaui-mcp repo (no `docs/fix-the-tool-backlog/` or `test/FlaUI.Mcp.Tests/`), simulate the loop: create `.claude/flaui-mcp/observations.md` with one `## Pending` line, invoke `flaui-curate`. Confirm it wrote `.claude/flaui-mcp/local-growth.md` (USER mode) and did **not** attempt to edit any `driving-flaui-mcp` skill. In the repo, confirm `flaui-curate` chooses MAINTAINER mode (edits the GROWTH region), unchanged.

- [ ] **Step 4: Surfacing smoke (Step "Load your learned rules first")**

With a `local-growth.md` present in a project, start a driving task and confirm the agent reads the learned-rules section (loads the growth file) before driving.

- [ ] **Step 5: Cap audit**

Run:
```bash
awk '/^---$/{n++; next} n>=2' plugins/flaui-mcp/skills/flaui-learn/SKILL.md | grep -c .
awk '/AUTOTRAIN:GROWTH:START/{f=1;next} /AUTOTRAIN:GROWTH:END/{f=0} f' plugins/flaui-mcp/skills/driving-flaui-mcp/SKILL.md | wc -l
```
Expected: flaui-learn body ≤ 10; seed GROWTH region ≤ 30 (the shipped seed).

- [ ] **Step 6: Commit any doc fixes from validation; update execution memory + push (on user approval)**

```bash
git add -A && git commit -m "test(plugin): local validation pass (USER/MAINTAINER mode, Fork 1B, caps)" || echo "nothing to commit"
```
Update `project_plugin_packaging_execution.md`: mark all tasks ✅, record final SHA, advance ▶ RESUME to "ship (merge/PR + optional plugin release)". Push / merge only on user approval.

---

## Phase 3 — agy-side automation (installer deploys the driving skill to agy)

**Context (all verified live):** agy (Antigravity) has a native plugin system — a folder dropped into
`%USERPROFILE%\.gemini\config\plugins\<name>\` with a **root** `plugin.json` of exactly `{name, version,
description}` (confirmed from the installed `agy-autotrain` plugin) plus `skills/<name>/SKILL.md`. agy has
**no** plugin lifecycle hooks, so it receives the **static seed** driving skill only (no autotrain loop). The
exe already registers agy's MCP server via `AgyConfigWriter`; we extend it to ALSO deploy the driving skill
and remove it on uninstall. The seed content is **embedded in the exe** (single-file publish).

**Real anchors (verified — implementer must Step-0 confirm they still match):**
- `src/FlaUI.Mcp.Server/Install/AgyConfigWriter.cs` — `Install(string exePath, IReadOnlyList<string>? args)`
  (two edits: `mcpServers` + `permissions.allow`), `Uninstall()`. There is a second `Install(exePath, addArgs,
  removeArgs)` overload used only by overlay/autosound — **do not** deploy the skill from that one.
- `src/FlaUI.Mcp.Server/Install/CliRouter.cs:196` `private readonly record struct Paths(string AgyServers,
  string AgyPerms, string GenericPath, string DataDir);` — `:200 ResolvePaths` sets `home =
  Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)` and `:207 agyServers = Path.Combine(home,
  ".gemini", "settings.json")`; `:213 Apply(...)` agy branch constructs `new AgyConfigWriter(paths.AgyServers,
  paths.AgyPerms)` (lines ~220 install / ~246 overlay).
- agy plugins base dir = `Path.Combine(home, ".gemini", "config", "plugins")` (verified: the dir exists and
  holds `agy-autotrain`, `superpowers`, …).
- The `.iss` needs **no change** — install runs `install --agent all` and uninstall `uninstall --agent all`,
  so the new deploy/remove ride along automatically.

**Shared-skill note:** the deployed skill is the SAME `driving-flaui-mcp/SKILL.md` (with the "Load your
learned rules first" section). On agy those growth files never exist, and that section explicitly says
"if neither exists, proceed — you are on the shipped seed only," so it no-ops gracefully. One skill, both
targets — no second variant to drift.

### Task 10: Embed the seed skill + AgyConfigWriter deploys/removes the agy plugin (TDD)

**Files:**
- Modify: `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj` (add an `EmbeddedResource`)
- Modify: `src/FlaUI.Mcp.Server/Install/AgyConfigWriter.cs`
- Modify: `test/FlaUI.Mcp.Tests/Install/AgyConfigWriterTests.cs` (**existing** — constructs `new AgyConfigWriter(servers, perms)` at lines **19, 39, 57**; the ctor change to 3 args breaks the build unless these are updated)
- Test: `test/FlaUI.Mcp.Tests/Install/AgySkillDeployTests.cs` (new)

- [ ] **Step 0: State-verify**

Run:
```
sed -n '13,42p' src/FlaUI.Mcp.Server/Install/AgyConfigWriter.cs
ls .claude/skills/driving-flaui-mcp/SKILL.md
grep -n 'EmbeddedResource\|<ItemGroup>' src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj | head
```
Expected: `AgyConfigWriter` has the constructor `(string mcpServersPath, string permissionsPath)` and
`Install(string exePath, IReadOnlyList<string>? args = null)`; the seed skill file exists. If the constructor
signature differs, STOP `STATE_MISMATCH`.

- [ ] **Step 1: Write the failing test**

Create `test/FlaUI.Mcp.Tests/Install/AgySkillDeployTests.cs`:
```csharp
using System.IO;
using FlaUI.Mcp.Server.Install;
using Xunit;

public class AgySkillDeployTests
{
    private static (string servers, string perms, string plugins) TempPaths()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-agy-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return (Path.Combine(dir, "settings.json"), Path.Combine(dir, "perms.json"), Path.Combine(dir, "plugins"));
    }

    [Fact]
    public void Install_deploys_agy_driving_skill_plugin()
    {
        var (servers, perms, plugins) = TempPaths();
        var w = new AgyConfigWriter(servers, perms, plugins);

        w.Install(@"C:\flaui-mcp.exe");

        var pluginJson = Path.Combine(plugins, "flaui-mcp", "plugin.json");
        var skill = Path.Combine(plugins, "flaui-mcp", "skills", "driving-flaui-mcp", "SKILL.md");
        Assert.True(File.Exists(pluginJson), "plugin.json deployed");
        Assert.True(File.Exists(skill), "SKILL.md deployed");
        Assert.Contains("\"name\": \"flaui-mcp\"", File.ReadAllText(pluginJson));
        Assert.Contains("Driving FlaUI.Mcp", File.ReadAllText(skill)); // seed content marker
    }

    [Fact]
    public void Uninstall_removes_the_agy_plugin_folder()
    {
        var (servers, perms, plugins) = TempPaths();
        var w = new AgyConfigWriter(servers, perms, plugins);
        w.Install(@"C:\flaui-mcp.exe");
        Assert.True(Directory.Exists(Path.Combine(plugins, "flaui-mcp")));

        w.Uninstall();

        Assert.False(Directory.Exists(Path.Combine(plugins, "flaui-mcp")), "plugin folder removed");
    }
}
```

- [ ] **Step 2: Run the test — verify it fails to compile/pass**

Run: `dotnet test -c Debug --filter "FullyQualifiedName~AgySkillDeployTests"`
Expected: FAIL — `AgyConfigWriter` has no 3-arg constructor yet.

- [ ] **Step 3: Embed the seed skill as a resource**

In `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj`, add an `ItemGroup`:
```xml
<ItemGroup>
  <EmbeddedResource Include="..\..\.claude\skills\driving-flaui-mcp\SKILL.md">
    <LogicalName>FlaUI.Mcp.Server.seed.driving-flaui-mcp.SKILL.md</LogicalName>
  </EmbeddedResource>
</ItemGroup>
```

- [ ] **Step 4: Extend AgyConfigWriter — add the plugins dir + deploy/remove**

Change the constructor to accept the plugins base dir, deploy in `Install(exePath, args?)`, and remove in
`Uninstall()`. Add these members and edits to `src/FlaUI.Mcp.Server/Install/AgyConfigWriter.cs`:
```csharp
    private const string PluginName = "flaui-mcp";
    private const string SkillResource = "FlaUI.Mcp.Server.seed.driving-flaui-mcp.SKILL.md";
    private readonly string _pluginsDir;

    // Updated constructor (add the third parameter; keep the two existing fields):
    public AgyConfigWriter(string mcpServersPath, string permissionsPath, string pluginsDir)
    {
        _serversPath = mcpServersPath;
        _permsPath = permissionsPath;
        _pluginsDir = pluginsDir;
    }

    private string PluginRoot => System.IO.Path.Combine(_pluginsDir, PluginName);

    /// <summary>Drop the static seed driving skill into agy's plugin dir (agy has no hooks → skill only).</summary>
    private void DeploySkill()
    {
        var skillDir = System.IO.Path.Combine(PluginRoot, "skills", "driving-flaui-mcp");
        System.IO.Directory.CreateDirectory(skillDir);

        var av = typeof(AgyConfigWriter).Assembly.GetName().Version;   // 4-part; trim to 3-part semver
        var version = av is null ? "0.0.0" : $"{av.Major}.{av.Minor}.{av.Build}";
        var pluginJson =
            "{\n  \"name\": \"flaui-mcp\",\n  \"version\": \"" + version + "\",\n" +
            "  \"description\": \"Driving skill (static seed) for the flaui-mcp desktop-automation MCP server.\"\n}\n";
        System.IO.File.WriteAllText(System.IO.Path.Combine(PluginRoot, "plugin.json"), pluginJson);

        using var res = typeof(AgyConfigWriter).Assembly.GetManifestResourceStream(SkillResource)
            ?? throw new System.InvalidOperationException($"embedded seed skill '{SkillResource}' missing");
        using var outFile = System.IO.File.Create(System.IO.Path.Combine(skillDir, "SKILL.md"));
        res.CopyTo(outFile);
    }

    private void RemoveSkill()
    {
        if (System.IO.Directory.Exists(PluginRoot))
            System.IO.Directory.Delete(PluginRoot, recursive: true);
    }
```
Then add `DeploySkill();` immediately before the `return new AgentResult(...)` in **BOTH** `Install`
overloads — the primary `Install(exePath, args?)` AND the `Install(exePath, addArgs, removeArgs)` overlay/
autosound overload (both create the server entry if absent, so both can be a first-registration path;
`DeploySkill` is an idempotent overwrite, so calling it from both is safe):
```csharp
        DeploySkill();
```
And at the END of `Uninstall()`, immediately before its `return new AgentResult(...)`, add:
```csharp
        RemoveSkill();
```
> `RemoveSkill` deletes only OUR managed `<pluginsDir>/flaui-mcp/` folder (the one the exe created) —
> acceptable for a managed plugin dir. It does not touch other agy plugins.

- [ ] **Step 4b: Fix the existing AgyConfigWriterTests (ctor is now 3-arg — build would break otherwise)**

`test/FlaUI.Mcp.Tests/Install/AgyConfigWriterTests.cs` constructs `new AgyConfigWriter(servers, perms)` at
lines 19, 39, 57. Update each of the three to pass a temp plugins dir so the file compiles. At the top of each
test (or via a shared helper), derive a temp dir and pass it, e.g.:
```csharp
var plugins = Path.Combine(Path.GetTempPath(), "flaui-agy-" + Path.GetRandomFileName());
// line 19: new AgyConfigWriter(servers, perms, plugins).Install(@"C:\x\flaui-mcp.exe");
// lines 39 & 57: var w = new AgyConfigWriter(servers, perms, plugins);
```
These tests assert on the servers/perms JSON only; the added skill deploy is a harmless side effect into a
temp dir. (Add `using System.IO;` if not already present.)

- [ ] **Step 5: Run the test — verify it passes**

Run: `dotnet test -c Debug --filter "FullyQualifiedName~AgySkillDeployTests|FullyQualifiedName~AgyConfigWriterTests"`
Expected: PASS (both new tests AND the three updated existing tests compile+pass). If the embedded-resource
name mismatches, the InvalidOperationException message names the expected resource — reconcile `LogicalName`
with `SkillResource`.

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj src/FlaUI.Mcp.Server/Install/AgyConfigWriter.cs test/FlaUI.Mcp.Tests/Install/AgySkillDeployTests.cs
git commit -m "feat(install): deploy static driving skill to agy on install; remove on uninstall (embedded seed)"
```

### Task 11: Wire CliRouter to pass the agy plugins dir + README

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Install/CliRouter.cs` (`Paths` record + `ResolvePaths` + `Apply` agy branches)
- Modify: `README.md`
- Test: existing headless suite (regression)

- [ ] **Step 0: State-verify** — `grep -n 'record struct Paths\|new AgyConfigWriter(' src/FlaUI.Mcp.Server/Install/CliRouter.cs` shows the `Paths` record (4 fields) and the `new AgyConfigWriter(paths.AgyServers, paths.AgyPerms)` construction sites. Else STOP `STATE_MISMATCH`.

- [ ] **Step 1: Add the plugins dir to `Paths` + `ResolvePaths`**

In `CliRouter.cs`, extend the record:
```csharp
    private readonly record struct Paths(string AgyServers, string AgyPerms, string GenericPath, string DataDir, string AgyPluginsDir);
```
In `ResolvePaths`, after the `agyPerms` line, add (honor a test override, mirroring `FLAUI_MCP_DATA_DIR`):
```csharp
        var agyPlugins = Environment.GetEnvironmentVariable("FLAUI_MCP_AGY_PLUGINS_DIR")
                         ?? Path.Combine(home, ".gemini", "config", "plugins");
```
and include `agyPlugins` as the new `AgyPluginsDir` argument in the returned `new Paths(...)`.

- [ ] **Step 2: Pass it at both `new AgyConfigWriter(...)` sites**

Change BOTH constructions to `new AgyConfigWriter(paths.AgyServers, paths.AgyPerms, paths.AgyPluginsDir)`
(the install branch ~line 220 and the overlay/autosound branch ~line 246). (The overlay branch uses the
addArgs/removeArgs overload, which does NOT deploy the skill — passing the dir is harmless.)

- [ ] **Step 3: Build + full headless suite green**

Run: `dotnet test -c Release --filter "Category!=Desktop&Category!=KnownDefect"`
Expected: PASS (all existing tests + the two new `AgySkillDeployTests`). 0 warnings on build.

- [ ] **Step 4: README — note agy gets the driving skill automatically**

In `README.md`, extend the plugin/install docs: state that running the installer (or `flaui-mcp install
--agent all`) now ALSO deploys the `driving-flaui-mcp` skill to agy (Antigravity) as a static plugin under
`%USERPROFILE%\.gemini\config\plugins\flaui-mcp\`; restart agy to load it. Note agy gets the seed skill only
(no self-improvement loop — agy has no plugin hooks), while Claude Code gets the full self-improving plugin.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/CliRouter.cs README.md
git commit -m "feat(install): resolve agy plugins dir + wire skill deploy through CliRouter; document agy parity"
```

---

## Self-review notes (author)

- **Spec coverage:** distribution + marketplace (T7), Fork 1B no-server (T7/T9), per-project storage + path unification (T1), mode-aware curate (T2), promote-to-global Hybrid (T3), Step-0 surfacing Windows-robust (T4), self-gating Stop hook / drop SessionStart in plugin (T5 repo path + T7 plugin hooks.json), source-of-truth snapshot (T6), migration `.claude/autotrain/`→`.claude/flaui-mcp/` (T1/T5), README + maintainer-collision note (T8), caps + validation (T9). Every spec component maps to a task.
- **Deliberate deviation from TDD:** markdown/manifest artifacts use review + command/`jq`/`claude plugin validate` verification instead of red/green; the one behavioral gate (curate mode detection) is smoke-tested in T9 Step 3.
- **Naming consistency:** unified data path `.claude/flaui-mcp/` (T1) is used identically by the learn skill (T1), curate USER mode (T2), the repo hook (T5), and the plugin hook (T7). Growth files: `local-growth.md` (project) + `%USERPROFILE%\.claude\flaui-mcp\global-growth.md` (global) are referenced identically in curate (T2/T3) and the driving skill (T4).
- **Real-code detail folded:** the driving skill already has `## Step 0 — load tools`, so the surfacing section is titled `## Load your learned rules first` and placed before it (T4) — no Step-number clash.
- **Open items from the spec resolved here:** manifest schema (T7, from official docs); maintainer disable mechanism (T8, `enabledPlugins`); belt-and-suspenders second skill deliberately NOT built (YAGNI — Step 0 only).
- **Phase 3 (agy automation, scope added by user after review):** real product C# → proper TDD (T10 red/green on deploy+remove). Anchored on verified symbols (`AgyConfigWriter` ctor/Install/Uninstall; `CliRouter.Paths`/`ResolvePaths`/`Apply`; agy plugins dir `%USERPROFILE%\.gemini\config\plugins\`, verified to exist). Seed shipped via embedded resource (single-file exe); same skill file as Claude (learned-rules section no-ops on agy). No `.iss` change (rides `--agent all`). agy gets static seed only — no loop (no agy plugin hooks). **NOTE:** this extends beyond the agy-reviewed spec (which was Claude-only); the spec should get a short Phase-3 addendum, OR the plan's Phase 3 stands as the record — flag for the AGY-AFTER plan review + user.
