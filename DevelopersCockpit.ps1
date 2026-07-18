#!/usr/bin/env pwsh
#requires -Version 7
<#
.SYNOPSIS
    DevelopersCockpit — a thin, interactive front-end over flaui-mcp's dev tasks.

.DESCRIPTION
    A single-keypress PowerShell menu that DELEGATES to the canonical task layer
    (`dotnet`, `scripts/*.ps1`, `git`, `gh`, `pwsh`) — it contains ZERO build/version/release
    logic of its own. The raw push and the GH-storage reset are guarded by a typed confirmation;
    release actions rely on release.ps1's own gates.

    Interactive-only (no -Action mode — dotnet + scripts already ARE the scriptable CLI).
    Dev-box tool, never shipped to an end user, so it freely assumes the dev toolchain.

    Design: docs/superpowers/specs/2026-07-18-developers-cockpit-design.md (owner + agy converged).

.EXAMPLE
    pwsh -File DevelopersCockpit.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Delegated commands resolve from the repo root regardless of the caller's cwd.
Set-Location $PSScriptRoot

# ---------------------------------------------------------------------------
# Presentation (honors NO_COLOR + non-TTY)
# ---------------------------------------------------------------------------
$script:Interactive = -not [Console]::IsOutputRedirected
$script:UseColor    = $script:Interactive -and -not $env:NO_COLOR

function Write-C([string]$Text = '', [string]$Color = 'Gray') {
    if ($script:UseColor) { Write-Host $Text -ForegroundColor $Color } else { Write-Host $Text }
}

# ---------------------------------------------------------------------------
# Delegation helper — surface a delegated command's failure without killing the menu
# ---------------------------------------------------------------------------
function Invoke-Cmd([string]$Cmd) {
    # $Cmd is always a hardcoded literal from the $Actions table (or a handler's own literal),
    # never operator free-text, so Invoke-Expression here runs OUR delegation strings.
    Write-C "  > $Cmd" 'DarkGray'
    Invoke-Expression $Cmd
    if ($LASTEXITCODE -ne 0) { throw "command exited with code $LASTEXITCODE" }
}

# ---------------------------------------------------------------------------
# Input + owner-gate helpers
# ---------------------------------------------------------------------------
function Read-Trimmed([string]$Prompt) { return (Read-Host $Prompt).Trim() }

# Owner-gate: print exactly what will run, require the literal case-sensitive word 'push'.
function Confirm-Owner([string[]]$Commands) {
    Write-C '  OWNER-GATED — this will run:' 'Yellow'
    foreach ($c in $Commands) { Write-C "    $c" 'Yellow' }
    $ans = Read-Host "  type 'push' (exactly) to proceed"
    if ($ans -cne 'push') { Write-C '  aborted.' 'DarkGray'; return $false }
    return $true
}

# ---------------------------------------------------------------------------
# Banner — display-only, best-effort (a missing source shows '?' / '(none)', never aborts)
# ---------------------------------------------------------------------------
function Get-CsprojVersion {
    try {
        $c = Get-Content -Raw -ErrorAction Stop -- 'src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj'
        if ($c -match '<Version>([^<]+)</Version>') { return $Matches[1] }
    } catch { }
    return '?'
}

function Get-Banner {
    $ver    = Get-CsprojVersion
    $branch = (git rev-parse --abbrev-ref HEAD 2>$null)
    if (-not $branch) { $branch = '?' }
    $tag    = (git describe --tags --match 'v*' --abbrev=0 2>$null)
    if (-not $tag)    { $tag = '(none)' }
    return "v$ver | $($branch.Trim()) | last $($tag.Trim())"
}

# ---------------------------------------------------------------------------
# Action set — PURE DATA (the review seam: eyeball each Key -> canonical command).
# Tier index: 0=[1] INNER LOOP, 1=[2] QUALITY GATE, 2=[3] SHIP & RELEASE, 3=[4] HOUSEKEEPING.
# Each row has EITHER a Cmd (string run through Invoke-Cmd) XOR a Handler (scriptblock).
# ---------------------------------------------------------------------------
$script:Tiers = @(
    '[1] INNER LOOP',
    '[2] QUALITY GATE',
    '[3] SHIP & RELEASE',
    '[4] HOUSEKEEPING'
)

$script:Actions = @(
    # [1] INNER LOOP
    [pscustomobject]@{ Key='B'; Tier=0; Desc='Build (Debug)';          Note='';       Cmd='dotnet build FlaUI.Mcp.slnx -c Debug' }
    [pscustomobject]@{ Key='T'; Tier=0; Desc='Test (unit)';            Note='';       Cmd='dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&Category!=SyntheticInput"' }
    [pscustomobject]@{ Key='R'; Tier=0; Desc='Regen plugin snapshot';  Note='writes'; Cmd='pwsh -File scripts/build-plugin.ps1' }
    [pscustomobject]@{ Key='N'; Tier=0; Desc='Scaffold new tool';      Note='writes'; Handler={ Invoke-Scaffold } }

    # [2] QUALITY GATE
    [pscustomobject]@{ Key='G'; Tier=1; Desc='Dev gate (build+test+Pester)'; Note=''; Handler={ Invoke-DevGate } }
    [pscustomobject]@{ Key='E'; Tier=1; Desc='Pester (scripts/)';      Note='';       Cmd='pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 5.8.0; Invoke-Pester -Path scripts/"' }
    [pscustomobject]@{ Key='I'; Tier=1; Desc='Install smoke';          Note='';       Cmd='pwsh -File scripts/install-smoke.ps1' }

    # [3] SHIP & RELEASE
    [pscustomobject]@{ Key='V'; Tier=2; Desc='Release preview (-WhatIf)'; Note='';    Cmd='pwsh -File scripts/release.ps1 -WhatIf' }
    [pscustomobject]@{ Key='C'; Tier=2; Desc='Cut release';            Note='writes'; Cmd='pwsh -File scripts/release.ps1' }
    [pscustomobject]@{ Key='P'; Tier=2; Desc='Push to origin';         Note='OWNER';  Handler={ Invoke-Push } }

    # [4] HOUSEKEEPING
    [pscustomobject]@{ Key='H'; Tier=3; Desc='Health check';           Note='';       Handler={ Invoke-HealthCheck } }
    [pscustomobject]@{ Key='D'; Tier=3; Desc='Bundle codebase';        Note='';       Cmd='pwsh -File BundleCodeBase.ps1' }
    [pscustomobject]@{ Key='A'; Tier=3; Desc='Reset GH Actions storage'; Note='OWNER'; Handler={ Invoke-ResetStorage } }
    [pscustomobject]@{ Key='Q'; Tier=3; Desc='Quit';                   Note='';       Handler={ $script:Quit = $true } }
)

# ---------------------------------------------------------------------------
# Handlers (rows that prompt / need an owner-gate / chain commands)
# ---------------------------------------------------------------------------
function Invoke-Scaffold {
    $name = Read-Trimmed '  new tool Name (PascalCase, e.g. DesktopFoo)'
    if (-not $name) { Write-C '  aborted.' 'DarkGray'; return }
    if ($name -notmatch '^[A-Z][A-Za-z0-9]+$') { Write-C '  Name must match ^[A-Z][A-Za-z0-9]+$' 'Yellow'; return }
    Invoke-Cmd "pwsh -File scripts/new-tool.ps1 -Name $name"
}

function Invoke-DevGate {
    Invoke-Cmd 'dotnet build FlaUI.Mcp.slnx -c Debug'
    Invoke-Cmd 'dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&Category!=SyntheticInput"'
    Invoke-Cmd 'pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 5.8.0; Invoke-Pester -Path scripts/"'
}

function Invoke-Push {
    # release.ps1 only pushes atomically-within-a-release; this is the orthogonal "sync my commits" action.
    if (-not (Confirm-Owner @('git push'))) { return }
    Invoke-Cmd 'git push'
}

function Invoke-ResetStorage {
    if (-not (Confirm-Owner @('pwsh -File GitHub_Actions_Storage_Reset.ps1'))) { return }
    Invoke-Cmd 'pwsh -File GitHub_Actions_Storage_Reset.ps1'
}

# Health check — probe each dev tool independently; one missing tool never stops the sweep.
$script:HealthProbes = @(
    [pscustomobject]@{ Name='pwsh';              Cmd='pwsh --version' }
    [pscustomobject]@{ Name='dotnet';            Cmd='dotnet --version' }
    [pscustomobject]@{ Name='git';               Cmd='git --version' }
    [pscustomobject]@{ Name='gh';                Cmd='gh --version' }
    [pscustomobject]@{ Name='claude';            Cmd='claude --version' }
    [pscustomobject]@{ Name='iscc (Inno Setup)'; Cmd='iscc' }  # no --version; presence is the signal, may be absent
)

function Invoke-HealthCheck {
    foreach ($p in $script:HealthProbes) {
        $exe = $p.Cmd.Split(' ')[0]
        if (-not (Get-Command $exe -ErrorAction SilentlyContinue)) {
            Write-C ('  [FAIL] {0,-20} (not on PATH)' -f $p.Name) 'Yellow'
            continue
        }
        try {
            $out = (& { Invoke-Expression $p.Cmd } 2>&1 | Select-Object -First 1)
            Write-C ('  [ ok ] {0,-20} {1}' -f $p.Name, (($out -join ' ') -replace '\s+', ' ').Trim()) 'Green'
        } catch {
            Write-C ('  [FAIL] {0,-20} {1}' -f $p.Name, $_.Exception.Message) 'Yellow'
        }
    }
    # Pester is a module, not a PATH exe — probe the pinned version separately.
    if (Get-Module -ListAvailable Pester | Where-Object { $_.Version -eq '5.8.0' }) {
        Write-C ('  [ ok ] {0,-20} 5.8.0' -f 'Pester') 'Green'
    } else {
        Write-C ('  [FAIL] {0,-20} 5.8.0 not installed' -f 'Pester') 'Yellow'
    }
}

# ---------------------------------------------------------------------------
# Render + main loop
# ---------------------------------------------------------------------------
function Render-Menu {
    if ($script:Interactive) { Clear-Host }
    Write-Host ''
    Write-C ('DEVELOPERS COCKPIT — flaui-mcp   [{0}]' -f (Get-Banner)) 'Cyan'
    Write-Host ''
    for ($t = 0; $t -lt $script:Tiers.Count; $t++) {
        Write-C $script:Tiers[$t] 'Magenta'
        foreach ($a in ($script:Actions | Where-Object { $_.Tier -eq $t })) {
            $note = if ($a.Note) { "  ($($a.Note))" } else { '' }
            Write-C ('   {0}   {1}{2}' -f $a.Key, $a.Desc, $note) 'Gray'
        }
        Write-Host ''
    }
}

$script:Quit = $false
while (-not $script:Quit) {
    Render-Menu
    $key = (Read-Host 'select').Trim().ToUpperInvariant()
    if ([string]::IsNullOrEmpty($key)) { continue }
    $row = $script:Actions | Where-Object { $_.Key -eq $key } | Select-Object -First 1
    if (-not $row) { Write-C "invalid key '$key', try again" 'Yellow'; Start-Sleep -Milliseconds 500; continue }

    # Run inside try/catch so any failure (thrown or non-zero exit) prints and the loop continues.
    # The `-and` short-circuits before touching $row.Cmd on a Handler row, which keeps StrictMode happy.
    try {
        if ($row.PSObject.Properties['Cmd'] -and $row.Cmd) { Invoke-Cmd $row.Cmd }
        elseif ($row.PSObject.Properties['Handler'] -and $row.Handler) { & $row.Handler }
    } catch {
        Write-C "[cockpit] Error: $($_.Exception.Message)" 'Red'
    }

    if (-not $script:Quit) {
        Write-Host ''
        [void](Read-Host 'Press Enter to continue')
    }
}
Write-C 'bye.' 'Cyan'
