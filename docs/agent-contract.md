# Agent Contract

This document defines the RPC surface an agent consumes to drive the Windows desktop.

## Targeting: `ref` vs `selector`

Many interaction and content tools accept either a `ref` (from a snapshot) or a `selector`. **Exactly one is required** (`desktop_key` takes at most one).

**`ref` (e.g. `e23`):**
- Bound to an element within a `desktop_snapshot`.
- Re-resolved on use: state-changing tools require the exact UIA RuntimeId. Read tools fall back to binding within the element's original container.
- If the element was destroyed and recreated (e.g. a virtualized row recycled its AutomationId), the action is refused with `REF_STALE_UNRESOLVABLE`. Take a fresh snapshot.

**`selector`:**
- Schema: `{ automationId?, name?, nameMatch?, controlType?, scope?, ignoreCase? }`.
- Resolved fresh on the action thread. Survives snapshot churn.
- Requires exactly one match:
  - 0 matches: `SelectorNoMatch`.
  - >1 matches: `AmbiguousMatch`. Refine the selector or fall back to `ref`.
  - 1 match: The tool acts and returns a freshly minted `resolvedElement` ref for immediate follow-up. Do not hold this ref across turns.

## Common parameters

- **`timeoutMs`:** Most interaction, perception, and wait tools accept an explicit block timeout in milliseconds (default 4000). Agents can extend it for slow UIs.
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
| `desktop_list_windows` | ReadOnly | List top-level desktop windows. Opt-ins: `includeBounds`, `includeHandles`. |
| `desktop_open_window` | ReadOnly | Open window by pid/title, returns handle (e.g. `w1`). |
| `desktop_launch_app` | Destructive | Launch app, returns handle. Blocked in `--read-only-mode`. |
| `desktop_focus_window` | Destructive | Bring window to foreground. Blocked in `--read-only-mode`. |
| `desktop_close_window` | Destructive | Close window, free handle. Blocked in `--read-only-mode`. |

### Perception
| Tool | Access | Description & Key Parameters |
|---|---|---|
| `desktop_snapshot` | ReadOnly | Walk tree into ref-tagged snapshot. **Params:** `root` (subtree scope), `maxDepth`, `interactiveOnly`, `fullProperties`, `includeOffscreen`, `timeoutMs`. |
| `desktop_snapshot_diff` | ReadOnly | Diff CURRENT tree against baseline `snapshotId`. **Params:** `scope` (subtree ref). |
| `desktop_snapshot_stats` | ReadOnly | Control counts. **Params:** `snapshotId` (offline stats) OR `window`. |
| `desktop_get_focused_element` | ReadOnly | Return UIA-focused element's ref and descriptor. |
| `desktop_find` | ReadOnly | Query window for refs without full walk. **Params:** `timeoutMs`. |
| `desktop_screenshot` | ReadOnly | PNG capture. Redacts passwords. **Params:** `window`, `ref`, `maxWidth` (default 1600). |
| `desktop_get_bounds` | ReadOnly | Get absolute screen bounds, dpiScale, isOffscreen status. |
| `desktop_wait_for` | ReadOnly | Poll until selector condition holds. **Params:** `by`, `value`, `until`, `equals`, `pollIntervalMs`, `timeoutMs`. |
| `desktop_wait_for_stable` | ReadOnly | Poll until tree stops changing. **Params:** `by`, `value`, `includeText`, `quietMs`, `pollIntervalMs`, `timeoutMs`. |
| `desktop_user_state` | ReadOnly | Report coarse human presence. Lease-exempt. |
| `desktop_wait_for_foreground` | ReadOnly | Block until window gains foreground. **Params:** `timeoutMs`. Lease-exempt. |

### Content & Clipboard
| Tool | Access | Description & Key Parameters |
|---|---|---|
| `desktop_get_grid_cell` | ReadOnly | Read one grid/table cell by `(row,col)`. |
| `desktop_grid_select` | Destructive | Select cell by `(row,col)`. |
| `desktop_get_text` | ReadOnly | Read element's text. **Params:** `selectionOnly`, `maxLength`, `fromEnd`. |
| `desktop_read_terminal_tab` | Destructive | Read terminal tab. **Params:** `tabIndex`, `restoreFocus`, `fromEnd`, `maxLength`. |
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

## Verification & Safety

- **TOCTOU Guards:** Input tools will abort with `ElementDisappearedDuringAction` if the target window loses foreground during the input sequence.

## Event Streaming Delivery

Events are buffered and can be fetched via `desktop_drain_events`, or delivered via push notifications (`notifications/flaui/desktop_event`) if the MCP client supports it. Do not rely on both in the same host.

Event payload shape:
```json
{ "subscriptionId": "s1", "event": "...", "window": "w1", "ref": "e123", "coalescedCount": 1, "timestampUtc": "..." }
```
- `structure_changed` bursts are coalesced. If a list shows `droppedCount > 0`, the buffer overflowed under load; re-snapshot to sync.
- Event refs are ephemeral. Re-snapshot for a durable ref.
- Subscriptions auto-evict when the window closes.

## Opaque-App Access

1. **Chromium/Electron:** Return an empty `Document` node with `wakeable:true`. Call `desktop_wake_accessibility` to hydrate the tree, then snapshot as usual.
2. **Zero-accessibility surfaces (games, Citrix, RDP):** Use `desktop_find_text` for on-box OCR targeting. OCR resolves visible text to click coordinates. Fuzzy match can trigger false positives in body text. OCR returns `OcrUnavailable` if no Windows OCR pack is installed.

## Known Tool Limitations

- Cannot drive elevated/Administrator apps (UIPI boundary blocks input).
- Input needs a connected, interactive, and unlocked desktop session (`InputDesktopUnavailable`).
- Reactive editors (Win11 Notepad, Chromium editors) garble `desktop_type` keystrokes. Prefer `desktop_set_value` or `desktop_paste_text`.
- Screenshots do not handle occlusion; target windows must be focused to guarantee full visibility.
- Credential stores (matched by process name) deny snapshot and grid cell reads.
- Elements with no `AutomationId` and no `Name` cannot be re-resolved after recycling. Fall back to `desktop_click_at`.
