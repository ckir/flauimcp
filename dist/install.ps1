<#
.SYNOPSIS  Download and silently run the FlaUI.Mcp installer.
.EXAMPLE   irm https://raw.githubusercontent.com/ckir/flauimcp/master/dist/install.ps1 | iex
#>
[CmdletBinding()]
param(
    [string] $Version = "latest",
    [string] $Owner   = "ckir",
    [string] $Repo    = "flauimcp"
)
$ErrorActionPreference = "Stop"

$api = if ($Version -eq "latest") {
    "https://api.github.com/repos/$Owner/$Repo/releases/latest"
} else {
    "https://api.github.com/repos/$Owner/$Repo/releases/tags/$Version"
}

Write-Host "Resolving FlaUI.Mcp release ($Version)..."
$release = Invoke-RestMethod -Uri $api -Headers @{ "User-Agent" = "flaui-mcp-installer" }
$asset = $release.assets | Where-Object { $_.name -eq "flaui-mcp-setup.exe" } | Select-Object -First 1
if (-not $asset) { throw "flaui-mcp-setup.exe not found in release '$($release.tag_name)'." }

$dest = Join-Path $env:TEMP "flaui-mcp-setup.exe"
Write-Host "Downloading $($asset.name) ($($release.tag_name))..."
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $dest -Headers @{ "User-Agent" = "flaui-mcp-installer" }

Write-Host "Running installer (silent)..."
$p = Start-Process -FilePath $dest -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "Installer exited with code $($p.ExitCode)." }
Write-Host "FlaUI.Mcp installed. Restart agy if you use it, so it loads the new tools."
