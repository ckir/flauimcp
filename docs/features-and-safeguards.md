[← Back to FlaUI.Mcp README](../README.md)

## What it does

FlaUI.Mcp is a stdio MCP server built on [FlaUI](https://github.com/FlaUI/FlaUI) (UI
Automation / UIA3) and the official MCP C# SDK. It exposes Windows desktop control as MCP
tools an agent can call.

**Current tools (window management + perception):**

| Tool | Read-only | Description |
| --- | --- | --- |
| `DesktopListWindows` | ✅ | List top-level windows with title, process name, and PID. Opt-in `includeBounds` adds absolute physical-pixel `Bounds` + `ZOrder` (0 = topmost) for occlusion reasoning. |
| `DesktopOpenWindow` | ✅ | Open a window by `pid` or `title`, returning a handle (e.g. `w1`). |
| `DesktopSnapshot` | ✅ | Walk a window's UI into an indented, ref-tagged accessibility-tree snapshot. Each line carries an `eN` ref, control type, name, bounds, state (incl. `focused`), and supported patterns. Options: `interactiveOnly` (prune noise, default on), `fullProperties` (add AutomationId/HelpText), `includeOffscreen` (default off), `maxDepth`, and `root` (root the walk at a prior ref). |
| `DesktopLaunchApp` | — | Launch an executable (with optional args) and return a handle to its main window. |
| `DesktopFocusWindow` | — | Bring a window to the foreground. Returns `foregroundGained`; when Windows' foreground-lock blocks it, also returns `currentForeground`/`recommendedAction` (`"call-wait-for-foreground"`)/`recovery` — see `desktop_wait_for_foreground` under [Synthetic input](#synthetic-input) below. |
| `DesktopCloseWindow` | — | Close a window and free its handle. |

**Perception-completion tools (read-only):** screenshot, bounds, snapshot
stats/diff, focus, and wait conditions. All are `readOnlyHint`/non-destructive.

| Tool | Description |
| --- | --- |
| `DesktopScreenshot` | Capture a window, an element (`window` + `ref`), or the full virtual desktop as a PNG (returned as a native MCP image block + `{bounds, dpiScale, scaleApplied, redactions}` metadata). Password fields are redacted at capture time; width is clamped to 1920. Focus the window first (no occlusion handling). |
| `DesktopGetBounds` | Absolute physical-pixel screen bounds `{x,y,w,h}` (signed, multi-monitor safe) of an element, plus its monitor `dpiScale` and `isOffscreen`. |
| `DesktopSnapshotStats` | Cheap orientation: control counts (total / interactive / offscreen / redacted) + a per-control-type histogram, without the full tree. Takes a `window` or a prior `snapshotId`. |
| `DesktopSnapshotDiff` | Diff a window's current tree against an explicit baseline `snapshotId` → `added` / `removed` / `changed` (Name/Enabled/Focused), keyed by a composite identity. Optional `scope=<ref>` re-roots the diff at a subtree instead of the whole window (slices the cached baseline in-memory). |
| `DesktopWaitFor` | Poll until a selector condition holds (`until` = exists \| enabled \| gone \| valueEquals). A timeout returns `{satisfied:false}` data, not an error; on success returns the matched ref + a fresh snapshotId. |
| `DesktopWaitForStable` | Poll until a window (or a scoped subtree) stops structurally changing; `includeText` also waits on text/Name settling. Timeout returns `{stable:false}`. |
| `DesktopGetFocusedElement` | O(1) "where am I": the UIA-focused element's ref + descriptor + owning window (handle/title/pid). The ref is scoped to that window so you can act on it. |
| `DesktopFind` | Query a window for element refs by `automationId` / `name` (`nameMatch` = eq \| contains) / `controlType` / `enabledOnly`, optional subtree `scope`, without walking the whole tree. Returns `matches` (ref, automationId, name, controlType, bounds, isOffscreen, isEnabled, hasFocus) plus `totalMatches`/`isTruncated` (capped by `max`, default 20). Honors the perception deny-list and password redaction — a password field's `name` is `[REDACTED]` and not matchable by name. Refs are additive: a find does not supersede snapshot refs. |

Read-only tools are annotated as such so MCP clients can auto-approve them while still
prompting for the mutating ones. Every tool returns structured JSON. Errors come back as a
uniform envelope (`{ error, message, suggestedRecovery }`) so the agent can recover rather than
crash the session.

**Interaction tools (pattern-based):** act on an element by its snapshot `ref`
through UI Automation control patterns. These are **not** synthetic mouse/keyboard input (that
is a later phase) — they drive the app's own automation providers, so they work over RDP and
never move the real cursor. All are state-changing (annotated `destructive`).

| Tool | Description |
| --- | --- |
| `DesktopInvoke` | Activate an element (e.g. click a button) via InvokePattern. |
| `DesktopSetValue` | Set a control's text/value via ValuePattern (focuses first). |
| `DesktopToggle` | Toggle a checkbox/switch via TogglePattern. |
| `DesktopExpand` | Expand/collapse a tree node, expander, or combo via ExpandCollapsePattern. |
| `DesktopSelect` | Select a list item / radio / tab via SelectionItemPattern. |
| `DesktopScroll` | Scroll a container (`direction` = up/down/left/right, `amount` = 1–50 steps) via ScrollPattern. |
| `DesktopScrollIntoView` | Realize a specific item in a scrollable container via ScrollItemPattern. |
| `DesktopSetFocus` | Set keyboard focus to an element via UIA Focus (reveals lazy-loaded content). |
| `DesktopWindowTransform` | Maximize / minimize / restore a window via the Window pattern. |

An action that opens a modal returns `ActionBlockedPending` instead of hanging the server —
snapshot the window to see the dialog, then act on it.

Every tool in this table also accepts a `selector` as an alternative to `ref` — see
[Targeting: `ref` or `selector`](#targeting-ref-or-selector) below.

**Structured content & clipboard tools:** read structured data from grid/table
elements and interact with the system clipboard. Read-only tools are `readOnlyHint`; mutating
tools are `destructive` and blocked in `--read-only-mode`.

| Tool | Description |
| --- | --- |
| `DesktopGetGridCell` | ✅ Read-only. Read one grid/table cell by 0-based `(row, col)`. Returns `{value, controlType, automationId, isPassword}`. Password cells are masked as `[REDACTED]`/`isPassword:true`; credential-store windows are denied outright (`TargetDenied`). `GridCellOutOfRange` if out of bounds; `PatternUnsupported` if the element is not a grid. |
| `DesktopGetText` | ✅ Read-only. Read an element's text via UIA TextPattern. `selectionOnly` reads only the current selection; `maxLength` caps output (default 10 000 chars, returns `truncated:true` if hit). Password fields return `[REDACTED]`/`isPassword:true`. Off-screen targets are readable. `PatternUnsupported` if no TextPattern. |
| `DesktopGridSelect` | Select a grid/table cell by `(row, col)` via UIA SelectionItemPattern. Off-screen cells return `ElementNotActionable` — scroll into view first. Blocked in `--read-only-mode`. |
| `DesktopClipboardGet` | ✅ Read-only. Read the system clipboard as plain text (CF_UNICODETEXT). ⚠ **Clipboard exfil risk:** the clipboard may contain passwords or tokens the user recently copied — no redaction is possible at the clipboard layer. Returns `ClipboardUnavailable` when the clipboard is locked or holds non-text content. |
| `DesktopClipboardSet` | Write text to the system clipboard. Blocked in `--read-only-mode`. |

`DesktopGetGridCell` and `DesktopGetText` honor the same credential-store denylist and
`IsPassword` redaction as `DesktopSnapshot` — password cells and fields always surface as
`[REDACTED]`, and windows owned by known credential stores are denied outright.

`DesktopGetGridCell`, `DesktopGetText`, and `DesktopGridSelect` also accept a `selector` as an
alternative to `ref` — see [Targeting: `ref` or `selector`](#targeting-ref-or-selector) below.

**Input safety foundation.** The safety infrastructure and seam interfaces landed *before* any
synthetic input tool could — deliberately *before* the blast radius. No mouse/keyboard tool lives
in this layer; the `SendInput`-backed tools (`desktop_type`, `desktop_click`, `desktop_key`, etc.)
build on it — see **Synthetic input** below.

What this layer introduces:

- **Three seam interfaces** — `ISyntheticInput`, `IPlatformEnvironment`, `ILeaseProvider` —
  injectable, testable boundaries isolating all future input work from the rest of the server.
- **InputGuard pipeline** — deny-list, per-window action budget, and an event-only audit log.
  Synthetic input into UAC / `consent.exe`, credential dialogs, and high-risk sinks (terminal,
  browser address bar, Win+R run dialog) is refused outright; other windows consume from a
  configurable per-window budget.
- **Time-lease (dead-man's switch)** — synthetic input is hard-refused by default. A human grants
  a time-bounded window via `flaui-mcp unlock --minutes N [--allow-shells]` run out-of-band; the
  lease expires automatically when the time runs out, or immediately on `flaui-mcp lock`. The agent
  cannot grant or extend its own lease.
- **Elevation hard-fail** — if the server starts with Administrator rights, synthetic input is
  refused unless `--unsafe-allow-elevation` is passed explicitly at launch.

### Synthetic input

Real `SendInput`-backed mouse/keyboard input, built on the safety foundation above.
**Ten tools** ship:

- **`desktop_type`** — type Unicode text into the focused element (or a `@ref` target). Capped at
  4096 characters per call (`InvalidArguments` over the cap; split on a surrogate-safe boundary).
  Keystrokes are **paced by default** (`interKeyDelayMs=15`) so slow/async consumers
  keep up with fast input; the foreground is re-verified before *each* key, so a mid-type focus-steal
  still aborts. Pass `interKeyDelayMs=0` for a single atomic blast. **Note:** pacing does **not** cure
  the Windows 11 Notepad autocomplete garble (it corrupts synthetic input at any pacing); for
  reactive/autocomplete editors prefer a non-keystroke path (`desktop_set_value`, or clipboard paste
  for editors without `ValuePattern`). The garble is now flagged automatically by `desktop_type`'s
  `verify` (below).

  When the target window isn't the OS foreground, `desktop_type` no longer aborts with the generic
  `ElementDisappearedDuringAction` for that specific cause — it flashes the window and returns (via the
  normal response, not an error) `targetNotForeground: { targetWindow, currentForeground:{handle,process},
  recommendedAction:"call-wait-for-foreground", recovery }`. `currentForeground` is leak-safe (process
  name only; a `title` only for a modal owned by the exact target window). The generic
  `ElementDisappearedDuringAction` still fires for a genuine mid-send focus steal. See
  `desktop_wait_for_foreground` below for the recommended recovery.
  
  `desktop_type` takes an optional `verify` (bool, default `true`). When on, it reads the element back after typing and returns a `verify` object:
  - `{ ran, verified, mismatch }` — always present.
  - On a clean match: `verified:true`.
  - On a mismatch: `mismatch:true` with `expected`, `actual` (both truncated to 256 chars), a `canSetValue` wire fact indicating whether the target has a writable ValuePattern, and a `recommendedFallbackTool` that branches to `desktop_set_value` (if `canSetValue:true`) or the clipboard-paste path (`desktop_clipboard_set` → `desktop_key "Ctrl+V"`, if no ValuePattern), plus a human-readable `remedy`.
  - When it can't assert (empty-field precondition not met, no readable TextPattern, read failed, or a password/redacted field): `verified:false, mismatch:false, reason:"…"`.
  - `reason` is an **open** string — treat unknown values as forward-compatible; branch machines on `recommendedFallbackTool`, never parse `remedy`.
  
  Mismatch is **advisory** — `ok` stays `true` and nothing is retried or corrected. **For reactive / RichEdit editors (the new Win11 Notepad), prefer `desktop_set_value` (UIA ValuePattern) when the target has one, or `desktop_paste_text` otherwise** — `SendInput` can garble those editors at any pacing. Pass `verify=false` for the old fire-and-forget speed (skips a ~100 ms settle + two reads).
- **`desktop_paste_text`** — paste text into a `@ref` target via an atomic clipboard-backed Ctrl+V,
  the reliable path for reactive editors that garble `desktop_type` keystrokes (new Win11 Notepad,
  Chromium `contenteditable`). All input gates (lease/deny-list/budget/session) are checked **before**
  the clipboard is touched, so a paste that would be refused never clobbers it. Clipboard restore is
  **best-effort**: the prior clipboard is put back only when the paste is confirmed to have landed
  (`clipboardRestored:"restored"`); otherwise it reports `clipboardRestored:"abandoned"` and leaves
  your pasted text sitting on the clipboard — this is expected whenever `verify=false` (no landing
  check is done) and in reactive editors that transform pasted text so the read-back can't confirm a
  match. A clipboard already holding non-text content (image/files) is refused
  (`ClipboardHoldsNonText`) unless `forceOverwriteClipboard=true`; a mixed text+rich clipboard is
  restored as plain text (`clipboardRestored:"text-degraded"`). The guarantees are the **paste** and
  the **safety** (no leak, no wrong text, no clobber on a refused paste) — restore is a courtesy, not
  a promise.
- **`desktop_key`** — send a key chord (e.g. `ctrl+a`, `enter`, `alt+f4`) to the focused window, or
  to a `@ref`/`window` target. `ref` without `window` is `InvalidArguments`. Shares the same
  `targetNotForeground` handshake as `desktop_type` above when the target isn't foreground.
- **`desktop_wait_for_foreground`** — flash a `window` then block (up to `timeoutMs`, default
  `45000`, server-capped) until it gains OS foreground, is closed, or times out; returns
  `{ foregroundGained, reason:"gained"|"timeout"|"window-destroyed", currentForeground }`. On a
  `"timeout"` result, re-invoke rather than yielding the turn. **Lease-exempt** (works even while
  input is locked) and **single-waiter** (max 1 concurrent wait per server). This is the recommended
  recovery for the `targetNotForeground` handshake above and for `desktop_focus_window`'s
  `recommendedAction`. Clicks (`desktop_click`/`desktop_click_at`/`desktop_drag`) are unaffected by
  the foreground-lock handshake — a click activates the window, so it's a remedy, not a victim of it.
- **`desktop_click`** — click a `@ref` element by its hit-test point.
- **`desktop_click_at`** — click an absolute window-relative point (`xPct`/`yPct`).
- **`desktop_drag`** — press-move-release between two points; **both** endpoints are deny-list
  checked and the end point is re-hit-tested before the button releases.
- **`desktop_input_status`** *(read-only)* — report the current lease state
  (`active`/`locked`, seconds remaining, whether the `shells` capability is held). Never exposes
  the SID or any payload.
- **`desktop_set_caret`** / **`desktop_select_text_range`** — move the caret / select a character
  span via UIA `TextPattern`. These do **not** use `SendInput`, so they are **exempt from the
  lease, session-active, and budget gates** — but the deny-list and terminal/console interlock
  still apply (you cannot drive a credential dialog or a shell this way without the `shells`
  capability).

`desktop_set_caret`, `desktop_select_text_range`, `desktop_type`, `desktop_paste_text`,
`desktop_click`, and `desktop_key` all accept a `selector` as an alternative to `ref` (`desktop_key`
takes at most one, omitting both targets the foreground window) — see
[Targeting: `ref` or `selector`](#targeting-ref-or-selector) below.

**The out-of-band lease is the required enabler.** None of the five `SendInput` tools fire
unless a human has granted a live lease out-of-band:

```bash
flaui-mcp unlock --minutes 5 [--allow-shells]   # grant a time-bounded synthetic-input lease
flaui-mcp lock                                   # revoke it immediately
```

The agent cannot grant, extend, or read the SID of its own lease; it expires automatically. Use
`desktop_input_status` to check how much time remains.

**Leases longer than 60 minutes require an explicit risk acknowledgment.** `unlock --minutes N` for
`N > 60` prints an honest warning that the server provides **no sandboxing** and requires typing
`'I understand'` interactively, or passing `--accept-risk` (alias `--i-understand`) non-interactively;
without a TTY and without the flag, a long lease is refused outright. Leases of 60 minutes or less are
unchanged.

**The session must stay active and unlocked.** `SendInput` cannot reach a locked or
RDP-disconnected desktop — those calls return `InputDesktopUnavailable` rather than silently
dropping keystrokes. Keep the RDP/console session connected and the workstation unlocked while an
agent drives input.

**Elevation is still hard-refused** unless `--unsafe-allow-elevation` is passed at launch.

**Honest boundary:** the lease and deny-list defend against the agent driving *high-risk* sinks,
not against a determined same-user host shell — anything running as your user can already act as
your user. This is a guardrail for an agent, not a sandbox.

#### Ref resolution: safe by default

A ref (`e23`) captured from a snapshot is re-resolved when you act on it — **strict on state-changing
tools**, **lenient on reads**, and it never silently binds a different control than the ref pointed at:

- **State-changing tools** (`desktop_invoke`, `desktop_set_value`, `desktop_toggle`, `desktop_expand`,
  `desktop_select`, `desktop_set_focus`, `desktop_scroll`, `desktop_scroll_into_view`, `desktop_type`,
  `desktop_key`, `desktop_click`, `desktop_set_caret`, `desktop_select_text_range`, `desktop_paste_text`) require the **exact
  element** (matched by UIA RuntimeId). If it was destroyed and recreated (e.g. a virtualized row
  recycled its AutomationId), the action is **refused** with `REF_STALE_UNRESOLVABLE` — never
  retargeted. Take a fresh `desktop_snapshot`. (Legacy Win32 apps with no stable UIA identity: use
  `desktop_click_at`.)
- **Read tools** (`desktop_get_text`, `desktop_get_grid_cell`, snapshots) re-bind within the element's
  original container. `AMBIGUOUS_MATCH` on a duplicate AutomationId/Name (or duplicated ancestor
  container); `REF_STALE_UNRESOLVABLE` if the container is gone or identity can't be re-verified — it
  never guesses. Re-snapshot and pick a specific ref.

**Operator override:** set `FLAUI_MCP_REF_STRICT=off` (read at startup) to force lenient resolution
globally as a break-glass for apps with too-volatile UIA identity — this **disables the INV-8 guard**,
so use it only when strict resolution blocks a legitimate workflow. `FLAUI_MCP_REF_MAXSCOPES` (default
512) tunes the ancestor fan-out cap.

#### Targeting: `ref` or `selector`

Every state-changing interaction tool above, plus the six ref-taking `InputTools`
(`desktop_set_caret`, `desktop_select_text_range`, `desktop_type`, `desktop_paste_text`,
`desktop_click`, `desktop_key`) and the ref-taking `ContentTools` reads (`desktop_get_text`,
`desktop_get_grid_cell`, `desktop_grid_select`) — 17 tools in all — accept an optional `selector`
alongside the existing `ref`. **Exactly one of the two is required** (`InvalidArguments` if you
pass both or neither); `desktop_key` is the one exception, taking **at most one** — omitting both
targets the current foreground window. Coordinate-only tools (`desktop_click_at`, `desktop_drag`)
and the window-level `desktop_window_transform` did not gain a selector.

```
selector: { automationId?, name?, nameMatch?, controlType?, scope?, ignoreCase? }
```

- `automationId` / `name` (`nameMatch`: `eq` default | `contains`) / `controlType` — the same
  fields as `desktop_find`; at least one is required (`InvalidArguments` otherwise, checked before
  any UIA walk).
- `scope` — an existing `eN` ref narrowing the search to that element's subtree.
- `ignoreCase` — defaults **true** on `selector` (ergonomic: `"Submit"` matches `"submit"`);
  defaults **false** on `desktop_find` (back-compat, unchanged ordinal matching). Preview how a
  selector will match with `desktop_find(ignoreCase:true, ...)` before committing to it; if a
  selector's name genuinely collides across a `Submit`/`submit` pair, set `ignoreCase:false` on the
  selector to disambiguate.

A selector is resolved **fresh, on the action thread, at the moment of the call** — there is no
stale snapshot ref to go bad, and no re-`desktop_snapshot` churn to keep a target valid across
turns. Resolution requires **exactly one match**:

- **0 matches** → `SelectorNoMatch` — the target isn't present right now (reveal it, or check the
  selector fields).
- **>1 matches** → `AmbiguousMatch` — refine with a more specific `controlType`/`automationId`, add
  `scope`, try `ignoreCase:false`, or fall back to a `desktop_snapshot` ref.
- Exactly 1 → the tool acts (or reads) and the response includes `resolvedElement:"eN"` — a
  **freshly minted ref** for the element that was just resolved.

**`resolvedElement` durability caveat:** treat it like any other ref — it dies on the next
re-walk/snapshot of that window. Reuse it for an *immediate* follow-up call only; don't hold onto
it across turns the way you might a snapshot ref.

**A selector that's too broad** (e.g. bare `controlType:"Button"` on a large, otherwise
unconstrained tree) is refused `InvalidArguments` ("selector too broad") rather than allowed to
walk the tree unbounded — a safety cap on the action thread, not a bug.

**Honest limitation:** a selector's payoff scales directly with `automationId` coverage. A control
with a stable `automationId` resolves cleanly every time and survives snapshot churn — that's the
whole point. A control with **no** `automationId` and a **non-unique** `name` degrades to
`AmbiguousMatch`, exactly like a duplicate-name `desktop_find` would — fall back to
`desktop_snapshot` + a specific `eN` for those. `selector` is a complement to `ref`, not a strict
upgrade — use whichever is more stable for the control you're targeting.

### Read-only mode

Start the server with **`--read-only-mode`** to refuse every state-changing tool — all the
interaction tools above, `DesktopGridSelect`, `DesktopClipboardSet`, plus launch/focus/close. They short-circuit to `WriteBlockedReadOnly`
without touching the desktop, while perception and enumeration keep working. Use it for an agent
that may *see* the desktop but not *act* on it.

### Perception safeguards (built in)

`DesktopSnapshot` and `DesktopScreenshot` read UI into the agent's context, so they ship with
privacy and safety floors — defense in depth, not a substitute for supervising the agent:

- **Credential stores are blocked.** Snapshotting a window owned by a known password manager
  (1Password, Bitwarden, KeePass, and similar) is rejected outright (`TargetDenied`).
- **Password fields are always redacted — in the tree *and* in pixels.** Any UIA password field
  renders as `[REDACTED]` in a snapshot; in a screenshot it is painted over with an opaque black
  rectangle at capture time (covering popups/menus too), so typed secrets leak through neither
  channel. A full-desktop screenshot is *refused* (`TargetDenied`) if a credential-store window is
  visible — capture a specific window instead.
- **Structured content tools inherit the same protections.** `DesktopGetGridCell` and
  `DesktopGetText` apply the credential-store denylist and always mask `IsPassword` fields as
  `[REDACTED]`. The clipboard layer (`DesktopClipboardGet`) cannot redact — see the exfil caveat
  in the tool table above.
- **Screenshots detect a dead session.** If the desktop is locked or RDP-disconnected (so the
  framebuffer would be black), `DesktopScreenshot` returns `CaptureUnavailable` rather than a black
  image.
- **Off-screen elements are culled by default.** A snapshot reflects what the user can see; pass
  `includeOffscreen` to reach scrolled-off-but-real elements.
- **Never run elevated.** The server warns (on stderr) if started with Administrator rights — it
  is meant to run at your user integrity level.

**On the roadmap** (see [`ROADMAP.md`](../ROADMAP.md)): a "driving FlaUI.Mcp" dogfood skill,
occlusion-aware capture (PrintWindow), AOT/trim to shrink the self-contained executable, and an
HTTP transport.

### Event streaming (`desktop_watch`)

React to desktop changes instead of polling snapshots in a loop. **Four tools**, all `ReadOnly`
and lease-exempt (they synthesize no input):

| Tool | Description |
| --- | --- |
| `DesktopWatch` | Subscribe to UIA events on a window: `window_opened`, `window_closed` (child dialogs/popups), `focus_changed` (input focus moves within that window's process), `structure_changed` (subtree repopulated — coalesced/debounced). Optional `scope=<ref>` narrows `structure_changed` to a subtree. Returns `{subscriptionId, window, events, scope?}`. |
| `DesktopUnwatch` | Stop a subscription. Idempotent — an unknown/already-ended `subscriptionId` returns `ok:true`. |
| `DesktopListWatches` | List your active subscriptions (recover them after a context loss). Returns `watches[{subscriptionId, window, events, scope?, droppedCount}]`. |
| `DesktopDrainEvents` | Fetch and clear buffered events for a subscription. Returns `{subscriptionId, events:[…], count, droppedCount}` (`droppedCount` = summed coalescer + buffer evictions for this subscription — `>0` means you missed some state, re-`desktop_snapshot` to resync). |

Events are delivered as MCP server→client notifications (method
**`notifications/flaui/desktop_event`**) over the existing **stdio** pipe — there is no HTTP/SSE
transport involved. Each notification (and each event returned by `DesktopDrainEvents`) has the
same payload shape:

```
{ subscriptionId, event, window, ref?, controlType?, name?, bounds?, coalescedCount, timestampUtc }
```

`ref`/`name`/`bounds` may be absent (e.g. `window_closed`); `name` is `[REDACTED]` for password
fields (the same INV-5 redaction as `DesktopSnapshot`).

**Push+drain, not push-only.** Some MCP hosts — including Claude Code today — do not surface
unsolicited server→client notifications back to the model. `desktop_watch` therefore *also*
buffers every event server-side; **`desktop_drain_events` is the reliable path in hosts that
don't surface push notifications.** Don't rely on both in the same host — a host that does
surface push would otherwise see each event twice.

Other things to know:

- **Coalescing/back-pressure.** `structure_changed` bursts are coalesced and debounced;
  `coalescedCount` on a payload tells you how many raw events were folded into it. A `droppedCount
  > 0` on `DesktopListWatches` means the buffer overflowed under load — re-`desktop_snapshot` to
  resync rather than trusting the stream to be complete.
- **Refs are ephemeral.** An event's `ref` is minted into a small bounded pool — it can return
  `REF_NOT_FOUND` if you wait too long to act on it (drained or notified). Re-`desktop_snapshot` for
  a durable ref.
- **Auto-evict on close.** A subscription is torn down automatically when its window closes (reuses
  the Phase-6 `WindowInvalidated` chokepoint) — no leaked subscriptions to clean up by hand.
- **Caps:** 5 watches per window, 20 per session (`TooManyWatches` beyond that).
- **Self-trigger warning.** Your own `desktop_type`/`desktop_click`/`desktop_key` calls fire UIA
  events too — an event arriving right after your own input is likely self-caused; correlate by
  timing rather than assuming an external change.

### Opaque apps: wake + find_text

Not every window gives up an accessibility tree or visible text for free. Three tiers, cheapest first:

1. **Rich UIA out of the box (WinUI 3 / WPF / WinForms / Qt, most native apps):** `desktop_snapshot`
   works directly — everything above (patterns, refs, structured reads) applies as-is.
2. **Opaque Chromium/Electron (VS Code, Slack, Teams, Discord, Chrome):** `desktop_snapshot` returns
   one big empty `Document` node and — when it detects a Chromium Win32 class with a collapsed tree —
   sets `wakeable:true`. Call **`desktop_wake_accessibility(window)`** to activate and **HOLD** that
   window's native UIA tree, then re-`desktop_snapshot` / `desktop_find` / interact as usual. The wake
   is held until **`desktop_release_accessibility(wakeId)`** or the window closes; Chromium
   re-collapses the tree **lazily** once idle after release, not necessarily immediately.
   **`desktop_list_wakes()`** recovers active wakes after a context loss. Even while woken, an
   editor's **document text body** can stay behind a screen-reader gate — fall through to tier 3 for
   that residual case.
3. **Zero-accessibility surfaces (games, canvas apps, Citrix/RDP inners, an editor's text body that
   stays gated even when woken):** UIA has nothing to offer. Use **`desktop_find_text(query, window,
   region?, matchMode?, all?)`** — on-box OCR (`Windows.Media.Ocr`) that returns every matching
   visible text run as `{text, confidence, bounds, center, xPct, yPct}` (both physical screen px and
   `desktop_click_at` window fractions), fuzzy by default. **`desktop_wait_for_text(query, window,
   region?, timeoutMs?)`** polls for text to appear (`{satisfied:false}` on timeout, not an error;
   throttled to ≥750ms between OCR passes). **OCR here is targeting, not reading** — it resolves
   visible text to click coordinates; the model already reads the screenshot. A fuzzy query can match
   inside body text (`"Click Submit below"` matching a query for `"submit"`), so inspect each match's
   `text`/`bounds` before `desktop_click_at`. `OcrUnavailable` if no Windows OCR language pack is
   installed.

| Tool | Read-only | Description |
| --- | --- | --- |
| `DesktopWakeAccessibility` | ✅ | Activate and hold an opaque Chromium/Electron window's native accessibility tree. Returns `{wakeId, window, alreadyAwake}`; idempotent per window; auto-releases when the window closes. Capped at 32 wakes/session (`TooManyWatches`). |
| `DesktopReleaseAccessibility` | ✅ | Release a held wake. Returns `{ok, wakeId}`; idempotent (unknown/already-released `wakeId` still returns `ok:true`). |
| `DesktopListWakes` | ✅ | List active wakes: `{wakes:[{wakeId, window}]}`. |
| `DesktopFindText` | ✅ | OCR a window/region for text matching `query`. Returns `{matches:[{text, confidence, bounds, center, xPct, yPct}]}`, best match first. Fuzzy by default; `all` (default true) returns every occurrence. |
| `DesktopWaitForText` | ✅ | Poll with OCR until `query` appears or timeout. `{satisfied:false}` on timeout (data, not error); `{satisfied:true, match:{...}}` on success. Throttled to ≥750ms between passes. |

All five tools are `ReadOnly` and lease-exempt (they synthesize no input).

### Electron / Chromium & other custom-render apps

Not every app exposes a clean accessibility tree. Honestly:

- **Electron / Chromium (VS Code, Slack, Discord, Teams, …):** Chromium keeps its accessibility
  tree **off by default**, so a snapshot is usually **one opaque `Document` node with no children**
  (and, when detected, `wakeable:true`). When you see that, call **`desktop_wake_accessibility`**
  (see [Opaque apps: wake + find_text](#opaque-apps-wake--find_text)) and re-snapshot instead of
  hunting for inner refs — most Chromium/Electron chrome hydrates fully. Reserve the **coordinate
  path** (`desktop_click_at` / `desktop_drag` by `xPct`/`yPct`) or `desktop_find_text` for surfaces
  that stay gated even when woken (a document's own text body, canvas-rendered content). Typed text
  into Chromium editors (Monaco, CodeMirror, `contenteditable`) can **garble** like the new Notepad;
  `desktop_type`'s `verify` flags it, but `desktop_set_value` often **isn't available** there (no
  `ValuePattern`) — the reliable path is **`desktop_paste_text`** (atomic clipboard-backed Ctrl+V;
  clipboard restore is best-effort — see [Synthetic input](#synthetic-input)).
  - **Escape hatch:** launch the specific app with **`--force-renderer-accessibility`** (edit its
    shortcut / launch args) and Chromium exposes its full UIA tree for that process.
- **WinUI 3 / WPF / WinForms / Qt:** generally expose **proper UIA peers** out of the box — this
  caveat mostly does **not** apply (a custom-drawn control with no UIA peer is the exception).
- **Zero-UIA surfaces (games, canvas, Citrix/RDP inners):** the coordinate + screenshot path still
  works; **`desktop_find_text`** (see [Opaque apps: wake + find_text](#opaque-apps-wake--find_text))
  resolves visible text to click coordinates via OCR without needing a UIA tree at all.

Everything above degrades **safely** — the foreground/hit-test re-verify, lease, and deny-list mean
a limited surface **aborts or no-ops**; it never mis-fires into the wrong window.

## How it compares to WebDriver-based test automation (e.g. Appium)

If you've automated Windows UIs before, it was probably with **WebDriver-based test automation** —
Appium driving the Windows Application Driver (WinAppDriver). FlaUI.Mcp and that stack both control
Windows apps through UI Automation, but they're built for **different jobs**, and it's worth being
clear which one you actually want.

**The intent divide (the important part).** WebDriver-based tools exist for **deterministic test
automation**: a test author writes an explicit script, runs it in a CI pipeline, and expects the
same steps to pass or fail the same way every time. FlaUI.Mcp exists for **non-deterministic AI
agents**: a model decides at runtime what to look at and what to do next, over MCP. These are
largely **non-competing niches** — one serves test suites, the other serves agents. Writing a
regression suite? Reach for the WebDriver stack. Giving an agent eyes and hands on the desktop?
That's this.

**What's similar**

- Both drive Windows apps through **UI Automation** (FlaUI.Mcp via UIA3/FlaUI; appium-windows-driver
  wraps WinAppDriver, also UIA-based).
- Both **enumerate windows, read element properties, screenshot, and interact** with controls.
- Both can **synthesize mouse/keyboard input** — though FlaUI.Mcp's is lease-gated (a human unlock is
  required), while Appium's fires immediately.

**What's different**

| Aspect | FlaUI.Mcp | WebDriver-based (Appium + WinAppDriver) |
|--------|-----------|------------------------------------------|
| **Consumer** | Non-deterministic AI agents | Test authors / CI pipelines |
| **Protocol** | Model Context Protocol (stdio JSON-RPC) | WebDriver protocol (HTTP) |
| **Shape** | A tool surface an agent calls ad hoc | A scripted client session |
| **Safety** | Time-lease, deny-lists, per-window budget, credential redaction | No equivalent guardrails |
| **Ecosystem** | MCP clients (Claude Code, Antigravity, generic) | Selenium/Appium language bindings, large community |
| **Packaging** | Single self-contained exe, no runtime | Appium server + driver + client stack |

**It is *not* a drop-in Appium replacement.** FlaUI.Mcp speaks **no WebDriver protocol**, ships **no
language-binding ecosystem** (the Java/Python/C#/JS Selenium clients), and is **not a test
framework** — no assertions, no test runner, no page objects. If you have an Appium suite, this does
not run it. It's a different tool for a different consumer.

**Rough guide:** if you're giving an AI agent supervised control of the Windows desktop — with safety
rails and credential redaction — FlaUI.Mcp is built for that. If you need deterministic UI test
automation, cross-platform coverage (Appium also drives macOS/Linux/Android/iOS), or you already have
a WebDriver investment, the Appium stack is the established choice.
