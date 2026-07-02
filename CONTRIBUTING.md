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
