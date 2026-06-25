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
   - **`WindowManager`** — discovers top-level windows; launches/attaches
     processes; owns the **window-handle registry** (`w1`, `w2`, …). Each
     handle wraps a FlaUI `Window` / `Application`.
   - **`SnapshotEngine`** — walks the UIA subtree of a window handle, emits the
     serialized accessibility snapshot, and registers element refs.
   - **`RefRegistry`** — the option-C engine. Maps `e23` →
     `ElementDescriptor { RuntimeId, ControlType, AutomationId, Name, IndexPath }`.
     `Resolve(ref)` tries the cached `AutomationElement`; on stale (COM
     exception / RuntimeId mismatch) it re-locates via the descriptor walk.
     Scoped per window handle.
   - **`Interactor`** — performs actions. Prefers UIA **control patterns**
     (Invoke, Value, Toggle, ExpandCollapse, Selection, ScrollItem); falls back
     to **synthetic input** (FlaUI `Mouse`/`Keyboard`, window-relative
     coordinates) for the vision path. Reports which path was used.
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
- `desktop_open_window` `{by: title|pid|automationId, value}` → registers and returns a `w#` handle.
- `desktop_launch_app` `{path, args?}` → starts a process, waits for main window, returns handle.
- `desktop_focus_window` `{window}` → bring to foreground.
- `desktop_close_window` `{window}` → close + free handle.

### Perception (both paths, co-equal)
- `desktop_snapshot` `{window, root?: ref, maxDepth?, interactiveOnly?}` → indented text tree; each line `[e23] Button "OK" {enabled, focusable}` plus available patterns. Refs registered in `RefRegistry`. Primary, token-efficient path. `interactiveOnly` (default true) prunes non-interactive container/decoration noise — large app trees otherwise blow the agent's context; like Playwright, only meaningful roles are surfaced. Refs are **namespaced per window handle** (`w1`'s `e23` is distinct from `w2`'s).
- `desktop_screenshot` `{window | ref}` → PNG image content (vision path).

### Interaction — ref path
- `desktop_click` `{window, ref, button?, double?}`
- `desktop_type` `{window, ref, text, clear?}`
- `desktop_set_value` `{window, ref, value}` (ValuePattern fast-path)
- `desktop_toggle` / `desktop_expand` / `desktop_select` `{window, ref}`
- `desktop_invoke` `{window, ref}` (raw InvokePattern)
- `desktop_scroll_into_view` `{window, ref}`

### Interaction — vision / coordinate path
- `desktop_click_at` `{window, x, y, button?, double?}` (window-relative)
- `desktop_drag` `{window, fromX, fromY, toX, toY}`
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

### Technical constraints to resolve in the plan

- **DPI awareness** — per-monitor DPI scaling distorts window-relative
  coordinates, synthetic input, and screenshots. The process must declare
  Per-Monitor-V2 DPI awareness, and `VisionCapture` + the coordinate-path tools
  (`desktop_click_at`, `desktop_drag`) must translate between physical and
  logical pixels consistently. This is a known Windows footgun; the plan must
  pin the coordinate contract (which space `x,y` are in) explicitly.
- **COM/UIA threading** — UIA access is COM and apartment-sensitive. Under the
  async HTTP transport, tool calls arrive on threadpool threads. The engine must
  marshal all FlaUI/UIA calls onto a defined automation thread (dedicated STA /
  single automation context) rather than touching COM from arbitrary threads.
- **Concurrency on a shared desktop** — the desktop is a single shared resource;
  concurrent actions (especially multiple HTTP clients) would interleave input
  and corrupt state. v1 **serializes action execution** (single-flight queue per
  server instance); read-only calls like `desktop_list_windows` may run
  concurrently. Multi-client HTTP is allowed at the transport level but actions
  are globally ordered.

## Testing strategy

- **Core is the primary test target** (no MCP needed). xUnit against real apps:
  the bundled `FlaUI.Mcp.TestApp` (buttons, text boxes, lists, tabs, a dialog,
  and a **dynamically-mutating list** to exercise ref re-resolution), plus smoke
  tests against Notepad/Calc.
- **Ref-engine tests are first-class** — snapshot → mutate tree → assert a cached
  ref goes stale → assert descriptor re-resolution recovers it (the core
  option-C guarantee); assert `REF_STALE_UNRESOLVABLE` when the element is
  genuinely removed.
- **Interactor tests** — pattern path and synthetic-fallback path both produce
  the expected UI state change.
- **Server layer** (thin) — a few stdio integration tests with an in-process MCP
  client verifying tool wiring and error-envelope shape.
- **CI** — Windows GitHub runner; input-injection tests carry the
  interactive-desktop caveat.

## Open follow-ups (post-v1)

- HTTP transport authentication / token.
- App allow/deny guardrails.
- Elevated-app automation story (broker process at higher integrity).
- Recording / codegen of action sequences.
