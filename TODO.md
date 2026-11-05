# FlaUI.Mcp Task Punch List (TODO)

This document tracks immediate, actionable tasks, polish items, and bite-sized contributor opportunities for **FlaUI.Mcp**.

For high-level project vision and completed phases, see [ROADMAP.md](ROADMAP.md).  
For long-term deferred test coverage gaps, see [docs/coverage-debt.md](docs/coverage-debt.md).  
For reproducible defect tests that intentionally fail by design, see [docs/fix-the-tool-backlog/](docs/fix-the-tool-backlog/).

---

## 🚀 Immediate / High Priority

- [ ] **Automated CLA Activation**: Enable `.github/workflows/cla.yml` triggers once personal access token (`CLA_PAT`) and legal review are provisioned.
- [ ] **CI Script Analyzer Warnings**: Run `PSScriptAnalyzer` in CI to surface warnings and stylistic diagnostics across PowerShell scripts.
- [ ] **Desktop Suite Automation**: Set up a recurring scheduled task on the physical test console (`just desktop`) to maintain recent `artifacts/desktop-runs/*.txt` reports.

---

## 🛠️ Tooling & Architecture Enhancements

- [ ] **Window Grafting Edge Cases**: Verify popup grafting behavior across complex multi-monitor virtual desktop arrangements.
- [ ] **OCR Pre-filtering**: Improve performance of opaque-window OCR text search by downsampling or caching unchanged window regions.
- [ ] **Redaction Rule Presets**: Provide out-of-the-box rule templates for popular enterprise applications (e.g., ERP, CRM, banking clients) in `docs/operator-manual.md`.

---

## 🧪 Testing & Coverage Debt (from `docs/coverage-debt.md`)

- [ ] **DEF-3 Selector Pin (A3 Debt)**: Add a focused fixture element verifying that redacted fields are withheld from selector name lookups without breaking layout invariants.
- [ ] **Render Full Content Verification (A5 Debt)**: Explore stable, environment-agnostic assertions for `PW_RENDERFULLCONTENT` (flag 2) non-client chrome capture.

---

## 📚 Documentation & Community

- [ ] **Agent Best Practices Guide**: Expand `docs/agent-contract.md` with recommended prompting strategies for agent retry/recovery when UI elements are temporarily unresponsive.
- [ ] **Troubleshooting RDP & Session Locks**: Add a quick-reference guide on configuring Windows autologon and display emulation for headless testing rigs.
