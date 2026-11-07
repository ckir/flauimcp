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
