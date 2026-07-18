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

if ($Help) { Show-Usage; exit 0 }

try {
    Assert-Preconditions -RepoRoot $RepoRoot

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

    $gate = Invoke-Gate -RepoRoot $RepoRoot
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
        Write-Host "`n[-WhatIf] No commit, tag, or push will happen. Exiting without side effects."
        exit 0
    }

    # --- draft (Task 9) / review (Task 10) / reconciliation + commit/tag/push (Task 11) appended below ---
}
catch {
    Write-Error $_
    exit 1
}
