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
        #
        # The rule differs by FILE KIND, and both halves were learned the hard way.
        #
        # CODE (.ps1/.yml): EVERY Pester-adjacent version string must match, not only the ones bound to
        # -RequiredVersion. A review found four bare references in DevelopersCockpit.ps1 -- the health
        # probe's `$_.Version -eq '6.1.0'` and three display strings -- and MEASURED that flipping all four
        # to 5.8.0 left the whole suite green. The cockpit would then report "[ ok ] Pester 5.8.0" while
        # its gates ran 6.1.0, and once the old versions are uninstalled it would report "5.8.0 not
        # installed" forever. A version the tool ASSERTS about itself is a pin like any other.
        #
        # PROSE (.md): only -RequiredVersion counts. The looser rule was measured failing on this repo's
        # own documentation, where a sentence explaining WHY the pin exists named another version and the
        # scan read that as a competing pin. Prose legitimately discusses versions; code does not.
        foreach ($file in $script:LiveFiles) {
            $text = Get-Content $file -Raw
            $isProse = [IO.Path]::GetExtension($file) -eq '.md'

            $versions = if ($isProse) {
                @([regex]::Matches($text, '-RequiredVersion (?<v>\d+\.\d+\.\d+)') |
                    ForEach-Object { $_.Groups['v'].Value })
            } else {
                # every version-shaped string on a line that mentions Pester
                @(($text -split "`r?`n") | Where-Object { $_ -match 'Pester' } | ForEach-Object {
                    [regex]::Matches($_, '(?<v>\d+\.\d+\.\d+)') | ForEach-Object { $_.Groups['v'].Value }
                })
            }
            $versions = @($versions | Sort-Object -Unique)
            $versions | Should -Not -BeNullOrEmpty -Because "$(Split-Path $file -Leaf) should name the Pester version at least once"

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
            # Stop at a STATEMENT separator, never at a quote. The first version excluded quotes from
            # the capture, so a switch sitting after a QUOTED path was invisible: on
            # `Invoke-Pester -Path 'scripts/' -EnableExit` the scan saw only " -Path " and reported the
            # invocation clean. MEASURED both quote styles. A gate that cannot see the argument it exists
            # to check is worse than no gate, because it reads as coverage.
            foreach ($m in [regex]::Matches($text, 'Invoke-Pester(?<args>[^\r\n;|]*)')) {
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
