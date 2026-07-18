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

function Get-VersionsInSync {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RepoRoot)

    $csprojPath = Join-Path $RepoRoot 'src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj'
    $issPath    = Join-Path $RepoRoot 'installer/flaui-mcp.iss'
    $pluginPath = Join-Path $RepoRoot 'plugins/flaui-mcp/.claude-plugin/plugin.json'

    foreach ($p in @($csprojPath, $issPath, $pluginPath)) {
        if (-not (Test-Path $p)) { throw "Get-VersionsInSync: version file not found: $p" }
    }

    $csprojVersion = $null
    if ((Get-Content $csprojPath -Raw) -match '<Version>(?<v>\d+\.\d+\.\d+)</Version>') { $csprojVersion = $Matches.v }
    $issVersion = $null
    if ((Get-Content $issPath -Raw) -match '#define AppVersion "(?<v>\d+\.\d+\.\d+)"') { $issVersion = $Matches.v }
    $pluginVersion = (Get-Content $pluginPath -Raw | ConvertFrom-Json).version

    if (-not $csprojVersion) { throw "Get-VersionsInSync: no <Version> element found in $csprojPath" }
    if (-not $issVersion)    { throw "Get-VersionsInSync: no #define AppVersion found in $issPath" }
    if (-not $pluginVersion) { throw "Get-VersionsInSync: no 'version' key found in $pluginPath" }

    $versions = [ordered]@{ Csproj = $csprojVersion; Iss = $issVersion; Plugin = $pluginVersion }
    $distinct = $versions.Values | Select-Object -Unique
    $inSync = ($distinct.Count -eq 1)

    [pscustomobject]@{
        InSync   = $inSync
        Versions = $versions
        Message  = if ($inSync) {
            "3 version files agree at $($distinct[0])"
        } else {
            "Version files disagree: csproj=$csprojVersion iss=$issVersion plugin=$pluginVersion"
        }
    }
}

function Set-FilePreservingBom {
    # Write $Content to $Path as UTF-8, preserving whether the file currently has a UTF-8 BOM.
    # WriteAllText writes exactly $Content (no appended newline), matching Set-Content -NoNewline.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Content
    )
    $bytes  = [System.IO.File]::ReadAllBytes($Path)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $encoding = [System.Text.UTF8Encoding]::new($hasBom)   # ctor arg = emit-BOM
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

function Set-ProjectVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version
    )

    $csprojPath = Join-Path $RepoRoot 'src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj'
    $issPath    = Join-Path $RepoRoot 'installer/flaui-mcp.iss'
    $pluginPath = Join-Path $RepoRoot 'plugins/flaui-mcp/.claude-plugin/plugin.json'

    # Guard on element EXISTENCE (not "string changed"): re-releasing the version already in the files
    # (a pre-bumped/first-use case, see Task 8) replaces target->target — a safe no-op that must NOT throw.
    $csproj = Get-Content $csprojPath -Raw
    if ($csproj -notmatch '<Version>\d+\.\d+\.\d+</Version>') { throw "Set-ProjectVersion: no <Version> element found in $csprojPath" }
    $newCsproj = [regex]::Replace($csproj, '<Version>\d+\.\d+\.\d+</Version>', "<Version>$Version</Version>")
    Set-FilePreservingBom -Path $csprojPath -Content $newCsproj

    $iss = Get-Content $issPath -Raw
    if ($iss -notmatch '#define AppVersion "\d+\.\d+\.\d+"') { throw "Set-ProjectVersion: no AppVersion found in $issPath" }
    $newIss = [regex]::Replace($iss, '#define AppVersion "\d+\.\d+\.\d+"', "#define AppVersion `"$Version`"")
    Set-FilePreservingBom -Path $issPath -Content $newIss

    $plugin = Get-Content $pluginPath -Raw
    if ($plugin -notmatch '"version":\s*"\d+\.\d+\.\d+"') { throw "Set-ProjectVersion: no version key found in $pluginPath" }
    $newPlugin = [regex]::Replace($plugin, '"version":\s*"\d+\.\d+\.\d+"', "`"version`": `"$Version`"")
    Set-FilePreservingBom -Path $pluginPath -Content $newPlugin

    Get-VersionsInSync -RepoRoot $RepoRoot
}

function Get-TopChangelogSection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ChangelogPath,
        [int]$Count = 1
    )

    $lines = Get-Content $ChangelogPath
    $headingIdx = @()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^## \[') { $headingIdx += $i }
    }
    if ($headingIdx.Count -eq 0) { throw "Get-TopChangelogSection: no '## [' section found in $ChangelogPath" }

    $take = [Math]::Min($Count, $headingIdx.Count)
    $start = $headingIdx[0]
    $end = if ($headingIdx.Count -gt $take) { $headingIdx[$take] - 1 } else { $lines.Count - 1 }
    ($lines[$start..$end] -join "`n").TrimEnd()
}

function Add-ChangelogSection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ChangelogPath,
        [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
        [Parameter(Mandatory)][string]$Body,
        [datetime]$Date = (Get-Date)
    )

    $heading = "## [$Version] - $($Date.ToString('yyyy-MM-dd'))"
    $content = Get-Content $ChangelogPath -Raw

    if ($content -match [regex]::Escape($heading)) {
        throw "Add-ChangelogSection: heading '$heading' already exists in $ChangelogPath — refusing to add a duplicate."
    }

    $lines = Get-Content $ChangelogPath
    $firstSectionLine = ($lines | Select-String -Pattern '^## \[' | Select-Object -First 1).LineNumber
    $section = "$heading`n`n$($Body.Trim())`n"

    if (-not $firstSectionLine) {
        $newContent = $content.TrimEnd() + "`n`n" + $section
    } else {
        $insertAt = $firstSectionLine - 1   # 0-based index of the first '## [' line
        $before = ($lines[0..($insertAt - 1)] -join "`n").TrimEnd()
        $after  = ($lines[$insertAt..($lines.Count - 1)] -join "`n")
        $newContent = "$before`n`n$section`n$after"
    }

    Set-Content -Path $ChangelogPath -Value $newContent -NoNewline -Encoding UTF8
}

function Get-ChangelogPrompt {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$CommitMessages,
        [string]$DiffText = '',
        [string]$DiffStatText = '',
        [Parameter(Mandatory)][string]$StyleExemplar,
        [int]$DiffSizeThresholdBytes = 150000,
        [int]$CommitCountThreshold = 40
    )

    $useStat = ($DiffText.Length -gt $DiffSizeThresholdBytes) -or ($CommitMessages.Count -gt $CommitCountThreshold)
    $diffSection = if ($useStat) {
        "Diff stat (full patch omitted — release is large):`n$DiffStatText"
    } else {
        "Full diff:`n$DiffText"
    }
    $commitList = ($CommitMessages | ForEach-Object { "- $(($_ -split "`n")[0])" }) -join "`n"

    @"
You are drafting the CHANGELOG.md body for flaui-mcp release $Version.

Output ONLY the body sections (### Added / ### Fixed / ### Changed as applicable) — no '## [$Version]'
heading, no commit-subject dump, no surrounding chatter, no code fences. Write explanatory prose bullets,
matching the style of the exemplar below (not a list of raw commit subjects).

## Style exemplar (last entries from CHANGELOG.md)
$StyleExemplar

## Commits in this release
$commitList

## $diffSection
"@
}
