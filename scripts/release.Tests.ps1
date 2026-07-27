# scripts/release.Tests.ps1
BeforeAll {
    $script:Repo = Split-Path -Parent $PSScriptRoot
    $script:Lib  = Join-Path $PSScriptRoot 'lib/release-lib.ps1'
    . $script:Lib

    # Source with every comment removed, via the PowerShell tokenizer.
    #
    # The source-level contract tests below pin behaviour that lives in files this suite cannot dot-source
    # (release.ps1 executes on load). Matching raw text for them is a trap that has now bitten five times on
    # this branch: a line-comment, then an inline comment, then a block comment each left the pinned string
    # present while the code it named was dead. Stripping comment tokens kills all three forms at once --
    # a match against this text is necessarily a match against executable code.
    function script:Get-CodeWithoutComments {
        param([Parameter(Mandatory)][string]$Path)
        $tokens = $null
        [void][System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$null)
        # Here-strings go too: wrapping code in @'...'@ is the same "disable it but leave the text visible"
        # move as a block comment. Ordinary string tokens must stay -- pinned values like '1' and '^###' ARE
        # strings. That leaves one acknowledged limit: text deliberately parked in a dead single-line string
        # would still match. These guard against regression and casual removal, not determined sabotage.
        ($tokens |
            Where-Object { $_.Kind -ne 'Comment' -and $_.Kind -notlike 'HereString*' } |
            ForEach-Object { $_.Text }) -join ' '
    }
}

Describe 'release-lib.ps1 harness' {
    It 'dot-sources without error' {
        { . $script:Lib } | Should -Not -Throw
    }
}

Describe 'Get-NextVersion' {
    It 'bumps minor on a feat commit' {
        $r = Get-NextVersion -CurrentVersion '0.16.2' -CommitMessages @('feat(release): add release automation script')
        $r.Version | Should -Be '0.17.0'
        $r.Level   | Should -Be 'minor'
        $r.Trigger | Should -Be 'feat'
        $r.NothingToRelease | Should -BeFalse
    }

    It 'bumps patch on a fix commit' {
        $r = Get-NextVersion -CurrentVersion '0.16.2' -CommitMessages @('fix(server): correct stdout leak')
        $r.Version | Should -Be '0.16.3'
        $r.Level   | Should -Be 'patch'
        $r.Trigger | Should -Be 'fix'
    }

    It 'feat beats fix when both are present in range' {
        $r = Get-NextVersion -CurrentVersion '0.16.2' -CommitMessages @('fix(server): tweak', 'feat(tools): add desktop_foo')
        $r.Level   | Should -Be 'minor'
        $r.Trigger | Should -Be 'feat'
    }

    It 'a bang breaking change bumps minor while pre-1.0' {
        $r = Get-NextVersion -CurrentVersion '0.16.2' -CommitMessages @('feat!: rework the CLI flag surface')
        $r.Version | Should -Be '0.17.0'
        $r.Level   | Should -Be 'minor'
        $r.Trigger | Should -Be 'breaking'
    }

    It 'a bang breaking change bumps major once at 1.0+' {
        $r = Get-NextVersion -CurrentVersion '1.2.3' -CommitMessages @('fix!: remove the legacy endpoint')
        $r.Version | Should -Be '2.0.0'
        $r.Level   | Should -Be 'major'
        $r.Trigger | Should -Be 'breaking'
    }

    It 'a BREAKING CHANGE footer (no bang) counts as breaking' {
        $msg = "feat(core): swap the ref-resolution algorithm`n`nBREAKING CHANGE: ref tokens minted before this release are invalid."
        $r = Get-NextVersion -CurrentVersion '0.16.2' -CommitMessages @($msg)
        $r.Level   | Should -Be 'minor'
        $r.Trigger | Should -Be 'breaking'
    }

    It 'reports nothing to release when only docs/chore commits are in range' {
        $r = Get-NextVersion -CurrentVersion '0.16.2' -CommitMessages @('docs: tidy the operator manual', 'chore: bump a dependency')
        $r.NothingToRelease | Should -BeTrue
        $r.Version | Should -BeNullOrEmpty
    }

    It 'reports nothing to release on an empty commit range' {
        $r = Get-NextVersion -CurrentVersion '0.16.2' -CommitMessages @()
        $r.NothingToRelease | Should -BeTrue
    }

    It 'counts non-conventional commit subjects without affecting the level' {
        $r = Get-NextVersion -CurrentVersion '0.16.2' -CommitMessages @('fix(server): correct stdout leak', 'oops forgot the prefix')
        $r.NonConventionalCount | Should -Be 1
        $r.Level | Should -Be 'patch'
    }

    It '-OverrideVersion wins regardless of commits' {
        $r = Get-NextVersion -CurrentVersion '0.16.2' -CommitMessages @() -OverrideVersion '0.16.2'
        $r.Version | Should -Be '0.16.2'
        $r.Level   | Should -Be 'override'
        $r.NothingToRelease | Should -BeFalse
    }

    It '-OverrideBump computes from CurrentVersion' {
        $r = Get-NextVersion -CurrentVersion '0.16.2' -CommitMessages @() -OverrideBump 'patch'
        $r.Version | Should -Be '0.16.3'
        $r.Level   | Should -Be 'patch'
    }

    It 'treats an empty-string -OverrideBump as no override (the unset-orchestrator-param idiom)' {
        # release.ps1 passes its $Bump verbatim; an unset [string] param is '' (not $null). '' must mean "no override".
        $r = Get-NextVersion -CurrentVersion '0.16.2' -CommitMessages @('feat: add x') -OverrideBump ''
        $r.Version | Should -Be '0.17.0'
        $r.Level   | Should -Be 'minor'
    }

    It 'treats an empty-string -OverrideVersion as no override' {
        $r = Get-NextVersion -CurrentVersion '0.16.2' -CommitMessages @('fix: y') -OverrideVersion ''
        $r.Version | Should -Be '0.16.3'
        $r.Level   | Should -Be 'patch'
    }

    It 'still rejects a non-empty invalid -OverrideBump' {
        { Get-NextVersion -CurrentVersion '0.16.2' -CommitMessages @() -OverrideBump 'bogus' } | Should -Throw
    }
}

Describe 'Get-VersionsInSync and Set-ProjectVersion' {
    BeforeEach {
        $script:VerSandbox = Join-Path ([IO.Path]::GetTempPath()) ("verbox_" + [guid]::NewGuid())
        New-Item -ItemType Directory -Force -Path (Join-Path $VerSandbox 'src/FlaUI.Mcp.Server') | Out-Null
        New-Item -ItemType Directory -Force -Path (Join-Path $VerSandbox 'installer') | Out-Null
        New-Item -ItemType Directory -Force -Path (Join-Path $VerSandbox 'plugins/flaui-mcp/.claude-plugin') | Out-Null
        @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>flaui-mcp</AssemblyName>
    <Version>0.16.2</Version>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  </PropertyGroup>
</Project>
'@ | Set-Content (Join-Path $VerSandbox 'src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj')
        @'
#define AppName "FlaUI.Mcp"
#define AppVersion "0.16.2"
#define ExeName "flaui-mcp.exe"
'@ | Set-Content (Join-Path $VerSandbox 'installer/flaui-mcp.iss')
        '{"displayName": "flaui-mcp driving", "version": "0.16.2", "author": {"name": "Costas Kirgoussios"}}' |
            Set-Content (Join-Path $VerSandbox 'plugins/flaui-mcp/.claude-plugin/plugin.json')
    }
    AfterEach { if (Test-Path $script:VerSandbox) { Remove-Item -Recurse -Force $script:VerSandbox } }

    It 'reports InSync=true when all 3 files agree' {
        $r = Get-VersionsInSync -RepoRoot $VerSandbox
        $r.InSync | Should -BeTrue
        $r.Versions.Csproj | Should -Be '0.16.2'
        $r.Versions.Iss    | Should -Be '0.16.2'
        $r.Versions.Plugin | Should -Be '0.16.2'
        $r.Message | Should -Match '0\.16\.2'   # full version, not just first char '0' (scalar-index regression guard)
    }

    It 'reports InSync=false and names the drift when one file disagrees' {
        (Get-Content (Join-Path $VerSandbox 'installer/flaui-mcp.iss') -Raw) -replace '0\.16\.2', '0.16.3' |
            Set-Content (Join-Path $VerSandbox 'installer/flaui-mcp.iss')
        $r = Get-VersionsInSync -RepoRoot $VerSandbox
        $r.InSync | Should -BeFalse
        $r.Message | Should -Match '0\.16\.3'
    }

    It 'Set-ProjectVersion rewrites all 3 files and preserves surrounding content' {
        Set-ProjectVersion -RepoRoot $VerSandbox -Version '0.17.0'
        $r = Get-VersionsInSync -RepoRoot $VerSandbox
        $r.InSync | Should -BeTrue
        $r.Versions.Csproj | Should -Be '0.17.0'
        (Get-Content (Join-Path $VerSandbox 'src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj') -Raw) | Should -Match '<AssemblyName>flaui-mcp</AssemblyName>'
        (Get-Content (Join-Path $VerSandbox 'installer/flaui-mcp.iss') -Raw) | Should -Match '#define AppName "FlaUI.Mcp"'
        (Get-Content (Join-Path $VerSandbox 'plugins/flaui-mcp/.claude-plugin/plugin.json') -Raw) | Should -Match 'Costas Kirgoussios'
    }

    It 'Get-VersionsInSync throws when a version file is missing' {
        Remove-Item (Join-Path $VerSandbox 'installer/flaui-mcp.iss')
        { Get-VersionsInSync -RepoRoot $VerSandbox } | Should -Throw
    }

    It 'preserves each version file''s original BOM state' {
        $csprojPath = Join-Path $VerSandbox 'src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj'
        $issPath    = Join-Path $VerSandbox 'installer/flaui-mcp.iss'

        $originalCsprojText = Get-Content $csprojPath -Raw
        [System.IO.File]::WriteAllText($csprojPath, $originalCsprojText, [System.Text.UTF8Encoding]::new($true))

        $originalIssText = Get-Content $issPath -Raw
        [System.IO.File]::WriteAllText($issPath, $originalIssText, [System.Text.UTF8Encoding]::new($false))

        Set-ProjectVersion -RepoRoot $VerSandbox -Version '0.17.0'

        $csprojBytes = [System.IO.File]::ReadAllBytes($csprojPath)
        $csprojHasBom = $csprojBytes.Length -ge 3 -and $csprojBytes[0] -eq 0xEF -and $csprojBytes[1] -eq 0xBB -and $csprojBytes[2] -eq 0xBF
        $csprojHasBom | Should -BeTrue
        (Get-Content $csprojPath -Raw) | Should -Match '<Version>0\.17\.0</Version>'

        $issBytes = [System.IO.File]::ReadAllBytes($issPath)
        $issHasBom = $issBytes.Length -ge 3 -and $issBytes[0] -eq 0xEF -and $issBytes[1] -eq 0xBB -and $issBytes[2] -eq 0xBF
        $issHasBom | Should -BeFalse
        (Get-Content $issPath -Raw) | Should -Match '#define AppVersion "0\.17\.0"'
    }
}

Describe 'Get-TopChangelogSection' {
    BeforeEach {
        $script:ClPath = Join-Path ([IO.Path]::GetTempPath()) ("changelog_" + [guid]::NewGuid() + '.md')
        @'
# Changelog

All notable changes to this project are documented here.

## [0.2.0] - 2026-07-20

### Added
- Thing two.

## [0.1.0] - 2026-07-01

### Added
- Thing one.
'@ | Set-Content $ClPath
    }
    AfterEach { Remove-Item $script:ClPath -Force -ErrorAction SilentlyContinue }

    It 'extracts only the top section by default' {
        $top = Get-TopChangelogSection -ChangelogPath $ClPath
        $top | Should -Match '^## \[0\.2\.0\] - 2026-07-20'
        $top | Should -Match 'Thing two\.'
        $top | Should -Not -Match 'Thing one\.'
        $top | Should -Not -Match '## \[0\.1\.0\]'
    }

    It 'returns the last 2 sections with -Count 2' {
        $top2 = Get-TopChangelogSection -ChangelogPath $ClPath -Count 2
        $top2 | Should -Match 'Thing two\.'
        $top2 | Should -Match 'Thing one\.'
    }

    It 'throws when the file has no ## [ section' {
        $empty = Join-Path ([IO.Path]::GetTempPath()) ("empty_" + [guid]::NewGuid() + '.md')
        '# Changelog' | Set-Content $empty
        { Get-TopChangelogSection -ChangelogPath $empty } | Should -Throw
        Remove-Item $empty -Force
    }
}

Describe 'Add-ChangelogSection' {
    BeforeEach {
        $script:ClPath = Join-Path ([IO.Path]::GetTempPath()) ("changelog_" + [guid]::NewGuid() + '.md')
        @'
# Changelog

All notable changes to this project are documented here.

## [0.16.1] - 2026-07-18

### Fixed
- Something.
'@ | Set-Content $ClPath
    }
    AfterEach { Remove-Item $script:ClPath -Force -ErrorAction SilentlyContinue }

    It 'prepends the new section above the existing ones, preserving the preamble' {
        Add-ChangelogSection -ChangelogPath $ClPath -Version '0.17.0' -Body "### Added`n- New thing." -Date (Get-Date '2026-07-19')
        $content = Get-Content $ClPath -Raw
        $content | Should -Match '(?s)# Changelog.*## \[0\.17\.0\] - 2026-07-19.*### Added.*New thing\..*## \[0\.16\.1\] - 2026-07-18'
    }

    It 'aborts (throws) when the target version heading already exists' {
        Add-ChangelogSection -ChangelogPath $ClPath -Version '0.17.0' -Body "### Added`n- New thing." -Date (Get-Date '2026-07-19')
        { Add-ChangelogSection -ChangelogPath $ClPath -Version '0.17.0' -Body "### Added`n- Different." -Date (Get-Date '2026-07-19') } | Should -Throw
    }

    It 'appends a section when the file has no existing sections yet' {
        $empty = Join-Path ([IO.Path]::GetTempPath()) ("emptycl_" + [guid]::NewGuid() + '.md')
        '# Changelog' | Set-Content $empty
        Add-ChangelogSection -ChangelogPath $empty -Version '0.1.0' -Body "### Added`n- First." -Date (Get-Date '2026-07-01')
        (Get-Content $empty -Raw) | Should -Match '## \[0\.1\.0\] - 2026-07-01'
        Remove-Item $empty -Force
    }

    It 'does not duplicate the file when the CHANGELOG has no preamble before the first section' {
        $noPreamble = Join-Path ([IO.Path]::GetTempPath()) ("nopreamble_" + [guid]::NewGuid() + '.md')
        @'
## [0.1.0] - 2026-01-01

### Added
- First thing.
'@ | Set-Content $noPreamble

        Add-ChangelogSection -ChangelogPath $noPreamble -Version '0.2.0' -Body "### Added`n- new thing" -Date (Get-Date '2026-07-19')
        $result = Get-Content $noPreamble -Raw

        $result | Should -Match '## \[0\.2\.0\] - 2026-07-19'
        ([regex]::Matches($result, '\[0\.1\.0\]')).Count | Should -Be 1
        $result.IndexOf('[0.2.0]') | Should -BeLessThan $result.IndexOf('[0.1.0]')

        Remove-Item $noPreamble -Force
    }
}

Describe 'Get-ChangelogPrompt' {
    BeforeEach {
        $script:Commits = @('feat(release): add release script', 'fix(server): correct a leak')
        $script:Exemplar = "## [0.16.1] - 2026-07-18`n`n### Fixed`n- Something."
    }

    It 'uses the full diff when under the size/commit thresholds' {
        $p = Get-ChangelogPrompt -Version '0.17.0' -CommitMessages $Commits -DiffText 'small diff' -DiffStatText 'stat' -StyleExemplar $Exemplar
        $p | Should -Match 'Full diff:'
        $p | Should -Not -Match 'Diff stat'
    }

    It 'degrades to the diff stat when the diff exceeds the size threshold' {
        $bigDiff = 'x' * 200000
        $p = Get-ChangelogPrompt -Version '0.17.0' -CommitMessages $Commits -DiffText $bigDiff -DiffStatText 'stat summary' -StyleExemplar $Exemplar
        $p | Should -Match 'Diff stat'
        $p | Should -Not -Match 'Full diff:'
    }

    It 'degrades to the diff stat when the commit count exceeds the threshold' {
        $manyCommits = 1..41 | ForEach-Object { "fix(x): change $_" }
        $p = Get-ChangelogPrompt -Version '0.17.0' -CommitMessages $manyCommits -DiffText 'small' -DiffStatText 'stat summary' -StyleExemplar $Exemplar
        $p | Should -Match 'Diff stat'
    }

    It 'instructs body-only output and includes the style exemplar and commit list' {
        $p = Get-ChangelogPrompt -Version '0.17.0' -CommitMessages $Commits -DiffText 'd' -DiffStatText 's' -StyleExemplar $Exemplar
        # Wording changed when the body moved inside <changelog> tags; the contract it pins did not.
        $p | Should -Match 'ONLY the body sections'
        $p | Should -Match '(?s)no.*heading'
        $p | Should -Match ([regex]::Escape($Exemplar))
        $p | Should -Match '- feat\(release\): add release script'
        $p | Should -Match '- fix\(server\): correct a leak'
    }
}

Describe 'Invoke-Gate' {
    BeforeEach {
        $script:GateSandbox = Join-Path ([IO.Path]::GetTempPath()) ("gatebox_" + [guid]::NewGuid())
        New-Item -ItemType Directory -Force -Path (Join-Path $GateSandbox 'src/FlaUI.Mcp.Server') | Out-Null
        New-Item -ItemType Directory -Force -Path (Join-Path $GateSandbox 'installer') | Out-Null
        New-Item -ItemType Directory -Force -Path (Join-Path $GateSandbox 'plugins/flaui-mcp/.claude-plugin') | Out-Null
        '<Project><PropertyGroup><Version>0.1.0</Version></PropertyGroup></Project>' | Set-Content (Join-Path $GateSandbox 'src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj')
        '#define AppVersion "0.1.0"' | Set-Content (Join-Path $GateSandbox 'installer/flaui-mcp.iss')
        '{"version": "0.1.0"}' | Set-Content (Join-Path $GateSandbox 'plugins/flaui-mcp/.claude-plugin/plugin.json')
        $script:Pass = { param($Root) $global:LASTEXITCODE = 0; 'ok' }
    }
    AfterEach { if (Test-Path $script:GateSandbox) { Remove-Item -Recurse -Force $script:GateSandbox } }

    It 'passes when all 4 checks pass' {
        $result = Invoke-Gate -RepoRoot $GateSandbox -BuildCheck $Pass -TestCheck $Pass -PluginDriftCheck $Pass
        $result.Passed | Should -BeTrue
        ($result.Checks | Where-Object Name -eq 'Build').Passed       | Should -BeTrue
        ($result.Checks | Where-Object Name -eq 'Test').Passed        | Should -BeTrue
        ($result.Checks | Where-Object Name -eq 'VersionSync').Passed | Should -BeTrue
        ($result.Checks | Where-Object Name -eq 'PluginDrift').Passed | Should -BeTrue
    }

    It 'fails overall when the build check fails' {
        $fail = { param($Root) $global:LASTEXITCODE = 1; 'error: something broke' }
        $result = Invoke-Gate -RepoRoot $GateSandbox -BuildCheck $fail -TestCheck $Pass -PluginDriftCheck $Pass
        $result.Passed | Should -BeFalse
        ($result.Checks | Where-Object Name -eq 'Build').Passed | Should -BeFalse
    }

    It 'fails VersionSync when the 3 files disagree' {
        '#define AppVersion "0.2.0"' | Set-Content (Join-Path $GateSandbox 'installer/flaui-mcp.iss')
        $result = Invoke-Gate -RepoRoot $GateSandbox -BuildCheck $Pass -TestCheck $Pass -PluginDriftCheck $Pass
        $result.Passed | Should -BeFalse
        ($result.Checks | Where-Object Name -eq 'VersionSync').Passed | Should -BeFalse
    }

    It 'treats a nonzero warning count as a build failure even when exit code is 0' {
        $warnBuild = { param($Root) $global:LASTEXITCODE = 0; "Build succeeded.`n    2 Warning(s)`n    0 Error(s)" }
        $result = Invoke-Gate -RepoRoot $GateSandbox -BuildCheck $warnBuild -TestCheck $Pass -PluginDriftCheck $Pass
        ($result.Checks | Where-Object Name -eq 'Build').Passed | Should -BeFalse
    }

    It 'passes the build check on a real 0-warning summary line' {
        $cleanBuild = { param($Root) $global:LASTEXITCODE = 0; "Build succeeded.`n    0 Warning(s)`n    0 Error(s)" }
        $result = Invoke-Gate -RepoRoot $GateSandbox -BuildCheck $cleanBuild -TestCheck $Pass -PluginDriftCheck $Pass
        ($result.Checks | Where-Object Name -eq 'Build').Passed | Should -BeTrue
    }

    It 'fails overall when the plugin-drift check fails' {
        $fail = { param($Root) $global:LASTEXITCODE = 1; 'drift detected' }
        $result = Invoke-Gate -RepoRoot $GateSandbox -BuildCheck $Pass -TestCheck $Pass -PluginDriftCheck $fail
        $result.Passed | Should -BeFalse
        ($result.Checks | Where-Object Name -eq 'PluginDrift').Passed | Should -BeFalse
    }

    It 'skips the PluginDrift check entirely when -SkipPluginDrift is passed' {
        $result = Invoke-Gate -RepoRoot $GateSandbox -BuildCheck $Pass -TestCheck $Pass -SkipPluginDrift
        ($result.Checks | Where-Object Name -eq 'PluginDrift') | Should -BeNullOrEmpty
        $result.Passed | Should -BeTrue
    }
}

Describe 'Get-ChangelogBodyFromLlmOutput' {
    It 'extracts a clean delimited body' {
        $raw = "<changelog>`n### Added`n- A thing.`n</changelog>"
        Get-ChangelogBodyFromLlmOutput -RawOutput $raw | Should -Be "### Added`n- A thing."
    }

    It 'discards conversational chatter that follows the body' {
        # Regression: Invoke-DraftReview's '^###\s' guard is a PARTIAL match, so a valid body with a chatty
        # trailer passed validation and the trailer was committed verbatim into CHANGELOG.md.
        $raw = "<changelog>`n### Fixed`n- A bug.`n</changelog>`n`nLet me know if you'd like me to adjust the wording!"
        Get-ChangelogBodyFromLlmOutput -RawOutput $raw | Should -Be "### Fixed`n- A bug."
    }

    It 'discards preamble that precedes the body' {
        $raw = "Sure — here is the changelog you asked for:`n`n<changelog>`n### Changed`n- A change.`n</changelog>"
        Get-ChangelogBodyFromLlmOutput -RawOutput $raw | Should -Be "### Changed`n- A change."
    }

    It 'discards stderr folded in by 2>&1' {
        $raw = "Warning: an update to claude is available`n<changelog>`n### Added`n- A thing.`n</changelog>"
        Get-ChangelogBodyFromLlmOutput -RawOutput $raw | Should -Be "### Added`n- A thing."
    }

    It 'returns null for the live v0.19.0 contamination (a hook reply, no tags at all)' {
        # Verbatim from the run that blocked v0.19.0: a Stop hook fired inside `claude -p`, the model answered
        # the hook instead of stopping, and that reply became the last message -- i.e. the "changelog".
        $raw = "The CHANGELOG.md body for v0.19.0 is ready above. I see the flaui-autotrain inbox has pending observations - I can run flaui-curate when you'd like, or proceed with whatever's next for the release."
        Get-ChangelogBodyFromLlmOutput -RawOutput $raw | Should -BeNullOrEmpty
    }

    It 'returns null for an untagged body that WOULD pass the "### " guard' {
        # The case above is rejected by the '### ' guard alone, so on its own it does not prove the TAGS are
        # doing any work -- deleting the extraction keeps it green. This one cannot pass without them.
        Get-ChangelogBodyFromLlmOutput -RawOutput "### Added`n- A perfectly good body the model forgot to tag." |
            Should -BeNullOrEmpty
    }

    It 'keeps a body that contains the literal closing tag' {
        # A release that changes the tag contract describes the tags in its own changelog. A non-greedy close
        # truncated the body at the inner tag, and the fragment still held a '### ' heading -- so it passed
        # every downstream check and would have shipped silently truncated.
        $raw = "<changelog>`n### Changed`n- The body is now wrapped in </changelog> tags.`n- A second bullet.`n</changelog>"
        Get-ChangelogBodyFromLlmOutput -RawOutput $raw |
            Should -Be "### Changed`n- The body is now wrapped in </changelog> tags.`n- A second bullet."
    }

    It 'returns null when the tags are present but hold no "### " section' {
        $raw = "<changelog>`nNothing much changed this release.`n</changelog>"
        Get-ChangelogBodyFromLlmOutput -RawOutput $raw | Should -BeNullOrEmpty
    }

    It 'returns null when the tags are empty' {
        Get-ChangelogBodyFromLlmOutput -RawOutput "<changelog></changelog>" | Should -BeNullOrEmpty
    }

    It 'returns null when the closing tag is missing' {
        Get-ChangelogBodyFromLlmOutput -RawOutput "<changelog>`n### Added`n- A thing." | Should -BeNullOrEmpty
    }

    It 'returns null on empty, whitespace and null input' {
        Get-ChangelogBodyFromLlmOutput -RawOutput ''      | Should -BeNullOrEmpty
        Get-ChangelogBodyFromLlmOutput -RawOutput "  `n " | Should -BeNullOrEmpty
        Get-ChangelogBodyFromLlmOutput -RawOutput $null   | Should -BeNullOrEmpty
    }

    It 'ignores a bare closing tag that appears in trailing chatter' {
        # The mirror of the truncation case, and the edge the truncation fix itself spawned: closing at the
        # LAST tag swallowed the real close plus the chatter, and the result still held a '### ' heading, so
        # nothing downstream caught it. Closing at the first LINE-ANCHORED tag settles both.
        $raw = "<changelog>`n### Added`n- A thing.`n</changelog>`n`nHope that helps! I used the </changelog> tag as requested."
        Get-ChangelogBodyFromLlmOutput -RawOutput $raw | Should -Be "### Added`n- A thing."
    }

    It 'refuses an inline closing tag when none is line-anchored' {
        # SUPERSEDES a round-8 behaviour, deliberately. The inline-close fallback was accepted then, on the
        # reading that a model closing on the same line as its final bullet is unambiguous. It is not: with the
        # open anchored and the anchored close simply forgotten, a PROSE mention of the closing tag is the last
        # inline candidate, so the body is truncated at the TAIL -- and unlike a head-truncation, the fragment
        # still STARTS with a valid '### ' heading, so the start-anchored gate below cannot see it. There is no
        # backstop on that side, so the fallback goes. Costs a recoverable run; costs it LOUDLY.
        Get-ChangelogBodyFromLlmOutput -RawOutput "<changelog>`n### Added`n- A thing.</changelog>" |
            Should -BeNullOrEmpty
    }

    It 'keeps a body whose prose mentions the OPENING tag before a later section' {
        # THE LIVE v0.19.0 DEFECT, verbatim in shape. The opening scan matched ANY occurrence and walked
        # newest-first, so this body's own prose mention of the tag -- present because the release documented
        # the tag contract -- became the start. The head was sliced mid-sentence, but the tail still held the
        # LATER '### Changed' heading, so the multiline gate passed it and it shipped: committed, tagged, and
        # published to the GitHub release notes. Anchoring the opening scan is what makes the mention inert.
        $raw = "<changelog>`n### Fixed`n- A body that mentions <changelog> is correctly accepted.`n`n### Changed`n- A change.`n</changelog>"
        Get-ChangelogBodyFromLlmOutput -RawOutput $raw |
            Should -Be "### Fixed`n- A body that mentions <changelog> is correctly accepted.`n`n### Changed`n- A change."
    }

    It 'refuses an inline opening tag that is only a prose example' {
        # The mirror hazard, and why the opening scan gets NO inline fallback. The model forgets the real
        # opening tag but shows one inside a prose example; the example's tail begins with a real heading, so
        # a fallback start would pass the gate and ship the example's INVENTED content as the changelog.
        $raw = "Avoid formatting like this:`n``<changelog>### Added`n- Some fake feature```n`nHere is the real one (I forgot the opening tag):`n`n### Fixed`n- The actual bug.`n</changelog>"
        Get-ChangelogBodyFromLlmOutput -RawOutput $raw | Should -BeNullOrEmpty
    }

    It 'extracts a body whose opening tag is preceded by a UTF-8 BOM' {
        # Regression introduced BY the anchoring fix and caught in capstone round 1: '(?m)^' matches index 0,
        # but a BOM sits between that anchor and the tag, so the only opening tag becomes invisible and a
        # perfectly good body is refused into $EDITOR. The old unanchored scan was immune (measured), so this
        # is a cost the fix must not carry. Only the OPEN needs it: a BOM can only appear at offset 0.
        $raw = "$([char]0xFEFF)<changelog>`n### Added`n- A thing.`n</changelog>"
        Get-ChangelogBodyFromLlmOutput -RawOutput $raw | Should -Be "### Added`n- A thing."
    }

    It 'refuses a head-truncated fragment even when a later heading survives' {
        # The gate itself, independent of which tag scan produced the fragment. '(?m)^###\s' asserts only that
        # a heading exists SOMEWHERE, which is what let the v0.19.0 fragment through; a changelog body's very
        # first content must BE a heading.
        $raw = "<changelog>`n is correctly accepted. Trailing chatter is refused.`n`n### Changed`n- A change.`n</changelog>"
        Get-ChangelogBodyFromLlmOutput -RawOutput $raw | Should -BeNullOrEmpty
    }

    It 'refuses to guess when the body itself holds a line-anchored closing tag' {
        # The residual of the residual: no delimiter scheme survives self-reference. Guessing would truncate
        # silently and the fragment keeps its '### ' heading, so nothing downstream would catch it. Failing
        # sends the caller to $EDITOR with the raw output in the Reason -- loud, and recoverable.
        $raw = "<changelog>`n### Changed`n- The closing tag is now:`n</changelog>`n- and that is all.`n</changelog>"
        Get-ChangelogBodyFromLlmOutput -RawOutput $raw | Should -BeNullOrEmpty
    }

    It 'refuses when a second opening tag sits outside the body' {
        # Superseded a round-3 behaviour, deliberately. This input was recovered then, on the reading that the
        # second tag is only chatter. But a cut-off second draft looks exactly the same, and recovering there
        # ships a body the model had already replaced -- silently. Nothing in the text separates the two, so
        # both now fail loudly into $EDITOR with the raw output in hand.
        $raw = "<changelog>`n### Added`n- A thing.</changelog>`nNote: the <changelog> wrapper is required."
        Get-ChangelogBodyFromLlmOutput -RawOutput $raw | Should -BeNullOrEmpty
    }

    It 'refuses a stale older block when a later draft was cut off unclosed' {
        $raw = "<changelog>`n### Stale`n- old.`n</changelog>`n<changelog>`n### Cut off mid"
        Get-ChangelogBodyFromLlmOutput -RawOutput $raw | Should -BeNullOrEmpty
    }

    It 'refuses when trailing chatter names both tags' {
        $raw = "<changelog>`n### Added`n- A.`n</changelog>`nI wrapped it in <changelog>...</changelog> tags!"
        Get-ChangelogBodyFromLlmOutput -RawOutput $raw | Should -BeNullOrEmpty
    }

    It 'does not promote a stale sibling block when the newest one is heading-less' {
        # Stepping outward is only right for a NESTED false start. An older sibling is a draft the model has
        # since replaced; silently promoting it discards the model's final intent and ships a stale changelog.
        $raw = "<changelog>`n### Stale`n- old.`n</changelog>`n<changelog>forgot heading</changelog>"
        Get-ChangelogBodyFromLlmOutput -RawOutput $raw | Should -BeNullOrEmpty
        # ...and not even when the stale block happens to name the tag in its own prose, which an earlier
        # string-containment version of this rule was fooled by. Position decides, not content.
        $withTag = "<changelog>`n### Stale`n- We now use <changelog>.`n</changelog>`n<changelog>forgot heading</changelog>"
        Get-ChangelogBodyFromLlmOutput -RawOutput $withTag | Should -BeNullOrEmpty
    }

    It 'recovers a body that mentions the OPENING tag in prose' {
        # The innermost tag is a false start: its tail yields a heading-less fragment. Giving up there would
        # reject a good body, so the walk validates each candidate and steps outward on failure.
        $raw = "<changelog>`n### Added`n- Use <changelog> to wrap the body.`n</changelog>"
        Get-ChangelogBodyFromLlmOutput -RawOutput $raw | Should -Be "### Added`n- Use <changelog> to wrap the body."
    }

    It 'takes the last complete pair when the model self-corrects' {
        $raw = "<changelog>`n### Added`n- Draft one.`n</changelog>`nOn reflection:`n<changelog>`n### Added`n- Draft two.`n</changelog>"
        Get-ChangelogBodyFromLlmOutput -RawOutput $raw | Should -Be "### Added`n- Draft two."
    }

    It 'strips a code fence the model wrapped inside the tags' {
        $raw = "<changelog>`n``````markdown`n### Added`n- A thing.`n```````n</changelog>"
        Get-ChangelogBodyFromLlmOutput -RawOutput $raw | Should -Be "### Added`n- A thing."
    }
}

Describe 'Get-ChangelogPrompt delimiter contract' {
    It 'instructs the model to wrap the body in <changelog> tags' {
        $p = Get-ChangelogPrompt -Version '9.9.9' -CommitMessages @('feat: a thing') -StyleExemplar 'exemplar'
        $p | Should -BeLike '*<changelog>*'
        $p | Should -BeLike '*</changelog>*'
        # The extractor drops everything outside the tags, so the prompt must say the tags are mandatory.
        $p | Should -BeLike '*discarded*'
    }
}

Describe 'Headless isolation contract' {
    # The primary fix (running the draft with hooks disabled) lives in release.ps1 and the nudge hook, neither
    # of which this suite can dot-source -- release.ps1 executes on load. These pin the contract at the source
    # level so it cannot be dropped silently; the behavioural proof is the live run recorded in the commit.
    It 'invokes claude with --safe-mode, and never with --bare' {
        # Scope to the invocation lines: the surrounding comment names --bare precisely to warn it off, so a
        # whole-file 'Should -Not -Match' would fail on the warning rather than on a real invocation.
        # Strip each line at its first '#', else the invocation survives as a trailing comment and this stays
        # green with safe-mode disabled -- both the whole-line and inline forms of that trap were measured.
        # Token text is joined with single spaces, so match the flattened invocation rather than a line.
        $code = Get-CodeWithoutComments (Join-Path $Repo 'scripts/release.ps1')
        ([regex]::Matches($code, '&\s+claude\s+-p')).Count | Should -Be 1
        $code | Should -Match '&\s+claude\s+-p\s+--safe-mode'
        # --bare would skip keychain reads and demand ANTHROPIC_API_KEY, breaking the operator's OAuth.
        $code | Should -Not -Match '--bare'
    }

    It 'sets the nudge opt-out for the child process' {
        # Must pin the ASSIGNMENT, not a mention: the comment above it names the variable, so a bare
        # 'Should -Match FLAUI_MCP_NO_NUDGE' stays green after the assignment is deleted (measured).
        $src = Get-CodeWithoutComments (Join-Path $Repo 'scripts/release.ps1')
        $src | Should -Match '\$env:FLAUI_MCP_NO_NUDGE\s*=\s*''1'''
    }

    It 'has a curate-nudge hook that honours the opt-out and CI' {
        # Same trap: pin the guards that EXIT, not the words. Both must short-circuit before the inbox read.
        $hook = Get-Content (Join-Path $Repo '.claude/hooks/flaui-curate-nudge.sh') -Raw
        $hook | Should -Match '(?m)^case\s+"\$\{FLAUI_MCP_NO_NUDGE:-\}".*exit 0.*esac$'
        $hook | Should -Match '(?m)^\[ -n "\$\{CI:-\}" \] && exit 0$'
    }

    It 'decodes the child process stdout as UTF-8' {
        # Live defect: the v0.19.0 entry shipped with 'ΓÇö' where em dashes belonged. The parent host runs
        # UTF-8, but a Start-Job runspace does NOT inherit that -- it came up on cp437, so claude's UTF-8
        # stdout was decoded with the OEM code page. Pin the ASSIGNMENT, and pin it INSIDE the job: setting it
        # in the parent is what already looked correct while the corruption shipped.
        $src = Get-CodeWithoutComments (Join-Path $Repo 'scripts/release.ps1')
        # Token text is joined with single spaces by the helper, so the operators are spaced out here.
        $assign = '\[\s*Console\s*\]\s*::\s*OutputEncoding\s*=\s*\[\s*(System\s*\.\s*)?Text\s*\.\s*Encoding\s*\]\s*::\s*UTF8'
        $src | Should -Match $assign
        $job = ([regex]::Match($src, 'Start-Job\s+-ScriptBlock\s*\{(?<body>.*?)\}\s*-ArgumentList', 'Singleline')).Groups['body'].Value
        $job | Should -Not -BeNullOrEmpty
        $job | Should -Match $assign
        # Presence is not state: a policy lock that only proves the line EXISTS stays green when a later
        # assignment overrides it. Exactly one assignment, so there is nothing to override it with.
        ([regex]::Matches($src, '\[\s*Console\s*\]\s*::\s*OutputEncoding\s*=')).Count | Should -Be 1

        # ...and text presence still is not EXECUTION. Measured: 'if ($false) { [Console]::OutputEncoding = ...}'
        # kept every regex above green while the job decoded as cp437 again. So assert against the AST that the
        # assignment is an UNCONDITIONAL statement of the job scriptblock, not buried in a branch or a loop.
        $ast = [System.Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $Repo 'scripts/release.ps1'), [ref]$null, [ref]$null)
        $assign = $ast.Find({
            param($a) $a -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                      $a.Left.Extent.Text -replace '\s', '' -eq '[Console]::OutputEncoding'
        }, $true)
        $assign | Should -Not -BeNullOrEmpty
        $conditional = @()
        for ($p = $assign.Parent; $null -ne $p; $p = $p.Parent) {
            if ($p -is [System.Management.Automation.Language.IfStatementAst] -or
                $p -is [System.Management.Automation.Language.LoopStatementAst] -or
                $p -is [System.Management.Automation.Language.TryStatementAst] -or
                $p -is [System.Management.Automation.Language.SwitchStatementAst]) { $conditional += $p.GetType().Name }
        }
        $conditional -join ',' | Should -BeNullOrEmpty
    }

    It 'pins the decode semantics the fix depends on' {
        # Why that one line is the whole fix, and why the shipped mojibake identifies the culprit code page.
        $emDash = [byte[]](0xE2, 0x80, 0x94)
        [Text.Encoding]::GetEncoding(437).GetString($emDash)   | Should -Be 'ΓÇö'   # what shipped => cp437
        # Ruled out by that signature: cp1252 (0x94 -> U+201D) and cp850 both decode these bytes differently.
        [Text.Encoding]::GetEncoding(1252).GetString($emDash)  |
            Should -Be ([string][char]0x00E2 + [char]0x20AC + [char]0x201D)
        [Text.Encoding]::GetEncoding(850).GetString($emDash)   | Should -Be 'ÔÇö'
        [Text.Encoding]::UTF8.GetString($emDash)               | Should -Be '—'
        # And the mechanism: the assignment inside the runspace is what makes the decode correct.
        $j = Start-Job -ScriptBlock { [Console]::OutputEncoding = [Text.Encoding]::UTF8; [Console]::OutputEncoding.CodePage }
        try { (Wait-Job $j | Receive-Job) | Should -Be 65001 }
        finally { Remove-Job $j -Force -ErrorAction SilentlyContinue }
    }

    It 'ships the hook to the plugin byte-identically' {
        $a = Get-Content (Join-Path $Repo '.claude/hooks/flaui-curate-nudge.sh') -Raw
        $b = Get-Content (Join-Path $Repo 'plugins/flaui-mcp/scripts/flaui-curate-nudge.sh') -Raw
        $b | Should -Be $a
    }
}

Describe 'Draft review gate' {
    # Found by a mutation sweep and independently by capstone round 1: EVERY body test targets the extractor,
    # so reverting Invoke-DraftReview's gate to '(?m)' kept all tests green while the last line of defence
    # before CHANGELOG.md silently accepted head-truncated fragments again. Pinned at the source level rather
    # than by mocking $EDITOR/Read-Host, which would hang the runner on a leaked mock.
    It 'anchors the accept gate to the START of the body' {
        $src = Get-CodeWithoutComments (Join-Path $Repo 'scripts/release.ps1')
        $src | Should -Match '\$validBody\s*=.*\$body\s+-match\s+''\^\\s\*###'
        # The mutation itself: a multiline gate anywhere in the accept path is the defect, by construction.
        $src | Should -Not -Match '\$body\s+-match\s+''\(\?m\)'
    }

    It 'keeps all three body gates consistent' {
        # Extractor, unattended resume, and interactive accept must agree; a body that one accepts and another
        # rejects is how a fragment reached CHANGELOG.md while every individual gate "looked right".
        $lib = Get-CodeWithoutComments (Join-Path $Repo 'scripts/lib/release-lib.ps1')
        $src = Get-CodeWithoutComments (Join-Path $Repo 'scripts/release.ps1')
        $lib | Should -Not -Match "'\(\?m\)\^###"
        ([regex]::Matches($src, "-match\s+'\^(\\s\*)?###")).Count | Should -BeGreaterOrEqual 2
    }
}

Describe 'Invoke-DraftReview at runtime' {
    # Capstone round 2, finding D: every gate pin above is a SOURCE-level regex, and agy named two mutations
    # that keep them green while breaking the gate ('$validBody = $true -or (...)', and burying the assignment
    # in 'if ($false) { }'). A source pin proves the text is present, never that it EXECUTES. release.ps1
    # cannot be dot-sourced (it runs on load), so lift the function out of its AST and call it for real.
    # -Yes is the key: it takes the branch that never touches Read-Host or $EDITOR, so nothing can hang.
    BeforeAll {
        $ast = [System.Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $Repo 'scripts/release.ps1'), [ref]$null, [ref]$null)
        $fn = $ast.Find({
            param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq 'Invoke-DraftReview'
        }, $true)
        if (-not $fn) { throw "Invoke-DraftReview not found in release.ps1" }
        $script:DraftReviewText = $fn.Extent.Text
    }

    It 'accepts a well-formed body unattended' {
        . ([scriptblock]::Create($script:DraftReviewText))
        $d = Join-Path ([IO.Path]::GetTempPath()) ("dr-ok-" + [guid]::NewGuid().ToString('N') + ".md")
        Set-Content -Path $d -Value "### Fixed`n- A bug." -NoNewline -Encoding UTF8
        try {
            $r = Invoke-DraftReview -Version '9.9.9' -DraftPath $d -Prompt 'x' -Yes
            $r.Action | Should -Be 'Accept'
        } finally { Remove-Item $d -Force -ErrorAction SilentlyContinue }
    }

    It 'throws on a head-truncated body unattended' {
        # THE v0.19.0 SHAPE, at runtime: begins mid-sentence, carries a later '### Changed'. Under the old
        # '(?m)' gate this returned Accept and went straight into CHANGELOG.md.
        . ([scriptblock]::Create($script:DraftReviewText))
        $d = Join-Path ([IO.Path]::GetTempPath()) ("dr-bad-" + [guid]::NewGuid().ToString('N') + ".md")
        Set-Content -Path $d -Value "` is correctly accepted. Trailing chatter.`n`n### Changed`n- A change." -NoNewline -Encoding UTF8
        try {
            { Invoke-DraftReview -Version '9.9.9' -DraftPath $d -Prompt 'x' -Yes } | Should -Throw
        } finally { Remove-Item $d -Force -ErrorAction SilentlyContinue }
    }

    It 'accepts a hand-staged draft saved with a UTF-8 BOM' {
        # I suspected a defect here and MEASURED IT AWAY: .Trim() does not strip U+FEFF (Char.IsWhiteSpace is
        # false for it), so a BOM'd draft would fail the '^\s*###\s' gate -- but Get-Content -Raw strips the
        # BOM while decoding, so it never reaches the gate. This test exists to keep that true: switching the
        # read to [IO.File]::ReadAllText, which does NOT strip it, would silently discard an operator's
        # pre-staged draft and regenerate it. A characterisation pin, not a bug fix.
        . ([scriptblock]::Create($script:DraftReviewText))
        $d = Join-Path ([IO.Path]::GetTempPath()) ("dr-bom-" + [guid]::NewGuid().ToString('N') + ".md")
        [IO.File]::WriteAllText($d, "### Fixed`n- A bug.", [Text.UTF8Encoding]::new($true))
        try {
            $r = Invoke-DraftReview -Version '9.9.9' -DraftPath $d -Prompt 'x' -Yes
            $r.Action | Should -Be 'Accept'
        } finally { Remove-Item $d -Force -ErrorAction SilentlyContinue }
    }
}

Describe 'Changelog line endings' {
    # Capstone round 2, finding C, confirmed by measurement: Out-String in the job returns the body with CRLF,
    # while Add-ChangelogSection joins with LF -- so the new entry carried a stray CRLF, and the ENTIRE existing
    # file was rewritten LF. This repo's core.autocrlf hands the next checkout a CRLF working tree, so the next
    # release would produce a whole-file diff on top of the real change.
    It 'normalises a CRLF body into an LF file' {
        $clog = Join-Path ([IO.Path]::GetTempPath()) ("eol-lf-" + [guid]::NewGuid().ToString('N') + ".md")
        [IO.File]::WriteAllText($clog, "# Changelog`n`n## [0.1.0] - 2026-01-01`n`n### Added`n- old`n", [Text.UTF8Encoding]::new($false))
        try {
            Add-ChangelogSection -ChangelogPath $clog -Version '0.2.0' -Date '2026-07-28' -Body "### Fixed`r`n- A bullet."
            $t = [IO.File]::ReadAllText($clog)
            ([regex]::Matches($t, "`r`n")).Count | Should -Be 0
        } finally { Remove-Item $clog -Force -ErrorAction SilentlyContinue }
    }

    It 'leaves a CRLF file entirely CRLF' {
        $clog = Join-Path ([IO.Path]::GetTempPath()) ("eol-crlf-" + [guid]::NewGuid().ToString('N') + ".md")
        [IO.File]::WriteAllText($clog, "# Changelog`r`n`r`n## [0.1.0] - 2026-01-01`r`n`r`n### Added`r`n- old`r`n", [Text.UTF8Encoding]::new($false))
        try {
            Add-ChangelogSection -ChangelogPath $clog -Version '0.2.0' -Date '2026-07-28' -Body "### Fixed`r`n- A bullet."
            $t = [IO.File]::ReadAllText($clog)
            ([regex]::Matches($t, "(?<!`r)`n")).Count | Should -Be 0
            ([regex]::Matches($t, "`r`n")).Count | Should -BeGreaterThan 0
        } finally { Remove-Item $clog -Force -ErrorAction SilentlyContinue }
    }
}

Describe 'Unattended draft-resume contract' {
    It 'never auto-resumes a stale draft under -Yes' {
        # A draft on disk can predate the <changelog> extraction, and resuming bypasses it. Under -Yes nothing
        # downstream inspects the body (Invoke-DraftReview auto-accepts on a PARTIAL '### ' match), so the
        # unattended path must regenerate rather than resume.
        $src = Get-Content (Join-Path $Repo 'scripts/release.ps1') -Raw
        $src | Should -Not -Match '(?m)^\s*\$resume\s*=\s*\[bool\]\$Yes'
        $src | Should -Match '(?m)^\s*\$resume\s*=\s*\$false'
    }
}

Describe 'Unattended pre-staged draft' {
    It 'gates the -Yes resume on the draft looking like a changelog body' {
        # Discarding unconditionally broke a real workflow (pre-stage interactively, finish from CI with -Yes)
        # and on the zero-commit path walked into a hard throw, since -Yes forbids the $EDITOR fallback.
        # Resuming unconditionally let a stale chatter draft ship unreviewed. The heading gate keeps both.
        $src = Get-CodeWithoutComments (Join-Path $Repo 'scripts/release.ps1')
        $src | Should -Match '\$resume\s*=\s*\$staged\s+-match\s+''\^###'
        $src | Should -Not -Match '\$resume\s*=\s*\[bool\]\$Yes'
    }

    It 'documents that gate in both the help block and the usage text' {
        # Docs that contradict behaviour are a real defect here: they tell an operator their pre-staged draft
        # will be used when it may be discarded.
        $src = (Get-Content (Join-Path $Repo 'scripts/release.ps1') -Raw)
        ([regex]::Matches($src, "starts with '### '|still starts with a '### ' heading")).Count |
            Should -BeGreaterOrEqual 2
    }
}

Describe 'Draft file edge cases' {
    It 'does not throw on a zero-byte draft under -Yes' {
        # Get-Content -Raw yields $null for an empty file (a run that died between creating and writing the
        # draft); $null.Trim() throws under ErrorActionPreference=Stop, killing the release.
        $src = Get-CodeWithoutComments (Join-Path $Repo 'scripts/release.ps1')
        $src | Should -Match '"\$\(Get-Content \$draftPath -Raw\)"'
        # Pin the semantics the guard depends on. A [string] CAST looks like the obvious fix and does NOT
        # work: casting PowerShell's no-output sentinel yields $null, not ''. Only interpolation does.
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ("draftbox_" + [guid]::NewGuid() + ".md")
        New-Item -ItemType File -Path $tmp | Out-Null
        try {
            { (Get-Content $tmp -Raw).Trim() }             | Should -Throw
            { ([string](Get-Content $tmp -Raw)).Trim() }   | Should -Throw
            { "$(Get-Content $tmp -Raw)".Trim() }          | Should -Not -Throw
            "$(Get-Content $tmp -Raw)".Trim()              | Should -BeNullOrEmpty
        } finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
    }
}

Describe 'Add-ChangelogSection edge cases' {
    BeforeEach {
        $script:Cl = Join-Path ([IO.Path]::GetTempPath()) ("cl_" + [guid]::NewGuid() + ".md")
    }
    AfterEach { Remove-Item $script:Cl -Force -ErrorAction SilentlyContinue }

    It 'preserves a one-line CHANGELOG instead of destroying it' {
        # Get-Content returns a SCALAR for a one-line file, so the range index sliced CHARACTERS: the existing
        # release section was overwritten by a single '#'. Measured before the @() fix.
        Set-Content -Path $Cl -Value '## [0.1.0] - 2026-01-01' -NoNewline
        Add-ChangelogSection -ChangelogPath $Cl -Version '0.2.0' -Body "### Added`n- New." -Date ([datetime]'2026-07-27')
        $after = Get-Content $Cl -Raw
        $after | Should -Match '(?m)^## \[0\.1\.0\] - 2026-01-01$'
        $after | Should -Match '(?m)^## \[0\.2\.0\] - 2026-07-27$'
        # New section on top, old one intact below it.
        $after.IndexOf('## [0.2.0]') | Should -BeLessThan $after.IndexOf('## [0.1.0]')
    }

    It 'refuses a duplicate version even on a different date' {
        # The guard used to match the whole heading, which carries the date, so a re-cut the next day appended
        # a second '## [X.Y.Z]' section instead of throwing. Measured: two sections, no error.
        Set-Content -Path $Cl -Value "# Changelog`n`n## [0.2.0] - 2026-07-26`n### Added`n- First cut.`n"
        { Add-ChangelogSection -ChangelogPath $Cl -Version '0.2.0' -Body "### Added`n- Second." -Date ([datetime]'2026-07-27') } |
            Should -Throw -ExpectedMessage '*already exists*'
        ([regex]::Matches((Get-Content $Cl -Raw), '(?m)^## \[0\.2\.0\]')).Count | Should -Be 1
    }

    It 'does not throw on a zero-byte CHANGELOG' {
        # Get-Content -Raw emits the no-output sentinel for an empty file and the later .TrimEnd() throws --
        # the same class as the zero-byte draft, and a [string] cast does not fix it either.
        New-Item -ItemType File -Path $Cl | Out-Null
        { Add-ChangelogSection -ChangelogPath $Cl -Version '0.2.0' -Body "### Added`n- New." -Date ([datetime]'2026-07-27') } |
            Should -Not -Throw
        (Get-Content $Cl -Raw) | Should -Match '(?m)^## \[0\.2\.0\] - 2026-07-27$'
    }

    It 'keeps a one-line file that is not a section heading' {
        Set-Content -Path $Cl -Value '# Changelog' -NoNewline
        Add-ChangelogSection -ChangelogPath $Cl -Version '0.2.0' -Body "### Added`n- New." -Date ([datetime]'2026-07-27')
        $after = Get-Content $Cl -Raw
        $after | Should -Match '(?m)^# Changelog$'
        $after | Should -Match '(?m)^## \[0\.2\.0\]'
    }

    It 'ignores a heading inside a fenced block' {
        # A fenced example documenting the changelog format shows a real '## [X.Y.Z]' at line start. It is
        # illustration, not structure -- treating it as a section blocked that version's release outright.
        $f = [string][char]0x60 * 3
        Set-Content -Path $Cl -Value "# Changelog`n`n## [0.1.0] - 2026-01-01`n### Added`n- The format is:`n${f}markdown`n## [1.0.0] - 2026-01-01`n${f}`n"
        { Add-ChangelogSection -ChangelogPath $Cl -Version '1.0.0' -Body "### Added`n- Real." -Date ([datetime]'2026-07-28') } |
            Should -Not -Throw
        # ...but a REAL section outside a fence is still refused.
        { Add-ChangelogSection -ChangelogPath $Cl -Version '0.1.0' -Body "### Added`n- Dup." -Date ([datetime]'2026-07-28') } |
            Should -Throw -ExpectedMessage '*already exists*'
    }

    It 'never inserts INTO a fenced block in the preamble' {
        # The guard and the insert point must agree. When only the guard ignored fences, the insert point still
        # landed on the fenced heading and the new entry was written inside the code block, splitting it.
        $f = [string][char]0x60 * 3
        Set-Content -Path $Cl -Value "# Changelog`n`nFormat:`n${f}markdown`n## [2.0.0] - 2026-01-01`n${f}`n`n## [0.1.0] - 2026-01-01`n### Added`n- Old.`n"
        Add-ChangelogSection -ChangelogPath $Cl -Version '0.3.0' -Body "### Added`n- New." -Date ([datetime]'2026-07-28')
        $after = Get-Content $Cl -Raw
        # The fence stays intact: its opening line is still immediately followed by the example heading.
        $normalised = $after.Replace("`r`n", "`n")
        $normalised.Contains("${f}markdown`n## [2.0.0]") | Should -BeTrue
        # And the new section lands after the fence but before the real first section.
        $after.IndexOf('## [0.3.0]') | Should -BeGreaterThan $after.IndexOf('## [2.0.0]')
        $after.IndexOf('## [0.3.0]') | Should -BeLessThan $after.IndexOf('## [0.1.0]')
    }

    It 'is not blinded by two inline triple-backtick spans in separate entries' {
        # A regex pairing fences across the whole file read these as one long fence and swallowed the real
        # headings between them, so a genuine duplicate sailed through. Measured: two '## [0.5.0]' sections.
        $f = [string][char]0x60 * 3
        Set-Content -Path $Cl -Value "# Changelog`n`n## [0.9.0] - 2026-05-01`n### Added`n- Use ${f} for fences.`n`n## [0.5.0] - 2026-02-01`n### Added`n- Also ${f} here.`n"
        { Add-ChangelogSection -ChangelogPath $Cl -Version '0.5.0' -Body "### Added`n- Dup." -Date ([datetime]'2026-07-28') } |
            Should -Throw -ExpectedMessage '*already exists*'
    }

    It 'treats a tilde fence the same as a backtick fence' {
        Set-Content -Path $Cl -Value "# Changelog`n`n## [0.1.0] - 2026-01-01`n### Added`n- Fmt:`n~~~markdown`n## [1.0.0] - 2026-01-01`n~~~`n"
        { Add-ChangelogSection -ChangelogPath $Cl -Version '1.0.0' -Body "### Added`n- Real." -Date ([datetime]'2026-07-28') } |
            Should -Not -Throw
    }

    It 'still inserts normally into a multi-section changelog' {
        Set-Content -Path $Cl -Value "# Changelog`n`n## [0.1.0] - 2026-01-01`n### Added`n- Old.`n"
        Add-ChangelogSection -ChangelogPath $Cl -Version '0.2.0' -Body "### Added`n- New." -Date ([datetime]'2026-07-27')
        $after = Get-Content $Cl -Raw
        $after | Should -Match '(?m)^# Changelog$'
        $after.IndexOf('## [0.2.0]') | Should -BeLessThan $after.IndexOf('## [0.1.0]')
        $after | Should -Match '- Old\.'
    }

    It 'does not trip the guard on a version merely mentioned in a body' {
        Set-Content -Path $Cl -Value "# Changelog`n`n## [0.1.0] - 2026-01-01`n### Fixed`n- Regression from ## [0.2.0] discussion.`n"
        { Add-ChangelogSection -ChangelogPath $Cl -Version '0.2.0' -Body "### Added`n- New." -Date ([datetime]'2026-07-27') } |
            Should -Not -Throw
    }
}

Describe 'Changelog fence and Unreleased handling' {
    BeforeEach {
        $script:Cl = Join-Path ([IO.Path]::GetTempPath()) ("cl2_" + [guid]::NewGuid() + ".md")
        $script:B3 = [string][char]0x60 * 3
        $script:B4 = [string][char]0x60 * 4
    }
    AfterEach { Remove-Item $script:Cl -Force -ErrorAction SilentlyContinue }

    It 'does not treat an inline triple-backtick span as an open fence' {
        # A naive toggle closed nothing here, so every line after it looked fenced -- the guard went blind and
        # a real duplicate would have been admitted. CommonMark: a backtick info string bars the fence.
        Set-Content -Path $Cl -Value "# Changelog`n`n${B3}code${B3}`n`n## [0.1.0] - 2026-01-01`n### Added`n- Old.`n"
        { Add-ChangelogSection -ChangelogPath $Cl -Version '0.1.0' -Body "### Added`n- Dup." -Date ([datetime]'2026-07-28') } |
            Should -Throw -ExpectedMessage '*already exists*'
    }

    It 'does not let a three-backtick line close a four-backtick fence' {
        Set-Content -Path $Cl -Value "# Changelog`n`n${B4}`n${B3}`n## [9.9.9] - 2026-01-01`n${B3}`n${B4}`n`n## [0.1.0] - 2026-01-01`n- Old.`n"
        { Add-ChangelogSection -ChangelogPath $Cl -Version '9.9.9' -Body "### Added`n- Real." -Date ([datetime]'2026-07-28') } |
            Should -Not -Throw
        # The example heading stayed inside the fence, so the new section went after it, not into it.
        $after = (Get-Content $Cl -Raw).Replace("`r`n", "`n")
        $after.Contains("${B4}`n${B3}`n## [9.9.9] - 2026-01-01") | Should -BeTrue
    }

    It 'does not let a tilde line close a backtick fence' {
        Set-Content -Path $Cl -Value "# Changelog`n`n${B3}md`n~~~`n## [9.9.9] - 2026-01-01`n${B3}`n`n## [0.1.0] - 2026-01-01`n- Old.`n"
        { Add-ChangelogSection -ChangelogPath $Cl -Version '9.9.9' -Body "### Added`n- Real." -Date ([datetime]'2026-07-28') } |
            Should -Not -Throw
    }

    It 'keeps an Unreleased section on top when inserting' {
        # Keep a Changelog puts '## [Unreleased]' first. Treating it as a release inserted the new section
        # ABOVE it, breaking the standard structure.
        Set-Content -Path $Cl -Value "# Changelog`n`n## [Unreleased]`n### Added`n- WIP.`n`n## [0.1.0] - 2026-01-01`n- Old.`n"
        Add-ChangelogSection -ChangelogPath $Cl -Version '0.3.0' -Body "### Added`n- New." -Date ([datetime]'2026-07-28')
        $after = Get-Content $Cl -Raw
        $after.IndexOf('## [Unreleased]') | Should -BeLessThan $after.IndexOf('## [0.3.0]')
        $after.IndexOf('## [0.3.0]')      | Should -BeLessThan $after.IndexOf('## [0.1.0]')
    }

    It 'does not publish Unreleased as the top release section' {
        # The reader and the writer must agree; when only the writer knew about Unreleased, the reader still
        # handed it to `gh release` as the release body.
        Set-Content -Path $Cl -Value "# Changelog`n`n## [Unreleased]`n### Added`n- WIP.`n`n## [0.2.0] - 2026-02-01`n### Added`n- Shipped.`n"
        $top = Get-TopChangelogSection -ChangelogPath $Cl
        $top | Should -Match '^## \[0\.2\.0\]'
        $top | Should -Not -Match 'WIP'
    }

    It 'does not publish a fenced example heading as the top section' {
        Set-Content -Path $Cl -Value "# Changelog`n`nFormat:`n${B3}markdown`n## [9.9.9] - 2026-01-01`n${B3}`n`n## [0.2.0] - 2026-02-01`n### Added`n- Shipped.`n"
        $top = Get-TopChangelogSection -ChangelogPath $Cl
        $top | Should -Match '^## \[0\.2\.0\]'
        $top | Should -Not -Match '9\.9\.9'
    }
}
