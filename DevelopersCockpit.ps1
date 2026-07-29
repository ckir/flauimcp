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

.PARAMETER WhatIf
    Dry run. Prints the exact canonical command every action WOULD delegate to, and executes
    NOTHING — no build, no test, no push, no probe. The design calls the action table "the review
    seam: eyeball each Key -> canonical command"; this makes that seam inspectable at runtime
    instead of only by reading the source.

    The rule covers every ACTION: no delegated command and no probe runs, not even the read-only
    health checks. Predictability beats convenience here — an operator must never have to remember
    which subset of a dry run was actually live.

    ONE exception, stated because an unstated one is how a contract rots: the banner still shells
    out to `git rev-parse`/`git describe` to render the header. That is chrome, not an action you
    selected, and suppressing it would print "?" instead of your branch. Nothing you press runs.

    Owner-gates are announced but NOT prompted under -WhatIf. A confirmation guarding a no-op is
    theatre, and worse, it trains the operator to type the confirmation word reflexively — which
    is precisely the habit the gate exists to prevent.

.EXAMPLE
    pwsh -File DevelopersCockpit.ps1

.EXAMPLE
    pwsh -File DevelopersCockpit.ps1 -WhatIf
    Browse the menu and see what each key would run, without running any of it.
#>
[CmdletBinding()]
param(
    [switch]$WhatIf
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Delegated commands resolve from the repo root regardless of the caller's cwd.
Set-Location $PSScriptRoot

# Hoisted to script scope so the delegation chokepoint and the gates can both see it.
$script:WhatIf = [bool]$WhatIf

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
    #
    # This is the single chokepoint every delegated command passes through, which is what makes
    # -WhatIf trustworthy: one guard here covers every Cmd row AND every handler, so no action can
    # acquire a live side effect without also acquiring a dry-run bypass.
    if ($script:WhatIf) { Write-C "  WHATIF > $Cmd" 'DarkCyan'; return }

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

    # Under -WhatIf nothing downstream executes, so prompting would be theatre — and it would train
    # the operator to type the confirmation word reflexively, which is the exact habit this gate
    # exists to prevent. Announce the bypass and let the caller through to Invoke-Cmd, which prints.
    if ($script:WhatIf) { Write-C '  (WHATIF — gate skipped; nothing below will execute)' 'DarkCyan'; return $true }

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
    [pscustomobject]@{ Key='T'; Tier=0; Desc='Test (unit)';            Note='';       Cmd='dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"' }
    [pscustomobject]@{ Key='R'; Tier=0; Desc='Regen plugin snapshot';  Note='writes'; Cmd='pwsh -File scripts/build-plugin.ps1' }
    [pscustomobject]@{ Key='N'; Tier=0; Desc='Scaffold new tool';      Note='writes'; Handler={ Invoke-Scaffold } }

    # [2] QUALITY GATE
    [pscustomobject]@{ Key='G'; Tier=1; Desc='Dev gate (build+test+Pester)'; Note=''; Handler={ Invoke-DevGate } }
    [pscustomobject]@{ Key='E'; Tier=1; Desc='Pester (scripts/)';      Note='';       Cmd='pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 5.8.0; Invoke-Pester -Path scripts/ -CI"' }
    [pscustomobject]@{ Key='I'; Tier=1; Desc='Install smoke';          Note='';       Cmd='pwsh -File scripts/install-smoke.ps1' }
    [pscustomobject]@{ Key='K'; Tier=1; Desc='Desktop suite (the v1.0 gate)'; Note='CONSOLE+LEASE'; Handler={ Invoke-DesktopSuite } }

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
    Invoke-Cmd 'dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"'
    Invoke-Cmd 'pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 5.8.0; Invoke-Pester -Path scripts/ -CI"'
}

# The Category=Desktop suite is Track A's definition of done, and it is the one gate that CANNOT be run
# unattended or headless. Two properties make stating the preconditions load-bearing rather than polite:
# SendInput does not deliver over RDP, and the lease-guarded tests are [SkippableFact] -- xUnit exits 0
# on a skipped test, so a lease-less run prints a clean pass having bypassed every assertion that
# matters. That failure mode is silent, which is why the skipped count is called out as part of the gate.
function Invoke-DesktopSuite {
    Write-C '  PRECONDITIONS — all four, or the result is meaningless:' 'Yellow'
    Write-C '    1. a PHYSICAL console session, connected and unlocked — NOT RDP.' 'Yellow'
    Write-C '       SendInput RETURNS SUCCESS over RDP (non-zero, GetLastError=0) but the keystroke can' 'DarkGray'
    Write-C '       silently fail to LAND — queued is not delivered. So an RDP run does not error, it' 'DarkGray'
    Write-C '       produces wrong results, which is worse. See the RDP-vs-console rule in the' 'DarkGray'
    Write-C '       driving-flaui-mcp skill.' 'DarkGray'
    Write-C '       check with: qwinsta   -- read the STATE column, NOT $env:SESSIONNAME, which is a' 'DarkGray'
    Write-C '       per-process snapshot frozen at launch and keeps reporting the old session.' 'DarkGray'
    Write-C '    2. an active input lease, granted by a human:' 'Yellow'
    Write-C '       flaui-mcp unlock --minutes 45 --allow-shells' 'DarkGray'
    Write-C '       --allow-shells is MANDATORY, or the terminal-tab tests fail for an unrelated reason' 'DarkGray'
    Write-C '       it must stay live for the WHOLE run (~12 min) -- if it lapses midway, the' 'DarkGray'
    Write-C '       SyntheticInput tests silently start skipping instead of asserting' 'DarkGray'
    Write-C '    3. step away — the suite drives the real mouse and keyboard throughout' 'Yellow'
    Write-C '    4. no orphaned testhost.exe / FlaUI.Mcp.TestApp.exe left from a previous run:' 'Yellow'
    Write-C '       Get-Process testhost,FlaUI.Mcp.TestApp -EA SilentlyContinue | % { taskkill /PID $_.Id /T /F }' 'DarkGray'
    Write-C '       do NOT kill Code.exe by name — an ambient VS Code is a legitimate part of the' 'DarkGray'
    Write-C '       hermeticity case, and killing it makes the suite prove green in a pristine' 'DarkGray'
    Write-C '       environment it quietly created for itself' 'DarkGray'
    Write-Host ''
    Write-C '  A "Skipped" count above ZERO means the lease was not active: the run proved NOTHING.' 'Red'
    Write-Host ''

    if ($script:WhatIf) {
        Write-C '  (WHATIF — gate skipped; nothing below will execute)' 'DarkCyan'
    } else {
        $ans = Read-Host "  type 'run' (exactly) to proceed"
        if ($ans -cne 'run') { Write-C '  aborted.' 'DarkGray'; return }
    }

    Invoke-Cmd 'dotnet test FlaUI.Mcp.slnx -c Release --filter "Category=Desktop&Category!=KnownDefect&FullyQualifiedName!~PopupGrafting"'
    Invoke-Cmd 'dotnet test FlaUI.Mcp.slnx -c Release --filter "FullyQualifiedName~PopupGrafting"'

    Write-Host ''
    Write-C '  READ BOTH SUMMARIES ABOVE — the gate is:' 'Cyan'
    Write-C '    main suite:    109 passed / 0 failed / 0 SKIPPED' 'Cyan'
    Write-C '    PopupGrafting:   1 passed / 0 failed / 0 SKIPPED' 'Cyan'
    Write-C '  Green here means the SUITE passed, NOT that the product has no known defects:' 'DarkGray'
    Write-C '  Category=KnownDefect is filtered out above, and each of those is a filed repro in' 'DarkGray'
    Write-C '  docs/fix-the-tool-backlog/. Check whether one still reproduces with:' 'DarkGray'
    Write-C '    dotnet test -c Release --filter "Category=KnownDefect"' 'DarkGray'
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
    # Probes are read-only, so running them under -WhatIf would be harmless -- but "nothing executes"
    # has to hold without exceptions, or the operator is left having to remember which parts of a dry
    # run were secretly live. List what would be probed instead.
    if ($script:WhatIf) {
        Write-C '  WHATIF — would probe, nothing executed:' 'DarkCyan'
        foreach ($p in $script:HealthProbes) { Write-C ('    {0,-20} {1}' -f $p.Name, $p.Cmd) 'DarkCyan' }
        Write-C ('    {0,-20} {1}' -f 'Pester', 'Get-Module -ListAvailable Pester (pinned 5.8.0)') 'DarkCyan'
        return
    }

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
    # Loud, not subtle: mistaking a dry run for a live one is the failure mode worth preventing,
    # and it is far likelier in the direction of "I thought that had run".
    if ($script:WhatIf) { Write-C '*** -WhatIf — commands are PRINTED, NOT EXECUTED ***' 'DarkCyan' }
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
