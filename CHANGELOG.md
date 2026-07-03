# Changelog

All notable changes to this project are documented here. This project adheres to
[Semantic Versioning](https://semver.org/).

## [0.7.6] - 2026-07-03

### Fixed
- **RefRegistry no longer leaks a closed window's refs.** Over a long, exploration-heavy session that
  launches/closes many windows (or issues many additive `desktop_find`s), the per-window ref maps —
  each potentially pinning a cached UIA/COM element — accumulated for every window ever seen and were
  never reclaimed. Closing a window now evicts its ref state (refs + counter + snapshot seq),
  releasing the pinned COM elements to the GC. Two paths feed the single `Invalidate` chokepoint: a
  push signal (process exit / `desktop_close_window`) via a new `WindowInvalidated` event, and an
  on-access liveness sweep (Win32 `IsWindow`) at the entry of `desktop_snapshot`, `desktop_find`, and
  `desktop_list_windows` that catches windows closed by the user/app without a process exit. Purely
  internal memory reclamation — no wire-contract, tool, or error-code change; a ref used after its
  window is gone yields the existing `REF_NOT_FOUND` (→ take a fresh snapshot), now surfaced sooner.

## [0.7.5] - 2026-07-02

### Changed
- `desktop_type` verify: on a mismatch, the recovery remedy is now chosen by the target's
  ValuePattern write-capability. The result carries a new `canSetValue` fact and the
  `recommendedFallbackTool` points to `desktop_clipboard_set` (clipboard-paste path) when the
  element has no writable ValuePattern (e.g. an Electron `contenteditable`) instead of always
  advising `desktop_set_value` (which returns `PatternUnsupported` there). The `remedy` prose now
  lists both strategies. Additive/backward-compatible; `verify` still never throws.

## [0.7.4] - 2026-07-02

### Fixed
- **`desktop_find nameMatch=contains` no longer crashes.** A UIA element with a null `Name` (unnamed
  containers — Panes/Groups, present in nearly every window) made the `contains` post-filter call
  `string.Contains` on a null reference and throw, surfacing as an `INTERNAL` error — so `contains`
  matching failed on essentially every real window in v0.7.3 (`eq` was unaffected). The element name is
  now coalesced to empty at the source and the post-filter predicate is total. This also hardens the
  `desktop_find` result wire contract: `name` and `automationId` are always present as empty strings,
  never null. (Caught by live smoke-testing of v0.7.3; fenced by a null-Name invariant unit test across
  every name matcher.)

## [0.7.3] - 2026-07-02

### Added
- **`desktop_find`.** Query a window for element refs (by automationId / name eq|contains / controlType / enabledOnly, optional subtree `scope`) without walking the whole tree. Returns matches with bounds + isEnabled/hasFocus + `totalMatches`/`isTruncated`. Read-only; honors the perception deny-list and password redaction (INV-5 — password fields are not findable by name). Refs are additive (a find does not supersede snapshot refs).
- **Scoped `desktop_snapshot_diff`.** Gains `scope=<ref>` to diff only a subtree (re-roots the current walk; slices the cached baseline in-memory).

### Security
- **`--read-only-mode` now blocks window tools (INV).** `desktop_launch_app`, `desktop_focus_window`, and
  `desktop_close_window` were state-changing but not gated by `--read-only-mode` (they used the non-guarding
  path); they now short-circuit to `WriteBlockedReadOnly` like every other mutating tool, and are marked
  `destructive` in their MCP annotations.
- **Password-name redaction in diff (INV-5).** `desktop_snapshot_diff` now renders an IsPassword element's
  `Name` as `[REDACTED]` in added/removed/changed output, matching `desktop_snapshot` — a scoped or
  whole-tree diff can no longer surface a password element's name (name-oracle). Identity matching keeps the
  raw name internally (never serialized), so a password node still matches itself across baseline/current.
- **Fail-closed password detection at snapshot build (INV-5).** The `IsPassword` bit is now read via the
  fail-closed helper (redact on a COM read failure) instead of defaulting to non-password — so a flaky
  `IsPassword` read can no longer leak a password element's raw `Name` through `desktop_snapshot` or
  `desktop_snapshot_diff`.
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

