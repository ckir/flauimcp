# FlaUI.Mcp Phase 4 — Synthetic Input + Safety Stack (design spec)

**Date:** 2026-06-30 · **Status:** design, pending user review-gate · **Targets:** v0.6.0 (4a), v0.7.0 (4b)
**Companion memories:** `project-flaui-mcp-prompt-injection`, `project-flaui-mcp-test-environment`,
`project-flaui-mcp-execution`. **Roadmap:** ROADMAP.md "Phase 4".
**Review provenance:** agy AGY-FIRST divergent consult (security-first split + `ISyntheticInput` seam +
time-lease adopted) → AGY-AFTER convergent R1 (2 blockers + 4 should-fix + 2 nits) → R2 "spec-ready" →
fidelity "SPEC: faithful" + delta-clean → **3 multi-persona rounds:** RED-TEAM (1 CRITICAL — agent-set
`allowInterlockedSink` self-jailbreak → moved to lease `--allow-shells`; +3 hit-test/mutation fixes);
WIN32-PEDANT (2 CRITICAL API bugs — foreground `GA_ROOT` compare, virtual-desktop mouse normalization;
+surrogate pairs, `GA_ROOT` deny-list walk-up); IMPLEMENTER (testability claim required broader seams →
added `IPlatformEnvironment` + `ILeaseProvider`; re-verify pushed into the send leaf). Seams:
`.clavity/seams/phase4-design-fork.md`, `phase4-design-review.md`, `phase4-design-review-r2.md`.

## 1. Goal & blast-radius framing

Phase 4 adds the deliberately-isolated **"act" leg of the lethal trifecta**: real OS mouse/keyboard
(`SendInput`) and the coordinate/vision action path, for apps whose controls implement no UIA patterns
or have broken accessibility. Because synthetic input can **mis-target** (type into a shell, click a
credential dialog) in ways pattern actions cannot, the load-bearing safety controls deferred through
Phases 2–3 — the ACTION deny-list, action budget + audit, and hard-fail-on-elevation — become real
here, alongside a new **out-of-band time-lease** kill switch.

### Decided forks (user rulings, 2026-06-30)
- **Sequencing = security-first split.** Build and prove the safety machinery *before* the dangerous
  capability exists.
- **Safety model = full time-lease (dead-man's switch) for v1**, not merely an enable flag.

## 2. Architecture — the seam and the split

A single interface, `ISyntheticInput`, is the **only** boundary between decision logic (testable on the
headless box) and the one OS call that cannot be validated here.

```
InputTools (4b)        InputGuard (4a)                 ISyntheticInput
  desktop_type   ──►   lease check                ┌─ Win32SyntheticInput   (real SendInput; 4b)
  desktop_click  ──►   deny-list / sink interlock ┤
  desktop_click_at ►   session-state guard        └─ RecordingSyntheticInput (test fake; records,
  desktop_key    ──►   budget + audit                                         never fires; 4a)
  desktop_drag   ──►   └─► ISyntheticInput.<op>(...)
  desktop_set_caret / desktop_select_text_range
```

- **`interface ISyntheticInput`** (new, Core/Interaction): the verbs the tools need, each carrying the
  *expected target* so the leaf can re-verify atomically — `KeyType(text, expectedForegroundRoot)`,
  `KeyChord(modifiers, key, expectedForegroundRoot)`, `MouseClick(physicalPoint, button, count,
  modifiers, expectedRootAtPoint)`, `MouseDrag(startPhysical, endPhysical, button, expectedRootAtEnd)`.
  Each verb performs the **atomic pre-send re-verify** against the expected target (via
  `IPlatformEnvironment`) *inside the same `Task.Run` delegate that calls `SendInput`* — closing the
  focus-steal / overlay TOCTOU as tightly as user-mode allows — then fires. DI-selected impl.
- **`Win32SyntheticInput`** — raw `SendInput` P/Invoke (apartment-agnostic `Task.Run`, mirroring the
  ClipboardAccess precedent). **Ships in 4b only.** The single un-automated leaf.
- **`RecordingSyntheticInput`** — records calls into a list, fires nothing. The 4a test double; lets
  every guard/lease/deny-list/budget/audit path be asserted headlessly. **It is the only "bypass" of
  the session guard, and it exists solely by DI in tests — never a shipped runtime flag** (this is what
  defuses the auto-lockout paradox: the guard wraps the *real* backend; the fake never reaches it). The
  fake consults the injected `IPlatformEnvironment` so the atomic-re-verify ABORT path (focus stolen /
  point changed before send) is exercised headlessly too.
- **`InputGuard`** (new, Core/Interaction) — the decision pipeline run before any delegation:
  `read-only-mode (GuardWrite) → lease → deny-list/sink interlock → session-state guard → budget →
  audit → ISyntheticInput.<op>`. It is a **pure function of its INJECTED dependencies** and performs no
  hidden I/O of its own — every environment read goes through a seam below, so each branch is
  deterministically assertable on a single-state CI runner. (The final atomic pre-send re-verify is
  delegated into the send leaf per `ISyntheticInput` above — not evaluated here on the STA thread a
  scheduling quantum early.)
- **`IPlatformEnvironment`** (new — the "read" half of the seam) — wraps every Win32 *probe* the guard
  needs: `WindowFromPhysicalPoint` + `GetAncestor(GA_ROOT)` (coordinate → top-level resolver), the
  foreground-window-root read, and the `OpenInputDesktop` session-state oracle. The fake returns
  scripted HWNDs / process+class / locked|unlocked so the coordinate deny-list and session guard are
  asserted headlessly.
- **`ILeaseProvider`** (new) — abstracts the lease-file read, its `LastWriteTime`, and the clock
  (`DateTime.UtcNow`), so lease present/absent/expired/foreign-SID and the `LastWriteTime` budget-reset
  are simulatable without touching the filesystem or wall clock.

### Phase split
- **Phase 4a (v0.6.0)** — ships **no input-firing MCP tool.** Delivers `ISyntheticInput` +
  `RecordingSyntheticInput`, `InputGuard`, the lease subsystem + `unlock` CLI subcommand, the deny-list
  /sink interlock, the session-state guard, the budget + audit, the elevation hard-fail, and the **2
  SHOULD-FIX** carried from 3b-2. Fully unit-tested headless. Non-Desktop gate stays the CI oracle.
- **Phase 4b (v0.7.0)** — ships `Win32SyntheticInput` + the thin tool layer, **opened by a throwaway
  spike** (does this box fire input in an active RDP session at all? does `Focus()`-then-type work, or
  do Electron/WinUI sinks need coordinate clicks?). The spike's findings feed the 4b targeting contract
  so it is not over-built in 4a (avoids the "vacuum trap"). 4b gate = spike + user manual validation on
  a real active session.

## 3. Safety stack (4a)

### 3.1 Time-lease (default-closed kill switch)
Synthetic input is **OFF unless an unexpired lease exists.** The human grants it **out-of-band**:

- **`flaui-mcp unlock --minutes N [--allow-shells]`** — a CLI subcommand of the *same exe*. Writes the
  lease file. `--allow-shells` records the **out-of-band human capability** to target interlocked sinks
  (terminal / Win+R / address bar); without it those sinks stay refused (§3.2). A companion
  `flaui-mcp lock` deletes the lease (immediate revoke).
- **Lease file:** `%LOCALAPPDATA%\FlaUI.Mcp\input.lease`. Content (single line, parse-tolerant):
  `expiryUtc=<ISO-8601 UTC>;sid=<granting user SID>;caps=<comma-separated capabilities>` (caps empty by
  default; the token `shells` — set only by `unlock --allow-shells` — authorizes interlocked sinks).
  The server reads it **per input action**, opens
  it `FileShare.ReadWrite` with a short retry-on-sharing-violation loop (races the `unlock` writer),
  and compares `expiryUtc` to `DateTime.UtcNow`. Missing/expired/unparseable/foreign-SID ⇒ **input
  refused** (`InputNotLeased`).
- **Auto-close:** expiry is purely time-based; no daemon — the next action past `expiryUtc` is refused.
- **Documented boundary (honest):** the lease is a **hard gate only against an agent whose sole
  capability is this server's MCP tools** (no MCP tool can write the lease). An agent with **same-user
  host shell** could run `unlock` or forge the file — there the lease is a default-closed time-box +
  audit trail, not a cryptographic wall. For hostile-content automation the **primary** gate remains
  the client's per-action `destructiveHint` approval plus running the agent without host shell.

### 3.2 ACTION deny-list / sink interlock
Run for **both** the ref path (window already known) and the coordinate path.

- **Hard-refuse** (`TargetDenied`) synthetic input whose target window is owned by: UAC / `consent.exe`,
  the `Winlogon`/secure-desktop surface, credential dialogs, and the credential-store processes already
  in `PerceptionPolicy.DeniedProcesses` (reuse + extend that list).
- **Interlock sinks** (`SinkInterlocked`) — terminal (`WindowsTerminal`, `ConsoleWindowClass`,
  `CASCADIA_HOSTING_WINDOW_CLASS`), the Win+R run dialog, and the browser address bar — are refused
  **unless the active lease carries the `shells` capability** (granted out-of-band via `unlock
  --allow-shells`; §3.1). **The override is NOT an agent-supplied tool parameter.** (Red-team finding,
  CRITICAL: an agent-facing `allowInterlockedSink` boolean is zero-friction for a prompt-injected
  agent — it sets it `true` and self-jailbreaks into a shell. The override must be human authority,
  out-of-band, consistent with the time-lease model; the agent cannot authorize its own shell /
  address-bar input.)
- **Coordinate-path hit-test (BLOCKER-1 fix):** before any coordinate input, hit-test the physical
  point with **`WindowFromPhysicalPoint`** (DPI-correct; *not* virtualized `WindowFromPoint`), then
  **walk to the top-level window via `GetAncestor(hwnd, GA_ROOT)`** — the raw hit returns the deepest
  child control (e.g. `Edit`/`DirectUIHWND`), but the deny-list keys off the top-level process + class
  (e.g. `ConsoleWindowClass`/`Chrome_WidgetWin_1`) — resolve its owning process + window class, and run
  the **identical** deny-list/interlock logic. The
  coordinate path is a first-class deny-list citizen — no side door. `desktop_drag` hit-tests **both**
  its start (MouseDown) and end (drop) points. **The hit-test is the IMMEDIATE pre-send check, not a
  call-entry pre-calculation:** `desktop_click_at` re-hit-tests the point in the same instant before
  `SendInput` (uniform with the ref path's pre-send re-check), and `desktop_drag` re-hit-tests the
  **end** point immediately before the `MouseUp` (red-team: an overlay/occluding window can move in
  the gap between a call-entry check and the actual click/drop, falling the input through to a denied
  sink).
- **Foreground-targeted actions:** a tool invoked without an explicit ref (`desktop_key` against the
  foreground window) resolves `GetForegroundWindow()` → owning process/class and runs the **identical**
  deny-list/interlock before firing — the no-ref path is not a bypass either.

### 3.3 Session-state guard (fail-closed)
Before firing (real backend only), assert the process can actually deliver input to the interactive
user desktop: **`OpenInputDesktop(0, FALSE, MAXIMUM_ALLOWED)`** succeeds (fails when the session is
locked or on the `Winlogon` secure desktop) **AND** `GetForegroundWindow() != IntPtr.Zero`. Handle
closed with `CloseDesktop` each check. **Biased fail-closed** — a false-positive merely refuses a
working session (annoying); a false-negative mis-fires input into the wrong desktop (dangerous).
Failure ⇒ `InputDesktopUnavailable`. (Accepted false-OPEN: a disconnected-but-not-locked RDP session
may still open `default` — input then reaches the correct app desktop, just unobserved; the guard's job
is to block the secure desktop, not to assert a human is watching.)

### 3.4 Budget + audit
- **Budget:** per-window rate limit (proposed defaults `MaxActionsPerWindow`=60 per rolling
  `BudgetWindowSeconds`=60 — i.e. ~1 action/sec sustained; concrete values pinned in the 4a plan).
  On exceed ⇒ refuse (`InputBudgetExceeded`) telling the agent to obtain human re-authorization. The
  budget **resets when the lease file `LastWriteTime` advances** (a fresh `unlock` grant). Rationale: a
  valid 5-minute lease must not become a blank cheque for an injected click/keystroke flood.
- **Audit = EVENT-ONLY**, written to **stderr** (matching the clipboard audit precedent): timestamp,
  target window handle + pid + process name, action kind, and **payload length only — never the typed
  text**. This preserves Phase-2's deliberate no-secrets-on-disk stance and resolves the "log payload"
  contradiction. (Content audit remains an explicit non-goal.)

### 3.5 Elevation hard-fail
Upgrades the Phase-2 warn-only: if the server process is elevated, **refuse all synthetic input**
(`AccessDeniedIntegrity`-class refusal at guard entry) unless launched with **`--unsafe-allow-elevation`**.
(Perception/pattern tools are unaffected; only the synthetic-input sink hard-fails.)

### 3.6 Carried SHOULD-FIX (3b-2 → 4a)
- **#1 Defensive grid-level reads:** wrap the grid-level reads (`RowCount`/`ColumnCount`/`GetItem`) in
  `PerceptionManager.GetGridCellAsync` in the same per-read try/catch the per-cell reads use, so a flaky
  COM grid degrades to a structured error instead of leaking `INTERNAL`.
- **#2 Fail-closed password redaction:** in `GetGridCellAsync`/`GetTextAsync`, if the `IsPassword` read
  **throws**, treat the value as a password (redact `[REDACTED]`) — never fall through to reading the
  text. (False-positive cost: a non-password element's text is redacted — harmless; the safe default.)

## 4. Tool surface (4b)

All carry `readOnlyHint:false, destructiveHint:true` and route through `InputGuard`. New error codes:
`InputNotLeased`, `InputDesktopUnavailable`, `InputBudgetExceeded`, `SinkInterlocked` (+ reuse
`TargetDenied`, `InvalidArgument`, `ElementNotActionable`).

| Tool | Path | Contract |
| --- | --- | --- |
| `desktop_type` | ref + Focus-first | Types `text` (≤ **4096** UTF-16 code units/call → `InvalidArgument` over cap; non-BMP chars emit surrogate-pair `INPUT`s) into the focused element. `Focus()` then **re-verify `GetForegroundWindow() == GetAncestor(targetTopLevelHwnd, GA_ROOT)` immediately before send; ABORT on mismatch** (BLOCKER-2 fix; foreground is a *top-level* window, so compare roots — a child-HWND equality would abort 100% of the time). |
| `desktop_key` | ref or foreground | Sends one chord. **Grammar:** `+`-delimited, zero-or-more modifiers `Ctrl\|Alt\|Shift\|Win` + exactly one key from the fixed table: letters/digits, `Enter Tab Esc Backspace Delete Home End PageUp PageDown Up Down Left Right Space`, `F1`–`F24`. Unknown token ⇒ `InvalidArgument` (never a silent mis-key). |
| `desktop_click` | ref | Synthetic mouse at the element's clickable point. Params: `button` (left/right/middle), `count` (1/2), `modifiers`. **Re-hit-test the point still maps to the target window immediately before send.** |
| `desktop_click_at` | coordinate | Click at `xPct`/`yPct` (see §5). Coordinate hit-test + deny-list (§3.2), re-checked in the **immediate pre-send instant**. Params `button`/`count`/`modifiers` (no `allowInterlockedSink` — the interlock override lives in the lease, §3.2). |
| `desktop_drag` | coordinate | Drag from (`startXPct`,`startYPct`) to (`endXPct`,`endYPct`). Hit-test + deny-list on **both** endpoints; the **end** point is re-hit-tested immediately before `MouseUp`. |
| `desktop_set_caret` | ref | Position the text caret (TextPattern range + select-degenerate-range); a typing precursor — folded here from the 3b deferral. |
| `desktop_select_text_range` | ref | Select a text range (start/length over the text provider). |

**`desktop_set_caret` / `desktop_select_text_range`:** these mutate caret/selection via UIA
`TextPattern` and synthesize **no** OS input, so the time-lease + session-state guard (which gate
`SendInput`) do **not** apply. **But they MUST still route through the deny-list / sink-interlock**
(red-team finding: mutating the selection of a *denied* credential app could extract text the app
fails to flag `IsPassword`). So the rule is fixed: deny-list / interlock = **always**; lease +
session-guard = exempt (no `SendInput`). The 4b plan only decides the wiring, not whether the deny-list
applies. They are listed here because they were deferred *from* Phase 3 as typing precursors, not
because they fire synthetic input.

## 5. Coordinate space contract
`xPct`/`yPct` ∈ [0.0, 1.0] are relative to the **target window's bounding rect**, converted to physical
screen coordinates via the window bounds + `dpiScale` already returned by `desktop_screenshot` /
`desktop_get_bounds`. This keeps the coordinate path anchored to the same contract the screenshot tool
publishes (an agent reads pixels from a window screenshot and acts in the same window's fractional
space). Declared in every coordinate tool's description.

**`SendInput` normalization (Win32 correctness):** absolute mouse input does **not** take pixels — the
`Win32SyntheticInput` backend must map the physical pixel point into the `0–65535` range over the
**virtual screen** (`SM_XVIRTUALSCREEN`/`SM_CXVIRTUALSCREEN` + Y equivalents) and set
`MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK`, or a secondary-monitor click lands in the wrong
place. (FlaUI's `Mouse` helper does this normalization internally — using it for the mouse leg instead
of raw `SendInput` is an acceptable 4b choice.) **P/Invoke nit:** the `INPUT` struct must use correct
sequential layout / union alignment and a valid `cbSize`, or `SendInput` fails silently returning 0 on
64-bit Windows.

## 6. Testing strategy
- **4a — fully headless** via three seams — `ISyntheticInput` (act), `IPlatformEnvironment` (Win32
  probes: hit-test resolver, foreground-root, session-state), `ILeaseProvider` (lease + clock). Together
  they make every guard path assertable: lease
  present/absent/expired/foreign-SID; deny-list hard-refuse + interlock override; coordinate hit-test
  resolves to a denied/allowed window (fake `WindowFromPhysicalPoint`); session guard open/locked;
  budget exceed + lease-write reset; audit emits event-only (length, not text); elevation hard-fail ±
  `--unsafe-allow-elevation`. The 2 SHOULD-FIX get pinning tests (throwing `IsPassword` ⇒ redacted;
  throwing grid-level read ⇒ structured error). Non-Desktop gate = CI-green oracle.
- **4b — spike + manual.** The spike validates the real leaf and the targeting mechanism on an active
  session; thereafter the user manually validates the input tools on their real environment. The Win32
  `SendInput` leaf is the only surface not covered by automated tests, and it is deliberately tiny.

## 7. Residual risks (documented, accepted)
- **App-internal sibling-focus shift:** the pre-send foreground re-verify protects the OS
  process/window boundary but not a focus move to a *sibling* control within the same window in the
  sub-millisecond send window (element-level `HasKeyboardFocus` was rejected as too slow/flaky for a
  TOCTOU mitigation). Payload could land in an adjacent field of the *same* app.
- **Coordinate hit-test TOCTOU:** no atomic check-and-click exists in user-mode Win32; a window change
  in the millisecond between hit-test and click is irreducible. Bounded by the tight pre-send re-check.
- **Session guard ≠ human presence:** the guard blocks the secure desktop and locked sessions, but a
  disconnected-but-unlocked session can still receive input unobserved.
- **Lease vs host-shell agent:** see §3.1 — the lease is not a wall against an agent with same-user
  shell/file access; that threat model is explicitly out of scope (run read-only / no host shell).

## 8. Sequencing & deliverables
- **Now:** this spec → user review-gate → **4a implementation plan** (its code is fully knowable now;
  PLAN-vs-SPEC: 4b stays spec-level until 4a merges + the spike runs).
- **4a (v0.6.0):** safety stack + seams + guard + 2 SHOULD-FIX, headless-tested; release. NB 4a ships a
  *dormant* safety engine — `InputGuard` has full test coverage but ~0% prod execution until 4b plugs
  the input tools in; that is the intended security-first-split tradeoff, not dead code by oversight.
- **Spike:** throwaway active-session synthetic-input probe; record findings.
- **4b (v0.7.0):** `Win32SyntheticInput` + tools behind the proven gates; manual validation; release.
