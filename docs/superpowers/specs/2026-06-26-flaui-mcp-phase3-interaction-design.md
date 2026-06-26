# FlaUI.Mcp — Phase 3: Pattern-Based Interaction (design)

**Date:** 2026-06-26
**Status:** Revised after AGY-AFTER review (cross-STA cache/window-root fix; window_transform→3a) → pending user review → feeds writing-plans (3a first)
**Builds on:** [`2026-06-25-flaui-mcp-server-design.md`](2026-06-25-flaui-mcp-server-design.md)
(master spec — tool semantics, error envelopes, STA dispatcher) and the
[`project-flaui-mcp-prompt-injection`] memory (safety model).

## Summary

Phase 3 turns the agent from observer into actor — but only via **UIA control
patterns** (element-targeted: `InvokePattern`, `ValuePattern`, `TogglePattern`, …).
**No synthetic mouse/keyboard** — that is Phase 4, deliberately isolated because real
input is the blast-radius surface (can mis-target, can type into a shell). The phase
line is drawn by **blast radius, not feature area**.

Phase 3 is still **state-changing** (a pattern `Invoke` can click "Delete"/"Send"), so
the read/write trust split is encoded from the first write tool: interaction tools
carry `destructiveHint:true`, and an opt-in `--read-only-mode` server flag gives a hard
perception-only deployment.

The master spec already fixes the per-tool semantics. This document fixes the **Phase-3
decisions** made in the 2026-06-26 brainstorming (agy AGY-BEFORE + AGY-AFTER passes
folded): the two-increment split, the new components, the cross-STA execution model, the
safety posture, and the deltas to the master tool catalog.

## Phase boundary (recap)

| | Phase 3 (this doc) | Phase 4 (later) |
| --- | --- | --- |
| Mechanism | UIA control patterns (element's own automation) | Synthetic `SendInput` mouse/keyboard + coordinate path |
| Blast radius | Low — cannot mis-target, cannot reach a shell | High — the lethal-trifecta "act" leg |
| Safety added | `destructiveHint`, `--read-only-mode`, granular tools, HITL is the client's job | ACTION sink deny-list, action budget+audit, hard-fail-on-elevation |

## Scope — two increments, one phase

Decided (user, after agy concurrence): **Option 1 — two release increments inside one
conceptual Phase 3.** 3a ships the "agent can act" milestone fast at low review/test
surface; 3b completes the surface. Both are synthetic-input-free.

### 3a → v0.3.0 — core pattern actions ("agent can act")

| Tool | Pattern | Notes |
| --- | --- | --- |
| `desktop_invoke` | InvokePattern | Action STA + timeout → `ACTION_BLOCKED_PENDING` on block (modal likely). |
| `desktop_set_value` | ValuePattern | Fast-path text/value set; `PATTERN_UNSUPPORTED` falls through (no synthetic fallback in P3). |
| `desktop_toggle` | TogglePattern | |
| `desktop_expand` | ExpandCollapsePattern | |
| `desktop_select` | SelectionItemPattern | |
| `desktop_scroll_into_view` | ScrollItemPattern | Bring an **element** into view. **Kept separate** from `desktop_scroll` (distinct intent; decided 2026-06-26). |
| `desktop_scroll` | ScrollPattern | Scroll a **container** by direction/amount — realizes virtualized children, then re-snapshot. |
| `desktop_set_focus` | UIA `Focus()` | **NEW (agy catch).** Pure UIA, not synthetic input. Focus is frequently a prerequisite that triggers lazy-loaded data / enables downstream controls. Element-scoped — distinct from Phase-1 `desktop_focus_window`. |
| `desktop_window_transform` | WindowPattern / TransformPattern | maximize/minimize/restore/move/resize (**not** close — already Phase-1 `desktop_close_window`). **Pulled into 3a (decided 2026-06-26):** window-scoped, low-complexity, and independent of the ref-resolution path — acts on the window handle directly via the action STA, no descriptor re-walk. |

Plus the cross-cutting 3a infrastructure: the **`Interactor`** component, the
**per-action STA action model** (Task C — fixes the modal deadlock) + **cross-STA
descriptor resolution** path (`RunOnRefActionAsync`), the **`--read-only-mode`** flag, and
`destructiveHint` annotations on every element-targeted tool above.

### 3b → v0.4.0 — structured patterns + perception completion

Intent-level here (detailed at 3b's own plan time, against then-current code — some
pieces, e.g. `VisionCapture`, are not yet built; per plan-vs-spec we do not fabricate
their shapes now):

- **Structured patterns:** `desktop_get_grid_cell` / `desktop_grid_select`
  (GridPattern/TablePattern), `desktop_get_text` / `desktop_set_caret` /
  `desktop_select_text_range` (TextPattern). (`desktop_window_transform` moved to 3a.)
- **Perception completion (read-only):** `desktop_screenshot` (+`dpiScale`/pixel dims),
  `desktop_get_bounds`, `desktop_snapshot_stats`, `desktop_snapshot_global`,
  `desktop_snapshot_diff`.
- **Clipboard / sync:** `desktop_clipboard_get` / `desktop_clipboard_set`,
  `desktop_wait_for`, `desktop_wait_for_stable` (stability = **UIA tree-structure**
  quiescence — no `StructureChanged` within `quietMs` — explicitly *not* pixel
  stability; documented in the tool description so the LLM doesn't expect it to wait on
  a spinning visual loader).

## New components (3a)

### `Interactor` (Core, new)

`FlaUI.Mcp.Core/Interaction/Interactor.cs`. Thin, stateless executor of a single
pattern action against a resolved `AutomationElement`. One method per pattern (or a
small switch), each: check pattern support → if unsupported throw
`ToolException(PATTERN_UNSUPPORTED, …)` (no synthetic fallback in Phase 3 — that
fallback is Phase 4) → perform the pattern call → return a small result
`{changed?, pathUsed:"pattern"}`. Zero MCP awareness (mirrors the Core/Server split).
**It never resolves refs and never touches the dispatcher** — it receives an already
resolved element on the correct STA (see below). This keeps it trivially unit-testable
against the TestApp.

### Cross-STA resolution-then-act (the load-bearing decision)

**Confirmed by agy's proxy-trap analysis:** an `AutomationElement` resolved on the
query STA must NOT be marshaled to the action STA — COM would create an apartment proxy
and marshal the blocking `Invoke()` call *back* to the query STA, re-blocking the very
thread the split dispatcher exists to keep alive. **The action STA must re-resolve the
ref to its own native COM pointer.**

Existing (keep): `PerceptionManager.RunOnRefAsync<T>(handle, ref, Func<element,T>)`
resolves **and** runs `func` on the **query** STA (via
`WindowManager.RunWithWindowAndDesktopAsync`). Correct for **read-only** ref tools
(`get_bounds`, `get_text`, `get_grid_cell`, element screenshot — all 3b): the cache
fast-path is fine there because there is no cross-apartment `Invoke`.

**The action path is NOT just "RunOnRefAsync on the action STA."** The AGY-AFTER review
surfaced two traps (both confirmed against v0.2.0) that make naive reuse silently defeat
the split:

- **Cache trap.** `RefRegistry.Resolve`'s fast-path returns the *cached*
  `AutomationElement` — created on the **query** STA. Hand it to the action STA, call
  `Invoke()`, and COM marshals the call **back** to the query STA, re-blocking the very
  thread the split exists to keep alive. → the action path must **bypass the cache** and
  re-walk from the `ElementDescriptor` only.
- **Window-root trap.** `WindowManager._handles` holds **query-STA** `Window` objects
  (built with the query-STA `UIA3Automation`); they cannot serve as action-STA search
  roots for the same reason.

So Phase 3 (3a) reworks the action context into a **per-action STA execution model**
(Task C) and adds a descriptor-first, **cache-free** resolution path:

- Each action runs on its **own transient STA thread** with its **own short-lived
  `UIA3Automation`** (COM is apartment-affine — it cannot reuse the query automation;
  through Phase 2 the query automation was the only one). This *supersedes* the earlier
  notion of a single long-lived action automation — see Task C (deadlock avoidance).
- `PerceptionManager.RunOnRefActionAsync<T>(handle, ref, Func<element,T>, int timeoutMs)`:
  (1) read **only** the `ElementDescriptor` from the registry (a brief locked read — *not*
  the cached element); (2) hop to the action STA via
  `AutomationDispatcher.RunActionAsync(…, timeoutMs)`; (3) on the action STA, resolve the
  target window **independently** from its stored native handle (`actionAutomation.FromHandle(hwnd)`
  — the HWND is captured once on the query STA at handle registration and stored with the
  `WindowHandle`; see Task A); (4) **build the same search roots the query path uses** —
  `{ window } + FindOwnerPopups(desktop)` — by also calling `actionAutomation.GetDesktop()`
  on the action STA and running the **identical** popup-grafting logic. This is load-bearing
  (AGY-AFTER catch): Win32 `#32768` context menus and WPF popups live at the **Desktop**
  level, *not* under the window, so a ref into a context-menu item would be unresolvable
  against the window root alone; (5) run the cache-free descriptor re-walk (AutomationId →
  Name+ControlType under nearest stable ancestor → IndexPath) scoped across those roots;
  (6) run `func` (the pattern call). On timeout the action STA stays parked on the pending
  COM call and the call returns `ACTION_BLOCKED_PENDING` (non-error: "snapshot to see the
  modal"); the query STA stays live.

To share logic between the query path (cached-or-walk) and the action path (cache-free
walk), two extractions are required — both **3a prerequisites**:
- **Task A — cache the HWND.** `WindowManager.Register` currently maps `w#` → a query-STA
  `Window` COM object only. Extract + cache the `NativeWindowHandle` (`IntPtr`) at
  registration (read once on the query STA) so the action STA can `FromHandle(hwnd)`
  without touching the query-STA `Window`.
- **Task B — extract `FindOwnerPopups`** out of `PerceptionManager` into a pure, stateless
  helper taking a `Desktop` element + owner identity (PID / RuntimeId), so the query and
  action STAs build **identical** `SearchRoots`. Likewise extract the
  AutomationId/Name/IndexPath search from `RefRegistry.Resolve` into a roots-only,
  cache-free descriptor-walk helper both paths call.

Each transient action thread instantiates **and** disposes its own `UIA3Automation` (on
that STA thread) — never letting the GC finalize a COM object on a random thread. A thread
abandoned on a blocked modal disposes its automation only when the blocking call finally
returns and the thread unwinds: a bounded, self-clearing parked thread, not a permanent
leak.

### Action execution model (Task C — severe-defect fix)

The Phase-1 `AutomationDispatcher` action context is a **single** long-lived STA thread
draining a `BlockingCollection` (verified in `StaThreadContext`). That is a **deadlock**
the moment actions exist: when an `Invoke` opens a modal the call blocks natively and
`RunActionAsync` returns `ACTION_BLOCKED_PENDING` — but the sole action thread stays parked
inside that call, so a **second** action (e.g. clicking "OK" to dismiss the modal) sits in
the queue forever. The documented recovery ("snapshot, then act on it") is then
unachievable: you can *perceive* the modal on the live query STA but never *act* on it.
(Surfaced by the AGY-AFTER pass; confirmed against the code.)

**Fix:** the action context becomes **transient STA thread per action** (abandonable), not
a shared serial queue. Each action spins a fresh STA thread → stateless resolve-and-act
(own `UIA3Automation`) → completes. If it blocks past the timeout it returns
`ACTION_BLOCKED_PENDING` and the thread is **abandoned**; the *next* action gets a
brand-new STA thread and can dismiss the modal. Safe because the Phase-3 action path holds
**zero cross-call state** (everything re-resolved per action). The query context is
unchanged (reads don't open modals). This intentionally relaxes the master spec's strict
single-flight action serialization — but only in the blocked case, which is exactly when a
second action is needed to recover; non-blocked actions still serialize naturally by the
agent awaiting each result. The abandoned action's eventual completion must not surface as
an unobserved task exception. Implementation mechanism (raw STA `Thread` +
`TaskCompletionSource` per call vs a custom STA `TaskScheduler`) is fixed in the 3a plan.

Every 3a state-changing tool flows: tool → `RunOnRefActionAsync` → (cache-free
descriptor-walk on the action STA) → `Interactor.<pattern>` → result. Window-scoped
`desktop_window_transform` skips the ref path entirely — it resolves the window on the
action STA and calls WindowPattern/TransformPattern directly.

**No new resolution error code** — descriptor re-walk failure returns the existing
`REF_STALE_UNRESOLVABLE` ("re-snapshot"); an element that vanishes mid-action yields the
existing `ELEMENT_DISAPPEARED_DURING_ACTION`. (We do **not** add agy's suggested
`ACTION_FAILED_STALE_REF` — it duplicates these.) The action-STA walk uses a short
fast-fail budget so a churning UI fails fast to the agent loop rather than retrying heavily.

### `--read-only-mode` flag (Server)

Opt-in hard gate (accepted from agy; the *only* server-side enforcement in v1 — no
stdio approval-token loop, no native approval toast). When the server is started with
`--read-only-mode`, any tool invocation whose tool lacks `readOnlyHint:true` is rejected
with a structured error before execution. Implementation: read the flag in `Program.cs`
arg handling (alongside the existing CLI/installer verb branch), store it on a small
injected server-config singleton, and enforce it explicitly in the existing
`ToolResponse.Guard` funnel via a known read/write tool classification — **not** by
reflecting the SDK `ReadOnly` annotation at runtime. (Per the AGY-AFTER review,
`WithToolsFromAssembly` dispatch exposes no pre-invocation hook to block on the
attribute, so enforcement is explicit, not "magical SDK interception.") Gives a
perception-only deployment that does not depend on the **client** honoring
`destructiveHint` — the right safety net for
a publicly `irm|iex`-distributed tool. **Explicitly rejected for v1:** a server-spawned
WinForms/WPF "Allow/Deny" toast (focus-stealing, overlay z-fighting, timeout deadlock —
a whole new failure domain, per agy and concurred).

## Safety posture (Phase 3)

- **Annotations:** every interaction tool → `readOnlyHint:false, destructiveHint:true`.
  Read tools keep `readOnlyHint:true`. This is the read/write trust split the client
  uses for auto-approve vs prompt.
- **Granular tools, never bundle read+write** — so a client *can* gate each write. This
  is the server's contribution to human-in-the-loop; the actual prompt is the client's
  job (a stdio server has no side-channel to a human).
- **`--read-only-mode`** is the hard, client-independent floor.
- **Never-elevated** stays Phase-2 warn-only; the hard-fail-behind-`--unsafe-allow-elevation`
  upgrade is **Phase 4** (the blast radius that justifies refusing to run only arrives
  with synthetic input).

## Error handling (reused, no new codes for 3a except the RO gate)

All from the master catalog: `PATTERN_UNSUPPORTED` (element lacks the pattern; in P3 this
is terminal — no synthetic fallback yet), `ELEMENT_NOT_ACTIONABLE` (offscreen/disabled —
hint `desktop_scroll_into_view`), `ACTION_BLOCKED_PENDING` (action parked past timeout —
non-error, snapshot to see the modal), `REF_NOT_FOUND` / `REF_STALE_UNRESOLVABLE`
(re-snapshot), `ELEMENT_DISAPPEARED_DURING_ACTION` (snapshot+retry). The
`--read-only-mode` rejection needs **one** new code (`WriteBlockedReadOnly`) — the single
error-catalog addition in 3a.

**Tool robustness (plan-level, AGY-AFTER):** `set_value` checks `ValuePattern.IsReadOnly`
(clean `ELEMENT_NOT_ACTIONABLE`, not a native throw) and focuses the element first;
`window_transform` checks `WindowPattern.CanMaximize`/`CanMinimize` before transforming;
`desktop_scroll` maps LLM directions to `ScrollAmount`/percent. **Read-only-mode tradeoff:**
`scroll`/`expand` are classified **write** (they mutate state and can trigger lazy-loads or
dismiss popups), so `--read-only-mode` blocks them — meaning a read-only deployment cannot
scroll to realize virtualized content. Accepted: that is the correct blast-radius call.

## Testing strategy (3a)

TestApp additions (deterministic targets): a button that toggles a state label
(`Invoke`), a `ValuePattern` text box, a checkbox (`Toggle`), an expander
(`ExpandCollapse`), a single-select list (`SelectionItem`), a scrollable/virtualized
container, and a focus-reveals-content control (focusing it makes a hidden label appear,
for `set_focus`). Plus the existing modal-on-click button for the blocking-Invoke test.

Tests (Core, `[Trait Desktop]` unless noted):
- **Per-pattern**: each `Interactor` call produces the expected UI state change against
  the TestApp.
- **`PATTERN_UNSUPPORTED`** (Desktop): calling a pattern an element doesn't support
  throws the terminal error (no synthetic fallback).
- **Blocking-Invoke (critical)**: invoking the modal-button returns
  `ACTION_BLOCKED_PENDING` within timeout AND a concurrent `desktop_snapshot` /
  `desktop_list_windows` still returns and shows the modal (query STA stays live); then
  dismiss and assert recovery.
- **Cross-STA correctness**: a state-change via `RunOnRefActionAsync` is observed; a ref
  resolved on a prior snapshot re-resolves on the action STA (not marshaled). The
  blocking-Invoke test's query-STA liveness is the observable proof isolation held.
- **Popup action (AGY-AFTER catch)**: right-click → snapshot grafts the context menu →
  invoke a menu item by its ref through `RunOnRefActionAsync`; proves the action STA
  resolves a ref living at the **Desktop** level (popup roots), not just under the window.
- **Deadlock-recovery proof (critical, Task C)**: invoke the modal-button (blocks) →
  assert `ACTION_BLOCKED_PENDING` → issue a **second** action that dismisses the modal and
  assert it **succeeds**. This is the test that proves the per-action STA model (a
  single-thread action queue would hang here).
- **`set_focus`**: focusing the reveal control makes the hidden label appear in a
  follow-up snapshot.
- **`window_transform`** (Desktop): maximize then restore the TestApp window; assert the
  window bounds change then revert (WindowPattern/TransformPattern — window-scoped,
  exercises the action STA without the ref path).
- **`--read-only-mode`** (non-Desktop): with the flag set, a write tool is rejected with
  `WriteBlockedReadOnly`; a read tool passes. Pure decision seam — runs in headless CI.

## Open items / deferred

- **3b detailed design** (structured patterns + perception completion + clipboard/sync)
  is intent-level here; its line-level plan is authored when 3a is merged, against
  then-current code (plan-vs-spec). `VisionCapture` (screenshot/DPI) is new there.
- **Phase 4** owns synthetic input + the action sink deny-list + budget/audit +
  hard-fail-on-elevation.
- **RefRegistry eviction on window close** (`WindowManager.Invalidate` →
  `RefRegistry.Forget`) — cheap bounded-leak follow-up, not 3a-blocking.

## Execution

3a is built **subagent-driven on a `phase-3-interaction` branch** off `master`
(TDD, per-task commits), then `finishing-a-development-branch` → v0.3.0. Per AGY-AFTER,
the finished 3a plan is routed to agy (web channel) before user presentation.
