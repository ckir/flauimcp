# PSScriptAnalyzer Warning Gate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Gate every tracked `.ps1` at ZERO PSScriptAnalyzer diagnostics (Warning and Error) under a committed, curated
profile and a pinned analyzer version, after fixing the one real defect the analyzer found.

**Architecture:** A settings file `PSScriptAnalyzerSettings.psd1` (5 reviewed exclusions, Severity Error+Warning) at the
repo root; 4 function-level suppressions; 9 source fixes; a new Pester file `scripts/psscriptanalyzer.Tests.ps1` that runs
the analyzer and guards itself against vacuous passes. It replaces the Error-only `It` in `scripts/pester-pin.Tests.ps1`.
CI pins `PSScriptAnalyzer -RequiredVersion 1.25.0`.

**Tech Stack:** PowerShell 7, PSScriptAnalyzer 1.25.0, Pester 6.1.0 (pinned), `just`.

**Spec:** `docs/superpowers/specs/2026-11-06-pssa-warning-gate-design.md` (approved 2026-11-07).

**Verification status of this plan.** Every edit below was applied to a throwaway copy of the tree at `4a87cee` and
measured on 2026-11-07 (PSSA 1.25.0; Pester **6.2.0**, because 6.1.0 is not installed on this machine, see Task 0):
16 files, 0 diagnostics, 0 parse errors; full `scripts/` suite 210 passed / 0 failed; 10 logic mutants each turned the
intended test red (listed in Task 6, Step 4). Line numbers are against `4a87cee`.

**Line endings.** `scripts/pester-pin.Tests.ps1` and `TODO.md` are CRLF; every other file touched here is LF
(`scripts/release.Tests.ps1` is LF with 2 stray CRs). Edit with a tool that preserves the file's endings; do not
re-save a whole file through a tool that normalizes them.

---

## File map

| file | change |
|---|---|
| `scripts/release.ps1` | L710 prompt fix (the real bug); suppression on `Invoke-ChangelogLlm` (L204) |
| `scripts/release.Tests.ps1` | new `It` pinning the prompt's variable; `$null = $Root` in 7 stubs |
| `DevelopersCockpit.ps1` | 3 suppressions (`Invoke-Cmd` rewritten to `param()` form, `Get-CsprojVersion`, `Invoke-HealthCheck`); `Render-Menu` -> `Show-Menu` |
| `PSScriptAnalyzerSettings.psd1` | NEW: the curated profile |
| `scripts/psscriptanalyzer.Tests.ps1` | NEW: the gate and its guards |
| `scripts/pester-pin.Tests.ps1` | remove the Error-only `It` (L280-291) |
| `.github/workflows/ci.yml` | L77 pin the analyzer |
| `justfile` | new `lint-scripts` recipe |
| `TODO.md`, `CONTRIBUTING.md`, `CHANGELOG.md` | docs |

---

### Task 0: Preconditions (operator)

- [ ] **Step 1: Install the pinned Pester (needs the operator's OK; it is a machine change)**

MEASURED 2026-11-07: `Get-Module -ListAvailable Pester` lists only 6.2.0 and 3.4.0, so `just pester` and the cockpit's
`E`/`G` gates cannot run here (`Import-Module Pester -RequiredVersion 6.1.0` fails). Run:

```powershell
Install-Module Pester -RequiredVersion 6.1.0 -Scope CurrentUser -Force -SkipPublisherCheck
```

- [ ] **Step 2: Confirm both pinned modules load**

Run: `pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 6.1.0 -ErrorAction Stop; Import-Module PSScriptAnalyzer -RequiredVersion 1.25.0 -ErrorAction Stop; 'ok'"`
Expected: `ok`

- [ ] **Step 3: Baseline the suite on the unchanged tree**

Run: `just pester`
Expected: exit 0, `Failed: 0`. Record the passed count: Task 6 Step 6 checks the final count against it. (The trial
copy measured 210 passed AFTER this change, on Pester 6.2.0; the before-count was not measured.)

---

### Task 1: The real bug: the release confirmation prompt

**Files:**
- Test: `scripts/release.Tests.ps1` (inside `Describe '-NoPush contract'`, after the `It` ending at L1607)
- Modify: `scripts/release.ps1:710`

Oracle: spec section 3.1. `"... $finalStep? [y/N]"` expands the variable `finalStep?` (never set), so the prompt prints
`bump versions,  [y/N]`.

- [ ] **Step 1: Write the failing test**

In `scripts/release.Tests.ps1`, replace (L1606-1608):

```powershell
        $branch | Should -Not -BeNullOrEmpty -Because 'the confirmation wording must branch on $NoPush'
    }
}
```

with:

```powershell
        $branch | Should -Not -BeNullOrEmpty -Because 'the confirmation wording must branch on $NoPush'
    }

    It 'interpolates $finalStep into the final confirmation' {
        # PowerShell reads "$finalStep?" as a variable NAMED 'finalStep?', which is never set, so the prompt
        # printed "bump versions,  [y/N]": it said nothing about pushing, which is the one thing the branch
        # above exists to state. Found by PSScriptAnalyzer (PSUseDeclaredVarsMoreThanAssignments). The fix
        # is "${finalStep}?". Pin the variable the string actually expands, read from the AST.
        $prompt = $script:RelAst.Find({
            param($n) $n -is [System.Management.Automation.Language.CommandAst] -and
                      $n.Extent.Text -match 'Read-Host' -and
                      $n.Extent.Text -match 'Cut release' }, $true)
        $prompt | Should -Not -BeNullOrEmpty
        $text = @($prompt.CommandElements |
            Where-Object { $_ -is [System.Management.Automation.Language.ExpandableStringExpressionAst] })
        $text.Count | Should -Be 1 -Because 'the prompt is one interpolated string'
        $vars = @($text[0].NestedExpressions |
            Where-Object { $_ -is [System.Management.Automation.Language.VariableExpressionAst] } |
            ForEach-Object { $_.VariablePath.UserPath })
        $vars | Should -Contain 'finalStep' -Because 'the prompt must say what the last step will do'
        $vars | Should -Not -Contain 'finalStep?' -Because '"$finalStep?" expands a variable that does not exist'
    }
}
```

(The file's last `}` closes `Describe '-NoPush contract'`; the next line, L1610, is `Describe 'Get-RepoBuildLockHolder' {`.)

- [ ] **Step 2: Run it and see it fail**

Run: `pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 6.1.0; Invoke-Pester -Path scripts/release.Tests.ps1 -FullNameFilter '*interpolates*' -Output Detailed"`
Expected: 1 failed, on `Should -Contain 'finalStep'` (the AST yields `finalStep?`, measured).

- [ ] **Step 3: Fix the prompt**

In `scripts/release.ps1:710` change `bump versions, $finalStep? [y/N]` to `bump versions, ${finalStep}? [y/N]`. The line
becomes:

```powershell
        $ans = Read-Host "Cut release v$($next.Version) — write CHANGELOG, bump versions, ${finalStep}? [y/N]"
```

(The em dash is already in the line; leave it.)

- [ ] **Step 4: Run it and see it pass**

Run: the Step 2 command. Expected: 1 passed.

- [ ] **Step 5: Commit**

```bash
git add scripts/release.ps1 scripts/release.Tests.ps1
git commit -m "fix(release): state the final step in the cut confirmation (\${finalStep}?)"
```

---

### Task 2: The curated profile

**Files:**
- Create: `PSScriptAnalyzerSettings.psd1` (repo root)

Oracle: spec section 3.2 (the five exclusions and their rationales).

- [ ] **Step 1: Create the file**

```powershell
<#
 Copyright (c) 2026 Costas Kirgoussios

 This software is licensed under the PolyForm Noncommercial License 1.0.0.
 You may obtain a copy of the License at:
 https://polyformproject.org/licenses/noncommercial/1.0.0/
#>

# PSScriptAnalyzer profile for this repository's scripts. Gate: scripts/psscriptanalyzer.Tests.ps1 fails on ANY
# diagnostic this profile reports, so every exclusion below widens what the gate cannot see. The exact list is
# pinned by that test file; adding a rule here means editing the test too, on purpose.
# Design and the measured baseline: docs/superpowers/specs/2026-11-06-pssa-warning-gate-design.md
@{
    Severity     = @('Error', 'Warning')

    ExcludeRules = @(
        # Interactive console tools and an installer whose output a human reads. The rule targets functions whose
        # output feeds a pipeline; switching to Write-Output would leak text into return values that callers
        # (and release.Tests.ps1) capture.
        'PSAvoidUsingWriteHost'

        # The BOM rule exists for Windows PowerShell 5.1. Every script here runs under pwsh 7 (justfile shell,
        # lefthook.yml, ci.yml), where UTF-8 without a BOM is the default encoding.
        'PSUseBOMForUnicodeEncodedFile'

        # The repo uses a custom -WhatIf switch on every runnable script instead of SupportsShouldProcess
        # (the "Design note" just below the param block of scripts/release.ps1; the -WhatIf convention is pinned by
        # scripts/pester-pin.Tests.ps1). These two rules ask for the mechanism the repo chose not to use.
        'PSUseShouldProcessForStateChangingFunctions'
        'PSUseSupportsShouldProcess'

        # Flags internal helpers (Assert-Preconditions, Get-CodeWithoutComments), not exported cmdlets.
        # Renaming churns the test suite for no safety gain.
        'PSUseSingularNouns'
    )
}
```

- [ ] **Step 2: Measure what is left**

Run, in a `pwsh` session at the repo root (this is the "lint check" later steps re-run):

```powershell
Import-Module PSScriptAnalyzer -RequiredVersion 1.25.0
git ls-files '*.ps1' |
    ForEach-Object { Invoke-ScriptAnalyzer -Path $_ -Settings ./PSScriptAnalyzerSettings.psd1 } |
    ForEach-Object { '{0}:{1} {2}' -f $_.ScriptName, $_.Line, $_.RuleName }
```

Expected: exactly these 13 lines (any order). The 14th, `release.ps1:709 PSUseDeclaredVarsMoreThanAssignments`, is gone
because of Task 1.

```
DevelopersCockpit.ps1:118 PSAvoidUsingEmptyCatchBlock
DevelopersCockpit.ps1:87 PSAvoidUsingInvokeExpression
DevelopersCockpit.ps1:294 PSAvoidUsingInvokeExpression
DevelopersCockpit.ps1:311 PSUseApprovedVerbs
release.ps1:219 PSUseUsingScopeModifierInNewRunspaces
release.ps1:219 PSUseUsingScopeModifierInNewRunspaces
release.Tests.ps1:693 PSReviewUnusedParameter
release.Tests.ps1:707 PSReviewUnusedParameter
release.Tests.ps1:721 PSReviewUnusedParameter
release.Tests.ps1:727 PSReviewUnusedParameter
release.Tests.ps1:733 PSReviewUnusedParameter
release.Tests.ps1:1655 PSReviewUnusedParameter
release.Tests.ps1:1672 PSReviewUnusedParameter
```

If the list differs, STOP and report: the spec's baseline no longer holds.

- [ ] **Step 3: Commit**

```bash
git add PSScriptAnalyzerSettings.psd1
git commit -m "chore(scripts): add the curated PSScriptAnalyzer profile (5 reviewed exclusions)"
```

---

### Task 3: Consume `$Root` in the 7 test stubs

**Files:**
- Modify: `scripts/release.Tests.ps1` lines 693, 707, 721, 727, 733, 1655, 1672 (Task 1 adds lines only after L1607, so
  L1655 and L1672 move down by 21; match by text, not number)

Oracle: spec section 3.4. The stubs are called with a root argument by the code under test; the parameter stays.

- [ ] **Step 1: Apply the 7 edits (each `old` string occurs exactly once)**

| old | new |
|---|---|
| `$script:Pass = { param($Root) $global:LASTEXITCODE = 0; 'ok' }` | `$script:Pass = { param($Root) $null = $Root; $global:LASTEXITCODE = 0; 'ok' }` |
| `$fail = { param($Root) $global:LASTEXITCODE = 1; 'error: something broke' }` | `$fail = { param($Root) $null = $Root; $global:LASTEXITCODE = 1; 'error: something broke' }` |
| `$warnBuild = { param($Root) $global:LASTEXITCODE = 0;` | `$warnBuild = { param($Root) $null = $Root; $global:LASTEXITCODE = 0;` |
| `$cleanBuild = { param($Root) $global:LASTEXITCODE = 0;` | `$cleanBuild = { param($Root) $null = $Root; $global:LASTEXITCODE = 0;` |
| `$fail = { param($Root) $global:LASTEXITCODE = 1; 'drift detected' }` | `$fail = { param($Root) $null = $Root; $global:LASTEXITCODE = 1; 'drift detected' }` |
| `$script:Ok = { param($Root) $global:LASTEXITCODE = 0; 'ok' }` | `$script:Ok = { param($Root) $null = $Root; $global:LASTEXITCODE = 0; 'ok' }` |
| `$spy = { param($Root) $script:BuildRan = $true;` | `$spy = { param($Root) $null = $Root; $script:BuildRan = $true;` |

- [ ] **Step 2: Run the release suite**

Run: `pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 6.1.0; Invoke-Pester -Path scripts/release.Tests.ps1 -Output None -PassThru | Select-Object PassedCount, FailedCount"`
Expected: `FailedCount` 0.

- [ ] **Step 3: Re-run Task 2 Step 2.** Expected: the 7 `PSReviewUnusedParameter` lines are gone (6 lines left).

- [ ] **Step 4: Commit**

```bash
git add scripts/release.Tests.ps1
git commit -m "test(release): consume the stub root parameter (PSReviewUnusedParameter)"
```

---

### Task 4: The cockpit: rename and 3 suppressions

**Files:**
- Modify: `DevelopersCockpit.ps1` L77, L114-115, L276, L311, L331

Oracle: spec sections 3.3 and 3.4. Suppressions go on a FUNCTION's `param()` block (a statement-level attribute is a parse
error, measured). `Invoke-Cmd`'s 9 call sites (L177, 181, 182, 187, 230, 231, 255, 260, 340) all pass `$Cmd`
positionally, so the `param()` rewrite does not change binding. No `[CmdletBinding()]` is added, so the functions stay
simple functions with no common parameters.

- [ ] **Step 1: `Invoke-Cmd` (L77).** Replace the line

```powershell
function Invoke-Cmd([string]$Cmd) {
```

with

```powershell
function Invoke-Cmd {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingInvokeExpression', '', Justification = 'Runs only hardcoded delegation strings from the $Actions table, never operator input.')]
    param([string]$Cmd)
```

The comment block and the body that follow stay unchanged.

- [ ] **Step 2: `Get-CsprojVersion` (L114-115).** Replace

```powershell
function Get-CsprojVersion {
    try {
```

with

```powershell
function Get-CsprojVersion {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingEmptyCatchBlock', '', Justification = 'Display-only banner: an unreadable csproj shows ?, never aborts.')]
    param()
    try {
```

- [ ] **Step 3: `Invoke-HealthCheck` (L276).** Replace the line `function Invoke-HealthCheck {` with

```powershell
function Invoke-HealthCheck {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingInvokeExpression', '', Justification = 'Runs only the hardcoded probe strings in $script:HealthProbes, never operator input.')]
    param()
```

- [ ] **Step 4: Rename.** `function Render-Menu {` (L311) becomes `function Show-Menu {`; the call `    Render-Menu` (L331) becomes
`    Show-Menu`. There is no other reference in live code (grep-measured; the hits under `docs/superpowers/plans/` are history
and stay).

- [ ] **Step 5: Verify**

Run, in `pwsh` at the repo root:

```powershell
rg -n 'Render-Menu' DevelopersCockpit.ps1
$e = $null; [void][Management.Automation.Language.Parser]::ParseFile("$PWD/DevelopersCockpit.ps1", [ref]$null, [ref]$e); "parse errors: $(@($e).Count)"
```

Expected: no `rg` hit, `parse errors: 0`. Then re-run the Task 2 Step 2 lint check: only the 2 `release.ps1:219` lines
are left.

- [ ] **Step 6: Smoke the cockpit dry run.** Run `'Q' | pwsh -NoProfile -File ./DevelopersCockpit.ps1 -WhatIf`.
Expected (measured on the trial tree): the whole menu prints (this calls `Show-Menu`), then `bye.`, exit 0.

- [ ] **Step 7: Commit**

```bash
git add DevelopersCockpit.ps1
git commit -m "refactor(cockpit): Show-Menu, and reviewed function-level PSSA suppressions"
```

---

### Task 5: The false positive in `Invoke-ChangelogLlm`

**Files:**
- Modify: `scripts/release.ps1` L204-206

Oracle: spec section 3.3 (FALSE POSITIVE, measured in a 3-line repro; the `$using:` "fix" is wrong and rejected).

- [ ] **Step 1: Replace**

```powershell
function Invoke-ChangelogLlm {
    [CmdletBinding()]
    param(
```

with

```powershell
function Invoke-ChangelogLlm {
    # FALSE POSITIVE in PSScriptAnalyzer 1.25.0: the Start-Job below passes its values through
    # param() + -ArgumentList, which is the correct pattern; the rule flags it anyway. Function-level, so a
    # second Start-Job added here would be covered too: review it by hand.
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseUsingScopeModifierInNewRunspaces', '', Justification = 'False positive: values reach the job via param() and -ArgumentList.')]
    [CmdletBinding()]
    param(
```

- [ ] **Step 2: Verify zero.** Re-run Task 2 Step 2. Expected: NO output (0 diagnostics over 16 files, measured).

- [ ] **Step 3: Run the release suite.** Task 3 Step 2's command. Expected: `FailedCount` 0 (several tests read
`release.ps1` by AST and by source text; this edit must not disturb them).

- [ ] **Step 4: Commit**

```bash
git add scripts/release.ps1
git commit -m "refactor(release): suppress the PSSA runspace false positive on Invoke-ChangelogLlm"
```

---

### Task 6: The gate

**Files:**
- Create: `scripts/psscriptanalyzer.Tests.ps1`
- Modify: `.github/workflows/ci.yml:77`
- Modify: `scripts/pester-pin.Tests.ps1:280-291`
- Modify: `justfile` (insert before L55)

Oracle: spec section 4. The test file below is already written and was measured green on the trial tree; implement the
surrounding edits until it passes. Do not weaken an assertion to get green.

- [ ] **Step 1: Pin the analyzer in CI.** In `.github/workflows/ci.yml` L77 change
`Install-Module PSScriptAnalyzer -Force -SkipPublisherCheck -Scope CurrentUser` to
`Install-Module PSScriptAnalyzer -RequiredVersion 1.25.0 -Force -SkipPublisherCheck -Scope CurrentUser`.
(Safe for the Pester pin scan: that scan reads only lines matching `Pester`, and this line does not, measured.)

- [ ] **Step 2: Create `scripts/psscriptanalyzer.Tests.ps1`**

```powershell
<#
 Copyright (c) 2026 Costas Kirgoussios

 This software is licensed under the PolyForm Noncommercial License 1.0.0.
 You may obtain a copy of the License at:
 https://polyformproject.org/licenses/noncommercial/1.0.0/
#>

# scripts/psscriptanalyzer.Tests.ps1
#
# The PSScriptAnalyzer gate: ZERO diagnostics, at Warning and Error, across every tracked .ps1, under the
# curated profile in PSScriptAnalyzerSettings.psd1. Design, measured baseline and every disposition:
# docs/superpowers/specs/2026-11-06-pssa-warning-gate-design.md
#
# A zero gate is only worth something if it cannot pass by seeing nothing, so most of this file guards the
# gate rather than running it:
#   - the analyzer is the version ci.yml pins, imported explicitly (a runner image or a developer machine
#     may carry another version, and an unpinned call resolves to the highest one);
#   - in CI a missing analyzer FAILS; locally it SKIPS with the install command, never a silent pass;
#   - the profile's exclusion list and the inline suppression sites are pinned, so widening either is an
#     edit to this file;
#   - a sentinel runs the real profile against a probe that must be flagged, which catches a profile that
#     excludes everything or drops Warning severity.

BeforeAll {
    $script:Repo     = Split-Path -Parent $PSScriptRoot
    $script:Settings = Join-Path $script:Repo 'PSScriptAnalyzerSettings.psd1'

    # The pin is read from CI, not hardcoded here: a test that carries its own copy of the version cannot
    # detect the two drifting apart (same rule as scripts/pester-pin.Tests.ps1).
    $ciText = Get-Content (Join-Path $script:Repo '.github/workflows/ci.yml') -Raw
    $script:PssaPin = ([regex]::Match($ciText,
        'Install-Module PSScriptAnalyzer -RequiredVersion (?<v>\d+\.\d+\.\d+)')).Groups['v'].Value

    # GitHub Actions sets CI=true. Anywhere else a missing analyzer is a skip, not a failure.
    $script:InCi = [bool]$env:CI

    $script:Pssa = $null
    if ($script:PssaPin) {
        $script:Pssa = Import-Module PSScriptAnalyzer -RequiredVersion $script:PssaPin -PassThru -ErrorAction SilentlyContinue
    }

    # Scope: the TRACKED scripts, so an untracked scratch .ps1 cannot fail a clean checkout. Anchored to the
    # repo root with -C, because Pester runs from wherever it was invoked. Paths use '/' either way.
    $script:ScanList = @()
    try {
        $tracked = @(git -C $script:Repo ls-files '*.ps1' 2>$null)
        if ($LASTEXITCODE -eq 0) { $script:ScanList = @($tracked | ForEach-Object { $_ -replace '\\', '/' }) }
    } catch { $script:ScanList = @() }
    if ($script:ScanList.Count -eq 0) {
        # Not a git checkout (an exported tree): walk the disk, skipping tool and build output.
        $skip = '(^|/)(\.git|node_modules|\.clavity|bin|obj|artifacts)/'
        $script:ScanList = @(Get-ChildItem -Path $script:Repo -Recurse -File -Filter *.ps1 |
            ForEach-Object { $_.FullName.Substring($script:Repo.Length + 1) -replace '\\', '/' } |
            Where-Object { $_ -notmatch $skip })
    }
}

function script:Test-AnalyzerReady {
    # Returns $true when the pinned analyzer is loaded. Otherwise fails in CI and marks the test skipped
    # locally; the CALLER must `return` on $false, so the body never reaches a cmdlet that is not there.
    if ($script:Pssa) { return $true }
    $why = "PSScriptAnalyzer $($script:PssaPin) (the version ci.yml pins) is not installed. " +
           "Install it: Install-Module PSScriptAnalyzer -RequiredVersion $($script:PssaPin) -Scope CurrentUser"
    if ($script:InCi) { throw "fail closed in CI: $why" }
    Set-ItResult -Skipped -Because $why
    return $false
}

Describe 'PSScriptAnalyzer gate' {

    It 'reads a pinned analyzer version out of CI' {
        $script:PssaPin | Should -Match '^\d+\.\d+\.\d+$' -Because 'ci.yml must install PSScriptAnalyzer with -RequiredVersion'
    }

    It 'runs the pinned analyzer version, not whichever is highest' {
        if (-not (Test-AnalyzerReady)) { return }
        $script:Pssa.Version.ToString() | Should -Be $script:PssaPin
        (Get-Command Invoke-ScriptAnalyzer).Module.Version.ToString() | Should -Be $script:PssaPin -Because 'Invoke-ScriptAnalyzer must resolve to the pinned module'
    }

    It 'has the settings file the gate runs under' {
        Test-Path $script:Settings | Should -BeTrue
    }

    It 'excludes exactly the reviewed rules' {
        # Each exclusion hides a whole rule from the gate. The rationale for each sits beside it in the
        # settings file; a sixth one has to be added here too, which makes it a reviewed edit.
        $excluded = @((Import-PowerShellDataFile $script:Settings).ExcludeRules | Sort-Object)
        $excluded | Should -Be @(
            'PSAvoidUsingWriteHost'
            'PSUseBOMForUnicodeEncodedFile'
            'PSUseShouldProcessForStateChangingFunctions'
            'PSUseSingularNouns'
            'PSUseSupportsShouldProcess'
        )
    }

    It 'suppresses rules inline only at the reviewed sites, each on a function param() block' {
        # A function-level suppression also covers a FUTURE second hit of the same rule in that function, so
        # the sites are pinned. On anything other than a function's param() block the attribute is either a
        # parse error or a suppression of an unknown scope; both show up here as an unexpected site.
        $sites = foreach ($f in $script:ScanList) {
            $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                (Join-Path $script:Repo $f), [ref]$null, [ref]$null)
            $found = $ast.FindAll({ param($n)
                    $n -is [System.Management.Automation.Language.AttributeAst] -and
                    $n.TypeName.Name -match 'SuppressMessage' }, $true)
            foreach ($a in $found) {
                $fn = $a.Parent.Parent.Parent
                $where = if ($a.Parent -is [System.Management.Automation.Language.ParamBlockAst] -and
                             $fn -is [System.Management.Automation.Language.FunctionDefinitionAst]) { $fn.Name } else { '<not a function param block>' }
                '{0}:{1}:{2}' -f $f, $where, $a.PositionalArguments[0].Value
            }
        }
        @($sites | Sort-Object) | Should -Be @(
            'DevelopersCockpit.ps1:Get-CsprojVersion:PSAvoidUsingEmptyCatchBlock'
            'DevelopersCockpit.ps1:Invoke-Cmd:PSAvoidUsingInvokeExpression'
            'DevelopersCockpit.ps1:Invoke-HealthCheck:PSAvoidUsingInvokeExpression'
            'scripts/release.ps1:Invoke-ChangelogLlm:PSUseUsingScopeModifierInNewRunspaces'
        )
    }

    It 'scans a non-empty list that includes the release script' {
        $script:ScanList.Count | Should -BeGreaterThan 0
        $script:ScanList | Should -Contain 'scripts/release.ps1'
    }

    It 'sentinel: the real settings still report a rule that is not excluded' {
        # Proves the profile has teeth. A profile with ExcludeRules = '*', with Severity reduced to Error, or
        # excluding this probe's rule makes the probe silent and this test red (each measured).
        if (-not (Test-AnalyzerReady)) { return }
        $probe = 'function P { param($Unused) 1 }'
        $rules = @(Invoke-ScriptAnalyzer -ScriptDefinition $probe -Settings $script:Settings | ForEach-Object RuleName)
        $rules | Should -Contain 'PSReviewUnusedParameter'
    }

    It 'reports zero Warning- or Error-level diagnostics across repository scripts' {
        # Severity comes from the settings file (Error and Warning), which the sentinel above guards.
        if (-not (Test-AnalyzerReady)) { return }
        $issues = @(foreach ($f in $script:ScanList) {
            Invoke-ScriptAnalyzer -Path (Join-Path $script:Repo $f) -Settings $script:Settings |
                ForEach-Object { '{0}:{1} [{2}] {3}' -f $f, $_.Line, $_.RuleName, $_.Message }
        })
        $issues | Should -BeNullOrEmpty -Because ("PSScriptAnalyzer findings:`n" + ($issues -join "`n"))
    }
}
```

- [ ] **Step 3: Remove the old Error-only `It`.** In `scripts/pester-pin.Tests.ps1` (CRLF), replace L280-292, which is the
blank line, the `It 'PSScriptAnalyzer reports zero Error-level diagnostics across repository scripts if installed' { ... }`
block and the closing `}` of `Describe 'Pester pin'`, with:

```powershell

    # The PSScriptAnalyzer gate (Error AND Warning, curated profile, pinned version) lives in
    # scripts/psscriptanalyzer.Tests.ps1. It replaced the Error-only check that used to sit here.
}
```

No other file names that `It` (grep-measured; the only reference is `TODO.md` item 6, updated in Task 7).

- [ ] **Step 4: Prove each guard is non-vacuous (temporary mutants; revert each before the next)**

Run the gate file after each mutant with
`pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 6.1.0; Invoke-Pester -Path scripts/psscriptanalyzer.Tests.ps1 -Output Detailed"`
(M1 also runs `scripts/release.Tests.ps1`). Expected red tests (measured on the trial tree under Pester 6.2.0):

| # | mutant | expected |
|---|---|---|
| M1 | `release.ps1`: `${finalStep}?` back to `$finalStep?` | red: `reports zero ...` and `interpolates $finalStep ...` |
| M2 | settings: `Severity = @('Error')` | red: `sentinel` |
| M3 | settings: add `'PSReviewUnusedParameter'` to ExcludeRules | red: `excludes exactly`, `sentinel` |
| M4 | `release.Tests.ps1`: drop `$null = $Root; ` from `$cleanBuild` | red: `reports zero ...` |
| M5a | `ci.yml`: pin `9.9.9`, `$env:CI` unset | 3 SKIPPED (`runs the pinned`, `sentinel`, `reports zero`), 0 failed |
| M5b | as M5a with `$env:CI = 'true'` | the same 3 FAILED (fail closed) |
| M5c | `ci.yml`: drop `-RequiredVersion 1.25.0` | red: `reads a pinned analyzer version`; 3 skipped |
| M6 | cockpit: `[SuppressMessage('PSAvoidUsingWriteHost','')] param()` on `Show-Menu` | red: `suppresses rules inline only at the reviewed sites` |
| M7 | settings: `ExcludeRules = @('*'` + the five | red: `excludes exactly`, `sentinel` |
| M8 | untracked `scratch-junk.ps1` with `function Bad { param($Unused) 1 }` at the repo root | stays GREEN (untracked files are out of scope) |

Delete the M8 file afterwards. Confirm `git status --short` shows only this task's intended edits.

- [ ] **Step 5: Add the `just lint-scripts` recipe.** In `justfile`, insert before L55
(`# Build, headless tests, and the PowerShell suite. The cockpit's [G] Dev gate.`):

```just
# Runs inside `just pester` too; this is the fast path for scripts only. A justfile-only recipe: the cockpit's
# [E] gate already runs this file as part of scripts/.
#
# The PSScriptAnalyzer gate alone (Warning and Error, curated profile, pinned analyzer).
lint-scripts:
    Import-Module Pester -RequiredVersion 6.1.0; $c = New-PesterConfiguration; $c.Run.Path = 'scripts/psscriptanalyzer.Tests.ps1'; $c.Run.Exit = $true; Invoke-Pester -Configuration $c

```

(The SUMMARY line comes last, because `just --list` shows only the comment line directly above a recipe, measured.
The recipe's switch, `-Configuration`, exists in Pester 6; `pester-pin.Tests.ps1` checks that.)

- [ ] **Step 6: Run**

Run: `just lint-scripts` -> exit 0, 8 passed.
Run: `just --list` -> shows `lint-scripts    # The PSScriptAnalyzer gate alone (...)`.
Run: `just pester` -> exit 0, `Failed: 0`; the passed count is Task 0's baseline + 9 (8 gate tests + 1 prompt test)
- 1 (removed `It`) = baseline + 8.
Run: `addlicense -check -f header.txt -ignore "**/obj/**" -ignore "**/bin/**" -ignore "**/artifacts/**" -ignore "**/.serena/**" .` -> exit 0.

- [ ] **Step 7: Commit**

```bash
git add scripts/psscriptanalyzer.Tests.ps1 scripts/pester-pin.Tests.ps1 .github/workflows/ci.yml justfile
git commit -m "ci(scripts): gate PSScriptAnalyzer Warnings at zero under a pinned analyzer"
```

---

### Task 7: Docs

**Files:**
- Modify: `TODO.md:23-28` (CRLF)
- Modify: `CONTRIBUTING.md` (after the Pester paragraph, L88)
- Modify: `CHANGELOG.md:6` (`## [Unreleased]`)

- [ ] **Step 1: `TODO.md`.** Items 4 and 5 are stale (B2 shipped in v1.1.0; B4 closed by ruling 2026-11-06). Replace
L23-28 with:

```markdown
4. [x] **B2 — `desktop_open_tray`** *(shipped in v1.1.0)*: system tray as a driveable surface (three tools).
5. [x] **B4 — `desktop_list_terminal_processes`** *(closed by ruling 2026-11-06, YAGNI)*: the measured facts and the
   reopen condition are in ROADMAP.md (B4).
6. [x] **CI Script Analyzer Warnings**: every tracked `.ps1` is gated at zero PSScriptAnalyzer diagnostics (Warning
   and Error) under `PSScriptAnalyzerSettings.psd1`, with the analyzer version pinned in CI
   (`scripts/psscriptanalyzer.Tests.ps1`, `just lint-scripts`).
```

- [ ] **Step 2: `CONTRIBUTING.md`.** After L88 (`` `-CI` also enables `TestResult`, which drops a `testResults.xml` into the working tree. ``),
insert, at the same 3-space indent:

```markdown
   The same suite lints every tracked script with PSScriptAnalyzer at Warning and Error, under the profile in
   `PSScriptAnalyzerSettings.psd1`; `just lint-scripts` runs only that part. Install the analyzer version that
   `.github/workflows/ci.yml` pins: without it the lint tests are skipped locally (the skip message prints the
   install command) and fail in CI. To silence a finding, fix it; an exclusion or a suppression is a reviewed edit
   to `scripts/psscriptanalyzer.Tests.ps1` too.
```

DO NOT write the analyzer's version as `-RequiredVersion X.Y.Z` in this file: `pester-pin.Tests.ps1` scans
`CONTRIBUTING.md` and requires EVERY `-RequiredVersion` there to equal the Pester pin (`Get-PesterVersionsNamed`, `.md`
rule), so it would go red.

- [ ] **Step 3: `CHANGELOG.md`.** Under `## [Unreleased]` (L6) insert:

```markdown

### Fixed

- **The release cut confirmation now states its final step.** `"... bump versions, $finalStep? [y/N]"` expanded a
  variable named `finalStep?`, so the prompt read `bump versions,  [y/N]` and never said whether the release would
  push. Found by PSScriptAnalyzer; pinned by a test on the prompt's AST.

### Changed

- **Scripts are gated on PSScriptAnalyzer Warnings, not only Errors.** Zero diagnostics across every tracked `.ps1`,
  under a committed profile (`PSScriptAnalyzerSettings.psd1`) whose five exclusions each carry a rationale. The
  analyzer version is pinned in CI, a missing analyzer fails CI instead of passing, and a sentinel proves the profile
  still reports. `just lint-scripts` runs the gate alone.
```

- [ ] **Step 4: Verify** `just pester` -> exit 0 (CONTRIBUTING.md is a scanned file).

- [ ] **Step 5: Commit**

```bash
git add TODO.md CONTRIBUTING.md CHANGELOG.md
git commit -m "docs: PSSA Warning gate (TODO 4-6, CONTRIBUTING, CHANGELOG)"
```

---

### Task 8: Completion gates (driver, not a subagent)

- [ ] AGY-CAPSTONE over `4a87cee..HEAD` (skill `clavity:agy-capstone`), rounds until GREEN, ledger row before marker.
- [ ] AGY-TEST-AUDIT over the same range (skill `clavity:agy-test-audit`).
- [ ] Ask the operator before `git push`; then watch CI. The first CI run is the measurement of the analyzer on the
  `windows-latest` image (spec section 6): confirm the `pester` job ran the 8 gate tests and none was skipped.

---

## Self-review (2026-11-07)

Spec coverage: 3.1 -> Task 1; 3.2 -> Task 2; 3.3 -> Tasks 4, 5; 3.4 -> Tasks 3, 4; section 4 (home, scope, settings,
fail-closed, skip+return, guards a-c, exclusion pin, suppression-site pin, PSSA pin, `just lint-scripts`) -> Task 6;
section 5 TODO fix -> Task 7. Items the spec deferred to the plan: prompt-test wording and mutant (Task 1, Task 6 M1),
`Invoke-Cmd` `param()` text (Task 4 Step 1), recipe text (Task 6 Step 5). All three are written out above.

Found while measuring and NOT in this plan's scope (logged in `.clavity/local-anomalies.md`): the existing
`pester-pin.Tests.ps1:228` test passes VACUOUSLY when the pinned Pester is absent (non-terminating `Import-Module`
error, then `Get-Command -Module $null` checks whichever Pester is loaded; measured with 6.1.0 absent).

## Stand-downs

AGY-AFTER panel round 1 (agy, 6 seats, 2026-11-07): PANEL VERDICT CLEAN, no findings. Two nits it discarded:
- DISCARDED-BELOW-FLOOR: `$script:InCi = [bool]$env:CI` treats `CI=false` as CI. Unreachable as a false PASS because of
  `if ($script:InCi) { throw "fail closed in CI: $why" }` in `Test-AnalyzerReady`: the misread only turns a local skip
  into a failure (fail-closed), never a failure into a pass.
- DISCARDED-BELOW-FLOOR: mixed line endings; already covered by the "Line endings" note at the top of this plan.

Unmeasured: the suite under Pester 6.1.0 (Task 0 Step 3 and Task 6 Step 6 measure it); the analyzer on the CI image
(Task 8).
