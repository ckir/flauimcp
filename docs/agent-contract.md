# Agent Contract

This document defines the RPC surface an agent consumes to drive the Windows desktop.

## Targeting: `ref` vs `selector`

Many interaction and content tools accept either a `ref` (from a snapshot) or a `selector`. **Exactly one is required** (`desktop_key` takes at most one).

**`ref` (e.g. `e23`):**
- Bound to an element within a `desktop_snapshot`.
- Re-resolved on use: state-changing tools require the exact UIA RuntimeId. Read tools fall back to binding within the element's original container.
- If the element was destroyed and recreated (e.g. a virtualized row recycled its AutomationId), the action is refused with `RefStaleUnresolvable`. Take a fresh snapshot.
- A `desktop_wait_for_stable` call that returns `stable:true` ends by taking a whole-window snapshot, which supersedes the window's refs — so a `scopeRef` passed to it is dead afterwards, and reusing it returns `RefNotFound`. Recover cheaply with `desktop_find` (targeted, and it registers matches **additively** without superseding — `PerceptionManager.cs:478`, `:623`), not another `desktop_snapshot`. A timeout (`stable:false`) does not supersede refs, so `scopeRef` survives it.

**`selector`:**
- Schema: `{ automationId?, name?, nameMatch?, controlType?, scope?, ignoreCase? }`.
- Resolved fresh on the action thread. Survives snapshot churn.
- Requires exactly one match:
  - 0 matches: `SelectorNoMatch`.
  - >1 matches: `AmbiguousMatch`. Refine the selector or fall back to `ref`.
  - 1 match: The tool acts and returns a freshly minted `resolvedElement` ref for immediate follow-up. Do not hold this ref across turns.

## Common parameters

- **`timeoutMs`:** Interaction and input tools accept a block timeout in milliseconds (default 4000); the wait tools take a total budget (default 5000). The server caps an action's timeout at 60000. See **Retry and recovery** before raising it.
- **`overlay`:** Mutative tools accept `overlay: true` (or `<milliseconds>`) to draw the red intent overlay before acting (visibility aid).
- **`verify`:** Synthetic-text tools (`desktop_type`, `desktop_paste_text`) perform an advisory read-back. They return a `verify` object; a mismatch is advisory and does NOT throw an error. Exact shape:
  ```json
  {
    "ran": true,
    "verified": false,
    "mismatch": true,
    "reason": "string (optional)",
    "expected": "string (optional)",
    "actual": "string (optional)",
    "canSetValue": true,
    "recommendedFallbackTool": "desktop_set_value | desktop_paste_text",
    "remedy": "string (optional prose advice)"
  }
  ```

## Tool Catalog

### Window Lifecycle
| Tool | Access | Description & Key Parameters |
|---|---|---|
| `desktop_list_windows` | ReadOnly | List top-level desktop windows. Opt-ins: `includeBounds`, `includeHandles`. A terminal tab title names the **launcher, not the program** — a tab titled `cmd.exe` may host a running CLI agent, so enumerate tabs and read every candidate before concluding a program is absent. |
| `desktop_open_window` | ReadOnly | Open window by pid/title, returns handle (e.g. `w1`). |
| `desktop_launch_app` | Destructive | Launch app, returns handle. Blocked in `--read-only-mode`. |
| `desktop_focus_window` | Destructive | Bring window to foreground. Blocked in `--read-only-mode`. |
| `desktop_close_window` | Destructive | Close window, free handle. Blocked in `--read-only-mode`. |

### Perception
| Tool | Access | Description & Key Parameters |
|---|---|---|
| `desktop_snapshot` | ReadOnly | Walk tree into ref-tagged snapshot. **Params:** `root` (subtree scope), `maxDepth`, `interactiveOnly`, `fullProperties`, `includeOffscreen`. |
| `desktop_snapshot_diff` | ReadOnly | Diff CURRENT tree against baseline `snapshotId`. **Params:** `scope` (subtree ref). |
| `desktop_snapshot_stats` | ReadOnly | Control counts. **Params:** `snapshotId` (offline stats) OR `window`. ⚠ The `redacted` field is now `osPasswordCount` (v1.0.0 BREAKING). |
| `desktop_get_focused_element` | ReadOnly | Return UIA-focused element's ref and descriptor. ⚠ `window.title` is the OWNING WINDOW's title. Before v1.0.0 it carried the focused ELEMENT's name — a silent BREAKING change, see the CHANGELOG. The element's name is in `descriptor`. |
| `desktop_find` | ReadOnly | Query window for refs without full walk. **Params:** `automationId`, `name`, `nameMatch`, `controlType`, `enabledOnly`, `max`, `scope`, `ignoreCase`. |
| `desktop_screenshot` | ReadOnly | PNG capture. Masks redacted elements (see **Redaction on the wire**). **Params:** `window`, `ref`, `maxWidth` (default 1600). Metadata carries `maskEscalations` + `escalated` + `unmaskedProcesses`; can fail `RedactionUnmaskable`. |
| `desktop_get_bounds` | ReadOnly | Get absolute screen bounds, dpiScale, isOffscreen status. |
| `desktop_wait_for` | ReadOnly | Poll until selector condition holds. **Params:** `by`, `value`, `until`, `equals`, `pollIntervalMs`, `timeoutMs`, `includeOffscreen`. |
| `desktop_wait_for_stable` | ReadOnly | Poll until tree stops changing. **Params:** `by`, `value`, `includeText`, `quietMs`, `pollIntervalMs`, `timeoutMs`, `scopeRef`, `includeOffscreen`. |
| `desktop_user_state` | ReadOnly | Report coarse human presence. Lease-exempt. |
| `desktop_wait_for_foreground` | ReadOnly | Block until window gains foreground. **Params:** `timeoutMs`. Lease-exempt. |

### Redaction on the wire

Content is withheld for two independent reasons, and the payload tells you which:

| Field | Where | Meaning |
|---|---|---|
| `redacted` | snapshot nodes, `desktop_get_text`, `desktop_get_grid_cell` | `true` ⇒ the content was withheld. **Use this**, not `isPassword`. |
| `redactedBy` | same | `"os"` (UIA password field), `"rule:<name>"` (operator rule), or `"unreadable"` (rules are configured but the element's identity could not be read, so it was withheld rather than guessed at). Absent when not redacted. |
| `isPassword` | snapshot nodes | **Deprecated signal.** Kept with its original meaning — OS password fields **only** — so existing consumers do not change behaviour. It is `false` for rule-redacted elements. Read `redacted` instead. |
| `redactedCount` | `desktop_snapshot_stats` | Count of **all** redacted nodes. |
| `osPasswordCount` | `desktop_snapshot_stats` | Count of OS password nodes **only**. A different number from `redactedCount`. Called `redacted` before v1.0.0. |

Withheld content reads `[REDACTED]`. This is uniform across the snapshot tree, diffs, text reads, grid cells, watch events and terminal tab titles; screenshots mask the pixels.

**When a screenshot cannot mask.** If a redacted element cannot report usable bounds, its mask is taken
from an ancestor: `maskEscalations` counts those **elements** (not levels climbed) and `escalated` lists
their `{automationId, controlType}` so you can tell which control has a broken provider — never their name.
If no ancestor is usable either, the capture is **refused** with `RedactionUnmaskable` rather than returning
an all-black image you would be tempted to interpret.

⚠ **This lands differently on deep and flat trees, and the difference is expected.** On WPF / Electron /
modern XAML a redacted control usually sits many levels down, so one unreadable rect degrades gracefully —
a local container is masked and you still get a usable screenshot. On classic Win32 dialogs, where controls
are direct children of the window, the parent IS the root, so one unreadable rect refuses the whole capture
on the first escalation step. If screenshots of one particular older app always fail this way, that is the
mechanism, not a bug — capture a different window, or use `desktop_snapshot` for that one.

**When a full-desktop screenshot could not even look at a window.** `unmaskedProcesses` lists the
processes whose windows contributed **no** masks — usually an ELEVATED one, which a non-elevated UIA client
cannot bind at all, or a window that closed mid-capture; the two are indistinguishable. It is always
present and is empty for window/element scope. ⚠ **A non-empty list means the image is NOT fully
redacted:** those windows' pixels are in it and their redactions are not, so do not treat such a capture as
exhaustively masked. A visible DENYLISTED credential window still refuses the whole capture outright, which
is a separate and stronger guard.

⚠ **A redacted element cannot be found by NAME** — not by its real name, and not by `"[REDACTED]"`. Confirming a guessed name is itself a leak, so name search excludes it even though no value crosses the wire. It remains findable by `controlType` / `automationId`, keeps real bounds, and its ref resolves normally. **To act on a redacted field, target it by `automationId` or `controlType`, then use the ref** — typing into a password box still works.

### Content & Clipboard
| Tool | Access | Description & Key Parameters |
|---|---|---|
| `desktop_get_grid_cell` | ReadOnly | Read one grid/table cell by `(row,col)`. |
| `desktop_grid_select` | Destructive | Select cell by `(row,col)`. |
| `desktop_get_text` | ReadOnly | Read element's text. **Params:** `selectionOnly`, `maxLength`, `fromEnd`. |
| `desktop_read_terminal_tab` | Destructive | Read terminal tab. **Params:** `tabIndex`, `restoreFocus`, `fromEnd`, `maxLength`. The tab title names the **launcher, not the program** — read every candidate tab before concluding the program you want is not running. |
| `desktop_list_terminal_tabs` | ReadOnly | List a Windows Terminal window's tabs without selecting any. **Params:** `window`. Returns `{ tabs: [{ index, title, active }], activeTabIndex }`; `activeTabIndex` is `-1` when no tab reported itself selected. Use this to learn the `tabIndex` `desktop_read_terminal_tab` needs — a `desktop_snapshot` ordinal is **not** usable as a `tabIndex` (the walk drops off-screen, culled, and too-deep nodes). Titles only — reading a background tab's buffer still needs `desktop_read_terminal_tab`. |
| `desktop_clipboard_get` | ReadOnly | Read system clipboard as text. |
| `desktop_clipboard_set` | Destructive | Write text to clipboard. |

### Interaction (UIA Patterns)
State-changing tools that drive the app's automation providers (no OS input). Blocked in `--read-only-mode`.

| Tool | Access | Description & Key Parameters |
|---|---|---|
| `desktop_invoke` | Destructive | Activate element (UIA InvokePattern). **Params:** `timeoutMs`, `overlay`. |
| `desktop_set_focus` | Destructive | Set keyboard focus (UIA Focus). **Params:** `timeoutMs`, `overlay`. |
| `desktop_set_value` | Destructive | Set value (UIA ValuePattern). **Params:** `timeoutMs`, `overlay`. |
| `desktop_toggle` | Destructive | Toggle element (UIA TogglePattern). **Params:** `timeoutMs`, `overlay`. |
| `desktop_expand` | Destructive | Expand/collapse element (UIA ExpandCollapse). **Params:** `timeoutMs`, `overlay`. |
| `desktop_select` | Destructive | Select element (UIA SelectionItem). **Params:** `timeoutMs`, `overlay`. |
| `desktop_scroll_into_view` | Destructive | Scroll element into view (UIA ScrollItem). **Params:** `timeoutMs`, `overlay`. |
| `desktop_scroll` | Destructive | Scroll container (UIA Scroll). **Params:** `timeoutMs`, `overlay`. |
| `desktop_window_transform` | Destructive | Maximize/minimize/restore window. |

### Synthetic Input
Real `SendInput`-backed actions. Require active lease, blocked in `--read-only-mode`.

| Tool | Access | Description & Key Parameters |
|---|---|---|
| `desktop_input_status` | ReadOnly | Report lease status. |
| `desktop_set_caret` | Destructive | Position caret. Lease-exempt. |
| `desktop_select_text_range` | Destructive | Select text range. Lease-exempt. |
| `desktop_type` | Destructive | Type text. **Params:** `interKeyDelayMs` (pacing), `verify` (read-back check), `overlay`. |
| `desktop_paste_text` | Destructive | Atomic Ctrl+V. **Params:** `verify`, `forceOverwriteClipboard`, `overlay`. |
| `desktop_key` | Destructive | Send keyboard chord. **Params:** `ref`/`selector` (focus element first), `overlay`. |
| `desktop_click` | Destructive | Click element. **Params:** `modifiers` (array of `Ctrl\|Alt\|Shift\|Win`), `overlay`. |
| `desktop_click_at` | Destructive | Click absolute/window-relative point. **Params:** `modifiers`, `overlay`. |
| `desktop_drag` | Destructive | Drag mouse. **Params:** `endWindow` (resolve end point in another window's [0,1] space for cross-window drag), `modifiers`, `overlay`. |

### Event Streaming (`desktop_watch`)
Subscribe to UIA events instead of polling. Lease-exempt.

| Tool | Access | Description & Key Parameters |
|---|---|---|
| `desktop_watch` | ReadOnly | Subscribe to events (`window_opened`, `focus_changed`, etc.). **Params:** `scope` (restrict to subtree). |
| `desktop_unwatch` | ReadOnly | Stop subscription. |
| `desktop_list_watches` | ReadOnly | List active watches. |
| `desktop_drain_events` | ReadOnly | Fetch buffered events. **Params:** `max` (limit count). |

### System Tray
The notification area as a driveable surface. Icons are identified by **tooltip substring only**: a tooltip is
app-chosen (any app can name its icon "Docker"), so always check `matchedIcon` in a reply.

| Tool | Access | Description & Key Parameters |
|---|---|---|
| `desktop_open_tray` | ReadOnly | Handle for the taskbar (`Shell_TrayWnd`), usable with snapshot/find/invoke. `includeHidden` also opens the hidden-icons overflow and returns `overflowHandle`, **left open** (`overflowLeftOpen: true`); it closes by itself when the foreground moves, after which the handle is `WindowHandleStale`. Tray handles refuse `desktop_close_window` / `desktop_window_transform` (`ElementNotActionable`). Lease-exempt. |
| `desktop_list_tray_icons` | ReadOnly | `{ icons: [{ name, location }], overflowRestored? }`, `location` = `visible` \| `system` \| `hidden`. `includeHidden` opens and restores the overflow. Lease-exempt. |
| `desktop_tray_action` | Destructive | `icon` (substring), `action` = `left_click` (UIA Invoke, lease-exempt) \| `right_click` (real input, **lease required**), `menuItem?`, `timeoutMs` (default 4000). Blocked in `--read-only-mode`. |

`right_click` returns one of three shapes:

| `menuKind` | When | Fields |
|---|---|---|
| `"menu"` | a UIA menu (WinForms/WPF) or a classic Win32 menu appeared | `matchedIcon`, `items: [{ text, enabled, hasSubmenu }]`, `selected?`, `dismissed`, `overflowRestored?`, `warning?` |
| `"window"` | the app opened its own titled window instead (OneDrive-style) | `matchedIcon`, `handle` (use snapshot/click on it), `warning?`, `overflowRestored?` |

`menuItem` matches exact text first, then substring. Disabled items and submenus are refused
(`ElementNotActionable`); the menu is dismissed with Escape on every error path. A menu owned by a credential-store
process is refused (`TargetDenied`) before any item is read.

**Tooltip redaction limit.** Icon names pass through the same redaction rules as a snapshot, evaluated against the
icons' real owner, **explorer**: a rule scoped to a password manager's process does **not** hide that manager's tray
tooltip; a global or explorer-scoped rule does. Redacted icons cannot be matched by `icon`.

**Out of scope (v1):** submenus, `double_click`, secondary-monitor taskbars, auto-hidden taskbars (the right-click is
refused `ElementNotActionable`), selecting inside a `"window"` result.

### Opaque-App Access
For apps lacking out-of-the-box trees. Lease-exempt.

| Tool | Access | Description & Key Parameters |
|---|---|---|
| `desktop_wake_accessibility` | ReadOnly | Hydrate Chromium/Electron native tree. |
| `desktop_release_accessibility` | ReadOnly | Release held wake. |
| `desktop_list_wakes` | ReadOnly | List active wakes. |
| `desktop_find_text` | ReadOnly | OCR text matching query. |
| `desktop_wait_for_text` | ReadOnly | Poll OCR until query appears. |

## Advanced Interactions

- **Cross-Window Dragging:** Use `desktop_drag` with `endWindow` to calculate the start coordinates relative to the source window and the end coordinates relative to a different window.
- **Targeted Keystrokes:** Pass a `ref` or `selector` to `desktop_key` to guarantee focus is set on a specific element immediately before sending the keystroke.
- **Subtree Diffing & Watching:** Pass `scope` (an element ref) to `desktop_snapshot_diff` or `desktop_watch` to constrain tree diffs and `structure_changed` events to a specific subset of the window, saving memory and CPU.

## Event Streaming Delivery

Events are buffered and can be fetched via `desktop_drain_events`, or delivered via push notifications (`notifications/flaui/desktop_event`) if the MCP client supports it. Do not rely on both in the same host.

Event payload shape:
```json
{ "subscriptionId": "s1", "event": "...", "window": "w1", "ref": "e123", "coalescedCount": 1, "timestampUtc": "..." }
```
- `structure_changed` bursts coalesce into ONE event carrying `coalescedCount`. Coalescing is **not** loss — the aggregate keeps the freshest meta. But it reports the common ancestor: a large burst names the ROOT window, not the subtree that changed. Scope the watch if you need WHERE.
- `droppedCount` counts **evictions only** (capacity or buffer), never coalescing. It stays `0` through a heavy burst — measured 3603 coalesced with `droppedCount: 0`. So `droppedCount > 0` means state was genuinely lost; re-snapshot to sync.
- An empty first `drain_events` is not evidence nothing happened — `structure_changed` is debounced, so poll again and cross-check with a snapshot.
- Event refs are ephemeral. Re-snapshot for a durable ref.
- Subscriptions auto-evict when the window closes.

## Opaque-App Access

1. **Chromium/Electron:** Return an empty `Document` node with `wakeable:true`. Call `desktop_wake_accessibility` to hydrate the tree, then snapshot. Hydration is a RAMP, not a step — poll `desktop_snapshot_stats` until the count crosses a threshold instead of sleeping once, or you sample mid-ramp and read it as a failed wake. The wake is HELD; once released Chromium re-collapses lazily. Page content, once loaded, is addressable by `name`/`automationId` — OCR is for surfaces that expose nothing, not for Chromium generally.
2. **Zero-accessibility surfaces (games, Citrix, RDP):** Use `desktop_find_text` for on-box OCR targeting. OCR resolves visible text to click coordinates. Fuzzy match can trigger false positives in body text. OCR returns `OcrUnavailable` if no Windows OCR pack is installed.

## Retry and recovery

Every tool error has the shape `{ "error": "<Code>", "message": "...", "suggestedRecovery": "..." }`. The
code is the PascalCase name shown below. Read `suggestedRecovery`: it is written for you.

A UI that does not respond to an action is one of two different things, and they need opposite handling:

- **The element is not ready**: not there yet, disabled, scrolled out of view, or replaced. Waiting fixes it.
  Wait for the condition, then act once.
- **The application is not answering**: a modal dialog opened, or the window is hung. Repeating the action
  does not help, and it can do the action twice.

### By error code

| Code | What it means | What to do |
|---|---|---|
| `SelectorNoMatch` | No element matches yet. | `desktop_wait_for` with `until: "exists"`, then act once. Do not loop the action. |
| `RefNotFound` | The ref is not in the window's current snapshot (a later snapshot superseded it). | Re-acquire with `desktop_find`. It adds refs without superseding your others. |
| `RefStaleUnresolvable` | The element is gone, or it was destroyed and recreated. | Re-acquire with `desktop_find` or a fresh snapshot. The same ref will never resolve again. |
| `AmbiguousMatch` | The selector matches more than one element. | Narrow the selector (`scope`, `controlType`), or use a ref. |
| `ElementNotActionable` | The element cannot be acted on as it is: off-screen, or a read-only value control. The message says which. | Off-screen: `desktop_scroll_into_view`, then retry once. Read-only: pick another control; retrying cannot help. |
| `ActionBlockedPending` | The action did not return within `timeoutMs`. Usually it opened a modal dialog that is now waiting for you. | **Do not repeat the action**: it has probably already happened. Snapshot the window, find the dialog, and act on the dialog. |
| `TooManyPendingActions` | Five actions are parked waiting for the UI, so the server refuses new ones. | Snapshot and dismiss the pending dialogs, then retry. If the window is genuinely hung, those slots are not freed while the server runs. Stop and tell the user. |
| `ElementDisappearedDuringAction` | Input tools only: the target lost the foreground, or the window under the pointer changed, just before input was sent. Usually nothing was sent. But a paced `desktop_type` (`interKeyDelayMs` > 0) checks before each character, so it can stop with part of the text typed, and a drag that aborts releases the button at its start point. | Check the field's current text before retrying a paced type. Then re-snapshot, re-focus the target, retry once. |
| `WindowHandleStale` | The window closed, or its HWND now belongs to another window. | `desktop_list_windows`, then `desktop_open_window` again, and take fresh refs from the new handle. |
| `InputDesktopUnavailable` | The session is locked, disconnected, or on a secure desktop (a UAC prompt). | Nothing to retry. Tell the user; input resumes only after they unlock or reconnect. |
| `Timeout` (`desktop_tray_action`) | No menu settled within `timeoutMs` after the right-click, or another tray call holds the tray lock. | Retry once with a larger `timeoutMs`; if the icon's app has no right-click menu, stop. |

A wait that runs out of time is **not** an error. `desktop_wait_for` returns `satisfied: false`, with
`unsatisfiedBecause` when it can say why (e.g. `outsideWindowBounds`), and `desktop_wait_for_stable` returns
`stable: false`. Decide from those fields; do not treat a timeout as an exception to retry.

### Rules

1. **Wait for a condition, never sleep and retry.** Before acting on a control that is not ready, use
   `desktop_wait_for` with `until: "exists"`, `"enabled"` or `"gone"`, or `"valueEquals"` with `equals`.
   Use `desktop_wait_for_stable` to let a busy window settle. Then act once.
2. **Never repeat a state-changing action blind.** After `ActionBlockedPending`, a dropped connection or
   any doubt, snapshot (or `desktop_get_text`) to see whether the action took effect. Invoking, clicking,
   typing and pasting are not idempotent.
3. **A larger `timeoutMs` does not cure a hung window.** It only keeps the call parked longer. The server
   caps an action's timeout at 60000 ms. Use a longer timeout for an app that is slow but responsive.
4. **`desktop_wait_for_stable` needs a scope on large trees.** Measured on a ~95-node window, one
   whole-window poll took ~4 s, so the default 5000 ms budget leaves room for about one poll. Pass
   `scopeRef` (a ref to the subtree that changes) and raise `timeoutMs`.
5. **Bound your retries.** Retry a step at most twice, each time after a wait or re-acquisition. Then stop
   and report what you saw (the error code, and what a snapshot or screenshot showed) rather than looping.

## Server log

The server writes a JSON Lines log of every tool call, on by default (`--no-log` turns it off). Read it to diagnose a failure after the fact.

- **Location:** `%LOCALAPPDATA%\flaui-mcp\logs\flaui-mcp-<pid>-<startUtc>-<seg>.jsonl`, or `FLAUI_MCP_LOG_DIR` if set. `<startUtc>` is `yyyyMMddTHHmmssZ`; `<seg>` is the segment number, at least two digits.
- **Reader rule:** read one process's segments in ascending **numeric** `<seg>` order (a plain name sort puts `-100` before `-99`). The newest segment can end in a torn line: skip an unparseable final line.
- **Pointer:** a tool error JSON may carry `log: "<cid> in <path>"`. Open that file and keep the records whose `cid` matches. The field is absent when logging is off.
- Records are one line each, at most 8 KB. An oversized record is shortened (stack frames, long strings, `msg`, then `args` replaced by `{"omitted": <bytes>}`) and carries `trunc: true`.

### Record fields

| Field | Meaning |
|---|---|
| `ts` | UTC time, `yyyy-MM-ddTHH:mm:ss.fffZ`. |
| `lvl` | `debug`, `info`, `warning` or `error`. |
| `pid` | Server process id. |
| `ev` | Event name (table below). |
| `cid` | Call id, `c-` + 8 hex digits. Shared by every record of one tool call. Absent outside a call. |
| `tool` | Tool name, e.g. `desktop_click`. Present with `cid`. |
| other | The event's own fields (table below). A field with no value is `null`. |

### Events

`*` marks UI text, which is masked in sensitive cases (see Masked values). `debug` events appear only under `--log-level=debug`; `--log-level=warning` keeps only `warning` and `error`.

| Event | Level | Fields |
|---|---|---|
| `server.start` | info | `version`, `flags` (names only, no values), `rulesSha`, `dpiAwareness`, `dpiScale`, `session` (`console`/`rdp`), `readOnly` |
| `server.stop` | info | `uptimeMs`, `calls`, `droppedTotal` |
| `tool.start` | info | `args` (object, per-parameter masking below) |
| `tool.end` | info if `outcome` is `ok`, else warning | `outcome` (`ok` or the error code), `ms`, `msg`* (errors only) |
| `tool.exception` | error | `exType`, `hresult`, `stack`, `msg`* |
| `window.bind` | info | `handle`, `hwnd`, `pid`, `process`, `title`* |
| `focus.attempt` | info | `target`, `gained`; when `gained` is false also `fgHwnd`, `fgHandle`, `fgProcess` |
| `action.blocked` | warning | `fgHwnd`, `fgHandle`, `fgProcess`, `fgTitle`* |
| `capture.decision` | info | `scope`, `ok`, `method`, `warnings` (codes), `code` |
| `ref.resolve` | info | `ref`, `found`, `controlType`, `automationId`*, `name`* |
| `selector.resolve` | debug | `path` (`selector` or `find`), `automationId`*, `name`*, `controlType`, `candidates`, then `chosen` (selector) or `returned` (find) |
| `wait.end` | debug | `kind` (`wait_for` or `wait_for_stable`), `condition`, `by`, `value`*, `ms`, `satisfied`, `code` |
| `input.decision` | info | `action`, `allowed`, `code`, `budgetLeft` |
| `log.dropped` | warning | `count` (records lost to a full queue; no `cid`) |

`ref.resolve.found` means the ref is registered for that window. `capture.decision.warnings` includes `ScrapeFallbackTargetUnresponsive` when the circuit breaker diverted the capture. A refused input shows `code`, e.g. `InputNotLeased`.

### Masked values

Content is never written. Where a value would appear, the log holds `{"len": <chars>, "sha8": "<8 hex>"}` instead. The hash is keyed per process, so equal values hash equal within one server process and differ across processes.

- `tool.start.args`: typed, pasted, set and clipboard text, and any parameter not known to be structural, are always masked. UI-text parameters (names, ids, selectors) are plain unless the call is sensitive.
- UI-text fields (`*`) are masked when the call targets a denylisted or rule-matchable window, when the event's own process is one, or when a call has no window and `--redaction-rules` is loaded.
- `msg` on `tool.exception`, and on an `INTERNAL` `tool.end`, is masked when the call carried content arguments.

Never in the log: typed, pasted, set or clipboard text, captured screen text, images, tool results.

## Known Tool Limitations

- Cannot drive elevated/Administrator apps (UIPI boundary blocks input).
- Input needs a connected, interactive, and unlocked desktop session (`InputDesktopUnavailable`).
- Reactive editors (Win11 Notepad, Chromium editors) garble `desktop_type` keystrokes. Prefer `desktop_set_value` or `desktop_paste_text`.
- Screenshots do not handle occlusion; target windows must be focused to guarantee full visibility.
- Credential stores (matched by process name) deny snapshot and grid cell reads.
- Elements with no `AutomationId` and no `Name` cannot be re-resolved after recycling. Fall back to `desktop_click_at`.
- Input tools abort with `ElementDisappearedDuringAction` if the target loses foreground mid-sequence (a TOCTOU guard).
- Tray icons carry no owner process: `desktop_tray_action` matches tooltips, and a redaction rule scoped to an app's process does not reach that app's tray tooltip (explorer owns the icon).
- Classic Win32 tray menus (menuKind "menu" from a native popup) are read through Win32 (GetMenuString), outside the UIA redaction path: item text is NOT passed through the redaction rules, unlike UIA menus and tooltips. A credential-store process's menu is still refused (TargetDenied) before any item is read.
- desktop_tray_action right_click on a hidden icon waits for the overflow's icons to stop animating (up to 1.5 s) and verifies through UIA that the click point belongs to the matched icon; if it cannot, it refuses (ElementNotActionable) rather than click.
