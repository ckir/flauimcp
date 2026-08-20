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

They are batched because they both land in release tooling and neither touches the other's code.

⚠ **THE "IF EITHER GROWS, SPLIT IT" CLAUSE WAS TESTED AND THE OPERATOR CHOSE NOT TO SPLIT.** Panel round 4
argued for a split on the grounds that the original rationale — "both are small" — no longer holds: D1 grew
from a two-line constant swap into a v2 schema migration, a three-kind source recorder, a three-step
restore, path normalisation and uninstall-safety rules, while item 12 remained one props file and a sweep.
The argument is sound and the asymmetry is real.

**Decision: keep them together.** One branch, one review cycle, one merge; the panel work for both is
already done, and the two changes genuinely do not touch each other's code. The accepted costs, stated
rather than waved away: a trivial change waits on a risky one, item 12's protection arrives only when D1 is
finished, and a reviewer must weigh two very different risk profiles in one diff.

⚠ Do not re-raise the split. It was argued, costed and decided.

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

⚠ **The legacy-cleanup risk is PARTIALLY closed, and the remainder is a PREREQUISITE the plan must
discharge before fix (a) lands.** A bare-name sweep also cleans up a copy registered under some *older*
id. `git log -S` over `PluginArtifactWriter.cs` shows `MarketplaceName = "flaui-mcp-marketplace"` is the
**only value it has ever had** — introduced once at `d9a464b`, never changed.

⚠ **That proves the NAME never changed. It does NOT prove the registration MECHANISM never changed**, and
panel round 4 caught this section still claiming the risk was fully closed while the round-2 ledger already
recorded it as an open gap. If an early release registered the plugin some other way — a skills-directory
deployment, a differently-named local marketplace — the bare-name sweep would have cleaned it up and the
qualified sweep will not, leaving two plugins named `flaui-mcp` active at once.

**Prerequisite:** check the released tags for any other registration mechanism before landing fix (a). If
one exists, the fix becomes an explicit allowlist of our own historical ids rather than a single qualified
target.

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
`{ name, kind, source }` — enough to run `claude plugin marketplace add <source>` before reinstalling, when
the marketplace has gone. A recorded entry whose source could not be read at install time, or whose kind is
unrecognised, degrades to re-enable-only.

**The exact schema.** Panel round 1 (Literal Implementer) — the spec named the value and not the KEY,
which would leave the reader and writer to invent it separately. Fixed here; this is the contract:

```jsonc
{ "version": 2,
  "disabled": [
    { "id": "flaui-mcp@flaui-mcp", "scope": "user", "projectPath": null,
      // OPTIONAL even at v2: absent when the source could not be read, or its kind is unrecognised.
      // `kind` is recorded for DIAGNOSTICS — an unsupported kind must be visible, not silent.
      "marketplace": { "name": "flaui-mcp", "kind": "github", "source": "ckir/flauimcp" } } ] }
```

`source` is the string passed verbatim to `claude plugin marketplace add`. It is read from
`known_marketplaces.json` at install time; the kind-to-field mapping is in the table below.

⚠ **THE NEW WRITER MUST STILL ROUND-TRIP ITS OWN FIELD.** The v2 bump protects the marker from the SHIPPED
v1 binary; it does nothing about a v2 writer that drops its own data. `BuildJson`
(`CollisionMarker.cs:103-109`) and the `DisabledEntry` mapping (`:188`) both need the field. **The plan
owns a test that writes a marker containing `marketplace`, performs a merge that adds a SECOND entry, and
asserts the first entry's `marketplace` survived** — the same lossy-merge shape, one version up.

⚠⚠ **THE MARKER GOES TO `version: 2`. MY ROUND-1 FOLD SAID KEEP v1, AND IT WAS WRONG.**
Panel round 2 (Axiom Breaker) refuted it, and the refutation is CONFIRMED by measurement.

The round-1 reasoning was: keep `version: 1` so an older build still restores, and require the new code to
preserve unknown properties across a merge. The second half is impossible. **v0.20.0 has already shipped**
(`git cat-file -e v0.20.0:src/.../CollisionMarker.cs` succeeds) carrying a writer that is lossy by
construction:

- `BuildJson` at `CollisionMarker.cs:103-109` serializes **only** `id`, `scope`, `projectPath`.
- `ReadState` at `:188` maps each element to `DisabledEntry(...)`, **dropping every other property**.
- `Record()` reads, merges, and rewrites the WHOLE file.

That binary is on users' machines and **cannot be patched retroactively.** Requiring "the implementation
MUST preserve unknown properties" constrains only the NEW code; the shipped one will still parse a v1
marker, drop `marketplace`, and rewrite it. Keeping v1 therefore guarantees the silent destruction of the
exact data the reinstall depends on.

**Both options lose something; the operator chose the loss that is visible and recoverable.**

| | v1 marker + shipped v0.20.0 | **v2 marker + shipped v0.20.0** |
|---|---|---|
| what the old build does | parses it, drops `marketplace`, rewrites | sees `FutureVersion` at `:166`, leaves it untouched |
| what the user sees | nothing | *"the restore record … was written by a newer flaui-mcp; it was left in place and not acted on"* (`ClaudeCollisionRemedy.cs:156-158`) |
| the marker afterwards | **source silently destroyed, permanently** | **intact** |
| the user's plugin | stays disabled, now unrestorable | stays disabled, fully restorable by the newer build |

**The `FutureVersion` guard is doing exactly its job** — it exists so an old build refuses to act on a
record it does not understand, rather than half-acting on it. Bumping to 2 uses that mechanism as designed.

⚠ The cost is real and must be stated in the release notes: **an older build can no longer restore a
marker written by this one.** It will say so plainly rather than failing silently, and running the newer
build again fixes it completely.

⚠⚠ **THE `FutureVersion` MESSAGE IS NOW LOAD-BEARING AND MUST BE REWRITTEN — THE v2 DECISION IS WHAT MAKES
IT SO.** Panel round 3 (Blindspot Auditor / open question 1). Today it reads, at
`ClaudeCollisionRemedy.cs:156-158`:

> *"the restore record at `<path>` was written by a newer flaui-mcp; it was left in place and not acted on."*

Before this change that message was nearly unreachable. After it, **any user who downgrades hits it during
UNINSTALL** — the worst possible moment, because they have just removed the tool, their plugin is still
disabled, and the message tells them nothing they can act on. It explains what happened and not what to do.

It must name the recourse: the plugin id(s) still disabled, the `claude plugin enable <id> --scope <scope>`
command for each, and that reinstalling the newer flaui-mcp and uninstalling again would also do it. The
data for this is already in hand — the entries were read before the version check rejected them.

⚠ **A message the user cannot act on is the same failure class as the warning that started this whole
defect** — the tool describing its own state instead of the user's problem. This one is a real behaviour
requirement, not documentation polish.

### Marketplace sources — all three kinds, not just `github`

Panel round 2 (open question 2) found the spec handled only `github`. MEASURED on the operator's own
machine, `known_marketplaces.json` holds **10 marketplaces: 5 `directory`, 4 `github`, 1 `git`** — so
`github` is the MINORITY kind, and `directory` is what `flaui-mcp-marketplace` itself uses. A github-only
implementation would strand the majority case.

`claude plugin marketplace add` takes *"a URL, path, or GitHub repo"* (measured from its `--help`), so one
recorded string serves all three:

| `source.source` in `known_marketplaces.json` | field to READ from it | value stored as `marketplace.source` |
|---|---|---|
| `github` | `repo` | `ckir/flauimcp` |
| `git` | `url` | the clone URL |
| `directory` | `path` | the local path |

⚠ **A kind this code does not recognise is recorded as ABSENT, never guessed** — and the set is now three,
not one. An unrecognised kind is a dead end, so the plan owns logging which kind was seen, otherwise the
next unsupported kind is invisible.

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

### Two constraints on the restore sequence

⚠⚠ **"PRESENT BY ALIAS" IS NOT "IS THE THING WE RECORDED", AND CONFLATING THEM COULD INSTALL SOMETHING THE
USER DID NOT ASK FOR.** Panel round 2 (Cascade Analyst). The re-add step is skipped when a marketplace of
that NAME exists — but a name is a local alias the user controls. If they removed `flaui-mcp` and added a
different source under the same alias, restore would skip the re-add and then run
`claude plugin install flaui-mcp@flaui-mcp`, silently installing **whatever now sits behind that name.**

**Compare the recorded `source` against the live one before skipping.** `known_marketplaces.json` exposes
the live source, so this is a read, not a guess:

- alias absent → re-add from the recorded source.
- alias present, source MATCHES → skip the re-add, proceed.
- alias present, source DIFFERS → **do not install, and do not overwrite the user's marketplace.** Report
  it: name the alias, both sources, and **skip THIS ENTRY ONLY** (`continue`, never `break`/`return`).
  ⚠ Panel round 4 (Literal Implementer): "stop" was ambiguous inside `Restore()`'s `foreach`, and reading
  it as `return` would strand every LATER entry because one alias mismatched. One entry's problem is never
  another entry's. The user's own configuration outranks our restore — for that entry.

⚠ Same principle as "never guess a source", one level up: the feature restores what it recorded, or it
says plainly that it could not.

⚠⚠ **BOUNDED, NOT BLOCKING.** Panel round 2 (Resource Vampire): `marketplace add` performs a `git clone` —
over SSH for a `github` source — and this now runs inside `uninstall`, which the Windows uninstaller
invokes (`installer/flaui-mcp.iss:44,46`). A hung clone would freeze the uninstall with the user unable to
proceed.

`ProcessRunner` already carries a `TimedOut` sentinel that the collision code handles
(`ClaudeCollisionRemedy.cs:87`), so the requirement is to **use it**: every new CLI call goes through the
same bounded runner, and a timeout degrades to the same "here is the command to run yourself" recourse as
any other failure. **A restore that cannot finish must never trap the user in an uninstall.**

⚠⚠ **NOTHING THE RESTORE DOES MAY ABORT AN UNINSTALL. THIS IS THE HARDEST CONSTRAINT IN THE SPEC.**
Panel round 3 (Cascade Analyst). The new steps read and parse a THIRD-PARTY file
(`known_marketplaces.json`) inside `uninstall` — and `installer/flaui-mcp.iss:44-47` runs that with
`Flags: runhidden waituntilterminated`. A malformed, locked, or ACL-blocked file that throws an unhandled
`JsonException` would fail the uninstall and **trap the user with software they cannot remove.**

The codebase already knows this hazard and states it at `CliRouter.cs:577`:
*"best-effort: the warning channel itself must never throw and abort uninstall"*. The requirement is to
extend that same discipline to every new read:

- Reading or parsing `known_marketplaces.json` **cannot throw**. ⚠ But "cannot read" and "not there" are
  DIFFERENT ANSWERS and must not collapse into one — panel round 4 (Cascade Analyst):
  - **File ABSENT** → there are no registered marketplaces, so the alias is definitively absent. Take the
    **"alias absent → re-add"** branch. Treating this as "unknown" would dead-end the restore in exactly
    the case where re-adding is both safe and necessary.
  - **File present but unreadable or malformed** → the live source is genuinely UNKNOWN. Skip the
    reinstall, report, continue — re-adding blind could overwrite a marketplace the user has repointed.
- **No new code on the uninstall path may propagate an exception.** A restore that cannot run is a warning;
  it is never a failed uninstall.
- The plan owns a test that puts DELIBERATELY MALFORMED JSON at that path and asserts `uninstall` still
  exits 0.

⚠ **Comparing a `directory` source is a PATH comparison, and naive string equality gets it wrong.**
Panel round 3 (open question 2). `C:\path` vs `c:\path`, `\` vs `/`, a trailing separator, or a relative
vs absolute form all denote the same location while comparing unequal — and an unequal compare triggers the
"source DIFFERS" branch, which **aborts a restore that should have proceeded.** `CollisionMarker.cs:45-49`
already faces exactly this problem for `projectPath` and solves it with a case-insensitive, normalised
comparison; `SameEntry` is the precedent to follow. `github` and `git` sources compare ordinally — only
`directory` needs path normalisation.

⚠ Panel round 2 also asked whether any halfway failure leaves the user WORSE off than the current defect.
Traced: re-add fails → same as today; reinstall fails → better (marketplace is back); enable fails →
better still (plugin is installed, one command from working). **No partial state is worse than the defect
being fixed**, which is what makes the sequence safe to attempt at all.

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

⚠⚠ **THE SWEEP MUST COVER NESTED `Directory.Build.props`, NOT JUST `.csproj` — MEASURED.** Panel round 3
(Mechanism Gamer) claimed a nested props file bypasses a `.csproj`-only sweep, and a live test CONFIRMS it:
a root props setting `true` plus `sub/Directory.Build.props` setting `false` produced
`1 Warning(s) / 0 Error(s)` and **Build succeeded** — the warning stayed a warning. MSBuild honours the
NEAREST props file, so the obvious sweep would have been trivially bypassable.

The sweep therefore asserts: **no `.csproj` AND no `Directory.Build.props` other than the repository root
one sets `TreatWarningsAsErrors`.** Scope it to build files by name rather than to any file containing the
string, or it fails on the root props file and on this spec.

⚠ Honest limit, stated so nobody mistakes the sweep for a proof: `Directory.Build.targets`, an
`<Import>`, a `.props` included by another name, or `-p:TreatWarningsAsErrors=false` on the command line
all still bypass it. The sweep raises the cost of an accidental or lazy override; it does not make the
gate tamper-proof, and it is not worth pretending otherwise.

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
| the alias-DIFFERS branch | a check that re-points the alias to another source, then asserts restore REFUSES and does not install. ⚠ Panel round 4 found this branch was specified but had NO test — an implementer could claim it worked with no way to check. It is also the only branch that protects the user from a silently wrong install |
| `directory` path comparison | the same check with a path that differs only in casing or a trailing separator, asserting restore PROCEEDS — the false-negative direction, which aborts a restore that should have run |
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

---

## Panel ledger — round 2 (folded; do NOT re-raise)

Rotation seats: **State Corruptor** (the spec now defines a persisted record two builds mutate),
**Boundary Smuggler** (a third-party JSON value is fed to a CLI), **Resource Vampire** (restore performs up
to three network/CLI operations) — plus the two core seats. Four open questions.

| # | Finding | Disposition |
|---|---|---|
| 12 | **`version: 1` is impossible: v0.20.0 has ALREADY SHIPPED with the lossy writer and cannot be patched, so it will strip `marketplace` whatever the new code does** | **FOLDED — this REFUTES my own round-1 fold.** CONFIRMED: `git cat-file -e v0.20.0:…/CollisionMarker.cs` succeeds. Marker goes to **version 2**; the operator chose the visible, recoverable failure over the silent, permanent one |
| 13 | Only `github` sources were handled | **FOLDED.** MEASURED on the operator's machine: **5 `directory`, 4 `github`, 1 `git`** — `github` is the MINORITY, and `directory` is what our own marketplace uses. All three kinds now specified |
| 14 | "Present by alias" is not "is what we recorded" — a re-pointed alias would install something else | FOLDED: compare recorded source against live before skipping the re-add; on mismatch, report and stop |
| 15 | `marketplace add` does a `git clone` inside `uninstall`, which the Windows uninstaller invokes — a hang traps the user | FOLDED: every new CLI call goes through the existing bounded `ProcessRunner`, timeout degrades to manual recourse |
| 16 | Q1: does any halfway failure leave the user worse off than the current defect? | Answered NO, and traced into the spec — that is what makes the sequence safe to attempt |
| 17 | **Q4 (least-evidenced assumption): "there is no legacy id to lose" only proves the MARKETPLACE NAME never changed, not that a pre-marketplace installer never registered the plugin some other way** | **ACCEPTED AS A REAL GAP.** `git log -S` covers `MarketplaceName`'s value, not the registration MECHANISM. The plan owns checking whether any released version registered by a different mechanism, before fix (a) lands |
| 18 | Boundary Smuggler: the recorded `source` is untrusted input fed to a CLI | **PARTIALLY REFUTED.** `ProcessRunner.cs:29-43` uses `UseShellExecute = false` + `ArgumentList`, so each argument is a separate argv entry — there is no shell to inject commands into, and a crafted string arrives as ONE literal argument. Argument-position abuse is not possible either, since our args are fixed and only the source value varies. Kept in mind, not folded as a defect |
| 19 | State Corruptor: `Record()`'s read-modify-write has no cross-process lock, so two concurrent installs lose an update | **REAL BUT OUT OF SCOPE, recorded rather than absorbed.** CONFIRMED: no `Mutex`/lock at `CollisionMarker.cs:62-91`, and `WriteAtomically` only prevents a TORN file, not a lost update. Pre-existing, unchanged by this spec, and requires two simultaneous installs. **Filing it as a tracked item is the plan's first task** — this project does not defer a verified defect merely for being pre-existing, but it also does not silently widen a subproject |
| 20 | Q3: do fixes (a) and (b) interact? Fix (a) means the copy is never evicted, so (b) never runs in the primary scenario | **CORRECT, and it is the DESIGN.** (b) is defence-in-depth for evictions we have not foreseen. It also confirms round 1's finding 3: the smoke MUST force (b)'s precondition, or (b) ships unexercised |

**PANEL VERDICT — round 2: NEGOTIATE-then-fold. Six findings folded including one that refuted a round-1
fold; one accepted as a real gap for the plan; two adjudicated (one partially refuted, one filed as
out-of-scope).**

---

## Panel ledger — round 3 (folded; do NOT re-raise)

Rotation seats: **Blindspot Auditor** (a human must now diagnose a `FutureVersion` warning mid-uninstall),
**Dependency Cynic** (the spec now depends on three CLI behaviours and a third-party JSON schema) — plus
Axiom Breaker, Cascade Analyst, Mechanism Gamer. Four open questions.

| # | Finding | Disposition |
|---|---|---|
| 21 | **Nothing the restore does may abort an uninstall** — it now reads/parses a third-party JSON file inside `uninstall`, which Inno runs with `waituntilterminated` (`flaui-mcp.iss:44-47`). Malformed or locked JSON would throw and trap the user with un-removable software | **FOLDED — the most severe of the round.** The codebase already states this discipline at `CliRouter.cs:577`; the spec now extends it to every new read, with a malformed-JSON test asserting `uninstall` still exits 0 |
| 22 | Comparing a `directory` source by string equality gets it WRONG — casing, separators, trailing slash, relative-vs-absolute all denote the same path unequally, and an unequal compare ABORTS a restore that should proceed | **FOLDED.** `CollisionMarker.cs:45-49` already solves this for `projectPath`; `SameEntry` is the precedent. Only `directory` needs normalisation |
| 23 | **A nested `Directory.Build.props` bypasses a `.csproj`-only sweep** | **FOLDED — CONFIRMED BY MEASUREMENT.** A live test (root `true` + `sub/Directory.Build.props` `false`) produced `1 Warning(s) / 0 Error(s)`, **Build succeeded**. The sweep now covers nested props files, with its residual bypasses stated honestly rather than oversold |
| 24 | The `FutureVersion` message explains what happened but not what to DO — and the v2 decision makes it reachable during UNINSTALL, the worst moment | **FOLDED.** It is now a behaviour requirement: name the disabled ids and the exact `enable` command. Same failure class as the warning that started this defect |
| 25 | Q4 (weakest section): the v2 writer still hardcodes fields into `DisabledEntry`, so a future v3 field would be stripped the same way — the structural flaw is deferred, not fixed | **ACCEPTED AS A KNOWN LIMIT, not folded as a change.** Correct diagnosis. Making the marker generically forward-compatible is a larger refactor than this subproject warrants, and the v2 bump plus the `FutureVersion` guard means a v3 marker is REFUSED by a v2 build rather than silently stripped — the failure mode is already the safe one. Recorded here so v3 does not rediscover it |
| 26 | Blindspot: the unrecognised-kind diagnostic is logged at INSTALL time but the failure surfaces at UNINSTALL, weeks later | **FOLDED IMPLICITLY by finding 24's requirement** — the restore message must name what it could not restore and why, at the moment it fails. The `kind` recorded in the marker is what makes that possible |
| 27 | Dependency Cynic: `known_marketplaces.json` is an undocumented internal format that upstream may change | **ACCEPTED, already covered.** The spec's testing section states the gate depends on third-party CLI behaviour and requires recording `claude --version` with each run. Finding 21's "must not throw" requirement is precisely what makes a schema change degrade instead of crash |
| 28 | Axiom Breaker: if the user registered the same source under a DIFFERENT alias, `add` may error on a duplicate source | **NOT FOLDED — speculative and self-flagged.** The seat offered no measurement and the claim rests on an assumed CLI uniqueness rule. If it holds, finding 21's rule already covers the consequence: the add fails, is reported, and the uninstall proceeds. The plan may measure it cheaply while implementing |
| 29 | Cascade Analyst: `ProcessRunner`'s timeout was tuned for local IPC and would guarantee timeouts on a network `git clone` | **PARTIALLY ACCEPTED.** The concern is real, but the fix is a per-call timeout rather than abandoning the bounded runner. The plan owns choosing a longer bound for `marketplace add` specifically — and finding 21 means a timeout is a warning, never a failed uninstall |

**PANEL VERDICT — round 3: NEGOTIATE-then-fold. Four findings folded (one measurement-confirmed), three
accepted as known limits with reasoning, one not folded as speculative, one partially accepted.**

---

## Panel ledger — round 4 (folded; do NOT re-raise)

Rotation focus: **coherence after heavy patching** rather than missing content — the spec had grown from
130 lines to 520 across three rounds. Seats: Literal Implementer and Protocol Pedant (re-aimed at
consistency), Axiom Breaker, Cascade Analyst, Mechanism Gamer. Four open questions.

| # | Finding | Disposition |
|---|---|---|
| 30 | **The body still declared the legacy-id risk "CLOSED by measurement" while the round-2 ledger recorded it as an open gap** — a direct self-contradiction introduced by my own folding | **FOLDED.** The body now states what `git log -S` actually proves (the NAME never changed, not the MECHANISM) and carries the prerequisite check explicitly |
| 31 | **The schema says the key is `source`; the table column said "field to persist" and listed `repo`/`url`/`path`** — an implementer following the table would serialize the wrong key | **FOLDED.** The column is now "field to READ from it" with the destination named. The table always meant extraction; the header said persistence |
| 32 | **A MISSING `known_marketplaces.json` is "no marketplaces", not "source unknown"** — collapsing them into one rule dead-ends the restore in exactly the case where re-adding is safe and necessary | **FOLDED.** Absent and unreadable are now separate branches with opposite actions |
| 33 | **"Stop" was ambiguous inside `Restore()`'s `foreach`** — read as `return`, one alias mismatch would strand every later entry | **FOLDED.** Now explicitly `continue`, never `break`/`return` |
| 34 | The alias-DIFFERS branch was specified with NO test — claimable as done with no way to check | **FOLDED** into the testing table, along with a `directory` path-comparison check for the false-negative direction |
| 35 | Q2: the subproject should SPLIT — D1 grew, item 12 did not | **RAISED TO THE OPERATOR, WHO CHOSE TO KEEP THEM TOGETHER.** The argument is sound and the asymmetry real; the decision and its accepted costs are recorded in "Why these two are one subproject". Do not re-raise |
| 36 | Q1: layer (b) is a fallback the spec admits never runs in the primary scenario, yet ~300 lines exist to make it safe | **NOTED, NOT ACTED ON.** A fair characterisation. The operator explicitly chose "qualify AND reinstall" over either alone, knowing (a) fixes the root cause; (b) is defence against evictions not yet foreseen. The observation stands as a reason to keep (b)'s scope disciplined, not to remove it |
| — | Mechanism Gamer | "no new findings" — the first seat in four rounds to return clean, on gates it called "rigorous and honest about their boundaries" |

**PANEL VERDICT — round 4: NEGOTIATE-then-fold. Five findings folded (three of them self-contradictions
introduced by earlier folding), one decided by the operator, one noted. One seat clean.**
