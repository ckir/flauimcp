# scripts/pester-pin.Tests.ps1
#
# The repo pins a Pester version in several places, and the pin is load-bearing -- but NOT for the reason
# this comment used to give. It claimed a legacy Pester 3.4.0 on the machine made an unpinned
# `Invoke-Pester` "pick the wrong one". MEASURED after 5.8.0 was uninstalled, with 3.4.0 still present:
# an unpinned `Import-Module Pester` resolves to 6.1.0. Highest version wins; an OLDER module does not
# shadow a newer one.
#
# What the pin actually buys is that the version is CHOSEN rather than inherited: a machine or a CI image
# with a NEWER Pester than this suite was written against would silently upgrade the runner underneath it,
# and that is precisely how -EnableExit would have vanished without anyone deciding to move.
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
    #
    # `justfile` has no extension, so Remove-CommentText falls through to the line-level `#` rule. That is
    # correct rather than lucky: `#` is just's comment character too, with the same start-of-line-or-after-
    # whitespace shape as the YAML rule it shares.
    #
    # This list is the gate's blind spot by construction - a file that names the version and is NOT here is
    # unguarded. Adding a second release route was exactly that risk, which is why the justfile is in it.
    $script:LiveFiles = @(
        '.github/workflows/ci.yml'
        'DevelopersCockpit.ps1'
        'CONTRIBUTING.md'
        'justfile'
    ) | ForEach-Object { Join-Path $script:Repo $_ }
}

function script:Remove-CommentText {
    <#
    .SYNOPSIS
    Blank out comment text so a scan of "code" cannot read prose.

    .DESCRIPTION
    PowerShell gets the real answer: its own tokenizer knows which spans are comments, so line comments,
    trailing comments and block comments are all handled in one pass. That is the same technique
    release.Tests.ps1 uses, and its comments record the trap being defeated three separate times by those
    three forms.

    (This paragraph deliberately does not spell out the block-comment delimiters. Writing the closing one
    inside a comment-based help block ENDS the block early -- measured: it turned the rest of this file
    into a parse error, Pester discovery failed, and the suite reported 137 passed / 0 failed because the
    three tests in this file had silently removed themselves.)

    YAML has no tokenizer to hand, so it gets a line-level rule: a `#` that starts a line or follows
    whitespace begins a comment. ACKNOWLEDGED LIMIT -- that is wrong for a `#` inside a quoted scalar, and
    a version number after such a `#` would be missed. It is the right trade here: a missed version in a
    quoted YAML string is a far smaller risk than the gate firing on every explanatory comment, which was
    measured happening.

    Lines are blanked rather than removed so that any line numbers a caller reports stay meaningful.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Extension
    )

    if ($Extension -in @('.ps1', '.psm1', '.psd1')) {
        $tokens = $null
        [void][System.Management.Automation.Language.Parser]::ParseInput($Text, [ref]$tokens, [ref]$null)
        $chars = $Text.ToCharArray()
        foreach ($t in $tokens) {
            if ($t.Kind -ne 'Comment') { continue }
            for ($i = $t.Extent.StartOffset; $i -lt $t.Extent.EndOffset -and $i -lt $chars.Count; $i++) {
                if ($chars[$i] -ne "`n" -and $chars[$i] -ne "`r") { $chars[$i] = ' ' }
            }
        }
        return -join $chars
    }

    ($Text -split "`r?`n" | ForEach-Object { $_ -replace '(^|\s)#.*$', '$1' }) -join "`n"
}

function script:Get-PesterVersionsNamed {
    <#
    .SYNOPSIS
    Every Pester version a file PINS. Versions named in COMMENTS are prose and are not pins.

    .DESCRIPTION
    One implementation, called by the live-file scan and by its own unit tests, because the last time the
    two were separate the fix drifted off the caller: a round added comment-stripping here and the next
    round moved it to the sibling scan, leaving a correct stripper that nothing called. The suite stayed
    green the whole time, because none of the scanned files happened to name another version in a comment.

    The rule differs by file kind:

    CODE (.ps1/.yml/extensionless): EVERY version-shaped string on a line mentioning Pester counts, not
    only the ones bound to -RequiredVersion -- a version a tool ASSERTS about itself is a pin like any
    other. Comments are stripped first. Splitting by EXTENSION alone fixed the symptom and not the cause:
    a .ps1 and a .yml are "code" by extension and both contain comments, which are prose in exactly the
    way Markdown is. MEASURED: an ordinary explanatory line --
        # Historical note: this gate ran Pester 5.8.0 until the 6.1.0 migration.
    -- turned this gate RED in both DevelopersCockpit.ps1 and ci.yml.

    PROSE (.md): only -RequiredVersion counts, and the text is deliberately NOT comment-stripped -- `#`
    opens a HEADING in Markdown, not a comment, so the code rule would blank every heading in the file.
    The looser match was measured failing on this repo's own documentation, where a sentence explaining
    WHY the pin exists named another version and the scan read it as a competing pin.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Extension
    )

    if ($Extension -eq '.md') {
        return @([regex]::Matches($Text, '-RequiredVersion (?<v>\d+\.\d+\.\d+)') |
            ForEach-Object { $_.Groups['v'].Value })
    }

    $code = Remove-CommentText -Text $Text -Extension $Extension
    @(($code -split "`r?`n") | Where-Object { $_ -match 'Pester' } | ForEach-Object {
        [regex]::Matches($_, '(?<v>\d+\.\d+\.\d+)') | ForEach-Object { $_.Groups['v'].Value }
    })
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
            $versions = @(Get-PesterVersionsNamed -Text (Get-Content $file -Raw) `
                              -Extension ([IO.Path]::GetExtension($file)) | Sort-Object -Unique)
            $versions | Should -Not -BeNullOrEmpty -Because "$(Split-Path $file -Leaf) should name the Pester version at least once"

            foreach ($v in $versions) {
                $v | Should -Be $script:PinnedVersion -Because "$(Split-Path $file -Leaf) must not drift from CI's pin"
            }
        }
    }

    It 'does not read a pin out of a COMMENT: <Kind>' -ForEach @(
        @{ Kind = 'PowerShell'; Ext = '.ps1'
           Text = "# Historical note: this gate ran Pester 5.8.0 until the 6.1.0 migration.`nImport-Module Pester -RequiredVersion 6.1.0" }
        @{ Kind = 'YAML'; Ext = '.yml'
           Text = "  # was Pester 5.8.0 before the migration`n  run: Import-Module Pester -RequiredVersion 6.1.0" }
        @{ Kind = 'justfile (no extension)'; Ext = ''
           Text = "# historically Pester 5.8.0`npester:`n    Import-Module Pester -RequiredVersion 6.1.0; Invoke-Pester" }
    ) {
        # A version named in a comment is prose. This has now regressed twice, in opposite directions:
        # once the stripper was missing, once it was present but wired to the sibling scan instead.
        $found = @(Get-PesterVersionsNamed -Text $Text -Extension $Ext)
        $found | Should -Not -Contain '5.8.0' -Because "the 5.8.0 sits in a $Kind comment"
        $found | Should -Contain '6.1.0'      -Because 'the real pin must still be seen'
    }

    It 'reads a Markdown heading as prose, not as a pin' {
        # .md is deliberately NOT comment-stripped: `#` opens a heading, and the code rule would blank
        # every heading in the file. Only -RequiredVersion counts there, so a heading naming a version
        # is inert either way.
        $md = "# Pester 5.8.0 was the old pin`n`nRun ``Import-Module Pester -RequiredVersion 6.1.0``."
        $found = @(Get-PesterVersionsNamed -Text $md -Extension '.md')
        $found | Should -Not -Contain '5.8.0'
        $found | Should -Contain '6.1.0'
    }

    It 'wires BOTH scans through the comment stripper' {
        # The WIRING contract, and the only guard that would have caught the regression this test was
        # written for. The unit tests above prove the stripper is correct; they cannot prove the scans
        # CALL it. Round 3 added stripping to the version scan, round 4 moved it to the switch scan, and
        # every test stayed green because no scanned file happened to name another version in a comment.
        #
        # So: every place this file reads a live file's raw text must hand it to something that strips
        # comments. If a third scan is added, it has to opt in the same way.
        #
        # The needle is built by concatenation on purpose: written as one literal it would appear in this
        # test's own source, the scan would match its own line, and the test would fail on itself. It did,
        # first run.
        $src = Get-Content $PSCommandPath -Raw
        $needle = 'Get-Content $file' + ' -Raw'
        $reads = @(($src -split "`r?`n") | Where-Object { $_ -match [regex]::Escape($needle) })
        $reads | Should -Not -BeNullOrEmpty -Because 'the scans must still read the live files'
        foreach ($line in $reads) {
            $line | Should -Match 'Remove-CommentText|Get-PesterVersionsNamed' `
                -Because 'a raw read that bypasses the stripper is how a comment becomes a pin'
        }
    }

    It 'passes only switches that EXIST in the pinned version' {
        # The general form. -EnableExit was removed in Pester 6 and would have made both cockpit gates red
        # on green; enumerating the switches we actually use catches the next removal too.
        $available = @((Get-Command -Module (Import-Module Pester -RequiredVersion $script:PinnedVersion -PassThru) -Name Invoke-Pester).Parameters.Keys)
        $available | Should -Not -BeNullOrEmpty -Because 'the pinned Pester must be installed for this gate to mean anything'

        $used = New-Object System.Collections.Generic.HashSet[string]
        foreach ($file in $script:LiveFiles) {
            # Comments stripped HERE TOO. The version scan below got this treatment first and this one did
            # not -- the same defect class, applied to one of the two scans in the same file. MEASURED in
            # both languages: a comment naming a switch, of a kind anyone would write --
            #     # note: Invoke-Pester -EnableExit was removed in Pester 6
            # -- was read as a live switch and failed the gate. Fixing one scan and not its sibling is how
            # the class survives; the shared helper is the point.
            $text = Remove-CommentText -Text (Get-Content $file -Raw) -Extension ([IO.Path]::GetExtension($file))
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
