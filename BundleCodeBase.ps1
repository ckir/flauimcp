<#
 Copyright (c) 2026 Costas Kirgoussios

 This software is licensed under the PolyForm Noncommercial License 1.0.0.
 You may obtain a copy of the License at:
 https://polyformproject.org/licenses/noncommercial/1.0.0/
#>

# Bundle this repo into a single text file for upload to agy's web interface.
#
# dir-to-text writes "<current-folder-name>.txt" into the current directory,
# strictly respecting every .gitignore (--use-gitignore). Run it from the repo root:
#
#   ./BundleCodeBase.ps1               # code only (docs/ excluded)
#   ./BundleCodeBase.ps1 -IncludeDocs  # also bundle docs/superpowers (plans/specs)
#   ./BundleCodeBase.ps1 -WhatIf       # dry run: preview bundle configuration

[CmdletBinding()]
param(
    # Include docs/ (plans + specs under docs/superpowers) so agy has design context.
    [switch]$IncludeDocs,

    # Preview bundle configuration and output path without executing dir-to-text.
    [switch]$WhatIf
)

$folderName = if ($PSScriptRoot) { (Get-Item $PSScriptRoot).Name } else { (Get-Item .).Name }
$bundle = "$folderName.txt"

# Excludes tuned for this C#/.NET solution:
#   bin/obj      - per-project build output
#   dist         - publish output (holds the ~120 MB self-contained flaui-mcp.exe)
#   .vs/.idea    - IDE state
#   TestResults  - test run artifacts
#   *.user       - Visual Studio per-user project settings
# --use-gitignore already drops most of these, but the explicit excludes are a
# belt-and-suspenders guard in case .gitignore is incomplete.
$exclude = @('bin', 'obj', 'dist', '.git', '.vs', '.idea', 'TestResults', '*.user')

# docs/ is excluded by default to keep the bundle code-focused; -IncludeDocs keeps it.
if (-not $IncludeDocs) { $exclude += 'docs' }

if ($WhatIf) {
    $scope = if ($IncludeDocs) { "code + docs" } else { "code only" }
    Write-Host "[WhatIf] Would bundle ($scope) into '$bundle' excluding: $($exclude -join ', ')"
    exit 0
}

Remove-Item $bundle -ErrorAction SilentlyContinue

$dtArgs = @('--use-gitignore')
foreach ($e in $exclude) { $dtArgs += '-e'; $dtArgs += $e }
$dtArgs += '.'

dir-to-text @dtArgs

if (Test-Path $bundle) {
    $item = Get-Item $bundle
    $kb = [math]::Round($item.Length / 1KB, 1)
    $scope = if ($IncludeDocs) { "code + docs" } else { "code only" }
    Write-Host "Bundled ($scope) -> $($item.FullName)  (${kb} KB)" -ForegroundColor Green
} else {
    Write-Warning "dir-to-text did not produce $bundle"
}
