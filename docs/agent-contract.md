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

## Tool Catalog

### Window Lifecycle
| Tool | Access | Description |
|---|---|---|
| `desktop_list_windows` | ReadOnly | List top-level desktop windows (Title, ProcessName, Pid, IsForeground). Opt-ins: includeBounds, includeHandles. |
| `desktop_open_window` | ReadOnly | Open a window by pid or title and return its handle (e.g. `w1`). |
| `desktop_launch_app` | Destructive | Launch an app and return a handle to its main window. Blocked in `--read-only-mode`. |
| `desktop_focus_window` | Destructive | Bring a window to the foreground. Blocked in `--read-only-mode`. |
| `desktop_close_window` | Destructive | Close a window and free its handle. Blocked in `--read-only-mode`. |

### Perception
| Tool | Access | Description |
|---|---|---|
| `desktop_snapshot` | ReadOnly | Walk a window's accessibility tree into an indented, ref-tagged snapshot. |
| `desktop_snapshot_diff` | ReadOnly | Diff a window's CURRENT tree against an explicit baseline snapshotId. |
| `desktop_snapshot_stats` | ReadOnly | Cheap orientation: control counts and per-ControlType histogram, without full tree text. |
| `desktop_get_focused_element` | ReadOnly | Return the UIA-focused element's ref, descriptor line, and owning window. |
| `desktop_find` | ReadOnly | Query a window for element refs without walking the whole tree. |
| `desktop_screenshot` | ReadOnly | Capture a window, element, or full desktop as PNG. Password fields are redacted at capture time. |
| `desktop_get_bounds` | ReadOnly | Get absolute physical-pixel screen bounds, monitor dpiScale, and isOffscreen status. |
| `desktop_wait_for` | ReadOnly | Poll a window until a selector condition holds. Timeout returns `{satisfied:false}`. |
| `desktop_wait_for_stable` | ReadOnly | Poll until a window subtree stops structurally changing. |
| `desktop_user_state` | ReadOnly | Report coarse human presence: active, nearby, away, or null. Lease-exempt. |
| `desktop_wait_for_foreground` | ReadOnly | Block until a window gains OS foreground, closes, or times out. Lease-exempt. |

### Content & Clipboard
| Tool | Access | Description |
|---|---|---|
| `desktop_get_grid_cell` | ReadOnly | Read one grid/table cell by `(row,col)` without snapshotting the whole grid. |
| `desktop_grid_select` | Destructive | Select a grid/table cell by `(row,col)` via UIA SelectionItemPattern. Blocked in `--read-only-mode`. |
| `desktop_get_text` | ReadOnly | Read an element's text via UIA TextPattern. |
| `desktop_read_terminal_tab` | Destructive | Read a background Windows Terminal tab buffer. Restores original tab. Blocked in `--read-only-mode`. |
| `desktop_clipboard_get` | ReadOnly | Read the system clipboard as text. Returns `ClipboardUnavailable` if locked. |
| `desktop_clipboard_set` | Destructive | Write text to the system clipboard. Blocked in `--read-only-mode`. |

### Interaction (UIA Patterns)
State-changing tools that drive the app's automation providers (no OS input). All are blocked in `--read-only-mode`.

| Tool | Access | Description |
|---|---|---|
| `desktop_invoke` | Destructive | Invoke (activate) an element by ref via UIA InvokePattern. |
| `desktop_set_focus` | Destructive | Set keyboard focus to an element by ref (UIA Focus). |
| `desktop_set_value` | Destructive | Set an element's value by ref via UIA ValuePattern (focuses first). |
| `desktop_toggle` | Destructive | Toggle an element by ref via UIA TogglePattern. |
| `desktop_expand` | Destructive | Expand or collapse an element by ref via UIA ExpandCollapsePattern. |
| `desktop_select` | Destructive | Select an element by ref via UIA SelectionItemPattern. |
| `desktop_scroll_into_view` | Destructive | Scroll an element into view by ref via UIA ScrollItemPattern. |
| `desktop_scroll` | Destructive | Scroll a container by ref via UIA ScrollPattern. |
| `desktop_window_transform` | Destructive | Transform a window by handle via UIA Window/Transform patterns (maximize/minimize/restore). |

### Synthetic Input
Real `SendInput`-backed actions. Input operations require an active lease and are blocked in `--read-only-mode`. See [Architecture and Safety](architecture-and-safety.md) for rationale.

| Tool | Access | Description |
|---|---|---|
| `desktop_input_status` | ReadOnly | Report the synthetic-input lease status without firing input. |
| `desktop_set_caret` | Destructive | Position the text caret via UIA TextPattern (no OS input, no lease required). |
| `desktop_select_text_range` | Destructive | Select a text range via UIA TextPattern (no OS input, no lease required). |
| `desktop_type` | Destructive | Type text via SendInput. Paced by default. Requires active input lease. |
| `desktop_paste_text` | Destructive | Paste text via atomic clipboard Ctrl+V. Best-effort restore. Requires active input lease. |
| `desktop_key` | Destructive | Send one keyboard chord via SendInput. Requires active input lease. |
| `desktop_click` | Destructive | Synthetic mouse click at an element's clickable point. Requires active input lease. |
| `desktop_click_at` | Destructive | Synthetic mouse click at a window-relative point. Requires active input lease. |
| `desktop_drag` | Destructive | Synthetic mouse drag between two window-relative points. Requires active input lease. |

### Event Streaming (`desktop_watch`)
Subscribe to UIA events instead of polling. Lease-exempt.

| Tool | Access | Description |
|---|---|---|
| `desktop_watch` | ReadOnly | Subscribe to UIA events (`window_opened`, `window_closed`, `focus_changed`, `structure_changed`). |
| `desktop_unwatch` | ReadOnly | Stop a subscription. Idempotent. |
| `desktop_list_watches` | ReadOnly | List active watch subscriptions. |
| `desktop_drain_events` | ReadOnly | Fetch and clear buffered events for a subscription. Use if host doesn't surface push notifications. |

### Opaque-App Access
For apps that do not expose accessibility trees out-of-the-box. Lease-exempt.

| Tool | Access | Description |
|---|---|---|
| `desktop_wake_accessibility` | ReadOnly | Activate and hold an opaque Chromium/Electron window's native accessibility tree. |
| `desktop_release_accessibility` | ReadOnly | Release a held wake. Idempotent. |
| `desktop_list_wakes` | ReadOnly | List active wakes. |
| `desktop_find_text` | ReadOnly | OCR a window/region for text matching a query. Returns click coordinates. |
| `desktop_wait_for_text` | ReadOnly | Poll with OCR until a query appears or timeout. |

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
- Reactive editors (Win11 Notepad, Monaco) garble `desktop_type` keystrokes. Prefer `desktop_set_value` or `desktop_paste_text`.
- Screenshots do not handle occlusion; target windows must be focused to guarantee full visibility.
- Credential stores (e.g. 1Password) deny snapshot and grid cell reads.
- Elements with no `AutomationId` and no `Name` cannot be re-resolved after recycling. Fall back to `desktop_click_at`.
