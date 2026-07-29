# Contributing to FlaUI.Mcp

Thanks for your interest! FlaUI.Mcp welcomes **code contributions — especially new MCP tools**.
This guide gets you from clone to a mergeable PR.

> **License & CLA.** FlaUI.Mcp is [PolyForm Noncommercial](LICENSE); the maintainer also sells commercial
> licenses. Contributions require signing the [CLA](CLA.md), granting commercial-relicensing rights. If you
> can't sign a CLA for a non-OSI project, that's an honest deal-breaker — no hard feelings.
>
> Automated CLA enforcement (`.github/workflows/cla.yml`) isn't live yet — the maintainer verifies sign-offs
> manually on each PR.

## Dev setup

- **.NET 10 SDK** (the projects target `net10.0-windows`).
- **Windows 10/11** with an interactive desktop (the UIA tests drive real windows).
- Build: `dotnet build -c Release` — this is the strict gate CI runs (no new warnings).
- Fast path: `pwsh -File DevelopersCockpit.ps1` — an interactive repo-root menu for build/test/scaffold/gate/release-preview/push/health-check.

## Running tests (the honest loop)

There are two tiers:

```powershell
# 1. Headless unit tests — THIS is what CI runs on your PR:
dotnet test -c Release --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"

# 2. Desktop/UIA tests — you MUST run these locally, on an UNLOCKED, connected session
#    (CI can't: GitHub-hosted runners have no interactive desktop):
dotnet test --filter "Category=Desktop&Category!=KnownDefect&FullyQualifiedName!~PopupGrafting"
dotnet test --filter "FullyQualifiedName~PopupGrafting"                     # synthetic input

# KnownDefect is excluded above because those tests fail BY DESIGN — each is a repro for a filed,
# unfixed defect in docs/fix-the-tool-backlog/. Run them deliberately when you want to check whether
# one still reproduces:
dotnet test --filter "Category=KnownDefect"
```

**Over RDP:** the two tiers differ, and conflating them is the easy mistake.

The **UIA-pattern** tests run fine over RDP as long as the session stays **connected and unlocked** —
that much really is a session-state requirement rather than an RDP limitation. Disconnecting or locking
is what breaks them: the active desktop switches to the secure `WinSta0\Winlogon` and calls fail
`InputDesktopUnavailable`, which is exactly the state an unattended CI runner sits in.

The **synthetic-input** tests need a **physical console**. A 2026-07-03 probe found that over live RDP
`OpenInputDesktop` succeeds and `SendInput` returns non-zero with `GetLastError`=0 — but that measures
the API *accepting* the events, not the events *landing*. Queued is not delivered: a real type or click
into a foreground window can silently no-op over RDP. So an RDP run of this tier does not fail loudly,
it produces **wrong results**, which is worse.

> This paragraph previously concluded from that same probe that "a physical console is not required".
> That does not follow from an API-level return value, and it contradicted the delivery finding recorded
> in the `driving-flaui-mcp` skill's RDP-vs-console rule. Corrected 2026-07-29.

Either tier additionally needs a granted lease (`flaui-mcp unlock`) for anything that fires input.

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
   Scaffolder tests require Pester 5.8.0 (pinned by CI and `DevelopersCockpit.ps1`). To run them:
   ```powershell
   Install-Module Pester -RequiredVersion 5.8.0 -Scope CurrentUser -Force
   Invoke-Pester -Path scripts/
   ```
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
