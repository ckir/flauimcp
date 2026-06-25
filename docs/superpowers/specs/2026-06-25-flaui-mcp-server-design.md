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
   - **`AutomationDispatcher`** — owns a single **dedicated STA thread** with its
     own dispatch queue. `FlaUI.UIA3` wraps COM; calling `AutomationElement` from
     MTA threadpool threads (which both the async stdio loop and ASP.NET Core
     use) throws `RPC_E_WRONG_THREAD` or deadlocks. **Every** UIA call —
     discovery, snapshot, resolution, action — is marshaled onto this STA thread
     and awaited via a `Task` boundary. This is the single automation context
     for the whole server; all other Core components run their UIA work through
     it.
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
     handle wraps a FlaUI `Window` / `Application`.
   - **`SnapshotEngine`** — walks the UIA subtree of a window handle, emits the
     serialized accessibility snapshot, and registers element refs.
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
- `desktop_snapshot` `{window, root?: ref, maxDepth?, interactiveOnly?}` → indented text tree; each line `[e23] Button "OK" {enabled, focusable}` plus available patterns. Refs registered in `RefRegistry`. Primary, token-efficient path. `interactiveOnly` (default true) prunes non-interactive container/decoration noise — large app trees otherwise blow the agent's context; like Playwright, only meaningful roles are surfaced. Refs are **namespaced per window handle** (`w1`'s `e23` is distinct from `w2`'s).
- `desktop_screenshot` `{window | ref}` → PNG image content **plus** metadata: `{imageWidthPx, imageHeightPx, dpiScale, logicalBounds}`. The agent needs the exact pixel dimensions and scale because the model may receive a resized image and Windows may run at non-100% DPI; a blind 1:1 pixel assumption clicks the wrong spot.

### Interaction — ref path
- `desktop_click` `{window, ref, button?, double?}`
- `desktop_type` `{window, ref, text, clear?}` — calls UIA `Focus()` on the ref (click-to-focus fallback) before injecting keystrokes.
- `desktop_set_value` `{window, ref, value}` (ValuePattern fast-path)
- `desktop_toggle` / `desktop_expand` / `desktop_select` `{window, ref}`
- `desktop_invoke` `{window, ref}` (raw InvokePattern)
- `desktop_scroll_into_view` `{window, ref}`

### Interaction — vision / coordinate path
Coordinate contract: `x,y` are in the **pixel space of the most recent
`desktop_screenshot` for that window** (server maps them to physical screen
pixels via the captured `dpiScale`/`logicalBounds`). Alternatively pass
`xPct,yPct` (0–1 fractions of the window) — resilient to DPI scaling and to the
model resizing the image before emitting coordinates.
- `desktop_click_at` `{window, x?, y? | xPct?, yPct?, button?, double?}`
- `desktop_drag` `{window, from{...}, to{...}}` (same coordinate contract)
- `desktop_key` `{window, keys}` (e.g. `"Ctrl+S"`)

### Synchronization
- `desktop_wait_for` `{window, ref?|name?|controlType?, state?: visible|enabled|gone, timeoutMs?}`

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

Structured, agent-recoverable envelopes — never raw stack traces:

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
  machine's desktop. v1 binds `localhost` only by default, requires an explicit
  `--bind` to expose externally, and prints a startup warning. Token/auth is a
  documented follow-up, not v1.
- **No app allow/deny list** in v1 (general control is the goal); noted as a
  future guardrail.

### Resolved technical constraints (design decisions)

These are the Windows/UIA footguns that drove specific design choices; each is
now pinned, not deferred:

- **COM/UIA STA threading** — UIA is COM and apartment-sensitive; MTA threadpool
  access (both transports) throws `RPC_E_WRONG_THREAD`/deadlocks. Resolved by
  the **`AutomationDispatcher`** (single dedicated STA thread; all UIA work
  marshaled onto it). See Architecture.
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
  `RPC_E_WRONG_THREAD`; all UIA work observed on the single STA dispatcher thread.
- **Lifecycle test** — simulate client disconnect and assert handles/snapshots
  freed and `killSpawnedOnDisconnect` policy honored per transport.
- **Window-resolution test** — two windows with identical titles → `AMBIGUOUS_MATCH`
  with candidates; `desktop_launch_app` against a splash-screen app resolves to
  the real main window, not the splash.
- **Server layer** (thin) — a few stdio integration tests with an in-process MCP
  client verifying tool wiring and error-envelope shape.
- **CI** — Windows GitHub runner; input-injection tests carry the
  interactive-desktop caveat.

## Open follow-ups (post-v1)

- HTTP transport authentication / token.
- App allow/deny guardrails.
- Elevated-app automation story (broker process at higher integrity).
- Recording / codegen of action sequences.
