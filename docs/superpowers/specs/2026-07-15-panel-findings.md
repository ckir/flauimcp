# Panel findings — v0.14.0 distribution review (2026-07-15)

Two independent adversarial panels over `2026-07-15-bundled-skill-distribution-design.md`:
a local 10-seat panel (verified against code) and an agy-peer 10-seat panel (same protocol,
same seats, no knowledge of the local findings). ~22 + 7 findings. Verdict: **NOT GREEN**.

**Key methodological result:** the local panel reviewed the document *against the code*; the agy
panel reviewed *the document*. agy's every citation pointed at the spec's own line numbers, never at
code. Consequently their findings are near-disjoint and complementary — agy caught two defects the
local panel missed *because* it was uncontaminated by the driver's assumptions (see A2, A3 below).

---

## TIER 1 — defects ALREADY LIVE in shipped v0.14.0 (independent of any redesign)

These are in code users have installed **right now**. Fix first.

### L1. Plugin manifest `hooks` key → plugin cannot load at all
`plugins/flaui-mcp/.claude-plugin/plugin.json` declares `"hooks": "./hooks/hooks.json"`.
Claude Code auto-loads `hooks/hooks.json` by convention → duplicate-file → **hard load failure**.
Anyone following `README.md:161-162` gets zero skills and zero hooks.
- `claude plugin validate` **PASSES** on this manifest. Validation is not a load test.
- **Verified fix:** delete the key. Re-tested live: `✘ failed to load` → `✔ enabled`, and
  `Hooks (1) Stop` still loads via convention. Nothing is lost.

### L2. Skill-deploy failure is invisible by construction — the v0.14.0 incident pattern
`AgyConfigWriter.cs:72` calls `DeploySkill()` with **no try/catch**. `DeploySkill()` itself throws
`InvalidOperationException` when the embedded resource is missing (`AgyConfigWriter.cs:44-45`).
The returned `AgentResult.Detail` is `$"{_serversPath}; {_permsPath}"` — it **never mentions the
skill directory**, success or failure. `CliRouter.Apply()` (`CliRouter.cs:215-236`) has no
try/catch either.
- **Failure:** resource missing/mis-cased in a future build → `install` throws → non-zero exit →
  but `installer/flaui-mcp.iss:38` runs it `runhidden waituntilterminated` with **no `Check:`** and
  Inno does not fail install on a non-zero `[Run]` step. Wizard says "completed successfully."
- This is literally *"The installation completed without errors"* while the payload is broken.

### L3. Writer ordering makes the agy peer a single point of failure for Claude
`CliRouter.Apply()` calls `AgyConfigWriter.Install` **first**, then `ClaudeCodeConfigWriter.Install`,
then `GenericMcpConfigWriter.Install`, in a plain loop with no try/catch (also none in `Run`
`CliRouter.cs:32-38`, none in `Program.cs:12-16`).
- **Failure:** any agy-side throw (missing resource, permission denied, disk full, path in use)
  aborts before Claude or generic are configured. Claude gets **nothing** — not even the
  unconditional tier — and Setup still reports success (L2).

### L4. Uninstall ignores "No, keep my settings"
`InitializeUninstall()` (`flaui-mcp.iss:84-89`) prompts, and `ShouldPurge`/`ShouldKeep`
(`flaui-mcp.iss:91-98`) select between `uninstall --agent all --purge-data` and
`uninstall --agent all`. **Both** dispatch to `RemoveSkill()` (`AgyConfigWriter.cs:50-54`):
unconditional `Directory.Delete(PluginRoot, recursive: true)`, no ownership check, no confirmation.
- **Failure:** user drops a note/symlink/customized SKILL.md into the plugin dir, answers **No**
  expecting preservation, and it is recursively deleted anyway.

### L5. Uninstall failure skips post-loop cleanup, silently
`SweepBackups`/`PurgeDataDir` (`CliRouter.cs:103-110`) run only **after** the writer loop. A throw in
`RemoveSkill()` (e.g. a running agent holds the dir open, AV lock) aborts before backups are swept or
`--purge-data` is honored — and `[UninstallRun]` is equally un-gated on exit code, so the uninstall
wizard reports success. A subsequent reinstall lands on stale orphaned files.

### L6. No way to ask what was actually deployed
`print-config` (`CliRouter.cs:28-30`) calls only `GenericMcpConfigWriter().PrintConfig(exePath)`.
It reports nothing about `~/.claude/skills/flaui-mcp/`, `~/.gemini/config/plugins/flaui-mcp/`,
which payload landed, or which build. No command answers "did the skill deploy?"

---

## TIER 2 — defects in the DESIGN (fix in the distribution-only rewrite)

### D1. The disable incantation does not exist for this delivery path  ⚑ BOTH PANELS, TOP-RANKED
`README.md:306-311` says disable via `enabledPlugins: {"flaui-mcp@flaui-mcp": false}` — a
**marketplace** identity (`plugin@marketplace`), built for the Fork 1B path. Mechanism A produces
`flaui-mcp@skills-dir`. There is no `flaui-mcp@flaui-mcp` to toggle.
- **Failure:** maintainer follows the README, the setting silently no-ops, both same-named
  `driving-flaui-mcp` skills load, and MAINTAINER mode is not authoritative — with no signal.
- agy's framing: the maintainer is **locked out of testing edits to their own skill**.
- **Unresolved:** it is unknown whether skills-dir plugins support per-project disable at all. If
  they don't, mechanism A needs a different answer (e.g. don't bundle when inside this repo).
  **This must be resolved before the rewrite is final — it can invalidate part of mechanism A.**

### D2. `--contributor` CLI shape is specified wrong
The spec says it "follows the same shape as `--agent`". `--agent` is a **valued** option
(`OptionValue(args, "--agent")`, `CliRouter.cs:16`). A bare boolean needs `HasFlag`, as
`--purge-data` does (`CliRouter.cs:18`).
- **Failure:** implementer wires `OptionValue(args, "--contributor")`; the documented command
  `install --agent all --contributor` puts the flag last → no following token → returns null →
  contributor payload silently skipped, exit 0. The common case, not an edge case.

### D3. "Mirrors `DeploySkill()` exactly" is false — the method cannot deploy a second skill
`AgyConfigWriter.cs:17` has a single hardcoded `SkillResource` const; `DeploySkill()` writes exactly
one file and stamps a fixed `plugin.json` description naming only "Driving skill (static seed)".
`FlaUI.Mcp.Server.csproj:8-10` embeds **only** `driving-flaui-mcp/SKILL.md` — `flaui-learn` is not
embedded at all, and no `LogicalName` convention exists for a second resource.
`AgyConfigWriter.Install` has no contributor parameter.
The spec sold real implementation surface as free parity.

### D4. No home named for the Claude-side deploy logic
`ClaudeCodeConfigWriter` does **zero file I/O** today — it only shells `claude mcp add/remove`
(`ClaudeCodeConfigWriter.cs:18-52`). The spec never says where the new write logic lives. Three
incompatible readings: a `DeploySkill()` on `ClaudeCodeConfigWriter`; a new class in
`CliRouter.Apply`; or inline in `CliRouter`. Different testable units, different meaning for the
stated unit tests.

### D5. The test gate is theater
`ci.yml`/`release.yml` run only `dotnet build`/`dotnet test`/Inno packaging — **no Claude Code CLI,
no `~/.claude`**. The specified "install and assert loaded" smoke **cannot run in CI**. Nothing
blocks a merge or release on it. It is a manual to-do dressed as a gate — enforced by the same
discipline that let v0.14.0 ship broken (the pre-push smoke that *did* run covered only the agy seed).
- Any real gate must either run headless or be an explicit, named, blocking manual step.

### D6. The marketplace-collision "mitigation" is a warning, not a gate
The Risks table says "the installer should detect and warn". A warning cannot stop the condition it
names — especially on a silent/unattended install where no one reads dialog text.

### D7. skills-dir is an unpinned host-platform bet with no runtime detection
The tripwire is a build-time smoke on the maintainer's machine. It cannot observe drift on a user's
machine: if Claude Code changes discovery, already-installed users silently stop getting the skill,
with zero signal to them or the maintainer. No runtime capability check is proposed.
- Same bet is made on `~/.gemini/config/plugins/` for agy, which has **no fallback named anywhere**
  (the Claude side at least has "fallback is B"), and the Risks table has no row for it.

### D8. "Verified live" was measured on the wrong population
The skills-dir load was verified on an initialized dev profile. `setup.exe` targets users who may
have **never run Claude Code** — `~/.claude` may not exist, and first-run init has not happened.
The one case that matters most is the one not confirmed.

### D9. Spec factual error — skills vs hooks conflated
The payload table's repo-only row lists `flaui-curate, flaui-learn-reminder, flaui-curate-nudge`,
then justifies "requires no work" by claiming `.claude/skills/` "already holds all three skills".
It does not: it holds `driving-flaui-mcp`, `flaui-curate`, `flaui-learn`. The two reminders are
**hook scripts** in `.claude/hooks/`, wired via `.claude/settings.json`. The evidence sentence points
at the wrong artifacts.

---

## TIER 3 — why the GLOBAL INBOX half was CUT (7 independent nails, 2 models)

Retained as the record for a future SP2 brainstorm. **Do not resurrect without answering these.**

### G1. It does not deliver capture — the stated justification fails
The design's whole premise is "capture wherever the tools are driven". But the only thing that
*prompts* capture — `flaui-learn-reminder.sh` (SessionStart) — is wired **only** in this repo's
`.claude/settings.json` and is explicitly not shipped. And `flaui-learn`'s frontmatter says
"while dogfooding **this project's** MCP server", which reads as repo-scoped and invites the model to
judge relevance by "am I in the repo".
- The design relocates *where* observations land and does nothing about *whether* capture happens
  outside the repo — **the harder half of the stated problem**. Found independently by 2 seats.

### G2. The nudge would read a permanently dead path
`.claude/hooks/flaui-curate-nudge.sh:15` hardcodes `inbox="$root/.claude/flaui-mcp/observations.md"`.
Move capture to the global file and nothing ever writes there again; the repo `## Pending` is already
empty, so `grep -qE '^- '` never matches and the Stop nudge silently never fires again — including in
MAINTAINER mode, the one place that is supposed to drain. Found by 2 seats.

### G3. It voids the anti-poisoning gate  ⚑ SECURITY
`flaui-curate/SKILL.md:42` rejects anything "whose wording looks lifted from dogfooded app content" —
a heuristic that only functions because the curator is *familiar with the apps being dogfooded*.
Pooling captures from arbitrary projects destroys that familiarity. Path: untrusted terminal buffer
(`driving-flaui-mcp/SKILL.md:255` — "a live injection surface") → agent paraphrases into a plausible
"observation" → global inbox → curator cannot recognize it as foreign → GROWTH region → **shipped to
every user and executed as operating instructions**. The gate's text is unchanged; its detection
capability is silently voided.

### G4. Cross-project confidentiality leak  ⚑ SECURITY
"Own words, never paste raw text" blocks literal quoting, not gist-level disclosure. An observation
captured during confidential/client work pools into a global file the maintainer later reads and may
promote into a **public** repo's shipped SKILL.md. No per-entry confidentiality check exists — the
gate screens for unverified/over-general/lifted, not for "originated in a confidential context".

### G5. The provenance field is itself a disclosure
The `(<project-dir-basename>)` field — added at agy's own round-1 suggestion — durably records
project names in a global file. Client/contract dirs routinely *are* the client name. This field did
not need to exist under project-local capture (provenance was implicit).

### G6. Concurrent writes race  ⚑ agy caught this; local panel MISSED it
Two parallel agent sessions in different projects both invoke `flaui-learn` → unsynchronized append
to one file → corruption, IO lock, or a silently dropped observation. No locking specified.
- **Local panel missed it because the driver consciously dropped the State Corruptor seat**, judging
  its surface "covered by Cascade Analyst and Boundary Smuggler". It was not.

### G7. `--purge-data` destroys months of insight  ⚑ agy caught this; local panel MISSED it
The spec gated the inbox behind `--purge-data` and called that user-data protection. But
`--purge-data` is exactly what a user runs to reset a corrupted config, and `flaui-mcp.iss:85`
describes that prompt as removing *"configuration and backups"* — no warning that it now also
destroys uncurated captured knowledge. High-value data co-located with disposable junk.

### G8. A checkbox does not make someone a contributor  ⚑ agy caught this; local panel MISSED it
A curious user ticks "Install autotrain loop", captures forever, and has **no curator** (repo-only) to
drain it → unbounded growth on a stranger's machine.
- **Local panel missed it because the driver's own ledger asserted "strangers never capture —
  flaui-learn is contributor-gated" as settled fact.** It was an assumption, fed to the seats as
  ground truth. A ledger of "settled" items can blind a panel; only put *measured* facts in it.

### G9. Unbounded artifacts with no governance
Every downstream artifact is capped (GROWTH ≤30 lines `flaui-curate/SKILL.md:51`; `local-growth.md`
≤30 `:86`; `global-growth.md` ≤30 `:96`) — but:
- the **inbox** has no size cap and no backlog visibility (the nudge is presence-only: fires
  identically at 1 or 10,000 pending lines);
- the **hand-authored floor of `driving-flaui-mcp/SKILL.md`** — 320 lines today, of which GROWTH is
  only lines 314-320, so ~314 lines are floor — has **no cap, no compaction, no split story**, and it
  is the file shipped unconditionally to every user. Graduation grows it monotonically forever.

### G10. `flaui-learn`'s append has no failure path
The append is executed by the LLM following prose, not a script with error propagation. No guidance
for a missing parent dir, denied write, or full disk — and the instruction's own "return immediately"
bias actively discourages verifying success. `flaui-curate` only sees what landed, so a dropped
observation is undetectable downstream.

### G11. Multi-line and delimiter injection in the line format
`- [YYYY-MM-DD] (<basename>) <text>` has no escaping rule. A Windows duplicate-folder basename
`flauimcp (2)` yields `- [2026-07-15] (flauimcp (2)) ...` — a first-match `\(([^)]*)\)` parse captures
`flauimcp (2` and strands `)` onto the text. Symmetrically, an observation opening with a
parenthetical is misattributed as the basename. A basename like `test)-[malformed` injects structural
Markdown. And an LLM-authored "one line" routinely contains newlines, breaking the flat list.

---

## Ledger discipline — lessons for the next panel

1. **Only put MEASURED facts in an "already settled, do not re-raise" ledger.** An assumption stated
   as settled fact blinds every seat that reads it (cost: G8).
2. **Do not drop a triggered seat on a judgment that another seat "covers" it** (cost: G6).
3. **A second-model panel must be uncontaminated** by the first panel's findings, or it grades
   homework instead of reviewing.
4. **Bind the peer to measurement**: name the exact files, require a `file:line` from the *referenced
   code* per finding, and treat an all-artifact-line citation set as a text-only review.
   (Captured to the agy-autotrain inbox as an `anti-pattern (driver/probabilistic)`.)
5. `claude plugin validate` passing means schema-valid, **not** load-working.
