# FlaUI.Mcp Task Punch List (TODO)

This document tracks immediate, actionable tasks, polish items, and bite-sized contributor opportunities for **FlaUI.Mcp**.

For high-level project vision and completed phases, see [ROADMAP.md](ROADMAP.md).  
For long-term deferred test coverage gaps, see [docs/coverage-debt.md](docs/coverage-debt.md).  
For reproducible defect tests that intentionally fail by design, see [docs/fix-the-tool-backlog/](docs/fix-the-tool-backlog/).

`R<n>` refers to row *n* of the hardening table in ROADMAP.md; `B<n>` to Track B.

---

## ▶ Sequenced work (decided 2026-11-05)

Take these in order. Gate trust comes first because B2 is verified on the Desktop suite that R16 makes flaky.

1. [x] **R16 — `PopupRootCoverageTests` ARRANGE flake** *(fixed: shared `ContextMenuArrange` helper)*: the coordinate-based right-click is swallowed by a sibling
   `TestApp` window stacked at the same default position. Open the menu without depending on screen coordinates
   rather than widening the 2.5 s window. Confirm on the full Desktop suite — the test already passes in isolation.
2. [ ] **ROADMAP item-8 citation gap**: the item 8 row cites filed debt "items 17, 18, 19", but the table ends at 16.
   Add the rows or correct the citation.
3. [ ] **Agent Best Practices Guide**: expand `docs/agent-contract.md` with retry/recovery guidance for agents
   when UI elements are temporarily unresponsive.
4. [ ] **B2 — `desktop_open_tray`**: system-tray pseudo-window. Own spec → plan → implementation cycle.
   **v1.1.0 is cut once this lands** (no v1.0.2).
5. [ ] **B4 — `desktop_list_terminal_processes`**: identify Windows Terminal tabs by hosted process, without
   switching tabs.
6. [ ] **CI Script Analyzer Warnings** *(partly done)*: CI already fails on **Error**-level `PSScriptAnalyzer`
   diagnostics (`scripts/pester-pin.Tests.ps1`). Remaining: surface **Warning**-level diagnostics without failing.

---

## ⏸ Parked

### Blocked outside the repo

- [ ] **Automated CLA Activation**: Enable `.github/workflows/cla.yml` triggers once personal access token (`CLA_PAT`) and legal review are provisioned.
- [ ] **Desktop Suite Automation**: Set up a recurring scheduled task on the physical test console (`just desktop`) to maintain recent `artifacts/desktop-runs/*.txt` reports.
- [ ] **Window Grafting Edge Cases**: Verify popup grafting behavior across complex multi-monitor virtual desktop arrangements (needs the hardware).

### Hardening — standalone efforts, not opportunistic

- [ ] **R13 — bare `catch` blocks repo-wide**: can swallow `OutOfMemoryException`. A repo-wide refactor; take it
  only as its own reviewed effort (see AB-12 in `docs/coverage-debt.md`).
- [ ] **R14 — `CollisionMarker.Record()` cross-process lock**: lost update if two installers run at once. Never observed.
- [ ] **R4 / R10 / B1 / B3**: deferred by the operator — see ROADMAP.md.

### Testing & Coverage Debt (from `docs/coverage-debt.md`)

- [ ] **DEF-3 Selector Pin (A3 Debt)**: not a small test — the direct pin needs a visible fixture element literally
  named `[REDACTED]`, which risks the layout-coupled fixture guards. The production path is already in place.
- [ ] **Render Full Content Verification (A5 Debt)**: Explore stable, environment-agnostic assertions for `PW_RENDERFULLCONTENT` (flag 2) non-client chrome capture.

### Unscheduled

- [ ] **OCR Pre-filtering**: Improve performance of opaque-window OCR text search by downsampling or caching unchanged window regions. Measure first.
- [ ] **Redaction Rule Presets**: Provide out-of-the-box rule templates for popular enterprise applications (e.g., ERP, CRM, banking clients) in `docs/operator-manual.md`.
- [ ] **Troubleshooting RDP & Session Locks**: Add a quick-reference guide on configuring Windows autologon and display emulation for headless testing rigs.
