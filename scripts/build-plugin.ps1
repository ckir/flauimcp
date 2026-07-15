#!/usr/bin/env pwsh
# Regenerates plugins/flaui-mcp/{skills,scripts} from the repo's .claude source of truth.
# The plugin's manifests (.claude-plugin/plugin.json, hooks/hooks.json) are hand-authored and NOT touched.
$ErrorActionPreference = 'Stop'
$root   = Split-Path -Parent $PSScriptRoot
$src    = Join-Path $root '.claude'
$plugin = Join-Path $root 'plugins/flaui-mcp'

$skillDst = Join-Path $plugin 'skills'
if (Test-Path $skillDst) { Remove-Item -Recurse -Force $skillDst }
New-Item -ItemType Directory -Force -Path $skillDst | Out-Null
foreach ($s in 'driving-flaui-mcp','flaui-learn','flaui-curate') {
  Copy-Item -Recurse -Force (Join-Path $src "skills/$s") (Join-Path $skillDst $s)
}

$scriptDst = Join-Path $plugin 'scripts'
New-Item -ItemType Directory -Force -Path $scriptDst | Out-Null
Copy-Item -Force (Join-Path $src 'hooks/flaui-curate-nudge.sh') (Join-Path $scriptDst 'flaui-curate-nudge.sh')

Write-Host "Plugin skills + hook snapshot regenerated under plugins/flaui-mcp/."
