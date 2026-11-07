# AGY-TEST-AUDIT ledger

One row per test audit, recorded before the audit is declared complete. Newest row at the BOTTOM. Every `|`
inside a cell must be escaped as `\|`. Evidence must be independently checkable (fold commits or the brief);
`none` is not a permitted value.

| date | range | rounds | verdict | evidence |
|---|---|---|---|---|
| 2026-11-06 | `cee7d8d..724049c` (B2 system tray test suites: `test/FlaUI.Mcp.Tests/Tray/*`, `Windows/WindowLivenessTests.cs`) | 1 | GAPS FOUND — all dispositioned (3 FOLDED, 1 REJECTED) | gaps: deny-list refusal, read-only `GuardWrite`, lock timeout all FOLDED in `724049c` (`TrayManagerGuardTests`, each proven non-vacuous by a logic mutant that turned its own test red; deny-list needed the `RefuseIfDenied` source seam so capstone round 4 re-ran GREEN); visible-icon loop REJECTED (deleting it turns the lease-gated `Right_click_on_a_VISIBLE_icon_lists_the_menu` red, measured); brief `.clavity/seams/b2-tray-test-audit.md` |
| 2026-11-07 | `4a87cee..067b2ff` (PSSA Warning gate: `scripts/psscriptanalyzer.Tests.ps1`, the prompt It in `scripts/release.Tests.ps1`) | 1 | GAPS FOUND — all dispositioned (2 FOLDED) | gaps: `Severity = @('Warning')` and an `IncludeRules` whitelist each left all 8 gate tests green (measured); both FOLDED in `067b2ff` (settings key-and-severity pin; sentinel gains an Error-level probe). Mutants: Warning-only, Error-only, IncludeRules, excluded Error probe each redden the sentinel; `Rules = @{...}` reddens only the key pin. Suite 211 passed / 0 failed. Discarded below floor: SuppressMessage named-argument form, `$null = $Root` stubs. Cascade bc50a396; brief `.clavity/seams/pssa-gate-test-audit.md` |
