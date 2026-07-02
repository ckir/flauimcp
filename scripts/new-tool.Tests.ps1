# scripts/new-tool.Tests.ps1
BeforeAll {
    $script:Repo = Split-Path -Parent $PSScriptRoot
    $script:Script = Join-Path $PSScriptRoot 'new-tool.ps1'
    $script:Sandbox = Join-Path ([IO.Path]::GetTempPath()) ("newtool_" + [guid]::NewGuid())
    New-Item -ItemType Directory -Force -Path (Join-Path $Sandbox 'src/FlaUI.Mcp.Server/Tools') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $Sandbox 'test/FlaUI.Mcp.Tests/Interaction') | Out-Null
    @'
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace FlaUI.Mcp.Server.Tools;

[McpServerToolType]
public sealed class InteractionTools
{
}
'@ | Set-Content (Join-Path $Sandbox 'src/FlaUI.Mcp.Server/Tools/InteractionTools.cs')
}
AfterAll { if (Test-Path $script:Sandbox) { Remove-Item -Recurse -Force $script:Sandbox } }

Describe 'new-tool.ps1' {
    It 'stamps a Destructive method + a test file, exit 0' {
        & $Script -Name 'DesktopFoo' -RepoRoot $Sandbox
        $LASTEXITCODE | Should -Be 0
        $cls = Get-Content (Join-Path $Sandbox 'src/FlaUI.Mcp.Server/Tools/InteractionTools.cs') -Raw
        $cls | Should -Match 'public\s+Task<string>\s+DesktopFoo'
        $cls | Should -Match 'McpServerTool\(Destructive = true\)'
        Test-Path (Join-Path $Sandbox 'test/FlaUI.Mcp.Tests/Interaction/DesktopFooTests.cs') | Should -BeTrue
    }
    It 'uses ReadOnly attribute with -ReadOnly' {
        & $Script -Name 'DesktopBar' -ReadOnly -RepoRoot $Sandbox
        (Get-Content (Join-Path $Sandbox 'src/FlaUI.Mcp.Server/Tools/InteractionTools.cs') -Raw) |
            Should -Match 'McpServerTool\(ReadOnly = true\)'
    }
    It 'refuses a duplicate method (non-zero, no second copy)' {
        & $Script -Name 'DesktopFoo' -RepoRoot $Sandbox
        $LASTEXITCODE | Should -Not -Be 0
        ([regex]::Matches((Get-Content (Join-Path $Sandbox 'src/FlaUI.Mcp.Server/Tools/InteractionTools.cs') -Raw), 'public Task<string> DesktopFoo\(').Count) |
            Should -Be 1
    }
    It '-WhatIf writes nothing' {
        & $Script -Name 'DesktopBaz' -WhatIf -RepoRoot $Sandbox
        Test-Path (Join-Path $Sandbox 'test/FlaUI.Mcp.Tests/Interaction/DesktopBazTests.cs') | Should -BeFalse
    }
}
