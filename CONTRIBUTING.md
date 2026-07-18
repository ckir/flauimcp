# Contributing to FlaUI.Mcp

Thanks for your interest! FlaUI.Mcp welcomes **code contributions — especially new MCP tools**.
This guide gets you from clone to a mergeable PR.

> **License & CLA (read first).** FlaUI.Mcp is [PolyForm Noncommercial](LICENSE); the maintainer also
> sells commercial licenses. So contributions require signing the **CLA** ([CLA.md](CLA.md)), granting
> the maintainer the rights needed to relicense your contribution commercially. **Automated CLA
> enforcement isn't live yet** — the bot workflow (`.github/workflows/cla.yml`) exists but its PR
> triggers are disabled pending legal review and PAT provisioning, so the maintainer verifies your
> sign-off manually on each PR for now. If you can't sign a CLA for a non-OSI project, that's an honest
> deal-breaker — no hard feelings.

## Dev setup

- **.NET 10 SDK** (the projects target `net10.0-windows`).
- **Windows 10/11** with an interactive desktop (the UIA tests drive real windows).
- Build: `dotnet build -c Release` — this is the strict gate CI runs (no new warnings).
- Fast path: `pwsh -File DevelopersCockpit.ps1` — an interactive repo-root menu for build/test/scaffold/gate/release-preview/push/health-check.

## Running tests (the honest loop)

There are two tiers:

```powershell
# 1. Headless unit tests — THIS is what CI runs on your PR:
dotnet test -c Release --filter "Category!=Desktop&Category!=KnownDefect"

# 2. Desktop/UIA tests — you MUST run these locally, on an UNLOCKED, connected session
#    (CI can't: GitHub-hosted runners have no interactive desktop):
dotnet test --filter "Category=Desktop&FullyQualifiedName!~PopupGrafting"   # UIA pattern tests
dotnet test --filter "FullyQualifiedName~PopupGrafting"                     # synthetic input
```

**Over RDP:** both sets run over RDP **as long as the session stays connected and unlocked** — this is a
*session-state* requirement, not an RDP limitation. The UIA-pattern tests drive UIA; the synthetic-input
tests fire real `SendInput`, which injects fine into a connected, unlocked session (measured 2026-07-03:
`OpenInputDesktop` succeeds and `SendInput` returns non-zero with `GetLastError`=0 over live RDP — a
**physical console is not required**). The synthetic-input tests additionally need a granted lease
(`flaui-mcp unlock`). What breaks input is **disconnecting or locking** the session: the active desktop
switches to the secure `WinSta0\Winlogon` and calls fail (`InputDesktopUnavailable`) — which is exactly
the state an unattended CI runner is in. So run either set yourself in a connected, unlocked session; do
**not** assume a headless/physical-console box is required.

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
   (Scaffolder tests use Pester 5.8.0 — CI and `DevelopersCockpit.ps1` pin this version:
   `Install-Module Pester -RequiredVersion 5.8.0 -Scope CurrentUser -Force` if you don't have it,
   then run with `Invoke-Pester -Path scripts/`.)
2. **Fill the stub.** A tool is a method on a `[McpServerToolType] public sealed class XxxTools` in
   `src/FlaUI.Mcp.Server/Tools/`, annotated `[McpServerTool(ReadOnly = true | Destructive = true),
   Description("…")]`. The MCP SDK **auto-discovers** it — no registration to edit.
3. **Put real logic in Core.** Keep the Server method thin; UIA/state logic goes in
   `src/FlaUI.Mcp.Core/`, which is unit-testable. The Server method returns `Task<string>`;
   **state-changing tools must route through `ToolResponse.GuardWrite(_options, …)` and return
   `ToolResponse.Ok(…)`** — that call is what enforces `--read-only-mode` and the error envelope.
4. **Annotate correctly — this is a safety boundary.** `ReadOnly = true` for a pure read;
   `Destructive = true` for anything that changes state. A mis-annotation silently defeats
   `--read-only-mode`. Return the uniform error envelope `{ error, message, suggestedRecovery }` on
   failure. State-changing tools must respect `--read-only-mode`.
5. **Write the test.** UIA-backed behavior → an `IClassFixture<TestAppFixture>` test with
   `[Trait("Category","Desktop")]`; pure logic → a plain unit test in `test/FlaUI.Mcp.Tests/`.
6. **Update docs.** Add the tool to `docs/agent-contract.md` (the tool catalog) and an entry under `CHANGELOG.md [Unreleased]`.

## Opening a PR

- Keep PRs small and focused (one tool / one fix).
- Fill the PR template checklist honestly (CLA, tests, docs, annotation).
- The CLA bot isn't live yet — the maintainer checks your sign-off manually before merging.

## Reporting bugs / requesting tools

For now, open a GitHub Issue (structured templates are coming). Include OS build, the app you were
driving, the tool call, and what you expected vs. saw.
