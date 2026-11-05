#!/usr/bin/env pwsh
<#
 Copyright (c) 2026 Costas Kirgoussios

 This software is licensed under the PolyForm Noncommercial License 1.0.0.
 You may obtain a copy of the License at:
 https://polyformproject.org/licenses/noncommercial/1.0.0/
#>

# Regenerates plugins/flaui-mcp/{skills,scripts} from the repo's .claude source of truth.
# The plugin's manifests (.claude-plugin/plugin.json, hooks/hooks.json) are hand-authored and NOT touched.

[CmdletBinding()]
param(
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$root   = Split-Path -Parent $PSScriptRoot
$src    = Join-Path $root '.claude'
$plugin = Join-Path $root 'plugins/flaui-mcp'

$skillDst = Join-Path $plugin 'skills'
$scriptDst = Join-Path $plugin 'scripts'

if ($WhatIf) {
    Write-Host "[WhatIf] Would regenerate $skillDst (driving-flaui-mcp, flaui-learn, flaui-curate) and $scriptDst/flaui-curate-nudge.sh from $src"
    exit 0
}

if (Test-Path $skillDst) { Remove-Item -Recurse -Force $skillDst }
New-Item -ItemType Directory -Force -Path $skillDst | Out-Null
foreach ($s in 'driving-flaui-mcp','flaui-learn','flaui-curate') {
  Copy-Item -Recurse -Force (Join-Path $src "skills/$s") (Join-Path $skillDst $s)
}

New-Item -ItemType Directory -Force -Path $scriptDst | Out-Null
Copy-Item -Force (Join-Path $src 'hooks/flaui-curate-nudge.sh') (Join-Path $scriptDst 'flaui-curate-nudge.sh')

Write-Host "Plugin skills + hook snapshot regenerated under plugins/flaui-mcp/."
