# scripts/lib/release-lib.ps1
# Pure, testable functions backing scripts/release.ps1.
# Dot-source this file; every function below is then available in the caller's scope.
$ErrorActionPreference = 'Stop'

function Step-SemVer {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][ValidateSet('major','minor','patch')][string]$Component
    )
    if ($Version -notmatch '^(?<maj>\d+)\.(?<min>\d+)\.(?<pat>\d+)$') {
        throw "Step-SemVer: not a valid X.Y.Z semver: '$Version'"
    }
    $maj = [int]$Matches.maj; $min = [int]$Matches.min; $pat = [int]$Matches.pat
    switch ($Component) {
        'major' { $maj++; $min = 0; $pat = 0 }
        'minor' { $min++; $pat = 0 }
        'patch' { $pat++ }
    }
    "$maj.$min.$pat"
}

function Get-NextVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CurrentVersion,
        [AllowEmptyCollection()][string[]]$CommitMessages = @(),
        [string]$OverrideVersion,
        [ValidateSet('major','minor','patch')][string]$OverrideBump
    )

    $conventionalPattern = '^(?<type>[a-zA-Z]+)(\((?<scope>[^)]+)\))?(?<bang>!)?:\s*(?<subject>.+)$'
    $hasBreaking = $false
    $hasFeat = $false
    $hasFix = $false
    $nonConventionalCount = 0
    $parsedCommits = @()

    foreach ($msg in $CommitMessages) {
        if ([string]::IsNullOrWhiteSpace($msg)) { continue }
        $subjectLine = ($msg -split "`n")[0].Trim()
        $m = [regex]::Match($subjectLine, $conventionalPattern)
        if (-not $m.Success) { $nonConventionalCount++; continue }

        $type = $m.Groups['type'].Value.ToLowerInvariant()
        $bang = $m.Groups['bang'].Success
        $bodyBreaking = [regex]::IsMatch($msg, '(?m)^BREAKING CHANGE:')
        $breaking = $bang -or $bodyBreaking

        $parsedCommits += [pscustomobject]@{
            Type     = $type
            Scope    = $m.Groups['scope'].Value
            Breaking = $breaking
            Subject  = $m.Groups['subject'].Value
        }

        if ($breaking)            { $hasBreaking = $true }
        elseif ($type -eq 'feat') { $hasFeat = $true }
        elseif ($type -eq 'fix')  { $hasFix = $true }
    }

    if ($OverrideVersion) {
        return [pscustomobject]@{
            Version = $OverrideVersion; Level = 'override'; Trigger = 'override'
            NothingToRelease = $false; NonConventionalCount = $nonConventionalCount; Commits = $parsedCommits
        }
    }
    if ($OverrideBump) {
        return [pscustomobject]@{
            Version = (Step-SemVer -Version $CurrentVersion -Component $OverrideBump)
            Level = $OverrideBump; Trigger = 'override'
            NothingToRelease = $false; NonConventionalCount = $nonConventionalCount; Commits = $parsedCommits
        }
    }
    if (-not $hasBreaking -and -not $hasFeat -and -not $hasFix) {
        return [pscustomobject]@{
            Version = $null; Level = $null; Trigger = $null
            NothingToRelease = $true; NonConventionalCount = $nonConventionalCount; Commits = $parsedCommits
        }
    }

    $currentMajor = [int]($CurrentVersion -split '\.')[0]
    if ($hasBreaking) {
        $level = if ($currentMajor -lt 1) { 'minor' } else { 'major' }
        $trigger = 'breaking'
    } elseif ($hasFeat) {
        $level = 'minor'; $trigger = 'feat'
    } else {
        $level = 'patch'; $trigger = 'fix'
    }

    [pscustomobject]@{
        Version = (Step-SemVer -Version $CurrentVersion -Component $level)
        Level = $level; Trigger = $trigger
        NothingToRelease = $false; NonConventionalCount = $nonConventionalCount; Commits = $parsedCommits
    }
}
