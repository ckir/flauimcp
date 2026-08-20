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

    # ⚠ THE LAYOUT MOVED, AND THIS GATE DID NOT NOTICE FOR A WHOLE RELEASE. `install --agent claude` used
    # to drop files into <config>\skills\flaui-mcp\; the registration rework made it register a LOCAL
    # MARKETPLACE instead, so the payload now lands under
    #   <config>\plugins\cache\flaui-mcp-marketplace\flaui-mcp\<version>\
    # This script kept asserting the old path. It was never run, so nothing surfaced that — it failed on
    # the FIRST real execution, during the v1.0 pre-release smoke on 2026-08-20. Glob the version rather
    # than pinning it, so a version bump cannot re-break the gate the same way.
    $root = Get-ChildItem -Directory (Join-Path $claude 'plugins\cache\flaui-mcp-marketplace\flaui-mcp') -EA SilentlyContinue |
            Sort-Object Name -Descending | Select-Object -First 1 | ForEach-Object { $_.FullName }
    Check 'plugin payload deployed' ($null -ne $root)
    Check 'manifest deployed'   ($null -ne $root -and (Test-Path (Join-Path $root 'plugin.json')))
    Check 'skill deployed'      ($null -ne $root -and (Test-Path (Join-Path $root 'skills\driving-flaui-mcp\SKILL.md')))
    Check 'mcp server declared' ($null -ne $root -and (Test-Path (Join-Path $root '.mcp.json')))
    Check 'hooks deployed'      ($null -ne $root -and (Test-Path (Join-Path $root 'hooks\hooks.json')))

    Write-Host "`n== version lockstep ==" -ForegroundColor Cyan
    $exeVer      = (& $Exe --version) -replace '^flaui-mcp\s+',''
    $manifestVer = if ($root) { (Get-Content (Join-Path $root 'plugin.json') -Raw | ConvertFrom-Json).version } else { '<no manifest>' }
    Check "manifest $manifestVer matches exe $exeVer" ($exeVer.StartsWith($manifestVer))
    # The payload DIRECTORY is named for the version too - a third place that must agree, and the one a
    # stale cache would betray.
    Check "payload dir $(if ($root) { Split-Path $root -Leaf } else { '<none>' }) matches exe $exeVer" `
          ($null -ne $root -and $exeVer.StartsWith((Split-Path $root -Leaf)))

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
    # ⚠ ASSERT DEREGISTRATION, NOT DELETION. The old check was `-not (Test-Path $root)` and it is the WRONG
    # question: MEASURED 2026-08-20, the agent's own marketplace cache KEEPS the payload directory after an
    # uninstall and drops an `.orphaned_at` marker beside it. That is the agent's housekeeping, not ours, and
    # our uninstall did its job - `installed_plugins.json` comes back `{"plugins": {}}`. Asserting the
    # directory is gone would make this gate fail forever on correct behaviour.
    $reg = Join-Path $claude 'plugins\installed_plugins.json'
    $stillRegistered = (Test-Path $reg) -and
                       ((Get-Content $reg -Raw | ConvertFrom-Json).plugins.PSObject.Properties.Name -match '^flaui-mcp@')
    Check 'plugin deregistered' (-not $stillRegistered)
    Check 'payload orphaned or gone' ($null -eq $root -or -not (Test-Path $root) -or (Test-Path (Join-Path $root '.orphaned_at')))
}
finally {
    Pop-Location
    Remove-Item Env:CLAUDE_CONFIG_DIR, Env:FLAUI_MCP_STATE_DIR, Env:FLAUI_MCP_DATA_DIR, Env:FLAUI_MCP_AGY_PLUGINS_DIR -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $sandbox -ErrorAction SilentlyContinue
}

Write-Host "`n== collision: a seeded marketplace copy must end up DISABLED ==" -ForegroundColor Cyan
$sandbox2 = Join-Path ([IO.Path]::GetTempPath()) "flaui-smoke2-$([guid]::NewGuid())"
$claude2  = Join-Path $sandbox2 'claude'
$state2   = Join-Path $sandbox2 'state'
$outside2 = Join-Path $sandbox2 'outside'
New-Item -ItemType Directory -Force -Path $claude2, $state2, $outside2 | Out-Null
$env:CLAUDE_CONFIG_DIR   = $claude2
$env:FLAUI_MCP_STATE_DIR = $state2
$env:FLAUI_MCP_DATA_DIR  = Join-Path $sandbox2 'data'
Push-Location $outside2
try {
    claude plugin marketplace add ckir/flauimcp 2>&1 | Out-Host
    claude plugin install flaui-mcp@flaui-mcp --scope user 2>&1 | Out-Host

    $before = claude plugin list --json | ConvertFrom-Json | Where-Object { $_.id -eq 'flaui-mcp@flaui-mcp' }
    # A gate that cannot run has NOT passed. Counting the skip as a failure is the whole point:
    # otherwise the one scenario this gate exists for is the one it silently stops checking.
    Check 'the marketplace copy could be seeded (the gate is able to run at all)' ($null -ne $before)
    if ($null -eq $before) {
        Write-Warning "could not seed the marketplace copy — the collision gate did NOT run. This is a FAILURE, not a skip."
    } else {
        Check 'seeded marketplace copy starts enabled' ($before.enabled -eq $true)

        & $Exe install --agent claude | Out-Host

        $after = claude plugin list --json | ConvertFrom-Json | Where-Object { $_.id -eq 'flaui-mcp@flaui-mcp' }
        # THE NEGATIVE. Without this the gate passes when the disable never ran.
        Check 'marketplace copy is DISABLED after install' ($after.enabled -eq $false)

        & $Exe uninstall --agent claude | Out-Host
        $restored = claude plugin list --json | ConvertFrom-Json | Where-Object { $_.id -eq 'flaui-mcp@flaui-mcp' }
        Check 'marketplace copy is RESTORED after uninstall' ($restored.enabled -eq $true)
        Check 'the marker was consumed' (-not (Test-Path (Join-Path $state2 'disabled-plugins.json')))
    }
}
finally {
    claude plugin uninstall flaui-mcp@flaui-mcp --scope user 2>&1 | Out-Null
    claude plugin marketplace remove flaui-mcp 2>&1 | Out-Null
    Pop-Location
    Remove-Item Env:CLAUDE_CONFIG_DIR, Env:FLAUI_MCP_STATE_DIR, Env:FLAUI_MCP_DATA_DIR -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $sandbox2 -ErrorAction SilentlyContinue
}

if ($failed.Count) { Write-Host "`n$($failed.Count) CHECK(S) FAILED" -ForegroundColor Red; exit 1 }
Write-Host "`nALL CHECKS PASSED" -ForegroundColor Green
