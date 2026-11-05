<#
 Copyright (c) 2026 Costas Kirgoussios

 This software is licensed under the PolyForm Noncommercial License 1.0.0.
 You may obtain a copy of the License at:
 https://polyformproject.org/licenses/noncommercial/1.0.0/
#>

<#
.SYNOPSIS
License check + Build (Debug) + the headless test suite. The fast gate, used by the lefthook pre-push hook.

.DESCRIPTION
Exists as a SCRIPT rather than an inline hook command because inlining it did not survive the shell.
lefthook ran `dotnet build ... && dotnet test ... --filter "Category!=Desktop&..."`, both halves succeeded
and printed their results, and the hook still exited 127 (command not found) — the `&&` chain and the `!`/`&`
characters in the filter do not survive intact through the shell lefthook invokes on Windows. A hook that
fails AFTER a green gate is worse than no hook: it teaches --no-verify.

Debug on purpose. A Release build fails MSB3027 whenever a flaui-mcp.exe launched from this repo is running,
which is most of the time while developing this server, and a hook that fails in the common case gets
bypassed rather than fixed.

Desktop and SyntheticInput are excluded: they need an interactive session and (for input) a human-granted
lease, neither of which a git hook can assume. Use `just desktop` for the lease-exempt Desktop subset.

.PARAMETER WhatIf
Dry run: preview pre-push gate actions without building or running tests.
#>
[CmdletBinding()]
param(
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $repoRoot 'FlaUI.Mcp.slnx'
$headerFile = Join-Path $repoRoot 'header.txt'

if ($WhatIf) {
    Write-Host "[WhatIf] Would verify license headers via addlicense, build FlaUI.Mcp.slnx (Debug), and run headless dotnet test suite."
    exit 0
}

# Verify license headers before compiling and testing.
if (Get-Command addlicense -ErrorAction SilentlyContinue) {
    & addlicense -check -f $headerFile -ignore '**/obj/**' -ignore '**/bin/**' -ignore '**/artifacts/**' $repoRoot
    if ($LASTEXITCODE -ne 0) {
        Write-Host ''
        Write-Host 'pre-push: license check FAILED. Source files are missing the license header.' -ForegroundColor Red
        Write-Host 'Run "just addlicense" or "addlicense -f header.txt ." to add headers.' -ForegroundColor Yellow
        exit 1
    }
} else {
    Write-Host 'pre-push: WARNING: "addlicense" not found in PATH; skipping license check.' -ForegroundColor Yellow
}

& dotnet build $sln -c Debug
if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host 'pre-push: build FAILED.' -ForegroundColor Red
    exit 1
}

# Never --no-build here: a deleted test still runs from a stale DLL.
& dotnet test $sln --filter 'Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect'
if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host 'pre-push: headless tests FAILED.' -ForegroundColor Red
    exit 1
}

exit 0
