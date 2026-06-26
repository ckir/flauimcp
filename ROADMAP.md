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

## Phase plan (execution sequencing)

The v1 surface ships in phases drawn by **blast radius**, not feature area — the
"act" leg of the lethal trifecta (synthetic input) is deliberately isolated last.
See [`docs/superpowers/specs/2026-06-25-flaui-mcp-server-design.md`](docs/superpowers/specs/2026-06-25-flaui-mcp-server-design.md)
for the full tool semantics and [`project-flaui-mcp-prompt-injection`] memory for the
safety rationale.

- **Phase 1 — Foundation** ✅ (v0.1.x): window/session management, split query/action
  STA dispatcher, option-C ref engine, 5 window tools.
- **Phase 2 — Perception** ✅ (v0.2.0): `desktop_snapshot` (a11y tree + popup grafting),
  perception-security floor (credential denylist, always-on `IsPassword` redaction,
  off-screen cull, never-elevated warn), ReadOnly annotations.
- **Phase 3 — Interaction (pattern-based) & perception completion** — the large,
  high-value, *low-blast-radius* phase. Element-targeted via UIA control patterns;
  **no synthetic mouse/keyboard.** After Phase 3 an agent can perceive everything and
  drive most apps.
  - *Pattern actions:* `desktop_invoke` (InvokePattern), `desktop_set_value`
    (ValuePattern), `desktop_toggle`, `desktop_expand`, `desktop_select`,
    `desktop_scroll_into_view`, `desktop_scroll`, `desktop_get_grid_cell` /
    `desktop_grid_select`, `desktop_get_text` / `desktop_set_caret` /
    `desktop_select_text_range`, `desktop_window_transform`.
  - *Perception completion (read-only):* `desktop_screenshot` (+`dpiScale`/bounds),
    `desktop_get_bounds`, `desktop_snapshot_stats`, `desktop_snapshot_global`,
    `desktop_snapshot_diff`.
  - *Clipboard / sync:* `desktop_clipboard_get` / `desktop_clipboard_set`,
    `desktop_wait_for`, `desktop_wait_for_stable`.
  - *Plumbing / hardening:* **cross-STA ref resolution** (re-resolve a ref on the
    **action** STA — never marshal an `AutomationElement` across apartments), fast-path
    RuntimeId-recycle guard (Name/AutomationId sanity check on the cache fast-path).
  - *Safety — load-bearing even here* (a pattern `Invoke` can still click "Delete" /
    "Send"): interaction tools carry `readOnlyHint:false, destructiveHint:true`;
    **human-in-the-loop confirmation on every state-changing action** (mostly the
    client's job — the server keeps tools granular, never bundling read + write, so
    it's possible). What pattern actions *cannot* do (mis-target, type into a shell) is
    exactly what Phase 4 adds.
- **Phase 4 — Synthetic input (the blast-radius phase)** — deliberately small and
  isolated. Real OS mouse/keyboard + the coordinate/vision *action* path, for apps
  whose controls implement no patterns or have broken a11y.
  - *Tools:* `desktop_type` (synthetic keystrokes, `Focus()`-first), `desktop_click`
    (synthetic mouse on a ref — modifiers / double / right), `desktop_click_at` /
    `desktop_drag` (coordinate path, screenshot-pixel contract + `xPct`/`yPct`),
    `desktop_key` (chords, e.g. `Ctrl+S`).
  - *Safety stack — only load-bearing once synthetic input exists:* **ACTION deny-list**
    — refuse synthetic input into UAC / `consent.exe`, credential dialogs, password
    managers, and interlock the worst sinks (terminal / `WindowsTerminal`, Win+R run
    dialog, browser address bar) behind a stronger confirm or refusal; **action budget
    + audit log** (rate-limit, re-confirm after N, log target+payload); **hard-fail on
    elevation behind `--unsafe-allow-elevation`** (upgrades the Phase-2 warn-only).
  - *Optional / v1.5:* `Windows.Media.Ocr`-assisted targeting + occlusion awareness for
    zero-UIA surfaces (also in the v2 table).

Not phased here (separate follow-on, not v1-blocking): HTTP/SSE transport with its hard
auth-token gate; RefRegistry eviction on window close.

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

## Perception — known limitations & hardening backlog (Phase 2 review)

Surfaced by the cycle-end adversarial review of Phase 2. None block v0.2.0; the perception
security floors are defense-in-depth, not an injection cure.

**Known limitations (documented, not bugs):**

- **Denylist is process-coarse.** The credential-store denylist matches by process name, so it
  misses *browser-embedded* password managers (`chrome://settings/passwords` is process `chrome`)
  and *UWP* apps whose window PID resolves to `ApplicationFrameHost.exe` rather than the real
  binary. Always-on `IsPassword` redaction is the field-level backstop; a full allowlist remains an
  opt-in hardened posture (deliberately not the default).
- **"Reveal password" defeats redaction.** When an app swaps a password field to plaintext (eye
  icon → `IsPassword` toggles false), the value is, by the app's own declaration, no longer a
  secret — nothing at our layer can re-mask it.
- **Electron/Chromium a11y is off by default.** Chromium exposes its full UIA tree only when a
  screen reader is detected or it is launched with `--force-renderer-accessibility`; otherwise a
  snapshot is one large `Document` node. *Future:* set the UIA "assistive tech present" flag.
- **Popup detection is class-name-based.** `FindOwnerPopups` recognizes Win32 (`#32768`), WPF
  (`HwndWrapper*`/`Popup`), and `Menu`; it misses WinForms/Qt/Electron overlay classes.
- **No occlusion awareness.** UIA reports `IsOffscreen` but not "visible-but-covered by another
  window." Relevant to action targeting (Phase 3) and the vision path (Phase 4).

**Hardening backlog:**

| Item | When | Notes |
| --- | --- | --- |
| **Cross-STA ref resolution** — re-resolve a ref on the *action* STA, never marshal an `AutomationElement` across apartments | Phase 3 | COM is thread-affine; `RunOnRefAsync` already resolves-then-acts in one STA lambda. Action tools must route resolution through the action dispatcher. |
| **Fast-path recycle guard** — add a `Name`/`AutomationId` sanity check to `RefRegistry.Resolve`'s cache fast-path | Phase 3 | Virtualized container recycling (e.g. `DataGrid` scroll) can reuse a `RuntimeId` for different data; harmless in Phase 2 (refs aren't consumed by actions yet). |
| **Hard-fail on elevation** behind `--unsafe-allow-elevation` | Phase 4 | v0.2.0 warns only; the blast radius that justifies refusing-to-run only arrives with synthetic input. |
| **Redact descriptor `Name` for `IsPassword`** | micro | Belt-and-suspenders; `Name` is empty for conformant password controls today, so no secret is stored. |
| **Window-prefixed refs in output** (`[w1:e1]`) | low | Mitigated already — the tools take window handle + ref as separate args, so refs can't alias across windows. |

## Notes

- Items marked "Both reviews" were independently flagged by both external
  reviewers (Grok + the second architect pass), raising confidence they're real.
- The v1/v2 line is drawn to reach a **working, resilient server fast**, then add
  reactive/perception superpowers once the core is proven.
