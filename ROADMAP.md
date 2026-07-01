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
  **no synthetic mouse/keyboard.** Split into two increments by the same blast-radius logic:
  - **Phase 3a** ✅ (v0.3.0): the 9 core pattern actions + `desktop_set_focus`, cross-STA ref
    resolution (per-action transient STA, never marshal an `AutomationElement` across
    apartments), and the `--read-only-mode` flag. After 3a an agent can perceive everything
    and drive most apps through UIA patterns.
  - **Phase 3b** — split by blast radius: **3b-1** (read-only perception completion) ✅ **shipped
    v0.4.0**; **3b-2** (state-mutating structured patterns + clipboard) ✅ **shipped v0.5.0**.
  - *Pattern actions:* **shipped in 3a** — `desktop_invoke` (InvokePattern), `desktop_set_value`
    (ValuePattern), `desktop_toggle`, `desktop_expand`, `desktop_select`,
    `desktop_scroll_into_view`, `desktop_scroll`, `desktop_set_focus`,
    `desktop_window_transform`. **Shipped in 3b-2** — `desktop_get_grid_cell` /
    `desktop_grid_select`, `desktop_get_text`. **Deferred to Phase 4** — `desktop_set_caret` /
    `desktop_select_text_range` (text caret/range mutation; inert without synthetic input).
  - *Perception completion (read-only):* ✅ **shipped 3b-1** — `desktop_screenshot`
    (native image + bounds/`dpiScale`/redactions), `desktop_get_bounds`, `desktop_snapshot_stats`,
    `desktop_snapshot_diff`, `desktop_wait_for`, `desktop_wait_for_stable`,
    `desktop_get_focused_element`; `desktop_list_windows` gained `includeBounds`/`zOrder`; always-on
    `focused` flag; `RefRegistry` recycle-guard `Name` compare. (`desktop_snapshot_global` was folded
    into `desktop_list_windows includeBounds` rather than shipped as a separate tool.)
    **3b-1 backlog (deferred):** occlusion-aware capture (PrintWindow, vs the current focus-first
    screen-scrape); full-desktop *per-field* redaction for non-denied windows (denylist whole-window
    refuse is the floor); snapshot/diff *value*-change detection (needs opt-in per-node value reads —
    omitted from the default walk for STA perf); `wait_for_stable` scope-by-ref; and the documented
    diff-identity limit on anonymous virtualized recycled rows (empty AutomationId+Name + recycled
    RuntimeId can collide — diff such content by value/text).
  - *Clipboard / sync:* ✅ **shipped 3b-2** — `desktop_clipboard_get` / `desktop_clipboard_set`.
  - *Plumbing / hardening:* **cross-STA ref resolution** (re-resolve a ref on the
    **action** STA — never marshal an `AutomationElement` across apartments), fast-path
    RuntimeId-recycle guard (Name/AutomationId sanity check on the cache fast-path).
  - *Safety — load-bearing even here* (a pattern `Invoke` can still click "Delete" /
    "Send"): interaction tools carry `readOnlyHint:false, destructiveHint:true`;
    **human-in-the-loop confirmation on every state-changing action** (mostly the
    client's job — the server keeps tools granular, never bundling read + write, so
    it's possible). What pattern actions *cannot* do (mis-target, type into a shell) is
    exactly what Phase 4 adds.
- **Phase 4 — Synthetic input (the blast-radius phase)** — split into 4a (safety stack)
  and 4b (real input tools), so the guards land before the blast radius does.
  - **Phase 4a** ✅ **(v0.6.0) — input safety foundation; no input tools yet.** Ships the
    3-seam set (`ISyntheticInput` / `IPlatformEnvironment` / `ILeaseProvider`), `InputGuard`
    pipeline (deny-list + per-window budget + event-only audit log), file-backed time-lease
    with `flaui-mcp unlock --minutes N [--allow-shells]` / `flaui-mcp lock` CLI (dead-man's
    switch; agent cannot self-grant), elevation hard-fail behind `--unsafe-allow-elevation`
    (upgrades the Phase-2 warn-only), and 2 carried 3b-2 SHOULD-FIX items. **No
    `SendInput`-backed tool ships in this phase.**
  - **Phase 4b** ✅ **(v0.7.0) — real Win32 SendInput tools, spike-validated.** Ships the real
    `Win32SyntheticInput` (`SendInput`) and `Win32PlatformEnvironment` (foreground / hit-test /
    fail-closed session oracle) leaves plus **eight tools**: `desktop_type` (Unicode keystrokes,
    `Focus()`-first, 4096-char cap), `desktop_key` (chords, e.g. `Ctrl+S`), `desktop_click`
    (synthetic mouse on a ref), `desktop_click_at` / `desktop_drag` (coordinate path,
    screenshot-pixel `xPct`/`yPct` contract + VIRTUALDESK 0–65535 normalization, both drag
    endpoints deny-listed), `desktop_input_status` (read-only lease pre-flight), and
    `desktop_set_caret` / `desktop_select_text_range` (UIA `TextPattern`, deny-list-only,
    lease/session/budget-exempt). `InputGuard` is now live in DI; F1–F5 merge-gate findings
    folded; atomic pre-send foreground/hit-test re-verify in the send leaf. Validated by an
    active-RDP spike (`SendInput` round-trip + abs-mouse normalization + ref-path `Focus()`
    targeting confirmed) — the headless box can't run `SendInput`, so Desktop input tests are
    console-only + manual.
  - **Phase 4b.1** ✅ **(v0.7.1) — reliable `desktop_type` on reactive editors.** Live dogfooding
    found the new Windows 11 Notepad (RichEdit + autocomplete) non-deterministically dropped/garbled
    fast multi-word `SendInput`, while classic Win32 edits were exact. Root cause: keystrokes were
    blasted in one `SendInput` with no pacing. Fix: `desktop_type` gains `interKeyDelayMs`
    (default 15) — a per-character send loop that re-verifies the foreground before *each* key
    (abort-on-focus-steal preserved; partial text on abort); `0` keeps the shipped single-batch
    behavior byte-for-byte. Deferred follow-on (A2): opt-in typed-text verification.
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
| **Hard-fail on elevation** behind `--unsafe-allow-elevation` | ✅ Phase 4a (v0.6.0) | v0.2.0 warned only; 4a upgrades to hard-fail — synthetic input is refused when running elevated unless `--unsafe-allow-elevation` is passed. |
| **Redact descriptor `Name` for `IsPassword`** | micro | Belt-and-suspenders; `Name` is empty for conformant password controls today, so no secret is stored. |
| **Window-prefixed refs in output** (`[w1:e1]`) | low | Mitigated already — the tools take window handle + ref as separate args, so refs can't alias across windows. |
| **Shrink the shipped exe** (Native AOT or trimming) | deferred | AOT is blocked by FlaUI's runtime COM interop + the MCP SDK's reflection tool discovery + STJ reflection serialization (would need source-gen JSON context + source-gen tool registration first); trimming risks silently stripping reflected tools/serializers. Low value for a once-installed dev tool — revisit only after a source-gen migration. (User to relay to agy.) |
| **"Driving FlaUI.Mcp" dogfood skill** — a skill teaching an agent to inspect desktop state via the installed server (`DesktopListWindows`/`DesktopSnapshot`) instead of `Get-Process` | end of roadmap | Deferred to the end of the roadmap (user, 2026-06-26); the tool surface is still growing each phase, so the skill is most useful written once the v1 surface stabilizes. |

## Notes

- Items marked "Both reviews" were independently flagged by both external
  reviewers (Grok + the second architect pass), raising confidence they're real.
- The v1/v2 line is drawn to reach a **working, resilient server fast**, then add
  reactive/perception superpowers once the core is proven.
