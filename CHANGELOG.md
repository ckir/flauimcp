# Changelog

All notable changes to this project are documented here. This project adheres to
[Semantic Versioning](https://semver.org/).

## [0.11.0] - 2026-07-04

### Added
- **`flaui-mcp overlay on|off`** — a one-command toggle that enables/disables the intent overlay by
  re-registering the `flaui-mcp` server with (or without) `--overlay --overlay-ms=800` across every detected
  client config (`claude mcp` for Claude Code; the JSON writers for agy/generic). Off by default.
- **Overlay discoverability** — the post-install message and the README now point at `flaui-mcp overlay on`
  instead of leaving users to hand-edit config args.
- **Structured `flaui-mcp --help` / `-h`** — replaces the terse one-line usage with multi-line help: every verb
  with a description, common options, and examples. A no-argument invocation shows it too.
- **Opt-in `desktop_list_windows(includeHandles:true)`** returns a reusable `wN` handle inline on each window,
  so you can snapshot / find / interact directly without a separate `desktop_open_window` round-trip. Handles
  are minted lazily (the list stays pure-Win32 and never blocks on an unresponsive window) and reused across
  polls; the UIA binding is deferred to first use and guarded by a HWND-recycle pid-reverify (on both the read
  and the write path) so a recycled handle fails `WindowHandleStale` rather than acting on a different process.

### Changed
- `flaui-mcp install` for Claude Code now re-registers idempotently (remove-then-add), so re-running install or
  toggling the overlay never fails on a duplicate server name.

## [0.10.1] - 2026-07-04

### Added
- **Element-identity audit trace for synthetic input** — when a mutative action resolves a `selector`
  targeting, the input audit line now names the resolved element's stable identity: an allow-listed set
  of `RuntimeId`, `AutomationId`, `ClassName`, `ControlType`, and bounds ONLY (never `Name`, `Value`,
  `HelpText`, or any content-bearing property). The trace is strictly omitted when no element resolves
  (selector targeting failed), so pre-0.10.1 log parsers still read the unchanged window-level audit fields.
- **Intent overlay (`--overlay` / `--overlay-ms=N`)** — opt-in visual feedback. When enabled (per-tool via
  parameter or globally via CLI flag; default off), draws a red rectangle on the target element
  (or a crosshair at a coordinate pair) for ~500 ms (configurable; `0` disables) BEFORE each mutative
  action, so a human watching the screen sees what the agent is about to touch. It is a *visibility aid
  for debugging, not an authorization gate*; the lease/deny-list/read-only-mode safety foundation is
  unchanged.

## [0.10.0] - 2026-07-04

### Added
- **First-class `selector` targeting on 17 interaction/input/content tools** — alongside the
  existing `@ref` param, an optional `selector` (`{automationId?, name?, nameMatch?, controlType?,
  scope?, ignoreCase?}`) lets you target a control by stable identity, resolved atomically at
  action time — eliminating the `eN` re-snapshot churn (every `desktop_snapshot` renumbers refs, so
  a held `eN` can die `RefNotFound`). **Exactly one of `{ref | selector}`** is required on
  `desktop_invoke`, `desktop_set_focus`, `desktop_set_value`, `desktop_toggle`, `desktop_expand`,
  `desktop_select`, `desktop_scroll`, `desktop_scroll_into_view`, `desktop_set_caret`,
  `desktop_select_text_range`, `desktop_type`, `desktop_paste_text`, `desktop_click`,
  `desktop_get_text`, `desktop_get_grid_cell`, and `desktop_grid_select`; `desktop_key` takes **at
  most one** (omitting both targets the current foreground window). Coordinate-only
  (`desktop_click_at`/`desktop_drag`) and window-level (`desktop_window_transform`) tools were not
  changed. Resolution requires **exactly one live match**: 0 → `SelectorNoMatch`, >1 →
  `AmbiguousMatch` (refine with a more specific `controlType`/`automationId`, a `scope`, or
  `ignoreCase:false`) — never a silent pick. A successful selector call returns
  `resolvedElement:"eN"`, a freshly minted ref for the resolved element — like any ref, it dies on
  the next re-walk/snapshot, so reuse it only for an immediate follow-up. An over-broad selector
  (e.g. bare `controlType` on a huge tree) fails `InvalidArguments` ("selector too broad") rather
  than walking unbounded. **Honest limitation:** the payoff scales with `automationId` coverage — a
  control with no `automationId` and a non-unique `name` still degrades to `AmbiguousMatch`, same as
  duplicate-name `desktop_find`; fall back to a `desktop_snapshot` ref for those.
- **`desktop_find` gains `ignoreCase`** (default `false`, back-compat) — case-insensitive `name`
  matching (both `eq` and `contains`), useful to preview how a `selector` (which defaults
  `ignoreCase:true`) will match before committing to it.

### Deferred
- Element-identity audit trace (recording a resolved selector target's RuntimeId/AutomationId/
  bounds in the input audit log, redaction-safe) was scoped but deferred to a 0.10.1 fast-follow —
  it's a wire-shape change to the existing window-level audit log, not required for the selector
  feature to be fully functional.

## [0.9.0] - 2026-07-03

### Added
- **`desktop_wake_accessibility` / `desktop_release_accessibility` / `desktop_list_wakes`** — vision
  prong A: activate and **HOLD** an opaque Chromium/Electron window's native UIA tree so
  `desktop_snapshot` / `desktop_find` / interaction tools can see and target its contents with full
  precision instead of falling back to coordinates. `desktop_wake_accessibility(window)` returns
  `{wakeId, window, alreadyAwake}` — idempotent per window (re-waking an already-awake window returns
  the existing `wakeId`), and auto-releases when the window closes. `desktop_release_accessibility
  (wakeId)` returns `{ok, wakeId}` and is idempotent (an unknown/already-released `wakeId` still
  returns `ok:true`); NOTE Chromium re-collapses the tree **lazily** once idle, not necessarily
  immediately (spike-confirmed). `desktop_list_wakes()` returns `{wakes:[{wakeId, window}]}` to
  recover active wakes after a context loss. All three are `ReadOnly` and lease-exempt. Capped at 32
  wakes/session (`TooManyWatches`). `desktop_snapshot` also now surfaces a `wakeable:true` hint when
  it detects an opaque Chromium/Electron window (Chromium Win32 class with a collapsed tree) that
  would benefit from waking.
- **`desktop_find_text` / `desktop_wait_for_text`** — vision prong B: on-box OCR
  (`Windows.Media.Ocr`, no external dependency) text targeting for opaque/canvas/game surfaces or an
  editor's text body where UIA can't see the text. `desktop_find_text(query, window, region?,
  matchMode?, all?)` returns `{matches:[{text, confidence, bounds:[x,y,w,h] (physical screen px),
  center:[x,y] (screen px), xPct, yPct (window fractions for `desktop_click_at`)}]}`, best match
  first; fuzzy match by default (OCR mis-reads UI text), `region` optionally scopes the capture to a
  window-relative fraction rectangle `[xPct,yPct,wPct,hPct]`. `desktop_wait_for_text(query, window,
  region?, timeoutMs?)` polls (throttled to ≥750ms between OCR passes) and returns `{satisfied:false}`
  on timeout (data, not an error) or `{satisfied:true, match:{...}}` on success. Both are `ReadOnly`
  and lease-exempt; `OcrUnavailable` if no Windows OCR language pack is installed. **OCR here is
  targeting, not reading** — it resolves visible text to click coordinates; the model already reads
  the screenshot. A fuzzy query can match inside body text, so verify each match's text/bounds before
  `desktop_click_at`. An editor's document text body may remain behind a screen-reader gate even
  after `desktop_wake_accessibility` — `desktop_find_text` is the fallback for that residual case.

### Notes
- **Raised minimum supported Windows to build 19041 (Windows 10, version 2004, May 2020).** The
  built-in WinRT OCR projection (`Windows.Media.Ocr`) behind `desktop_find_text`/
  `desktop_wait_for_text` moved the TFM to `net10.0-windows10.0.19041.0`; older Windows 10 builds are
  no longer supported.

## [0.8.0] - 2026-07-03

### Added
- **`desktop_watch` / `desktop_unwatch` / `desktop_list_watches` / `desktop_drain_events`** — push perception:
  subscribe to UIA events (`window_opened`, `window_closed`, `focus_changed`, `structure_changed`) and receive
  them as MCP server→client notifications (`notifications/flaui/desktop_event`) over the existing **stdio** pipe —
  no HTTP/SSE. Because some hosts (including Claude Code) do not surface unsolicited notifications to the model,
  each event is ALSO buffered server-side and retrievable via **`desktop_drain_events`** (push+drain: use drain
  in hosts that don't surface push). All four tools are ReadOnly and lease-exempt. Events carry a freshly-minted
  (bounded, evictable) `ref`, `controlType`, INV-5-redacted `name`, `bounds`, and a `coalescedCount`.
  `structure_changed` is coalesced + debounced; focus/window events are process-filtered to the subscribed
  window. Subscriptions auto-evict when their window closes (reuses the Phase-6 `WindowInvalidated` chokepoint).
  Caps: 5 watches/window, 20/session. Event `ref`s are ephemeral (bounded pool) — re-`desktop_snapshot` for a
  durable ref if a drained/notified ref returns `REF_NOT_FOUND`.

## [0.7.7] - 2026-07-03

### Added
- **`desktop_paste_text`** — atomic clipboard-preserving paste for reactive editors (new Win11
  Notepad, Chromium `contenteditable`) that garble `desktop_type` keystrokes. Session-state safe:
  ALL input gates (lease / deny-list / budget / session-state) are checked BEFORE the clipboard is
  borrowed, so a paste that will be refused never clobbers the clipboard. The prior clipboard is
  restored only when the paste is confirmed to have landed (else `clipboardRestored:"abandoned"`,
  leaving your text on the clipboard — expected in reactive editors that transform pasted text, and
  whenever `verify=false`). A non-text clipboard (image/files) is refused with
  `ClipboardHoldsNonText` unless `forceOverwriteClipboard=true`; mixed text+rich clipboards restore
  as plain text (`clipboardRestored:"text-degraded"`).

### Changed
- `desktop_type` verify remedy now recommends `desktop_paste_text` (instead of the manual
  `desktop_clipboard_set` + `desktop_key` two-step) for no-writable-ValuePattern targets.

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

