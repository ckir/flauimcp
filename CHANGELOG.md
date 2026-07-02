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

## [0.7.1] - 2026-07-01
- **Paced typing.** `desktop_type` keystrokes are paced by default (`interKeyDelayMs=15`) so slow/async
  consumers keep up; the foreground is re-verified before *each* key, so a mid-type focus-steal still
  aborts. `interKeyDelayMs=0` restores the single atomic blast.

## [0.7.0] - 2026-07-01
### Added
- **Synthetic input (Phase 4b).** Real `SendInput`-backed mouse/keyboard tools go live on the Phase 4a
  safety foundation: `desktop_type`, `desktop_key`, `desktop_click`, `desktop_click_at`, `desktop_drag`,
  `desktop_input_status` (read-only), and `desktop_set_caret` / `desktop_select_text_range` (UIA
  `TextPattern`, lease-exempt).
### Security
- Every `SendInput` tool requires a live out-of-band human lease (`flaui-mcp unlock`); it fires into a
  locked/RDP-disconnected desktop with `InputDesktopUnavailable` rather than dropping keystrokes.
  Elevation stays hard-refused unless `--unsafe-allow-elevation`. Ref targets re-verify the foreground
  and hit-test before acting, aborting on a focus-steal.

## [0.6.0] - 2026-06-30
### Added
- **Input safety foundation (Phase 4a)** — the guardrails shipped *before* any input tool. Three
  injectable seam interfaces (`ISyntheticInput`, `IPlatformEnvironment`, `ILeaseProvider`); the
  `InputGuard` pipeline (deny-list, per-window action budget, event-only audit log); the time-lease
  dead-man's switch (`flaui-mcp unlock --minutes N [--allow-shells]` / `flaui-mcp lock`); and an
  elevation hard-fail (`--unsafe-allow-elevation`, upgrading the earlier warn-only). No mouse/keyboard
  tool shipped in this release.

## [0.5.0] - 2026-06-30
### Added
- **Structured content & clipboard tools.** `desktop_get_grid_cell`, `desktop_get_text` (both
  read-only), `desktop_grid_select`, `desktop_clipboard_get` (read-only), and `desktop_clipboard_set`.
- The credential-store denylist and `IsPassword` `[REDACTED]` masking now cover grid cells and text
  reads. The clipboard layer cannot redact (documented exfil caveat).

## [0.4.0] - 2026-06-27
### Added
- **Perception-completion tools (read-only).** `desktop_screenshot`, `desktop_get_bounds`,
  `desktop_snapshot_stats`, `desktop_snapshot_diff`, `desktop_wait_for`, `desktop_wait_for_stable`,
  and `desktop_get_focused_element`.
### Security
- Password fields are painted over in captured pixels (not only in the tree); a full-desktop screenshot
  is refused when a credential-store window is visible; screenshots of a dead/locked session return
  `CaptureUnavailable` instead of a black image.

## [0.3.0] - 2026-06-26
### Added
- **Pattern-based interaction tools (Phase 3a).** `desktop_invoke`, `desktop_set_value`,
  `desktop_toggle`, `desktop_expand`, `desktop_select`, `desktop_scroll`, `desktop_scroll_into_view`,
  `desktop_set_focus`, and `desktop_window_transform`. These drive the app's own UIA control patterns
  (not synthetic input), so they work over RDP and never move the real cursor.

<!-- Releases before 0.3.0 (window management + the perception/snapshot foundation) predate this
changelog; see the tagged history and ROADMAP.md for that lineage. -->

