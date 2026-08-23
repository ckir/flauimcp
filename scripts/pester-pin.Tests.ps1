# scripts/pester-pin.Tests.ps1
#
# The repo pins a Pester version in several places, and the pin is load-bearing: this machine also carries
# a legacy Pester 3.4.0, so an unpinned `Invoke-Pester` picks the wrong one.
#
# Adopting 6.1.0 was nearly a silent gate break. MEASURED: Pester 6 REMOVED `-EnableExit`, which both
# cockpit gates used. On a PASSING suite, `Invoke-Pester -Path scripts/ -EnableExit` under 6.1.0 exits 1
# with a parameter-binding error -- a gate that is RED ON GREEN. CHANGELOG.md records this repo already
# shipping the mirror-image defect once (failures silently passing the E and G gates), so it gets a gate.
#
# The assertion below is deliberately NOT "-EnableExit is absent". It enumerates every switch the repo
# actually passes to Invoke-Pester and checks each against the pinned version's real parameter list, so a
# DIFFERENT parameter removed by a future Pester version is caught too. Pinning only the symptom that bit
# us would leave the next one to be discovered in CI.

BeforeAll {
    $script:Repo = Split-Path -Parent $PSScriptRoot

    # The single source of truth for the pin, read from CI rather than hardcoded here: a test that carries
    # its own copy of the expected version cannot detect the two drifting apart.
    $script:CiText = Get-Content (Join-Path $script:Repo '.github/workflows/ci.yml') -Raw
    $script:PinnedVersion = ([regex]::Match($script:CiText,
        'Import-Module Pester -RequiredVersion (?<v>\d+\.\d+\.\d+)')).Groups['v'].Value

    # Every live file that invokes Pester. Plans and the changelog are history and are excluded on purpose.
    $script:LiveFiles = @(
        '.github/workflows/ci.yml'
        'DevelopersCockpit.ps1'
        'CONTRIBUTING.md'
    ) | ForEach-Object { Join-Path $script:Repo $_ }
}

Describe 'Pester pin' {

    It 'reads a pinned version out of CI' {
        $script:PinnedVersion | Should -Match '^\d+\.\d+\.\d+$'
    }

    It 'pins the SAME version everywhere it is named' {
        # Drift between CI and the cockpit is how a contributor ends up running a different Pester from the
        # one the gate uses, which is exactly the situation the pin exists to prevent.
        foreach ($file in $script:LiveFiles) {
            $text = Get-Content $file -Raw
            # Match a PIN -- a version bound to -RequiredVersion -- not any version-shaped text near the
            # word "Pester". The looser form was measured failing on this repo's own prose: a sentence
            # explaining WHY the pin exists mentioned another Pester version, and the scan read that as a
            # competing pin. A gate whose evidence can come from prose about the subject is not a gate.
            $versions = @([regex]::Matches($text, '-RequiredVersion (?<v>\d+\.\d+\.\d+)') |
                ForEach-Object { $_.Groups['v'].Value } | Sort-Object -Unique)
            foreach ($v in $versions) {
                $v | Should -Be $script:PinnedVersion -Because "$(Split-Path $file -Leaf) must not drift from CI's pin"
            }
        }
    }

    It 'passes only switches that EXIST in the pinned version' {
        # The general form. -EnableExit was removed in Pester 6 and would have made both cockpit gates red
        # on green; enumerating the switches we actually use catches the next removal too.
        $available = @((Get-Command -Module (Import-Module Pester -RequiredVersion $script:PinnedVersion -PassThru) -Name Invoke-Pester).Parameters.Keys)
        $available | Should -Not -BeNullOrEmpty -Because 'the pinned Pester must be installed for this gate to mean anything'

        $used = New-Object System.Collections.Generic.HashSet[string]
        foreach ($file in $script:LiveFiles) {
            $text = Get-Content $file -Raw
            foreach ($m in [regex]::Matches($text, 'Invoke-Pester(?<args>[^\r\n"'']*)')) {
                foreach ($s in [regex]::Matches($m.Groups['args'].Value, '-(?<sw>[A-Za-z]\w+)')) {
                    [void]$used.Add($s.Groups['sw'].Value)
                }
            }
        }
        $used.Count | Should -BeGreaterThan 0 -Because 'the scan must actually find the invocations it is checking'

        foreach ($switch in $used) {
            $available | Should -Contain $switch -Because "Invoke-Pester -$switch does not exist in the pinned Pester $($script:PinnedVersion); under 6.x that is a parameter-binding error, so the gate fails even when every test passes"
        }
    }
}
