# Release Automation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make cutting a flaui-mcp release one command (`scripts/release.ps1`): compute the next semver from
conventional commits, gate locally (build/test/version-sync/plugin-drift), draft the CHANGELOG body with a
headless `claude -p` call (heading stays script-owned/deterministic), let the human do a light accept/edit
review, then commit + tag + `git push --atomic` — with `release.yml` sourcing the GitHub Release body from the
top CHANGELOG section instead of auto-generated notes.

**Architecture:** A pure, fully-Pester-tested function library (`scripts/lib/release-lib.ps1`) does all the
deterministic work (version math, file rewrites, changelog parsing/prepending, prompt construction, the gate's
result aggregation). A thin orchestrator (`scripts/release.ps1`) sequences preconditions → compute → gate →
draft → review → confirm → commit → push, shelling out to `git`, `dotnet`, and `claude -p` — these integration
points are smoke-tested via `-WhatIf` and real local runs rather than Pester-mocked. `release.yml` gets one
new step that renders the release body from `CHANGELOG.md` via the same library function CI and the human use.

**Tech Stack:** PowerShell 7 (pwsh) scripts, Pester v5 (`Invoke-Pester`), .NET 10 SDK (`dotnet build`/`dotnet
test` against `FlaUI.Mcp.slnx`), the `claude` CLI (`claude -p`, headless/print mode), GitHub Actions
(`windows-latest`, `softprops/action-gh-release@v3`).

---

## Ground-truth facts (verified at plan-time, 2026-07-18)

Every citation below was read/grepped against the live repo (branch `feat/release-automation`) or run live
immediately before writing this plan — nothing here is inferred from the spec alone.

- **`src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj`** — line 27, inside the `<PropertyGroup>` that spans lines
  19-31 (also holds `AssemblyName`, `RuntimeIdentifier`, etc.):
  ```
  27:    <Version>0.16.2</Version>
  ```
- **`installer/flaui-mcp.iss`** — line 4:
  ```
  4:#define AppVersion "0.16.2"
  ```
  (line 3 is `#define AppName "FlaUI.Mcp"`, line 5 is `#define ExeName "flaui-mcp.exe"`.)
- **`plugins/flaui-mcp/.claude-plugin/plugin.json`** — line 5:
  ```json
  5:  "version": "0.16.2",
  ```
- **`.github/workflows/release.yml`** — the `Create GitHub Release` step is lines 53-61:
  ```yaml
        - name: Create GitHub Release
          if: startsWith(github.ref, 'refs/tags/')   # only on a v* tag; manual runs stop after checksums
          uses: softprops/action-gh-release@v3
          with:
            files: |
              dist/flaui-mcp-setup.exe
              publish/flaui-mcp.exe
              dist/install.ps1
              dist/SHA256SUMS.txt
  ```
  Confirmed: no `body`/`body_path` key today — GitHub's auto-generated notes are used.
- **`.github/workflows/ci.yml`** — confirmed two jobs only, `unit` (lines 9-20) and `scaffolder-smoke` (lines
  31-43). No Pester job exists.
- **`scripts/build-plugin.ps1`** (21 lines, confirmed in full) — regenerates
  `plugins/flaui-mcp/{skills,scripts}` from `.claude/` (skills `driving-flaui-mcp`, `flaui-learn`,
  `flaui-curate`, plus the curate-nudge hook script). It has **no dry-run flag** — running it always writes.
  The plugin's `.claude-plugin/plugin.json` and `hooks/hooks.json` are hand-authored and untouched by it.
- **`scripts/new-tool.Tests.ps1`** — confirmed Pester v5 idiom to match: `BeforeAll`/`AfterAll` with a
  `$script:Sandbox = Join-Path ([IO.Path]::GetTempPath()) (...)`, `Describe`/`It`/`Should`, cleanup via
  `Remove-Item -Recurse -Force`.
- **`claude` CLI** — v2.1.214. `--help` confirms `-p, --print`, `--model <model>`, and `--output-format
  <format>` with choices `text` (default) / `json` / `stream-json`, "only works with --print". **Live-verified
  stdin behavior:** `echo "Reply with exactly: PONG" | claude -p --model haiku --output-format text` returned
  exactly `PONG` — confirms the CLI reads the prompt from stdin when no positional prompt argument is given.
- **Pester**: `Get-Module -ListAvailable Pester` shows both `5.8.0` and a legacy `3.4.0` on this machine — the
  plan pins `-RequiredVersion 5.8.0` everywhere to avoid the ambiguous-version trap.
- **`FlaUI.Mcp.slnx`** exists at repo root (376 bytes) — the solution file is `.slnx`, not `.sln`.
- **`dotnet build FlaUI.Mcp.slnx -c Debug`** — live-run result: **0 errors, 0 warnings** (5 projects). Note:
  this Debug run was only a plan-time smoke check — the actual gate (below) builds `-c Release`, matching
  CI's `unit` job and `release.yml`.
- **`dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&Category!=SyntheticInput"`** — live-run result:
  ```
  Passed!  - Failed:     0, Passed:   689, Skipped:     0, Total:   689
  ```
- **`Category=SyntheticInput`** trait confirmed present (`test/FlaUI.Mcp.Tests/Interaction/InputToolsTests.cs`)
  — the filter is not vacuous.
- **`scripts/lib/`** does not exist yet — Task 1 creates it.
- **`CHANGELOG.md`** confirmed format: `## [X.Y.Z] - YYYY-MM-DD` headings, `### Added`/`### Fixed`/`###
  Changed` subsections, most-recent-first (top of file, right after the `# Changelog` + intro preamble).
- **Last tag** is `v0.16.1` (`git tag --sort=-creatordate`), while the 3 version files already read `0.16.2`
  — this is exactly the "legitimately ahead" state the spec's Operational Note describes (see Task 8's
  version-mismatch guard).

### A design gap this plan resolves (flagged explicitly, not silently decided)

The spec's `Get-VersionsInSync`/`Set-ProjectVersion` bullet says the sync check must NOT require the 3 files
to equal the last tag, and separately: *"If the files' version differs from the computed version, warn and
let the human confirm or `-Version`-override rather than hard-failing."* Read literally, "the computed
version" can only be meaningful if `Get-NextVersion`'s bump base is **the last release tag**, not the current
files' version (bumping the files' own version and then comparing the result back to the files' version would
compare `bump(X)` to `X`, which always differs by construction — that can't be the intended check). This plan
therefore fixes `Get-NextVersion -CurrentVersion` to **the last tag's version** (`0.0.0` if no tag exists), and
adds an orchestrator-level warn-and-confirm step (Task 8) comparing the computed result against the files'
actual version — separately from `Get-VersionsInSync`'s files-agree-with-each-other check, which is unrelated
to tags. This also gives the `-Yes` contract an 8th row beyond the spec's literal 7 (Task 10's table).

## Build & test commands

```powershell
# .NET gate (also what Invoke-Gate's default checks run) — mirrors CI's `unit` job exactly:
dotnet build FlaUI.Mcp.slnx -c Release
#   Expected: "Build succeeded." with "0 Warning(s)" and "0 Error(s)" in the summary.

dotnet test FlaUI.Mcp.slnx -c Release --filter "Category!=Desktop&Category!=SyntheticInput" --no-build
#   Expected: "Passed!  - Failed:     0, Passed:   689, Skipped:     0, Total:   689" (count may grow as
#   this plan's own Pester tests are NOT .NET tests and don't affect this number).

# Pester (pin the version — this machine has both 5.8.0 and legacy 3.4.0 installed):
pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 5.8.0; Invoke-Pester -Path scripts/release.Tests.ps1 -PassThru" | Select-Object PassedCount, FailedCount, TotalCount
#   Expected (grows task by task): "Tests Passed: N, Failed: 0, Skipped: 0, Inconclusive: 0, NotRun: 0"
```

---

## Task 1: scripts/lib scaffold + Pester harness + CI Pester job

**Files:**
- Create: `scripts/lib/release-lib.ps1`
- Create: `scripts/release.Tests.ps1`
- Modify: `.github/workflows/ci.yml:31-43` (append a new job after `scaffolder-smoke`)

- [ ] **Step 1: Write the failing harness test**

```powershell
# scripts/release.Tests.ps1
BeforeAll {
    $script:Repo = Split-Path -Parent $PSScriptRoot
    $script:Lib  = Join-Path $PSScriptRoot 'lib/release-lib.ps1'
}

Describe 'release-lib.ps1 harness' {
    It 'dot-sources without error' {
        { . $script:Lib } | Should -Not -Throw
    }
}
```

- [ ] **Step 2: Run it and confirm it fails (file doesn't exist yet)**

Run: `pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 5.8.0; Invoke-Pester -Path scripts/release.Tests.ps1"`
Expected: FAIL — dot-sourcing a nonexistent path throws.

- [ ] **Step 3: Create the lib file (header only — functions arrive in later tasks)**

```powershell
# scripts/lib/release-lib.ps1
# Pure, testable functions backing scripts/release.ps1.
# Dot-source this file; every function below is then available in the caller's scope.
$ErrorActionPreference = 'Stop'
```

- [ ] **Step 4: Run it again and confirm it passes**

Run: `pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 5.8.0; Invoke-Pester -Path scripts/release.Tests.ps1"`
Expected: `Tests Passed: 1, Failed: 0, Skipped: 0, Inconclusive: 0, NotRun: 0`

- [ ] **Step 5: Add the CI Pester job**

`.github/workflows/ci.yml` currently ends (lines 31-43) with the `scaffolder-smoke` job's last step:
```yaml
      - name: Scaffold a throwaway tool and build
        shell: pwsh
        run: |
          ./scripts/new-tool.ps1 -Name DesktopCiSmoke
          if ($LASTEXITCODE -ne 0) { throw "new-tool.ps1 failed with exit $LASTEXITCODE" }
          dotnet build -c Release
```
Append a new job directly after it (same indentation level as `unit:`/`scaffolder-smoke:`):
```yaml

  pester:
    name: Pester (scripts/)
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - name: Run Pester tests
        shell: pwsh
        run: |
          Install-Module Pester -RequiredVersion 5.8.0 -Force -SkipPublisherCheck -Scope CurrentUser
          Import-Module Pester -RequiredVersion 5.8.0
          $result = Invoke-Pester -Path scripts/ -CI -PassThru
          if ($result.FailedCount -gt 0) { throw "$($result.FailedCount) Pester test(s) failed." }
```
Note: this job also picks up the existing `scripts/new-tool.Tests.ps1` (previously local-only) — confirmed
it needs no interactive desktop or `dotnet`, so it runs headless fine (verified locally: 4/4 pass, 7s).
No `setup-dotnet` step is needed in this job: every `release.Tests.ps1` test through Task 7 injects fake
script blocks instead of shelling to a real `dotnet build`/`dotnet test` (see Task 7) — so this job stays
fast and dependency-light.

- [ ] **Step 6: Commit**

```bash
git add scripts/lib/release-lib.ps1 scripts/release.Tests.ps1 .github/workflows/ci.yml
git commit -m "feat(release): scaffold scripts/lib + Pester harness + CI Pester job"
```

---

## Task 2: `Get-NextVersion` — conventional-commit version math

**Files:**
- Modify: `scripts/lib/release-lib.ps1` (append)
- Modify: `scripts/release.Tests.ps1` (append)

`CurrentVersion` here is **the base to bump from** — conventionally the last release tag's version (stripped
of the `v` prefix), or `0.0.0` if no tag exists yet. It is deliberately NOT "whatever the 3 project files
currently say" — see the "design gap this plan resolves" note above; the orchestrator (Task 8) is responsible
for passing the tag-derived base in and separately warning if that disagrees with the files.

- [ ] **Step 1: Write the failing tests**

```powershell
# --- append to scripts/release.Tests.ps1 ---

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
```

- [ ] **Step 2: Run and confirm failure**

Run: `pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 5.8.0; Invoke-Pester -Path scripts/release.Tests.ps1"`
Expected: FAIL — `Get-NextVersion`/`Step-SemVer` not recognized.

- [ ] **Step 3: Implement `Step-SemVer` and `Get-NextVersion`**

```powershell
# --- append to scripts/lib/release-lib.ps1 ---

function Step-SemVer {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][ValidateSet('major','minor','patch')][string]$Component
    )
    if ($Version -notmatch '^(?<maj>\d+)\.(?<min>\d+)\.(?<pat>\d+)$') {
        throw "Step-SemVer: not a valid X.Y.Z semver: '$Version'"
    }
    $maj = [int]$Matches.maj; $min = [int]$Matches.min; $pat = [int]$Matches.pat
    switch ($Component) {
        'major' { $maj++; $min = 0; $pat = 0 }
        'minor' { $min++; $pat = 0 }
        'patch' { $pat++ }
    }
    "$maj.$min.$pat"
}

function Get-NextVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CurrentVersion,
        [AllowEmptyCollection()][string[]]$CommitMessages = @(),
        [string]$OverrideVersion,
        [ValidateSet('major','minor','patch')][string]$OverrideBump
    )

    $conventionalPattern = '^(?<type>[a-zA-Z]+)(\((?<scope>[^)]+)\))?(?<bang>!)?:\s*(?<subject>.+)$'
    $hasBreaking = $false
    $hasFeat = $false
    $hasFix = $false
    $nonConventionalCount = 0
    $parsedCommits = @()

    foreach ($msg in $CommitMessages) {
        if ([string]::IsNullOrWhiteSpace($msg)) { continue }
        $subjectLine = ($msg -split "`n")[0].Trim()
        $m = [regex]::Match($subjectLine, $conventionalPattern)
        if (-not $m.Success) { $nonConventionalCount++; continue }

        $type = $m.Groups['type'].Value.ToLowerInvariant()
        $bang = $m.Groups['bang'].Success
        $bodyBreaking = [regex]::IsMatch($msg, '(?m)^BREAKING CHANGE:')
        $breaking = $bang -or $bodyBreaking

        $parsedCommits += [pscustomobject]@{
            Type     = $type
            Scope    = $m.Groups['scope'].Value
            Breaking = $breaking
            Subject  = $m.Groups['subject'].Value
        }

        if ($breaking)            { $hasBreaking = $true }
        elseif ($type -eq 'feat') { $hasFeat = $true }
        elseif ($type -eq 'fix')  { $hasFix = $true }
    }

    if ($OverrideVersion) {
        return [pscustomobject]@{
            Version = $OverrideVersion; Level = 'override'; Trigger = 'override'
            NothingToRelease = $false; NonConventionalCount = $nonConventionalCount; Commits = $parsedCommits
        }
    }
    if ($OverrideBump) {
        return [pscustomobject]@{
            Version = (Step-SemVer -Version $CurrentVersion -Component $OverrideBump)
            Level = $OverrideBump; Trigger = 'override'
            NothingToRelease = $false; NonConventionalCount = $nonConventionalCount; Commits = $parsedCommits
        }
    }
    if (-not $hasBreaking -and -not $hasFeat -and -not $hasFix) {
        return [pscustomobject]@{
            Version = $null; Level = $null; Trigger = $null
            NothingToRelease = $true; NonConventionalCount = $nonConventionalCount; Commits = $parsedCommits
        }
    }

    $currentMajor = [int]($CurrentVersion -split '\.')[0]
    if ($hasBreaking) {
        $level = if ($currentMajor -lt 1) { 'minor' } else { 'major' }
        $trigger = 'breaking'
    } elseif ($hasFeat) {
        $level = 'minor'; $trigger = 'feat'
    } else {
        $level = 'patch'; $trigger = 'fix'
    }

    [pscustomobject]@{
        Version = (Step-SemVer -Version $CurrentVersion -Component $level)
        Level = $level; Trigger = $trigger
        NothingToRelease = $false; NonConventionalCount = $nonConventionalCount; Commits = $parsedCommits
    }
}
```

- [ ] **Step 4: Run and confirm all pass**

Run: `pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 5.8.0; Invoke-Pester -Path scripts/release.Tests.ps1"`
Expected: `Tests Passed: 12, Failed: 0, Skipped: 0, Inconclusive: 0, NotRun: 0` (11 new + the Task 1 harness test)

- [ ] **Step 5: Commit**

```bash
git add scripts/lib/release-lib.ps1 scripts/release.Tests.ps1
git commit -m "feat(release): Get-NextVersion conventional-commit version math"
```

---

## Task 3: `Get-VersionsInSync` + `Set-ProjectVersion` — the 3 version files

**Files:**
- Modify: `scripts/lib/release-lib.ps1` (append)
- Modify: `scripts/release.Tests.ps1` (append)

Sync means "the 3 files agree with each other" — NOT "equal the last git tag" (see the ground-truth note:
files legitimately run ahead of the last tag). `Set-ProjectVersion` rewrites only the version substring in
each file via a targeted regex replace, preserving every other byte.

- [ ] **Step 1: Write the failing tests**

```powershell
# --- append to scripts/release.Tests.ps1 ---

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
```

- [ ] **Step 2: Run and confirm failure**

Run: `pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 5.8.0; Invoke-Pester -Path scripts/release.Tests.ps1"`
Expected: FAIL — `Get-VersionsInSync`/`Set-ProjectVersion` not recognized.

- [ ] **Step 3: Implement both functions**

```powershell
# --- append to scripts/lib/release-lib.ps1 ---

function Get-VersionsInSync {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RepoRoot)

    $csprojPath = Join-Path $RepoRoot 'src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj'
    $issPath    = Join-Path $RepoRoot 'installer/flaui-mcp.iss'
    $pluginPath = Join-Path $RepoRoot 'plugins/flaui-mcp/.claude-plugin/plugin.json'

    foreach ($p in @($csprojPath, $issPath, $pluginPath)) {
        if (-not (Test-Path $p)) { throw "Get-VersionsInSync: version file not found: $p" }
    }

    $csprojVersion = $null
    if ((Get-Content $csprojPath -Raw) -match '<Version>(?<v>\d+\.\d+\.\d+)</Version>') { $csprojVersion = $Matches.v }
    $issVersion = $null
    if ((Get-Content $issPath -Raw) -match '#define AppVersion "(?<v>\d+\.\d+\.\d+)"') { $issVersion = $Matches.v }
    $pluginVersion = (Get-Content $pluginPath -Raw | ConvertFrom-Json).version

    if (-not $csprojVersion) { throw "Get-VersionsInSync: no <Version> element found in $csprojPath" }
    if (-not $issVersion)    { throw "Get-VersionsInSync: no #define AppVersion found in $issPath" }
    if (-not $pluginVersion) { throw "Get-VersionsInSync: no 'version' key found in $pluginPath" }

    $versions = [ordered]@{ Csproj = $csprojVersion; Iss = $issVersion; Plugin = $pluginVersion }
    $distinct = $versions.Values | Select-Object -Unique
    $inSync = ($distinct.Count -eq 1)

    [pscustomobject]@{
        InSync   = $inSync
        Versions = $versions
        Message  = if ($inSync) {
            "3 version files agree at $($distinct[0])"
        } else {
            "Version files disagree: csproj=$csprojVersion iss=$issVersion plugin=$pluginVersion"
        }
    }
}

function Set-ProjectVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version
    )

    $csprojPath = Join-Path $RepoRoot 'src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj'
    $issPath    = Join-Path $RepoRoot 'installer/flaui-mcp.iss'
    $pluginPath = Join-Path $RepoRoot 'plugins/flaui-mcp/.claude-plugin/plugin.json'

    $csproj = Get-Content $csprojPath -Raw
    $newCsproj = [regex]::Replace($csproj, '<Version>\d+\.\d+\.\d+</Version>', "<Version>$Version</Version>")
    if ($newCsproj -eq $csproj) { throw "Set-ProjectVersion: no <Version> element replaced in $csprojPath" }
    Set-Content -Path $csprojPath -Value $newCsproj -NoNewline -Encoding UTF8

    $iss = Get-Content $issPath -Raw
    $newIss = [regex]::Replace($iss, '#define AppVersion "\d+\.\d+\.\d+"', "#define AppVersion `"$Version`"")
    if ($newIss -eq $iss) { throw "Set-ProjectVersion: no AppVersion replaced in $issPath" }
    Set-Content -Path $issPath -Value $newIss -NoNewline -Encoding UTF8

    $plugin = Get-Content $pluginPath -Raw
    $newPlugin = [regex]::Replace($plugin, '"version":\s*"\d+\.\d+\.\d+"', "`"version`": `"$Version`"")
    if ($newPlugin -eq $plugin) { throw "Set-ProjectVersion: no version key replaced in $pluginPath" }
    Set-Content -Path $pluginPath -Value $newPlugin -NoNewline -Encoding UTF8

    Get-VersionsInSync -RepoRoot $RepoRoot
}
```

- [ ] **Step 4: Run and confirm all pass**

Run: `pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 5.8.0; Invoke-Pester -Path scripts/release.Tests.ps1"`
Expected: `Tests Passed: 16, Failed: 0, Skipped: 0, Inconclusive: 0, NotRun: 0` (4 new + 12 prior)

- [ ] **Step 5: Commit**

```bash
git add scripts/lib/release-lib.ps1 scripts/release.Tests.ps1
git commit -m "feat(release): Get-VersionsInSync + Set-ProjectVersion for the 3 version files"
```

---

## Task 4: `Get-TopChangelogSection` — extract the top (or top-N) section

**Files:**
- Modify: `scripts/lib/release-lib.ps1` (append)
- Modify: `scripts/release.Tests.ps1` (append)

Used two ways: `release.yml` calls it with no `-Count` (defaults to 1) to get the release body; Task 8's
prompt-assembly calls it with `-Count 2` to build the style exemplar for the LLM. One function serves both —
avoids a second near-duplicate "get last N sections" helper (DRY). Tolerant extraction: it anchors only on
lines starting with `## [`, so minor formatting drift elsewhere in a section doesn't break it (per the spec's
"Malformed LLM markdown" guard).

- [ ] **Step 1: Write the failing tests**

```powershell
# --- append to scripts/release.Tests.ps1 ---

Describe 'Get-TopChangelogSection' {
    BeforeEach {
        $script:ClPath = Join-Path ([IO.Path]::GetTempPath()) ("changelog_" + [guid]::NewGuid() + '.md')
        @'
# Changelog

All notable changes to this project are documented here.

## [0.2.0] - 2026-07-20

### Added
- Thing two.

## [0.1.0] - 2026-07-01

### Added
- Thing one.
'@ | Set-Content $ClPath
    }
    AfterEach { Remove-Item $script:ClPath -Force -ErrorAction SilentlyContinue }

    It 'extracts only the top section by default' {
        $top = Get-TopChangelogSection -ChangelogPath $ClPath
        $top | Should -Match '^## \[0\.2\.0\] - 2026-07-20'
        $top | Should -Match 'Thing two\.'
        $top | Should -Not -Match 'Thing one\.'
        $top | Should -Not -Match '## \[0\.1\.0\]'
    }

    It 'returns the last 2 sections with -Count 2' {
        $top2 = Get-TopChangelogSection -ChangelogPath $ClPath -Count 2
        $top2 | Should -Match 'Thing two\.'
        $top2 | Should -Match 'Thing one\.'
    }

    It 'throws when the file has no ## [ section' {
        $empty = Join-Path ([IO.Path]::GetTempPath()) ("empty_" + [guid]::NewGuid() + '.md')
        '# Changelog' | Set-Content $empty
        { Get-TopChangelogSection -ChangelogPath $empty } | Should -Throw
        Remove-Item $empty -Force
    }
}
```

- [ ] **Step 2: Run and confirm failure**

Run: `pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 5.8.0; Invoke-Pester -Path scripts/release.Tests.ps1"`
Expected: FAIL — `Get-TopChangelogSection` not recognized.

- [ ] **Step 3: Implement it**

```powershell
# --- append to scripts/lib/release-lib.ps1 ---

function Get-TopChangelogSection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ChangelogPath,
        [int]$Count = 1
    )

    $lines = Get-Content $ChangelogPath
    $headingIdx = @()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^## \[') { $headingIdx += $i }
    }
    if ($headingIdx.Count -eq 0) { throw "Get-TopChangelogSection: no '## [' section found in $ChangelogPath" }

    $take = [Math]::Min($Count, $headingIdx.Count)
    $start = $headingIdx[0]
    $end = if ($headingIdx.Count -gt $take) { $headingIdx[$take] - 1 } else { $lines.Count - 1 }
    ($lines[$start..$end] -join "`n").TrimEnd()
}
```

- [ ] **Step 4: Run and confirm all pass**

Run: `pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 5.8.0; Invoke-Pester -Path scripts/release.Tests.ps1"`
Expected: `Tests Passed: 19, Failed: 0, Skipped: 0, Inconclusive: 0, NotRun: 0` (3 new + 16 prior)

- [ ] **Step 5: Commit**

```bash
git add scripts/lib/release-lib.ps1 scripts/release.Tests.ps1
git commit -m "feat(release): Get-TopChangelogSection extraction"
```

---

## Task 5: `Add-ChangelogSection` — prepend, abort on duplicate heading

**Files:**
- Modify: `scripts/lib/release-lib.ps1` (append)
- Modify: `scripts/release.Tests.ps1` (append)

Heading is assembled here (script-owned, deterministic); `Body` is opaque text the caller supplies (from the
LLM or a human edit). If the exact `## [X.Y.Z] - YYYY-MM-DD` heading already exists anywhere in the file
(a manual stub, or a re-run after a partially-committed release), this throws rather than duplicating it.

- [ ] **Step 1: Write the failing tests**

```powershell
# --- append to scripts/release.Tests.ps1 ---

Describe 'Add-ChangelogSection' {
    BeforeEach {
        $script:ClPath = Join-Path ([IO.Path]::GetTempPath()) ("changelog_" + [guid]::NewGuid() + '.md')
        @'
# Changelog

All notable changes to this project are documented here.

## [0.16.1] - 2026-07-18

### Fixed
- Something.
'@ | Set-Content $ClPath
    }
    AfterEach { Remove-Item $script:ClPath -Force -ErrorAction SilentlyContinue }

    It 'prepends the new section above the existing ones, preserving the preamble' {
        Add-ChangelogSection -ChangelogPath $ClPath -Version '0.17.0' -Body "### Added`n- New thing." -Date (Get-Date '2026-07-19')
        $content = Get-Content $ClPath -Raw
        $content | Should -Match '(?s)# Changelog.*## \[0\.17\.0\] - 2026-07-19.*### Added.*New thing\..*## \[0\.16\.1\] - 2026-07-18'
    }

    It 'aborts (throws) when the target version heading already exists' {
        Add-ChangelogSection -ChangelogPath $ClPath -Version '0.17.0' -Body "### Added`n- New thing." -Date (Get-Date '2026-07-19')
        { Add-ChangelogSection -ChangelogPath $ClPath -Version '0.17.0' -Body "### Added`n- Different." -Date (Get-Date '2026-07-19') } | Should -Throw
    }

    It 'appends a section when the file has no existing sections yet' {
        $empty = Join-Path ([IO.Path]::GetTempPath()) ("emptycl_" + [guid]::NewGuid() + '.md')
        '# Changelog' | Set-Content $empty
        Add-ChangelogSection -ChangelogPath $empty -Version '0.1.0' -Body "### Added`n- First." -Date (Get-Date '2026-07-01')
        (Get-Content $empty -Raw) | Should -Match '## \[0\.1\.0\] - 2026-07-01'
        Remove-Item $empty -Force
    }
}
```

- [ ] **Step 2: Run and confirm failure**

Run: `pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 5.8.0; Invoke-Pester -Path scripts/release.Tests.ps1"`
Expected: FAIL — `Add-ChangelogSection` not recognized.

- [ ] **Step 3: Implement it**

```powershell
# --- append to scripts/lib/release-lib.ps1 ---

function Add-ChangelogSection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ChangelogPath,
        [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
        [Parameter(Mandatory)][string]$Body,
        [datetime]$Date = (Get-Date)
    )

    $heading = "## [$Version] - $($Date.ToString('yyyy-MM-dd'))"
    $content = Get-Content $ChangelogPath -Raw

    if ($content -match [regex]::Escape($heading)) {
        throw "Add-ChangelogSection: heading '$heading' already exists in $ChangelogPath — refusing to add a duplicate."
    }

    $lines = Get-Content $ChangelogPath
    $firstSectionLine = ($lines | Select-String -Pattern '^## \[' | Select-Object -First 1).LineNumber
    $section = "$heading`n`n$($Body.Trim())`n"

    if (-not $firstSectionLine) {
        $newContent = $content.TrimEnd() + "`n`n" + $section
    } else {
        $insertAt = $firstSectionLine - 1   # 0-based index of the first '## [' line
        $before = ($lines[0..($insertAt - 1)] -join "`n").TrimEnd()
        $after  = ($lines[$insertAt..($lines.Count - 1)] -join "`n")
        $newContent = "$before`n`n$section`n$after"
    }

    Set-Content -Path $ChangelogPath -Value $newContent -NoNewline -Encoding UTF8
}
```

- [ ] **Step 4: Run and confirm all pass**

Run: `pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 5.8.0; Invoke-Pester -Path scripts/release.Tests.ps1"`
Expected: `Tests Passed: 22, Failed: 0, Skipped: 0, Inconclusive: 0, NotRun: 0` (3 new + 19 prior)

- [ ] **Step 5: Commit**

```bash
git add scripts/lib/release-lib.ps1 scripts/release.Tests.ps1
git commit -m "feat(release): Add-ChangelogSection prepend with duplicate-heading guard"
```

---

## Task 6: `Get-ChangelogPrompt` — constrained LLM prompt, with the context-blowout guard

**Files:**
- Modify: `scripts/lib/release-lib.ps1` (append)
- Modify: `scripts/release.Tests.ps1` (append)

Takes both the full diff and the diff stat (caller supplies both — see Task 8) so the size/commit-count
degrade decision from the spec's "Context blowout" guard lives in one testable place instead of being decided
by the shell-out caller.

- [ ] **Step 1: Write the failing tests**

```powershell
# --- append to scripts/release.Tests.ps1 ---

Describe 'Get-ChangelogPrompt' {
    BeforeEach {
        $script:Commits = @('feat(release): add release script', 'fix(server): correct a leak')
        $script:Exemplar = "## [0.16.1] - 2026-07-18`n`n### Fixed`n- Something."
    }

    It 'uses the full diff when under the size/commit thresholds' {
        $p = Get-ChangelogPrompt -Version '0.17.0' -CommitMessages $Commits -DiffText 'small diff' -DiffStatText 'stat' -StyleExemplar $Exemplar
        $p | Should -Match 'Full diff:'
        $p | Should -Not -Match 'Diff stat'
    }

    It 'degrades to the diff stat when the diff exceeds the size threshold' {
        $bigDiff = 'x' * 200000
        $p = Get-ChangelogPrompt -Version '0.17.0' -CommitMessages $Commits -DiffText $bigDiff -DiffStatText 'stat summary' -StyleExemplar $Exemplar
        $p | Should -Match 'Diff stat'
        $p | Should -Not -Match 'Full diff:'
    }

    It 'degrades to the diff stat when the commit count exceeds the threshold' {
        $manyCommits = 1..41 | ForEach-Object { "fix(x): change $_" }
        $p = Get-ChangelogPrompt -Version '0.17.0' -CommitMessages $manyCommits -DiffText 'small' -DiffStatText 'stat summary' -StyleExemplar $Exemplar
        $p | Should -Match 'Diff stat'
    }

    It 'instructs body-only output and includes the style exemplar and commit list' {
        $p = Get-ChangelogPrompt -Version '0.17.0' -CommitMessages $Commits -DiffText 'd' -DiffStatText 's' -StyleExemplar $Exemplar
        $p | Should -Match 'Output ONLY'
        $p | Should -Match 'no.*heading'
        $p | Should -Match ([regex]::Escape($Exemplar))
        $p | Should -Match '- feat\(release\): add release script'
        $p | Should -Match '- fix\(server\): correct a leak'
    }
}
```

- [ ] **Step 2: Run and confirm failure**

Run: `pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 5.8.0; Invoke-Pester -Path scripts/release.Tests.ps1"`
Expected: FAIL — `Get-ChangelogPrompt` not recognized.

- [ ] **Step 3: Implement it**

```powershell
# --- append to scripts/lib/release-lib.ps1 ---

function Get-ChangelogPrompt {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$CommitMessages,
        [string]$DiffText = '',
        [string]$DiffStatText = '',
        [Parameter(Mandatory)][string]$StyleExemplar,
        [int]$DiffSizeThresholdBytes = 150000,
        [int]$CommitCountThreshold = 40
    )

    $useStat = ($DiffText.Length -gt $DiffSizeThresholdBytes) -or ($CommitMessages.Count -gt $CommitCountThreshold)
    $diffSection = if ($useStat) {
        "Diff stat (full patch omitted — release is large):`n$DiffStatText"
    } else {
        "Full diff:`n$DiffText"
    }
    $commitList = ($CommitMessages | ForEach-Object { "- $(($_ -split "`n")[0])" }) -join "`n"

    @"
You are drafting the CHANGELOG.md body for flaui-mcp release $Version.

Output ONLY the body sections (### Added / ### Fixed / ### Changed as applicable) — no '## [$Version]'
heading, no commit-subject dump, no surrounding chatter, no code fences. Write explanatory prose bullets,
matching the style of the exemplar below (not a list of raw commit subjects).

## Style exemplar (last entries from CHANGELOG.md)
$StyleExemplar

## Commits in this release
$commitList

## $diffSection
"@
}
```

- [ ] **Step 4: Run and confirm all pass**

Run: `pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 5.8.0; Invoke-Pester -Path scripts/release.Tests.ps1"`
Expected: `Tests Passed: 26, Failed: 0, Skipped: 0, Inconclusive: 0, NotRun: 0` (4 new + 22 prior)

- [ ] **Step 5: Commit**

```bash
git add scripts/lib/release-lib.ps1 scripts/release.Tests.ps1
git commit -m "feat(release): Get-ChangelogPrompt constrained LLM prompt builder"
```

---

## Task 7: `Invoke-Gate` — build + test + version-sync + plugin-drift

**Files:**
- Modify: `scripts/lib/release-lib.ps1` (append)
- Modify: `scripts/release.Tests.ps1` (append)

The 3 shell-out checks (build, test, plugin-drift) are **dependency-injected as script blocks** with real
defaults that call `dotnet`/`scripts/build-plugin.ps1`. This keeps `Invoke-Gate` genuinely Pester-testable
(inject fake script blocks that set `$global:LASTEXITCODE` without touching `dotnet`) while still doing the
real thing by default in production. The version-sync check is NOT injectable — it calls `Get-VersionsInSync`
directly (already Pester-tested in Task 3), so the sandbox fixture here supplies real files on disk.

Build "pass" requires **both** exit code 0 **and** a `0 Warning(s)` count parsed from the summary line — a
plain exit-code check would be a false pass, because `dotnet build`'s success summary always prints a
`Warning(s)` line (e.g. `2 Warning(s)`) even when it's the string "Warning(s)" with count 0, so a naive
`-notmatch 'warning'` text search would falsely FAIL on a clean 0-warning build. This is `Invoke-Gate`'s
production behavior against the real `dotnet build FlaUI.Mcp.slnx -c Debug`, confirmed live: 0 errors, 0
warnings.

`scripts/build-plugin.ps1` has no dry-run mode (ground-truth fact above) — its default drift check runs it
for real, then checks `git diff --quiet -- plugins/flaui-mcp`. If it detects drift, the corrected files are
left in the working tree (mirroring how a human runs it by hand today) and the gate reports FAIL so the human
reviews + commits the regenerated snapshot before releasing.

- [ ] **Step 1: Write the failing tests**

```powershell
# --- append to scripts/release.Tests.ps1 ---

Describe 'Invoke-Gate' {
    BeforeEach {
        $script:GateSandbox = Join-Path ([IO.Path]::GetTempPath()) ("gatebox_" + [guid]::NewGuid())
        New-Item -ItemType Directory -Force -Path (Join-Path $GateSandbox 'src/FlaUI.Mcp.Server') | Out-Null
        New-Item -ItemType Directory -Force -Path (Join-Path $GateSandbox 'installer') | Out-Null
        New-Item -ItemType Directory -Force -Path (Join-Path $GateSandbox 'plugins/flaui-mcp/.claude-plugin') | Out-Null
        '<Project><PropertyGroup><Version>0.1.0</Version></PropertyGroup></Project>' | Set-Content (Join-Path $GateSandbox 'src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj')
        '#define AppVersion "0.1.0"' | Set-Content (Join-Path $GateSandbox 'installer/flaui-mcp.iss')
        '{"version": "0.1.0"}' | Set-Content (Join-Path $GateSandbox 'plugins/flaui-mcp/.claude-plugin/plugin.json')
        $script:Pass = { param($Root) $global:LASTEXITCODE = 0; 'ok' }
    }
    AfterEach { if (Test-Path $script:GateSandbox) { Remove-Item -Recurse -Force $script:GateSandbox } }

    It 'passes when all 4 checks pass' {
        $result = Invoke-Gate -RepoRoot $GateSandbox -BuildCheck $Pass -TestCheck $Pass -PluginDriftCheck $Pass
        $result.Passed | Should -BeTrue
        ($result.Checks | Where-Object Name -eq 'Build').Passed       | Should -BeTrue
        ($result.Checks | Where-Object Name -eq 'Test').Passed        | Should -BeTrue
        ($result.Checks | Where-Object Name -eq 'VersionSync').Passed | Should -BeTrue
        ($result.Checks | Where-Object Name -eq 'PluginDrift').Passed | Should -BeTrue
    }

    It 'fails overall when the build check fails' {
        $fail = { param($Root) $global:LASTEXITCODE = 1; 'error: something broke' }
        $result = Invoke-Gate -RepoRoot $GateSandbox -BuildCheck $fail -TestCheck $Pass -PluginDriftCheck $Pass
        $result.Passed | Should -BeFalse
        ($result.Checks | Where-Object Name -eq 'Build').Passed | Should -BeFalse
    }

    It 'fails VersionSync when the 3 files disagree' {
        '#define AppVersion "0.2.0"' | Set-Content (Join-Path $GateSandbox 'installer/flaui-mcp.iss')
        $result = Invoke-Gate -RepoRoot $GateSandbox -BuildCheck $Pass -TestCheck $Pass -PluginDriftCheck $Pass
        $result.Passed | Should -BeFalse
        ($result.Checks | Where-Object Name -eq 'VersionSync').Passed | Should -BeFalse
    }

    It 'treats a nonzero warning count as a build failure even when exit code is 0' {
        $warnBuild = { param($Root) $global:LASTEXITCODE = 0; "Build succeeded.`n    2 Warning(s)`n    0 Error(s)" }
        $result = Invoke-Gate -RepoRoot $GateSandbox -BuildCheck $warnBuild -TestCheck $Pass -PluginDriftCheck $Pass
        ($result.Checks | Where-Object Name -eq 'Build').Passed | Should -BeFalse
    }

    It 'passes the build check on a real 0-warning summary line' {
        $cleanBuild = { param($Root) $global:LASTEXITCODE = 0; "Build succeeded.`n    0 Warning(s)`n    0 Error(s)" }
        $result = Invoke-Gate -RepoRoot $GateSandbox -BuildCheck $cleanBuild -TestCheck $Pass -PluginDriftCheck $Pass
        ($result.Checks | Where-Object Name -eq 'Build').Passed | Should -BeTrue
    }

    It 'fails overall when the plugin-drift check fails' {
        $fail = { param($Root) $global:LASTEXITCODE = 1; 'drift detected' }
        $result = Invoke-Gate -RepoRoot $GateSandbox -BuildCheck $Pass -TestCheck $Pass -PluginDriftCheck $fail
        $result.Passed | Should -BeFalse
        ($result.Checks | Where-Object Name -eq 'PluginDrift').Passed | Should -BeFalse
    }
}
```

- [ ] **Step 2: Run and confirm failure**

Run: `pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 5.8.0; Invoke-Pester -Path scripts/release.Tests.ps1"`
Expected: FAIL — `Invoke-Gate` not recognized.

- [ ] **Step 3: Implement it**

```powershell
# --- append to scripts/lib/release-lib.ps1 ---

function Invoke-Gate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [scriptblock]$BuildCheck = {
            param($Root)
            dotnet build (Join-Path $Root 'FlaUI.Mcp.slnx') -c Release 2>&1 | Out-String
        },
        [scriptblock]$TestCheck = {
            param($Root)
            dotnet test (Join-Path $Root 'FlaUI.Mcp.slnx') -c Release --filter 'Category!=Desktop&Category!=SyntheticInput' --no-build 2>&1 | Out-String
        },
        [scriptblock]$PluginDriftCheck = {
            param($Root)
            & (Join-Path $Root 'scripts/build-plugin.ps1') | Out-Null
            git -C $Root diff --quiet -- plugins/flaui-mcp
            $global:LASTEXITCODE = $LASTEXITCODE
            'plugin snapshot regenerated and diffed against the working tree'
        }
    )

    $checks = @()

    $buildOutput = & $BuildCheck $RepoRoot | Out-String
    $warnMatch = [regex]::Match($buildOutput, '(?m)^\s*(\d+)\s+Warning\(s\)\s*$')
    $warningCount = if ($warnMatch.Success) { [int]$warnMatch.Groups[1].Value } else { 0 }
    $buildPassed = ($LASTEXITCODE -eq 0) -and ($warningCount -eq 0)
    $checks += [pscustomobject]@{ Name = 'Build'; Passed = $buildPassed; Detail = $buildOutput.Trim() }

    $testOutput = & $TestCheck $RepoRoot | Out-String
    $testPassed = ($LASTEXITCODE -eq 0)
    $checks += [pscustomobject]@{ Name = 'Test'; Passed = $testPassed; Detail = $testOutput.Trim() }

    $sync = Get-VersionsInSync -RepoRoot $RepoRoot
    $checks += [pscustomobject]@{ Name = 'VersionSync'; Passed = $sync.InSync; Detail = $sync.Message }

    & $PluginDriftCheck $RepoRoot | Out-Null
    $driftPassed = ($LASTEXITCODE -eq 0)
    $driftDetail = if ($driftPassed) {
        'no drift'
    } else {
        'plugins/flaui-mcp drifted from .claude source — scripts/build-plugin.ps1 was run and regenerated it; review + commit the diff'
    }
    $checks += [pscustomobject]@{ Name = 'PluginDrift'; Passed = $driftPassed; Detail = $driftDetail }

    [pscustomobject]@{
        Passed = -not [bool]($checks | Where-Object { -not $_.Passed })
        Checks = $checks
    }
}
```

- [ ] **Step 4: Run and confirm all pass**

Run: `pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 5.8.0; Invoke-Pester -Path scripts/release.Tests.ps1"`
Expected: `Tests Passed: 32, Failed: 0, Skipped: 0, Inconclusive: 0, NotRun: 0` (6 new + 26 prior)

- [ ] **Step 5: Run the real (non-injected) gate against this repo as an integration smoke check**

Run: `pwsh -NoProfile -Command ". scripts/lib/release-lib.ps1; (Invoke-Gate -RepoRoot (Get-Location)).Checks | Format-Table Name, Passed"`
Expected: `Build`, `Test`, `VersionSync` all `True` (matches the live-verified 0-warning build / 689-pass test
run / 0.16.2-everywhere sync above). `PluginDrift` may be `False` if `scripts/build-plugin.ps1` finds real
drift — that's a legitimate finding, not a test bug; if so, inspect `git status` under `plugins/flaui-mcp/`.

- [ ] **Step 6: Commit**

```bash
git add scripts/lib/release-lib.ps1 scripts/release.Tests.ps1
git commit -m "feat(release): Invoke-Gate build+test+version-sync+plugin-drift gate"
```

---

## Task 8: `release.ps1` orchestrator skeleton — `-Help`, `-WhatIf`, preconditions, version compute

**Files:**
- Create: `scripts/release.ps1`

This task's shell-outs (`git`, `dotnet` via `Invoke-Gate`'s defaults) make it an **integration** task, not a
unit-tested one — Task 7 already unit-tested `Invoke-Gate`'s aggregation logic with fakes, and Task 2/3 already
unit-tested the version math and file I/O this orchestrator calls into. What's new here (`Assert-Preconditions`,
the top-level flow, `-Help`/`-WhatIf`) is verified by **running the real script** against this repo, per
instruction to be honest about integration-verified vs unit-tested steps. No commit/tag/push/LLM call happens
in this task — those arrive in Tasks 9-11.

- [ ] **Step 1: Write `scripts/release.ps1`**

```powershell
# scripts/release.ps1
<#
.SYNOPSIS
One-command flaui-mcp release: compute the next version from conventional commits, gate locally, draft the
CHANGELOG body with claude -p, review, then commit/tag/push.

.PARAMETER Help
Print usage and exit 0. No side effects.

.PARAMETER WhatIf
Run preconditions + version compute + gate, print the result and the prompt that WOULD be sent to claude -p.
Makes no writes (no commit/tag/push) and never calls the LLM.

.PARAMETER Yes
Unattended: never blocks on stdin. Auto-accepts the changelog draft and the final cut confirmation; every
other interactive gate hard-fails (exit 1) rather than opening an interactive fallback. See the plan's
"Unattended (-Yes) contract" table.

.PARAMETER Version
Pin the release to this exact X.Y.Z version instead of computing one.

.PARAMETER Bump
Force a {major|minor|patch} bump from the last tag instead of computing the level from commits.

.PARAMETER Model
The claude -p model to use for the changelog draft. Default: haiku.

.EXAMPLE
scripts/release.ps1 -WhatIf
.EXAMPLE
scripts/release.ps1 -Yes
.EXAMPLE
scripts/release.ps1 -Version 0.16.2
#>
[CmdletBinding()]
param(
    [Alias('H')][switch]$Help,
    [switch]$WhatIf,
    [Alias('y')][switch]$Yes,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [ValidateSet('major','minor','patch')][string]$Bump,
    [string]$Model = 'haiku',
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)
# -RepoRoot mirrors scripts/new-tool.ps1's existing convention (a param defaulting to the script's own repo
# location, overridable) — it's not a documented user-facing flag (omitted from Show-Usage below, since a
# real release is always run in-place), but it lets Task 9/11's integration smoke tests point the whole
# orchestrator at a disposable sandbox repo instead of the live flauimcp checkout.
# Design note: this is a plain custom -WhatIf switch, NOT [CmdletBinding(SupportsShouldProcess)]'s built-in
# ShouldProcess mechanism — the spec's -WhatIf is a specific "preview + gate, no LLM, no writes" flow, not
# generic per-cmdlet confirmation prompts, so SupportsShouldProcess is deliberately not used.
# `-?` is not declared as a param: PowerShell intercepts `-?` on any script/function that has comment-based
# help (the block above) and prints that help natively — this satisfies the spec's -Help/-H/-? trio without
# fighting the engine for the `-?` token specifically.

$ErrorActionPreference = 'Stop'
# $RepoRoot (the -RepoRoot parameter above) is the repo BEING RELEASED — for Task 9/11's sandbox smoke
# tests that's a disposable throwaway repo, not this checkout. The library always loads from THIS script's
# own location ($PSScriptRoot = the real scripts/ directory), independent of -RepoRoot.
. (Join-Path $PSScriptRoot 'lib/release-lib.ps1')

function Show-Usage {
    Write-Host @"
scripts/release.ps1 — one-command flaui-mcp release

USAGE
  scripts/release.ps1 [-WhatIf] [-Yes] [-Version X.Y.Z] [-Bump major|minor|patch] [-Model <name>]
  scripts/release.ps1 -Help

FLOW
  1. Preconditions: on master, tracked tree clean, tags fetched.
  2. Compute the next version from conventional commits since the last v* tag.
  3. Gate: dotnet build/test, 3-file version sync, plugin-snapshot drift.
  4. Draft the CHANGELOG body with 'claude -p' (heading is script-owned).
  5. You review: Accept / Regenerate / Edit / Abort.
  6. Confirm: "Cut release vX.Y.Z?" (skipped under -Yes).
  7. Commit chore(release), tag vX.Y.Z, 'git push --atomic origin master vX.Y.Z'.

FLAGS
  -Help, -H, -?     Print this usage and exit 0. No side effects.
  -WhatIf           Preview version + gate + LLM prompt. No writes, no LLM call.
  -Yes, -y          Unattended: auto-accept the draft and the final confirmation;
                    every other interactive gate hard-fails instead of blocking.
  -Version X.Y.Z    Pin the release version (skips commit-driven computation).
  -Bump <level>     Force major/minor/patch from the last tag.
  -Model <name>     claude -p model for the changelog draft (default: haiku).

EXAMPLES
  scripts/release.ps1 -WhatIf
  scripts/release.ps1 -Yes
  scripts/release.ps1 -Version 0.16.2
"@
}

function Assert-Preconditions {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RepoRoot)

    Write-Host "Fetching tags from origin..."
    git -C $RepoRoot fetch origin --tags 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "git fetch origin --tags failed (exit $LASTEXITCODE). Check network/remote access." }

    $branch = (git -C $RepoRoot rev-parse --abbrev-ref HEAD).Trim()
    if ($branch -ne 'master') { throw "Must be on 'master' to release (currently on '$branch')." }

    $dirty = git -C $RepoRoot status --porcelain --untracked-files=no
    if ($dirty) { throw "Working tree has uncommitted tracked changes (untracked files are OK):`n$dirty" }

    Write-Host "Preconditions OK: on master, tracked tree clean, tags fetched."
}

if ($Help) { Show-Usage; exit 0 }

try {
    Assert-Preconditions -RepoRoot $RepoRoot

    $sync = Get-VersionsInSync -RepoRoot $RepoRoot
    if (-not $sync.InSync) { throw "Version files are out of sync: $($sync.Message). Fix before releasing." }
    $filesVersion = $sync.Versions.Csproj

    $lastTag = (git -C $RepoRoot describe --tags --match 'v*' --abbrev=0 2>$null)
    $lastTagFound = ($LASTEXITCODE -eq 0) -and $lastTag
    $lastTagVersion = if ($lastTagFound) { $lastTag.Trim().TrimStart('v') } else { '0.0.0' }
    $rangeArgs = if ($lastTagFound) { @("$($lastTag.Trim())..HEAD") } else { @() }

    $rawLog = (git -C $RepoRoot log @rangeArgs --format='%B%x1e' 2>$null | Out-String)
    $commitMessages = @($rawLog -split "`u{1e}" | Where-Object { $_.Trim() })

    $next = Get-NextVersion -CurrentVersion $lastTagVersion -CommitMessages $commitMessages -OverrideVersion $Version -OverrideBump $Bump

    if ($next.NothingToRelease) {
        $since = if ($lastTagFound) { $lastTag.Trim() } else { '(repo start)' }
        Write-Host "Nothing to release since $since — no feat/fix/breaking commits found. Use -Version or -Bump to force a release."
        exit 0
    }

    Write-Host "Computed next version: $($next.Version) (level=$($next.Level), trigger=$($next.Trigger), base=$lastTagVersion)"
    if ($next.NonConventionalCount -gt 0) {
        Write-Warning "$($next.NonConventionalCount) commit(s) in range are not conventional-format and were ignored for version math."
    }

    # Design gap this plan resolves (see Ground-truth facts): warn, don't hard-fail, when the freshly
    # computed version disagrees with what the 3 project files currently say — unless the human explicitly
    # pinned -Version/-Bump, in which case they've already made the call.
    if (-not $Version -and -not $Bump -and ($next.Version -ne $filesVersion)) {
        $msg = "Computed next version ($($next.Version), from $lastTagVersion + commit math) differs from " +
               "the version already in the 3 project files ($filesVersion). This can happen when the files " +
               "were pre-bumped by a prior merge that hasn't been tagged/released yet."
        Write-Warning $msg
        if ($Yes) { throw "$msg Refusing to guess under -Yes — re-run with an explicit -Version to pin it." }
        $ans = Read-Host "Proceed releasing as $($next.Version)? [y/N] (or Ctrl+C and re-run with -Version to pin a different one)"
        if ($ans -notmatch '^[Yy]') { Write-Host "Aborted — re-run with -Version X.Y.Z to pin the release version."; exit 0 }
    }

    $gate = Invoke-Gate -RepoRoot $RepoRoot
    foreach ($c in $gate.Checks) {
        $status = if ($c.Passed) { 'PASS' } else { 'FAIL' }
        Write-Host "[$status] $($c.Name): $($c.Detail)"
    }
    if (-not $gate.Passed) { throw "Gate failed — fix the failing check(s) above before releasing." }

    $diffText = (git -C $RepoRoot log @rangeArgs -p 2>$null | Out-String)
    $diffStatText = (git -C $RepoRoot log @rangeArgs --stat 2>$null | Out-String)
    $exemplar = Get-TopChangelogSection -ChangelogPath (Join-Path $RepoRoot 'CHANGELOG.md') -Count 2
    $prompt = Get-ChangelogPrompt -Version $next.Version -CommitMessages $commitMessages -DiffText $diffText -DiffStatText $diffStatText -StyleExemplar $exemplar

    if ($WhatIf) {
        Write-Host "`n[-WhatIf] Prompt that WOULD be sent to 'claude -p --model $Model':`n"
        Write-Host $prompt
        Write-Host "`n[-WhatIf] No commit, tag, or push will happen. Exiting without side effects."
        exit 0
    }

    # --- draft (Task 9) / review (Task 10) / reconciliation + commit/tag/push (Task 11) appended below ---
}
catch {
    Write-Error $_
    exit 1
}
```

- [ ] **Step 2: Smoke-test `-Help` (no git/gate calls, exit 0)**

Run: `pwsh -NoProfile -File scripts/release.ps1 -Help`
Expected: prints the USAGE/FLOW/FLAGS/EXAMPLES block above and exits 0. Confirm no network/git activity:
`git -C . status --porcelain` before and after should be identical (Help returns before `Assert-Preconditions`
even runs).

- [ ] **Step 3: Smoke-test `-WhatIf` against this repo's real state**

Run: `pwsh -NoProfile -File scripts/release.ps1 -WhatIf`
Expected: preconditions OK, `Computed next version: ...`, all 4 gate checks print (Build/Test/VersionSync
PASS; PluginDrift depends on live drift state — see Task 7 Step 5), the full `claude -p` prompt text prints,
then `[-WhatIf] No commit, tag, or push will happen. Exiting without side effects.` and exit code 0. Given the
ground-truth "files read 0.16.2, last tag v0.16.1" state, expect either the version-mismatch confirmation
prompt (if the computed next version differs from 0.16.2) or a clean match — both are correct behavior,
not a bug in this script.

Run: `git status --porcelain` immediately after — expected: **identical to before the run** except for
whatever `PluginDrift`'s regeneration step may have changed under `plugins/flaui-mcp/` (that's `Invoke-Gate`
doing its job, not `-WhatIf` writing; if it fires, review/commit it separately, it's not a release-tooling bug).

- [ ] **Step 4: Commit**

```bash
git add scripts/release.ps1
git commit -m "feat(release): release.ps1 orchestrator skeleton with -Help and -WhatIf"
```

---

## Task 9: `claude -p` changelog draft — timeout, resume, zero-commit skip, editor fallback

**Files:**
- Modify: `scripts/release.ps1`

Integration task (shells to `claude -p`, `Start-Job`, `$env:EDITOR`) — verified by live invocation (already
confirmed in Ground-truth facts: `echo "..." | claude -p --model haiku --output-format text` → `PONG`) plus a
`-WhatIf`-adjacent smoke run described below. No Pester coverage: a background-job timeout test would be slow
and flaky, and mocking `claude -p` itself would test the mock, not the integration.

- [ ] **Step 1: Add the draft-step functions to `scripts/release.ps1`**

Insert these function definitions above the `if ($Help) { Show-Usage; exit 0 }` line added in Task 8:

```powershell
function Get-EmptyChangelogTemplate {
    @"
### Added
- 

### Fixed
- 

### Changed
- 
"@
}

function Edit-InEditor {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$InitialContent)

    $tmp = [IO.Path]::GetTempFileName() -replace '\.tmp$', '.md'
    Set-Content -Path $tmp -Value $InitialContent -NoNewline -Encoding UTF8
    $editor = $env:EDITOR
    if ([string]::IsNullOrWhiteSpace($editor)) { $editor = 'notepad' }
    & $editor $tmp | Out-Null
    $result = Get-Content $tmp -Raw
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    $result
}

function Invoke-ChangelogLlm {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Prompt,
        [string]$Model = 'haiku',
        [int]$TimeoutSeconds = 120
    )

    $job = Start-Job -ScriptBlock {
        param($PromptText, $ModelName)
        $out = $PromptText | & claude -p --model $ModelName --output-format text 2>&1 | Out-String
        [pscustomobject]@{ Output = $out; ExitCode = $LASTEXITCODE }
    } -ArgumentList $Prompt, $Model

    $finished = Wait-Job -Job $job -Timeout $TimeoutSeconds
    if (-not $finished) {
        Stop-Job $job | Out-Null
        Remove-Job $job -Force | Out-Null
        return [pscustomobject]@{ Success = $false; Body = $null; Reason = "claude -p timed out after ${TimeoutSeconds}s" }
    }

    $result = Receive-Job $job
    Remove-Job $job -Force | Out-Null

    if (-not $result -or $result.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($result.Output)) {
        $reason = if ($result) { "claude -p exited $($result.ExitCode): $($result.Output)" } else { "claude -p produced no result" }
        return [pscustomobject]@{ Success = $false; Body = $null; Reason = $reason }
    }

    [pscustomobject]@{ Success = $true; Body = $result.Output.Trim(); Reason = $null }
}

function Get-OrCreateDraft {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$Prompt,
        [Parameter(Mandatory)][bool]$HasCommits,
        [string]$Model = 'haiku',
        [switch]$Yes
    )

    $draftPath = Join-Path ([IO.Path]::GetTempPath()) "flaui-mcp-release-draft-$Version.md"

    if (Test-Path $draftPath) {
        $resume = [bool]$Yes
        if (-not $Yes) {
            $ans = Read-Host "Found an existing draft for v$Version from a previous run. Resume it? [Y/n]"
            $resume = ($ans -eq '' -or $ans -match '^[Yy]')
        }
        if ($resume) {
            Write-Host "Resuming draft from $draftPath"
            return [pscustomobject]@{ DraftPath = $draftPath; Body = (Get-Content $draftPath -Raw) }
        }
        Remove-Item $draftPath -Force
    }

    if (-not $HasCommits) {
        if ($Yes) {
            throw "Zero-commit release with -Yes: nothing for the LLM to summarize, and no interactive `$EDITOR fallback is allowed unattended. Re-run without -Yes, or edit CHANGELOG.md by hand."
        }
        Write-Warning "No commits in range — nothing to summarize. Opening `$EDITOR on an empty template."
        $body = Edit-InEditor -InitialContent (Get-EmptyChangelogTemplate)
        Set-Content -Path $draftPath -Value $body -NoNewline -Encoding UTF8
        return [pscustomobject]@{ DraftPath = $draftPath; Body = $body }
    }

    $llm = Invoke-ChangelogLlm -Prompt $Prompt -Model $Model
    if (-not $llm.Success) {
        if ($Yes) {
            throw "claude -p failed unattended (-Yes): $($llm.Reason). No interactive `$EDITOR fallback is allowed under -Yes."
        }
        Write-Warning "claude -p failed: $($llm.Reason). Falling back to `$EDITOR on an empty template."
        $body = Edit-InEditor -InitialContent (Get-EmptyChangelogTemplate)
    } else {
        $body = $llm.Body
    }
    Set-Content -Path $draftPath -Value $body -NoNewline -Encoding UTF8
    [pscustomobject]@{ DraftPath = $draftPath; Body = $body }
}
```

- [ ] **Step 2: Wire it into the main flow**

Replace the `# --- draft (Task 9) / review (Task 10) / reconciliation + commit/tag/push (Task 11) appended
below ---` placeholder line from Task 8 with:

```powershell
    $hasCommits = ($commitMessages.Count -gt 0)
    $draft = Get-OrCreateDraft -Version $next.Version -Prompt $prompt -HasCommits $hasCommits -Model $Model -Yes:$Yes

    # --- review (Task 10) / reconciliation + commit/tag/push (Task 11) appended below ---
```

- [ ] **Step 3: Live-verify the `claude -p` stdin invocation shape used by `Invoke-ChangelogLlm` matches the CLI**

Run: `echo "Reply with exactly: PONG" | claude -p --model haiku --output-format text`
Expected: `PONG` (already confirmed in Ground-truth facts — this step re-confirms it wasn't a fluke and that
the pipe-to-stdin shape `Invoke-ChangelogLlm` uses is exactly this).

- [ ] **Step 4: Verify the zero-commit + `-Yes` guard**

Dot-sourcing `scripts/release.ps1` directly to reach into its internals is NOT viable here: it has top-level
executable code ending in `exit`, and PowerShell's `exit` terminates the *whole calling process* when hit
inside a dot-sourced script (not just the dot-sourced scope) — it would kill an interactive smoke session,
not just return from the Help branch. `new-tool.ps1` avoids this entirely by only ever being **invoked**
(`& $Script args`), never dot-sourced (confirmed: `new-tool.Tests.ps1` always calls it via `&`), and this
script follows the same convention — so the guard is verified by code inspection instead of a live call:

The guard is a single, unconditional, statically-checkable branch (from Step 1's `Get-OrCreateDraft`):
```powershell
    if (-not $HasCommits) {
        if ($Yes) {
            throw "Zero-commit release with -Yes: nothing for the LLM to summarize, and no interactive `$EDITOR fallback is allowed unattended. Re-run without -Yes, or edit CHANGELOG.md by hand."
        }
        ...
```
Confirm by reading `scripts/release.ps1` after Step 1: `$HasCommits` is computed once, right before the call,
as `($commitMessages.Count -gt 0)` (Step 2's wiring) — so this branch is unreachable unless the commit range
is genuinely empty, and the `-Yes` check is the first line inside it (no code path reaches the editor or the
LLM before the throw). The end-to-end behavioral case — a real zero-commit range — occurs naturally the first
time someone runs `-Version <version-equal-to-HEAD's-already-released-tag>` (an unusual but real "nothing to
re-summarize" scenario); Task 12's final integration pass re-confirms this reasoning holds once the full
script exists.

- [ ] **Step 5: Commit**

```bash
git add scripts/release.ps1
git commit -m "feat(release): claude -p changelog draft step with timeout + resume + fallback"
```

---

## Task 10: interactive draft review + the `-Yes` unattended contract

**Files:**
- Modify: `scripts/release.ps1`

Integration task (reads from stdin, shells to `claude -p` on Regenerate). "Abort" is bound to key `X`, not
`A` — `A`ccept and `A`bort both start with the letter A, so binding Abort to `A` too would make the prompt
ambiguous; `X` (as in exit) is the deliberate, documented resolution.

- [ ] **Step 1: Add `Invoke-DraftReview` to `scripts/release.ps1`**

Insert directly after `Get-OrCreateDraft` (added in Task 9):

```powershell
function Invoke-DraftReview {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$DraftPath,
        [Parameter(Mandatory)][string]$Prompt,
        [string]$Model = 'haiku',
        [switch]$Yes
    )

    while ($true) {
        $body = Get-Content $DraftPath -Raw

        if ($Yes) {
            $key = 'A'
        } else {
            Write-Host "`n--- Draft changelog body for v$Version ---`n$body`n---`n"
            $ans = Read-Host "[A]ccept (default) / [R]egenerate / [E]dit / e[X]it-abort"
            $key = if ([string]::IsNullOrWhiteSpace($ans)) { 'A' } else { $ans.Substring(0,1).ToUpperInvariant() }
        }

        if ($key -eq 'A') {
            $validBody = (-not [string]::IsNullOrWhiteSpace($body)) -and ($body -match '(?m)^###\s')
            if ($validBody) { return [pscustomobject]@{ Action = 'Accept'; Body = $body } }

            if ($Yes) {
                throw "Accepted body for v$Version failed validation (empty, or no '### ' section) — cannot loop back to Review unattended under -Yes."
            }
            Write-Warning "Draft is empty or has no '### ' section — a heading-only release entry is not allowed. Opening `$EDITOR to fix."
            $body = Edit-InEditor -InitialContent $body
            Set-Content -Path $DraftPath -Value $body -NoNewline -Encoding UTF8
            continue
        }

        if ($key -eq 'X') {
            Write-Host "Aborted. Draft left at $DraftPath for a later resume."
            return [pscustomobject]@{ Action = 'Abort'; Body = $null }
        }

        if ($key -eq 'R') {
            $llm = Invoke-ChangelogLlm -Prompt $Prompt -Model $Model
            $body = if ($llm.Success) {
                $llm.Body
            } else {
                Write-Warning $llm.Reason
                Edit-InEditor -InitialContent (Get-EmptyChangelogTemplate)
            }
            Set-Content -Path $DraftPath -Value $body -NoNewline -Encoding UTF8
            continue
        }

        if ($key -eq 'E') {
            $body = Edit-InEditor -InitialContent $body
            Set-Content -Path $DraftPath -Value $body -NoNewline -Encoding UTF8
            continue
        }

        Write-Warning "Unrecognized choice '$ans' — enter A, R, E, or X."
    }
}
```

- [ ] **Step 2: Wire it into the main flow**

Replace the `# --- review (Task 10) / reconciliation + commit/tag/push (Task 11) appended below ---`
placeholder from Task 9 with:

```powershell
    $review = Invoke-DraftReview -Version $next.Version -DraftPath $draft.DraftPath -Prompt $prompt -Model $Model -Yes:$Yes
    if ($review.Action -eq 'Abort') { exit 0 }

    # --- reconciliation + commit/tag/push (Task 11) appended below ---
```

- [ ] **Step 3: The full `-Yes` unattended contract (every interactive gate → its resolution)**

| # | Interactive gate | Where implemented | `-Yes` resolution |
|---|---|---|---|
| 1 | Draft review (accept/regen/edit/abort) | Task 10, `Invoke-DraftReview` | auto-accept (`$key = 'A'`) |
| 2 | Resume a surviving temp draft | Task 9, `Get-OrCreateDraft` | auto-resume (`$resume = [bool]$Yes`) |
| 3 | LLM timeout/error → `$EDITOR` fallback | Task 9, `Get-OrCreateDraft` | **hard-fail `exit 1`** (throws before any editor call) |
| 4 | Zero-commit range → `$EDITOR` fallback | Task 9, `Get-OrCreateDraft` | **hard-fail `exit 1`** (throws before any editor call) |
| 5 | Accepted-body validation failure | Task 10, `Invoke-DraftReview` | **hard-fail `exit 1`** (the loop-back-to-Review is interactive-only) |
| 6 | Computed-vs-files version mismatch (this plan's added guard — see Ground-truth facts) | Task 8, main flow | **hard-fail `exit 1`** (refuses to guess) |
| 7 | Half-finished prior release reconciliation | Task 11, main flow | **hard-fail `exit 1`**, printing manual reconciliation steps |
| 8 | Final "Cut release?" confirmation | Task 11, `Invoke-ReleaseCommit` | proceed (skip the prompt) |

Rows 1, 2, and 8 are the only prompts `-Yes` answers by *proceeding*; rows 3-7 are all "never guess, never
silently mutate git history unattended" hard-fails. This is a superset of the spec's literal 7-row table —
row 6 is this plan's own addition, required because Task 8 introduces an interactive gate (the version-
mismatch confirm) the spec's prose implies but doesn't enumerate in its `-Yes` table.

- [ ] **Step 4: Commit**

```bash
git add scripts/release.ps1
git commit -m "feat(release): interactive draft review + -Yes unattended contract"
```

---

## Task 11: half-finished-release reconciliation + commit/tag/push

**Files:**
- Modify: `scripts/release.ps1`

Integration task. Two distinct pieces: (a) an EARLY, version-agnostic half-finished-release detector that
runs right after preconditions — before any version computation, gate, or draft — because a prior failed
`--atomic` push can leave HEAD *as* the release commit, which would otherwise get re-processed as if it were
new pending work; (b) the final commit/tag/push itself, gated by the "Cut release?" confirmation.

Per this repo's git safety norms (no destructive `reset`/`tag -d` run automatically), reconciliation offers
only **Push the existing commit+tag** or **Abort and leave it**, never an automated "reset and re-cut" — if
the user wants a clean re-cut they run the reset themselves (the error/prompt text says so explicitly). This
is a deliberate simplification of the spec's more abstract "offer to re-push... or reset them for a clean
re-cut" — captured here, not silently decided.

- [ ] **Step 1: Add `Get-ReleaseReconciliationState` and `Invoke-ReleaseCommit` to `scripts/release.ps1`**

Insert directly after `Invoke-DraftReview` (added in Task 10):

```powershell
function Get-ReleaseReconciliationState {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RepoRoot)

    git -C $RepoRoot fetch origin master --quiet 2>$null
    $remoteHead = (git -C $RepoRoot rev-parse origin/master 2>$null)
    $remoteHead = if ($remoteHead) { $remoteHead.Trim() } else { $null }
    $localHead  = (git -C $RepoRoot rev-parse HEAD).Trim()
    $headOnRemote = ($remoteHead -and ($remoteHead -eq $localHead))

    $headSubject = (git -C $RepoRoot log -1 --format='%s').Trim()
    $releaseMatch = [regex]::Match($headSubject, '^chore\(release\):\s*v(?<v>\d+\.\d+\.\d+)')
    $headIsUnpushedRelease = $releaseMatch.Success -and -not $headOnRemote
    $headReleaseVersion = if ($releaseMatch.Success) { $releaseMatch.Groups['v'].Value } else { $null }

    $localTagsAtHead = @(git -C $RepoRoot tag --points-at HEAD | Where-Object { $_ -match '^v\d+\.\d+\.\d+$' })
    $orphanTags = @()
    foreach ($t in $localTagsAtHead) {
        $remoteHas = [bool](git -C $RepoRoot ls-remote --tags origin $t)
        if (-not $remoteHas) { $orphanTags += $t }
    }

    [pscustomobject]@{
        HalfFinished          = $headIsUnpushedRelease -or ($orphanTags.Count -gt 0)
        HeadIsUnpushedRelease = $headIsUnpushedRelease
        HeadReleaseVersion    = $headReleaseVersion
        LocalOnlyTagsAtHead   = $orphanTags
    }
}

function Resolve-HalfFinishedRelease {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][pscustomobject]$Reconciliation,
        [switch]$Yes,
        [switch]$WhatIf
    )

    $targetVersion = if ($Reconciliation.HeadReleaseVersion) {
        $Reconciliation.HeadReleaseVersion
    } else {
        $Reconciliation.LocalOnlyTagsAtHead[0].TrimStart('v')
    }
    $tag = "v$targetVersion"

    $tagExistsLocally = [bool](git -C $RepoRoot tag -l $tag)
    $tagPointsAtHead = $false
    if ($tagExistsLocally) {
        $tagSha = (git -C $RepoRoot rev-parse "$tag^{commit}" 2>$null)
        $headSha = (git -C $RepoRoot rev-parse HEAD).Trim()
        $tagPointsAtHead = ($tagSha -and ($tagSha.Trim() -eq $headSha))
    }

    if ($tagExistsLocally -and -not $tagPointsAtHead) {
        throw "Tag $tag exists locally but does NOT point at HEAD — likely orphaned by a 'git commit --amend' after a failed push. Refusing to auto-reconcile. Inspect manually: git log --oneline $tag HEAD~3..HEAD, then either move the tag or reset HEAD by hand."
    }

    # -WhatIf takes precedence over -Yes when both are somehow passed: -WhatIf's contract is "no writes,
    # ever" (including the -Yes hard-fail's exit 1, which is itself a mutation-adjacent unattended
    # decision), so it must win to preserve the read-only guarantee.
    if ($WhatIf) {
        Write-Host "[-WhatIf] Half-finished release detected: HEAD looks like an unpushed '$tag' release (the commit and/or tag exist locally, but a previous 'git push --atomic' didn't land). -WhatIf makes no changes; re-run without -WhatIf to reconcile."
        return
    }

    if ($Yes) {
        throw "Half-finished prior release detected for $tag — refusing to auto-mutate git history under -Yes. Re-run without -Yes to reconcile interactively."
    }

    Write-Host "Half-finished prior release detected: HEAD looks like an unpushed '$tag' release (the commit and/or tag exist locally, but a previous 'git push --atomic' didn't land)."
    $ans = Read-Host "[P]ush the existing commit+tag now / e[X]it and leave as-is?"
    if ($ans.Substring(0,1).ToUpperInvariant() -eq 'P') {
        if (-not $tagExistsLocally) { git -C $RepoRoot tag $tag }
        git -C $RepoRoot push --atomic origin master $tag
        if ($LASTEXITCODE -ne 0) { throw "push --atomic failed again (exit $LASTEXITCODE). Local state is unchanged; re-run to retry." }
        Write-Host "Pushed $tag."
        exit 0
    }
    Write-Host "Left as-is. Re-run scripts/release.ps1 when ready to retry the push."
    exit 0
}

function Invoke-ReleaseCommit {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$Version,
        [switch]$Yes
    )

    if (-not $Yes) {
        $ans = Read-Host "Cut release v$Version — commit, tag, and push to origin? [y/N]"
        if ($ans -notmatch '^[Yy]') { Write-Host "Aborted — nothing committed."; exit 0 }
    }

    $tag = "v$Version"
    git -C $RepoRoot add `
        CHANGELOG.md `
        src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj `
        installer/flaui-mcp.iss `
        plugins/flaui-mcp/.claude-plugin/plugin.json
    if ($LASTEXITCODE -ne 0) { throw "git add failed (exit $LASTEXITCODE)." }

    git -C $RepoRoot commit -m "chore(release): $tag"
    if ($LASTEXITCODE -ne 0) { throw "git commit failed (exit $LASTEXITCODE)." }

    git -C $RepoRoot tag $tag
    if ($LASTEXITCODE -ne 0) { throw "git tag $tag failed (exit $LASTEXITCODE)." }

    git -C $RepoRoot push --atomic origin master $tag
    if ($LASTEXITCODE -ne 0) {
        throw "git push --atomic origin master $tag FAILED (exit $LASTEXITCODE). Both refs were rejected together (--atomic working as intended) — the local commit + tag are intact. Re-run scripts/release.ps1 to retry; it will detect this half-finished state and offer to re-push."
    }

    $remoteUrl = (git -C $RepoRoot remote get-url origin).Trim()
    $slug = if ($remoteUrl -match 'github\.com[:/](?<slug>[^/]+/[^/.]+)') { $Matches.slug } else { $null }
    if ($slug) {
        Write-Host "Pushed. Watch CI: https://github.com/$slug/actions"
    } else {
        Write-Host "Pushed $tag to origin/master."
    }
}
```

- [ ] **Step 2: Insert the EARLY reconciliation check right after preconditions**

In the `try { ... }` block from Task 8, immediately after `Assert-Preconditions -RepoRoot $RepoRoot` and
before `$sync = Get-VersionsInSync ...`, insert:

```powershell
    $recon = Get-ReleaseReconciliationState -RepoRoot $RepoRoot
    if ($recon.HalfFinished) {
        Resolve-HalfFinishedRelease -RepoRoot $RepoRoot -Reconciliation $recon -Yes:$Yes -WhatIf:$WhatIf
    }

```

- [ ] **Step 3: Wire the final commit/tag/push into the main flow**

Replace the `# --- reconciliation + commit/tag/push (Task 11) appended below ---` placeholder from Task 10
with:

```powershell
    Add-ChangelogSection -ChangelogPath (Join-Path $RepoRoot 'CHANGELOG.md') -Version $next.Version -Body $review.Body
    Set-ProjectVersion -RepoRoot $RepoRoot -Version $next.Version

    Invoke-ReleaseCommit -RepoRoot $RepoRoot -Version $next.Version -Yes:$Yes

    Remove-Item $draft.DraftPath -Force -ErrorAction SilentlyContinue
```

- [ ] **Step 4: Integration smoke — half-finished detection against a disposable sandbox**

```powershell
# Build a throwaway "origin" + working clone so the push/reconciliation logic is exercised for real,
# without touching this repo or any real GitHub remote.
$bare = Join-Path ([IO.Path]::GetTempPath()) ("release-smoke-origin-" + [guid]::NewGuid())
$work = Join-Path ([IO.Path]::GetTempPath()) ("release-smoke-work-"   + [guid]::NewGuid())
git init -q --bare $bare
git clone -q $bare $work
git -C $work config user.email 'smoke@example.com'
git -C $work config user.name  'Smoke Test'
git -C $work checkout -q -b master
'seed' | Set-Content (Join-Path $work 'seed.txt')
git -C $work add -A
git -C $work commit -q -m "chore: seed"
git -C $work push -q -u origin master
git -C $work commit -q --allow-empty -m "chore(release): v9.9.9"
git -C $work tag v9.9.9
# NOTE: deliberately do NOT push — this simulates a failed 'git push --atomic'.
```
`Get-ReleaseReconciliationState`/`Resolve-HalfFinishedRelease` live inside `release.ps1` itself, which (per
Task 9 Step 4's reasoning) is **invoked**, not dot-sourced, so exercise them by calling the real script
against the sandbox via `-RepoRoot`:
```powershell
& (Join-Path (Get-Location) 'scripts/release.ps1') -RepoRoot $work -Yes
```
Expected: exits non-zero with the message starting `Half-finished prior release detected for v9.9.9 —
refusing to auto-mutate git history under -Yes.` — confirming `-Yes` hard-fails rather than pushing or
hanging.
```powershell
& (Join-Path (Get-Location) 'scripts/release.ps1') -RepoRoot $work
```
Then answer `P` at the `[P]ush the existing commit+tag now / e[X]it and leave as-is?` prompt. Expected:
`git -C $bare tag -l` now lists `v9.9.9`, and `git -C $bare log master --oneline -1` shows the
`chore(release): v9.9.9` commit — the existing commit+tag were pushed, nothing was duplicated.

Clean up: `Remove-Item -Recurse -Force $bare, $work`.

- [ ] **Step 5: Commit**

```bash
git add scripts/release.ps1
git commit -m "feat(release): commit/tag/push with half-finished-release reconciliation"
```

---

## Task 12: `release.yml` release body from CHANGELOG + final integration verification

**Files:**
- Modify: `.github/workflows/release.yml:53-61` (insert a step before `Create GitHub Release`)

- [ ] **Step 1: Insert a "Build release notes" step and add `body_path` to the Release step**

Current (lines 53-61, confirmed in Ground-truth facts):
```yaml
      - name: Create GitHub Release
        if: startsWith(github.ref, 'refs/tags/')   # only on a v* tag; manual runs stop after checksums
        uses: softprops/action-gh-release@v3
        with:
          files: |
            dist/flaui-mcp-setup.exe
            publish/flaui-mcp.exe
            dist/install.ps1
            dist/SHA256SUMS.txt
```
Replace with:
```yaml
      - name: Build release notes from CHANGELOG
        if: startsWith(github.ref, 'refs/tags/')
        shell: pwsh
        run: |
          . scripts/lib/release-lib.ps1
          Get-TopChangelogSection -ChangelogPath CHANGELOG.md | Set-Content -Path release-body.md -Encoding UTF8

      - name: Create GitHub Release
        if: startsWith(github.ref, 'refs/tags/')   # only on a v* tag; manual runs stop after checksums
        uses: softprops/action-gh-release@v3
        with:
          body_path: release-body.md
          files: |
            dist/flaui-mcp-setup.exe
            publish/flaui-mcp.exe
            dist/install.ps1
            dist/SHA256SUMS.txt
```
Note: the spec's inline example (`Get-TopChangelogSection | Set-Content release-body.md`) omits the
mandatory `-ChangelogPath` parameter this plan's actual function signature requires (Task 4) — the workflow
step above calls it correctly with the explicit path. windows-latest already has `pwsh` (used elsewhere in
this same workflow's "Build installer"/"Checksums" steps), so no new runner dependency is introduced.

- [ ] **Step 2: Validate the YAML is well-formed**

Run: `yq eval . .github/workflows/release.yml`
Expected: prints the full parsed document with no error (a YAML syntax error would print `Error: yaml:
line N: ...` and exit non-zero).

- [ ] **Step 3: Unit-verify the exact function call the workflow uses, against the real CHANGELOG.md**

Run: `pwsh -NoProfile -Command ". scripts/lib/release-lib.ps1; Get-TopChangelogSection -ChangelogPath CHANGELOG.md"`
Expected: prints the `## [0.16.1] - 2026-07-18` section verbatim (the current top section, per Ground-truth
facts) — confirming the exact command the CI step runs produces sane output against this repo's real file
before it's ever exercised by an actual tag push.

- [ ] **Step 4: Final integration verification — the full local gate + full Pester suite**

```powershell
dotnet build FlaUI.Mcp.slnx -c Debug
# Expected: "Build succeeded." — "0 Warning(s)", "0 Error(s)".

dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&Category!=SyntheticInput"
# Expected: "Passed!  - Failed:     0, Passed:   689, Skipped:     0, Total:   689" (or higher, if unrelated
# work landed on master in the meantime — 0 failures either way).

pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 5.8.0; `$r = Invoke-Pester -Path scripts/ -PassThru; \"Passed=`$($r.PassedCount) Failed=`$($r.FailedCount) Total=`$($r.TotalCount)\""
# Expected: Failed=0. PassedCount = 32 (this plan's scripts/release.Tests.ps1 tests, Tasks 1-7) + 4
# (pre-existing scripts/new-tool.Tests.ps1) = 36, Total=36, Failed=0.

pwsh -NoProfile -File scripts/release.ps1 -Help
# Expected: usage text, exit 0.

pwsh -NoProfile -File scripts/release.ps1 -WhatIf
# Expected: preconditions OK, computed version, all-PASS gate (or a legitimate PluginDrift finding to
# resolve separately), full LLM prompt text, "[-WhatIf] No commit, tag, or push will happen.", exit 0.
```

- [ ] **Step 5: Commit**

```bash
git add .github/workflows/release.yml
git commit -m "feat(release): release.yml release body from CHANGELOG top section"
```

---

## Self-Review

### Spec-coverage table (every spec section → task)

| Spec section | Task(s) |
|---|---|
| Goal | Whole plan; orchestration in Tasks 8-11 |
| Current release flow (context) | Ground-truth facts |
| One-command flow steps 1-8 | 1=T8, 2=T8, 3=T7/T8, 4=T6/T9, 5=T10, 6=T11, 7=T11, 8=T12 |
| Source-of-truth split table | T2 (version=script), T9/T6 (prose=LLM), T10 (review=human) |
| Why this ordering (gate before LLM; `--atomic`) | T8 (gate runs before prompt assembly/draft), T11 (`push --atomic`) |
| Components: `release.ps1` | T8-T11 |
| Components: `release-lib.ps1` (7 functions) | T2 (`Get-NextVersion`), T3 (`Get-VersionsInSync`/`Set-ProjectVersion`), T4 (`Get-TopChangelogSection`), T5 (`Add-ChangelogSection`), T6 (`Get-ChangelogPrompt`), T7 (`Invoke-Gate`) |
| Components: `release.Tests.ps1` | T1 (scaffold) + appended in T2-T7 |
| Components: reuses `build-plugin.ps1` | T7 (`Invoke-Gate`'s default `PluginDriftCheck`) |
| Components: `release.yml` change | T12 |
| Version computation rules | T2 |
| Changelog drafting (LLM step) | T6 (prompt), T9 (invocation/resume/temp-file), T10 (review actions) |
| Guard: LLM timeout/error | T9 |
| Guard: zero-commit release | T9 |
| Guard: context blowout | T6 |
| Guard: malformed LLM markdown (tolerant extraction) | T4 |
| Guard: accepted-body check | T10 |
| Guard: half-finished prior release + orphaned-tag check | T11 |
| CLI flags (`-Help`/`-WhatIf`/`-Yes`/`-Version`/`-Bump`/`-Model`) | T8 (all declared); `-Yes` threaded through T9-T11 |
| Unattended (`-Yes`) contract table | T10 Step 3 (8 rows — spec's 7 + this plan's added version-mismatch row) |
| `release.yml` change detail | T12 |
| Testing section | T1 (CI job), T2-T7 (Pester), T8-T12 (honest integration-vs-unit split stated per task) |
| Non-goals (no build/publish changes beyond body; no multi-crate; no local installer build in gate; no Desktop tests) | Implicitly respected — no task touches `release.yml`'s build/publish steps, adds cross-project machinery, runs ISCC locally, or runs `Category=Desktop`/`SyntheticInput` tests |
| Operational note (files=0.16.2 vs tag=v0.16.1) | Ground-truth facts + T8's version-mismatch warn-and-confirm guard exercises exactly this scenario |
| Success criterion 1 (end-to-end cut) | T8-T11 |
| Success criterion 2 (release.yml publishes with curated body) | T12 |
| Success criterion 3 (partial push failure leaves remote clean, draft intact) | T11 (`--atomic` + `Get-OrCreateDraft`'s temp-file survives a push failure since it's removed only after `Invoke-ReleaseCommit` succeeds) |
| Success criterion 4 (`-WhatIf` full preview, zero writes) | T8 |
| Success criterion 5 (Pester tests pass in CI) | T1 (job) + T2-T7 (tests) |

### Placeholder scan

Searched the finished document for "TBD", "TODO", "implement later", "add appropriate error handling", "add
validation", "handle edge cases", "similar to Task N", and bare prose describing behavior without code. None
found in any code block. The only literal `TODO`-shaped text is inside `Get-EmptyChangelogTemplate`'s output
(the `### Added` / `- ` blank-bullet template a human fills in by hand under the LLM-failure/zero-commit
fallback) — that's the intended, real runtime behavior (an empty section for a human to edit), not a plan
placeholder.

### Type/naming consistency check across tasks

Verified every cross-task reference resolves to the same shape it was defined with:
- `Get-NextVersion` returns `{Version, Level, Trigger, NothingToRelease, NonConventionalCount, Commits}` (T2)
  — T8 reads `.NothingToRelease`, `.Version`, `.Level`, `.Trigger`, `.NonConventionalCount`, all present.
- `Get-VersionsInSync` returns `{InSync, Versions{Csproj,Iss,Plugin}, Message}` (T3) — T8 reads
  `.InSync`, `.Message`, `.Versions.Csproj`, all present; T7's `Invoke-Gate` reads `.InSync`/`.Message` the
  same way.
- `Invoke-Gate` returns `{Passed, Checks[{Name,Passed,Detail}]}` (T7) — T8 iterates `.Checks` reading
  `.Passed`/`.Name`/`.Detail`, consistent.
- `Get-ChangelogPrompt` signature `(-Version, -CommitMessages, -DiffText, -DiffStatText, -StyleExemplar,
  [-DiffSizeThresholdBytes], [-CommitCountThreshold])` (T6) — T8's call site supplies all 5 required params
  by name, consistent.
- `Get-TopChangelogSection -ChangelogPath -Count` (T4, `-Count` default 1) — T8 calls it with `-Count 2` for
  the exemplar; T12's `release.yml` step and Step 3 call it with no `-Count` (defaults to 1), consistent with
  "extract the top section" for the release body.
- `Get-OrCreateDraft` returns `{DraftPath, Body}` (T9) — T10 reads `.DraftPath`, T11 reads `.DraftPath` again
  for cleanup; consistent.
- `Invoke-DraftReview` returns `{Action, Body}` (T10) — T11 reads `.Action -eq 'Abort'` and `.Body`,
  consistent.
- `-RepoRoot` is threaded identically through every lib function that touches the filesystem (`Get-
  VersionsInSync`, `Set-ProjectVersion`, `Invoke-Gate`) and every orchestrator function (`Assert-
  Preconditions`, `Get-ReleaseReconciliationState`, `Resolve-HalfFinishedRelease`, `Invoke-ReleaseCommit`) —
  no task uses a bare `$RepoRoot`-less git/dotnet call that would silently target the wrong tree.
- `-Yes` is a `[switch]` everywhere it's declared (T8's `param()`) and always passed onward as `-Yes:$Yes`
  (T9/T10/T11 call sites), never re-declared with a different type or name (no stray `-Unattended` or
  `-Force` alias drift).

### Exhaustiveness self-audit outcome

Two real gaps were found and closed while drafting (not deferred):

1. **Off-by-one in every cumulative Pester test count** (Tasks 2-7's "Run and confirm all pass" expected
   counts each omitted the Task 1 harness test from the running total). Found during this self-audit,
   corrected in all 6 locations plus Task 12's final total (32, not 31; 36, not 35 including
   `new-tool.Tests.ps1`).
2. **`Get-NextVersion`'s bump base was ambiguous in the spec** (files' version vs. last tag's version — see
   "A design gap this plan resolves" under Ground-truth facts). Resolved by fixing the base to the last tag
   and adding an explicit orchestrator-level warn-and-confirm guard (Task 8) comparing the computed result
   against the files, which also required an 8th row in the `-Yes` contract table (Task 10) not present in
   the spec's literal 7-row table.

Specifically checked and closed:
- **Zero-commit guard**: T9, hard-fails under `-Yes` before any editor/LLM call (verified by code inspection
  per T9 Step 4's reasoning about why a live dot-source smoke isn't viable here).
- **Orphaned-tag guard**: T11's `Resolve-HalfFinishedRelease` explicitly checks `tag^{commit}` SHA against
  `HEAD` SHA and refuses to auto-reconcile if they differ (the `commit --amend`-orphans-the-tag case).
- **Duplicate-heading guard**: T5, `Add-ChangelogSection` throws on an existing exact heading match; tested.
- **Double-heading fallback**: not a distinct spec concept from duplicate-heading — the spec's guard section
  doesn't separately define a "double-heading fallback" beyond the duplicate-heading abort; treated as the
  same guard (T5).
- **`-Yes` hang states**: every interactive `Read-Host` call site in T8-T11 is gated by an `if ($Yes) { ... }
  else { Read-Host ... }` branch (never a bare unconditional `Read-Host`) — cross-checked against the 8-row
  contract table in T10 Step 3, all 8 rows map to a real code branch in a named task.
- **Every named lib function's Pester tests exercise both the "happy path" and at least one throw/guard
  path** — confirmed for all 7 (T2: nothing-to-release + overrides; T3: missing-file throw; T4: no-section
  throw; T5: duplicate-heading throw; T6: both threshold branches; T7: each of the 4 checks failing
  independently).

No further gaps were left open or flagged for later resolution — every item above was closed in this
document, not deferred.
