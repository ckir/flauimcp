# Release tooling (D1 + item 12) — design

**Status:** design agreed 2026-08-20. Branch `fix/installer-smoke-gate`.
**Scope:** the first of four subprojects folded into v1.0.0. Two independent changes that share a theme —
tooling that claimed to work and did not.

---

## Why these two are one subproject

They are unrelated in code and identical in kind: **a mechanism that reports success while doing nothing,
or the opposite of what it says.** D1 tells the user their plugin will be restored and destroys it; item 12
prints `0 Warning(s)` from a gate that inspected nothing. Both were found the same way — by running
something for the first time.

They are batched because they are small, they both land in release tooling, and neither touches the other's
code. If either grows, split it; nothing here depends on them shipping together.

---

## D1 — the installer destroys a conflicting marketplace copy

### The defect, as filed versus as measured

The filed report (`docs/fix-the-tool-backlog/install-removes-conflicting-marketplace-copy-and-never-restores-it.md`)
blamed the disable/restore machinery. **That was wrong, and the correction is the whole design.**

MEASURED 2026-08-20 in isolated `CLAUDE_CONFIG_DIR` sandboxes:

| Step | Result |
|---|---|
| `claude plugin disable flaui-mcp@flaui-mcp --scope user` | exit 0, `✔ Successfully disabled`, still listed, `enabled: false` |
| the marker `disabled-plugins.json` after our install | correct: `{id: flaui-mcp@flaui-mcp, scope: user, projectPath: null}` |
| `installed_plugins.json` after our install | holds ONLY `flaui-mcp@flaui-mcp-marketplace` — the user's entry is GONE |
| `claude plugin uninstall flaui-mcp` (bare name) run against a disabled copy | exit 0, `✔ Successfully uninstalled`, list becomes EMPTY |

**Root cause: `ClaudePluginRegistrar.cs:33`.** An idempotency sweep runs
`claude plugin uninstall flaui-mcp` with the **bare plugin name**, no marketplace qualifier and no scope.
Both marketplaces install a plugin *named* `flaui-mcp`; only the qualified id distinguishes them. So the
sweep matches the user's copy and uninstalls it — moments after `ClaudeCollisionRemedy` carefully disabled
and recorded it.

⚠ **Both subsystems are individually correct.** `ClaudeCollisionRemedy` is careful code with panel history
in its comments; the registrar's sweep is a reasonable idempotency step. The defect exists only in their
sequence. This is why reading either file alone would not have found it, and why the fix is not "make the
remedy more robust".

### The fix, in two layers

**(a) Qualify the sweep — the root-cause fix.**

`ClaudePluginRegistrar.cs:33` and `:61` target `PluginIds.InstallTarget`
(`flaui-mcp@flaui-mcp-marketplace`) instead of `PluginIds.PluginName` (`flaui-mcp`). Our sweep can then
never match a copy we do not own.

Both constants already exist in `PluginArtifactWriter.cs:8-12` (`internal static class PluginIds`), so the
change is a constant swap at two call sites — no new plumbing:

```csharp
// ClaudePluginRegistrar.cs:33 and :61, currently:
_cli.Invoke(Claude, "plugin", "uninstall", PluginIds.PluginName);      // "flaui-mcp" — matches ANY marketplace
// becomes:
_cli.Invoke(Claude, "plugin", "uninstall", PluginIds.InstallTarget);   // "flaui-mcp@flaui-mcp-marketplace"
```

⚠ Both invocations are deliberately unchecked (`// swallow`) because an absent plugin is the normal case on
a first install. That stays true — a qualified id that is not installed exits non-zero exactly as the bare
name did, and the result is still ignored.

⚠ **The legacy-cleanup risk was raised and is CLOSED by measurement.** A bare-name sweep also cleans up a
copy registered under some *older* id. `git log -S` over `PluginArtifactWriter.cs` shows
`MarketplaceName = "flaui-mcp-marketplace"` is the **only value it has ever had** — introduced once at
`d9a464b`, never changed. There is no legacy id to lose. Had there been one, the fix would have been an
explicit allowlist of our own ids rather than a single qualified target.

**(b) Reinstall fallback — defence in depth.**

`ClaudeCollisionRemedy.Restore()` at `:218-222` already detects "recorded but no longer installed" and
warns. That branch becomes: **reinstall from the recorded marketplace, then re-enable.**

This is deliberately kept even though (a) removes the only known path that destroys the copy. It protects
against any other eviction — a future registrar change, a `claude` CLI behaviour shift, or the user's own
tooling. The operator chose both layers over either alone.

### The marker schema change, and the trap in it

`DisabledEntry` is `{Id, Scope, ProjectPath}`. Reinstalling needs the **originating marketplace**, so a
fourth field is required and the marker's schema version must rise.

⚠⚠ **THE MARKETPLACE NAME ALONE IS NOT ENOUGH, AND RECORDING ONLY IT WOULD SHIP A RESTORE THAT FAILS.**
Panel round 1 (Cascade Analyst) raised this and it is CONFIRMED BY MEASUREMENT:

| Attempt | Result |
|---|---|
| `claude plugin install flaui-mcp@flaui-mcp --scope user`, marketplace still registered | **exit 0**, installs |
| the same command after `claude plugin marketplace remove flaui-mcp` | **exit 1** — *"Plugin flaui-mcp not found in marketplace flaui-mcp"* |

A reinstall therefore depends on the user's marketplace still being REGISTERED, which is not guaranteed:
the user may remove it themselves, and our own uninstall path removes marketplaces at
`ClaudePluginRegistrar.cs:62`. So the entry must record the marketplace's **SOURCE**, not just its name.

**That source is recoverable at install time.** MEASURED: `<config>/plugins/known_marketplaces.json` holds

```json
{ "flaui-mcp": { "source": { "source": "github", "repo": "ckir/flauimcp" }, "installLocation": "…" } }
```

and `claude plugin marketplace list` prints the same thing. So `Apply()` can read the source when it
records the entry.

**Consequence for the field shape:** the optional field is not a string. It is
`{ name, source }` — enough to run `claude plugin marketplace add <source>` before reinstalling, when the
marketplace has gone. A recorded entry whose source could not be read at install time degrades to
re-enable-only, exactly as a v1 marker does.

**The exact schema.** Panel round 1 (Literal Implementer) — the spec named the value and not the KEY,
which would leave the reader and writer to invent it separately. Fixed here; this is the contract:

```jsonc
{ "version": 1,
  "disabled": [
    { "id": "flaui-mcp@flaui-mcp", "scope": "user", "projectPath": null,
      // OPTIONAL. Absent = written by an older build, or the source could not be read.
      "marketplace": { "name": "flaui-mcp", "source": "ckir/flauimcp" } } ] }
```

`source` is the string that would be passed to `claude plugin marketplace add`. It is read from
`known_marketplaces.json` at install time, whose measured shape is
`{"<name>": {"source": {"source": "github", "repo": "ckir/flauimcp"}, …}}` — for a `github` source the
value to persist is the `repo`. **A source kind this code does not recognise is recorded as absent, never
guessed.**

⚠⚠ **KEEPING `version: 1` IS NOT SUFFICIENT — THE MERGE IS LOSSY, AND THIS WOULD HAVE SHIPPED SILENT DATA
LOSS.** Panel round 1 (Protocol Pedant), CONFIRMED by reading the code:

- `BuildJson` at `CollisionMarker.cs:103-109` serializes **only** `id`, `scope`, `projectPath`.
- `ReadState` at `:188` maps each element to `DisabledEntry(id, scope, projectPath)`, **dropping every
  other property**.
- `Record()` reads the existing marker, merges, and rewrites the WHOLE file.

So an older build — a downgrade, or a second machine — that records any new disabled plugin **erases the
`marketplace` of every previously recorded entry**, silently. The v1 reader tolerating an unknown field
(which it does) only covers the READ path; the write path destroys it.

**This is a real constraint on the design, not a footnote.** The implementation MUST preserve unknown
properties across a merge: parse into a shape that carries the original `JsonObject` (or re-read and
merge at the `JsonNode` level) rather than round-tripping through a lossy `DisabledEntry`. **The plan owns
proving this with a test that writes a marker containing `marketplace`, performs a merge that adds a
second entry, and asserts the first entry's `marketplace` SURVIVED.**

⚠ An older build still cannot USE the field — it will re-enable only, which is the intended degrade. The
requirement is that it must not DESTROY it.

⚠ The restore order therefore becomes: **re-add the marketplace if absent → reinstall the plugin if absent
→ enable.** Each step is skipped when unnecessary, and each failure reports the exact command for the user
to run, as the existing failure paths already do. The plan owns proving each step is genuinely idempotent —
re-adding an already-registered marketplace must not error.

⚠⚠ **A NAIVE VERSION BUMP SILENTLY BREAKS DOWNGRADES, and `CollisionMarker.cs:166` is why.**
`ReadState` returns `FutureVersion` for `version > 1`, and `Restore()` at `:156` treats that as *leave it
untouched and do not act on it*. So if v2 writes `"version": 2` and the user then runs an older build — a
downgrade, or a second machine — that build sees a future marker, **abandons it, and never re-enables the
user's plugin**. The FutureVersion guard is correct and must not be weakened; the schema change has to
avoid triggering it.

**Resolution: keep `"version": 1` and add the marketplace as an OPTIONAL field.** A v1 reader ignores an
unknown property (verify this against the actual parse at `:151-170` during implementation — the plan owns
that check). A new reader treats a missing marketplace as "not recorded", which degrades to today's
behaviour: re-enable only, no reinstall. Both directions stay safe, and the FutureVersion path stays
reserved for a genuinely incompatible change.

If implementation finds a v1 reader does NOT tolerate the extra field, the fallback is a version bump plus
teaching old builds nothing — and accepting the downgrade hole explicitly, in the ledger, rather than
silently. **The plan must not proceed on the assumption; it must measure it.**

### Behaviour contract

- **Install, no conflicting copy present:** unchanged. No marker, no warning.
- **Install, conflicting copy present:** it is disabled (not uninstalled) and recorded with its
  marketplace. The existing warning — *"they will be re-enabled if you uninstall flaui-mcp"* — becomes
  TRUE, which it currently is not.
- **Uninstall, copy still installed:** re-enabled as today.
- **Uninstall, copy missing:** reinstalled from the recorded marketplace, then re-enabled. If the reinstall
  fails, the user gets the exact command to run, as the existing failure paths already do.
- **Uninstall, marker has no marketplace (written by an older build):** re-enable only, warn as today. No
  guessing.
- **Uninstall, marketplace gone AND its source was recorded:** re-add the marketplace, reinstall, enable.
- **Uninstall, marketplace gone and NO source recorded:** re-enable fails; report the plugin id and say
  plainly that it could not be restored. **Never guess a source.**
  ⚠ Panel round 1 (Cascade Analyst) is right that calling this a graceful "degrade" flatters it: the
  plugin is GONE and this path cannot bring it back. It is a dead end, and the message must read as one —
  name the id and the marketplace, and say the user must re-add the marketplace themselves. It is
  nonetheless the correct behaviour, because the alternative is guessing a source and installing something
  the user did not ask for. **The honest failure is the feature.**

⚠ **THE WARNING TEXT MUST NOT OVERSTATE WHAT WAS DONE** — the whole defect began as a message that promised
more than the code delivered, and a two-layer restore makes that easier to repeat, not harder. Panel round 1
(Blindspot Auditor). Three rules the implementation owns:

1. The install-time promise stays conditional on the record actually being written. That logic already
   exists at `ClaudeCollisionRemedy.cs:136-143` — Apply only promises a restore when `Record` succeeded —
   and it must survive the new field. **A record written WITHOUT a usable source must not promise a
   reinstall**, only a re-enable.
2. The uninstall-time message must say which of the three steps actually ran. "Restored" when only
   `enable` was needed and "reinstalled from `<marketplace>`" when the copy was rebuilt are different
   facts, and an operator debugging a lost plugin needs to know which happened.
3. A partial restore — marketplace re-added but reinstall failed — reports as a FAILURE with the remaining
   commands, never as a success. Today's failure paths already print the exact command; the new steps
   follow that convention.

### Acceptance

`scripts/install-smoke.ps1`'s two collision checks — `marketplace copy is DISABLED after install` and
`marketplace copy is RESTORED after uninstall` — go GREEN. They are already written, already correct, and
currently failing. **They are the oracle; do not adjust them to fit the implementation.**

⚠ The reinstall path needs its own coverage, because the existing checks exercise it only if something
else evicts the copy — and (a) is meant to stop that happening. The plan owns adding a check that
explicitly removes the copy between install and uninstall, then asserts it comes back.

⚠⚠ **THE TWO EXISTING CHECKS GO GREEN UNDER FIX (a) ALONE — SO THEY CANNOT PROVE FIX (b) AT ALL.** Panel
round 1 (Mechanism Gamer). Once the sweep is qualified, nothing evicts the copy, so `DISABLED after
install` and `RESTORED after uninstall` both pass with the reinstall fallback **entirely unimplemented**.
Treating a green smoke as proof that both layers work is exactly the false-GREEN this project keeps
finding. The reinstall path needs a check that FORCES its precondition:

```
install                                   -> copy disabled + recorded
claude plugin uninstall flaui-mcp@flaui-mcp   (simulate the user, or any other evictor)
claude plugin marketplace remove flaui-mcp    (the harder case: source needed)
uninstall                                 -> copy REINSTALLED and ENABLED
```

⚠ And each new check must be proven non-vacuous by a mutant, per this project's convention: with the
reinstall branch removed, the new check must go RED. A check that passes both with and without the code it
guards is worse than no check, because it reports coverage that does not exist.

---

## Item 12 — the 0-warning gate enforces nothing

### The defect

`dotnet build FlaUI.Mcp.slnx -c Release` is INCREMENTAL and does not re-report warnings for projects it
considers up to date. MEASURED during SP4: it printed `0 Warning(s)` on a tree where `--no-incremental`
printed **2**. A warning introduced by one commit is therefore invisible to every later build gate — which
is how SP4's own `MaskEscalation.cs` shipped two CS8629 warnings through a gate that reported clean.

### The fix

**One new `Directory.Build.props` at the repository root**, setting `TreatWarningsAsErrors`. MSBuild applies
it to all three projects automatically; no `.csproj` edits.

The mechanism is the point: a warning becomes an **error**, so the project **fails to build**. MSBuild never
marks a failed project up to date, so the next incremental build recompiles it and reports the error again.
**The gate enforces itself without `--no-incremental` anywhere.**

⚠ **VERIFIED SAFE TO ENABLE: a clean `--no-incremental` build of the current tree is 0 Warning(s) /
0 Error(s).** There is no latent warning waiting to become a hard failure. This was measured, not assumed —
turning warnings into errors on a tree with unknown warning state would be reckless.

### Acceptance

A **mutant proof**, not a passing build: introduce a deliberate warning (an unused variable is enough),
confirm the build **FAILS**; revert, confirm it passes. A green build proves only that the property parses.

The existing gate commands in `docs/building.md` keep working unchanged. `--no-incremental` remains useful
for a belt-and-braces check but stops being load-bearing.

⚠ **A MUTANT PROVES THE FLAG IS ON TODAY; IT PROVES NOTHING ABOUT TOMORROW.** Panel round 1 (Mechanism
Gamer): nothing stops a future `.csproj` adding `<TreatWarningsAsErrors>false</TreatWarningsAsErrors>` to
silence a stubborn warning, and the gate would then report green while enforcing nothing — the exact
failure this item exists to fix, reintroduced one layer down.

**Resolution: a source-sweep test, in the style this repo already uses.** `RedactionSurfaceInventoryTests`
is an existing precedent — a test that walks the source tree and enforces an invariant mechanically. Add
one asserting **no `.csproj` in the repo overrides `TreatWarningsAsErrors`**. It is a few lines, it runs
headless in CI, and it converts a convention into something that fails loudly.

⚠ The sweep must permit the props file itself to SET the property while forbidding a project from
overriding it — assert on `.csproj` files specifically, not on every file that mentions the string, or the
test fails on `Directory.Build.props` and on this spec.

**On test-project warnings** — panel round 1 asked whether `TreatWarningsAsErrors` is wrong for the test
project, citing `CS0618` (deliberate `[Obsolete]` use) and `CS1591` (missing XML docs). MEASURED, both are
inapplicable here: `grep` finds NO `Obsolete` usage anywhere in `test/`, and NO project sets
`GenerateDocumentationFile`, so `CS1591` cannot fire at all. The property therefore applies to all three
projects uniformly. **If a legitimate test-only warning ever appears, the correct answer is a targeted
`NoWarn` for that specific ID with a written reason — never disabling the property for the project**, which
is what the sweep above prevents.

⚠ **This does not close ROADMAP item 13** (108 bare catches repo-wide). Different defect, still tracked,
explicitly out of scope here.

---

## Testing

| Change | How it is proven |
|---|---|
| (a) qualified sweep | `install-smoke.ps1` collision checks go green |
| (b) reinstall fallback | a NEW smoke check that removes the copy between install and uninstall |
| marker optional field | a v1 marker must still restore under new code — measured, not assumed |
| item 12 | mutant: a deliberate warning must FAIL the build |

The headless suite (894) and the Desktop suite (156 + PopupGrafting 1) must stay green. Neither change
touches the pixel path, so the Desktop suite is a regression check rather than the subject.

⚠ `install-smoke.ps1` is a MANUAL gate — it needs the real `claude` CLI and network access to seed a
marketplace, and it is not part of `dotnet test`. It must be run by hand and its output recorded.

⚠⚠ **THE ONLY GATE FOR THIS SUBPROJECT DEPENDS ON A THIRD-PARTY CLI, A GITHUB CLONE AND THE NETWORK.**
Panel round 1 (Dependency Cynic). `claude plugin marketplace add ckir/flauimcp` clones over SSH, so the
gate cannot run offline, cannot run in CI, and breaks if that repository is renamed, made private, or has
its marketplace manifest changed by someone else. The smoke's own header already concedes it is
"NOT runnable in CI".

This is accepted rather than solved — building a local fake marketplace would test a fake instead of the
real CLI behaviour, and the CLI's real behaviour is precisely what this defect turned on. But it has two
consequences the plan must handle:

1. **A gate that cannot run has NOT passed.** The smoke already encodes this for the collision block
   (`'the marketplace copy could be seeded (the gate is able to run at all)'` is a CHECK, counted as a
   FAILURE when seeding fails). The new reinstall checks must be guarded the same way — never silently
   skipped.
2. **The acceptance record must state WHEN it was run and against which `claude` version**, because a
   green result is only evidence about the CLI behaviour of that day. `claude --version` goes in the
   recorded output.

---

## Out of scope, stated so it is not silently absorbed

- ROADMAP item 13 (bare catches repo-wide).
- Items 8, 4, 10 — the other three v1.0.0 subprojects, each with its own spec.
- The `agy` half of the installer. D1 is Claude-specific; nothing measured suggests the agy path has the
  same collision, and nothing here changes it.
- `SweepBackups` ignoring the `--agent` filter — a known wart noted in `install-smoke.ps1`'s own header,
  pre-existing and untouched.

---

## Panel ledger — round 1 (folded; do NOT re-raise)

**Solo panel** (relentless-adversarial-auditor; Axiom Breaker + Cascade Analyst core, plus Protocol Pedant
(persisted schema + CLI contract), Literal Implementer (executable spec), Mechanism Gamer (defines gates),
Blindspot Auditor (a human operates this during a failed install), Dependency Cynic (third-party CLI +
network). State Corruptor, Boundary Smuggler, Resource Vampire and Activation Auditor were consciously
DROPPED — no concurrency/caching, no trust boundary, no iteration or quota, no auto-discovered trigger.)

| # | Finding | Disposition |
|---|---|---|
| 1 | Marketplace NAME alone cannot reinstall — MEASURED exit 1 once the marketplace is removed | FOLDED: record the SOURCE; restore order re-add → reinstall → enable |
| 2 | The warning text could overstate what a two-layer restore did | FOLDED: three rules on the message |
| 3 | Both existing smoke checks go green under fix (a) alone, so they cannot prove fix (b) | FOLDED: a forcing check + a non-vacuity mutant |
| 4 | The only gate needs a third-party CLI, a GitHub clone and the network | FOLDED as accepted, with two consequences specified |

**agy escalation** — five seats, four open questions.

| # | Finding | Disposition |
|---|---|---|
| 5 | **The v1 merge is LOSSY: `BuildJson:107` writes 3 keys and `ReadState:188` drops the rest, so a downgrade build erases `marketplace` from every entry** | **FOLDED — the sharpest finding of the round.** CONFIRMED by reading the code. Preserving unknown properties across a merge is now a hard requirement with its own test |
| 6 | The spec named the field's VALUE but never its JSON KEY | FOLDED: the exact schema is now written out |
| 7 | Calling the no-source path a graceful "degrade" flatters it; it is a dead end | FOLDED: the message must read as a dead end. Behaviour unchanged — guessing a source is worse |
| 8 | A mutant proves the flag is on today, not that a future `.csproj` cannot override it | FOLDED: a source-sweep test forbidding `TreatWarningsAsErrors` overrides in `.csproj` |
| 9 | Re-adding by source yields alias `flauimcp`, mismatching `flaui-mcp` — "a mathematical certainty" | **REFUTED BY MEASUREMENT.** `claude plugin marketplace add ckir/flauimcp` produces alias **`flaui-mcp`**. The alias comes from the marketplace MANIFEST, not the repo name. The seat flagged it unmeasured and was right to; the certainty was misplaced |
| 10 | `CS0618`/`CS1591` make `TreatWarningsAsErrors` wrong for the test project | **REFUTED BY MEASUREMENT.** No `Obsolete` usage anywhere in `test/`; no project sets `GenerateDocumentationFile`, so CS1591 cannot fire. Recorded in the item-12 section with the right answer if one ever appears |
| 11 | Q4: `disable` happens BEFORE `Record()` persists, so a failed record strands the plugin | **PARTIALLY REFUTED.** The window is real, but it is neither silent nor unknown: `ClaudeCollisionRemedy.cs:131-134` names this hazard and REPORTS it, and `:136-143` withholds the restore promise when `Record` failed. Pre-existing, already hardened by a prior panel, out of scope here |
| — | Q1: does a v1 reader tolerate an unknown property? | Answered YES (read path only) — which is what exposed finding 5's write path |
| — | Q2: what does the sweep clean up, and what does qualifying leave behind? | Answered: any same-named plugin from any marketplace. Qualifying leaves the user's copy alone, which is the point |

**PANEL VERDICT — round 1: NEGOTIATE-then-fold. Eight findings folded, three refuted by measurement. One
(the lossy v1 merge) would have shipped silent data loss.**
