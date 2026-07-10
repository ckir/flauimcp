# FlaUI.Mcp Roadmap

Design spec: [`docs/superpowers/specs/2026-06-25-flaui-mcp-server-design.md`](docs/superpowers/specs/2026-06-25-flaui-mcp-server-design.md)

## Status: v1 feature-complete (2026-07-11, v0.13.0)

The full phased v1 plan has shipped — window management, hybrid perception (a11y tree +
screenshot/coordinates), pattern-based interaction, synthetic input behind the safety stack,
ref-resolution hardening, direct `desktop_find`, push event streaming, vision/opaque-app OCR + wake,
first-class `selector` targeting, inline window handles, session-hygiene healing, the human-attention
toolset, coarse presence, and background Windows-Terminal-tab reading. The tool surface an agent needs
to *perceive and drive an arbitrary Windows desktop app* is present and dogfooded.

The roadmap therefore pivots from **"phases toward v1"** to **two forward tracks plus a formal
drop-list.** Track A finishes the road to a stamped **v1.0**; Track B is curated post-v1.0 features.

## v1 scope (in the spec)

General agent control of arbitrary Windows desktop apps via FlaUI/UIA3 + the
official MCP C# SDK. Hybrid perception (a11y tree + screenshot/coordinates),
explicit multi-window handles, stdio + HTTP transports, option-C ref engine.

**Resilience cornerstones:** split query/action STA dispatcher (blocking-`Invoke`
can't freeze the server), popup grafting into snapshots, per-snapshot ref
scoping, connection-lifecycle cleanup, DPI-aware coordinate contract.

---

## Track A — v1.0 Release Candidate Path

**Completing Track A is what stamps the official v1.0.** These are productionization/trust items, not
features: they make the already-complete v1 surface *provably correct* and *trusted at install*. Ordered.

### A1 — Continuous interactive CI (Desktop/UIA + synthetic input) — **DO FIRST**

Today the green CI badge proves only the *headless* half; the `Category=Desktop` suite — UIA + real
`SendInput`, the product's entire reason to exist — is maintainer-run at manual smoke time. A regression
in real interaction behavior is not caught continuously. This is the single biggest correctness blind
spot, and it gates everything else: sign and ship only a tool that CI proves works.

- **Feasibility validated (2026-07-03).** `SendInput` works in any *connected, unlocked* session, so a
  local connected+leased run is already a legitimate pre-tag gate. The **sound** unattended approach:
  Sysinternals **Autologon → box boots into an unlocked physical console → run the CI agent as an
  interactive startup app** (never a Session-0 Windows service). `tscon /dest:console` is **not** sound
  (resolution collapse breaks bounds/visibility tests; WS2022/Win11 harden against `tscon` hijacking).
- **Prerequisite hygiene:** the real end-to-end `SendInput` test (`InputToolsTests`) must first be made
  reliably runnable (see the known-broken harness note in the Phase 7 spec §9). Also fold in the
  Desktop-only **e2e title-settle flake** in `TerminalTabE2ETests` (WT tab title settles async after
  launch) so the interactive suite is green before it becomes a gate.
- Complements the deferred "Full DPI × OS × integrity test matrix in CI" (kept in v2 — CI runners are
  single-DPI/non-elevated; the DPI matrix stays a documented manual gate).

### A2 — Code signing the distributed exe

An unsigned, self-extracting binary that **synthesizes input and configures agents** is a rough
first-touch trust barrier for a *security* tool — a strong AV/SmartScreen trigger. Authenticode signing
(cert) materially improves the install experience and unblocks locked-down environments. v1 ships
unsigned + checksum + "Run anyway" docs; signing is the last thing before the v1.0 stamp.

> **Ordering note (agy-consulted, user-decided 2026-07-11):** A1 before A2 — *guarantee the tool works
> (CI) before asking the OS to vouch for it (signing).* The exception that would flip it: a hard adoption
> wall where target users literally cannot run an unsigned exe. Not the case today.

---

## Track B — curated post-v1.0 features

Genuine feature work, sequenced after v1.0. Each gets its own spec → plan → implementation cycle.

### B1 — SP-C: legitimate foreground raise

The natural completion of the SP-A attention line: a *sanctioned* way to actually bring a window to the
foreground when that is the correct outcome, versus today's flash-and-wait handshake. Specced as the
SP-A follow-on ([`SP-A design`](docs/superpowers/specs/2026-07-05-flaui-mcp-human-attention-toolset-design.md));
backlog only, not yet planned.

### B2 — System-tray pseudo-window (`desktop_open_tray`)

Walk `Shell_TrayWnd` / `NotifyIconOverflowWindow` to expose the notification area as a driveable
surface. Useful for machine management (Wi-Fi, Docker, VPN trays). A special-case surface beyond core
app control — **prioritized** within Track B.

### B3 — OLE/COM file drop (`desktop_drop_files`) — **low**

Synthesize `IDataObject` / `IDropTarget` to drop files into apps (the real need: uploading files into a
UI). Complex, error-prone COM injection; rated **YAGNI-High**. Kept on the list but lowest priority.

---

## Opportunistic hardening (fold in when adjacent)

Not scheduled on their own — pick up when touching the surrounding code. None block anything.

- **Phase 3b-1 perception leftovers:** occlusion-aware capture (`PrintWindow`, vs the current
  focus-first screen-scrape); full-desktop *per-field* redaction for non-denied windows (denylist
  whole-window refuse is the floor); snapshot/diff *value*-change detection (needs opt-in per-node value
  reads — omitted from the default walk for STA perf); `desktop_wait_for_stable` scope-by-ref; the
  documented diff-identity limit on anonymous virtualized recycled rows (diff those by value/text).
- **Phase 7.1 clipboard:** delayed-render `WM_RENDERFORMAT` clipboard for a precise paste-consumption
  signal (vs the current best-effort restore-on-confirmed-consumption).
- **v0.13.0 micro follow-ups:** surface each `TabItem`'s `tabIndex` in `desktop_snapshot` (or have
  `desktop_read_terminal_tab` echo the `index→title` map) so ordinal selection isn't hand-counted
  (consumer-UX, from live smoke — design fork, agy-first before implementing); tool-level JSON-shape
  tests asserting `truncatedFrom` (`desktop_get_text`) and `Hint` (`desktop_list_windows`) surface
  through the anonymous projections.
- **Micro belt-and-suspenders:** redact descriptor `Name` for `IsPassword` controls (`Name` is empty
  for conformant password controls today, so no secret is stored — pure defense-in-depth).

---

## Formally dropped

Cut to keep scope honest. Dropping these clarifies **what this tool is not** — an automation *bridge*,
not a window manager, a security sandbox, or a multi-tenant remoting server.

| Dropped | Why |
| --- | --- |
| **Phase 10 #3** (third consumer-ergonomics change) | The Phase-10 spec itself calls it a likely-drop, "revisit after #2." #1 and #2 shipped; #3 never earned a plan. |
| **Native AOT / exe shrink** | Blocked by FlaUI's runtime COM interop + the MCP SDK's reflection tool discovery + STJ reflection serialization (would need source-gen JSON + source-gen tool registration first). Low value for a once-installed dev tool. |
| **Window arrangement** (`desktop_arrange_windows` tile/cascade) | Cosmetic scope creep; `desktop_window_transform` + `desktop_list_windows includeBounds` cover the real needs. Not a window manager. |
| **Shell / system integration** (shell execute, taskbar pinning) | Scope creep beyond UI automation. Clipboard — the high-value piece — already shipped. |
| **Raw window messaging** (`SendMessage`/`PostMessage`) | Brittle footgun; MSAA is surfaced via `LegacyIAccessiblePattern` and the vision/coordinate path is the zero-UIA fallback. |
| **Elevated-app automation** (higher-integrity broker + IPC) | Security-sensitive; needs an elevated helper. v1 documents `ACCESS_DENIED_INTEGRITY` instead. Not a privilege-escalation surface. |
| **App allow/deny guardrails** | General control is the goal; per-app guardrails are an opt-in hardening posture, not core. |
| **HTTP/SSE remote reachability** | A multi-connection server driving one physical mouse/keyboard is a focus-steal mirage (agy-first, 2026-07-03). Push event streaming — the one thing that seemed to need it — was decoupled onto stdio in Phase 8. Remains only about driving a remote/headless box; not pursued. |
| **Recording / codegen** of action sequences | Convenience layer, not core capability. |

Still genuinely deferred (not dropped): **Full DPI × OS × integrity test matrix in CI** (manual gate
today — CI runners are single-DPI/non-elevated).

---

## Shipped (v1) — provenance

Kept as the historical record; each line is one shipped increment. Full semantics live in the design
spec and the per-phase specs under `docs/superpowers/specs/`.

**Core surface (Phases 1–10):**

- **Phase 1 — Foundation** ✅ v0.1.x — window/session management, split query/action STA dispatcher,
  option-C ref engine, 5 window tools.
- **Phase 2 — Perception** ✅ v0.2.0 — `desktop_snapshot` (a11y tree + popup grafting), perception-security
  floor (credential denylist, always-on `IsPassword` redaction, off-screen cull, never-elevated warn).
- **Phase 3a — Pattern interaction** ✅ v0.3.0 — 9 core pattern actions + `desktop_set_focus`, cross-STA
  ref resolution, `--read-only-mode` flag.
- **Phase 3b-1 — Perception completion (read-only)** ✅ v0.4.0 — `desktop_screenshot`, `desktop_get_bounds`,
  `desktop_snapshot_stats`/`_diff`, `desktop_wait_for`/`_stable`, `desktop_get_focused_element`,
  `desktop_list_windows includeBounds`/`zOrder`.
- **Phase 3b-2 — Structured patterns + clipboard** ✅ v0.5.0 — `desktop_get_grid_cell`/`grid_select`,
  `desktop_get_text`, `desktop_clipboard_get`/`set`.
- **Phase 4a — Input safety foundation** ✅ v0.6.0 — 3-seam set, `InputGuard` (deny-list + per-window
  budget + audit), file-backed time-lease CLI (`unlock`/`lock`), elevation hard-fail. No input tools yet.
- **Phase 4b — Synthetic input** ✅ v0.7.0 — `desktop_type`/`key`/`click`/`click_at`/`drag`,
  `desktop_input_status`, `desktop_set_caret`/`select_text_range` (real Win32 `SendInput`, spike-validated).
- **Phase 4b.1–4b.3 — Typing robustness** ✅ v0.7.1 / v0.7.2 / v0.7.5 — inter-key pacing; typed-text
  `verify`; ValuePattern-aware verify remedy (`canSetValue` + `recommendedFallbackTool`).
- **Phase 5a — Ref hardening (INV-8)** ✅ v0.7.3a — strict RuntimeId-only writes, fail-closed lenient
  reads, break-glass `FLAUI_MCP_REF_STRICT=off`.
- **Phase 5b — `desktop_find` + scoped diff** ✅ v0.7.3 (+ v0.7.4 null-Name hotfix) — direct element query
  without a full walk; `desktop_snapshot_diff scope=<ref>`.
- **Phase 6 — RefRegistry eviction on close** ✅ v0.7.6 — `WindowInvalidated` push + on-access liveness sweep.
- **Phase 7 — `desktop_paste_text`** ✅ v0.7.7 — atomic clipboard-preserving Ctrl+V for reactive editors.
- **Phase 8 — `desktop_watch`** ✅ v0.8.0 — UIA event streaming over stdio MCP notifications (+ `desktop_drain_events`
  buffered fallback); no HTTP/SSE.
- **Phase 9 — Vision & opaque-app access** ✅ v0.9.0 — `desktop_wake_accessibility`/`release`/`list_wakes`
  (Chromium/Electron UIA wake) + `desktop_find_text`/`wait_for_text` (on-box OCR targeting).
- **Phase 10 — Consumer ergonomics** ✅ — first-class `selector` targeting (v0.10.0); audit trace + GDI
  intent overlay (v0.10.1); opt-in inline window handles `includeHandles` (v0.11.0). (#3 dropped, above.)
- **Consumer-lens capstone** ✅ v0.7.3 — read-only enforcement as a structural invariant
  (`ToolReadOnlyInvariantTests` asserts every `[McpServerTool]` declares exactly one of
  `ReadOnly`/`Destructive` and every `Destructive` tool short-circuits to `WriteBlockedReadOnly`).

**Session hygiene (SP1–SP4):** *no tool leaves the human's session worse than it found it.*

- **SP1 — Audit + invariant** ✅ — found exactly one live gap (`window_transform minimize` orphan) + one
  low-severity observability gap.
- **SP2 — Harden the gap** ✅ v0.11.2 — shared `RestoreForegroundAfterCollapse` for close + minimize;
  `desktop_focus_window` `foregroundGained` signal.
- **SP3 — Chaos harness** ⏸ shelved (YAGNI — its trigger, a *second* hygiene gap, never fired; design on record).
- **SP4 — Session Sentinel** 🚫 retired — a 100× spike observed zero async re-orphans; a lease-less healer
  isn't justified. Known blind spot on record: apps taking >500 ms to close can outrun SP2's spin-wait (SP4 spec §6).

**Human-attention + presence:**

- **SP-A — Foreground-lock legibility** ✅ v0.12.0 — enriched `targetNotForeground`, `desktop_wait_for_foreground`,
  attention flash + opt-in `autosound`, long-lease risk gate.
  ([spec](docs/superpowers/specs/2026-07-05-flaui-mcp-human-attention-toolset-design.md))
- **SP-B — User-state presence** ✅ v0.12.0 — read-only, opt-in `desktop_user_state` (coarse
  active/nearby/away; never raw idle-ms); `flaui-mcp presence on|off`.
  ([spec](docs/superpowers/specs/2026-07-05-flaui-mcp-user-state-presence-design.md))

**Background-tab terminal reading:**

- **WT tab reading** ✅ v0.13.0 — `desktop_read_terminal_tab` composite (select → settle → read sibling
  `Custom→Text` buffer → restore active tab; Destructive, read-only-gated); `desktop_get_text`
  `fromEnd`/`truncatedFrom`; `desktop_list_windows` multiplexer `Hint`; `driving-flaui-mcp` recipe rewrite.
  Incident-driven (a background agy CLI misdiagnosed as "headless").
  ([spec](docs/superpowers/specs/2026-07-10-windows-terminal-tab-reading-design.md))

**Tooling:** the `driving-flaui-mcp` dogfood skill ✅ — teaches an agent to inspect/drive the desktop via
the installed server, empirically grounded and extended each phase.

---

## Perception — known limitations (documented, not bugs)

Surfaced by the Phase 2 adversarial review; the perception security floors are defense-in-depth, not an
injection cure. These are stable, documented behaviors — reference, not backlog.

- **Denylist is process-coarse.** Matches by process name, so it misses *browser-embedded* password
  managers (`chrome://settings/passwords` is process `chrome`) and *UWP* apps whose PID resolves to
  `ApplicationFrameHost.exe`. Always-on `IsPassword` redaction is the field-level backstop; a full
  allowlist remains an opt-in hardened posture (deliberately not default).
- **"Reveal password" defeats redaction.** When an app swaps a password field to plaintext (`IsPassword`
  toggles false), the value is — by the app's own declaration — no longer a secret; nothing at our layer
  can re-mask it.
- **Electron/Chromium a11y is off by default.** Chromium exposes its full UIA tree only when a screen
  reader is detected or it's launched with `--force-renderer-accessibility`; otherwise a snapshot is one
  large `Document` node. `desktop_wake_accessibility` hydrates it on demand (Phase 9); typed text into
  Chromium editors garbles like the new Notepad — `desktop_paste_text` is the reliable path. WinUI 3 /
  WPF / Qt expose proper UIA and are unaffected.
- **Popup detection is class-name-based.** `FindOwnerPopups` recognizes Win32 (`#32768`), WPF
  (`HwndWrapper*`/`Popup`), and `Menu`; it misses WinForms/Qt/Electron overlay classes.
- **No occlusion awareness.** UIA reports `IsOffscreen` but not "visible-but-covered by another window"
  — see the Phase 3b-1 occlusion-aware-capture item in Opportunistic hardening.

## Notes

- Items originally marked "Both reviews" were independently flagged by both external reviewers
  (Grok + a second architect pass), raising confidence they were real.
- The v1 line was drawn to reach a **working, resilient server fast**, then add reactive/perception
  superpowers once the core was proven. With v1 feature-complete, the emphasis shifts to
  *provable correctness* (Track A) before new surface (Track B).
