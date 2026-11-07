# FlaUI.Mcp Task Punch List (TODO)

This document tracks immediate, actionable tasks, polish items, and bite-sized contributor opportunities for **FlaUI.Mcp**.

For high-level project vision and completed phases, see [ROADMAP.md](ROADMAP.md).  
For long-term deferred test coverage gaps, see [docs/coverage-debt.md](docs/coverage-debt.md).  
For reproducible defect tests that intentionally fail by design, see [docs/fix-the-tool-backlog/](docs/fix-the-tool-backlog/).

`R<n>` refers to row *n* of the hardening table in ROADMAP.md; `B<n>` to Track B.

---

## ▶ Sequenced work (decided 2026-11-05)

Take these in order. Gate trust comes first because B2 is verified on the Desktop suite that R16 makes flaky.

1. [x] **R16 — popup-test ARRANGE flake** *(fixed `016cac5`; console Desktop gate 164/164)*: the root cause was
   foreground ownership, not a stacked sibling TestApp. The first fix (`7169ab4`) was wrong. See ROADMAP item 16.
2. [x] **ROADMAP item-8 citation gap** *(not a gap)*: items 17–19 exist as `### N.` sections under Notes; the
   table now says so, and row 8's citation points there.
3. [x] **Agent Best Practices Guide**: `docs/agent-contract.md` now has a **Retry and recovery** section (per error
   code, plus rules), checked against the code.
4. [x] **B2 — `desktop_open_tray`** *(shipped in v1.1.0)*: system tray as a driveable surface (three tools).
5. [x] **B4 — `desktop_list_terminal_processes`** *(closed by ruling 2026-11-06, YAGNI)*: the measured facts and the
   reopen condition are in ROADMAP.md (B4).
6. [x] **CI Script Analyzer Warnings**: every tracked `.ps1` is gated at zero PSScriptAnalyzer diagnostics (Warning
   and Error) under `PSScriptAnalyzerSettings.psd1`, with the analyzer version pinned in CI
   (`scripts/psscriptanalyzer.Tests.ps1`, `just lint-scripts`).

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

- [ ] **Structured logging in the flaui-mcp server**: today `Program.cs` only wires the default console logger to
  stderr, and just a few files write log output. Add structured, leveled logging (tool name, window handle,
  outcome, duration) to a file sink that an operator can read after the fact, so a failure in a live session
  can be diagnosed from the log rather than reconstructed. Keep stdout clean: it carries the MCP protocol.

- [ ] **OCR Pre-filtering**: Improve performance of opaque-window OCR text search by downsampling or caching unchanged window regions. Measure first.
- [ ] **Redaction Rule Presets**: Provide out-of-the-box rule templates for popular enterprise applications (e.g., ERP, CRM, banking clients) in `docs/operator-manual.md`.
- [ ] **Troubleshooting RDP & Session Locks**: Add a quick-reference guide on configuring Windows autologon and display emulation for headless testing rigs.
