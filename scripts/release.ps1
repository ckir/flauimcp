# scripts/release.ps1
<#
.SYNOPSIS
One-command flaui-mcp release: compute the next version from conventional commits, gate locally, draft the
CHANGELOG body with claude -p, review, then commit/tag/push.

.PARAMETER Help
Print usage and exit 0. No side effects.

.PARAMETER WhatIf
Run preconditions + version compute + gate, print the result and the prompt that WOULD be sent to claude -p.
Makes no writes (no commit/tag/push) and never calls the LLM.

.PARAMETER Yes
Unattended: never blocks on stdin. Auto-accepts the changelog draft and the final cut confirmation; every
other interactive gate hard-fails (exit 1) rather than opening an interactive fallback. See the plan's
"Unattended (-Yes) contract" table.

.PARAMETER Version
Pin the release to this exact X.Y.Z version instead of computing one.

.PARAMETER Bump
Force a {major|minor|patch} bump from the last tag instead of computing the level from commits.

.PARAMETER Model
The claude -p model to use for the changelog draft. Default: haiku.

.EXAMPLE
scripts/release.ps1 -WhatIf
.EXAMPLE
scripts/release.ps1 -Yes
.EXAMPLE
scripts/release.ps1 -Version 0.16.2
#>
[CmdletBinding()]
param(
    [Alias('H')][switch]$Help,
    [switch]$WhatIf,
    [Alias('y')][switch]$Yes,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [ValidateSet('major','minor','patch')][string]$Bump,
    [string]$Model = 'haiku',
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)
# -RepoRoot mirrors scripts/new-tool.ps1's existing convention (a param defaulting to the script's own repo
# location, overridable) — it's not a documented user-facing flag (omitted from Show-Usage below, since a
# real release is always run in-place), but it lets Task 9/11's integration smoke tests point the whole
# orchestrator at a disposable sandbox repo instead of the live flauimcp checkout.
# Design note: this is a plain custom -WhatIf switch, NOT [CmdletBinding(SupportsShouldProcess)]'s built-in
# ShouldProcess mechanism — the spec's -WhatIf is a specific "preview + gate, no LLM, no writes" flow, not
# generic per-cmdlet confirmation prompts, so SupportsShouldProcess is deliberately not used.
# `-?` is not declared as a param: PowerShell intercepts `-?` on any script/function that has comment-based
# help (the block above) and prints that help natively — this satisfies the spec's -Help/-H/-? trio without
# fighting the engine for the `-?` token specifically.

$ErrorActionPreference = 'Stop'
# $RepoRoot (the -RepoRoot parameter above) is the repo BEING RELEASED — for Task 9/11's sandbox smoke
# tests that's a disposable throwaway repo, not this checkout. The library always loads from THIS script's
# own location ($PSScriptRoot = the real scripts/ directory), independent of -RepoRoot.
. (Join-Path $PSScriptRoot 'lib/release-lib.ps1')

function Show-Usage {
    Write-Host @"
scripts/release.ps1 — one-command flaui-mcp release

USAGE
  scripts/release.ps1 [-WhatIf] [-Yes] [-Version X.Y.Z] [-Bump major|minor|patch] [-Model <name>]
  scripts/release.ps1 -Help

FLOW
  1. Preconditions: on master, tracked tree clean, tags fetched.
  2. Compute the next version from conventional commits since the last v* tag.
  3. Gate: dotnet build/test, 3-file version sync, plugin-snapshot drift.
  4. Draft the CHANGELOG body with 'claude -p' (heading is script-owned).
  5. You review: Accept / Regenerate / Edit / Abort.
  6. Confirm: "Cut release vX.Y.Z?" (skipped under -Yes).
  7. Commit chore(release), tag vX.Y.Z, 'git push --atomic origin master vX.Y.Z'.

FLAGS
  -Help, -H, -?     Print this usage and exit 0. No side effects.
  -WhatIf           Preview version + gate + LLM prompt. No writes, no LLM call.
  -Yes, -y          Unattended: auto-accept the draft and the final confirmation;
                    every other interactive gate hard-fails instead of blocking.
  -Version X.Y.Z    Pin the release version (skips commit-driven computation).
  -Bump <level>     Force major/minor/patch from the last tag.
  -Model <name>     claude -p model for the changelog draft (default: haiku).

EXAMPLES
  scripts/release.ps1 -WhatIf
  scripts/release.ps1 -Yes
  scripts/release.ps1 -Version 0.16.2
"@
}

function Assert-Preconditions {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RepoRoot)

    Write-Host "Fetching tags from origin..."
    git -C $RepoRoot fetch origin --tags 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "git fetch origin --tags failed (exit $LASTEXITCODE). Check network/remote access." }

    $branch = (git -C $RepoRoot rev-parse --abbrev-ref HEAD).Trim()
    if ($branch -ne 'master') { throw "Must be on 'master' to release (currently on '$branch')." }

    $dirty = git -C $RepoRoot status --porcelain --untracked-files=no
    if ($dirty) { throw "Working tree has uncommitted tracked changes (untracked files are OK):`n$dirty" }

    Write-Host "Preconditions OK: on master, tracked tree clean, tags fetched."
}

function Get-EmptyChangelogTemplate {
    @"
### Added
- 

### Fixed
- 

### Changed
- 
"@
}

function Edit-InEditor {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$InitialContent)

    $tmp = [IO.Path]::GetTempFileName() -replace '\.tmp$', '.md'
    Set-Content -Path $tmp -Value $InitialContent -NoNewline -Encoding UTF8
    $editor = $env:EDITOR
    if ([string]::IsNullOrWhiteSpace($editor)) { $editor = 'notepad' }
    & $editor $tmp | Out-Null
    $result = Get-Content $tmp -Raw
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    $result
}

function Invoke-ChangelogLlm {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Prompt,
        [string]$Model = 'haiku',
        [int]$TimeoutSeconds = 120
    )

    $job = Start-Job -ScriptBlock {
        param($PromptText, $ModelName)
        $out = $PromptText | & claude -p --model $ModelName --output-format text 2>&1 | Out-String
        [pscustomobject]@{ Output = $out; ExitCode = $LASTEXITCODE }
    } -ArgumentList $Prompt, $Model

    $finished = Wait-Job -Job $job -Timeout $TimeoutSeconds
    if (-not $finished) {
        Stop-Job $job | Out-Null
        Remove-Job $job -Force | Out-Null
        return [pscustomobject]@{ Success = $false; Body = $null; Reason = "claude -p timed out after ${TimeoutSeconds}s" }
    }

    $result = Receive-Job $job
    Remove-Job $job -Force | Out-Null

    if (-not $result -or $result.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($result.Output)) {
        $reason = if ($result) { "claude -p exited $($result.ExitCode): $($result.Output)" } else { "claude -p produced no result" }
        return [pscustomobject]@{ Success = $false; Body = $null; Reason = $reason }
    }

    [pscustomobject]@{ Success = $true; Body = $result.Output.Trim(); Reason = $null }
}

function Get-OrCreateDraft {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$Prompt,
        [Parameter(Mandatory)][bool]$HasCommits,
        [string]$Model = 'haiku',
        [switch]$Yes
    )

    $draftPath = Join-Path ([IO.Path]::GetTempPath()) "flaui-mcp-release-draft-$Version.md"

    if (Test-Path $draftPath) {
        $resume = [bool]$Yes
        if (-not $Yes) {
            $ans = Read-Host "Found an existing draft for v$Version from a previous run. Resume it? [Y/n]"
            $resume = ($ans -eq '' -or $ans -match '^[Yy]')
        }
        if ($resume) {
            Write-Host "Resuming draft from $draftPath"
            return [pscustomobject]@{ DraftPath = $draftPath; Body = (Get-Content $draftPath -Raw) }
        }
        Remove-Item $draftPath -Force
    }

    if (-not $HasCommits) {
        if ($Yes) {
            throw "Zero-commit release with -Yes: nothing for the LLM to summarize, and no interactive `$EDITOR fallback is allowed unattended. Re-run without -Yes, or edit CHANGELOG.md by hand."
        }
        Write-Warning "No commits in range — nothing to summarize. Opening `$EDITOR on an empty template."
        $body = Edit-InEditor -InitialContent (Get-EmptyChangelogTemplate)
        Set-Content -Path $draftPath -Value $body -NoNewline -Encoding UTF8
        return [pscustomobject]@{ DraftPath = $draftPath; Body = $body }
    }

    $llm = Invoke-ChangelogLlm -Prompt $Prompt -Model $Model
    if (-not $llm.Success) {
        if ($Yes) {
            throw "claude -p failed unattended (-Yes): $($llm.Reason). No interactive `$EDITOR fallback is allowed under -Yes."
        }
        Write-Warning "claude -p failed: $($llm.Reason). Falling back to `$EDITOR on an empty template."
        $body = Edit-InEditor -InitialContent (Get-EmptyChangelogTemplate)
    } else {
        $body = $llm.Body
    }
    Set-Content -Path $draftPath -Value $body -NoNewline -Encoding UTF8
    [pscustomobject]@{ DraftPath = $draftPath; Body = $body }
}

function Invoke-DraftReview {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$DraftPath,
        [Parameter(Mandatory)][string]$Prompt,
        [string]$Model = 'haiku',
        [switch]$Yes
    )

    while ($true) {
        $body = Get-Content $DraftPath -Raw

        if ($Yes) {
            $key = 'A'
        } else {
            Write-Host "`n--- Draft changelog body for v$Version ---`n$body`n---`n"
            $ans = Read-Host "[A]ccept (default) / [R]egenerate / [E]dit / e[X]it-abort"
            $key = if ([string]::IsNullOrWhiteSpace($ans)) { 'A' } else { $ans.Substring(0,1).ToUpperInvariant() }
        }

        if ($key -eq 'A') {
            $validBody = (-not [string]::IsNullOrWhiteSpace($body)) -and ($body -match '(?m)^###\s')
            if ($validBody) { return [pscustomobject]@{ Action = 'Accept'; Body = $body } }

            if ($Yes) {
                throw "Accepted body for v$Version failed validation (empty, or no '### ' section) — cannot loop back to Review unattended under -Yes."
            }
            Write-Warning "Draft is empty or has no '### ' section — a heading-only release entry is not allowed. Opening `$EDITOR to fix."
            $body = Edit-InEditor -InitialContent $body
            Set-Content -Path $DraftPath -Value $body -NoNewline -Encoding UTF8
            continue
        }

        if ($key -eq 'X') {
            Write-Host "Aborted. Draft left at $DraftPath for a later resume."
            return [pscustomobject]@{ Action = 'Abort'; Body = $null }
        }

        if ($key -eq 'R') {
            $llm = Invoke-ChangelogLlm -Prompt $Prompt -Model $Model
            $body = if ($llm.Success) {
                $llm.Body
            } else {
                Write-Warning $llm.Reason
                Edit-InEditor -InitialContent (Get-EmptyChangelogTemplate)
            }
            Set-Content -Path $DraftPath -Value $body -NoNewline -Encoding UTF8
            continue
        }

        if ($key -eq 'E') {
            $body = Edit-InEditor -InitialContent $body
            Set-Content -Path $DraftPath -Value $body -NoNewline -Encoding UTF8
            continue
        }

        Write-Warning "Unrecognized choice '$ans' — enter A, R, E, or X."
    }
}

function Get-ReleaseReconciliationState {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RepoRoot)

    git -C $RepoRoot fetch origin master --quiet 2>$null
    $remoteHead = (git -C $RepoRoot rev-parse origin/master 2>$null)
    $remoteHead = if ($remoteHead) { $remoteHead.Trim() } else { $null }
    $localHead  = (git -C $RepoRoot rev-parse HEAD).Trim()
    $headOnRemote = ($remoteHead -and ($remoteHead -eq $localHead))

    $headSubject = (git -C $RepoRoot log -1 --format='%s').Trim()
    $releaseMatch = [regex]::Match($headSubject, '^chore\(release\):\s*v(?<v>\d+\.\d+\.\d+)')
    $headIsUnpushedRelease = $releaseMatch.Success -and -not $headOnRemote
    $headReleaseVersion = if ($releaseMatch.Success) { $releaseMatch.Groups['v'].Value } else { $null }

    $localTagsAtHead = @(git -C $RepoRoot tag --points-at HEAD | Where-Object { $_ -match '^v\d+\.\d+\.\d+$' })
    $orphanTags = @()
    foreach ($t in $localTagsAtHead) {
        $remoteHas = [bool](git -C $RepoRoot ls-remote --tags origin $t)
        if (-not $remoteHas) { $orphanTags += $t }
    }

    [pscustomobject]@{
        HalfFinished          = $headIsUnpushedRelease -or ($orphanTags.Count -gt 0)
        HeadIsUnpushedRelease = $headIsUnpushedRelease
        HeadReleaseVersion    = $headReleaseVersion
        LocalOnlyTagsAtHead   = $orphanTags
    }
}

function Resolve-HalfFinishedRelease {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][pscustomobject]$Reconciliation,
        [switch]$Yes,
        [switch]$WhatIf
    )

    $targetVersion = if ($Reconciliation.HeadReleaseVersion) {
        $Reconciliation.HeadReleaseVersion
    } else {
        $Reconciliation.LocalOnlyTagsAtHead[0].TrimStart('v')
    }
    $tag = "v$targetVersion"

    $tagExistsLocally = [bool](git -C $RepoRoot tag -l $tag)
    $tagPointsAtHead = $false
    if ($tagExistsLocally) {
        $tagSha = (git -C $RepoRoot rev-parse "$tag^{commit}" 2>$null)
        $headSha = (git -C $RepoRoot rev-parse HEAD).Trim()
        $tagPointsAtHead = ($tagSha -and ($tagSha.Trim() -eq $headSha))
    }

    if ($tagExistsLocally -and -not $tagPointsAtHead) {
        throw "Tag $tag exists locally but does NOT point at HEAD — likely orphaned by a 'git commit --amend' after a failed push. Refusing to auto-reconcile. Inspect manually: git log --oneline $tag HEAD~3..HEAD, then either move the tag or reset HEAD by hand."
    }

    # -WhatIf takes precedence over -Yes when both are somehow passed: -WhatIf's contract is "no writes,
    # ever" (including the -Yes hard-fail's exit 1, which is itself a mutation-adjacent unattended
    # decision), so it must win to preserve the read-only guarantee.
    if ($WhatIf) {
        Write-Host "[-WhatIf] Half-finished release detected: HEAD looks like an unpushed '$tag' release (the commit and/or tag exist locally, but a previous 'git push --atomic' didn't land). -WhatIf makes no changes; re-run without -WhatIf to reconcile."
        return
    }

    if ($Yes) {
        throw "Half-finished prior release detected for $tag — refusing to auto-mutate git history under -Yes. Re-run without -Yes to reconcile interactively."
    }

    Write-Host "Half-finished prior release detected: HEAD looks like an unpushed '$tag' release (the commit and/or tag exist locally, but a previous 'git push --atomic' didn't land)."
    $ans = Read-Host "[P]ush the existing commit+tag now / e[X]it and leave as-is?"
    $choice = if ([string]::IsNullOrWhiteSpace($ans)) { 'X' } else { $ans.Substring(0,1).ToUpperInvariant() }
    if ($choice -eq 'P') {
        if (-not $tagExistsLocally) { git -C $RepoRoot tag $tag }
        git -C $RepoRoot push --atomic origin master $tag
        if ($LASTEXITCODE -ne 0) { throw "push --atomic failed again (exit $LASTEXITCODE). Local state is unchanged; re-run to retry." }
        Write-Host "Pushed $tag."
        exit 0
    }
    Write-Host "Left as-is. Re-run scripts/release.ps1 when ready to retry the push."
    exit 0
}

function Invoke-ReleaseCommit {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$Version
    )

    $tag = "v$Version"
    git -C $RepoRoot add `
        CHANGELOG.md `
        src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj `
        installer/flaui-mcp.iss `
        plugins/flaui-mcp/.claude-plugin/plugin.json
    if ($LASTEXITCODE -ne 0) { throw "git add failed (exit $LASTEXITCODE)." }

    git -C $RepoRoot commit -m "chore(release): $tag"
    if ($LASTEXITCODE -ne 0) { throw "git commit failed (exit $LASTEXITCODE)." }

    git -C $RepoRoot tag $tag
    if ($LASTEXITCODE -ne 0) { throw "git tag $tag failed (exit $LASTEXITCODE)." }

    git -C $RepoRoot push --atomic origin master $tag
    if ($LASTEXITCODE -ne 0) {
        throw "git push --atomic origin master $tag FAILED (exit $LASTEXITCODE). Both refs were rejected together (--atomic working as intended) — the local commit + tag are intact. Re-run scripts/release.ps1 to retry; it will detect this half-finished state and offer to re-push."
    }

    $remoteUrl = (git -C $RepoRoot remote get-url origin).Trim()
    $slug = if ($remoteUrl -match 'github\.com[:/](?<slug>[^/]+/[^/.]+)') { $Matches.slug } else { $null }
    if ($slug) {
        Write-Host "Pushed. Watch CI: https://github.com/$slug/actions"
    } else {
        Write-Host "Pushed $tag to origin/master."
    }
}

if ($Help) { Show-Usage; exit 0 }

try {
    Assert-Preconditions -RepoRoot $RepoRoot

    $recon = Get-ReleaseReconciliationState -RepoRoot $RepoRoot
    if ($recon.HalfFinished) {
        Resolve-HalfFinishedRelease -RepoRoot $RepoRoot -Reconciliation $recon -Yes:$Yes -WhatIf:$WhatIf
    }

    $sync = Get-VersionsInSync -RepoRoot $RepoRoot
    if (-not $sync.InSync) { throw "Version files are out of sync: $($sync.Message). Fix before releasing." }
    $filesVersion = $sync.Versions.Csproj

    $lastTag = (git -C $RepoRoot describe --tags --match 'v*' --abbrev=0 2>$null)
    $lastTagFound = ($LASTEXITCODE -eq 0) -and $lastTag
    $lastTagVersion = if ($lastTagFound) { $lastTag.Trim().TrimStart('v') } else { '0.0.0' }
    $rangeArgs = if ($lastTagFound) { @("$($lastTag.Trim())..HEAD") } else { @() }

    $rawLog = (git -C $RepoRoot log @rangeArgs --format='%B%x1e' 2>$null | Out-String)
    $commitMessages = @($rawLog -split "`u{1e}" | Where-Object { $_.Trim() })

    $next = Get-NextVersion -CurrentVersion $lastTagVersion -CommitMessages $commitMessages -OverrideVersion $Version -OverrideBump $Bump

    if ($next.NothingToRelease) {
        $since = if ($lastTagFound) { $lastTag.Trim() } else { '(repo start)' }
        Write-Host "Nothing to release since $since — no feat/fix/breaking commits found. Use -Version or -Bump to force a release."
        exit 0
    }

    Write-Host "Computed next version: $($next.Version) (level=$($next.Level), trigger=$($next.Trigger), base=$lastTagVersion)"
    if ($next.NonConventionalCount -gt 0) {
        Write-Warning "$($next.NonConventionalCount) commit(s) in range are not conventional-format and were ignored for version math."
    }

    # Design gap this plan resolves (see Ground-truth facts): warn, don't hard-fail, when the freshly
    # computed version disagrees with what the 3 project files currently say — unless the human explicitly
    # pinned -Version/-Bump, in which case they've already made the call.
    if (-not $Version -and -not $Bump -and ($next.Version -ne $filesVersion)) {
        $msg = "Computed next version ($($next.Version), from $lastTagVersion + commit math) differs from " +
               "the version already in the 3 project files ($filesVersion). This can happen when the files " +
               "were pre-bumped by a prior merge that hasn't been tagged/released yet."
        Write-Warning $msg
        if ($Yes) { throw "$msg Refusing to guess under -Yes — re-run with an explicit -Version to pin it." }
        $ans = Read-Host "Proceed releasing as $($next.Version)? [y/N] (or Ctrl+C and re-run with -Version to pin a different one)"
        if ($ans -notmatch '^[Yy]') { Write-Host "Aborted — re-run with -Version X.Y.Z to pin the release version."; exit 0 }
    }

    $gate = Invoke-Gate -RepoRoot $RepoRoot -SkipPluginDrift:$WhatIf
    foreach ($c in $gate.Checks) {
        $status = if ($c.Passed) { 'PASS' } else { 'FAIL' }
        Write-Host "[$status] $($c.Name): $($c.Detail)"
    }
    if (-not $gate.Passed) { throw "Gate failed — fix the failing check(s) above before releasing." }

    $diffText = (git -C $RepoRoot log @rangeArgs -p 2>$null | Out-String)
    $diffStatText = (git -C $RepoRoot log @rangeArgs --stat 2>$null | Out-String)
    $exemplar = Get-TopChangelogSection -ChangelogPath (Join-Path $RepoRoot 'CHANGELOG.md') -Count 2
    $prompt = Get-ChangelogPrompt -Version $next.Version -CommitMessages $commitMessages -DiffText $diffText -DiffStatText $diffStatText -StyleExemplar $exemplar

    if ($WhatIf) {
        Write-Host "`n[-WhatIf] Prompt that WOULD be sent to 'claude -p --model $Model':`n"
        Write-Host $prompt
        Write-Host "`n[-WhatIf] plugin-drift check was skipped in preview (no writes); the real run verifies it."
        Write-Host "`n[-WhatIf] No commit, tag, or push will happen. Exiting without side effects."
        exit 0
    }

    $hasCommits = ($commitMessages.Count -gt 0)
    $draft = Get-OrCreateDraft -Version $next.Version -Prompt $prompt -HasCommits $hasCommits -Model $Model -Yes:$Yes

    $review = Invoke-DraftReview -Version $next.Version -DraftPath $draft.DraftPath -Prompt $prompt -Model $Model -Yes:$Yes
    if ($review.Action -eq 'Abort') { exit 0 }

    if (-not $Yes) {
        $ans = Read-Host "Cut release v$($next.Version) — write CHANGELOG, bump versions, commit, tag, and push to origin? [y/N]"
        if ($ans -notmatch '^[Yy]') { Write-Host "Aborted — nothing written or committed."; exit 0 }
    }

    Add-ChangelogSection -ChangelogPath (Join-Path $RepoRoot 'CHANGELOG.md') -Version $next.Version -Body $review.Body
    Set-ProjectVersion -RepoRoot $RepoRoot -Version $next.Version

    Invoke-ReleaseCommit -RepoRoot $RepoRoot -Version $next.Version

    Remove-Item $draft.DraftPath -Force -ErrorAction SilentlyContinue
}
catch {
    Write-Error $_
    exit 1
}
