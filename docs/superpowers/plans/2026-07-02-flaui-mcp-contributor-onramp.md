# Contributor On-Ramp Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a code-contributor on-ramp — a `CONTRIBUTING.md` + "add a tool" guide, an in-repo `scripts/new-tool.ps1` scaffolder, a PR template, a CLA Assistant Action gate, and a README pointer — so a newcomer can add a new MCP tool and open a mergeable PR.

**Architecture:** All artifacts are docs/config/PowerShell — no product-code (`src/`) changes. Only the scaffolder (`scripts/new-tool.ps1`) has real logic and gets TDD (Pester); the docs/config artifacts are author-then-verify. The scaffolder stamps files that match the repo's verified tool pattern: a `[McpServerTool(...)]` method on a `[McpServerToolType] public sealed class XxxTools` in `src/FlaUI.Mcp.Server/Tools/` (SDK auto-discovers), plus a matching xUnit test in `test/FlaUI.Mcp.Tests/` using `IClassFixture<TestAppFixture>`.

**Tech Stack:** .NET 10, PowerShell 7 (Pester for the scaffolder test), GitHub Actions, `contributor-assistant/github-action`.

**§8 resolutions (from the spec, verified against the repo):**
- **TestApp fixture** = `TestAppFixture` (`test/FlaUI.Mcp.Tests/TestAppFixture.cs`), consumed via `IClassFixture<TestAppFixture>`; Desktop methods carry `[Trait("Category","Desktop")]`.
- **CLA ledger storage** = a JSON file on a dedicated **`cla-signatures`** branch (the action's `branch` input), path `signatures/version1/cla.json` — keeps the signature ledger out of `master` history but in-repo/auditable.
- **Scaffolder CI guard** = **MVP, but the last and severable task** (Task 6) — drop it if it balloons; it becomes the first fast-follow.
- **CLA template source** = a standard Individual CLA (broad, sublicensable grant), provided verbatim in Task 4 and marked **legal-review-gated**.

---

## File Structure

- Create `scripts/new-tool.ps1` — the scaffolder (only file with logic).
- Create `scripts/new-tool.Tests.ps1` — Pester tests for the scaffolder.
- Create `CONTRIBUTING.md` — dev setup + add-a-tool guide + honest test loop + CLA note.
- Create `.github/pull_request_template.md` — review checklist.
- Create `CLA.md` — the agreement text (legal-review-gated).
- Create `.github/workflows/cla.yml` — the CLA Assistant Action gate.
- Modify `README.md` — add a "Contributing" section before `## License`.
- Modify `.github/workflows/ci.yml` — add the severable scaffolder-smoke job (Task 6).

---

## Task 1: `scripts/new-tool.ps1` scaffolder (TDD)

**Files:**
- Create: `scripts/new-tool.ps1`
- Test: `scripts/new-tool.Tests.ps1`

The scaffolder appends a tool-method stub to a Tools class and creates a matching test file. It is **idempotent-safe**: it refuses (non-zero, no writes) if the method already exists, and supports `-WhatIf` (dry run).

**Contract:**
`./scripts/new-tool.ps1 -Name <PascalName> [-ReadOnly] [-Class <ToolsClass>] [-WhatIf]`
- `-Name DesktopFoo` → method `DesktopFoo`, snake tool id `desktop_foo`.
- `-Class` default `InteractionTools`; target file `src/FlaUI.Mcp.Server/Tools/<Class>.cs` (must exist).
- Attribute = `[McpServerTool(ReadOnly = true), ...]` when `-ReadOnly`, else `[McpServerTool(Destructive = true), ...]`.
- Creates `test/FlaUI.Mcp.Tests/<Area>/<Name>Tests.cs` (default area `Interaction`).
- Exit non-zero if the method or test file already exists, or the target class file is missing.

- [ ] **Step 1: Write the failing test**

```powershell
# scripts/new-tool.Tests.ps1  (run with: Invoke-Pester scripts/new-tool.Tests.ps1)
BeforeAll {
    $script:Repo = Split-Path -Parent $PSScriptRoot
    $script:Script = Join-Path $PSScriptRoot 'new-tool.ps1'
    # Work against a temp copy of the two files the scaffolder touches so we never dirty the repo.
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
        $cls | Should -Match 'public\s+object\s+DesktopFoo'
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
        ([regex]::Matches((Get-Content (Join-Path $Sandbox 'src/FlaUI.Mcp.Server/Tools/InteractionTools.cs') -Raw), 'DesktopFoo').Count) |
            Should -BeLessOrEqual 2   # one attribute Description mention + one method sig, not doubled
    }
    It '-WhatIf writes nothing' {
        & $Script -Name 'DesktopBaz' -WhatIf -RepoRoot $Sandbox
        Test-Path (Join-Path $Sandbox 'test/FlaUI.Mcp.Tests/Interaction/DesktopBazTests.cs') | Should -BeFalse
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `pwsh -NoProfile -Command "Invoke-Pester scripts/new-tool.Tests.ps1"`
Expected: FAIL — `new-tool.ps1` does not exist (or `Invoke-Pester` reports the script path missing). If Pester isn't installed: `Install-Module Pester -Scope CurrentUser -Force` first (note this in CONTRIBUTING Task 2).

- [ ] **Step 3: Write the scaffolder**

```powershell
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
function Fail($m) { Write-Error $m; exit 1 }

$classPath = Join-Path $RepoRoot "src/FlaUI.Mcp.Server/Tools/$Class.cs"
$testPath  = Join-Path $RepoRoot "test/FlaUI.Mcp.Tests/$Area/${Name}Tests.cs"
if (-not (Test-Path $classPath)) { Fail "Tools class not found: $classPath" }
$src = Get-Content $classPath -Raw
if ($src -match "\b$Name\s*\(") { Fail "Method $Name already exists in $Class." }
if (Test-Path $testPath)        { Fail "Test file already exists: $testPath" }

$attr = if ($ReadOnly) { 'ReadOnly = true' } else { 'Destructive = true' }
$snake = ($Name -creplace '(?<!^)([A-Z])', '_$1').ToLower()   # DesktopFoo -> desktop_foo
$method = @"

    [McpServerTool($attr), Description("TODO: one-line description of $snake. Return a small JSON object; use the {error,message,suggestedRecovery} envelope on failure.")]
    public object $Name(/* TODO params, e.g. string window, string @ref */)
    {
        // TODO: thin Server method — put real UIA/logic in src/FlaUI.Mcp.Core and call it here.
        throw new System.NotImplementedException("$Name not implemented yet.");
    }
"@

# Insert the method before the final closing brace of the class file.
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
    public void $Name_does_the_thing()
    {
        // TODO: arrange via _fx, act on the new tool, assert the result.
        Assert.True(false, "TODO: implement $Name_does_the_thing");
    }
}
"@

if ($WhatIf) {
    Write-Host "[WhatIf] would append $Name to $Class and create $testPath"
    exit 0
}
Set-Content -Path $classPath -Value $newSrc -NoNewline
New-Item -ItemType Directory -Force -Path (Split-Path $testPath) | Out-Null
Set-Content -Path $testPath -Value $test -NoNewline
Write-Host "Created tool stub $Name ($snake) in $Class and test $testPath."
Write-Host "Next: fill the stub + Core logic, run headless tests, then Desktop tests on an unlocked session, then update README + CHANGELOG."
exit 0
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `pwsh -NoProfile -Command "Invoke-Pester scripts/new-tool.Tests.ps1"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add scripts/new-tool.ps1 scripts/new-tool.Tests.ps1
git commit -m "feat(contrib): scripts/new-tool.ps1 scaffolder + Pester tests"
```

---

## Task 2: `CONTRIBUTING.md`

**Files:** Create `CONTRIBUTING.md`

- [ ] **Step 1: Write the file**

```markdown
# Contributing to FlaUI.Mcp

Thanks for your interest! FlaUI.Mcp welcomes **code contributions — especially new MCP tools**.
This guide gets you from clone to a mergeable PR.

> **License & CLA (read first).** FlaUI.Mcp is [PolyForm Noncommercial](LICENSE); the maintainer also
> sells commercial licenses. So your **first PR triggers a one-click CLA** ([CLA.md](CLA.md)) granting
> the maintainer the rights needed to relicense your contribution commercially. If you can't sign a CLA
> for a non-OSI project, that's an honest deal-breaker — no hard feelings.

## Dev setup

- **.NET 10 SDK** (the projects target `net10.0-windows`).
- **Windows 10/11** with an interactive desktop (the UIA tests drive real windows).
- Build: `dotnet build -c Release` — this is the strict gate CI runs (no new warnings).

## Running tests (the honest loop)

There are two tiers:

```powershell
# 1. Headless unit tests — THIS is what CI runs on your PR:
dotnet test -c Release --filter "Category!=Desktop"

# 2. Desktop/UIA tests — you MUST run these locally, on an UNLOCKED, connected session
#    (CI can't: GitHub-hosted runners have no interactive desktop):
dotnet test --filter "Category=Desktop&FullyQualifiedName!~PopupGrafting"   # UIA pattern tests
dotnet test --filter "FullyQualifiedName~PopupGrafting"                     # synthetic input
```

CI runs **only the headless suite**. The maintainer does a final interactive verification of the
Desktop tests before merging — so your PR must state you ran them locally (or that they're N/A).

## Add a tool in ~20 minutes

FlaUI.Mcp tools follow one regular pattern:

1. **Scaffold it:**
   ```powershell
   ./scripts/new-tool.ps1 -Name DesktopFoo            # state-changing (Destructive)
   ./scripts/new-tool.ps1 -Name DesktopFoo -ReadOnly  # safe read
   ```
   This stamps a method stub into a `Tools` class and a matching test file. Add `-WhatIf` to preview.
   (Scaffolder tests use Pester: `Install-Module Pester -Scope CurrentUser -Force` if you don't have it.)
2. **Fill the stub.** A tool is a method on a `[McpServerToolType] public sealed class XxxTools` in
   `src/FlaUI.Mcp.Server/Tools/`, annotated `[McpServerTool(ReadOnly = true | Destructive = true),
   Description("…")]`. The MCP SDK **auto-discovers** it — no registration to edit.
3. **Put real logic in Core.** Keep the Server method thin; UIA/state logic goes in
   `src/FlaUI.Mcp.Core/`, which is unit-testable.
4. **Annotate correctly — this is a safety boundary.** `ReadOnly = true` for a pure read;
   `Destructive = true` for anything that changes state. A mis-annotation silently defeats
   `--read-only-mode`. Return the uniform error envelope `{ error, message, suggestedRecovery }` on
   failure. State-changing tools must respect `--read-only-mode`.
5. **Write the test.** UIA-backed behavior → an `IClassFixture<TestAppFixture>` test with
   `[Trait("Category","Desktop")]`; pure logic → a plain unit test in `test/FlaUI.Mcp.Tests/`.
6. **Update docs.** Add a row to the README tool table and an entry under `CHANGELOG.md [Unreleased]`.

## Opening a PR

- Keep PRs small and focused (one tool / one fix).
- Fill the PR template checklist honestly (CLA, tests, docs, annotation).
- Expect the CLA bot on your first PR.

## Reporting bugs / requesting tools

For now, open a GitHub Issue (structured templates are coming). Include OS build, the app you were
driving, the tool call, and what you expected vs. saw.
```

- [ ] **Step 2: Verify accuracy**

Run: `dotnet test -c Release --filter "Category!=Desktop"` and confirm the command in the doc matches CI (`.github/workflows/ci.yml` line 20). Eyeball that the Desktop-test commands match `ci.yml`'s commented commands verbatim.
Expected: commands identical; no fabricated paths.

- [ ] **Step 3: Commit**

```bash
git add CONTRIBUTING.md
git commit -m "docs(contrib): CONTRIBUTING.md — dev setup, honest test loop, add-a-tool guide"
```

---

## Task 3: `.github/pull_request_template.md`

**Files:** Create `.github/pull_request_template.md`

- [ ] **Step 1: Write the file**

```markdown
## What & why

<!-- One or two sentences: what does this change and why? -->

## Checklist

- [ ] I have signed the CLA (the bot will prompt on my first PR).
- [ ] Headless tests pass locally: `dotnet test --filter "Category!=Desktop"`.
- [ ] Desktop tests run locally on an unlocked session — **or N/A** (no UIA-backed behavior).
- [ ] Build is clean (`dotnet build -c Release`, no new warnings); ran `dotnet format` if an `.editorconfig` is present.
- [ ] **Safety annotation is correct** — safe reads are `[McpServerTool(ReadOnly = true)]`, state-changing tools are `Destructive = true` (a mis-annotation bypasses `--read-only-mode`).
- [ ] README tool table + `CHANGELOG.md [Unreleased]` updated (if this adds/changes a tool).
- [ ] Follows the tool pattern: auto-discovered, thin Server method, logic in Core, error envelope.
```

- [ ] **Step 2: Commit**

```bash
git add .github/pull_request_template.md
git commit -m "docs(contrib): PR template with CLA/test/annotation checklist"
```

---

## Task 4: CLA gate — `CLA.md` + `.github/workflows/cla.yml`

**Files:** Create `CLA.md`, create `.github/workflows/cla.yml`

> **LEGAL-REVIEW-GATED:** the `CLA.md` text below is a standard Individual CLA starting point, **not
> legal advice.** Do NOT enable `cla.yml` on the repo until the maintainer has had the wording
> reviewed and has provisioned the PAT (see Step 3). The workflow is committed **disabled-by-default**
> (manual `workflow_dispatch` + a guard) so merging this task does not silently start gating PRs.
>
> **UPDATE (2026-07-02):** `CLA.md` was drafted early at repo root and pre-reviewed by agy under an
> IP/contract-lawyer lens. The committed file has **9 clauses** (adds third-party-materials disclosure,
> a moral-rights waiver, Apache-style patent "alone or in combination with the Project" scope, a
> strengthened authority + Corporate-CLA note, and a governing-law/venue clause). **Step 1's 5-clause
> block below is SUPERSEDED — commit the existing repo-root `CLA.md`, do not regenerate from the block.**
> Two placeholders remain the maintainer's call before go-live: `[JURISDICTION]` and whether a separate
> Corporate CLA is required. Human legal sign-off still gates enabling `cla.yml`.

- [ ] **Step 1: Write `CLA.md`**

```markdown
# FlaUI.Mcp Individual Contributor License Agreement

Thank you for contributing to FlaUI.Mcp ("the Project"), maintained by Costas Kirgoussios
("the Maintainer"). By signing (commenting the agreement statement on your pull request), you agree:

1. **Grant.** You grant the Maintainer a perpetual, worldwide, non-exclusive, royalty-free,
   irrevocable, **sublicensable and transferable** license to reproduce, prepare derivative works of,
   publicly display, publicly perform, and distribute your Contributions and such derivative works,
   **including the right to relicense your Contributions under any license, including commercial
   (non-open-source) terms.**
2. **Patent grant.** You grant the Maintainer a perpetual, worldwide, non-exclusive, royalty-free,
   irrevocable patent license to make, use, sell, and otherwise transfer your Contributions, where
   such license applies to patent claims you can license that are necessarily infringed by your
   Contribution.
3. **Original work.** Each Contribution is your original creation and you have the right to grant this
   license. If your employer has rights to work you create, you have received permission to contribute,
   or your employer has waived such rights.
4. **No warranty.** Contributions are provided "as is," without warranty of any kind.
5. **You retain your copyright.** This is a license grant, not an assignment; you keep ownership of
   your Contributions.

"Contribution" means any work of authorship submitted by you to the Project.
```

- [ ] **Step 2: Write `.github/workflows/cla.yml` (disabled-by-default)**

```yaml
name: CLA Assistant
on:
  # Enabled by the maintainer AFTER legal review + PAT provisioning (see below).
  # To go live, replace `workflow_dispatch:` with the two triggers in the comment.
  workflow_dispatch:
  # issue_comment:
  #   types: [created]
  # pull_request_target:
  #   types: [opened, closed, synchronize]

permissions:
  actions: write
  contents: write
  pull-requests: write
  statuses: write

jobs:
  cla:
    runs-on: ubuntu-latest
    steps:
      - name: CLA Assistant
        if: (github.event.comment.body == 'I have read the CLA Document and I hereby sign the CLA') || github.event_name == 'pull_request_target'
        uses: contributor-assistant/github-action@v2.6.1
        env:
          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
          # PERSONAL_ACCESS_TOKEN: a fine-grained PAT with Contents:write on this repo — REQUIRED,
          # because fork-PR GITHUB_TOKEN cannot write the signature ledger branch.
          PERSONAL_ACCESS_TOKEN: ${{ secrets.CLA_PAT }}
        with:
          path-to-document: 'https://github.com/ckir/flauimcp/blob/master/CLA.md'
          path-to-signatures: 'signatures/version1/cla.json'
          branch: 'cla-signatures'
          allowlist: 'ckir,dependabot[bot]'
```

- [ ] **Step 3: Record the enable procedure (in the commit body / an ops note)**

The maintainer, before enabling: (1) has the `CLA.md` wording legally reviewed; (2) creates a
fine-grained PAT (Contents: read/write on `flauimcp`) and stores it as the `CLA_PAT` secret;
(3) creates the empty `cla-signatures` branch; (4) swaps `workflow_dispatch:` for the
`issue_comment` + `pull_request_target` triggers.

- [ ] **Step 4: Commit**

```bash
git add CLA.md .github/workflows/cla.yml
git commit -m "docs(contrib): CLA text + disabled-by-default CLA Assistant workflow (legal-review-gated)"
```

---

## Task 5: README "Contributing" pointer

**Files:** Modify `README.md` (insert before `## License`)

- [ ] **Step 1: Add the section**

Insert immediately before the `## License` heading:

```markdown
## Contributing

Contributions — **especially new MCP tools** — are welcome. The fast path: run
`./scripts/new-tool.ps1 -Name DesktopFoo` to scaffold a tool + test, fill the stub, and open a PR.
See **[CONTRIBUTING.md](CONTRIBUTING.md)** for setup, the (honest) test loop, and the tool pattern.

Heads-up: FlaUI.Mcp is noncommercially licensed and the maintainer sells commercial licenses, so your
first PR triggers a one-click **CLA** ([CLA.md](CLA.md)).

```

- [ ] **Step 2: Verify it renders**

Confirm the section sits between "Building from source" and "License", and the three links resolve
(`CONTRIBUTING.md`, `scripts/new-tool.ps1`, `CLA.md` all exist after Tasks 1–4).

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "docs(readme): add Contributing pointer (scaffolder + CONTRIBUTING + CLA)"
```

---

## Task 6 (SEVERABLE): scaffolder-smoke CI guard

Guards against `new-tool.ps1` rotting against the real code. **Severable** — if it fights the runner, drop it and file it as the first fast-follow.

**Files:** Modify `.github/workflows/ci.yml`

- [ ] **Step 1: Add a job to `ci.yml`**

```yaml
  scaffolder-smoke:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - name: Scaffold a throwaway tool and build
        shell: pwsh
        run: |
          ./scripts/new-tool.ps1 -Name DesktopCiSmoke
          dotnet build src/FlaUI.Mcp.Server -c Release
      - name: Assert it is discarded (never committed)
        shell: pwsh
        run: |
          git checkout -- .
          git clean -fd
```

- [ ] **Step 2: Verify locally**

Run: `pwsh -c "./scripts/new-tool.ps1 -Name DesktopCiSmoke; dotnet build src/FlaUI.Mcp.Server -c Release"` then `git checkout -- . ; git clean -fd`
Expected: build succeeds (the stub compiles — it throws `NotImplementedException` but compiles); working tree clean afterward.

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci(contrib): scaffolder-smoke guard (stamp -> build -> discard)"
```

---

## Self-Review

- **Spec coverage:** 3.1 CONTRIBUTING → Task 2; 3.2 add-a-tool guide → Task 2 (§ "Add a tool"); 3.3 scaffolder → Task 1; 3.4 PR template → Task 3; 3.5 CLA gate → Task 4; 3.6 README pointer → Task 5; §5 scaffolder-rot mitigation → Task 6; §5 CLA legal-review gate → Task 4 (disabled-by-default + note); §8 fixture/ledger/guard/template → resolved in header. All covered.
- **Placeholder scan:** the `TODO`s inside the *stamped* stub are intentional scaffold output, not plan placeholders. No plan-level TBDs.
- **Type/name consistency:** `TestAppFixture`, `[McpServerToolType]`/`[McpServerTool]`, `Category=Desktop` filter, `contributor-assistant/github-action`, and the `cla-signatures` branch / `signatures/version1/cla.json` path are used identically across tasks and match the verified repo + the spec.

## Verified-against-repo citations

- `ci.yml:20` = `dotnet test -c Release --filter "Category!=Desktop" --no-build`; Desktop commands live in `ci.yml` comments (lines ~26–27). ✅
- Tool pattern: `src/FlaUI.Mcp.Server/Tools/{WindowTools,InteractionTools,SnapshotTools,ClipboardTools}.cs` = `[McpServerToolType] public sealed class …Tools` with `[McpServerTool(ReadOnly=…|Destructive=…), Description(…)]`. ✅
- Fixture: `test/FlaUI.Mcp.Tests/TestAppFixture.cs`, consumed as `IClassFixture<TestAppFixture>`. ✅
- `scripts/` does not exist yet (Task 1 creates it). ✅
