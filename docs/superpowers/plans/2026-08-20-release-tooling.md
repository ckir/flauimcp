# Release Tooling (D1 + ROADMAP item 12) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended)
> or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Stop the installer deregistering a user's conflicting marketplace plugin while promising to
restore it, and make the 0-warning build gate actually enforce something.

**Architecture:** Two independent changes on one branch. D1 is fixed in two layers — (a) qualify the
registrar's idempotency sweep so it can only ever match our own plugin id, and (b) teach `Restore()` to
rebuild a copy that something else evicted, which requires bumping the restore marker to `version: 2` so
it can carry the originating marketplace's SOURCE. Item 12 is one new root `Directory.Build.props` turning
warnings into errors, plus a source-sweep test that forbids any build file from overriding it.

**Tech Stack:** C# / .NET 10 (`net10.0-windows10.0.19041.0`), xUnit, MSBuild, PowerShell 7 for the manual
smoke gate. Solution file is `FlaUI.Mcp.slnx` — there is **no** `.sln`.

**Spec:** `docs/superpowers/specs/2026-08-20-release-tooling-design.md` (panel-green after five rounds,
committed `76335f7`). Read its "Out of scope" section before widening anything.

**Branch:** `fix/installer-smoke-gate`, which already carries the repaired smoke gate whose two failing
collision checks are D1's acceptance oracle.

---

## Prerequisites — read this before Task 1

The spec deferred three prerequisites to this plan. **One is already discharged; its result is recorded
here so nobody re-runs it. The other two are Tasks 1 and 5.**

### Prerequisite 1 — DISCHARGED. Fix (a) keeps its shape.

The spec required checking released tags for another plugin-registration mechanism before qualifying the
sweep, because a bare-name sweep would have cleaned up a copy registered under an older mechanism and a
qualified sweep will not.

**MEASURED across released tags:**

| Tag | `ClaudeSkillDeployer.cs` | `ClaudePluginRegistrar.cs` |
|---|---|---|
| v0.15.0, v0.16.0 | present | **absent** |
| v0.17.0 … v0.20.0 | present | present |

So v0.15.0 and v0.16.0 **did** register by a different mechanism: a copied skill directory at
`~/.claude/skills/flaui-mcp`. **But that mechanism was never a `claude` plugin at all**, so the bare-name
`claude plugin uninstall flaui-mcp` sweep never cleaned it up in the first place. It has its own dedicated
cleanup, which still runs on every install:

```csharp
// src/FlaUI.Mcp.Server/Install/CliRouter.cs:316
new ClaudeSkillDeployer(paths.ClaudeConfigDir).Remove(); // drop legacy ~/.claude/skills/flaui-mcp (skill now ships in the plugin)
```

**Conclusion: qualifying the sweep loses no legacy cleanup.** Fix (a) is the single constant swap the spec
describes. Do not add an allowlist.

### Prerequisite 2 — the v2 round-trip test. Owned by **Task 5, Step 1**.

### Prerequisite 3 — filing `Record()`'s missing cross-process lock. Owned by **Task 1**.

---

## Two corrections this plan makes to the spec

Both were found by reading the code the spec cites. Neither changes the design; both change what an
implementer must write.

**Correction A — the `FutureVersion` message cannot use data that "is already in hand".**
Spec finding 24 requires the `FutureVersion` message to name the still-disabled plugin ids and their
`enable` commands, on the grounds that "the entries were read before the version check rejected them".
**That is false.** `CollisionMarker.cs:166` returns `(MarkerState.FutureVersion, empty)` — the version
check happens *before* the `disabled` array is parsed at `:168`, so no entries are ever read. Task 5 must
therefore add a best-effort entry projection for the `FutureVersion` state, or the message has nothing to
name. This is why Task 5 comes before Task 8.

**Correction B — the out-of-process uninstall test cannot prove the new catch.**
Spec finding 37 requires the malformed-JSON test to shell out to the built exe and assert exit 0, because
an in-process `Restore()` test would prove nothing about the process boundary. That is right as far as it
goes — but `CliRouter.cs:387-391` already converts *any* throw into a `Failed` result:

```csharp
private static AgentResult Isolate(string agent, Func<AgentResult> configure)
{
    try { return configure(); }
    catch (Exception e) { return new AgentResult(agent, AgentChange.Failed, e.Message); }
}
```

…and `Report` deliberately never exits non-zero for a partial failure. So the out-of-process test would
pass **with or without** the new exception handling, which makes it vacuous as a proof of the new code.

**Resolution — two tests, each proving a different thing, each with a valid mutant:**
- **Task 9 Step 6 (in-process):** `Restore()` returns a warning *naming the unreadable file* and does not
  throw. Mutant: neuter the `catch` in `KnownMarketplaces.Read` → RED. **This proves the new code.**
- **Task 10 (out-of-process):** `flaui-mcp.exe uninstall` exits 0 with the same malformed file. **Stated
  honestly: this is an end-to-end boundary pin over an existing structural guarantee (`Isolate` +
  `Report`), not a proof of the new catch.** It is worth having — it is the only test that exercises the
  real Inno invocation path — but do not record it as proving more than it does.

---

## File Structure

**Created:**
| Path | Responsibility |
|---|---|
| `Directory.Build.props` | Repo-root MSBuild properties. `TreatWarningsAsErrors` only. Applies to all **four** projects. |
| `src/FlaUI.Mcp.Server/Install/KnownMarketplaces.cs` | Never-throwing reader for `<config>/plugins/known_marketplaces.json`, plus `SameSource` (path-normalised for `directory`). |
| `test/FlaUI.Mcp.Tests/Install/BuildPropertySweepTests.cs` | Source sweep: no build file may override `TreatWarningsAsErrors`. |
| `test/FlaUI.Mcp.Tests/Install/KnownMarketplacesTests.cs` | Unit tests for the reader and `SameSource`. |
| `test/FlaUI.Mcp.Tests/Install/ClaudeCollisionReinstallTests.cs` | Layer (b): the three-step restore, all branches. |
| `test/FlaUI.Mcp.Tests/Install/UninstallProcessBoundaryTests.cs` | Task 10's out-of-process exit-code pin. |

**Modified:**
| Path | Change |
|---|---|
| `ROADMAP.md` | Task 1: file `Record()`'s missing cross-process lock. |
| `src/FlaUI.Mcp.Server/Install/ClaudePluginRegistrar.cs:33,61` | Fix (a): `PluginIds.PluginName` → `PluginIds.InstallTarget`. |
| `src/FlaUI.Mcp.Server/Install/CollisionMarker.cs` | `MarketplaceSource` record, `DisabledEntry.Marketplace`, `SchemaVersion = 2`, `BuildJson`, `ReadState` + `FutureVersion` projection. |
| `src/FlaUI.Mcp.Server/Install/ClaudeCollisionRemedy.cs` | Records the source in `Apply()`; three-step `Restore()`; both message rewrites. |
| `src/FlaUI.Mcp.Server/Install/CliRouter.cs` | `MarketplaceAddTimeout`, `BudgetedTimeout` cap overload, `ClaudeLongRunner`, pass `ClaudeConfigDir` to the remedy. |
| `test/FlaUI.Mcp.Tests/Install/ClaudePluginRegistrarTests.cs:29` | The pinned argv for the sweep. |
| `scripts/install-smoke.ps1` | The forcing reinstall check, the alias-DIFFERS check, `claude --version` in the record. |

**Verified and deliberately NOT touched:** `InstallStatus.cs:165` — its `FutureVersion` arm ignores
`recorded`, so Task 5's projection change is safe there. Listed so nobody "fixes" it.
Also untouched: `ClaudeSkillDeployer.cs` (Prerequisite 1), ROADMAP item 13's bare catches, the agy half of
the installer, `SweepBackups`' `--agent` wart.

---

## Gate commands (use these verbatim)

```bash
# build — --no-incremental is load-bearing UNTIL Task 2 lands, and belt-and-braces after
dotnet build FlaUI.Mcp.slnx -c Release --no-incremental
# expect: 0 Warning(s) / 0 Error(s)

# headless suite
dotnet test --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"
# expect: Failed: 0, Skipped: 0, Passed: 894+   (894 is the count at 76335f7)

# Desktop suite — MAIN THREAD ONLY, physical console, no subagents co-running, ~14 min
dotnet test --filter "Category=Desktop&Category!=KnownDefect&Category!=Measurement&FullyQualifiedName!~PopupGrafting"
dotnet test --filter "FullyQualifiedName~PopupGrafting"
# expect: 156/0/0 and 1/0/0, 0 skipped
```

⚠ **`--no-build` runs deleted tests from a stale DLL.** Never pass it after changing a test file.

---

### Task 1: File `Record()`'s missing cross-process lock as tracked debt

Discharges prerequisite 3. The spec (round-2 finding 19) confirmed this is real, pre-existing, and out of
scope for the code change — but this project does not defer a verified defect silently, so it gets a
tracked slot before any code moves.

**Files:**
- Modify: `ROADMAP.md`

- [ ] **Step 1: Verify the defect still exists**

Run: `sed -n '62,91p' src/FlaUI.Mcp.Server/Install/CollisionMarker.cs`

Expected: `Record` does `ReadState` → merge → `WriteAtomically` with **no** `Mutex` and no file lock.
`WriteAtomically` (`:114-121`) uses tmp + `File.Move(overwrite: true)`, which prevents a TORN file but not
a lost update. If you find a lock, STOP and report `STATE_MISMATCH: Record now locks`.

- [ ] **Step 2: Add the ROADMAP entry**

Find the highest existing numbered item in `ROADMAP.md` and add the next number (item 13 is the last one
recorded, so this is 14 unless the file says otherwise). Text:

```markdown
**14. `CollisionMarker.Record()` has no cross-process lock — two concurrent installs lose an update.**
`Record()` reads the marker, merges, and rewrites the whole file (`CollisionMarker.cs:62-91`) with no
`Mutex` and no file lock. `WriteAtomically` (`:114-121`) prevents a TORN file, not a lost update: two
installers that both read the same baseline each write their own entry and the second overwrites the
first, so one user's disabled plugin is never restored. CONFIRMED by reading the code during the D1
review (spec `2026-08-20-release-tooling-design.md`, round-2 finding 19). Requires two simultaneous
installs, which is why it has never been observed. Pre-existing and deliberately NOT fixed in the D1
subproject — filed here rather than silently widening that scope.
```

- [ ] **Step 3: Commit**

```bash
git add ROADMAP.md
git commit -m "docs(roadmap): file Record()'s missing cross-process lock as item 14

Verified during the D1 spec review (round-2 finding 19) and confirmed by
reading CollisionMarker.cs:62-91. Pre-existing, requires two simultaneous
installs, deliberately not fixed inside the D1 subproject."
```

---

### Task 2: Turn warnings into errors (ROADMAP item 12, the fix)

**Files:**
- Create: `Directory.Build.props`

- [ ] **Step 1: Establish the tree is warning-clean BEFORE enabling the flag**

Run: `dotnet build FlaUI.Mcp.slnx -c Release --no-incremental`
Expected: `0 Warning(s)` and `0 Error(s)`.

⚠ If there are ANY warnings, STOP and report them. Turning warnings into errors on a tree with unknown
warning state would convert this task into an unscoped cleanup.

- [ ] **Step 2: Create the props file**

Create `Directory.Build.props` at the repository root:

```xml
<Project>

  <!-- ROADMAP item 12. `dotnet build` is INCREMENTAL and does not re-report warnings for projects it
       considers up to date: MEASURED during SP4, it printed `0 Warning(s)` on a tree where
       `--no-incremental` printed 2. So a warning introduced by one commit was invisible to every later
       build gate, and SP4's own MaskEscalation.cs shipped two CS8629 warnings through a gate that
       reported clean.

       Making a warning an ERROR fixes that mechanically rather than by discipline: MSBuild never marks a
       FAILED project up to date, so the next incremental build recompiles it and reports it again. The
       gate enforces itself without `--no-incremental` anywhere.

       This applies to all FOUR projects in FlaUI.Mcp.slnx (Core, Server, TestApp, Tests) — MSBuild walks
       up from each project directory to the first Directory.Build.props it finds. Verified safe to
       enable: a clean `--no-incremental` build of this tree is 0 Warning(s) / 0 Error(s).

       If a legitimate warning ever needs silencing, add a targeted <NoWarn> for that specific ID with a
       written reason. Do NOT set TreatWarningsAsErrors=false in a .csproj or a nested
       Directory.Build.props — BuildPropertySweepTests forbids both, and a nested props file silently
       wins over this one (MEASURED: root `true` + sub/Directory.Build.props `false` produced
       `1 Warning(s) / 0 Error(s)`, Build succeeded). -->
  <PropertyGroup>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>

</Project>
```

- [ ] **Step 3: Confirm the tree still builds**

Run: `dotnet build FlaUI.Mcp.slnx -c Release --no-incremental`
Expected: `0 Warning(s)` / `0 Error(s)`, `Build succeeded`.

- [ ] **Step 4: MUTANT PROOF — a warning must now FAIL the build**

A passing build proves only that the property parses. Introduce a deliberate warning: add this line as
the first statement inside `ResolvePaths` in `src/FlaUI.Mcp.Server/Install/CliRouter.cs`:

```csharp
        var deliberateWarningForMutantProof = 1;   // CS0219: assigned but never used
```

Run: `dotnet build FlaUI.Mcp.slnx -c Release --no-incremental`
Expected: **`Build FAILED`** with `error CS0219`. Not a warning — an error.

⚠ If it reports `1 Warning(s)` and `Build succeeded`, the props file is not being applied. STOP and
report `MUTANT DID NOT FIRE` rather than proceeding.

- [ ] **Step 5: Revert the mutant and re-confirm**

Remove the line.
Run: `dotnet build FlaUI.Mcp.slnx -c Release --no-incremental` — expected `0 Warning(s)` / `0 Error(s)`.
Run: `git status --short` — expected: only `Directory.Build.props` as untracked. If `CliRouter.cs` still
shows as modified, the mutant was not fully reverted.

- [ ] **Step 6: Commit**

```bash
git add Directory.Build.props
git commit -m "fix(build): make warnings errors so the 0-warning gate enforces something

ROADMAP item 12. dotnet build is incremental and does not re-report warnings
for up-to-date projects, so the gate reported 0 Warning(s) on a tree that
--no-incremental showed had 2. A failed project is never marked up to date,
so an error re-reports on every build and the gate enforces itself.

MUTANT PROVEN: an unused local (CS0219) turns Build succeeded into
Build FAILED; reverting restores 0/0."
```

---

### Task 3: Source sweep — no build file may override the flag

A mutant proves the flag is on today. Nothing stops a future `.csproj` — or a nested
`Directory.Build.props`, which MEASURABLY wins — from turning it back off. This sweep is the repo's
existing answer to that shape; `test/FlaUI.Mcp.Tests/Server/RedactionSurfaceInventoryTests.cs` is the
precedent.

**Files:**
- Create: `test/FlaUI.Mcp.Tests/Install/BuildPropertySweepTests.cs`
- Existing helper: `test/FlaUI.Mcp.Tests/Install/RepoPaths.cs` (walks up to `FlaUI.Mcp.slnx`)

- [ ] **Step 1: Write the sweep test**

Create `test/FlaUI.Mcp.Tests/Install/BuildPropertySweepTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

/// <summary>ROADMAP item 12's anti-gaming half. The root Directory.Build.props turns warnings into
/// errors; this sweep enforces that no other build file quietly turns them back off.
///
/// It covers BOTH .csproj and nested Directory.Build.props, because a .csproj-only sweep is trivially
/// bypassable: MEASURED, a root props setting `true` plus a `sub/Directory.Build.props` setting `false`
/// produced `1 Warning(s) / 0 Error(s)` and Build succeeded — MSBuild honours the NEAREST props file.
///
/// HONEST LIMIT, stated so nobody mistakes this for a proof: Directory.Build.targets, an &lt;Import&gt;,
/// a .props included under another name, or `-p:TreatWarningsAsErrors=false` on the command line all
/// still bypass it. This raises the cost of an accidental or lazy override; it does not make the gate
/// tamper-proof, and it is not worth pretending otherwise.</summary>
[Trait("Category", "SourceSweep")]
public class BuildPropertySweepTests
{
    private const string Property = "TreatWarningsAsErrors";

    // Build output, third-party, or scratch — never tracked build inputs. `.clavity` is gitignored
    // runtime state and DOES contain throwaway .csproj files (measured), so omitting it would make this
    // test fail on scratch nobody ships.
    private static readonly string[] ExcludedDirs =
        { "bin", "obj", ".git", ".clavity", ".serena", "dist", "publish", "node_modules" };

    [Fact]
    public void The_root_props_file_is_the_one_that_sets_the_property()
    {
        var root = Path.Combine(RepoPaths.Root, "Directory.Build.props");
        Assert.True(File.Exists(root), $"the root {root} must exist — it is what makes warnings errors");
        Assert.Contains($"<{Property}>true</{Property}>", File.ReadAllText(root), StringComparison.Ordinal);
    }

    [Fact]
    public void No_other_build_file_overrides_TreatWarningsAsErrors()
    {
        var rootProps = Path.Combine(RepoPaths.Root, "Directory.Build.props");
        var offenders = new List<string>();

        foreach (var file in BuildFiles())
        {
            // The root props file is the one that is SUPPOSED to set it.
            if (string.Equals(file, rootProps, StringComparison.OrdinalIgnoreCase)) continue;
            if (File.ReadAllText(file).Contains(Property, StringComparison.OrdinalIgnoreCase))
                offenders.Add(Path.GetRelativePath(RepoPaths.Root, file));
        }

        Assert.True(offenders.Count == 0,
            $"{Property} must be set ONLY by the root Directory.Build.props. These build files mention " +
            $"it and would override or contradict it: {string.Join(", ", offenders)}. If a warning " +
            "genuinely needs silencing, add a targeted <NoWarn> for that specific warning ID with a " +
            "written reason instead.");
    }

    /// Every TRACKED build file: .csproj anywhere, plus Directory.Build.props anywhere (including the
    /// root one, which the caller filters). Scoped by FILE NAME rather than by content, so a document
    /// that merely discusses the property — this plan, the spec — is never swept.
    private static IEnumerable<string> BuildFiles()
    {
        foreach (var f in Directory.EnumerateFiles(RepoPaths.Root, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(f);
            var isBuildFile = name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(name, "Directory.Build.props", StringComparison.OrdinalIgnoreCase);
            if (!isBuildFile) continue;

            var rel = Path.GetRelativePath(RepoPaths.Root, f);
            var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => ExcludedDirs.Contains(s, StringComparer.OrdinalIgnoreCase))) continue;

            yield return f;
        }
    }
}
```

- [ ] **Step 2: Run it — expect PASS**

Run: `dotnet test --filter "FullyQualifiedName~BuildPropertySweepTests"`
Expected: `Passed: 2, Failed: 0`.

⚠ Unlike a normal TDD step this starts green, because the invariant already holds. That is exactly why
Step 3 is mandatory — a green sweep proves nothing until you have seen it go red.

- [ ] **Step 3: MUTANT PROOF — the sweep must catch an override**

Temporarily add to `test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj`, inside its `<PropertyGroup>`:

```xml
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
```

Run: `dotnet test --filter "FullyQualifiedName~BuildPropertySweepTests"`
Expected: **`No_other_build_file_overrides_TreatWarningsAsErrors` FAILS**, naming
`test\FlaUI.Mcp.Tests\FlaUI.Mcp.Tests.csproj` in the message.

⚠ Confirm it is that SPECIFIC test that went red, not merely that the run returned non-zero.

- [ ] **Step 4: Revert the mutant and re-confirm**

Remove the line. Re-run the same filter — expected `Passed: 2, Failed: 0`.
Run `git status --short` — `FlaUI.Mcp.Tests.csproj` must NOT appear as modified.

- [ ] **Step 5: Commit**

```bash
git add test/FlaUI.Mcp.Tests/Install/BuildPropertySweepTests.cs
git commit -m "test(build): sweep forbids any build file overriding TreatWarningsAsErrors

A mutant proves the flag is on today, not that a future .csproj cannot turn
it off. Covers nested Directory.Build.props too, which MEASURABLY wins over
the root one. MUTANT PROVEN: TreatWarningsAsErrors=false in the test csproj
turns the sweep red; reverting restores it."
```

---

### Task 4: Qualify the registrar's sweep (D1 fix (a) — the root-cause fix)

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Install/ClaudePluginRegistrar.cs:33` and `:61`
- Modify: `test/FlaUI.Mcp.Tests/Install/ClaudePluginRegistrarTests.cs:29`

- [ ] **Step 1: STATE VERIFICATION**

Run: `grep -n 'plugin", "uninstall"' src/FlaUI.Mcp.Server/Install/ClaudePluginRegistrar.cs`

Expected — exactly two hits, `33` and `61`, both identical:

```csharp
        _cli.Invoke(Claude, "plugin", "uninstall", PluginIds.PluginName);                            // swallow
```

If either differs, STOP and report `STATE_MISMATCH: <what you found>`.

- [ ] **Step 2: Update the pinning test FIRST — it is the oracle**

In `test/FlaUI.Mcp.Tests/Install/ClaudePluginRegistrarTests.cs`, change line 29 from:

```csharp
        Assert.Equal(new[] { "/C", "claude", "plugin", "uninstall", "flaui-mcp" }, calls[2]);
```

to:

```csharp
        // D1: the sweep MUST be qualified. `flaui-mcp` is the plugin NAME, which BOTH marketplaces use —
        // MEASURED, `claude plugin uninstall flaui-mcp` run against the user's own `flaui-mcp@flaui-mcp`
        // copy exits 0 and empties the list. The qualified id is the only thing that distinguishes ours.
        Assert.Equal(new[] { "/C", "claude", "plugin", "uninstall", "flaui-mcp@flaui-mcp-marketplace" }, calls[2]);
```

- [ ] **Step 3: Run it and watch it FAIL**

Run: `dotnet test --filter "FullyQualifiedName~ClaudePluginRegistrarTests"`
Expected: `Register_runs_remove_add_uninstall_install_then_readback` FAILS — actual `flaui-mcp`, expected
`flaui-mcp@flaui-mcp-marketplace`.

- [ ] **Step 4: Apply the fix at both call sites**

`ClaudePluginRegistrar.cs:33` becomes:

```csharp
        // D1: QUALIFIED, never the bare name. `PluginIds.PluginName` matches a plugin named `flaui-mcp`
        // from ANY marketplace — including the user's own `flaui-mcp@flaui-mcp` copy that
        // ClaudeCollisionRemedy has just carefully disabled and recorded, which this swallowed sweep then
        // destroyed (MEASURED 2026-08-20: exit 0, "Successfully uninstalled", list becomes EMPTY). Both
        // subsystems were individually correct; the defect lived only in their sequence.
        _cli.Invoke(Claude, "plugin", "uninstall", PluginIds.InstallTarget);                          // swallow
```

`ClaudePluginRegistrar.cs:61` becomes:

```csharp
        _cli.Invoke(Claude, "plugin", "uninstall", PluginIds.InstallTarget);                          // swallow
```

⚠ Both stay unchecked (`// swallow`). An absent plugin is the normal case on a first install, and a
qualified id that is not installed exits non-zero exactly as the bare name did.

⚠ Do **not** add a legacy allowlist. See Prerequisite 1 — it is discharged.

- [ ] **Step 5: Run and verify GREEN**

Run: `dotnet test --filter "FullyQualifiedName~ClaudePluginRegistrarTests"` — expected `Passed: 4, Failed: 0`.
Run: `dotnet build FlaUI.Mcp.slnx -c Release --no-incremental` — expected `0 Warning(s)` / `0 Error(s)`.

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/ClaudePluginRegistrar.cs test/FlaUI.Mcp.Tests/Install/ClaudePluginRegistrarTests.cs
git commit -m "fix(install): qualify the plugin sweep so it can only match our own copy

D1 root cause. The idempotency sweep ran `claude plugin uninstall flaui-mcp`
with the bare plugin NAME. Both marketplaces install a plugin named
flaui-mcp, so the sweep matched the user's copy and uninstalled it moments
after ClaudeCollisionRemedy disabled and recorded it - which is why the
installer announced it would restore a copy it had already destroyed.

MEASURED: the bare-name uninstall against a disabled user copy exits 0 and
empties the plugin list."
```

---

### Task 5: Marker schema v2 — carry the originating marketplace

Reinstalling needs the marketplace SOURCE, not just its name: MEASURED, `claude plugin install
flaui-mcp@flaui-mcp --scope user` exits 0 while the marketplace is registered and **exit 1** —
*"Plugin flaui-mcp not found in marketplace flaui-mcp"* — once it is removed.

⚠ The marker goes to `version: 2`, not 1. A lossy v1 writer has ALREADY SHIPPED in v0.20.0
(`BuildJson:107` writes three keys, `ReadState:188` drops the rest, `Record` rewrites the whole file), so
keeping v1 would guarantee that an older build silently destroys the exact field the reinstall depends on.
At v2 that build hits the `FutureVersion` guard instead, leaves the marker intact, and says so.

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Install/CollisionMarker.cs`
- Test: `test/FlaUI.Mcp.Tests/Install/CollisionMarkerTests.cs`

- [ ] **Step 1: Write the failing tests (prerequisite 2 lives here)**

Append to `test/FlaUI.Mcp.Tests/Install/CollisionMarkerTests.cs`, inside the class. Add `using System;` to
the file's usings — it currently has only `System.IO` and `System.Linq`.

```csharp
    private static readonly MarketplaceSource GithubSrc = new("flaui-mcp", "github", "ckir/flauimcp");
    private static readonly MarketplaceSource DirSrc = new("acme", "directory", @"C:\Marketplaces\acme");

    // PREREQUISITE 2, and the sharpest test in this file. The v2 bump protects the marker from the
    // SHIPPED v1 binary; it does nothing about a v2 writer that drops its OWN data. This is the same
    // lossy-merge shape that made version 1 unsalvageable, one version up.
    [Fact]
    public void A_recorded_marketplace_survives_a_merge_that_adds_another_entry()
    {
        var s = TempState();
        CollisionMarker.Record(s, new[] { UserEntry with { Marketplace = GithubSrc } });

        CollisionMarker.Record(s, new[] { ProjA });   // a SECOND entry: read, merge, rewrite the whole file

        var read = CollisionMarker.Read(s);
        Assert.Equal(2, read.Count);
        var user = read.Single(e => e.Scope == "user");
        Assert.NotNull(user.Marketplace);
        Assert.Equal("flaui-mcp", user.Marketplace!.Name);
        Assert.Equal("github", user.Marketplace.Kind);
        Assert.Equal("ckir/flauimcp", user.Marketplace.Source);
        Assert.Null(read.Single(e => e.Scope == "local").Marketplace);   // absent stays absent
    }

    [Fact]
    public void The_written_marker_declares_version_2()
    {
        var s = TempState();
        CollisionMarker.Record(s, new[] { UserEntry with { Marketplace = GithubSrc } });
        Assert.Contains("\"version\": 2", File.ReadAllText(CollisionMarker.PathIn(s)));
    }

    [Fact]
    public void All_three_source_kinds_round_trip()
    {
        var s = TempState();
        CollisionMarker.Record(s, new[]
        {
            UserEntry with { Marketplace = GithubSrc },
            ProjA with { Marketplace = DirSrc },
            ProjB with { Marketplace = new MarketplaceSource("ecc", "git", "https://github.com/a/b.git") },
        });

        var read = CollisionMarker.Read(s);
        Assert.Equal(3, read.Count);
        Assert.Equal(new[] { "directory", "git", "github" },
            read.Select(e => e.Marketplace!.Kind).OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    // A v1 marker written by v0.20.0 must still restore under the new code. Version 1 is VALID, just
    // without a marketplace — it must NOT read as Corrupt or FutureVersion.
    [Fact]
    public void A_version_1_marker_still_reads_as_present_with_no_marketplace()
    {
        var s = TempState();
        File.WriteAllText(CollisionMarker.PathIn(s), """
            { "version": 1,
              "disabled": [ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "projectPath": null } ] }
            """);

        var e = Assert.Single(CollisionMarker.Read(s));
        Assert.Equal("flaui-mcp@flaui-mcp", e.Id);
        Assert.Null(e.Marketplace);
    }

    // The FutureVersion floor moves with the schema: 2 is now ours, 3 is the future.
    [Fact]
    public void Version_3_is_a_future_version_and_is_not_acted_on()
    {
        var s = TempState();
        File.WriteAllText(CollisionMarker.PathIn(s), """
            { "version": 3, "disabled": [ { "id": "x@y", "scope": "user", "projectPath": null } ] }
            """);

        Assert.Empty(CollisionMarker.Read(s));                                  // fail-safe: restore nothing
        Assert.NotNull(CollisionMarker.Record(s, new[] { UserEntry }));         // refuses to touch it
    }

    // CORRECTION A. The FutureVersion message must name the ids it could not restore, so ReadState has
    // to project them best-effort — the version check at :166 fires BEFORE the entries are parsed, so
    // today it returns an EMPTY list and the message would have nothing to name.
    [Fact]
    public void A_future_version_marker_still_projects_its_entries_for_the_message()
    {
        var s = TempState();
        File.WriteAllText(CollisionMarker.PathIn(s), """
            { "version": 3,
              "disabled": [ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "projectPath": null } ] }
            """);

        var (state, entries) = CollisionMarker.ReadStateForTests(s);
        Assert.Equal("FutureVersion", state);
        Assert.Equal("flaui-mcp@flaui-mcp", Assert.Single(entries).Id);
    }

    // A malformed `marketplace` object drops the FIELD, never the ENTRY. Re-enabling is still possible
    // without a source; dropping the entry would strand the user's plugin disabled forever.
    [Fact]
    public void A_malformed_marketplace_object_drops_only_the_field()
    {
        var s = TempState();
        File.WriteAllText(CollisionMarker.PathIn(s), """
            { "version": 2,
              "disabled": [ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "projectPath": null,
                              "marketplace": { "name": "flaui-mcp", "kind": 7 } } ] }
            """);

        var e = Assert.Single(CollisionMarker.Read(s));
        Assert.Equal("flaui-mcp@flaui-mcp", e.Id);
        Assert.Null(e.Marketplace);
    }
```

⚠ `ReadStateForTests` does not exist yet — Step 3 adds it. `MarkerState` is `internal` and the test
assembly has no `InternalsVisibleTo`, so the state must be surfaced by NAME through a public shim rather
than by widening the enum's visibility.

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test --filter "FullyQualifiedName~CollisionMarkerTests"`
Expected: **compile errors** — `MarketplaceSource` not found, `DisabledEntry` has no `Marketplace`,
`ReadStateForTests` not found. That is the expected failing state.

- [ ] **Step 3: Implement the schema in `CollisionMarker.cs`**

**3a.** Replace line 11 (`public sealed record DisabledEntry(...)`) and its doc comment with:

```csharp
/// <summary>Where a disabled plugin's marketplace came from, so a restore can rebuild the plugin when
/// something has evicted it. MEASURED: `claude plugin install flaui-mcp@flaui-mcp` exits 0 while the
/// marketplace is registered and exit 1 once it is removed — so the NAME alone cannot reinstall, and the
/// SOURCE is what makes the restore promise keepable.</summary>
/// <param name="Name">The local alias, e.g. "flaui-mcp". A name the USER controls, not an identity.</param>
/// <param name="Kind">"github" | "git" | "directory". Recorded for DIAGNOSTICS — an unsupported kind must
/// be visible at the moment a restore fails, not silent.</param>
/// <param name="Source">Passed VERBATIM to `claude plugin marketplace add`, which takes "a URL, path, or
/// GitHub repo" (measured from its --help), so one string serves all three kinds.</param>
public sealed record MarketplaceSource(string Name, string Kind, string Source);

/// <summary>One plugin entry that WE disabled, and everything needed to put it back.</summary>
/// <param name="ProjectPath">Null for user scope. For any other scope this is the directory the
/// enable must RUN from — `claude plugin enable` cannot target another project by flag.</param>
/// <param name="Marketplace">OPTIONAL even at v2: absent when the source could not be read at install
/// time, or its kind was unrecognised. An entry without one degrades to re-enable-only — we never guess
/// a source, because guessing would install something the user did not ask for.</param>
public sealed record DisabledEntry(string Id, string Scope, string? ProjectPath, MarketplaceSource? Marketplace = null);
```

⚠ The new parameter is **optional and last**, so every existing `new DisabledEntry(id, scope, path)` call
site keeps compiling. `SameEntry` (`:49-52`) deliberately does NOT compare it — an entry is the same entry
by id/scope/projectPath, and R1's write-once rule means a later run never rewrites an existing entry's
marketplace.

**3b.** Update the `MarkerState` doc comment (`:13-15`) — `FutureVersion` now means `> 2`:

```csharp
/// <summary>How <see cref="CollisionMarker.ReadState"/> classified the marker file.
/// Absent = no file. Corrupt = present but structurally unreadable (fail-safe: collapse to empty).
/// FutureVersion = written by a newer build (version &gt; <see cref="CollisionMarker.SchemaVersion"/>) —
/// leave it untouched. Present = a valid v1 or v2 marker.</summary>
internal enum MarkerState { Absent, Corrupt, FutureVersion, Present }
```

**3c.** Add the version constant immediately after `FileName` (`:39`):

```csharp
    /// <summary>The schema version this build WRITES, and the highest it will act on.
    ///
    /// ⚠ WHY 2 AND NOT 1. v0.20.0 has already shipped with a writer that is lossy by construction —
    /// BuildJson serialized only id/scope/projectPath and ReadState dropped every other property, while
    /// Record reads-merges-rewrites the WHOLE file. That binary is on users' machines and cannot be
    /// patched. Keeping version 1 would therefore let an older build silently strip `marketplace` from
    /// every entry, permanently destroying the one field the reinstall depends on. At version 2 the old
    /// build hits the FutureVersion guard instead: it leaves the marker intact and SAYS so. The cost is
    /// real and stated in the release notes — an older build can no longer restore a marker written by
    /// this one — but that failure is visible and recoverable, and running the newer build fixes it.</summary>
    public const int SchemaVersion = 2;
```

**3d.** Replace `BuildJson` (`:103-109`) entirely:

```csharp
    private static JsonObject BuildJson(IReadOnlyList<DisabledEntry> entries)
    {
        var arr = new JsonArray();
        foreach (var e in entries)
        {
            var o = new JsonObject { ["id"] = e.Id, ["scope"] = e.Scope, ["projectPath"] = e.ProjectPath };
            // Written only when we have one, so a v1-shaped entry stays v1-shaped inside a v2 file.
            if (e.Marketplace is { } m)
                o["marketplace"] = new JsonObject { ["name"] = m.Name, ["kind"] = m.Kind, ["source"] = m.Source };
            arr.Add(o);
        }
        return new JsonObject { ["version"] = SchemaVersion, ["disabled"] = arr };
    }
```

**3e.** In `ReadState`, replace lines `165-190` — that is, from `if (version < 1)` through the end of the
entry loop and its `return (MarkerState.Present, list);` — with:

```csharp
            if (version < 1) return (MarkerState.Corrupt, empty);
            if (o["disabled"] is not JsonArray arr) return (MarkerState.Corrupt, empty);

            // CORRECTION A: project the entries BEFORE branching on version. A FutureVersion marker's
            // ids are what the restore message needs to name so the user can re-enable them by hand —
            // and the old order (version check at :166, parse at :168) meant that message had nothing to
            // name. Best-effort: a future schema may shape entries differently, in which case this
            // yields fewer or none, which is exactly the fail-safe direction.
            var list = ParseEntries(arr);
            if (version > SchemaVersion) return (MarkerState.FutureVersion, list);
            return (MarkerState.Present, list);
```

⚠ **Move the entry loop VERBATIM into `ParseEntries` below** — comments and guards included. This is the
most error-prone edit in the plan; several of those guards exist for inputs no test exercises, so a
dropped one will not necessarily turn a test red. **Re-read the diff of this hunk before committing.**

⚠ **Consumers audited — all three are safe with a non-empty `FutureVersion` list:** `Read` (`:210`) gates
on `Present`; `Record` (`:67`) returns before using it; `Restore` (`ClaudeCollisionRemedy.cs:156`) branches
on `FutureVersion` first; `InstallStatus.cs:165`'s `FutureVersion` arm ignores `recorded` entirely. Do
**not** "fix" `InstallStatus`.

**3f.** Add these three members after `ReadState`:

```csharp
    /// The `disabled` array projection, shared by the Present and FutureVersion paths. Never throws:
    /// a wrong-typed field drops THAT entry only, never the whole file.
    private static IReadOnlyList<DisabledEntry> ParseEntries(JsonArray arr)
    {
        var list = new List<DisabledEntry>();
        foreach (var node in arr)
        {
            if (node is not JsonObject e) continue;
            // A bare (string?)e["id"] cast THROWS on a numeric/boolean node (it does NOT return
            // null), so every field is gated on GetValueKind == String; a wrong-typed field drops
            // THIS entry only, never the whole file.
            var id = AsString(e["id"]);
            var scope = AsString(e["scope"]);
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(scope)) continue;

            // projectPath may be legitimately absent or JSON null (user scope). A present-but-
            // wrong-typed projectPath drops the entry.
            var ppNode = e["projectPath"];
            string? projectPath;
            if (ppNode is null) projectPath = null;                     // absent OR JSON null
            else { projectPath = AsString(ppNode); if (projectPath is null) continue; }

            list.Add(new DisabledEntry(id!, scope!, projectPath, ParseMarketplace(e["marketplace"])));
        }
        return list;
    }

    /// <summary>v2's optional field. ALL THREE of name/kind/source or NONE: a partial record cannot drive
    /// `claude plugin marketplace add`, and completing it by guessing is exactly what the spec forbids.
    ///
    /// ⚠ A malformed marketplace drops the FIELD, never the ENTRY. Re-enabling is still possible without
    /// a source, and dropping the entry would strand the user's plugin disabled with no record that we
    /// were the ones who disabled it.</summary>
    private static MarketplaceSource? ParseMarketplace(JsonNode? node)
    {
        if (node is not JsonObject m) return null;
        var name = AsString(m["name"]);
        var kind = AsString(m["kind"]);
        var source = AsString(m["source"]);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(source))
            return null;
        return new MarketplaceSource(name!, kind!, source!);
    }

    /// <summary>Test-only projection of <see cref="ReadState"/>'s classification. <see cref="MarkerState"/>
    /// is internal and the test assembly has no InternalsVisibleTo, so the state is surfaced by NAME
    /// rather than by widening the enum — production code must keep using ReadState.</summary>
    public static (string State, IReadOnlyList<DisabledEntry> Entries) ReadStateForTests(string stateDir)
    {
        var (state, entries) = ReadState(stateDir);
        return (state.ToString(), entries);
    }
```

- [ ] **Step 4: Run and verify GREEN**

Run: `dotnet test --filter "FullyQualifiedName~CollisionMarkerTests"` — expected `Failed: 0`.

Run: `dotnet test --filter "FullyQualifiedName~ClaudeCollisionRestoreTests|FullyQualifiedName~ClaudeCollisionRemedyTests|FullyQualifiedName~InstallStatus"`
Expected: `Failed: 0`. These exercise `DisabledEntry` heavily; a break here means the optional parameter
changed a shape it should not have.

Run: `dotnet build FlaUI.Mcp.slnx -c Release --no-incremental` — expected `0 Warning(s)` / `0 Error(s)`.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/CollisionMarker.cs test/FlaUI.Mcp.Tests/Install/CollisionMarkerTests.cs
git commit -m "feat(install): marker v2 carries the originating marketplace source

Reinstalling a copy something evicted needs the marketplace SOURCE, not just
its name: MEASURED, `claude plugin install flaui-mcp@flaui-mcp` exits 0 while
the marketplace is registered and exit 1 once it is removed.

Version 2, not 1: a lossy v1 writer already SHIPPED in v0.20.0 and cannot be
patched, so keeping v1 would let an older build silently strip the field the
reinstall depends on. At v2 that build hits the FutureVersion guard, leaves
the marker intact, and says so.

Also fixes a spec error found by reading the code: the version check fired
BEFORE the entries were parsed, so a FutureVersion marker returned an empty
list and the message that must name the un-restored ids had nothing to name."
```

---

### Task 6: `KnownMarketplaces` — read the live source, never guess

MEASURED on the operator's machine, `<config>/plugins/known_marketplaces.json` holds **10 marketplaces:
5 `directory`, 4 `github`, 1 `git`** — so `github` is the MINORITY kind and `directory` is what
`flaui-mcp-marketplace` itself uses. A github-only implementation would strand the majority case.

All five `directory` sources measured are **absolute** Windows paths — which is why `NormalisePath` below
refuses to resolve a relative one rather than inventing an answer.

**Files:**
- Create: `src/FlaUI.Mcp.Server/Install/KnownMarketplaces.cs`
- Create: `test/FlaUI.Mcp.Tests/Install/KnownMarketplacesTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `test/FlaUI.Mcp.Tests/Install/KnownMarketplacesTests.cs`:

```csharp
using System.IO;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class KnownMarketplacesTests
{
    private static string TempConfig()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-claude-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(dir, "plugins"));
        return dir;
    }

    private static void Write(string cfg, string json) =>
        File.WriteAllText(KnownMarketplaces.PathIn(cfg), json);

    // ABSENT and UNREADABLE are DIFFERENT ANSWERS with OPPOSITE actions. Absent means there are no
    // registered marketplaces at all, so an alias is definitively absent and re-adding is both safe and
    // necessary; unreadable means the live source is genuinely unknown and re-adding could overwrite a
    // marketplace the user has repointed. Collapsing them dead-ends the restore in exactly the case
    // where it should proceed.
    [Fact]
    public void A_missing_file_is_FileAbsent_not_Unreadable()
    {
        var snap = KnownMarketplaces.Read(TempConfig());
        Assert.Equal(MarketplacesState.FileAbsent, snap.State);
        Assert.Empty(snap.ByName);
    }

    [Fact]
    public void A_null_config_dir_is_FileAbsent()
        => Assert.Equal(MarketplacesState.FileAbsent, KnownMarketplaces.Read(null).State);

    [Fact]
    public void Malformed_json_is_Unreadable_and_never_throws()
    {
        var cfg = TempConfig();
        Write(cfg, "{ this is not json");
        var snap = KnownMarketplaces.Read(cfg);
        Assert.Equal(MarketplacesState.Unreadable, snap.State);
        Assert.Empty(snap.ByName);
    }

    [Fact]
    public void All_three_kinds_are_read_from_their_own_field()
    {
        var cfg = TempConfig();
        Write(cfg, """
            { "flaui-mcp":  { "source": { "source": "github",    "repo": "ckir/flauimcp" } },
              "ecc":        { "source": { "source": "git",       "url":  "https://github.com/a/b.git" } },
              "clavity":    { "source": { "source": "directory", "path": "C:\\Programs\\clavity" } } }
            """);

        var snap = KnownMarketplaces.Read(cfg);

        Assert.Equal(MarketplacesState.Read, snap.State);
        Assert.Equal("ckir/flauimcp", snap.ByName["flaui-mcp"].Source);
        Assert.Equal("github", snap.ByName["flaui-mcp"].Kind);
        Assert.Equal("https://github.com/a/b.git", snap.ByName["ecc"].Source);
        Assert.Equal(@"C:\Programs\clavity", snap.ByName["clavity"].Source);
        Assert.Empty(snap.UnsupportedKinds);
    }

    // A kind this code does not recognise is recorded as ABSENT, never guessed — but it must be VISIBLE,
    // or the next unsupported kind is invisible until a restore silently fails weeks later.
    [Fact]
    public void An_unrecognised_kind_is_omitted_and_surfaced()
    {
        var cfg = TempConfig();
        Write(cfg, """{ "weird": { "source": { "source": "mercurial", "repo": "x" } } }""");

        var snap = KnownMarketplaces.Read(cfg);

        Assert.Equal(MarketplacesState.Read, snap.State);
        Assert.Empty(snap.ByName);
        Assert.Equal("mercurial", Assert.Single(snap.UnsupportedKinds));
    }

    // THE FALSE-NEGATIVE DIRECTION, which is the dangerous one: an unequal compare triggers the
    // "source DIFFERS" branch, which ABORTS a restore that should have proceeded.
    [Theory]
    [InlineData(@"C:\Marketplaces\acme", @"c:\marketplaces\acme")]      // casing
    [InlineData(@"C:\Marketplaces\acme", @"C:/Marketplaces/acme")]      // separator
    [InlineData(@"C:\Marketplaces\acme", @"C:\Marketplaces\acme\")]     // trailing separator
    [InlineData(@"C:\Marketplaces\acme", @"C:\Marketplaces\.\acme")]    // redundant segment
    public void Directory_sources_that_denote_the_same_place_compare_equal(string a, string b)
        => Assert.True(KnownMarketplaces.SameSource(
            new MarketplaceSource("m", "directory", a), new MarketplaceSource("m", "directory", b)));

    [Fact]
    public void Genuinely_different_directory_sources_compare_unequal()
        => Assert.False(KnownMarketplaces.SameSource(
            new MarketplaceSource("m", "directory", @"C:\Marketplaces\acme"),
            new MarketplaceSource("m", "directory", @"C:\Marketplaces\other")));

    // github/git are IDENTIFIERS, not paths — they compare ordinally (case-insensitively), and must NOT
    // be run through path normalisation, which would mangle a URL's forward slashes.
    [Fact]
    public void Git_and_github_sources_compare_without_path_normalisation()
    {
        Assert.True(KnownMarketplaces.SameSource(
            new MarketplaceSource("m", "github", "ckir/flauimcp"),
            new MarketplaceSource("m", "github", "CKIR/FlauiMcp")));
        Assert.False(KnownMarketplaces.SameSource(
            new MarketplaceSource("m", "git", "https://github.com/a/b.git"),
            new MarketplaceSource("m", "git", "https://github.com/a/c.git")));
    }

    [Fact]
    public void A_different_kind_is_never_the_same_source()
        => Assert.False(KnownMarketplaces.SameSource(
            new MarketplaceSource("m", "github", "a/b"), new MarketplaceSource("m", "directory", "a/b")));
}
```

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test --filter "FullyQualifiedName~KnownMarketplacesTests"`
Expected: compile errors — `KnownMarketplaces` and `MarketplacesState` do not exist.

- [ ] **Step 3: Implement the reader**

Create `src/FlaUI.Mcp.Server/Install/KnownMarketplaces.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlaUI.Mcp.Server.Install;

/// <summary>How a read of `known_marketplaces.json` turned out.
///
/// ⚠ FileAbsent and Unreadable are DIFFERENT ANSWERS WITH OPPOSITE ACTIONS, and collapsing them is a
/// defect. FileAbsent means there are no registered marketplaces at all, so a recorded alias is
/// DEFINITIVELY absent and re-adding it is both safe and necessary. Unreadable means the live source is
/// genuinely UNKNOWN, so re-adding blind could overwrite a marketplace the user has repointed.</summary>
public enum MarketplacesState { FileAbsent, Unreadable, Read }

/// <summary>What one read saw. <paramref name="UnsupportedKinds"/> exists so an unrecognised source kind
/// is VISIBLE rather than silently dropped — otherwise the next unsupported kind is invisible until a
/// restore fails weeks later.</summary>
public sealed record MarketplacesSnapshot(
    MarketplacesState State,
    IReadOnlyDictionary<string, MarketplaceSource> ByName,
    IReadOnlyList<string> UnsupportedKinds);

/// <summary>
/// Reads Claude Code's marketplace registry so a restore can compare what it RECORDED against what is
/// LIVE before touching anything.
///
/// MEASURED 2026-08-20 on the operator's machine — 10 marketplaces: 5 `directory`, 4 `github`, 1 `git`.
/// So `github` is the MINORITY kind and `directory` is what our own marketplace uses; a github-only
/// implementation would strand the majority case. `claude plugin marketplace add` takes "a URL, path, or
/// GitHub repo" (measured from its --help), so ONE recorded string serves all three.
///
/// ⚠⚠ NEVER THROWS — this runs inside `uninstall`, which the Windows uninstaller invokes with
/// `Flags: runhidden waituntilterminated` (installer/flaui-mcp.iss:44-47). A malformed, locked or
/// ACL-blocked file that threw would fail the uninstall and trap the user with software they cannot
/// remove. The codebase already states this discipline at CliRouter.cs:577 — "best-effort: the warning
/// channel itself must never throw and abort uninstall". A restore that cannot run is a warning; it is
/// never a failed uninstall.
///
/// ⚠ This is an UNDOCUMENTED internal format that upstream may change. That is accepted rather than
/// solved: the never-throws rule is precisely what makes a schema change degrade into "unreadable"
/// instead of crashing, and the smoke gate records `claude --version` so a green result stays evidence
/// about the CLI behaviour of a known day.
/// </summary>
public static class KnownMarketplaces
{
    public const string FileName = "known_marketplaces.json";

    public static string PathIn(string claudeConfigDir) =>
        Path.Combine(claudeConfigDir, "plugins", FileName);

    private static MarketplacesSnapshot Empty() => new(
        MarketplacesState.FileAbsent,
        new Dictionary<string, MarketplaceSource>(StringComparer.OrdinalIgnoreCase),
        Array.Empty<string>());

    private static MarketplacesSnapshot UnreadableSnapshot() => new(
        MarketplacesState.Unreadable,
        new Dictionary<string, MarketplaceSource>(StringComparer.OrdinalIgnoreCase),
        Array.Empty<string>());

    /// <summary>Never throws. A null or empty config dir reads as FileAbsent — there is no registry to
    /// consult, which is the same answer as "the file is not there".</summary>
    public static MarketplacesSnapshot Read(string? claudeConfigDir)
    {
        if (string.IsNullOrWhiteSpace(claudeConfigDir)) return Empty();

        var byName = new Dictionary<string, MarketplaceSource>(StringComparer.OrdinalIgnoreCase);
        var unsupported = new List<string>();
        try
        {
            var path = PathIn(claudeConfigDir);
            if (!File.Exists(path)) return Empty();
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return UnreadableSnapshot();

            foreach (var (name, node) in root)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (node is not JsonObject entry) continue;
                if (entry["source"] is not JsonObject src) continue;

                var kind = AsString(src["source"]);
                if (string.IsNullOrWhiteSpace(kind)) continue;

                var value = SourceValue(src, kind!);
                if (string.IsNullOrWhiteSpace(value))
                {
                    // Recorded as ABSENT, never guessed — but SURFACED, so the next unsupported kind is
                    // not invisible. A recognised kind with a missing value lands here too, which is
                    // correct: we still have nothing usable to re-add from.
                    unsupported.Add(kind!);
                    continue;
                }
                byName[name] = new MarketplaceSource(name, kind!, value!);
            }
            return new MarketplacesSnapshot(MarketplacesState.Read, byName, unsupported);
        }
        catch
        {
            // Locked, ACL-blocked, or a schema that made a cast throw. "I know nothing" is a usable
            // answer inside a hidden uninstaller; an exception is not.
            return UnreadableSnapshot();
        }
    }

    /// The kind-to-field mapping. MEASURED from the operator's own known_marketplaces.json.
    private static string? SourceValue(JsonObject src, string kind) => kind.ToLowerInvariant() switch
    {
        "github" => AsString(src["repo"]),
        "git" => AsString(src["url"]),
        "directory" => AsString(src["path"]),
        _ => null,
    };

    /// <summary>Do two records denote the same marketplace?
    ///
    /// ⚠ A `directory` source is a PATH, and naive string equality gets it WRONG: `C:\p` vs `c:\p`, `\`
    /// vs `/`, a trailing separator, or a redundant `.\` segment all denote one location while comparing
    /// unequal — and an unequal compare triggers the "source DIFFERS" branch, which ABORTS a restore that
    /// should have proceeded. That is the dangerous direction. `CollisionMarker.SameEntry` already faces
    /// this for projectPath and is the precedent followed here.
    ///
    /// `github` and `git` are IDENTIFIERS, not paths, and compare ordinally — running a URL through path
    /// normalisation would mangle its forward slashes.</summary>
    public static bool SameSource(MarketplaceSource a, MarketplaceSource b)
    {
        if (!string.Equals(a.Kind, b.Kind, StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals(a.Kind, "directory", StringComparison.OrdinalIgnoreCase)
            ? string.Equals(NormalisePath(a.Source), NormalisePath(b.Source), StringComparison.OrdinalIgnoreCase)
            : string.Equals(a.Source, b.Source, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Canonical form of a directory source for comparison ONLY — never for launching anything.
    ///
    /// ⚠ A RELATIVE path is deliberately NOT resolved. `Path.GetFullPath` resolves against the CURRENT
    /// working directory, which during uninstall is Inno's, not the user's — so resolving one would
    /// invent a confident wrong answer instead of leaving an honest textual comparison. MEASURED: all
    /// five `directory` sources in the operator's registry are absolute, so this is not a live case.</summary>
    private static string NormalisePath(string p)
    {
        var t = p.Trim();
        if (t.Length == 0) return t;
        try
        {
            if (Path.IsPathRooted(t)) t = Path.GetFullPath(t);
        }
        catch { /* a hostile recorded value: fall through to the textual form */ }
        return t.Replace('/', '\\').TrimEnd('\\');
    }

    private static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
}
```

- [ ] **Step 4: Run and verify GREEN**

Run: `dotnet test --filter "FullyQualifiedName~KnownMarketplacesTests"` — expected `Failed: 0`.
Run: `dotnet build FlaUI.Mcp.slnx -c Release --no-incremental` — expected `0 Warning(s)` / `0 Error(s)`.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/KnownMarketplaces.cs test/FlaUI.Mcp.Tests/Install/KnownMarketplacesTests.cs
git commit -m "feat(install): read the live marketplace registry, never guess a source

All three source kinds - MEASURED, github is the MINORITY (5 directory, 4
github, 1 git) and directory is what our own marketplace uses. Absent and
unreadable are separate states with opposite actions. Never throws: this runs
inside uninstall, which Inno invokes waituntilterminated, so a malformed file
that threw would trap the user with software they cannot remove.

directory sources compare as normalised PATHS - casing, separators and a
trailing slash all denote one location, and an unequal compare would abort a
restore that should proceed. A relative path is deliberately NOT resolved:
GetFullPath would resolve against Inno's cwd and invent an answer."
```

---

### Task 7: `Apply()` records the marketplace source

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Install/ClaudeCollisionRemedy.cs` (fields, constructor, `Apply`)
- Modify: `src/FlaUI.Mcp.Server/Install/CliRouter.cs:311`
- Test: `test/FlaUI.Mcp.Tests/Install/ClaudeCollisionRemedyTests.cs`

- [ ] **Step 1: Write the failing tests**

Open `test/FlaUI.Mcp.Tests/Install/ClaudeCollisionRemedyTests.cs` first and reuse its existing
fake-runner helper rather than inventing a second one. The tests below assume it is named `FakeCli` with
a `.Run` method and a `ListJson` field, matching `ClaudeCollisionRestoreTests`; if that file names it
differently, adapt these three tests to it. Report `STATE_MISMATCH` only if no such helper exists.

```csharp
    private static string TempClaudeConfigWith(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-claude-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(dir, "plugins"));
        File.WriteAllText(KnownMarketplaces.PathIn(dir), json);
        return dir;
    }

    private const string LiveRegistry = """
        { "flaui-mcp": { "source": { "source": "github", "repo": "ckir/flauimcp" } } }
        """;

    [Fact]
    public void A_disabled_entry_records_the_marketplace_it_came_from()
    {
        var cli = new FakeCli { ListJson = """[ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "enabled": true } ]""" };
        var state = TempState();

        new ClaudeCollisionRemedy(cli.Run, state, claudeConfigDir: TempClaudeConfigWith(LiveRegistry)).Apply();

        var e = Assert.Single(CollisionMarker.Read(state));
        Assert.NotNull(e.Marketplace);
        Assert.Equal("flaui-mcp", e.Marketplace!.Name);
        Assert.Equal("github", e.Marketplace.Kind);
        Assert.Equal("ckir/flauimcp", e.Marketplace.Source);
    }

    // RULE 1: a record written WITHOUT a usable source must not promise a reinstall. The whole defect
    // began as a message that promised more than the code delivered.
    [Fact]
    public void With_no_readable_source_the_promise_is_re_enable_only()
    {
        var cli = new FakeCli { ListJson = """[ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "enabled": true } ]""" };
        var state = TempState();

        // A registry that does not know this alias: nothing to record, and nothing to promise about it.
        var warning = new ClaudeCollisionRemedy(cli.Run, state,
            claudeConfigDir: TempClaudeConfigWith("""{ "someone-else": { "source": { "source": "github", "repo": "a/b" } } }""")).Apply();

        Assert.Null(Assert.Single(CollisionMarker.Read(state)).Marketplace);
        Assert.Contains("re-enabled if you uninstall", warning);
        Assert.DoesNotContain("reinstall", warning, StringComparison.OrdinalIgnoreCase);
    }

    // The alias is DERIVED from the id (`<plugin>@<alias>`), never assumed. An unknown alias records
    // nothing rather than falling back to some other marketplace's source.
    [Fact]
    public void An_alias_absent_from_the_registry_records_no_source()
    {
        var cli = new FakeCli { ListJson = """[ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "enabled": true } ]""" };
        var state = TempState();

        new ClaudeCollisionRemedy(cli.Run, state,
            claudeConfigDir: TempClaudeConfigWith("""{ "other": { "source": { "source": "directory", "path": "C:\\x" } } }""")).Apply();

        Assert.Null(Assert.Single(CollisionMarker.Read(state)).Marketplace);
    }
```

⚠ **Non-vacuity check before you rely on these.** All three depend on the fixture id matching
`ClaudeCollisionRemedy.MarketplaceId` (`"flaui-mcp@flaui-mcp"`) at `:58` — an id that does not match is
filtered out and every assertion over an empty list passes vacuously. After Step 4, confirm the first
test FAILS if you delete the `SourceFor(...)` argument. If it does not, the fixture is not reaching the
code and the tests must be fixed before proceeding.

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test --filter "FullyQualifiedName~ClaudeCollisionRemedyTests"`
Expected: compile error — `ClaudeCollisionRemedy` has no `claudeConfigDir` parameter.

- [ ] **Step 3: Widen the constructor**

Add two fields beside `_dirExists` (`ClaudeCollisionRemedy.cs:36`):

```csharp
    private readonly string? _claudeConfigDir;
    private readonly Func<string, string[], string?, RunResult> _longRun;
```

Replace the constructor and its doc comment (`:38-47`):

```csharp
    /// <param name="dirExists">Injected for testability; defaults to <see cref="Directory.Exists"/>.
    /// A non-user entry whose project directory is gone cannot load the plugin, and `disable` would
    /// have nowhere valid to run from — such entries are skipped.</param>
    /// <param name="claudeConfigDir">Where `plugins/known_marketplaces.json` lives, so a disable can
    /// RECORD the source it came from and a restore can compare it against the live one. Null means
    /// "no registry" — every read then reads as FileAbsent, which is the correct answer for a caller
    /// that has no config dir to offer.</param>
    /// <param name="longRun">Runner for the ONE call that touches the network (`marketplace add` does a
    /// git clone). Defaults to <paramref name="run"/>, which keeps every existing caller and test
    /// unchanged; CliRouter passes a runner with a longer per-call bound.</param>
    public ClaudeCollisionRemedy(Func<string, string[], string?, RunResult> run, string stateDir,
        Func<string, bool>? dirExists = null, string? claudeConfigDir = null,
        Func<string, string[], string?, RunResult>? longRun = null)
    {
        _run = run;
        _stateDir = stateDir;
        _dirExists = dirExists ?? Directory.Exists;
        _claudeConfigDir = claudeConfigDir;
        _longRun = longRun ?? run;
    }
```

- [ ] **Step 4: Record the source in `Apply()`**

Insert after `:62` (`var justDisabled = new List<DisabledEntry>();`):

```csharp
        // Read the marketplace registry ONCE per pass, not per entry: it is the same file for every
        // entry and re-reading it inside the loop would multiply the failure surface for no gain.
        var marketplaces = KnownMarketplaces.Read(_claudeConfigDir);
```

Replace `:66`:

```csharp
            var entry = new DisabledEntry(e.Id, e.Scope, e.ProjectPath);
```

with:

```csharp
            var entry = new DisabledEntry(e.Id, e.Scope, e.ProjectPath, SourceFor(e.Id, marketplaces));
```

Add the helper next to `Where` (`:298`):

```csharp
    /// <summary>The marketplace an installed plugin came from, or null when we cannot know it.
    ///
    /// A plugin id is `&lt;plugin&gt;@&lt;marketplace-alias&gt;`, so the alias is DERIVED from the id rather
    /// than assumed. Null when the id has no alias segment, when the registry could not be read, or when
    /// the alias's source kind is one we do not recognise. ⚠ NEVER GUESSED: an entry with no source
    /// degrades to re-enable-only, and Apply's promise narrows to match (rule 1). Guessing would let a
    /// restore install something the user did not ask for.</summary>
    private static MarketplaceSource? SourceFor(string id, MarketplacesSnapshot marketplaces)
    {
        var at = id.LastIndexOf('@');
        if (at < 0 || at == id.Length - 1) return null;
        var alias = id[(at + 1)..];
        return marketplaces.ByName.TryGetValue(alias, out var src) ? src : null;
    }
```

- [ ] **Step 5: Narrow the promise when no source was recorded (rule 1)**

Replace `ClaudeCollisionRemedy.cs:136-143` with:

```csharp
        if (justDisabled.Count > 0)
        {
            // Only PROMISE a restore if we actually recorded it. If Record failed, recordWarning
            // already tells the user it will NOT be re-enabled — promising the opposite in the same
            // concatenated line is worse than saying nothing. (agy panel round 2.)
            //
            // ⚠ RULE 1, D1: the promise must not overstate what a two-layer restore can do. We can only
            // REINSTALL an evicted copy for entries whose marketplace source we actually captured; for
            // the rest the honest promise is a re-enable. The whole defect began as a message that
            // promised more than the code delivered, and a richer restore makes that easier to repeat.
            var restorable = justDisabled.Count(d => d.Marketplace is not null);
            warnings.Insert(0, recordWarning is not null
                ? $"disabled {justDisabled.Count} conflicting marketplace copy/copies of the driving skill."
                : restorable == justDisabled.Count
                    ? $"disabled {justDisabled.Count} conflicting marketplace copy/copies of the driving skill " +
                      "(they will be re-enabled if you uninstall flaui-mcp, and reinstalled first if anything " +
                      "has removed them)."
                    : $"disabled {justDisabled.Count} conflicting marketplace copy/copies of the driving skill " +
                      "(they will be re-enabled if you uninstall flaui-mcp).");
        }
```

⚠ The middle branch is the ONLY one that says "reinstalled". The `restorable != count` branch keeps the
original wording verbatim, so an entry with no source promises exactly what it can deliver.

- [ ] **Step 6: Wire the config dir through `CliRouter`**

`CliRouter.cs:311` currently:

```csharp
                var remedy = new ClaudeCollisionRemedy(ClaudeRunner(() => sw2.Elapsed, ClaudeBudget), paths.StateDir);
```

becomes:

```csharp
                var remedy = new ClaudeCollisionRemedy(
                    ClaudeRunner(() => sw2.Elapsed, ClaudeBudget), paths.StateDir,
                    claudeConfigDir: paths.ClaudeConfigDir);
```

⚠ The `longRun:` argument is added in Task 9 Step 3, once `ClaudeLongRunner` exists. Keeping this step to
`claudeConfigDir:` alone means the tree stays buildable between commits.

- [ ] **Step 7: Run and verify GREEN**

Run: `dotnet test --filter "FullyQualifiedName~ClaudeCollision|FullyQualifiedName~CliRouter"` — expected `Failed: 0`.
Run: `dotnet build FlaUI.Mcp.slnx -c Release --no-incremental` — expected `0 Warning(s)` / `0 Error(s)`.

- [ ] **Step 8: Prove the new tests are non-vacuous**

Temporarily revert Step 4's one-line change (drop the `SourceFor(...)` argument).
Run: `dotnet test --filter "FullyQualifiedName~A_disabled_entry_records_the_marketplace"`
Expected: that test FAILS on `Assert.NotNull(e.Marketplace)`. Restore the argument and re-run.

- [ ] **Step 9: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/ClaudeCollisionRemedy.cs src/FlaUI.Mcp.Server/Install/CliRouter.cs test/FlaUI.Mcp.Tests/Install/ClaudeCollisionRemedyTests.cs
git commit -m "feat(install): record which marketplace a disabled copy came from

The alias is derived from the plugin id, never assumed, and looked up in the
live registry. An entry whose source cannot be read degrades to
re-enable-only and the install-time promise NARROWS to match: only entries
with a captured source are promised a reinstall. Rule 1 of the spec's three
warning-text rules - this defect began as a message that promised more than
the code delivered."
```

---

### Task 8: The `FutureVersion` messages must name a recourse

The v2 bump is what makes these reachable. Before it, a `FutureVersion` marker was nearly impossible;
after it, **any user who downgrades hits one during UNINSTALL** — the worst possible moment, because they
have just removed the tool, their plugin is still disabled, and today's message tells them nothing they
can act on. Task 5 made the entries available (Correction A). Now use them.

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Install/ClaudeCollisionRemedy.cs:156-158` and `:188-193`
- Modify: `src/FlaUI.Mcp.Server/Install/CollisionMarker.cs:67-70`
- Test: `test/FlaUI.Mcp.Tests/Install/ClaudeCollisionRestoreTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `test/FlaUI.Mcp.Tests/Install/ClaudeCollisionRestoreTests.cs`:

```csharp
    // A message the user cannot act on is the same failure class as the warning that started this whole
    // defect: the tool describing its own state instead of the user's problem. The v2 bump is what makes
    // this reachable - a downgraded build hits it during UNINSTALL, with the plugin still disabled.
    [Fact]
    public void A_future_version_marker_names_every_disabled_id_and_its_enable_command()
    {
        var s = TempState();
        File.WriteAllText(CollisionMarker.PathIn(s), """
            { "version": 3,
              "disabled": [ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "projectPath": null },
                            { "id": "other@mkt", "scope": "local", "projectPath": "C:\\Proj" } ] }
            """);

        var warning = new ClaudeCollisionRemedy(new FakeCli().Run, s, _ => true).Restore();

        Assert.NotNull(warning);
        Assert.Contains("claude plugin enable flaui-mcp@flaui-mcp --scope user", warning);
        Assert.Contains("claude plugin enable other@mkt --scope local", warning);
        Assert.Contains(@"C:\Proj", warning);                       // where to run the local one FROM
        Assert.Contains("newer flaui-mcp", warning);                // still says WHAT happened
        Assert.True(File.Exists(CollisionMarker.PathIn(s)), "a future-version marker must be left in place");
    }
```

- [ ] **Step 2: Run and watch it fail**

Run: `dotnet test --filter "FullyQualifiedName~A_future_version_marker_names_every"`
Expected: FAIL — the message contains none of the `enable` commands.

- [ ] **Step 3: Rewrite the `Restore()` message**

Replace `ClaudeCollisionRemedy.cs:156-158`:

```csharp
            if (state == MarkerState.FutureVersion)
                // ⚠ THIS MESSAGE IS LOAD-BEARING, and the v2 marker bump is what made it so. Before that
                // it was nearly unreachable; now any user who downgrades hits it during UNINSTALL — the
                // worst moment, because they have just removed the tool and their plugin is still
                // disabled. Explaining WHAT happened without saying what to DO is the same failure class
                // as the warning that started this defect: the tool describing its own state instead of
                // the user's problem. So name the ids and the exact command for each.
                return $"the restore record at {CollisionMarker.PathIn(_stateDir)} was written by a newer " +
                       "flaui-mcp, so it was left in place and not acted on — the plugin(s) below are still " +
                       "DISABLED. " + ManualEnableRecourse(recorded) +
                       " Reinstalling the newer flaui-mcp and uninstalling it again would also do this.";
```

Add the helper beside `Where` (`:298`):

```csharp
    /// <summary>The exact commands a human must run to undo our disable themselves.
    ///
    /// Entries whose project directory is gone are OMITTED, not listed with an impossible "run it from
    /// &lt;deleted path&gt;" — the same bad-recourse defect the in-loop guard fixes. An empty result says so
    /// rather than printing an empty list. Shared by the FutureVersion path and the
    /// inventory-unreadable path so the two recourses can never drift.</summary>
    private string ManualEnableRecourse(IReadOnlyList<DisabledEntry> entries)
    {
        var recoverable = entries.Where(e => e.ProjectPath is null || _dirExists(e.ProjectPath)).ToList();
        if (recoverable.Count == 0) return "No manual action is possible (the recorded projects no longer exist).";
        return "To re-enable manually: " + string.Join("; ", recoverable.Select(e =>
            $"claude plugin enable {e.Id} --scope {e.Scope}" +
            (e.ProjectPath is null ? "" : $" (run from {e.ProjectPath})"))) + ".";
    }
```

- [ ] **Step 4: De-duplicate the inventory-unreadable recourse**

`:188-193` builds the same list inline. Replace those six lines with:

```csharp
                var recourse = ManualEnableRecourse(recorded);
```

Run the existing test for that path. If it pinned the old text and the only difference is the leading
"To re-enable manually:" wording or a trailing full stop, update the expectation — the two messages
sharing one builder is the point. **If the difference is anything more than wording, STOP** — you have
changed behaviour, not phrasing.

- [ ] **Step 5: Update `Record()`'s refusal message the same way**

`CollisionMarker.cs:67-70` refuses to touch a `FutureVersion` marker at INSTALL time. It names the file
but not the recourse. Replace with:

```csharp
        if (state == MarkerState.FutureVersion)
            return $"the restore record at {PathIn(stateDir)} was written by a newer flaui-mcp and was " +
                   "left unchanged; this install's disable was NOT recorded, so uninstalling flaui-mcp " +
                   "will not re-enable it automatically. If you did not expect this, remove " +
                   $"{PathIn(stateDir)} and run `flaui-mcp install --agent claude` again.";
```

- [ ] **Step 6: Run and verify GREEN**

Run: `dotnet test --filter "FullyQualifiedName~ClaudeCollisionRestoreTests|FullyQualifiedName~CollisionMarkerTests"`
Expected: `Failed: 0`. If an existing test pinned the old message text, update that expectation — the
message is the behaviour under change, and the spec makes it a requirement, not polish.

Run: `dotnet build FlaUI.Mcp.slnx -c Release --no-incremental` — expected `0 Warning(s)` / `0 Error(s)`.

- [ ] **Step 7: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/ClaudeCollisionRemedy.cs src/FlaUI.Mcp.Server/Install/CollisionMarker.cs test/FlaUI.Mcp.Tests/Install/ClaudeCollisionRestoreTests.cs
git commit -m "fix(install): the future-version messages must name a recourse

The v2 bump is what makes these reachable: a downgraded build now hits one
during UNINSTALL, having just removed the tool while the plugin is still
disabled. Explaining what happened without saying what to do is the same
failure class as the warning that started this defect. Both messages now name
the still-disabled ids and the exact enable command for each, sharing one
recourse builder so they cannot drift."
```

---

### Task 9: `Restore()` rebuilds an evicted copy (D1 fix (b))

Layer (b) is defence in depth. Fix (a) removes the only KNOWN path that destroys the copy, so in the
primary scenario this code never runs — that is the design, and it is exactly why Task 11's smoke gate
must FORCE its precondition or it ships unexercised.

⚠⚠ **NOTHING HERE MAY ABORT AN UNINSTALL.** Inno runs `flaui-mcp.exe uninstall` with
`Flags: runhidden waituntilterminated` (`installer/flaui-mcp.iss:44-47`). Every new call is bounded and
every new read is non-throwing. A restore that cannot finish must never trap a user with software they
cannot remove.

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Install/ClaudeCollisionRemedy.cs` (`Restore`'s loop, `TryReinstall`)
- Modify: `src/FlaUI.Mcp.Server/Install/CliRouter.cs`
- Create: `test/FlaUI.Mcp.Tests/Install/ClaudeCollisionReinstallTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `test/FlaUI.Mcp.Tests/Install/ClaudeCollisionReinstallTests.cs`:

```csharp
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

/// <summary>D1 layer (b): when the recorded copy is no longer installed, rebuild it from the recorded
/// marketplace rather than reporting a dead end. Fix (a) removes the only KNOWN evictor, so this path
/// exists for evictions not yet foreseen — a future registrar change, a `claude` CLI behaviour shift, or
/// the user's own tooling.</summary>
public class ClaudeCollisionReinstallTests
{
    private static string TempState()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-state-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string TempClaudeConfig(string? registryJson)
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-claude-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(dir, "plugins"));
        if (registryJson is not null) File.WriteAllText(KnownMarketplaces.PathIn(dir), registryJson);
        return dir;
    }

    private sealed class FakeCli
    {
        public string ListJson = "[]";
        public readonly Dictionary<string, int> CodeFor = new();   // "plugin <verb> <target>" -> exit code
        public readonly List<string[]> Calls = new();

        public RunResult Run(string file, string[] args, string? cwd)
        {
            if (args.Length >= 2 && args[0] == "plugin" && args[1] == "list")
                return new RunResult(0, ListJson);
            Calls.Add(args);
            var key = string.Join(" ", args.Take(3));
            return new RunResult(CodeFor.TryGetValue(key, out var c) ? c : 0, "");
        }
    }

    private static readonly MarketplaceSource Src = new("flaui-mcp", "github", "ckir/flauimcp");
    private static readonly DisabledEntry Recorded = new("flaui-mcp@flaui-mcp", "user", null, Src);

    private const string RegistryWithAlias = """
        { "flaui-mcp": { "source": { "source": "github", "repo": "ckir/flauimcp" } } }
        """;

    // THE HAPPY PATH FOR (b): the copy was evicted and the marketplace is gone too, so all three steps
    // run - re-add, reinstall, enable.
    [Fact]
    public void An_evicted_copy_is_re_added_reinstalled_and_enabled()
    {
        var cli = new FakeCli { ListJson = "[]" };          // the recorded copy is NOT installed
        var s = TempState();
        CollisionMarker.Record(s, new[] { Recorded });

        var warning = new ClaudeCollisionRemedy(cli.Run, s, _ => true,
            claudeConfigDir: TempClaudeConfig(null)).Restore();   // registry file absent => alias absent

        Assert.Equal(new[] { "plugin", "marketplace", "add", "ckir/flauimcp" }, cli.Calls[0]);
        Assert.Equal(new[] { "plugin", "install", "flaui-mcp@flaui-mcp", "--scope", "user" }, cli.Calls[1]);
        Assert.Equal(new[] { "plugin", "enable", "flaui-mcp@flaui-mcp", "--scope", "user" }, cli.Calls[2]);
        // RULE 2: the message must say WHICH steps ran. "reinstalled from X" and "restored" are
        // different facts and an operator debugging a lost plugin needs to know which happened.
        Assert.Contains("reinstalled", warning!, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ckir/flauimcp", warning);
    }

    // The alias is present AND is what we recorded: skip the re-add, but still reinstall and enable.
    [Fact]
    public void A_matching_live_alias_skips_the_re_add()
    {
        var cli = new FakeCli { ListJson = "[]" };
        var s = TempState();
        CollisionMarker.Record(s, new[] { Recorded });

        new ClaudeCollisionRemedy(cli.Run, s, _ => true,
            claudeConfigDir: TempClaudeConfig(RegistryWithAlias)).Restore();

        Assert.DoesNotContain(cli.Calls, c => c.Contains("marketplace"));
        Assert.Equal(new[] { "plugin", "install", "flaui-mcp@flaui-mcp", "--scope", "user" }, cli.Calls[0]);
    }

    // ⚠ THE BRANCH THAT PROTECTS THE USER FROM A SILENTLY WRONG INSTALL. "Present by alias" is not "is
    // the thing we recorded": the alias is a local name the user controls. If they repointed it, a blind
    // reinstall would install whatever now sits behind that name.
    [Fact]
    public void A_repointed_alias_refuses_to_install_and_does_not_touch_the_marketplace()
    {
        var cli = new FakeCli { ListJson = "[]" };
        var s = TempState();
        CollisionMarker.Record(s, new[] { Recorded });

        var warning = new ClaudeCollisionRemedy(cli.Run, s, _ => true,
            claudeConfigDir: TempClaudeConfig("""
                { "flaui-mcp": { "source": { "source": "github", "repo": "someone-else/fork" } } }
                """)).Restore();

        Assert.Empty(cli.Calls);                               // no add, no install, no enable
        Assert.Contains("someone-else/fork", warning!);        // BOTH sources named
        Assert.Contains("ckir/flauimcp", warning);
    }

    // ⚠ ONE ENTRY'S PROBLEM IS NEVER ANOTHER ENTRY'S. Reading "stop" as `return` inside Restore's foreach
    // would strand every LATER entry because one alias mismatched.
    [Fact]
    public void A_repointed_alias_skips_only_that_entry()
    {
        var cli = new FakeCli { ListJson = "[]" };
        var s = TempState();
        CollisionMarker.Record(s, new[]
        {
            Recorded,
            new DisabledEntry("other@mkt", "user", null, new MarketplaceSource("mkt", "github", "a/b")),
        });

        new ClaudeCollisionRemedy(cli.Run, s, _ => true,
            claudeConfigDir: TempClaudeConfig("""
                { "flaui-mcp": { "source": { "source": "github", "repo": "someone-else/fork" } },
                  "mkt":       { "source": { "source": "github", "repo": "a/b" } } }
                """)).Restore();

        // The SECOND entry was still restored despite the first entry's mismatch.
        Assert.Contains(cli.Calls, c => c.SequenceEqual(new[] { "plugin", "install", "other@mkt", "--scope", "user" }));
    }

    // THE FALSE-NEGATIVE DIRECTION for a `directory` source: a path differing only in casing or a
    // trailing separator is the SAME marketplace, and treating it as different would ABORT a restore
    // that should proceed.
    [Fact]
    public void A_directory_alias_differing_only_in_casing_is_the_same_marketplace()
    {
        var cli = new FakeCli { ListJson = "[]" };
        var s = TempState();
        CollisionMarker.Record(s, new[]
        {
            new DisabledEntry("acme@acme", "user", null,
                new MarketplaceSource("acme", "directory", @"C:\Marketplaces\acme")),
        });

        new ClaudeCollisionRemedy(cli.Run, s, _ => true,
            claudeConfigDir: TempClaudeConfig("""
                { "acme": { "source": { "source": "directory", "path": "c:\\marketplaces\\acme\\" } } }
                """)).Restore();

        Assert.DoesNotContain(cli.Calls, c => c.Contains("marketplace"));   // matched: no re-add
        Assert.Contains(cli.Calls, c => c.Contains("install"));             // and it PROCEEDED
    }

    // No source recorded => a dead end, and the message must READ as one. Never guess a source.
    [Fact]
    public void An_evicted_copy_with_no_recorded_source_is_an_honest_dead_end()
    {
        var cli = new FakeCli { ListJson = "[]" };
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "user", null) });

        var warning = new ClaudeCollisionRemedy(cli.Run, s, _ => true,
            claudeConfigDir: TempClaudeConfig(RegistryWithAlias)).Restore();

        Assert.Empty(cli.Calls);
        Assert.Contains("no marketplace source was recorded", warning!);
    }

    // RULE 3: a partial restore reports as a FAILURE with the remaining commands, never as a success.
    [Fact]
    public void A_re_added_marketplace_with_a_failed_reinstall_reports_a_failure()
    {
        var cli = new FakeCli { ListJson = "[]" };
        cli.CodeFor["plugin install flaui-mcp@flaui-mcp"] = 1;
        var s = TempState();
        CollisionMarker.Record(s, new[] { Recorded });

        var warning = new ClaudeCollisionRemedy(cli.Run, s, _ => true,
            claudeConfigDir: TempClaudeConfig(null)).Restore();

        Assert.Contains("could NOT reinstall", warning!);
        Assert.Contains("claude plugin install flaui-mcp@flaui-mcp --scope user", warning);
        Assert.DoesNotContain(cli.Calls, c => c.Contains("enable"));   // never enable a phantom
    }

    // ⚠⚠ THE UNINSTALL-SAFETY TEST (in-process half). Malformed registry JSON must degrade to a warning,
    // never an exception - Inno runs uninstall waituntilterminated. MUTANT: neuter the catch in
    // KnownMarketplaces.Read and this goes RED.
    [Fact]
    public void Malformed_registry_json_degrades_to_a_warning_and_never_throws()
    {
        var cli = new FakeCli { ListJson = "[]" };
        var s = TempState();
        CollisionMarker.Record(s, new[] { Recorded });

        var warning = new ClaudeCollisionRemedy(cli.Run, s, _ => true,
            claudeConfigDir: TempClaudeConfig("{ not json at all")).Restore();

        Assert.NotNull(warning);
        Assert.Contains("could not be read", warning!);
        Assert.Contains(KnownMarketplaces.FileName, warning);
        Assert.Empty(cli.Calls);          // an UNKNOWN live source must not be overwritten blind
    }
}
```

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test --filter "FullyQualifiedName~ClaudeCollisionReinstallTests"`
Expected: most FAIL — the "no longer installed" branch still only warns.

- [ ] **Step 3: Add the bounded long-call runner in `CliRouter`**

`marketplace add` performs a `git clone`, over SSH for a `github` source. `ProcessRunner.DefaultTimeout`
is 30s, tuned for local IPC, and would routinely kill a legitimate clone. Give that ONE call its own
bound rather than raising the ceiling for every call.

Add beside `ClaudeBudget` (`CliRouter.cs:338`):

```csharp
    /// The per-call bound for `claude plugin marketplace add`, the ONE claude call that touches the
    /// network: it performs a git clone, which DefaultTimeout (30s, tuned for local IPC) would routinely
    /// kill. Still BOUNDED and still inside ClaudeBudget — a slow clone eats the pass budget and later
    /// entries degrade to TimedOut warnings, which is the correct behaviour. An uninstall must always
    /// finish.
    internal static readonly TimeSpan MarketplaceAddTimeout = TimeSpan.FromSeconds(90);
```

Replace `BudgetedTimeout` (`:342-348`) with the cap overload, keeping the existing two-argument signature
so `ClaudeBudgetTests` stays green:

```csharp
    /// The timeout to hand the next claude call given the elapsed pass time, or null when the budget is
    /// spent (the caller short-circuits to TimedOut without launching a process). Pure — unit-tested.
    internal static TimeSpan? BudgetedTimeout(TimeSpan budget, TimeSpan elapsed) =>
        BudgetedTimeout(budget, elapsed, ProcessRunner.DefaultTimeout);

    /// As above, with an explicit per-call cap — the network-bound `marketplace add` needs a longer one
    /// than the local-IPC default, but must still never exceed the pass budget.
    internal static TimeSpan? BudgetedTimeout(TimeSpan budget, TimeSpan elapsed, TimeSpan cap)
    {
        var remaining = budget - elapsed;
        if (remaining <= TimeSpan.Zero) return null;
        return remaining < cap ? remaining : cap;
    }
```

Add `ClaudeLongRunner` immediately after `ClaudeRunner` (which ends at `:381`):

```csharp
    /// ClaudeRunner with the longer per-call cap, for the network-bound `marketplace add`. It delegates
    /// to ClaudeRunner whenever a test seam is active, so a fake CLI behaves identically on both runners
    /// — only the real path's deadline differs.
    private static Func<string, string[], string?, RunResult> ClaudeLongRunner(Func<TimeSpan> elapsed, TimeSpan budget)
    {
        if (Environment.GetEnvironmentVariable("FLAUI_MCP_FAKE_CLAUDE_MISSING") == "1"
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLAUI_MCP_FAKE_CLAUDE_COLLISION"))
            || Environment.GetEnvironmentVariable("FLAUI_MCP_FAKE_CLAUDE_PRESENT") == "1")
            return ClaudeRunner(elapsed, budget);

        return (file, args, cwd) =>
        {
            var timeout = BudgetedTimeout(budget, elapsed(), MarketplaceAddTimeout);
            if (timeout is null) return new RunResult(ProcessRunner.TimedOut, "");
            return ProcessRunner.Run(file, args, cwd, timeout.Value);
        };
    }
```

Then complete Task 7 Step 6 by adding the `longRun:` argument at `CliRouter.cs:311`:

```csharp
                var remedy = new ClaudeCollisionRemedy(
                    ClaudeRunner(() => sw2.Elapsed, ClaudeBudget), paths.StateDir,
                    claudeConfigDir: paths.ClaudeConfigDir,
                    longRun: ClaudeLongRunner(() => sw2.Elapsed, ClaudeBudget));
```

- [ ] **Step 4: Implement the three-step restore**

Insert immediately after `ClaudeCollisionRemedy.cs:198`
(`var present = ClaudePluginInventory.Matching(entries, MarketplaceId);`):

```csharp
            // Read ONCE per pass. Every entry consults the same file, and re-reading it per entry would
            // multiply the failure surface of a read that runs inside uninstall for no gain.
            var marketplaces = KnownMarketplaces.Read(_claudeConfigDir);
```

Replace the "no longer installed" branch (`:218-222`):

```csharp
                if (!present.Any(p => Same(p, e)))
                {
                    warnings.Add($"{e.Id} ({Where(e)}) is no longer installed, so it was not re-enabled.");
                    continue;
                }
```

with:

```csharp
                if (!present.Any(p => Same(p, e)))
                {
                    // LAYER (b): something evicted the copy. Rebuild it from the marketplace we recorded
                    // rather than reporting a dead end — but only what we RECORDED, never a guess.
                    if (!TryReinstall(e, marketplaces, warnings)) continue;
                    // Reinstalled: fall through to the enable below, which is what actually undoes our
                    // disable. `install` does not imply the entry is enabled at our recorded scope.
                }
```

Add `TryReinstall` beside `ManualEnableRecourse`:

```csharp
    /// <summary>Rebuild an evicted copy from its recorded marketplace: re-add the marketplace if the
    /// alias is absent, then reinstall. Returns true when the caller should go on to ENABLE it, false
    /// when this entry is done (a warning has been recorded either way).
    ///
    /// ⚠ EVERY false return is a `continue` at the call site, NEVER a `return`/`break`. One entry's
    /// problem is never another entry's — reading "stop" as `return` would strand every LATER entry
    /// because one alias mismatched.
    ///
    /// ⚠ NOTHING HERE MAY THROW. It runs inside `uninstall`, which Inno invokes with
    /// `waituntilterminated` (installer/flaui-mcp.iss:44-47). KnownMarketplaces.Read never throws and
    /// every CLI call goes through a bounded runner, so a hang or a hostile file becomes a warning.</summary>
    private bool TryReinstall(DisabledEntry e, MarketplacesSnapshot marketplaces, List<string> warnings)
    {
        var manual = $"claude plugin install {e.Id} --scope {e.Scope}" +
                     (e.ProjectPath is null ? "" : $" (run it from {e.ProjectPath})");

        if (e.Marketplace is not { } mkt)
        {
            // ⚠ AN HONEST DEAD END, not a graceful degrade. The plugin is GONE and this path cannot
            // bring it back, so the message must read as one. Guessing a source would install something
            // the user did not ask for, which is strictly worse. The honest failure is the feature.
            warnings.Add($"{e.Id} ({Where(e)}) is no longer installed and no marketplace source was " +
                         "recorded for it, so it could NOT be restored. To put it back yourself, re-add " +
                         $"the marketplace it came from and run: {manual}.");
            return false;
        }

        if (marketplaces.State == MarketplacesState.Unreadable)
        {
            // "Cannot read" is NOT "not there". The live source is genuinely UNKNOWN, and re-adding
            // blind could overwrite a marketplace the user has repointed.
            warnings.Add($"{e.Id} ({Where(e)}) is no longer installed, and the marketplace registry " +
                         $"({KnownMarketplaces.FileName}) could not be read, so we did not risk changing " +
                         $"it. To restore it yourself: claude plugin marketplace add {mkt.Source} then {manual}.");
            return false;
        }

        // FileAbsent means there are NO registered marketplaces, so the alias is DEFINITIVELY absent and
        // re-adding is both safe and necessary. Only a successfully READ file can say an alias is live.
        var live = marketplaces.State == MarketplacesState.Read
                   && marketplaces.ByName.TryGetValue(mkt.Name, out var l) ? l : null;

        if (live is not null && !KnownMarketplaces.SameSource(live, mkt))
        {
            // ⚠ "PRESENT BY ALIAS" IS NOT "IS THE THING WE RECORDED". The alias is a local name the USER
            // controls. If they repointed it, installing would silently deliver whatever now sits behind
            // that name. The user's own configuration outranks our restore — for THIS entry.
            warnings.Add($"{e.Id} ({Where(e)}) is no longer installed, and the marketplace `{mkt.Name}` " +
                         $"now points at {live.Kind} `{live.Source}` instead of the {mkt.Kind} " +
                         $"`{mkt.Source}` we recorded — so it was NOT reinstalled and your marketplace " +
                         $"was NOT changed. To restore it yourself once `{mkt.Name}` points where you " +
                         $"expect: {manual}.");
            return false;
        }

        if (live is null)
        {
            var add = _longRun("claude", new[] { "plugin", "marketplace", "add", mkt.Source }, e.ProjectPath);
            if (add.Code != 0)
            {
                warnings.Add($"could not re-add the marketplace `{mkt.Name}` for {e.Id} ({Where(e)}): " +
                             $"claude {DescribeExit(add.Code)}. To restore it yourself: claude plugin " +
                             $"marketplace add {mkt.Source} then {manual}.");
                return false;
            }
        }

        var reinstall = _run("claude", new[] { "plugin", "install", e.Id, "--scope", e.Scope }, e.ProjectPath);
        if (reinstall.Code != 0)
        {
            // ⚠ RULE 3: a PARTIAL restore reports as a FAILURE with the remaining commands, never as a
            // success. We may have just re-added a marketplace and left the plugin missing.
            warnings.Add((live is null ? $"re-added the marketplace `{mkt.Name}` but " : "") +
                         $"could NOT reinstall {e.Id} ({Where(e)}): claude {DescribeExit(reinstall.Code)}. " +
                         $"To finish: {manual}.");
            return false;
        }

        // ⚠ RULE 2: say WHICH steps ran. "Restored" when only an enable was needed and "reinstalled from
        // <marketplace>" when the copy was rebuilt are different facts, and an operator debugging a lost
        // plugin needs to know which happened.
        warnings.Add($"{e.Id} ({Where(e)}) was no longer installed, so it was reinstalled from " +
                     $"`{mkt.Name}` ({mkt.Kind}: {mkt.Source})" +
                     (live is null ? ", after re-adding that marketplace." : "."));
        return true;
    }
```

- [ ] **Step 5: Run and verify GREEN**

Run: `dotnet test --filter "FullyQualifiedName~ClaudeCollision"` — expected `Failed: 0` across all three
collision test classes.

- [ ] **Step 6: MUTANT PROOF for the never-throws requirement**

In `KnownMarketplaces.Read`, temporarily narrow the outer `catch` so a `JsonException` escapes: change
`catch` to `catch (System.OverflowException)`, which the malformed-JSON path never throws.

Run: `dotnet test --filter "FullyQualifiedName~Malformed_registry_json_degrades"`
Expected: **that specific test FAILS** with a `JsonException` escaping.

Restore the bare `catch`. Re-run — expected PASS.

⚠ Confirm it is that named test that went red, not merely that the run returned non-zero.

- [ ] **Step 7: Full build + headless suite**

Run: `dotnet build FlaUI.Mcp.slnx -c Release --no-incremental` — expected `0 Warning(s)` / `0 Error(s)`.
Run: `dotnet test --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"` —
expected `Failed: 0, Skipped: 0`.

- [ ] **Step 8: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/ClaudeCollisionRemedy.cs src/FlaUI.Mcp.Server/Install/CliRouter.cs test/FlaUI.Mcp.Tests/Install/ClaudeCollisionReinstallTests.cs
git commit -m "feat(install): restore rebuilds an evicted copy from its marketplace

Layer (b), defence in depth. Fix (a) removes the only KNOWN evictor, so this
runs only for evictions not yet foreseen - which is why the smoke gate must
force its precondition or it ships unexercised.

Re-add the marketplace if the alias is absent, reinstall, enable. A live alias
whose SOURCE differs from what we recorded is refused, not overwritten: the
alias is a local name the user controls, and installing blind would deliver
whatever now sits behind it. That refusal skips THAT ENTRY ONLY.

marketplace add gets a 90s bound because it does a git clone; 30s was tuned
for local IPC. Still inside the pass budget - an uninstall must always finish.

MUTANT PROVEN: narrowing the catch in KnownMarketplaces.Read turns
Malformed_registry_json_degrades_to_a_warning_and_never_throws red."
```

---

### Task 10: Out-of-process uninstall exit-code pin

⚠ **Read Correction B at the top of this plan before writing this test.** It is an end-to-end boundary
pin over an existing structural guarantee (`Isolate` + `Report`), **not** a proof of the new exception
handling — Task 9 Step 6's mutant is what proves that. Record it as what it is.

It is still worth having: it is the only test that exercises the real Inno invocation path
(`flaui-mcp.exe uninstall` as a PROCESS), and it would catch a future change that made `Report` propagate
a failure into the exit code.

**Files:**
- Create: `test/FlaUI.Mcp.Tests/Install/UninstallProcessBoundaryTests.cs`

- [ ] **Step 1: Write the test**

Create `test/FlaUI.Mcp.Tests/Install/UninstallProcessBoundaryTests.cs`:

```csharp
using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

/// <summary>THE UNINSTALL-MUST-ALWAYS-FINISH PIN, at the boundary that actually matters.
///
/// Inno runs `flaui-mcp.exe uninstall` as a PROCESS with `Flags: runhidden waituntilterminated`
/// (installer/flaui-mcp.iss:44-47), so what the user experiences is the OS EXIT CODE, not whether
/// Restore() returned. A user must always be able to remove the software.
///
/// ⚠ HONEST SCOPE, so nobody records this as proving more than it does: CliRouter.Isolate
/// (CliRouter.cs:387-391) already converts ANY throw into a Failed result, and Report deliberately never
/// exits non-zero for a partial failure. So this test passes with or without the never-throws handling
/// in KnownMarketplaces. It pins the END-TO-END boundary and would catch a future change that let a
/// failure reach the exit code; the proof that the new read degrades gracefully is the in-process mutant
/// on Malformed_registry_json_degrades_to_a_warning_and_never_throws.
///
/// Spawns a process; Category=Desktop, matching StdioProtocolCleanlinessTests.</summary>
[Trait("Category", "Desktop")]
public class UninstallProcessBoundaryTests
{
    [Fact]
    public void Uninstall_exits_zero_even_with_a_malformed_marketplace_registry()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "flaui-uninstall-" + Path.GetRandomFileName());
        var claude = Path.Combine(sandbox, "claude");
        var state = Path.Combine(sandbox, "state");
        Directory.CreateDirectory(Path.Combine(claude, "plugins"));
        Directory.CreateDirectory(state);

        try
        {
            // DELIBERATELY MALFORMED, at the exact path the restore reads.
            File.WriteAllText(Path.Combine(claude, "plugins", "known_marketplaces.json"), "{ not json at all");

            // A v2 marker recording a copy that the (faked) inventory will NOT list, so Restore takes the
            // reinstall branch and therefore READS the malformed file. Without this the test would exit
            // 0 without ever reaching the code under test.
            File.WriteAllText(Path.Combine(state, "disabled-plugins.json"), """
                { "version": 2,
                  "disabled": [ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "projectPath": null,
                                  "marketplace": { "name": "flaui-mcp", "kind": "github", "source": "ckir/flauimcp" } } ] }
                """);

            var psi = new ProcessStartInfo(LocateExe())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("uninstall");
            psi.ArgumentList.Add("--agent");
            psi.ArgumentList.Add("claude");
            psi.Environment["CLAUDE_CONFIG_DIR"] = claude;
            psi.Environment["FLAUI_MCP_CLAUDE_CONFIG_DIR"] = claude;
            psi.Environment["FLAUI_MCP_STATE_DIR"] = state;
            psi.Environment["FLAUI_MCP_DATA_DIR"] = Path.Combine(sandbox, "data");
            psi.Environment["FLAUI_MCP_AGY_PLUGINS_DIR"] = Path.Combine(sandbox, "agy");
            psi.Environment["FLAUI_MCP_STAGING_DIR"] = Path.Combine(sandbox, "staging");
            // Fake a claude that lists ONE unrelated plugin: the recorded id is therefore "no longer
            // installed" and Restore enters the reinstall branch.
            psi.Environment["FLAUI_MCP_FAKE_CLAUDE_COLLISION"] = "something-else@elsewhere";

            using var p = Process.Start(psi) ?? throw new Xunit.Sdk.XunitException("could not start the exe");
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            Assert.True(p.WaitForExit(60_000), "uninstall did not finish within 60s — a hang traps the user");

            Assert.True(p.ExitCode == 0,
                "uninstall must ALWAYS exit 0 — Inno runs it waituntilterminated and a non-zero exit " +
                $"traps the user with software they cannot remove. Exit {p.ExitCode}.\n" +
                $"stdout:\n{stdout}\nstderr:\n{stderr}");

            // NON-VACUITY: prove the run actually REACHED the malformed read. Without this the test
            // could pass having exited 0 for entirely unrelated reasons.
            Assert.Contains("known_marketplaces.json", stdout);
        }
        finally
        {
            try { Directory.Delete(sandbox, recursive: true); } catch { }
        }
    }

    private static string LocateExe()
    {
        var baseDir = AppContext.BaseDirectory;
        var config = baseDir.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase) ? "Debug" : "Release";
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FlaUI.Mcp.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not locate the repo root (FlaUI.Mcp.slnx) from the test assembly.");
        var exe = Path.Combine(dir!.FullName, "src", "FlaUI.Mcp.Server", "bin", config,
            "net10.0-windows10.0.19041.0", "win-x64", "flaui-mcp.exe");
        Assert.True(File.Exists(exe), $"exe not found at {exe} — build FlaUI.Mcp.Server first.");
        return exe;
    }
}
```

- [ ] **Step 2: Run it**

Run: `dotnet test --filter "FullyQualifiedName~UninstallProcessBoundaryTests"`
Expected: PASS.

⚠ If the exit-code assertion FAILS, that is a real finding — report the stdout/stderr it prints rather
than weakening the assertion.

⚠ If the `Assert.Contains("known_marketplaces.json", stdout)` assertion fails, the run never reached the
code under test: the fake-CLI seam or the marker is not producing the reinstall branch. **Fix the
fixture; do not delete the assertion.** A green that proves nothing is the exact failure class this
project keeps finding.

- [ ] **Step 3: Commit**

```bash
git add test/FlaUI.Mcp.Tests/Install/UninstallProcessBoundaryTests.cs
git commit -m "test(install): pin that uninstall exits 0 with a malformed registry

Inno runs flaui-mcp.exe uninstall waituntilterminated, so what the user
experiences is the OS exit code. An in-process Restore() test bypasses the
entire boundary.

Honest scope, recorded rather than oversold: Isolate already converts any
throw into a Failed result and Report never exits non-zero, so this passes
with or without the never-throws handling. It pins the end-to-end boundary;
the in-process mutant is what proves the new read degrades gracefully."
```

---

### Task 11: Smoke gate — force layer (b)'s precondition

⚠⚠ **THE TWO EXISTING COLLISION CHECKS GO GREEN UNDER FIX (a) ALONE.** Once the sweep is qualified,
nothing evicts the copy, so `marketplace copy is DISABLED after install` and `RESTORED after uninstall`
both pass with the reinstall fallback **entirely unimplemented**. Treating a green smoke as proof that
both layers work is exactly the false-GREEN this project keeps finding.

**Files:**
- Modify: `scripts/install-smoke.ps1`
- Create: `docs/superpowers/plans/2026-08-20-release-tooling-smoke.md`

- [ ] **Step 1: STATE VERIFICATION**

Run: `grep -n "DISABLED after install\|RESTORED after uninstall\|the marker was consumed" scripts/install-smoke.ps1`
Expected: lines `131`, `135`, `136`. If they moved, adapt; if they are gone, STOP and report
`STATE_MISMATCH`.

⚠ **These two checks are THE ORACLE. Do not adjust them to fit the implementation.**

- [ ] **Step 2: Record the CLI version — a green result is only evidence about a known day**

Insert immediately after line 113 (`Push-Location $outside2`) and the opening `try {`:

```powershell
    # ⚠ THE ONLY GATE FOR THIS SUBPROJECT DEPENDS ON A THIRD-PARTY CLI, A GITHUB CLONE AND THE NETWORK.
    # It cannot run offline, cannot run in CI, and breaks if ckir/flauimcp is renamed or made private.
    # That is accepted rather than solved - a local fake marketplace would test a fake instead of the real
    # CLI behaviour, and the CLI's real behaviour is precisely what this defect turned on. The consequence
    # is that a green result is only evidence about the CLI of a particular day, so the version goes in
    # the recorded output.
    Write-Host "claude CLI: $(claude --version 2>&1)" -ForegroundColor DarkGray
```

- [ ] **Step 3: Add the forcing reinstall check**

Insert after line 136 (`Check 'the marker was consumed' ...`), still inside the `else` block:

```powershell
        # ⚠⚠ THE FORCING CHECK FOR LAYER (b). Everything above passes under fix (a) ALONE - the qualified
        # sweep means nothing evicts the copy, so the reinstall fallback would ship completely
        # unexercised. This block SIMULATES an eviction the qualified sweep no longer causes (the user's
        # own tooling, a future registrar change, a claude behaviour shift) and then asserts the copy
        # comes back. It also removes the MARKETPLACE, which is the harder case: the recorded SOURCE is
        # the only thing that can rebuild it. MEASURED: `claude plugin install flaui-mcp@flaui-mcp` exits
        # 1 once the marketplace is removed.
        Write-Host "`n== collision: an EVICTED copy must be REINSTALLED on uninstall ==" -ForegroundColor Cyan

        claude plugin marketplace add ckir/flauimcp 2>&1 | Out-Null
        claude plugin install flaui-mcp@flaui-mcp --scope user 2>&1 | Out-Host
        & $Exe install --agent claude | Out-Host

        $seeded = claude plugin list --json | ConvertFrom-Json | Where-Object { $_.id -eq 'flaui-mcp@flaui-mcp' }
        # A gate that cannot run has NOT passed. Same rule as the seeding check above.
        Check 'the reinstall scenario could be set up (the check is able to run at all)' `
              ($null -ne $seeded -and $seeded.enabled -eq $false)
        if ($null -eq $seeded -or $seeded.enabled -ne $false) {
            Write-Warning "could not re-seed a DISABLED marketplace copy - the reinstall check did NOT run. This is a FAILURE, not a skip."
        } else {
            # The marker must carry the source, or the reinstall has nothing to rebuild from.
            $marker = Get-Content (Join-Path $state2 'disabled-plugins.json') -Raw | ConvertFrom-Json
            Check 'the marker is version 2' ($marker.version -eq 2)
            Check 'the marker recorded the marketplace source' `
                  ($null -ne $marker.disabled[0].marketplace -and $marker.disabled[0].marketplace.source)

            # SIMULATE THE EVICTION, then remove the marketplace so only the recorded SOURCE can recover.
            claude plugin uninstall flaui-mcp@flaui-mcp 2>&1 | Out-Host
            claude plugin marketplace remove flaui-mcp 2>&1 | Out-Host
            $gone = claude plugin list --json | ConvertFrom-Json | Where-Object { $_.id -eq 'flaui-mcp@flaui-mcp' }
            Check 'the copy really was evicted (the precondition holds)' ($null -eq $gone)

            & $Exe uninstall --agent claude | Out-Host
            $rebuilt = claude plugin list --json | ConvertFrom-Json | Where-Object { $_.id -eq 'flaui-mcp@flaui-mcp' }
            Check 'the evicted copy was REINSTALLED from the recorded marketplace' ($null -ne $rebuilt)
            Check 'and re-enabled' ($rebuilt.enabled -eq $true)
        }

        # ⚠ THE BRANCH THAT PROTECTS THE USER FROM A SILENTLY WRONG INSTALL. A repointed alias must be
        # REFUSED, not followed - "present by alias" is not "is the thing we recorded".
        Write-Host "`n== collision: a REPOINTED alias must be refused, not followed ==" -ForegroundColor Cyan

        claude plugin marketplace add ckir/flauimcp 2>&1 | Out-Null
        claude plugin install flaui-mcp@flaui-mcp --scope user 2>&1 | Out-Null
        & $Exe install --agent claude | Out-Host
        claude plugin uninstall flaui-mcp@flaui-mcp 2>&1 | Out-Null

        # Repoint the alias at a DIFFERENT source by hand - the registry is what restore compares against.
        $registry = Join-Path $claude2 'plugins\known_marketplaces.json'
        $repointed = $false
        if (Test-Path $registry) {
            $json = Get-Content $registry -Raw | ConvertFrom-Json
            if ($json.'flaui-mcp') {
                $json.'flaui-mcp'.source.repo = 'someone-else/a-different-fork'
                $json | ConvertTo-Json -Depth 10 | Set-Content $registry
                $repointed = $true
            }
        }
        Check 'the alias could be repointed (the check is able to run at all)' $repointed
        if (-not $repointed) {
            Write-Warning "could not repoint the alias - the refusal check did NOT run. This is a FAILURE, not a skip."
        } else {
            $out = & $Exe uninstall --agent claude 2>&1 | Out-String
            Write-Host $out
            Check 'restore REFUSED to install from a repointed alias' ($out -match 'someone-else/a-different-fork')
            $wrong = claude plugin list --json | ConvertFrom-Json | Where-Object { $_.id -eq 'flaui-mcp@flaui-mcp' }
            Check 'and installed nothing' ($null -eq $wrong)
        }
```

- [ ] **Step 4: Check the cleanup still covers everything**

The `finally` at lines 139-145 removes `flaui-mcp@flaui-mcp` and the `flaui-mcp` marketplace, and deletes
the whole sandbox. Read it and confirm it still covers everything the new blocks create. Add any missing
removal **inside the `finally`**, never inside the `try`.

- [ ] **Step 5: Run the gate**

⚠ **This is a MANUAL gate.** It needs the real `claude` CLI and network access. It is not part of
`dotnet test` and cannot run in CI.

```powershell
dotnet publish src/FlaUI.Mcp.Server -c Release -o publish
pwsh -File scripts/install-smoke.ps1
```

Expected: `ALL CHECKS PASSED`.

⚠ **A gate that cannot run has NOT passed.** Every new block has an "able to run at all" check for
exactly that reason — a seeding failure is a FAILURE, never a silent skip.

- [ ] **Step 6: MUTANT PROOF — the new checks must be non-vacuous**

Temporarily make `TryReinstall` never reinstall: insert `return false;` as its very first statement.
Rebuild and re-publish, then re-run the smoke.

Expected: **`the evicted copy was REINSTALLED from the recorded marketplace` FAILS**, while the two
ORIGINAL collision checks still PASS — which is precisely the false-GREEN this new check exists to close.

Revert, rebuild, re-publish, re-run. Expected: `ALL CHECKS PASSED`.

- [ ] **Step 7: Record the acceptance evidence**

Save the full smoke output — including the `claude CLI:` line — to
`docs/superpowers/plans/2026-08-20-release-tooling-smoke.md`, with the date it was run and the commit SHA
it was run against. A green result is only evidence about the CLI behaviour of that day.

- [ ] **Step 8: Commit**

```bash
git add scripts/install-smoke.ps1 docs/superpowers/plans/2026-08-20-release-tooling-smoke.md
git commit -m "test(smoke): force layer (b)'s precondition and pin the repointed-alias refusal

The two existing collision checks go green under fix (a) ALONE - once the
sweep is qualified nothing evicts the copy, so the reinstall fallback would
ship entirely unexercised. The new block simulates an eviction AND removes the
marketplace, so only the recorded SOURCE can rebuild it.

Also pins the repointed-alias refusal, which had no test at all and is the
only branch protecting the user from a silently wrong install. Records
claude --version: a green gate is evidence about the CLI of one day.

MUTANT PROVEN: making TryReinstall return false immediately turns the new
check red while both original checks stay green."
```

---

### Task 12: Full gates and handoff

- [ ] **Step 1: Clean build**

Run: `dotnet build FlaUI.Mcp.slnx -c Release --no-incremental` — expected `0 Warning(s)` / `0 Error(s)`.

⚠ `--no-incremental` is now belt-and-braces rather than load-bearing — Task 2 is what makes the
incremental build honest. Keep passing it anyway on a final gate.

- [ ] **Step 2: Headless suite**

Run: `dotnet test --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: `Failed: 0, Skipped: 0`. The count should be **894 plus the tests this plan added**. Write the
actual number down — it becomes the next subproject's baseline.

- [ ] **Step 3: Desktop suite**

⚠ **MAIN THREAD ONLY.** Do not run a subagent alongside it — a co-running subagent produced 4 spurious
failures and a 40%-longer run. It needs a **physical console**: `SendInput` does not deliver over RDP.
Check with `qwinsta` (not `$env:SESSIONNAME`); a session state of `Disc` means not even RDP. `0 skipped`
needs a user-granted lease. It takes ~14 minutes; run it backgrounded against the 600s tool cap.

```bash
dotnet test --filter "Category=Desktop&Category!=KnownDefect&Category!=Measurement&FullyQualifiedName!~PopupGrafting"
dotnet test --filter "FullyQualifiedName~PopupGrafting"
```

Expected: **156 + 1, plus Task 10's new Desktop-traited test**, `Failed: 0, Skipped: 0`.

Neither change touches the pixel path, so this is a regression check rather than the subject.

- [ ] **Step 4: Confirm the smoke evidence is current**

Task 11 already ran it. Confirm the recorded output is committed and its SHA matches the branch tip — a
smoke record from before the last code commit is not evidence.

- [ ] **Step 5: Update the release notes**

⚠ The v2 marker's cost must be stated, not buried: **an older build can no longer restore a marker
written by this one.** It says so plainly rather than failing silently, and running the newer build again
fixes it completely. Add this to `CHANGELOG.md`'s unreleased section, or note it for
`scripts/release.ps1` to pick up.

- [ ] **Step 6: Update the durable task index**

Refresh `project_v1_batch.md` and `MEMORY.md`: this subproject is done, with each task's commit SHA and
the single ▶ resume point (item 8, `PrintWindow`). A commit not reflected in the index is a recovery hole.

⚠ Also rewrite the two `⚠` blocks in `MEMORY.md` that this work discharges: the **"v1.0 no-known-defects
bar is NOT met"** warning (D1 was the reason) and **ROADMAP item 12** (now fixed). Add the new **item 14**
to the tracked list.

⚠ Delete `docs/fix-the-tool-backlog/install-removes-conflicting-marketplace-copy-and-never-restores-it.md`
once the smoke is green — **and note in the commit message that its diagnosis was WRONG.** It blamed the
disable/restore machinery; measurement showed the registrar's bare-name sweep was the cause. That
correction is worth more than the filing.

- [ ] **Step 7: AGY-CAPSTONE**

Run a convergent agy review of the **committed implementation** — the code and tests, not this plan —
over the branch's full commit range. Verify every finding by measurement before folding, **and verify the
peer's suggested FIX the same way**: a correct finding routinely arrives with a wrong or incomplete fix.
Re-run rounds with a do-not-re-raise ledger until one comes back GREEN.

⚠ **THE LESSON SP4 PAID FOR, and the reason this step is not optional:** SP4's plan survived FIFTEEN
adversarial panel rounds and executing it still found ~7 defects those rounds structurally could not —
because a panel reviews TEXT and never RUNS it. **A green review is not a green build.** This spec had
five rounds; expect the same class of finding here.

- [ ] **Step 8: AGY-TEST-AUDIT**

After the capstone is GREEN, audit the committed test suites for coverage exhaustiveness — the orthogonal
question the capstone does not ask. Surface verified gaps for the operator to scope.

- [ ] **Step 9: Finish the branch**

Use `superpowers:finishing-a-development-branch`.

⚠ **Merged branches are KEPT in this project** — `sp2-additive-perception`, `sp3-per-field-redaction` and
`sp4-redaction-completeness` all still exist post-merge. The skill's script says delete; this project does
not. Do not "tidy" them away.

⚠ **Nothing is pushed this release.** `master` is ~189 commits ahead of `origin/master` by explicit
standing operator decision, not oversight. Merge locally with `--no-ff`, as SP0–SP4 all did.

---

## Self-review

**Spec coverage.** Walked the spec section by section:

| Spec requirement | Task |
|---|---|
| Fix (a), qualified sweep at both call sites | 4 |
| Prerequisite: check released tags before (a) | Discharged in this plan's header; recorded, not re-run |
| Fix (b), reinstall fallback | 9 |
| Marker `version: 2` | 5 |
| The exact `marketplace` schema | 5 |
| The v2 writer round-trips its own field (prereq 2) | 5, Step 1 |
| A v1 marker still restores | 5, Step 1 |
| All three source kinds | 6 |
| Unrecognised kind surfaced, never guessed | 6 (`UnsupportedKinds`) |
| `FutureVersion` message names a recourse | 8 |
| Warning-text rules 1 / 2 / 3 | 7 (rule 1) · 9 (rules 2, 3) |
| Alias-vs-source identity check | 9 |
| `continue`, never `break`/`return` | 9 (`TryReinstall` returns bool) |
| `directory` path normalisation | 6 (`NormalisePath`) + 9 (casing test) |
| Absent vs unreadable are separate branches | 6 + 9 |
| Nothing may abort an uninstall | 6 (never throws) + 9 + 10 |
| Malformed-JSON test, out-of-process, asserts exit 0 | 10 (+ Correction B) |
| Bounded runner for every new call | 9 (`_longRun`) |
| Per-call timeout for `marketplace add` | 9, Step 3 (`MarketplaceAddTimeout` = 90s) |
| `Record()`'s missing lock filed (prereq 3) | 1 |
| Item 12: `Directory.Build.props` | 2 |
| Item 12: mutant proof | 2, Step 4 |
| Item 12: source sweep incl. nested props | 3 |
| Smoke: forcing reinstall check + mutant | 11 |
| Smoke: alias-DIFFERS check | 11 |
| Smoke: `claude --version` recorded | 11, Step 2 |
| Gate that cannot run is a FAILURE | 11 (each block has an "able to run" check) |

**No spec requirement is unimplemented.** Two spec statements are corrected rather than followed as
written (Corrections A and B); both are argued in the header with the code that disproves them.

**Placeholder scan.** No "TBD", no "add appropriate error handling", no "similar to Task N". Every code
step carries the actual code. Three steps deliberately ask the implementer to make a judgement and RECORD
it — Task 7 Step 1's fixture-helper name, Task 8 Step 4's message-text expectation, Task 11 Step 4's
cleanup — and each names the decision and its criterion rather than leaving it open.

**Type consistency, checked across tasks.**
- `MarketplaceSource(Name, Kind, Source)` — defined in Task 5, used identically in Tasks 6, 7, 9 and every test.
- `MarketplacesSnapshot(State, ByName, UnsupportedKinds)` — defined in Task 6; read by property in Tasks 7 and 9.
- `MarketplacesState { FileAbsent, Unreadable, Read }` — the three names are used verbatim everywhere.
- `DisabledEntry`'s fourth parameter is `Marketplace`, optional and last, in Task 5's definition and every construction site.
- `KnownMarketplaces.Read` takes `string?`; `_claudeConfigDir` is `string?`. Consistent.
- `TryReinstall(DisabledEntry, MarketplacesSnapshot, List<string>) -> bool` — one definition (Task 9), one call site (Task 9).
- `ManualEnableRecourse(IReadOnlyList<DisabledEntry>) -> string` — one definition (Task 8), two call sites (Task 8 Steps 3 and 4).
- `CollisionMarker.SchemaVersion` — defined Task 5 Step 3c, read in Step 3d and 3e.

**Sequencing dependencies, verified.**
- Task 5 must precede Task 8 (Correction A: the entry projection is what the message needs).
- Task 6 must precede Tasks 7 and 9 (both consume `KnownMarketplaces`).
- Task 5 must precede Task 7 (`DisabledEntry.Marketplace` must exist to be assigned).
- Task 9 Step 3 completes a wiring line left partial in Task 7 Step 6 — deliberately, so the tree builds
  between commits. **Do not skip Task 9 Step 3's final paragraph.**
- Task 2 before everything else that compiles, so warnings-as-errors guards all later code.

**One risk worth naming.** Task 5 Step 3e moves a block of code, which is the most error-prone edit in
this plan — the entry loop must move VERBATIM into `ParseEntries` with only the `ParseMarketplace`
argument added. If the resulting code drops a comment or a guard, `CollisionMarkerTests` will not
necessarily catch it: several of those guards exist for inputs no test exercises. **Re-read the diff of
that hunk specifically before committing Task 5.**
