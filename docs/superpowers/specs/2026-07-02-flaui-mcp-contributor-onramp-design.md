# FlaUI.Mcp — Contributor On-Ramp (design)

**Date:** 2026-07-02
**Status:** Design approved (user), pending AGY-AFTER + spec review gate
**Goal:** Give code contributors an easy, legally-clean way to add value to FlaUI.Mcp — specifically to add a new MCP tool — without the maintainer having to hand-hold each one.

## 1. Context & decision record

FlaUI.Mcp is a C#/.NET 10 MCP server (UIA3/FlaUI) that gives an AI agent control of the Windows
desktop. Public repo `github.com/ckir/flauimcp`, **solo maintainer**, licensed **PolyForm
Noncommercial 1.0.0** with commercial licensing sold separately. Contributor infrastructure today is
**zero** (`.github/` holds only CI/release workflows).

Two decisions were made during brainstorming and are **fixed inputs** to this design:

- **Posture = code-contributors-first** (user's choice, over the agy+controller recommendation of a
  field-research-first funnel). We are recruiting engine co-developers and accept the CLA friction and
  the reduced contributor pool the noncommercial license implies.
- **Strategic constraint, named and accepted:** PolyForm NC + a commercial-licensing business
  *structurally* limits the contributor pool (it repels OSI purists and corporate devs who can't sign
  a CLA for a non-OSI project). This design optimizes the on-ramp *within* that constraint; it does
  not try to remove it.

The four execution forks below were consulted with agy (agy + controller aligned on all four).

## 2. Execution decisions (forks)

| Fork | Decision | Why |
| --- | --- | --- |
| **A. Scaffolding mechanism** | In-repo **`scripts/new-tool.ps1`** | Versions *with* the code — a base-class refactor updates the scaffolder in the same commit. A `dotnet new` template lives separately and desyncs; a Roslyn generator is overkill. PowerShell is already the repo's shell. |
| **B. CLA vehicle** | **CLA Assistant GitHub Action** (`contributor-assistant/github-action`) with a broad-grant CLA | DCO is insufficient — it certifies origin but grants no relicensing rights, poisoning the commercial IP pedigree. The Action variant keeps the signature ledger self-hosted (no third-party app dependency). |
| **C. Desktop-test barrier** | **Document the honest local loop** | Contributors to a Windows desktop tool are on Windows with an interactive desktop. `CONTRIBUTING.md` states: run the full `dotnet test` (incl. Desktop) locally on an unlocked session before the PR; CI runs headless-only; maintainer does the final interactive verify. No blind code drops; no multi-month UIA-mock investment. |
| **D. MVP scope** | **Code-PR funnel only** | Defer issue templates / labels / Discussions (the field-research funnel de-prioritized by the posture choice). |

## 3. What ships (MVP)

Five artifacts, each with one clear purpose:

### 3.1 `CONTRIBUTING.md`
The single entry point for a code contributor. Sections:
- **Dev setup** — .NET 10 SDK; `dotnet build -c Release`; the two-tier test story.
- **The test story (honest, per Fork C):**
  - Headless unit tests (what CI runs): `dotnet test -c Release --filter "Category!=Desktop"`.
  - Desktop/UIA tests (contributor must run locally, unlocked interactive session) — the exact
    commands documented in `.github/workflows/ci.yml` today:
    `dotnet test --filter "Category=Desktop&FullyQualifiedName!~PopupGrafting"` (UIA patterns) and
    `dotnet test --filter "FullyQualifiedName~PopupGrafting"` (synthetic input).
  - Statement that **CI runs headless only** and the **maintainer does the final interactive verify**
    before merge — so a PR must show Desktop tests were run locally (or state N/A).
- **"Add a tool in ~20 minutes"** — the load-bearing guide (see 3.2).
- **CLA note** — first PR triggers the CLA bot; one-click sign; link to the CLA text.
- **Scope & etiquette** — small focused PRs; follow the existing tool pattern; update README tool
  table + CHANGELOG `[Unreleased]`.

### 3.2 The "add a tool" guide (inside `CONTRIBUTING.md`)
Leans on the repo's **regular pattern** (verified against current code):
- A tool is a method on a `[McpServerToolType] public sealed class XxxTools` in
  `src/FlaUI.Mcp.Server/Tools/`, annotated `[McpServerTool(ReadOnly=true | Destructive=true),
  Description("…")]`. The MCP C# SDK **auto-discovers** it from the assembly — no registration edit.
- Non-trivial logic lives in `src/FlaUI.Mcp.Core/`; the Server method stays thin.
- A matching xUnit test in `test/FlaUI.Mcp.Tests/` (`[Trait("Category","Desktop")]` for UIA-backed
  behavior, wired to the existing TestApp fixture; plain unit test otherwise).
- Read-only vs state-changing annotation rules, the uniform error envelope
  (`{ error, message, suggestedRecovery }`), and the `--read-only-mode` contract.
- Ends by pointing at `scripts/new-tool.ps1` to stamp the skeleton.

### 3.3 `scripts/new-tool.ps1`
Idempotent scaffolder. `./scripts/new-tool.ps1 -Name DesktopFoo [-ReadOnly] [-Class InteractionTools]`:
- Stamps a `[McpServerTool(...)]` method stub (with `Description`) into a Tools class (new or named
  existing), a matching Core helper stub when `-Class` implies one, and a test skeleton wired to the
  fixtures.
- **Errors cleanly** (non-zero, no partial writes) if the tool/method already exists, so a contributor
  can re-run safely.
- Emits a short "next steps" note (fill the stub, run headless + Desktop tests, update docs).
- Lives in-repo, plain PowerShell, no external module dependency.

### 3.4 `.github/pull_request_template.md`
A checklist that makes review mechanical:
- [ ] CLA signed (the bot will prompt on first PR)
- [ ] Headless tests pass locally (`dotnet test --filter "Category!=Desktop"`)
- [ ] Desktop tests run locally on an unlocked session (or **N/A — no UIA-backed behavior**)
- [ ] README tool table + `CHANGELOG.md [Unreleased]` updated
- [ ] Follows the tool pattern (auto-discovered, thin Server method, Core logic, error envelope)
- [ ] **Safety annotation correct** — safe reads are `[McpServerTool(ReadOnly=true)]`, state-changing
  tools are `Destructive=true`. A mis-annotation silently bypasses the `--read-only-mode` guard, so
  this is a **non-negotiable review gate**, not a nicety.
- [ ] Build is clean — no new compiler/analyzer warnings (`dotnet build -c Release` is the strict
  gate CI runs); ran `dotnet format` if the repo carries an `.editorconfig`.
- A one-line "what & why" summary field.

### 3.5 CLA gate (`.github/workflows/cla.yml` + `CLA.md`)
- `contributor-assistant/github-action` gates PRs; signature ledger stored in-repo (a signatures
  file/branch) so there's an auditable log for future IP due-diligence.
- **Ops gotcha (must provision):** fork PRs run with a restricted `GITHUB_TOKEN` that **cannot write**
  the signature ledger, so the bot fails on the very first external PR unless a fine-grained
  **Personal Access Token** (repo write scope) is provisioned as a secret and passed to the action.
  This is a setup prerequisite, not optional.
- `CLA.md` = the agreement text: contributor grants the maintainer a broad, irrevocable,
  **sublicensable** copyright license (enough to relicense/sell commercially), and asserts they have
  the right to grant it. **Started from a standard CLA template; flagged for the maintainer's own
  legal review before it goes live** — this design does not present boilerplate as settled law.

### 3.6 README "Contributing" pointer
A few lines near the end of README (before or after License) → `CONTRIBUTING.md`, naming the
add-a-tool path and the CLA up front so a would-be contributor isn't surprised.

## 4. Contributor journey (data flow)

```
README "Contributing" pointer
   → CONTRIBUTING.md (setup + add-a-tool guide + test story + CLA note)
      → scripts/new-tool.ps1 -Name DesktopFoo         (stamp skeleton)
         → fill tool stub + Core helper + test
            → dotnet test --filter "Category!=Desktop" (headless, local)
            → dotnet test  (Desktop, local, unlocked session)
               → open PR (template checklist)
                  → CLA bot gates first-time contributor
                  → CI runs headless suite
                  → maintainer reviews + interactive-verifies Desktop
                     → merge
```

## 5. Risks & mitigations

- **Scaffolder rot** (script drifts from the real code and stamps something that no longer compiles).
  Mitigation: a **cheap CI guard** — a job that runs `new-tool.ps1` for a throwaway tool, builds, and
  asserts it compiles, then discards. Catches desync at PR time on the scaffolder itself. *(MVP+ —
  include if it's a small step in the plan; otherwise the first fast-follow.)*
- **CLA text is a legal instrument.** Mitigation: ship a standard template as the *starting point*,
  explicitly flagged in `CLA.md` and the plan as **requiring the maintainer's legal review before the
  gate is enabled**. Do not enable the `cla.yml` gate on a PR until the maintainer signs off on the
  wording.
- **CLA friction turns away the rare good code PR.** Accepted per the posture decision; the one-click
  Action is the minimum viable friction.
- **Guide/pattern drift.** The add-a-tool guide references real class/attribute names; if the pattern
  changes, the guide and scaffolder must change together (same reason they live in-repo).

## 6. Testing (of the kit itself)

- `scripts/new-tool.ps1`: the CI guard above (stamp → build → discard) is the functional test;
  additionally a dry-run/`-WhatIf` path so a contributor can preview without writing.
- `CONTRIBUTING.md` commands are copy-runnable and match `ci.yml` verbatim (a doc-accuracy check at
  authoring time; no automation).
- PR template + `cla.yml`: validated by opening a scratch PR once the gate is legally cleared.

## 7. Deferred (explicit non-goals for this spec)

Issue templates (bug / app-compat / tool-request), good-first-issue labels, GitHub Discussions,
CODE_OF_CONDUCT, SECURITY.md, a UIA test-mock harness. These are the field-research funnel or
larger governance surface; they are fast-follows, not part of the code-PR MVP.

## 8. Open items for the plan

- Confirm the exact TestApp fixture class name(s) in `test/FlaUI.Mcp.Tests/` the scaffolder wires to.
- Decide the CLA signature-ledger storage shape (in-repo file vs a dedicated branch/gist) for
  `contributor-assistant/github-action`.
- Decide whether the scaffolder CI guard is MVP or first fast-follow.
- Source the concrete CLA template to start from (and record that legal review gates enabling it).
- Provision the fine-grained **PAT** (repo write) as a secret for `contributor-assistant/github-action`
  before enabling `cla.yml` — fork PRs can't write the ledger with the default token.
- Confirm whether the build/CI enforces code style (`.editorconfig` / `dotnet format
  --verify-no-changes` / warnings-as-errors); if so, add the exact command to the local loop so a
  newcomer's PR doesn't fail CI on formatting after clearing tests + CLA.
