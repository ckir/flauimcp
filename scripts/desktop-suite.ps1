<#
.SYNOPSIS
Run the LEASE-EXEMPT half of the Desktop (UIA) suite and write a dated report.

.DESCRIPTION
The Desktop suite is deliberately not a CI job — GitHub-hosted runners have no interactive desktop session,
so it can only run on a real machine. The consequence is that it runs almost never, and that has a measured
cost: on 2026-09-09 `DesktopWakeTests.Waking_hydrates_the_tree_while_held` had not run since the v1.0 gate on
2026-07-29, and in that window a hand measurement contradicting it was believed and propagated into the
driving skill, two human-facing docs and the ROADMAP across ~20 commits. One run of that one test settled it
in 20 seconds. The gap was not the test; it was that nothing ever ran it.

SCOPE: `Category=Desktop` MINUS `SyntheticInput`. Tests that drive real keystrokes need a human-granted input
lease (`flaui-mcp unlock`), which a scheduled job cannot obtain and should not try to — this script therefore
covers the majority that need only an interactive session. Individual tests may still self-skip; the report
records that honestly rather than counting a skip as a pass.

PRECONDITIONS this checks before wasting ~10 minutes:
  - no process launched from this repo holds the build output (MSB3027), reusing Get-RepoBuildLockHolder
  - an interactive session exists

.PARAMETER ReportDir
Where to write the dated report. Default: artifacts/desktop-runs (git-ignored).

.PARAMETER Filter
Override the test filter. Default is the lease-exempt Desktop subset.

.EXAMPLE
pwsh -File scripts/desktop-suite.ps1

.EXAMPLE
# Register a weekly run (Sunday 03:00). Runs only when a session is present; it is a no-op otherwise.
$pwsh = (Get-Command pwsh).Source
$repo = 'C:\Users\user\Development\c#\flauimcp'
$action  = New-ScheduledTaskAction -Execute $pwsh -Argument "-NoProfile -File `"$repo\scripts\desktop-suite.ps1`"" -WorkingDirectory $repo
$trigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek Sunday -At 3am
Register-ScheduledTask -TaskName 'flaui-mcp desktop suite' -Action $action -Trigger $trigger -RunLevel Limited
#>
[CmdletBinding()]
param(
    [string]$ReportDir,
    [string]$Filter = 'Category=Desktop&Category!=SyntheticInput&Category!=KnownDefect&Category!=Measurement'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'lib/release-lib.ps1')

if (-not $ReportDir) { $ReportDir = Join-Path $repoRoot 'artifacts/desktop-runs' }
New-Item -ItemType Directory -Force -Path $ReportDir | Out-Null
$stamp  = Get-Date -Format 'yyyy-MM-dd_HHmmss'
$report = Join-Path $ReportDir "desktop-$stamp.txt"

function Write-Both { param([string]$Text) Write-Host $Text; Add-Content -LiteralPath $report -Value $Text }

Write-Both "flaui-mcp Desktop suite — $(Get-Date -Format 'u')"
Write-Both "commit : $(git -C $repoRoot rev-parse --short HEAD 2>$null)"
Write-Both "filter : $Filter"
Write-Both ''

# --- precondition 1: build lock -------------------------------------------------------------------
$holders = @(Get-RepoBuildLockHolder -RepoRoot $repoRoot)
if ($holders.Count -gt 0) {
    Write-Both 'ABORTED — a process launched from this repo holds its build output open:'
    foreach ($h in $holders) { Write-Both "  PID $($h.Id)  $($h.Path)" }
    Write-Both "Clear it, then re-run:  $(($holders | ForEach-Object { "taskkill /F /PID $($_.Id)" }) -join '; ')"
    exit 2
}

# --- precondition 2: an interactive session ---------------------------------------------------------
# UIA needs a real session. Under Task Scheduler with no one logged on there is nothing to drive, and the
# failures that produces look like product defects rather than a missing desktop.
if ([Environment]::UserInteractive -ne $true) {
    Write-Both 'ABORTED — no interactive session; the Desktop suite cannot drive UIA here. Not a failure.'
    exit 3
}

Write-Both 'Preconditions OK. Building (Debug) and running the lease-exempt Desktop subset...'
Write-Both ''

Push-Location $repoRoot
try {
    $build = & dotnet build FlaUI.Mcp.slnx -c Debug 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        Write-Both 'BUILD FAILED:'
        Write-Both $build
        exit 1
    }

    # Never --no-build: a deleted test still runs from a stale DLL.
    $out = & dotnet test FlaUI.Mcp.slnx --filter $Filter 2>&1 | Out-String
    $code = $LASTEXITCODE
    Write-Both $out

    # Read the COUNTS, not just the exit code: a test file that fails to PARSE removes its own tests and
    # still reports failed=0. Total is the number that catches that.
    $m = [regex]::Match($out, 'Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)')
    if ($m.Success) {
        Write-Both ''
        Write-Both ("SUMMARY  failed={0} passed={1} skipped={2} total={3}" -f `
            $m.Groups[1].Value, $m.Groups[2].Value, $m.Groups[3].Value, $m.Groups[4].Value)
        if ([int]$m.Groups[3].Value -gt 0) {
            Write-Both 'NOTE: skipped > 0. A skip is not a pass — check whether those tests needed a lease.'
        }
    } else {
        Write-Both 'WARNING: could not parse a test summary line; treat this run as INCONCLUSIVE.'
    }

    Write-Both ''
    Write-Both "Report: $report"
    exit $code
}
finally { Pop-Location }
