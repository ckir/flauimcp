# FlaUI.Mcp — Windows Desktop Automation MCP Server

**Date:** 2026-06-25
**Status:** Design approved, pending implementation plan

## Summary

A "Playwright for the Windows desktop": a Model Context Protocol (MCP) server,
written in C#/.NET, that lets an LLM agent perceive and drive **arbitrary
Windows desktop applications**. It uses [FlaUI](https://github.com/FlaUI/FlaUI)
(a wrapper over Windows UI Automation, UIA) as its automation core and the
official `ModelContextProtocol` C# SDK for the MCP layer.

Conceptually it mirrors Microsoft's Playwright MCP server — but where Playwright
exposes the browser accessibility tree, this exposes the Windows UIA tree, with
control patterns (Invoke, Value, Toggle, ExpandCollapse, Selection) as the
action primitives.

## Locked design decisions

| Axis | Decision |
| --- | --- |
| **Primary use case** | General agent control of arbitrary Windows desktop apps (broadest "Playwright for desktop" vision). |
| **Perception model** | **Hybrid, co-equal**: accessibility-tree snapshots *and* screenshot + coordinate interaction, both first-class. |
| **Session model** | **Explicit multi-window handles**: agent opens window handles (`w1`, `w2`, …) and passes a `window` handle to every action (like Playwright multiple pages). |
| **Transport** | **Both** stdio (default, local) and HTTP/SSE (remote/shared), selectable by flag. |
| **Automation core** | FlaUI (`FlaUI.UIA3`). |
| **Element-reference engine** | **Option C**: opaque agent-facing ref (`e23`) backed by a re-resolvable descriptor (RuntimeId + ControlType + AutomationId/Name + tree path), resilient to UIA tree mutation. |

## Architecture

.NET 8, C#. Single solution, layered so the automation engine has **zero MCP
dependencies** and is testable headless.

### Projects

1. **`FlaUI.Mcp.Core`** — automation engine, no MCP awareness.
   - **`AutomationDispatcher`** — marshals all UIA work onto **dedicated STA
     thread(s)**. `FlaUI.UIA3` wraps COM; calling `AutomationElement` from MTA
     threadpool threads (which both the async stdio loop and ASP.NET Core use)
     throws `RPC_E_WRONG_THREAD` or deadlocks. It exposes **two STA contexts**,
     a deliberate split that prevents a class of total-server freeze:
     - a **query STA** for read work (discovery, snapshot, resolution,
       `get_bounds`) — must stay responsive at all times;
     - an **action STA** for potentially-blocking calls (`InvokePattern.Invoke`,
       pattern actions). `Invoke()` is a **blocking COM call**: if the click
       opens a modal ("Save As…") on the target's UI thread, `Invoke()` does not
       return until the modal closes. Running it on the query thread would freeze
       the whole server — the agent couldn't even snapshot the modal it just
       triggered. So action calls run on the action STA with a **timeout +
       abandonment**: past the timeout the call returns `ACTION_BLOCKED_PENDING`
       (a non-error signal: "likely opened a modal — snapshot to see it") while
       the worker thread stays parked on the pending COM call. The query STA
       stays live so the agent can perceive and dismiss the blocking dialog.
     COM objects are thread-affine; cross-context use is marshaled through the
     dispatcher, never by touching an element from a foreign thread.
   - **`SessionManager`** — tracks per-connection state: open window handles,
     the live snapshot/ref registry, and **server-owned spawned processes**
     (from `desktop_launch_app`). Exposes a **connection-termination hook**: on
     client disconnect/crash it always frees handles and snapshots; process
     teardown follows the `killSpawnedOnDisconnect` policy (default **on** for
     single-session stdio, **off** for shared HTTP — auto-killing a user's app
     under a shared server is destructive). Prevents leaked handles, snapshots,
     and orphaned processes littering the desktop.
   - **`WindowManager`** — discovers top-level windows; launches/attaches
     processes; owns the **window-handle registry** (`w1`, `w2`, …). Each
     handle wraps a FlaUI `Window` / `Application`. On resolving a handle it
     subscribes to the underlying `Process.Exited` event so a `w#` is
     **proactively invalidated** when the user closes the window or the app
     crashes independently — rather than going stale until the next action
     throws `WINDOW_NOT_FOUND`.
   - **`SnapshotEngine`** — walks the UIA subtree of a window handle, emits the
     serialized accessibility snapshot, and registers element refs. **Popup-aware:**
     Win32 context menus (`#32768`) and WPF `PopupRoot`/dropdowns are children of
     the **Desktop**, not the target window — so a naive window-scoped snapshot
     after a right-click shows nothing and the agent walls. The engine queries the
     Desktop for foreground popups/menus owned by the same process and **grafts
     them into the returned tree under a `[Popups]` node** (refs assigned
     normally). Also surfaces `LegacyIAccessiblePattern` (the UIA↔MSAA bridge) so
     controls exposed only via MSAA still appear.
   - **`RefRegistry`** — the option-C engine. Maps `e23` →
     `ElementDescriptor { RuntimeId, ControlType, AutomationId, Name, AncestorAutomationId, IndexPath }`.
     **Resolution order** (re-resolution must not rely on rigid tree-walking,
     which fails precisely when the tree mutates):
     1. Cached `AutomationElement` if its `RuntimeId` still matches.
     2. `AutomationId` (or `Name` + `ControlType`) searched **scoped under the
        nearest stable ancestor** — the closest ancestor that itself carries an
        `AutomationId` (`AncestorAutomationId`). `RuntimeId` is ephemeral across
        UI rebuilds, so it is a fast-path check only, never the primary key.
     3. `IndexPath` only as a last-resort fuzzy hint (a list item inserted above
        the target shifts the index, so this is best-effort).
     4. Otherwise `REF_STALE_UNRESOLVABLE`.
     **Refs are per-snapshot scoped:** a new `desktop_snapshot(w1)` supersedes
     and clears `w1`'s previous refs, so the registry stays bounded under long
     agent loops; a stale held ref returns `REF_NOT_FOUND`.
   - **`Interactor`** — performs actions. Prefers UIA **control patterns**
     (Invoke, Value, Toggle, ExpandCollapse, Selection, ScrollItem); falls back
     to **synthetic input** (FlaUI `Mouse`/`Keyboard`) for the vision path.
     Reports which path was used. Synthetic typing (`desktop_type`) **always
     calls UIA `Focus()` on the resolved ref first** (click-to-focus fallback if
     `Focus()` is unsupported/fails), so keystrokes can't bleed into whatever
     control currently holds OS focus.
   - **`VisionCapture`** — per-window and per-element screenshots (PNG);
     coordinate translation window ↔ screen.
   - **`Waiter`** — synchronization helpers (wait for element by descriptor,
     wait for window idle via UIA, timeouts).

2. **`FlaUI.Mcp.Server`** — the MCP host. Defines tools as thin adapters over
   Core, registers them with the SDK, wires up both transports, serializes
   snapshots and error envelopes.

3. **`FlaUI.Mcp.Tests`** — xUnit. Core tested against real Win32 apps plus a
   bundled WPF test-harness app.

4. **`FlaUI.Mcp.TestApp`** — a small WPF app with known AutomationIds used as a
   deterministic automation target in tests.

### Transport

One entry point:
- `--transport stdio` (default) — for local MCP clients (Claude Desktop/Code).
- `--transport http --port N` — ASP.NET Core hosting (`ModelContextProtocol.AspNetCore`),
  SSE / streamable-HTTP. Binds to `localhost` only unless `--bind` is given.

Same tool set on both transports. UIA + synthetic input require running in the
user's interactive desktop session either way.

## Tool catalog

Tools use a `desktop_*` prefix. Every action tool takes a `window` handle.

### Window / session management
- `desktop_list_windows` → `[{handle?, title, processName, pid, bounds, isForeground}]`. `handle` is null until opened.
- `desktop_open_window` `{by: title|pid|automationId, value}` → registers and returns a `w#` handle. Titles are rarely unique (e.g. several "Untitled - Notepad"): multiple matches return `AMBIGUOUS_MATCH` listing candidates (`pid`, `bounds`, `title`) so the agent re-selects `by:pid`. No silent first-match.
- `desktop_launch_app` `{path, args?, timeoutMs?}` → starts a process, then handles splash-screen races: `WaitForInputIdle` + poll until a top-level window owned by the process with a non-empty title appears, then returns its handle. On timeout → `LAUNCH_TIMEOUT`. The process is registered as **server-owned** (see `SessionManager`).
- `desktop_focus_window` `{window}` → bring to foreground.
- `desktop_close_window` `{window}` → close + free handle.

### Perception (both paths, co-equal)
- `desktop_snapshot` `{window, root?: ref, maxDepth?, interactiveOnly?, fullProperties?}` → indented text tree; each line `[e23] Button "OK" @{x,y,w,h} {enabled, focusable}` plus available patterns. **Bounding rects (screenshot-pixel space) are always included** — they bridge the ref path and the vision path. Refs registered in `RefRegistry`. Primary, token-efficient path. `interactiveOnly` (default true) prunes non-interactive container/decoration noise; like Playwright, only meaningful roles are surfaced. `fullProperties` (default false) adds HelpText/LabeledBy/AutomationId/etc. for richer reasoning. Returns a `snapshotId` (for `desktop_snapshot_diff`). Refs are **namespaced per window handle**.
- `desktop_snapshot_stats` `{window, root?, interactiveOnly?}` → node count + rough token estimate **without** emitting the full tree, so the agent can decide whether to narrow scope before a big snapshot (SAP/Oracle-forms-scale trees).
- `desktop_snapshot_global` → macro-view of the desktop: top-level windows with `{title, bounds, pid, zOrder, isForeground, occludes?}` — no deep UI trees. Lets the agent reason about Z-order, occlusion, and focus-stealing toasts (the desktop is not isolated tabs).
- `desktop_snapshot_diff` `{window, sinceSnapshotId?}` → structured delta vs a prior snapshot: `[Modified: e45 CheckBox "Accept" {checked}]`, `[Added: e99 Text "Success!"]`, `[Removed: e12]`. Backed by a fast-hash of the prior tree in `SessionManager`. Answers "what changed since I acted?" without re-sending the whole tree.
- `desktop_screenshot` `{window | ref, captureRect?}` → PNG image content **plus** metadata: `{imageWidthPx, imageHeightPx, dpiScale, logicalBounds}`. The agent needs exact pixel dims and scale because the model may receive a resized image and Windows may run at non-100% DPI; a blind 1:1 assumption misclicks. `captureRect` limits capture to a subregion.
- `desktop_get_bounds` `{window | ref}` → element/window rectangle in screenshot-pixel space (and logical), for mapping a ref to the vision path and back.

### Interaction — ref path
- `desktop_click` `{window, ref, button?, double?, modifiers?: ["Ctrl"|"Shift"|"Alt"]}` — modifiers held during the click (visual/list multi-select).
- `desktop_type` `{window, ref, text, clear?}` — calls UIA `Focus()` on the ref (click-to-focus fallback) before injecting keystrokes.
- `desktop_set_value` `{window, ref, value}` (ValuePattern fast-path)
- `desktop_toggle` / `desktop_expand` / `desktop_select` `{window, ref}`
- `desktop_invoke` `{window, ref, timeoutMs?}` (raw InvokePattern) — runs on the action STA; on block past `timeoutMs` returns `ACTION_BLOCKED_PENDING` (modal likely opened, snapshot to see it) instead of freezing.
- `desktop_scroll_into_view` `{window, ref}` (ScrollItemPattern)
- `desktop_scroll` `{window, ref, direction, amount}` — ScrollPattern on a **container** (ListBox/grid). For **UI virtualization** (WPF/UWP off-screen items don't exist in the UIA tree at all), the agent scrolls to force realization, then re-snapshots to get refs for newly materialized items.

### Interaction — structured patterns (Grid / Text / Window)
- `desktop_get_grid_cell` `{window, ref, row, col}` → GridPattern/TablePattern cell access without snapshotting an entire spreadsheet (an Excel/data-grid snapshot would explode the context limit). `desktop_grid_select` `{window, ref, row, col}` selects it.
- `desktop_get_text` `{window, ref, selectionOnly?}` → TextPattern: full text or current selection; `desktop_set_caret` `{window, ref, offset}` positions the caret; `desktop_select_text_range` `{window, ref, startOffset, length}` highlights a range via UIA `TextRange` (so the agent can replace a paragraph natively instead of synthesizing shift-arrow keys). For Notepad/VSCode-style read/scrape/edit.
- `desktop_window_transform` `{window, action: maximize|minimize|restore|move|resize, bounds?}` → WindowPattern/TransformPattern, so the agent can move windows out of the way or size them.

### Clipboard (fast path)
- `desktop_clipboard_get` → read text. `desktop_clipboard_set` `{text}` → write. Pasting large blocks via `desktop_type` synthetic input is slow and focus-fragile; the agent sets the clipboard then sends `Ctrl+V` via `desktop_key`. Also the primary cross-app data-transfer path.

### Interaction — vision / coordinate path
Coordinate contract: `x,y` are in the **pixel space of the most recent
`desktop_screenshot` for that window** (server maps them to physical screen
pixels via the captured `dpiScale`/`logicalBounds`). Alternatively pass
`xPct,yPct` (0–1 fractions of the window) — resilient to DPI scaling and to the
model resizing the image before emitting coordinates.
- `desktop_click_at` `{window, x?, y? | xPct?, yPct?, button?, double?, modifiers?: ["Ctrl"|"Shift"|"Alt"]}`
- `desktop_drag` `{window, path: [{x?, y? | xPct?, yPct?, delayMs?}, …]}` — a single **atomic** drag along ordered waypoints (same coordinate contract). `delayMs` dwells on a waypoint so spring-loaded drop targets (an unexpanded tree folder) have time to open before the drag continues. Atomic (not separate down/move/up tools) so a failure can't leave the mouse button stuck down.
- `desktop_key` `{window, keys}` (e.g. `"Ctrl+S"`)

### Synchronization
- `desktop_wait_for` `{window, ref?|name?|controlType?, state?: visible|enabled|gone, timeoutMs?}`
- `desktop_wait_for_stable` `{window, quietMs?, timeoutMs?}` → waits until the UIA tree stops mutating for `quietMs` (no StructureChanged churn), for apps that animate/populate asynchronously before they're safe to act on.

### Data flow (snapshot → act)
1. `desktop_snapshot(w1)` → `SnapshotEngine` walks UIA subtree → `RefRegistry`
   assigns `e#` + stores descriptors → returns text tree.
2. `desktop_click(w1, "e23")` → `RefRegistry.Resolve("e23")` (cached element, or
   descriptor re-walk if stale) → `Interactor` invokes pattern (or synthetic
   click) → `{ok, changed?, pathUsed}`.
3. Vision path: `desktop_screenshot` → agent reasons on pixels →
   `desktop_click_at(w1, x, y)`.

### YAGNI cuts for v1
No recording/codegen, no PDF/print, no network interception, no multi-monitor
abstraction beyond raw coordinates, no persisted selector store.

## Error handling

Structured, agent-recoverable envelopes — never raw stack traces. **Every error
carries a `suggestedRecovery` field** (e.g. `"snapshot+retry"`, `"re-list windows"`,
`"scroll then re-snapshot"`) so an agent loop has a concrete next move instead of
guessing:

- `WINDOW_NOT_FOUND` / `WINDOW_HANDLE_STALE` — window closed; agent should re-list.
- `REF_NOT_FOUND` — ref never existed / wrong window.
- `REF_STALE_UNRESOLVABLE` — descriptor re-walk failed (element truly gone);
  message instructs "re-snapshot." This is the **expected** outcome when
  option-C re-resolution can't recover — a normal signal, not a crash.
- `PATTERN_UNSUPPORTED` — element lacks the requested pattern; `Interactor`
  auto-falls-back to synthetic click and reports which path it used.
- `ELEMENT_NOT_ACTIONABLE` — offscreen/disabled; hint to `desktop_scroll_into_view`
  or `desktop_wait_for`.
- `AMBIGUOUS_MATCH` — `desktop_open_window` matched multiple windows; lists
  candidates (`pid`, `bounds`, `title`) so the agent re-selects `by:pid`.
- `LAUNCH_TIMEOUT` — `desktop_launch_app` saw no qualifying top-level window
  before `timeoutMs` (splash-screen race or slow start).
- `ACCESS_DENIED_INTEGRITY` — target window is at a higher integrity level than
  the server (see Security & constraints).
- `ACTION_BLOCKED_PENDING` — an action (`Invoke`/pattern) blocked past its
  timeout, most likely because it opened a modal. **Not a failure** — the query
  STA is still live; recovery is `"snapshot to see the dialog"`.
- `ELEMENT_DISAPPEARED_DURING_ACTION` — the resolved element vanished mid-action
  (tree mutated); recovery `"snapshot+retry"`.
- `UAC_PROMPT_DETECTED` — a launch/action raised a UAC consent prompt on the
  secure desktop, which UIA cannot see unless the server is elevated.
- `TIMEOUT` — from `Waiter`, naming what it waited on.

COM/UIA exceptions are caught at the tool boundary and mapped. A single bad call
never kills the process.

## Security & constraints

Called out explicitly, not solved away:

- **Integrity level / UIPI** — the server can only drive windows at its own
  integrity level or lower. It cannot automate an elevated (admin) app unless
  the server itself is elevated. `desktop_open_window` returns
  `ACCESS_DENIED_INTEGRITY` rather than failing silently.
- **Interactive session required** — UIA + synthetic input need a real, unlocked
  desktop session. No headless / Windows-service / locked-screen operation.
- **HTTP transport is inherently dangerous** — it exposes full control of the
  machine's desktop. v1 binds `localhost` only by default and prints a startup
  warning. A non-localhost `--bind` is a **hard gate**: the server **refuses to
  start** without an auth token configured (not a deferred nicety). Each
  connection gets its **own ref registry / snapshot state** in `SessionManager`,
  so concurrent clients can't read or stomp each other's refs.
- **No app allow/deny list** in v1 (general control is the goal); noted as a
  future guardrail.

### Resolved technical constraints (design decisions)

These are the Windows/UIA footguns that drove specific design choices; each is
now pinned, not deferred:

- **COM/UIA STA threading + blocking-Invoke freeze** — UIA is COM and
  apartment-sensitive; MTA threadpool access throws `RPC_E_WRONG_THREAD`. And
  `InvokePattern.Invoke()` blocks until any modal it opens is dismissed. Resolved
  by the **`AutomationDispatcher`**'s split **query STA** (always responsive) +
  **action STA** (blocking actions with timeout/abandonment →
  `ACTION_BLOCKED_PENDING`). See Architecture. This is the single most important
  resilience decision in the design.
- **DPI awareness** — process declares **Per-Monitor-V2** DPI awareness;
  `desktop_screenshot` reports `dpiScale`/pixel dims and the coordinate-path
  tools accept screenshot-pixel-space coords or `xPct/yPct`. See the coordinate
  contract under the vision/coordinate tools.
- **Concurrency on a shared desktop** — the desktop is one shared resource;
  concurrent actions would interleave input. v1 **serializes action execution**
  (single-flight queue, naturally enforced by the single STA dispatcher);
  read-only calls like `desktop_list_windows` may run concurrently. Multi-client
  HTTP is allowed at the transport level but actions are globally ordered.
- **Session/connection lifecycle** — `SessionManager`'s connection-termination
  hook frees handles and snapshots on disconnect/crash and applies the
  `killSpawnedOnDisconnect` policy to server-owned processes; refs are
  per-snapshot scoped so the registry can't grow unbounded under agent loops.
  See Architecture.

## Testing strategy

- **Core is the primary test target** (no MCP needed). xUnit against real apps:
  the bundled `FlaUI.Mcp.TestApp` (buttons, text boxes, lists, tabs, a dialog,
  and a **dynamically-mutating list** to exercise ref re-resolution), plus smoke
  tests against Notepad/Calc.
- **Ref-engine tests are first-class** — snapshot → mutate tree → assert a cached
  ref goes stale → assert descriptor re-resolution recovers it (the core
  option-C guarantee); **insert a list item above the target and assert
  re-resolution still finds it by `AutomationId` under its stable ancestor** (the
  `IndexPath`-shift case); assert `REF_STALE_UNRESOLVABLE` when the element is
  genuinely removed; assert a new snapshot invalidates prior refs
  (`REF_NOT_FOUND`).
- **Interactor tests** — pattern path and synthetic-fallback path both produce
  the expected UI state change; **`desktop_type` focuses the target first** —
  shift OS focus to another control, type, and assert text lands in the target,
  not the focused control.
- **Threading test** — issue concurrent tool calls (HTTP path) and assert no
  `RPC_E_WRONG_THREAD`; UIA work observed on the dispatcher's STA threads.
- **Blocking-Invoke test (critical)** — TestApp button opens a modal on click;
  assert `desktop_invoke` returns `ACTION_BLOCKED_PENDING` within timeout and the
  **query STA stays responsive** (a concurrent `desktop_snapshot`/`desktop_list_windows`
  still returns and shows the modal). Then dismiss and assert recovery.
- **Popup-grafting test** — right-click a TestApp element that opens a Win32
  context menu; assert `desktop_snapshot(w1)` shows the menu items under `[Popups]`.
- **Pattern tests** — Grid cell read/select against a data grid; TextPattern
  selection/caret against an edit control; Window transform (maximize/restore).
- **Virtualization test** — a virtualized list of 1000 items: assert item 500 is
  absent from the snapshot, `desktop_scroll` realizes it, re-snapshot exposes a
  usable ref.
- **Snapshot-diff test** — toggle a checkbox, assert `desktop_snapshot_diff`
  reports only the changed node.
- **Modifier-click test** — Ctrl-click two list items, assert both selected.
- **Text-range test** — `desktop_select_text_range` highlights the expected
  substring; **drag-waypoint test** — a path with a dwell springs open a tree
  folder mid-drag; **Process.Exited test** — kill a tracked app externally and
  assert the `w#` handle is invalidated without an intervening action call.
- **Lifecycle test** — simulate client disconnect and assert handles/snapshots
  freed and `killSpawnedOnDisconnect` policy honored per transport.
- **Window-resolution test** — two windows with identical titles → `AMBIGUOUS_MATCH`
  with candidates; `desktop_launch_app` against a splash-screen app resolves to
  the real main window, not the splash.
- **Server layer** (thin) — a few stdio integration tests with an in-process MCP
  client verifying tool wiring and error-envelope shape.
- **CI** — Windows GitHub runner; input-injection tests carry the
  interactive-desktop caveat.

## Post-v1

Deferred features and the v1→v2 boundary are tracked in
[`ROADMAP.md`](../../../ROADMAP.md) (UIA event streaming, built-in
`Windows.Media.Ocr` perception fallback, window arrangement, shell/system
integration, elevated-app broker, allow/deny guardrails, recording/codegen).
