<#
.SYNOPSIS
Validate a commit subject against this repo's conventional-commit shape. Used by the lefthook commit-msg hook.

.DESCRIPTION
scripts/release.ps1 computes the next version FROM COMMIT SUBJECTS, so a subject the parser does not
recognise contributes nothing to the version. This catches the malformed ones at write time, when they are
free to fix, instead of at release time, when amending history is not.

Scope is deliberately narrow. It rejects a subject that LOOKS like it meant to be conventional and is not
(a typo'd type, a missing colon). It does not police prose, length, mood, or trailers.

WHAT IS ALLOWED, AND WHY - each exemption below is a real subject from this repo's history:

  - Merge commits ("Merge branch ...", "Merge pull request ..."). These are plumbing and carry no version
    signal of their own; the work rides in the merged commits, which ARE conventional because this repo
    merges --no-ff. MEASURED 2026-09-09: every one of the 6 subjects release.ps1 reported as
    non-conventional in the v1.0.0..HEAD range was a merge commit. Rejecting them would fail correct history.
  - Revert, fixup!, squash!, amend! - git's own generated prefixes.
  - A comment-only / empty message, which git itself aborts on. Not this script's job to duplicate.

TYPES include `measure`, which is NOT in the Conventional Commits spec but IS this repo's convention (6 uses).
A stock commitlint config would reject it - which is how a hook gets uninstalled rather than fixed.

.PARAMETER Path
Path to the commit message file (git passes this to the commit-msg hook as $1).

.PARAMETER Subject
A subject string to validate directly. For tests; mutually exclusive with -Path.
#>
[CmdletBinding()]
param(
    [string]$Path,
    [string]$Subject
)

$script:AllowedTypes = @(
    'feat', 'fix', 'docs', 'chore', 'test', 'refactor', 'perf', 'ci', 'build', 'style', 'revert',
    'measure'   # repo-specific: commits that record a measurement rather than change behaviour
)

function Test-CommitSubject {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Line)

    $s = $Line.Trim()

    # git's own generated prefixes, and empty/comment-only input git already rejects itself.
    if ($s -eq '') { return $true }
    if ($s.StartsWith('#')) { return $true }
    foreach ($p in @('Merge branch', 'Merge pull request', 'Merge remote-tracking', 'Revert "',
                     'fixup!', 'squash!', 'amend!')) {
        if ($s.StartsWith($p)) { return $true }
    }

    $types = ($script:AllowedTypes -join '|')
    # type(optional scope)optional-! : space, then at least one non-space character.
    return [bool]([regex]::IsMatch($s, "^($types)(\([^)]+\))?!?: \S"))
}

if ($PSBoundParameters.ContainsKey('Subject')) {
    if (Test-CommitSubject -Line $Subject) { exit 0 }
    exit 1
}

if (-not $Path) { Write-Error 'Pass -Path <commit-msg-file> or -Subject <string>.'; exit 2 }
if (-not (Test-Path -LiteralPath $Path)) { Write-Error "commit message file not found: $Path"; exit 2 }

$first = (Get-Content -LiteralPath $Path -TotalCount 1 -ErrorAction SilentlyContinue)
if ($null -eq $first) { $first = '' }

if (Test-CommitSubject -Line $first) { exit 0 }

Write-Host ''
Write-Host 'Commit subject is not in this repo''s conventional format:' -ForegroundColor Red
Write-Host "  $first" -ForegroundColor Red
Write-Host ''
Write-Host 'Expected:  <type>(<optional scope>): <description>' -ForegroundColor Yellow
Write-Host "Types:     $($script:AllowedTypes -join ', ')" -ForegroundColor Yellow
Write-Host ''
Write-Host 'This is not style policing: scripts/release.ps1 computes the next version FROM these subjects,'
Write-Host 'so an unrecognised one contributes nothing to the version and nobody notices until release.'
Write-Host ''
Write-Host 'Merge, revert and fixup subjects are accepted as-is.'
exit 1
