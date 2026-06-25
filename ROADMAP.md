# FlaUI.Mcp Roadmap

Design spec: [`docs/superpowers/specs/2026-06-25-flaui-mcp-server-design.md`](docs/superpowers/specs/2026-06-25-flaui-mcp-server-design.md)

## v1 scope (in the spec)

General agent control of arbitrary Windows desktop apps via FlaUI/UIA3 + the
official MCP C# SDK. Hybrid perception (a11y tree + screenshot/coordinates),
explicit multi-window handles, stdio + HTTP transports, option-C ref engine.

**Resilience cornerstones:** split query/action STA dispatcher (blocking-`Invoke`
can't freeze the server), popup grafting into snapshots, per-snapshot ref
scoping, connection-lifecycle cleanup, DPI-aware coordinate contract.

**v1 tool surface** includes (beyond the basics): structured patterns
(`desktop_get_grid_cell`/`grid_select`, `desktop_get_text`/`set_caret`,
`desktop_window_transform`), virtualization (`desktop_scroll` container scroll),
perception helpers (`desktop_snapshot_diff`, `desktop_snapshot_stats`,
`desktop_snapshot_global`, `desktop_get_bounds`, always-on bounding rects),
`desktop_clipboard_get`/`set`, `desktop_wait_for_stable`, and
`suggestedRecovery` on every error envelope.

## v2 (deferred)

| Feature | Why deferred | Source |
| --- | --- | --- |
| **UIA event streaming** (`desktop_watch` — Window_Opened, StructureChanged, FocusChanged → MCP notifications over SSE) | Highest-value creative idea, but the biggest complexity/risk: UIA event callbacks arrive on COM threads and must interplay with the STA dispatcher; needs bidirectional agent prompting. Polling + `desktop_snapshot_diff` covers v1 needs. | Both reviews |
| **Built-in OCR fallback** (`Windows.Media.Ocr` augmenting `desktop_screenshot` with text bounding boxes) | On-box API, no external dep, genuinely useful for zero-UIA apps (games, canvas, Citrix). Opt-in, not core to first working server. v1.5 candidate. | Both reviews |
| **Window arrangement** (`desktop_arrange_windows` tile/cascade) | Cosmetic scope creep; `desktop_window_transform` + `desktop_snapshot_global` cover the real needs. | Review 1 |
| **Shell / system integration** (shell execute, notification area, taskbar pinning) | Scope creep beyond UI automation. Clipboard (the high-value piece) is already in v1. | Review 1 |
| **Raw window messaging** (`desktop_send_message` / SendMessage/PostMessage) | Brittle footgun; MSAA is already surfaced via `LegacyIAccessiblePattern` and the vision/coordinate path is the zero-UIA fallback. | Rejected (review 1.2) |
| **Elevated-app automation** (broker process at higher integrity to drive admin apps) | Requires an elevated helper + IPC; security-sensitive. v1 documents `ACCESS_DENIED_INTEGRITY` instead. | Original spec |
| **App allow/deny guardrails** | General control is the v1 goal; guardrails are a hardening pass. | Original spec |
| **Recording / codegen** of action sequences | Convenience layer, not core capability. | Original spec |
| **Full DPI × OS × integrity test matrix in CI** | CI runners are single-DPI/non-elevated; v1 keeps the DPI matrix as a documented **manual** gate. | Review 1.7 |
| **GDI intent overlay** (`--overlay`: draw a red rect on the target element ~500ms before each action) | Trust/debugging nicety, not core capability; cheap to add. **Strong v1.5 fast-follow.** | Review 3 |
| **OLE/COM file drop** (`desktop_drop_files` via synthesized `IDataObject`/`IDropTarget`) | Real need (upload files into apps) but complex/error-prone COM injection; reviewer rates YAGNI High. | Review 3 |
| **System-tray pseudo-window** (`desktop_open_tray` walking `Shell_TrayWnd`/`NotifyIconOverflowWindow`) | Useful for machine management (Wi-Fi/Docker tray) but a special-case surface beyond core app control. **Prioritized v2.** | Review 3 |
| **Code signing the distributed exe** (Authenticode / cert) | An unsigned self-extracting exe that synthesizes input is a strong AV/SmartScreen trigger; signing materially improves the install experience. v1 ships unsigned + checksum + "Run anyway" docs. **Top v2 distribution item.** | Distribution spec / agy review |

## Notes

- Items marked "Both reviews" were independently flagged by both external
  reviewers (Grok + the second architect pass), raising confidence they're real.
- The v1/v2 line is drawn to reach a **working, resilient server fast**, then add
  reactive/perception superpowers once the core is proven.
