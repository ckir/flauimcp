# scripts/new-tool.ps1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Z][A-Za-z0-9]+$')][string]$Name,
    [switch]$ReadOnly,
    [string]$Class = 'InteractionTools',
    [string]$Area  = 'Interaction',
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$WhatIf
)
$ErrorActionPreference = 'Stop'
function Fail($m) { Write-Error $m -ErrorAction Continue; exit 1 }

$classPath = Join-Path $RepoRoot "src/FlaUI.Mcp.Server/Tools/$Class.cs"
$testPath  = Join-Path $RepoRoot "test/FlaUI.Mcp.Tests/$Area/${Name}Tests.cs"
if (-not (Test-Path $classPath)) { Fail "Tools class not found: $classPath" }
$src = Get-Content $classPath -Raw
if ($src -match "\b$Name\s*\(") { Fail "Method $Name already exists in $Class." }
if (Test-Path $testPath)        { Fail "Test file already exists: $testPath" }

$attr = if ($ReadOnly) { 'ReadOnly = true' } else { 'Destructive = true' }
$snake = ($Name -creplace '(?<!^)([A-Z])', '_$1').ToLower()

$method = @"

    [McpServerTool($attr), Description("TODO: one-line description of $snake.")]
    public Task<string> $Name(/* TODO params, e.g. string window, string @ref */)
    {
        // TODO: thin Server method. Put real UIA/logic in src/FlaUI.Mcp.Core.
        // State-changing tools MUST route through ToolResponse.GuardWrite(_options, ...) (enforces
        // --read-only-mode) and return ToolResponse.Ok(...); reads: Task.FromResult(ToolResponse.Ok(...)).
        throw new System.NotImplementedException("$Name not implemented yet.");
    }
"@

$idx = $src.LastIndexOf('}')
if ($idx -lt 0) { Fail "No closing brace found in $classPath." }
$newSrc = $src.Substring(0, $idx) + $method + "`n" + $src.Substring($idx)

$test = @"
using FlaUI.Mcp.Tests;
using Xunit;

namespace FlaUI.Mcp.Tests.$Area;

public class ${Name}Tests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _fx;
    public ${Name}Tests(TestAppFixture fx) => _fx = fx;

    [Fact]
    [Trait("Category", "Desktop")]   // remove if this tool needs no interactive desktop
    public void ${Name}_does_the_thing()
    {
        // TODO: arrange via _fx, act on the new tool, assert the result.
        Assert.True(false, "TODO: implement ${Name}_does_the_thing");
    }
}
"@

if ($WhatIf) {
    Write-Host "[WhatIf] would append $Name to $Class and create $testPath"
    exit 0
}
Set-Content -Path $classPath -Value $newSrc -NoNewline -Encoding UTF8
New-Item -ItemType Directory -Force -Path (Split-Path $testPath) | Out-Null
Set-Content -Path $testPath -Value $test -NoNewline -Encoding UTF8
Write-Host "Created tool stub $Name ($snake) in $Class and test $testPath."
Write-Host "Next: fill the stub + Core logic, run headless tests, then Desktop tests on an unlocked session, then update README + CHANGELOG."
exit 0
