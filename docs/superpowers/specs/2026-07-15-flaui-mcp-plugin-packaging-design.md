# flaui-mcp — self-improving plugin packaging (design)

**Status:** approved design (agy-reviewed, 2 adversarial rounds → VIABLE). Ready for implementation planning.

## Goal

Package flaui-mcp so an end user can install it cleanly and its `driving-flaui-mcp` skill
**self-improves per install from that user's own tasks**, starting from a maintainer-shipped seed —
without leaking context across projects, corrupting shared state, or polluting unrelated repos.

## Context — current state (verified)

- flaui-mcp ships today via an **Inno Setup installer** (`installer/flaui-mcp.iss`): it places
  `flaui-mcp.exe` in `%localappdata%\Programs\FlaUI.Mcp`, optionally adds it to PATH, and runs
  `flaui-mcp.exe install --agent all` so the **exe self-registers** the MCP server into every detected
  agent's config. Uninstall runs `uninstall --agent all`.
- The exe registers using its **own absolute path**: `Program.cs:14` resolves
  `exePath = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location` and threads it to
  `ClaudeCodeConfigWriter.Install(exePath, …)`, which calls `claude mcp add --scope user`. **This is the
  fact Fork 1B relies on.**
- The repo's `.claude/` holds authoring assets: the `driving-flaui-mcp` skill (a ~300-line manual whose
  machine-owned `AUTOTRAIN:GROWTH` region carries accumulated wisdom) plus the just-built **flaui-autotrain**
  loop — `flaui-learn` (capture), `flaui-curate` (curate), two hooks, `settings.json`, a
  `docs/fix-the-tool-backlog/` template, and `Category=KnownDefect` CI gating.
- There is **no** plugin/marketplace manifest yet — packaging as a Claude Code plugin is greenfield.

## Architecture overview

Three delivery vehicles with disjoint responsibilities:

1. **The installer** — OS-level: places the exe, PATH, and **owns all MCP registration** (`install --agent
   all`, absolute path). Unchanged in substance.
2. **The plugin** (new, marketplace-installable) — ships the **skills + hooks only**. No MCP server
   declaration. This is the Claude-Code-facing authoring layer.
3. **The self-improving loop** — runs inside each install, **per-project by default**, with an explicit
   **promote-to-global** opt-in (Hybrid). Writes only to user-writable locations; never to the read-only
   plugin.

The maintainer separately runs the **full in-repo loop** (the same skills, in maintainer mode) to grow the
shipped seed for each release.

---

## Component 1 — Plugin & marketplace packaging

**What the plugin bundles** (all read-only in the plugin cache once installed):
- `skills/driving-flaui-mcp/SKILL.md` — the **seed** manual (its GROWTH region = the maintainer's
  accumulated wisdom at release time), plus a new **Step 0** that reads the user's learned-growth files
  (Component 3).
- `skills/flaui-learn/SKILL.md`, `skills/flaui-curate/SKILL.md` — the capture/curate skills (mode-aware,
  Component 3).
- `hooks/flaui-curate-nudge.sh` + hook wiring — the **self-gating** Stop nudge only (Component 4).

**What the plugin does NOT bundle:** any `mcpServers` declaration (Component 2); the `SessionStart`
learn-reminder hook (Component 4); the `docs/fix-the-tool-backlog/` template or any C# test scaffolding
(those are maintainer/repo-only, Component 5).

**Marketplace form:** the flaui-mcp repo becomes its **own single-plugin marketplace**:
- `.claude-plugin/marketplace.json` (lists the one plugin).
- `.claude-plugin/plugin.json` (name, version, declares the bundled skills + hooks).
- The plugin's source-of-truth skill/hook files are the repo's existing `.claude/skills/*` and
  `.claude/hooks/*` (one source; the release step snapshots them into the plugin — see Component 5).
- Install UX: `/plugin marketplace add ckir/flauimcp` then install the plugin. Documented in README
  alongside the installer step.

**Plugin version** tracks the repo version (lockstep with `.csproj` / `.iss`), so a plugin upgrade ships a
fresher seed.

---

## Component 2 — MCP registration (Fork 1B)

**Decision: the installer keeps sole ownership of MCP registration; the plugin declares no server.**

- Rationale: the exe registers Claude via its **own absolute path** (verified above), so registration is
  robust regardless of whether the user added the exe to PATH. A plugin-declared `mcpServers` invoking
  `flaui-mcp` via PATH would **silently break** whenever the user unchecks the optional add-to-PATH box
  (the rejected Fork 1A / "PATH trap").
- **No installer or exe change is required for registration** — `install --agent all` already does the
  right thing. This component is essentially "confirm status quo + do not add a plugin server block."
- Uninstall symmetry is preserved: the exe's `uninstall --agent all` cleans Claude's config; the plugin
  uninstall cleans the skills/hooks. No shared ownership, no double-registration.

---

## Component 3 — The self-improving loop (per-project default + Hybrid promote-to-global)

### Storage layout (all user-writable; never the plugin cache)

| Store | Path | Scope | Written by | Read by |
|---|---|---|---|---|
| Capture inbox | `<project>/.claude/flaui-mcp/observations.md` | project | `flaui-learn` | `flaui-curate` |
| Project growth | `<project>/.claude/flaui-mcp/local-growth.md` (≤30 lines) | project | `flaui-curate` | driving skill Step 0 |
| Global growth | `~/.claude/flaui-mcp/global-growth.md` (≤30 lines) | user (all projects) | promote-to-global | driving skill Step 0 |

Rationale for per-project default (agy-confirmed): a rule learned while driving on Project A never loads
into unrelated Project B (no cross-project noun leakage), and there is no shared global file for concurrent
sessions to corrupt.

### `flaui-learn` (capture) — changes from the repo version

- Appends one plain-English line to the **project** inbox `<project>/.claude/flaui-mcp/observations.md`
  (creating `.claude/flaui-mcp/` on first capture), instead of the repo-only `.claude/autotrain/...`.
- Capture hygiene unchanged: own words, never paste raw app-screen text.

### `flaui-curate` (curate) — mode-aware, two modes gated by structural detection

`flaui-curate` detects its mode by **repo-structure existence** (robust, not folder name):

- **MAINTAINER mode** — iff BOTH `docs/fix-the-tool-backlog/` AND `test/FlaUI.Mcp.Tests/` exist in the
  project (i.e. you are literally in the flaui-mcp repo): the **full existing loop** — promote by editing
  the actual `driving-flaui-mcp/SKILL.md` GROWTH region in place, route driver/deterministic tool-defects
  to `docs/fix-the-tool-backlog/` + `Category=KnownDefect`/`Desktop` xUnit tests. (Component 5.)
- **USER mode** — otherwise: promote into the **project growth file** `local-growth.md` (the driving skill
  is read-only in the plugin and must never be edited); a tool-defect-smelling observation is recorded as a
  **local known-quirk/workaround heuristic** in `local-growth.md` (end users cannot patch C# or run repo
  tests) — **no** backlog file, **no** test generation.

Shared across both modes: the tag scheme (class/audience/nature), the triage matrix, and the
**anti-poisoning gate** ("you are the gate; when in doubt, drop it").

**Eviction in USER mode** (W3 fix): the ≤30-line cap on `local-growth.md` uses **compress/merge → drop
lowest-leverage**; there is **no** graduate-to-floor step (the user cannot edit the shipped seed). The
file IS its own editable floor, so normal compaction applies.

### Surfacing — the driving skill's Step 0 (W1 fix, capability-grounded)

Claude Code does not document that multiple same-topic skills co-trigger, and same-`name` collisions are
undefined. So surfacing must **not** depend on a second skill firing. Instead, the **single** shipped
`driving-flaui-mcp` skill gains a **Step 0**:

> **Step 0 — load your learned rules.** Before driving, read (if present) this project's
> `.claude\flaui-mcp\local-growth.md` and your global `%USERPROFILE%\.claude\flaui-mcp\global-growth.md`
> (resolve your Windows user-profile directory, e.g. `C:\Users\<you>\.claude\…`), and treat their rules as
> extensions of this manual. They are small and maintained automatically by flaui-curate.

**Windows path robustness (plan author, load-bearing):** flaui-mcp is Windows-exclusive. Step 0 must NOT
hardcode a literal `~` for the global file — an agent may try to resolve a folder literally named `~` or
need an extra shell round-trip. Express the global path via the Windows user profile (`%USERPROFILE%` /
`C:\Users\<user>\.claude\…`) and instruct the agent to resolve it. The project file stays a project-relative
path (resolved from the working project).

This is one triggered skill directing the agent to two small files — no co-trigger gamble, no staleness
(the seed is always the current plugin version), no repo pollution (growth files are tiny and gitignorable).
The residual risk (the model skips Step 0) is bounded by placing it first in the very skill the agent just
invoked to drive.

### Hybrid — promote-to-global (explicit, user-invoked)

- A deliberate user action lifts a proven rule from a project's `local-growth.md` into
  `~/.claude/flaui-mcp/global-growth.md` (loaded by Step 0 in **every** project). Cross-project compounding
  becomes an opt-in, consciously chosen act — never silent leakage.
- Mechanism: a `flaui-curate` sub-command/prompt "promote entry N to global" (kept in the same skill to
  avoid a fourth surface). Same ≤30-line cap + anti-poisoning applies to the global file.

---

## Component 4 — Hooks in the distributed model

- **Ship the Stop `flaui-curate-nudge` hook** — it is **self-gating**: silent unless the current project's
  `observations.md` has pending items, so it never nags in unrelated projects. It already emits
  `hookSpecificOutput.additionalContext` JSON on Stop (the only Stop output that reaches the model) with a
  per-session sentinel throttle; paths point at `${CLAUDE_PROJECT_DIR}/.claude/flaui-mcp/...` (project
  scope, correct here).
- **Do NOT ship the `SessionStart` learn-reminder hook** in the plugin — it fires in *every* session
  including unrelated projects and cannot self-gate cheaply; it would be pure nag. Capture is instead
  prompted by the driving skill when the user is actually driving flaui-mcp. (The learn-reminder stays in
  the repo for the maintainer's own capture discipline.)
- Sentinel path (`.claude/flaui-mcp/.nudged-*`) is gitignored by the plugin-provided `.gitignore` guidance
  or documented for the user; runtime-created growth/inbox files are the user's (visible, gitignorable).

---

## Component 5 — Maintainer in-repo loop (grows the seed)

- The repo keeps the **full** loop unchanged in behavior: `flaui-curate` in MAINTAINER mode edits the real
  `driving-flaui-mcp/SKILL.md` GROWTH region, routes tool-defects to `docs/fix-the-tool-backlog/` +
  `Category=KnownDefect`/`Desktop` tests, and the CI gate excludes those traits. This is how the SEED grows.
- **Release step:** packaging a plugin release **snapshots** the repo's current
  `.claude/skills/driving-flaui-mcp/SKILL.md` (GROWTH region included = the seed) and the mode-aware
  `flaui-learn`/`flaui-curate` skills + the Stop hook into the plugin under `.claude-plugin/`. One source of
  truth (the repo's `.claude/`), snapshotted per release. Each release ships a richer seed.
- Because the same mode-aware skills are shipped, MAINTAINER vs USER behavior is decided at runtime by
  structural detection — no separate skill copies to keep in sync.

---

## Out of scope for v1 (YAGNI)

- **Re-seed / merge on plugin upgrade.** The seed lives read-only in the plugin and is *always* the current
  version (Step 0 reads it live), so there is no per-project stale copy to reconcile. A `flaui-reseed`/merge
  nicety can come later if a user's `local-growth` ever duplicates a newly-shipped seed rule (curate's
  "don't duplicate the floor" check already de-dupes against the current seed each run).
- **Phone-home / upstream telemetry** of user-learned wisdom. Learnings stay local; the maintainer grows the
  seed independently via the in-repo loop.
- **Aggregator MCP tool / hook-injected context.** Rejected in favor of the Step-0 file reads (simpler, no
  server/plugin coupling, no per-prompt token cost).
- **A machine-wide default store.** Rejected in review (cross-project privacy leak + concurrent-write
  corruption); global growth exists only as the explicit promote-to-global opt-in.

## Open items to confirm during implementation

- **Marketplace/plugin manifest schema** — confirm the exact `.claude-plugin/marketplace.json` +
  `plugin.json` fields and how a plugin bundles skills + a Stop hook (vs. the repo's `settings.json`
  wiring). The plugin's hook wiring lives in `plugin.json`/`hooks.json`, not the repo `settings.json`.
- **Belt-and-suspenders surfacing (optional):** in addition to Step 0, optionally also write `local-growth`
  as a *differently-named* project skill (e.g. `flaui-mcp-learned`) so that IF Claude co-triggers it, great;
  Step 0 remains the guaranteed path. Decide during the plan; default is Step 0 only (YAGNI).
- **`.gitignore` guidance** for consumers (ignore `.claude/flaui-mcp/observations.md` + `.nudged-*`; keep or
  ignore `local-growth.md` per team preference).
- **Maintainer dogfooding collision (document the workflow):** the repo's project-scope
  `.claude/skills/driving-flaui-mcp` and the published plugin's `driving-flaui-mcp` share the **same name**.
  A maintainer who installs their own plugin globally and opens Claude Code **inside the flaui-mcp repo**
  loads both under one name — the undefined same-name collision. Document that maintainers develop with the
  plugin **disabled in this repo** (via Claude Code's per-project plugin toggle / settings), so the repo's
  live skill is the single authority during development. (End users never hit this — they have no
  project-scope copy.)

## Migration (repo's existing autotrain)

The just-built repo loop uses `.claude/autotrain/observations.md` + `.claude/autotrain/graduation-candidates.md`
and hard-codes those paths in the `flaui-learn`/`flaui-curate` skills and hooks. Because the shipped skills are
**one source of truth** (mode-aware, Component 5), the repo must adopt the **unified** path
`<project>/.claude/flaui-mcp/` too:
- `.claude/autotrain/observations.md` → `.claude/flaui-mcp/observations.md` (inbox, both modes).
- `.claude/autotrain/graduation-candidates.md` → `.claude/flaui-mcp/graduation-candidates.md` (maintainer-mode only).
- Update the Stop-hook + skill paths + `.gitignore` sentinel pattern accordingly.
- MAINTAINER mode still additionally edits the real `driving-flaui-mcp/SKILL.md` GROWTH region and routes to
  `docs/fix-the-tool-backlog/` + tests, exactly as today — only the inbox/queue directory name changes.

## Verification approach

- Markdown/config artifacts: review + command checks (caps: seed GROWTH ≤30, local/global growth ≤30,
  flaui-learn body ≤10), mirroring the autotrain build's verification style.
- Mode detection: a test that `flaui-curate` in a scratch dir (no repo structure) chooses USER mode and
  never touches a `driving-flaui-mcp` skill; in the repo it chooses MAINTAINER mode.
- Registration: confirm (existing test / manual) that `install --agent claude` writes the absolute
  `Environment.ProcessPath`, and that the plugin declares no server (no double registration).
- Surfacing: an empirical check that a driving session reads Step 0's growth files.

## Review provenance

agy adversarial design review, 2 rounds (cascade dc853b01):
- Round 1 killed the initial machine-wide + read-only-overlay design (W6 cross-project privacy leak, W5
  global-write races, W2 PATH trap on Fork 1A, W1 two-skill collision, W7 curate dual-mode, W3/W8) →
  verdict RETHINK.
- Round 2 assessed the per-project + copy-on-init counter → VIABLE, but flagged copy-on-init hazards
  (staleness, brittle init, repo pollution, out-of-project litter). Folded by dropping copy-on-init in favor
  of **read-only seed skill + Step-0 file reads** (agy's own refinement) + per-project storage + Fork 1B.
- Multi-skill scoping facts (Claude Code docs) confirmed co-triggering is undocumented → drove the Step-0
  single-skill surfacing rather than a second skill.
- **AGY-AFTER panel on this written spec** (FIX-THESE-FIRST → folded): (1) Step-0 must use a Windows-robust
  global path (`%USERPROFILE%`), not a literal `~`, since flaui-mcp is Windows-only; (2) documented the
  maintainer same-name collision (repo project-skill vs installed plugin skill) → develop with the plugin
  disabled in-repo. Both folded above; no design forks reopened.
