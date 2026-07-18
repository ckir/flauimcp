# scripts/release.Tests.ps1
BeforeAll {
    $script:Repo = Split-Path -Parent $PSScriptRoot
    $script:Lib  = Join-Path $PSScriptRoot 'lib/release-lib.ps1'
    . $script:Lib
}

Describe 'release-lib.ps1 harness' {
    It 'dot-sources without error' {
        { . $script:Lib } | Should -Not -Throw
    }
}

Describe 'Get-NextVersion' {
    It 'bumps minor on a feat commit' {
        $r = Get-NextVersion -CurrentVersion '0.16.2' -CommitMessages @('feat(release): add release automation script')
        $r.Version | Should -Be '0.17.0'
        $r.Level   | Should -Be 'minor'
        $r.Trigger | Should -Be 'feat'
        $r.NothingToRelease | Should -BeFalse
    }

    It 'bumps patch on a fix commit' {
        $r = Get-NextVersion -CurrentVersion '0.16.2' -CommitMessages @('fix(server): correct stdout leak')
        $r.Version | Should -Be '0.16.3'
        $r.Level   | Should -Be 'patch'
        $r.Trigger | Should -Be 'fix'
    }

    It 'feat beats fix when both are present in range' {
        $r = Get-NextVersion -CurrentVersion '0.16.2' -CommitMessages @('fix(server): tweak', 'feat(tools): add desktop_foo')
        $r.Level   | Should -Be 'minor'
        $r.Trigger | Should -Be 'feat'
    }

    It 'a bang breaking change bumps minor while pre-1.0' {
        $r = Get-NextVersion -CurrentVersion '0.16.2' -CommitMessages @('feat!: rework the CLI flag surface')
        $r.Version | Should -Be '0.17.0'
        $r.Level   | Should -Be 'minor'
        $r.Trigger | Should -Be 'breaking'
    }

    It 'a bang breaking change bumps major once at 1.0+' {
        $r = Get-NextVersion -CurrentVersion '1.2.3' -CommitMessages @('fix!: remove the legacy endpoint')
        $r.Version | Should -Be '2.0.0'
        $r.Level   | Should -Be 'major'
        $r.Trigger | Should -Be 'breaking'
    }

    It 'a BREAKING CHANGE footer (no bang) counts as breaking' {
        $msg = "feat(core): swap the ref-resolution algorithm`n`nBREAKING CHANGE: ref tokens minted before this release are invalid."
        $r = Get-NextVersion -CurrentVersion '0.16.2' -CommitMessages @($msg)
        $r.Level   | Should -Be 'minor'
        $r.Trigger | Should -Be 'breaking'
    }

    It 'reports nothing to release when only docs/chore commits are in range' {
        $r = Get-NextVersion -CurrentVersion '0.16.2' -CommitMessages @('docs: tidy the operator manual', 'chore: bump a dependency')
        $r.NothingToRelease | Should -BeTrue
        $r.Version | Should -BeNullOrEmpty
    }

    It 'reports nothing to release on an empty commit range' {
        $r = Get-NextVersion -CurrentVersion '0.16.2' -CommitMessages @()
        $r.NothingToRelease | Should -BeTrue
    }

    It 'counts non-conventional commit subjects without affecting the level' {
        $r = Get-NextVersion -CurrentVersion '0.16.2' -CommitMessages @('fix(server): correct stdout leak', 'oops forgot the prefix')
        $r.NonConventionalCount | Should -Be 1
        $r.Level | Should -Be 'patch'
    }

    It '-OverrideVersion wins regardless of commits' {
        $r = Get-NextVersion -CurrentVersion '0.16.2' -CommitMessages @() -OverrideVersion '0.16.2'
        $r.Version | Should -Be '0.16.2'
        $r.Level   | Should -Be 'override'
        $r.NothingToRelease | Should -BeFalse
    }

    It '-OverrideBump computes from CurrentVersion' {
        $r = Get-NextVersion -CurrentVersion '0.16.2' -CommitMessages @() -OverrideBump 'patch'
        $r.Version | Should -Be '0.16.3'
        $r.Level   | Should -Be 'patch'
    }
}

Describe 'Get-VersionsInSync and Set-ProjectVersion' {
    BeforeEach {
        $script:VerSandbox = Join-Path ([IO.Path]::GetTempPath()) ("verbox_" + [guid]::NewGuid())
        New-Item -ItemType Directory -Force -Path (Join-Path $VerSandbox 'src/FlaUI.Mcp.Server') | Out-Null
        New-Item -ItemType Directory -Force -Path (Join-Path $VerSandbox 'installer') | Out-Null
        New-Item -ItemType Directory -Force -Path (Join-Path $VerSandbox 'plugins/flaui-mcp/.claude-plugin') | Out-Null
        @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>flaui-mcp</AssemblyName>
    <Version>0.16.2</Version>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  </PropertyGroup>
</Project>
'@ | Set-Content (Join-Path $VerSandbox 'src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj')
        @'
#define AppName "FlaUI.Mcp"
#define AppVersion "0.16.2"
#define ExeName "flaui-mcp.exe"
'@ | Set-Content (Join-Path $VerSandbox 'installer/flaui-mcp.iss')
        '{"displayName": "flaui-mcp driving", "version": "0.16.2", "author": {"name": "Costas Kirgoussios"}}' |
            Set-Content (Join-Path $VerSandbox 'plugins/flaui-mcp/.claude-plugin/plugin.json')
    }
    AfterEach { if (Test-Path $script:VerSandbox) { Remove-Item -Recurse -Force $script:VerSandbox } }

    It 'reports InSync=true when all 3 files agree' {
        $r = Get-VersionsInSync -RepoRoot $VerSandbox
        $r.InSync | Should -BeTrue
        $r.Versions.Csproj | Should -Be '0.16.2'
        $r.Versions.Iss    | Should -Be '0.16.2'
        $r.Versions.Plugin | Should -Be '0.16.2'
    }

    It 'reports InSync=false and names the drift when one file disagrees' {
        (Get-Content (Join-Path $VerSandbox 'installer/flaui-mcp.iss') -Raw) -replace '0\.16\.2', '0.16.3' |
            Set-Content (Join-Path $VerSandbox 'installer/flaui-mcp.iss')
        $r = Get-VersionsInSync -RepoRoot $VerSandbox
        $r.InSync | Should -BeFalse
        $r.Message | Should -Match '0\.16\.3'
    }

    It 'Set-ProjectVersion rewrites all 3 files and preserves surrounding content' {
        Set-ProjectVersion -RepoRoot $VerSandbox -Version '0.17.0'
        $r = Get-VersionsInSync -RepoRoot $VerSandbox
        $r.InSync | Should -BeTrue
        $r.Versions.Csproj | Should -Be '0.17.0'
        (Get-Content (Join-Path $VerSandbox 'src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj') -Raw) | Should -Match '<AssemblyName>flaui-mcp</AssemblyName>'
        (Get-Content (Join-Path $VerSandbox 'installer/flaui-mcp.iss') -Raw) | Should -Match '#define AppName "FlaUI.Mcp"'
        (Get-Content (Join-Path $VerSandbox 'plugins/flaui-mcp/.claude-plugin/plugin.json') -Raw) | Should -Match 'Costas Kirgoussios'
    }

    It 'Get-VersionsInSync throws when a version file is missing' {
        Remove-Item (Join-Path $VerSandbox 'installer/flaui-mcp.iss')
        { Get-VersionsInSync -RepoRoot $VerSandbox } | Should -Throw
    }
}
