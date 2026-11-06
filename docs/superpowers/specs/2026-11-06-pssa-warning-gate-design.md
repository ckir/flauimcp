# PSScriptAnalyzer Warning gate: design

Status: DRAFT 2026-11-06. Backlog source: `TODO.md` item 6 ("CI Script Analyzer Warnings, partly done": CI already fails
on Error-level diagnostics via `scripts/pester-pin.Tests.ps1`; remaining: surface Warning-level).
Design consult: AGY-FIRST (peer ALIGNED on a curated profile gated at zero, pinned tool version). Operator chose
approach B on 2026-11-06 over surface-only annotations (A) and a count baseline (C).

## 1. Goal and success criteria

Make Warning-level script quality visible and non-regressing without making a noisy rule red.

1. A new Warning-level diagnostic introduced by any commit fails the Pester job in CI (gate at zero on a CURATED rule set).
2. The curated set is a committed file; every excluded rule has a written rationale and the exclusion list is pinned by a test,
   so widening it is a visible, reviewed edit.
3. The analyzer version is pinned (as Pester already is), so an upstream release cannot turn CI red on an unrelated commit.
4. In CI the gate cannot pass vacuously (analyzer missing, settings missing, or zero files scanned all fail).
5. No behaviour change to the release or gate scripts except the one real defect in section 3 (a fix).

## 2. Measured baseline (2026-11-06, PSScriptAnalyzer 1.25.0, default rules, `-Severity Warning,Error`)

Scope = the existing gate's scope: every `*.ps1` under the repo (`Get-ChildItem -Recurse -Filter *.ps1`) = the 16 tracked
files (`BundleCodeBase.ps1`, `DevelopersCockpit.ps1`, `GitHub_Actions_Storage_Reset.ps1`, `dist/install.ps1`, 12 under
`scripts/`). Total 147 diagnostics, 0 Error, all Warning:

| rule | n | disposition |
|---|---|---|
| PSAvoidUsingWriteHost | 120 | EXCLUDE (settings) |
| PSUseBOMForUnicodeEncodedFile | 8 | EXCLUDE (settings) |
| PSReviewUnusedParameter | 7 | FIX |
| PSUseShouldProcessForStateChangingFunctions | 2 | EXCLUDE (settings) |
| PSUseSupportsShouldProcess | 1 | EXCLUDE (settings) |
| PSUseSingularNouns | 2 | EXCLUDE (settings) |
| PSAvoidUsingInvokeExpression | 2 | SUPPRESS inline |
| PSUseUsingScopeModifierInNewRunspaces | 2 | SUPPRESS inline (false positive) |
| PSAvoidUsingEmptyCatchBlock | 1 | SUPPRESS inline |
| PSUseApprovedVerbs | 1 | FIX (rename) |
| PSUseDeclaredVarsMoreThanAssignments | 1 | FIX (REAL BUG) |

120 + 8 + 7 + 2 + 1 + 2 + 2 + 2 + 1 + 1 + 1 = 147.

## 3. Dispositions and the evidence for each

**3.1 REAL BUG found by the analyzer: `scripts/release.ps1:709-711`.** The unattended-off confirmation prompt is
`"... bump versions, $finalStep? [y/N]"`. PowerShell parses `$finalStep?` as the variable `finalStep?` (measured in pwsh 7.6.9:
`"A: bump versions, $x? [y/N]"` prints `A: bump versions,  [y/N]`, while `${x}?` prints the value). So the prompt never says
whether the release will push, and drops its `?`. The code comment directly above says the prompt "must describe what will
ACTUALLY happen". Fix: `${finalStep}?`. A Pester test pins the prompt text for both `-NoPush` and push (non-vacuous: it must fail
on `$finalStep?`).

**3.2 EXCLUDE in `PSScriptAnalyzerSettings.psd1`, each with a rationale comment:**
- `PSAvoidUsingWriteHost` (120): these are interactive console tools and an installer whose output a human reads; the rule targets
  pipeline-consumed functions. Replacing with `Write-Output` would pollute return values (release.Tests captures function results).
- `PSUseBOMForUnicodeEncodedFile` (8): the BOM rule exists for Windows PowerShell 5.1. Every script runs under `pwsh` 7
  (`justfile` shell/recipes, `lefthook.yml`, `install-smoke.ps1:9 #requires -Version 7`); files are UTF-8 without BOM and contain
  non-ASCII only in comments and a few strings.
- `PSUseShouldProcessForStateChangingFunctions` (2) and `PSUseSupportsShouldProcess` (1): the repo deliberately uses a custom
  `-WhatIf` switch, not `SupportsShouldProcess` (`scripts/release.ps1:81-83` design note; pinned by the pester-pin test that
  every runnable script declares `-WhatIf`).
- `PSUseSingularNouns` (2: `Assert-Preconditions`, `Get-CodeWithoutComments`): internal helper names, not exported cmdlets;
  renaming churns release.Tests.ps1 for no safety gain.

**3.3 SUPPRESS at FUNCTION level (`[System.Diagnostics.CodeAnalysis.SuppressMessageAttribute('Rule','', Justification='...')]`
placed on the enclosing function's `param()` block), because the rule is valuable for future code and each hit is a measured
non-defect.** MEASURED (panel finding, pwsh 7.6.9 + PSSA 1.25.0): the attribute placed on a STATEMENT is a parse error
(`UnexpectedAttribute`, which `pester-pin.Tests.ps1:258` would turn red); on `[SuppressMessage(...)] param()` it parses and
suppresses, and an unattributed sibling function is still flagged. Functions with no `param()` block (`Invoke-Cmd([string]$Cmd)`
uses inline parameters) get a `param()` block form, which is a pure signature rewrite with identical binding. Enclosing functions
(grep-measured): `Invoke-ChangelogLlm` (`scripts/release.ps1:204`), `Invoke-Cmd` (`DevelopersCockpit.ps1:77`), `Get-CsprojVersion`
(`:114`), `Invoke-HealthCheck` (`:276`). Residual risk, accepted and recorded: a function-level suppression also covers a FUTURE
second hit of the same rule in that function; the rationale string says so, and the plan adds a test that the suppression list
is exactly these four sites.
- `PSUseUsingScopeModifierInNewRunspaces` at `scripts/release.ps1:219`: FALSE POSITIVE. `Start-Job -ScriptBlock { param($P,$M) ... }
  -ArgumentList $a,$b` (the correct pattern) is flagged even in a 3-line repro (measured). The peer's suggested "use `$using:`" fix
  is wrong and is rejected.
- `PSAvoidUsingInvokeExpression` at `DevelopersCockpit.ps1:87,294`: executes hard-coded command strings from the cockpit's own
  static menu table, never external input.
- `PSAvoidUsingEmptyCatchBlock` at `DevelopersCockpit.ps1:118`: display-only best-effort banner (comment states a missing source
  shows `?`, never aborts).

**3.4 FIX:**
- `PSReviewUnusedParameter` (7, `scripts/release.Tests.ps1` lines 693, 707, 721, 727, 733, 1655, 1672): stub scriptblocks
  `{ param($Root) ...}` are invoked with the root argument by the code under test; keep the parameter and consume it
  (`$null = $Root`). No behaviour change.
- `PSUseApprovedVerbs` (1): rename `Render-Menu` to `Show-Menu` in `DevelopersCockpit.ps1` (definition L311, one call L331;
  no other reference, grep-measured).

## 4. The gate

HOME (panel finding, accepted): a NEW file `scripts/psscriptanalyzer.Tests.ps1` (picked up by the existing `Invoke-Pester -Path
scripts/` in `ci.yml` and `just gate`), not `pester-pin.Tests.ps1`, which is about the Pester version. The current Error-level
`It` (`pester-pin.Tests.ps1:281`) MOVES there and is subsumed by the Warning+Error gate. A `just lint-scripts` recipe runs only
that file, so a developer can lint in seconds (recipe follows the justfile's existing conventions).
- Scope: `git -C <repo root> ls-files '*.ps1'` (anchored to the repo root, NOT the working directory: Pester runs with `Run.Path = scripts/`; paths normalized to forward slashes before any comparison, since git emits `/` and `Get-ChildItem` emits ``) when the repo is a git checkout (so an untracked scratch `.ps1` cannot fail a clean tree);
  otherwise `Get-ChildItem -Recurse -Filter *.ps1` excluding `.git`, `node_modules`, `.clavity`, `bin`, `obj`, `artifacts`. (Panel finding: the first draft's
  "count equals git ls-files" would have failed on dirty trees; it is dropped.)
- Run with `-Settings <repo>/PSScriptAnalyzerSettings.psd1`; assert zero diagnostics per script, message lists line and text.
- Fail closed in CI: when `$env:CI` (GitHub Actions sets `CI=true`) and PSSA is not installed, FAIL (today it returns, passing
  vacuously). Locally without PSSA: skip visibly (`Set-ItResult -Skipped -Because ...` followed by an explicit `return`, because Set-ItResult does not necessarily stop the test body and the fall-through would call a missing cmdlet), not a silent pass.
- Non-vacuity guards: (a) settings file exists; (b) the scan list is non-empty and contains `scripts/release.ps1`; (c) SENTINEL,
  corrected after the panel (the first draft stripped the exclusions, which proved nothing about the settings file): run the real
  `-Settings` file against a probe script that triggers a rule that is NOT excluded (`function P { param($Unused) 1 }` ->
  `PSReviewUnusedParameter`, measured to fire with the settings loaded) and assert it is reported. A settings file that
  excludes everything (`ExcludeRules=@('*')`, measured to silence the probe) or drops Warning severity fails this test.
- A new `It` pins the exact `ExcludeRules` list (the five names above): adding a sixth requires editing the test.
- Pin PSSA: `ci.yml` installs `PSScriptAnalyzer -RequiredVersion 1.25.0`. The existing "pins the SAME version everywhere it is
  named" test is Pester-specific and requires EVERY file in `$script:LiveFiles` (CONTRIBUTING.md, justfile, cockpit, ci.yml) to
  name the version, so it must NOT be extended blindly (panel finding, confirmed at `pester-pin.Tests.ps1:167`). Instead a
  dedicated `It` reads `ci.yml`, asserts a `PSScriptAnalyzer ... -RequiredVersion X.Y.Z` pin exists, and asserts the
  installed analyzer's version equals it when run in CI. Local drift is reported (skipped-with-reason), not failed. The cockpit
  health probe for PSSA is out of scope.

## 5. Out of scope

- Rewriting `Write-Host` output; changing any script's UX.
- Gating on Information-level rules.
- Upgrading PSSA: a version bump is a deliberate change (edit the pin, re-measure the baseline).
- Stale `TODO.md` entries (items 4 and 5 are done/closed): fixed in the same change as a docs step, since item 6 lives there.

## 6. Exhaustiveness audit (self)

Closed in this document: every one of the 147 diagnostics has a disposition (section 2 sums to 147); every exclusion has a
rationale and a verified anchor; the one real defect is named, measured and has a pinning test requirement; the peer's wrong fix
is recorded as rejected; tool-version drift and vacuous-pass risks are addressed in section 4.
Panel round 1 (agy, PANEL VERDICT FINDINGS-4, all four MEASURED then folded): broken sentinel, statement-level attribute parse
error, dirty-tree file-count check, Pester-only pin machinery. One agy detail was wrong and is not folded: it named the
enclosing function `Invoke-DraftReview`; the real one is `Invoke-ChangelogLlm`. Discarded below the floor by the peer: the
`Render-Menu` rename is style churn (kept: the analyzer flags it and the rename is two grep-measured sites).
Deferred to the PLAN (not to a later spec): the wording and mutants of the prompt-text test, the exact `param()` rewrite of
`Invoke-Cmd`, and the `just lint-scripts` recipe text. Not measured: PSSA behaviour on the `windows-latest` runner image (CI version drift is removed by the pin; the
first CI run after the change is the measurement and must be watched).

Panel round 2 (agy, PANEL VERDICT FINDINGS-2, both minor, folded without a behaviour risk): `git ls-files` anchored to the repo root with
normalized separators and `bin/obj/artifacts` added to the non-git fallback exclusions; explicit `return` after `Set-ItResult -Skipped`.
The fold audit found no defect in the corrected sentinel (a settings file with Error-only severity or excluding the probe rule
fails it), in the `Invoke-Cmd` `param()` rewrite (all 9 call sites are positional), or in moving the Error-level `It` (no dangling
reference except `TODO.md` item 6, updated in the same change). Added CI cost: about 25 s of analyzer time, of which the Error-level
run already paid most.
