Describe 'check-commit-msg.ps1' {
    BeforeAll {
        # Set in BeforeAll, referenced with $script: — a Describe-level *function* is not visible inside It
        # under Pester 6, which is how the first draft of this file failed 26/29.
        $script:Checker = Join-Path $PSScriptRoot 'check-commit-msg.ps1'
    }

    Context 'accepts what this repo actually writes' {
        It 'accepts <_>' -ForEach @(
            'feat(watch): add desktop_watch',
            'fix: correct the wake claim',
            'docs(roadmap): file item 30',
            'chore(release): v1.0.1',
            'test(autotrain): assert retired rules stay retired',
            'refactor: split the coalescer',
            'perf: avoid a second walk',
            'ci: bump setup-dotnet',
            'build: pin the SDK',
            'style: wrap at 110',
            'fix!: drop the deprecated field'
        ) {
            & pwsh -NoProfile -File $script:Checker -Subject $_
            $LASTEXITCODE | Should -Be 0
        }

        It 'accepts the repo-specific "measure" type that a stock commitlint config would reject' {
            # 6 real commits use it. A validator that rejects the repo's own convention gets uninstalled,
            # and an uninstalled hook is worse than none because it still looks like protection.
            & pwsh -NoProfile -File $script:Checker -Subject 'measure: two-arm cold-provider wake test'
            $LASTEXITCODE | Should -Be 0
        }
    }

    Context 'accepts git-generated subjects that carry no version signal' {
        # MEASURED 2026-09-09: every one of the 6 subjects release.ps1 reported as non-conventional across
        # v1.0.0..HEAD was a merge commit. Rejecting these would fail correct history.
        It 'accepts <_>' -ForEach @(
            "Merge branch 'fix/tool-descriptions-state-lease-posture'",
            'Merge pull request #12 from ckir/topic',
            "Merge remote-tracking branch 'origin/master'",
            'Revert "feat: add a thing"',
            'fixup! feat: add a thing',
            'squash! fix: something'
        ) {
            & pwsh -NoProfile -File $script:Checker -Subject $_
            $LASTEXITCODE | Should -Be 0
        }
    }

    Context 'rejects subjects that would silently vote nothing at release time' {
        It 'rejects <_>' -ForEach @(
            'added a new tool',
            'Fix: capitalised type',
            'feet: typo in the type',
            'fix missing colon',
            'fix:no space after the colon',
            '(scope): missing type'
        ) {
            & pwsh -NoProfile -File $script:Checker -Subject $_
            $LASTEXITCODE | Should -Be 1
        }

        It 'rejects a type with an empty description' {
            & pwsh -NoProfile -File $script:Checker -Subject 'fix:'
            $LASTEXITCODE | Should -Be 1
        }
    }

    Context 'the file-path interface git actually uses' {
        BeforeEach {
            $script:MsgFile = Join-Path ([IO.Path]::GetTempPath()) ("msg_" + [guid]::NewGuid() + '.txt')
        }
        AfterEach { if (Test-Path $script:MsgFile) { Remove-Item -Force $script:MsgFile } }

        It 'passes a good subject read from the file' {
            "fix(core): a real fix`n`nbody text here" | Set-Content -LiteralPath $MsgFile
            & pwsh -NoProfile -File $script:Checker -Path $MsgFile
            $LASTEXITCODE | Should -Be 0
        }

        It 'fails a bad subject, and reads ONLY the first line' {
            # The distractor: a perfectly conventional line in the BODY must not rescue a malformed subject.
            "nonsense subject`n`nfix: this is in the body" | Set-Content -LiteralPath $MsgFile
            & pwsh -NoProfile -File $script:Checker -Path $MsgFile
            $LASTEXITCODE | Should -Be 1
        }

        It 'exits 2 (not 1) when the message file is missing, so a broken hook is distinguishable from a bad subject' {
            & pwsh -NoProfile -File $script:Checker -Path (Join-Path ([IO.Path]::GetTempPath()) 'no-such-file.txt') 2>$null
            $LASTEXITCODE | Should -Be 2
        }
    }
}
