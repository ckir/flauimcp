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

### ▶ TOP PRIORITY — Session Hygiene (SP1–SP4)

**Invariant:** *no tool leaves the human's interactive session (foreground window, keyboard focus,
input-teardown state) worse than it found it, except for its declared purpose.* Consumer-driven after
the v0.11.1 keyboard-orphan fix (closing the foreground window orphaned the physical keyboard) —
that bug is one instance of a broader class. Specs:
[`SP1 audit`](docs/superpowers/specs/2026-07-05-flaui-mcp-session-hygiene-design.md) ·
[`SP2`](docs/superpowers/specs/2026-07-05-flaui-mcp-session-hygiene-sp2-harden-design.md) ·
[`SP3`](docs/superpowers/specs/2026-07-05-flaui-mcp-session-hygiene-sp3-chaos-harness-design.md) ·
[`SP4`](docs/superpowers/specs/2026-07-05-flaui-mcp-session-hygiene-sp4-sentinel-design.md).

- **SP1 — Audit + invariant + session-delta contract** ✅ (audit complete): exactly ONE live gap —
  `desktop_window_transform minimize` orphans the foreground window (twin of the close bug) — plus one
  low-severity observability gap (`desktop_focus_window` can silently no-op under foreground-lock);
  modifier-teardown and clipboard surfaces are already sound.
- **SP2 — Harden the gap** ✅ (v0.11.2, shipped + console-smoked): generalized the v0.11.1 close healer
  into a shared `RestoreForegroundAfterCollapse(hwnd, hasCollapsed)` used by both close and minimize (no
  new class), fixed the minimize orphan, added the `desktop_focus_window` `foregroundGained` signal, and
  a Desktop test. All three validated live at a console.
- **SP3 — Session-delta snapshot + chaos test harness** ⏸ shelved (unbuilt): its YAGNI trigger — a
  *second* hygiene gap — never fired; the design stays on record. Desktop-only, can't be a headless-CI gate.
- **SP4 — Session Sentinel** (lease-less self-healing watchdog) ✅ **RETIRED (2026-07-05)**: a disposable
  spike drove the real close/minimize paths 100× and observed **zero** async re-orphans, so the residual
  the Sentinel targeted is unobserved and a lease-less healer isn't justified (YAGNI). One **known blind
  spot** on record as the reopen trigger — apps taking **>500 ms** to close can outrun SP2's spin-wait;
  see SP4 spec §6. Modifier-healing was already dropped as Win32-unsound. **Session-hygiene effort ends here.**

### Human-Attention Toolset (SP-A) — foreground-lock legibility

Consumer-driven follow-on to Session Hygiene: the foreground-lock (a background-process server can't
always bring a window forward) is a Windows fact of life, not a bug — but a *silent* abort left the
agent guessing. SP-A makes the lock **legible** and gives the agent an explicit attention handshake,
rather than trying to defeat the OS boundary. **Shipped (v0.12.0):**

- Enriched `targetNotForeground` result on `desktop_type`/`desktop_key` — replaces the generic
  `ElementDisappearedDuringAction` abort for the not-foreground cause with a structured, leak-safe
  `{ targetWindow, currentForeground:{handle,process}, recommendedAction, recovery }` plus a window
  flash; clicks are unaffected (a click is already a remedy, not a victim).
- `desktop_focus_window` additive `currentForeground`/`recommendedAction`/`recovery` when the lock
  blocks it (keeps `foregroundGained`).
- New tool `desktop_wait_for_foreground` — flash + block up to a server-capped 45s for the target to
  gain foreground; lease-exempt; designed to be re-invoked on `reason:"timeout"` rather than yielding
  the turn.
- Attention signals: flash always-on, plus opt-in `flaui-mcp autosound on|off` (leak-safe spoken cue,
  off by default).
- Non-destructive multi-flag config merge (`overlay`/`autosound` coexist for agy/generic; known gap on
  the `claude` target, whose CLI can't read back existing args).
- Long-lease (`--minutes N > 60`) disclaiming warning + `--accept-risk`/`'I understand'` gate.

Spec: [`SP-A design`](docs/superpowers/specs/2026-07-05-flaui-mcp-human-attention-toolset-design.md).

### User-State Presence (SP-B) — coarse, opt-in activity sensor

Landed on this branch (v0.12.0, folded into the same unreleased entry as SP-A): a coarse, opt-in,
agent-orchestrated activity axis so an agent can reason about whether a human is even at the
keyboard, without the server ever exposing raw idle time.

- New read-only, lease-exempt tool `desktop_user_state` — `{ enabled, activity:
  "active"|"nearby"|"away"|null }`. Off by default; never raw idle-ms.
- `flaui-mcp presence on|off [--nearby-secs N] [--away-secs N]` CLI verb — human-only, off by
  default, coexists with `overlay`/`autosound` via the same non-destructive config merge; both `on`
  and `off` apply immediately through a live state file.
- Agent-side derivation (combining this activity axis with SP-A's foreground signals into
  watching/working/nearby/away, and escalation policy) is explicitly **out of scope** here — it
  belongs to the agent layer (`/autogoal`). This server remains a dumb sensor with no outbound calls.

Spec: [`SP-B design`](docs/superpowers/specs/2026-07-05-flaui-mcp-user-state-presence-design.md).

**Follow-on (not yet built):** **SP-C — legitimate raise** (a sanctioned way to actually bring a
window to the foreground when that's the correct outcome, vs. today's flash-and-wait handshake).
Remains specced/backlog only.

### Background-tab terminal reading — **Shipped (v0.13.0)**

Incident-driven: a peer `agy` CLI running in a *non-active* Windows Terminal tab was misdiagnosed as
"headless" because there was no way to read a background tab's buffer. Design = Hybrid (a composite
read tool plus lightweight enabling primitives), agy-consulted twice.

- New composite tool `desktop_read_terminal_tab(window, tabIndex, restoreFocus, fromEnd, maxLength)` —
  selects the target tab, settles, reads the sibling `Custom→Text` buffer pane, and restores the
  originally-active tab (restore runs exactly once, never throws; `restored` degrades honestly).
  **Destructive** (tab select visibly mutates state) — blocked in `--read-only-mode`.
- `desktop_get_text` gains `fromEnd`/`truncatedFrom` — read the **tail** of a long buffer, surrogate-safe.
- `desktop_list_windows` surfaces a multiplexer `Hint` for Windows Terminal windows (pure Win32, no UIA/
  tab enumeration — the tool's non-blocking guarantee is preserved).
- Rewrote the `driving-flaui-mcp` skill's "reading another agent's TUI" recipe around the composite tool
  (`desktop_select` is the correct lease-exempt activation primitive, not a lease-gated click).

Spec: [`WT tab-reading design`](docs/superpowers/specs/2026-07-10-windows-terminal-tab-reading-design.md).

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
    targeting confirmed), and re-confirmed 2026-07-03 (`OpenInputDesktop` OK + `SendInput` non-zero /
    err=0 over live RDP). `SendInput` is a **session-state** dependency, not an RDP one: it works in a
    connected, unlocked session and fails only when disconnected/locked (`WinSta0\Winlogon`) — which is
    what an unattended CI runner is. So Desktop input tests are maintainer-run in a connected session,
    not "unrunnable over RDP".
  - **Phase 4b.1** ✅ **(v0.7.1) — inter-key pacing for `desktop_type`.** `desktop_type` gains
    `interKeyDelayMs` (default 15) — a per-character send loop that re-verifies the foreground before
    *each* key (abort-on-focus-steal preserved; partial text on abort); `0` keeps the shipped
    single-batch behavior byte-for-byte. Helps genuinely slow/async consumers keep up. **Live
    validation caveat:** this does NOT fix the new Windows 11 Notepad (RichEdit + autocomplete),
    which garbles synthetic input at *any* pacing (0ms and 15ms both fail; a classic Win32 edit is
    exact either way) — that editor needs a non-keystroke path, see 4b.2.
  - **Phase 4b.2** ✅ **(v0.7.2, delivered) — typed-text verification.** `desktop_type` optionally reads the element back (`verify`, default true) and returns a soft `verify` object; on a mismatch it names `desktop_set_value` (UIA ValuePattern) as the remedy for reactive/RichEdit editors (the new Notepad). No hard error, no auto-retry. NB: the earlier "reactive-editor non-keystroke path" framing was superseded — a live probe showed the new Notepad already exposes ValuePattern, so `desktop_set_value` was already the reliable path; v0.7.2 closes the *discoverability* gap, not a missing path.
  - **Phase 4b.3** ✅ **(v0.7.5) — ValuePattern-aware verify remedy.**
    When `desktop_type`'s verify detects a mismatch, the recovery remedy is chosen by the target's
    ValuePattern **write-capability** instead of hardcoding `desktop_set_value`. The result carries a new
    `canSetValue` fact (`Value.IsSupported && !IsReadOnly`; `null` when a UIA read throws) plus a
    best-effort `recommendedFallbackTool`: `desktop_set_value` when writable (and as the **safe default**
    on unknown — a wrong guess yields a recoverable `PatternUnsupported`), or the **clipboard-paste** path
    (`desktop_clipboard_set` → `desktop_key "Ctrl+V"`) when the target has no writable ValuePattern (e.g.
    an Electron `contenteditable`, which returns `PatternUnsupported` from `set_value`). The `remedy` prose
    lists both strategies. The capability is read inside the verify after-read's STA visit (no extra
    resolution, no hot-path tax). Additive / backward-compatible. Spec:
    [`docs/superpowers/specs/2026-07-02-flaui-mcp-phase4b3-valuepattern-remedy-design.md`](docs/superpowers/specs/2026-07-02-flaui-mcp-phase4b3-valuepattern-remedy-design.md);
    plan: [`docs/superpowers/plans/2026-07-02-flaui-mcp-phase4b3-valuepattern-remedy.md`](docs/superpowers/plans/2026-07-02-flaui-mcp-phase4b3-valuepattern-remedy.md).
    *Deferred follow-on:* `desktop_paste_text` (an atomic clipboard-preserving paste tool) — its own spec.
  - *Optional / v1.5:* `Windows.Media.Ocr`-assisted targeting + occlusion awareness for
    zero-UIA surfaces (also in the v2 table).
- **Phase 5 — Read-only targeting** — split by what shipped: durable-ref hardening first,
  direct element query second.
  - **Phase 5a** ✅ **(v0.7.3a) — ref-resolution hardening (INV-8).** Strict RuntimeId-only resolution for
    state-changing ref tools (`REF_STALE_UNRESOLVABLE` on a recycled `AutomationId`, no
    positional fallback); fail-closed lenient reads (`AMBIGUOUS_MATCH`/`REF_STALE`); break-glass
    `FLAUI_MCP_REF_STRICT=off`. Prerequisite for `desktop_find` below.
  - **Phase 5b** ✅ **(v0.7.3) — `desktop_find` + scoped `desktop_snapshot_diff`.** `desktop_find` queries a
    window for element refs (`automationId` / `name` `eq`\|`contains` / `controlType` /
    `enabledOnly`, optional subtree `scope`) without walking the whole tree — returns matches
    with bounds + isEnabled/hasFocus + `totalMatches`/`isTruncated`; read-only, honors the
    perception deny-list and password redaction (INV-5 — password fields are not findable by
    name); refs are additive (a find does not supersede snapshot refs). `desktop_snapshot_diff`
    gains `scope=<ref>` to diff only a subtree (re-roots the current walk; slices the cached
    baseline in-memory).
    - **v0.7.4 hotfix** ✅ — `desktop_find nameMatch=contains` threw a `NullReferenceException` on
      unnamed containers (UIA returns a null element `Name`), surfacing as an `INTERNAL` error on
      essentially every real window; caught by the v0.7.3 live smoke. Fixed by coalescing the null
      name at the read sites + making the match predicate total, with +4 null-Name invariant tests.

Not phased here (separate follow-on, not v1-blocking): HTTP/SSE transport with its hard
auth-token gate. **Note (2026-07-03):** the one capability that seemed to *need* HTTP/SSE — push event
streaming — was **decoupled** from it in Phase 8 (MCP JSON-RPC notifications ride the existing stdio pipe),
so HTTP/SSE is now only about *remote reachability* (driving a cloud/headless box). Deprioritized: a
multi-connection server driving one physical desktop is a focus-steal mirage (agy-first, 2026-07-03).
    - **Phase 6 — RefRegistry eviction on window close** ✅ **(shipped v0.7.6).** A closed window's
      ref state (and `WindowManager`'s per-window COM pin) is reclaimed via a `WindowInvalidated`
      push signal through the existing `Invalidate` chokepoint plus an on-access `IsWindow` liveness
      sweep at the snapshot/find/list entry points. `windowId` is a never-reused `w{n}`, so dropping
      all three registry dicts is alias-safe; no background thread, timer, or UIA event pump.
    - **Phase 7 — `desktop_paste_text`** ✅ **(shipped v0.7.7).** Atomic clipboard-preserving Ctrl+V
      paste for reactive editors; all input gates precede the clipboard borrow; restore only on
      confirmed consumption; non-text fail-fast with `forceOverwriteClipboard`; mixed text+rich →
      `text-degraded`. Deferred (Phase 7.1): delayed-render `WM_RENDERFORMAT` clipboard for a precise
      consumption signal.
    - **Phase 8 — `desktop_watch` (UIA event streaming over stdio)** ✅ **(shipped v0.8.0).**
      Push perception: subscribe to UIA events (`window_opened`/`window_closed`/`focus_changed`/
      `structure_changed`) and receive them as **MCP server→client notifications over the existing stdio
      pipe** — no HTTP/SSE (MCP is JSON-RPC; `ModelContextProtocol` 1.4.0 exposes `SendNotificationAsync`,
      verified). Shipped tools `desktop_watch`/`desktop_unwatch`/`desktop_list_watches`/`desktop_drain_events`
      (all `ReadOnly`, lease-exempt) — the fourth, `desktop_drain_events`, was added over the course of the
      phase once host-surfacing testing showed some hosts (including Claude Code) don't surface unsolicited
      notifications to the model, so every event is also buffered server-side as a reliable fallback
      (push+drain). Central design: UIA callbacks arrive on COM RPC threads → thin non-blocking capture →
      coalesce/debounce → payload-build marshaled onto the single query STA (INV-5 redaction, minted ref) →
      emit off-STA. Subscription lifecycle reuses the Phase-6 `WindowInvalidated` chokepoint for
      auto-evict-on-close. **Scope fork decided agy-first (2026-07-03)** — HTTP/SSE (below) was the original
      lean and was **discarded** for this capability (a multi-tenant server driving one physical
      mouse/keyboard is a focus-steal mirage; events don't need it). Spec:
      [`docs/superpowers/specs/2026-07-03-flaui-mcp-phase8-desktop-watch-design.md`](docs/superpowers/specs/2026-07-03-flaui-mcp-phase8-desktop-watch-design.md).
    - **Phase 9 — vision & opaque-app access** ✅ **(shipped v0.9.0).** Two prongs closing the
      Electron/Chromium and zero-UIA-surface residuals cited throughout this roadmap. **Prong A
      (accessibility wake):** `desktop_wake_accessibility`/`desktop_release_accessibility`/
      `desktop_list_wakes` activate and **HOLD** an opaque Chromium/Electron window's native UIA tree
      (idempotent per window, auto-releases on window close, capped 32 wakes/session); `desktop_snapshot`
      gains a `wakeable:true` hint when it detects a Chromium Win32 class with a collapsed tree. Chromium
      re-collapses the tree **lazily** once idle after release, not necessarily immediately
      (spike-confirmed). **Prong B (on-box OCR targeting):** `desktop_find_text`/`desktop_wait_for_text`
      use `Windows.Media.Ocr` (no external dependency) to resolve visible text to click coordinates —
      both physical screen px and `desktop_click_at` window fractions — for canvas/game surfaces,
      Citrix/RDP inners, and an editor's text body that stays gated even when woken; fuzzy match by
      default, `OcrUnavailable` if no Windows OCR language pack is installed. OCR here is **targeting,
      not reading** — the model already reads the screenshot. Decision flow: rich UIA → snapshot
      directly; opaque Chromium (`wakeable:true`) → wake then snapshot/find/interact; zero-accessibility
      (game/canvas/editor-text-body) → `desktop_find_text` + `desktop_click_at`. Spec:
      [`docs/superpowers/specs/2026-07-03-flaui-mcp-phase9-vision-opaque-access-design.md`](docs/superpowers/specs/2026-07-03-flaui-mcp-phase9-vision-opaque-access-design.md);
      plan:
      [`docs/superpowers/plans/2026-07-03-flaui-mcp-phase9-vision-opaque-access.md`](docs/superpowers/plans/2026-07-03-flaui-mcp-phase9-vision-opaque-access.md).
    - **Phase 10 — Consumer ergonomics.** Three consumer-prioritized changes from a single design
      spec; **#2 shipped (v0.10.0)**, **#1 shipped (v0.11.0)**, #3 not yet done.
      - **#2 — First-class `selector` targeting** ✅ **(shipped v0.10.0), the main feature.** An
        optional `selector` (`{automationId?, name?, nameMatch?, controlType?, scope?, ignoreCase?}`)
        alongside the existing `@ref` param on 17 interaction/input/content-read tools, resolved
        atomically on the action thread at call time — eliminating the `eN` re-snapshot churn (every
        `desktop_snapshot` renumbers refs, so a held `eN` can die `RefNotFound`). **Exactly one of
        `{ref | selector}`** (`desktop_key`: at most one; omit both → foreground target). Resolution is
        count==1 fail-closed: `0` → `SelectorNoMatch`, `>1` → `AmbiguousMatch` — never a silent pick. A
        successful call returns `resolvedElement:"eN"` (a freshly minted, equally ephemeral ref — reuse
        promptly, don't hoard it). `ignoreCase` is a shared flag defaulting **true** on `selector`
        (ergonomic) and **false** on `desktop_find` (back-compat); preview a selector's match with
        `desktop_find(ignoreCase:true)`. Honest limitation: the payoff scales with `automationId`
        coverage — a control with no `automationId` and a non-unique `name` still degrades to
        `AmbiguousMatch`, same as a duplicate-name `desktop_find`; fall back to a `desktop_snapshot`
        ref for those. Spec: [`docs/superpowers/specs/2026-07-04-flaui-mcp-phase10-consumer-ergonomics-design.md`](docs/superpowers/specs/2026-07-04-flaui-mcp-phase10-consumer-ergonomics-design.md);
        plan: [`docs/superpowers/plans/2026-07-04-flaui-mcp-phase10-selector.md`](docs/superpowers/plans/2026-07-04-flaui-mcp-phase10-selector.md).
      - **#2-follow (v0.10.1) — Trustworthy Hands: audit trace + intent overlay** ✅ **(shipped v0.10.1).**
        Element-identity audit trace (RuntimeId/AutomationId/ClassName/ControlType/bounds, never
        content-bearing properties) for synthetic-input actions that resolve a selector target — so
        the audit log names exactly what the agent touched. Intent overlay (`--overlay`/`--overlay-ms=N`)
        draws a red rectangle on the target element ~500 ms before each action, so a human watching sees
        what the agent is about to do (visibility aid, not a safety gate; lease/deny-list remain the
        gates).
      - **#1 — Opt-in handle on `desktop_list_windows`** ✅ **(shipped v0.11.0).** `includeHandles:true`
        mints a reusable `wN` handle inline per window (fork 1a, lazy), so you can snapshot/find/
        interact directly, skipping the separate `desktop_open_window` round-trip. Minting stays
        pure-Win32 (never blocks on an unresponsive window) and reuses the same `wN` across polls; the
        UIA binding is deferred to first use and guarded by a HWND-recycle pid-reverify (read and write
        paths), so a recycled handle fails `WindowHandleStale` rather than acting on a different
        process. Plan:
        [`docs/superpowers/plans/2026-07-04-flaui-mcp-phase10-window-handles.md`](docs/superpowers/plans/2026-07-04-flaui-mcp-phase10-window-handles.md).
      - **#3** — spec treats this as a likely-drop, revisit-after-#2 item; not yet planned/implemented.

## Consumer-lens hardening backlog (v0.7.3 release-capstone review, 2026-07-02)

Raised from a whole-repo "ready-to-cut-release" review through the eyes of the final operator (an AI
agent driving a real desktop). None block v0.7.3; they harden the product's central promise — a
*trustworthy* pair of hands on the desktop.

- **Read-only enforcement as a structural invariant** ✅ **(shipped v0.7.3).** The capstone found that
  `desktop_launch_app`/`focus_window`/`close_window` bypassed `--read-only-mode` (used the non-guarding
  path). Beyond the point-fix, `ToolReadOnlyInvariantTests` now reflects the whole tool surface and
  asserts EVERY `[McpServerTool]` declares exactly one of `ReadOnly`/`Destructive` AND that every
  `Destructive` tool actually short-circuits to `WriteBlockedReadOnly` in read-only mode — so a future
  tool that forgets to route through `GuardWrite` fails in CI, not production. "We remembered to gate
  each tool" is now an enforced invariant.
- **Continuous interactive (Desktop/UIA + synthetic-input) coverage in CI.** Today the green CI badge
  proves only the headless half; the `Category=Desktop` suite — the part that exercises the product's
  entire reason to exist — is maintainer-run. Stand up a self-hosted/scheduled interactive runner so a
  regression in real UIA/`SendInput` behavior is caught continuously, not just at manual smoke time.
  **Feasibility validated (2026-07-03):** `SendInput` works in any *connected, unlocked* session, so a
  local connected+leased run is already a legitimate pre-tag gate. The **sound** unattended approach is
  **Sysinternals Autologon → box boots into an unlocked physical console → run the CI agent as an
  interactive startup app (never a Session-0 Windows service)**; `tscon /dest:console` is *not* sound
  (resolution collapse breaks bounds/visibility tests; WS2022/Win11 harden against `tscon` hijacking).
  Prerequisite hygiene: the real end-to-end `SendInput` test (`InputToolsTests`) must first be made
  reliably runnable (see the known-broken harness note in the Phase 7 spec §9).
  (Complements the deferred "Full DPI × OS × integrity test matrix in CI" below.)
- **Reactive-editor typing robustness → first-class remedy** ✅ **(delivered v0.7.7 — see Phase 7
  above).** Typed text into the new Win11 Notepad and Chromium editors garbles at any pacing; it was a
  documented limitation with a soft `verify` + `desktop_set_value`/clipboard-paste fallback (see
  **Phase 4b.3** above and Known Limitations below). The clipboard-paste remedy is now the first-class
  `desktop_paste_text` tool (atomic, clipboard-preserving) rather than a manual
  `desktop_clipboard_set` + `desktop_key` two-step, and `desktop_type`'s verify remedy recommends it
  directly.
- **Code signing the distributed exe (pull earlier).** An unsigned input-synthesizing binary that also
  configures agents is a rough first-touch trust barrier for a *security* tool — see the "Top v2
  distribution item" in the v2 table below; worth pulling ahead of other v2 work for adoption.

## v2 (deferred)

| Feature | Why deferred | Source |
| --- | --- | --- |
| **UIA event streaming** (`desktop_watch` — Window_Opened, StructureChanged, FocusChanged) | ✅ **SHIPPED in Phase 8 (v0.8.0)** — over **stdio** MCP notifications, NOT SSE (the SSE assumption was the reason it was deferred; it was a false dependency). The real risk (COM-thread event callbacks × the STA dispatcher) is addressed by the Phase 8 capture→coalesce→STA-marshal→emit pipeline. Push+drain: `desktop_drain_events` was added as a reliable fallback for hosts that don't surface MCP notifications. | Both reviews |
| **Built-in OCR fallback** (`Windows.Media.Ocr` text targeting) | ✅ **SHIPPED in Phase 9 (v0.9.0)** as `desktop_find_text`/`desktop_wait_for_text` — see the Phase 9 entry above. | Both reviews |
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
  snapshot is one large `Document` node (no inner refs ⇒ no element-targeted perception or input —
  fall back to the coordinate/vision path). Typed text into Chromium editors (Monaco/CodeMirror/
  `contenteditable`) garbles like the new Notepad; `desktop_type`'s verify flags it, but
  `desktop_set_value` is usually `PatternUnsupported` there — clipboard paste is the reliable path
  (see Phase 4b.3). **Per-app escape hatch:** launch with `--force-renderer-accessibility`.
  **WinUI 3 / WPF / Qt** generally expose proper UIA peers and are *not* affected. *Future (its own
  spike, v1.5/v2):* set the system UIA "assistive tech present" flag to light this up globally —
  deferred because it's system-wide and alters every app's behavior.
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
| **Ref-resolution hardening (INV-8)** — strict RuntimeId-only writes (scoped narrow-then-verify; recycled AutomationId → `REF_STALE_UNRESOLVABLE`); fail-closed lenient reads (`AMBIGUOUS_MATCH`/`REF_STALE`, no positional fallback); break-glass `FLAUI_MCP_REF_STRICT=off` | v0.7.3a | Closes recycled-AutomationId destructive-action + confused-deputy read retarget; prerequisite for `desktop_find`/durable refs (Plan 2). |

## Notes

- Items marked "Both reviews" were independently flagged by both external
  reviewers (Grok + the second architect pass), raising confidence they're real.
- The v1/v2 line is drawn to reach a **working, resilient server fast**, then add
  reactive/perception superpowers once the core is proven.
