# Changelog

All notable changes to this project are documented here. This project adheres to
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Security
- **Ref-resolution hardening (INV-8).** State-changing ref tools now resolve a ref **strictly** — they
  require the exact element by live UIA RuntimeId and refuse (`REF_STALE_UNRESOLVABLE`) rather than
  silently retarget a recycled/duplicate `AutomationId` (the destructive-action-on-the-wrong-control
  hazard under virtualization). Lenient reads accumulate across roots + ancestors, dedup by RuntimeId,
  and **fail closed** — `AMBIGUOUS_MATCH` on duplicates, `REF_STALE_UNRESOLVABLE` on lost identity —
  with no positional/whole-window fallback. Error messages carry `AutomationId` for triage but never
  `Name` (potential user content).
- **Break-glass switch** `FLAUI_MCP_REF_STRICT=off` forces lenient resolution globally (disables INV-8)
  for apps with too-volatile UIA identity; `FLAUI_MCP_REF_MAXSCOPES` tunes the ancestor fan-out cap
  (default 512). Both are read at process start.

## [0.7.2] - 2026-07-01
- Typed-text soft-advisory verification (`desktop_type` reads back via TextPattern; soft `verify` on mismatch).
