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
    # ⚠ Sort as VERSIONS, not as text. `Sort-Object Name` is a string sort, so `1.2.0` sorts ABOVE
    # `1.10.0` and `0.9.0` above `0.20.0` — the gate would silently select a stale payload directory and
    # then assert against it. MEASURED: over 0.9.0 / 0.20.0 / 1.2.0 / 1.10.0 the string sort picks 1.2.0
    # and the version sort picks 1.10.0. Not reachable today because the sandbox only ever installs one
    # version, which is exactly why it would have gone unnoticed until a release broke it. The
    # `-as [version]` filter keeps a stray non-version directory from throwing the sort.
    $root = Get-ChildItem -Directory (Join-Path $claude 'plugins\cache\flaui-mcp-marketplace\flaui-mcp') -EA SilentlyContinue |
            Where-Object { $_.Name -as [version] } |
            Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 | ForEach-Object { $_.FullName }
    Check 'plugin payload deployed' ($null -ne $root)
    Check 'manifest deployed'   ($null -ne $root -and (Test-Path (Join-Path $root 'plugin.json')))
    Check 'skill deployed'      ($null -ne $root -and (Test-Path (Join-Path $root 'skills\driving-flaui-mcp\SKILL.md')))
    Check 'mcp server declared' ($null -ne $root -and (Test-Path (Join-Path $root '.mcp.json')))
    Check 'hooks deployed'      ($null -ne $root -and (Test-Path (Join-Path $root 'hooks\hooks.json')))

    Write-Host "`n== version lockstep ==" -ForegroundColor Cyan
    $exeVer      = (& $Exe --version) -replace '^flaui-mcp\s+',''
    $manifestVer = if ($root) { (Get-Content (Join-Path $root 'plugin.json') -Raw | ConvertFrom-Json).version } else { '<no manifest>' }
    # ⚠ NOT StartsWith. MEASURED: '1.10.0'.StartsWith('1.1') is TRUE, so a stale 1.1 payload would PASS
    # against a 1.10.0 exe — a gate reporting success while the condition it names is false, which is the
    # exact false-GREEN class this file exists to catch. StartsWith was here to tolerate a 4-part exe
    # version (0.20.0.0); normalise to three parts and compare exactly instead, so the tolerance costs no
    # accuracy. A version with fewer than three parts fails the compare, which is the safe direction.
    $exeVer3 = ($exeVer -split '\.')[0..2] -join '.'
    Check "manifest $manifestVer matches exe $exeVer" ($manifestVer -eq $exeVer3)
    # The payload DIRECTORY is named for the version too - a third place that must agree, and the one a
    # stale cache would betray.
    Check "payload dir $(if ($root) { Split-Path $root -Leaf } else { '<none>' }) matches exe $exeVer" `
          ($null -ne $root -and (Split-Path $root -Leaf) -eq $exeVer3)

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
    # ⚠ THE ONLY GATE FOR THIS SUBPROJECT DEPENDS ON A THIRD-PARTY CLI, A GITHUB CLONE AND THE NETWORK.
    # It cannot run offline, cannot run in CI, and breaks if ckir/flauimcp is renamed or made private.
    # That is accepted rather than solved - a local fake marketplace would test a fake instead of the real
    # CLI behaviour, and the CLI's real behaviour is precisely what this defect turned on. The consequence
    # is that a green result is only evidence about the CLI of a particular day, so the version goes in
    # the recorded output.
    Write-Host "claude CLI: $(claude --version 2>&1)" -ForegroundColor DarkGray

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

        # ⚠⚠ THE FORCING CHECK FOR LAYER (b). Everything above passes under fix (a) ALONE - the qualified
        # sweep means nothing evicts the copy, so the reinstall fallback would ship completely
        # unexercised. This block SIMULATES an eviction the qualified sweep no longer causes (the user's
        # own tooling, a future registrar change, a claude behaviour shift) and then asserts the copy
        # comes back. It also removes the MARKETPLACE, which is the harder case: the recorded SOURCE is
        # the only thing that can rebuild it. MEASURED: `claude plugin install flaui-mcp@flaui-mcp` exits
        # 1 once the marketplace is removed.
        Write-Host "`n== collision: an EVICTED copy must be REINSTALLED on uninstall ==" -ForegroundColor Cyan

        claude plugin marketplace add ckir/flauimcp 2>&1 | Out-Null
        claude plugin install flaui-mcp@flaui-mcp --scope user 2>&1 | Out-Host
        & $Exe install --agent claude | Out-Host

        $seeded = claude plugin list --json | ConvertFrom-Json | Where-Object { $_.id -eq 'flaui-mcp@flaui-mcp' }
        # A gate that cannot run has NOT passed. Same rule as the seeding check above.
        Check 'the reinstall scenario could be set up (the check is able to run at all)' `
              ($null -ne $seeded -and $seeded.enabled -eq $false)
        if ($null -eq $seeded -or $seeded.enabled -ne $false) {
            Write-Warning "could not re-seed a DISABLED marketplace copy - the reinstall check did NOT run. This is a FAILURE, not a skip."
        } else {
            # The marker must carry the source, or the reinstall has nothing to rebuild from.
            $marker = Get-Content (Join-Path $state2 'disabled-plugins.json') -Raw | ConvertFrom-Json
            Check 'the marker is version 2' ($marker.version -eq 2)
            Check 'the marker recorded the marketplace source' `
                  ($null -ne $marker.disabled[0].marketplace -and $marker.disabled[0].marketplace.source)

            # SIMULATE THE EVICTION, then remove the marketplace so only the recorded SOURCE can recover.
            claude plugin uninstall flaui-mcp@flaui-mcp 2>&1 | Out-Host
            claude plugin marketplace remove flaui-mcp 2>&1 | Out-Host
            $gone = claude plugin list --json | ConvertFrom-Json | Where-Object { $_.id -eq 'flaui-mcp@flaui-mcp' }
            Check 'the copy really was evicted (the precondition holds)' ($null -eq $gone)

            & $Exe uninstall --agent claude | Out-Host
            $rebuilt = claude plugin list --json | ConvertFrom-Json | Where-Object { $_.id -eq 'flaui-mcp@flaui-mcp' }
            Check 'the evicted copy was REINSTALLED from the recorded marketplace' ($null -ne $rebuilt)
            Check 'and re-enabled' ($rebuilt.enabled -eq $true)
        }

        # ⚠ THE BRANCH THAT PROTECTS THE USER FROM A SILENTLY WRONG INSTALL. A repointed alias must be
        # REFUSED, not followed - "present by alias" is not "is the thing we recorded".
        Write-Host "`n== collision: a REPOINTED alias must be refused, not followed ==" -ForegroundColor Cyan

        claude plugin marketplace add ckir/flauimcp 2>&1 | Out-Null
        claude plugin install flaui-mcp@flaui-mcp --scope user 2>&1 | Out-Null
        & $Exe install --agent claude | Out-Host
        claude plugin uninstall flaui-mcp@flaui-mcp 2>&1 | Out-Null

        # Repoint the alias at a DIFFERENT source by hand - the registry is what restore compares against.
        $registry = Join-Path $claude2 'plugins\known_marketplaces.json'
        $repointed = $false
        if (Test-Path $registry) {
            $json = Get-Content $registry -Raw | ConvertFrom-Json
            if ($json.'flaui-mcp') {
                $json.'flaui-mcp'.source.repo = 'someone-else/a-different-fork'
                $json | ConvertTo-Json -Depth 10 | Set-Content $registry
                $repointed = $true
            }
        }
        Check 'the alias could be repointed (the check is able to run at all)' $repointed
        if (-not $repointed) {
            Write-Warning "could not repoint the alias - the refusal check did NOT run. This is a FAILURE, not a skip."
        } else {
            $out = & $Exe uninstall --agent claude 2>&1 | Out-String
            Write-Host $out
            Check 'restore REFUSED to install from a repointed alias' ($out -match 'someone-else/a-different-fork')
            $wrong = claude plugin list --json | ConvertFrom-Json | Where-Object { $_.id -eq 'flaui-mcp@flaui-mcp' }
            Check 'and installed nothing' ($null -eq $wrong)
        }
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
