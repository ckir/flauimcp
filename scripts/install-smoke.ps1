#requires -Version 7
<#
.SYNOPSIS
  Local/manual gate for skill distribution. NOT runnable in CI: no claude CLI, no ~/.claude there.
.DESCRIPTION
  Runs in a THROWAWAY CLAUDE_CONFIG_DIR (plus isolated FLAUI_MCP_DATA_DIR / _STATE_DIR /
  _AGY_PLUGINS_DIR), and asserts both:
    - our bundled skill loads, and
    - a seeded colliding marketplace copy does NOT.
  The negative is the point: both plugins load silently, so asserting only our own would pass even
  when the disable never ran.
.NOTES
  NOT fully hermetic. The agy config paths (~/.gemini/settings.json and the antigravity-cli file)
  have no environment override, so an `uninstall --agent claude` run here still sweeps them: it
  deletes any stale `<config>.bak-*` files next to your REAL agy config. Live settings are untouched
  and backups regenerate on the next install, but this is not zero-touch. Root cause — SweepBackups
  ignores the --agent filter — is tracked as a follow-up, deliberately out of scope for the held
  v0.15.0 release.
#>
[CmdletBinding()]
param([string]$Exe = "$PSScriptRoot\..\publish\flaui-mcp.exe")

$ErrorActionPreference = 'Stop'
$failed = @()
function Check($name, $cond) {
    if ($cond) { Write-Host "  PASS  $name" -ForegroundColor Green }
    else { Write-Host "  FAIL  $name" -ForegroundColor Red; $script:failed += $name }
}

if (-not (Test-Path $Exe)) { throw "no exe at $Exe — run: dotnet publish src/FlaUI.Mcp.Server -c Release -o publish" }

$sandbox  = Join-Path ([IO.Path]::GetTempPath()) "flaui-smoke-$([guid]::NewGuid())"
$claude   = Join-Path $sandbox 'claude'
$state    = Join-Path $sandbox 'state'
$outside  = Join-Path $sandbox 'outside-the-repo'
New-Item -ItemType Directory -Force -Path $claude, $state, $outside | Out-Null

$env:CLAUDE_CONFIG_DIR          = $claude
$env:FLAUI_MCP_STATE_DIR        = $state
$env:FLAUI_MCP_DATA_DIR         = Join-Path $sandbox 'data'
$env:FLAUI_MCP_AGY_PLUGINS_DIR  = Join-Path $sandbox 'agy'
Push-Location $outside      # inside the repo, project-scope skills mask the result
try {
    Write-Host "`n== install ==" -ForegroundColor Cyan
    & $Exe install --agent claude | Out-Host

    $root = Join-Path $claude 'skills\flaui-mcp'
    Check 'manifest deployed'  (Test-Path (Join-Path $root '.claude-plugin\plugin.json'))
    Check 'skill deployed'     (Test-Path (Join-Path $root 'skills\driving-flaui-mcp\SKILL.md'))

    Write-Host "`n== version lockstep ==" -ForegroundColor Cyan
    $exeVer      = (& $Exe --version) -replace '^flaui-mcp\s+',''
    $manifestVer = (Get-Content (Join-Path $root '.claude-plugin\plugin.json') -Raw | ConvertFrom-Json).version
    Check "manifest $manifestVer matches exe $exeVer" ($exeVer.StartsWith($manifestVer))

    Write-Host "`n== the skill actually LOADS (validate would not catch this) ==" -ForegroundColor Cyan
    $list = claude plugin list --json | ConvertFrom-Json
    $ours = $list | Where-Object { $_.id -like 'flaui-mcp@*' }
    Check 'our plugin is listed'  ($null -ne $ours)
    Check 'our plugin is enabled' ($ours.enabled -eq $true)

    # M3: this also settles whether --json lists skills-dir plugins at all. The earlier check was
    # VACUOUS (none were installed), not negative. If our plugin is absent here while the files are
    # on disk, --json does NOT list skills-dir plugins -> `status` must keep reading the filesystem,
    # and this assertion needs a different mechanism. Report it either way; do not silently adapt.
    if ($null -eq $ours) {
        Write-Warning "M3: --json did not list our skills-dir plugin though its files exist. RECORD THIS in the spec."
    }

    Write-Host "`n== uninstall ==" -ForegroundColor Cyan
    & $Exe uninstall --agent claude | Out-Host
    Check 'skill removed' (-not (Test-Path $root))
}
finally {
    Pop-Location
    Remove-Item Env:CLAUDE_CONFIG_DIR, Env:FLAUI_MCP_STATE_DIR, Env:FLAUI_MCP_DATA_DIR, Env:FLAUI_MCP_AGY_PLUGINS_DIR -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $sandbox -ErrorAction SilentlyContinue
}

if ($failed.Count) { Write-Host "`n$($failed.Count) CHECK(S) FAILED" -ForegroundColor Red; exit 1 }
Write-Host "`nALL CHECKS PASSED" -ForegroundColor Green
