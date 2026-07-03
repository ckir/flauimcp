# Phase 8 — `desktop_watch`: UIA event streaming over stdio (design)

**Status:** design / awaiting review. **Target version:** v0.8.0 (new capability → minor bump).
**Decision provenance:** scope fork decided agy-first (2026-07-03); user delegated the pick to Claude as the
tool's final consumer. HTTP/SSE was considered and **discarded** — see §2. Prior art: this reuses the split
query/action STA dispatcher (Phase 1), INV-5 password redaction (Phase 2), the `RefRegistry`/ref-minting and
`WindowInvalidated` eviction chokepoint (Phase 6).

## 1. Goal & motivation

Give an agent **push perception**: subscribe to UIA events and receive them as asynchronous MCP notifications,
instead of polling `desktop_snapshot` / `desktop_snapshot_diff` in a loop. Polling is slow (each cycle is an
LLM↔stdio round-trip) and **misses transient state** — a dialog that appears and auto-dismisses, a focus change,
a progress completion — because the agent only sees the world when it asks. `desktop_watch` lets the agent say
"tell me when a window opens / focus moves / this subtree changes" and then *react*.

**In scope (v1):** a subscribe/unsubscribe tool pair; a small, high-value set of event kinds; server→client
notifications carrying a ref-tagged, redaction-safe payload; back-pressure so events never starve the read STA;
subscription lifecycle tied to window close and connection teardown.

**Non-goals (v1):** HTTP/SSE transport or any multi-connection story (§2, §12); fine-grained
`PropertyChanged`/`TextChanged` streams (§5 — deferred, flood-prone); OCR/vision events (Phase 9); a bundled
"watch-then-act" macro (tools stay granular — the agent composes watch + existing action tools).

## 2. Transport decision — stdio MCP notifications, NOT HTTP/SSE

**MCP is JSON-RPC, which carries server→client notifications over the same stdio pipe already in use.** Verified
against the pinned SDK: `ModelContextProtocol` 1.4.0 exposes `SendNotificationAsync`, `NotificationMethods`,
`SendMessageAsync`, and `RegisterNotificationHandler` (XML-doc measured, 2026-07-03). So push notifications need
**no new transport**.

HTTP/SSE was the original lean and is **rejected** for this capability: the server drives **one physical
mouse/keyboard/foreground** — a singleton OS resource. Multiple concurrent clients would fight over the
foreground window (focus-steal deadlocks), so multi-tenancy is a mirage here; and HTTP's real cost (auth-token
gate, per-connection `RefRegistry` lifetime, revisiting the single-STA invariant under N connections) buys
nothing that events need. HTTP/SSE remains available as a *separate future* item if remote reachability (driving
a cloud/headless Windows box) ever becomes a goal; it is not a prerequisite for `desktop_watch`.

## 3. Tool surface

Two new `[McpServerTool]`s (both `ReadOnly` — they synthesize **no** input and read only perception state; they
do NOT require the input lease). A watch is scoped to a **window** the agent already has a handle for.

### `desktop_watch`
```
desktop_watch(window: string,            // window handle, e.g. w1
              events: string[],          // one+ of: "window_opened","window_closed",
                                         //          "focus_changed","structure_changed"
              scope?: string,            // optional element ref to scope structure_changed to a subtree
              timeoutMs?: int = 4000)
  -> { subscriptionId: "s1", window, events:[...], scope? }
```
- `subscriptionId` is a never-reused `s{n}` (mirrors `w{n}`/`e{n}`), minted per subscription.
- `focus_changed` is **process/desktop-global by nature** in UIA (`RegisterFocusChangedEvent` is on the
  automation root), but a subscription still *filters* delivered focus events to the subscribed `window`'s
  process/root so the agent isn't spammed by unrelated apps (design choice — filter server-side, §7).
- `window_opened`/`window_closed`: registered as UIA `Window_Opened`/`Window_Closed` automation events; v1 scopes
  them to descendants of the subscribed top-level window (child dialogs/popups) — the dominant agent need
  ("did my app open a dialog?"). Desktop-global new-window watching is a documented v2 extension.
- `structure_changed`: `RegisterStructureChangedEvent` on the window root (or on `scope`'s element subtree).
- Refusals (fail-closed, before any registration): unknown event token → `InvalidArguments`; a **deny-listed /
  credential window** → `TargetDenied` (no watching a credential store); stale window handle →
  `WindowHandleStale`. Read-only mode does NOT block it (it is `ReadOnly`).

### `desktop_unwatch`
```
desktop_unwatch(subscriptionId: string) -> { ok: true, subscriptionId }
```
Idempotent: unknown/already-evicted id returns `ok:true` (not an error) — the caller's intent (no more events)
is satisfied. Unregisters the UIA handlers on the query STA and drops the subscription.

### `desktop_list_watches` (read-only helper)
```
desktop_list_watches() -> { watches: [ { subscriptionId, window, events:[...], scope?, droppedCount } ] }
```
Lets the agent recover its subscription set (and see per-sub coalescing drops, §8) after a compaction / context
loss — the same recovery role `desktop_input_status` plays for the lease.

## 4. Notification wire contract

Emitted via `IMcpServer.SendNotificationAsync(method, params)`. Method (namespaced, stable):
```
"notifications/flaui/desktop_event"
```
Params (one event; booleans/strings always present unless marked optional — JsonIgnore-when-null):
```jsonc
{
  "subscriptionId": "s1",
  "event": "focus_changed",            // the event kind (open string; consumers tolerate unknowns)
  "window": "w1",                       // the subscription's window handle
  "ref": "e42",                         // freshly-minted ref for the source element (nullable: e.g. window_closed)
  "controlType": "Edit",               // minimal identity of the source (nullable)
  "name": "Search",                    // INV-5 REDACTED to "[REDACTED]" for IsPassword; nullable
  "bounds": [x,y,w,h],                  // nullable (element may be gone by delivery)
  "coalescedCount": 1,                  // >1 when N same-(sub,kind,target) events were collapsed (§8)
  "timestampUtc": "2026-07-03T...Z"     // when the event was observed
}
```
**Contract notes:** `ref` is minted into the subscription's window RefRegistry and is resolvable by later tools
(subject to the usual strict/lenient ref rules); it may already be stale by the time the agent acts (expected —
that is the nature of async events). `name` follows INV-5 exactly (§10). The payload is intentionally **minimal**
(no full subtree) — the agent calls `desktop_snapshot`/`desktop_get_text` with `ref` if it wants detail. Wording
of any human-facing string is not stability-guaranteed; the machine keys above are.

## 5. Event kinds (v1 set + rationale)

| Kind | UIA registration | Why v1 |
| --- | --- | --- |
| `window_opened` | `RegisterAutomationEvent(Window_Opened)` **globally on the Desktop root**, cached-`ProcessId`-filtered to the sub's window (§7, Seat E) | "did my app pop a dialog/child window?" — the top agent need |
| `window_closed` | `RegisterAutomationEvent(Window_Closed)` **globally**, same PID filter | dialog dismissed / step done |
| `focus_changed` | `RegisterFocusChangedEvent` (root; cached-`ProcessId`-filtered to the sub's process) | track where input focus is; detect steal |
| `structure_changed` | `RegisterStructureChangedEvent(Subtree)` on the window root or `scope` element | list/grid repopulated, content loaded (coalesced, §8) |

> **Why global + PID-filter for window events (AGY-AFTER Seat E):** Windows top-level dialogs — even owned/modal
> ones — are usually parented to the **Desktop root** in the UIA tree, NOT as descendants of the owner window's
> element. A `TreeScope.Descendants`-of-the-owner registration would therefore **miss most real dialogs**,
> defeating the tool's primary use case. So `window_opened`/`window_closed` register globally and filter by the
> subscription window's cached `ProcessId` (same mechanism as `focus_changed`). `structure_changed` stays scoped
> to the window/`scope` subtree (structure changes ARE descendant-scoped and would flood if global).

**Deferred (v2, flood/complexity):** `property_changed` (Name/Value/ToggleState — needs a property allow-list +
heavy coalescing), `text_changed` / `active_text_position_changed`, `notification_event`, desktop-global
new-window watching. Named here so the wire `event` string space is reserved.

## 6. Architecture — the COM-thread → query-STA → notification pump pipeline

The **central hazard** (roadmap's "biggest complexity/risk"): UIA delivers event callbacks on **COM RPC threads**
(the UIAutomation client runtime's own threads, not the registering STA). Reading `AutomationElement` properties
on that thread crosses apartments and can deadlock or violate the single-STA invariant. Resolution — a **three-stage
pipeline** keeping all UIA object access on the one query STA:

1. **Register (on query STA) WITH A CACHE REQUEST.** `desktop_watch` runs the FlaUI `Register*Event` calls via
   `AutomationDispatcher.RunQueryAsync` (UIA event registration must happen from the automation-owning STA), and
   **registers them under a UIA `CacheRequest` that pre-fetches `RuntimeId`, `ProcessId`, `ControlType`, `Name`,
   and `BoundingRectangle`** (AGY-AFTER Seat A). The delivered event's source element then carries these as
   **cached** properties — readable from the local cache with NO live cross-apartment COM round-trip. The
   registered handler is a **thin capture** delegate.
2. **Capture (on the COM callback thread) — minimal, non-blocking.** The handler performs NO *live* UIA reads
   (the cross-apartment ban), but it MAY read the **cached** properties above off the event's source, because
   those are local — no COM round-trip, no deadlock risk. Using them it: (a) drops the event immediately if it
   fails the process filter (foreign `ProcessId` for focus/window events — §7, closes the Seat-B DoS *before* the
   channel), else (b) computes the coalesce key from the cached `RuntimeId` (§8) and offers
   `(subscriptionId, kind, cachedProps, timestamp)` to a **bounded `System.Threading.Channels.Channel`** (single
   reader). If the channel is full it applies the drop/coalesce policy (§8) and increments the sub's
   `droppedCount` — it never blocks the COM thread. (Carrying the cached props forward also lets the worker build
   most of the payload from cache, further shrinking its query-STA load.)
3. **Build + emit (a dedicated worker task).** A single background worker drains the channel; for each (post-
   coalesce) event it marshals a **payload-build onto the query STA** (`RunQueryAsync`) — there the source element
   is safely read: mint a `ref`, read `controlType`/`name`(redacted)/`bounds`. Then, **off the STA**, it calls
   `IMcpServer.SendNotificationAsync(...)` (that is just JSON to stdout — no UIA, no STA needed). Keeping the emit
   off the STA means notification I/O never blocks perception reads.

**Why a worker + channel, not "build on the callback thread":** (a) apartment safety (no cross-apartment UIA
reads); (b) the query STA is the **responsiveness-critical** resource — it also serves the agent's
snapshot/find/get_text calls — so event payload-builds are *coalesced and bounded* before they touch it, and the
emit (the slow part relative to a property read) stays off it entirely.

`IMcpServer` acquisition: injected where the SDK provides it (a tool method parameter and/or an
`IHostedService`/session-scoped resolve). **Plan must confirm** the exact injection point against the SDK's
stdio session model (there is a single stdio session; the pump captures that session's `IMcpServer`).

## 7. Focus-event filtering & scoping

`RegisterFocusChangedEvent` and the global `Window_Opened`/`Window_Closed` registrations (§5) are desktop-wide.
The **capture (COM) thread** reads the event source's **cached `ProcessId`** (pre-fetched via the §6
`CacheRequest`, so this is a local read, not a cross-apartment call) and **drops any event whose process ≠ the
subscription window's process BEFORE it enters the bounded channel** (AGY-AFTER Seat B — filtering in the worker
instead would let a noisy foreign app flood the 256-slot channel and starve the subscribed window's events: a
cross-process DoS). This keeps the design "subscribe to a window" even though the underlying UIA registrations
are global, and prevents leaking any unrelated app's focus/window activity to the agent (a §10 security point).
Each global registration (`RegisterFocusChangedEvent`; global `Window_Opened`; global `Window_Closed`) is
established once and **ref-counted** across all subscriptions that need it; the last unsubscribe removes it.

## 8. Back-pressure & coalescing

`structure_changed` (and focus churn) can burst. Policy:
- **Bounded channel** (capacity `N`, e.g. 256). Use **coalesce-on-key**: events keyed
  `(subscriptionId, kind, targetContainerRuntimeId)` collapse to the most-recent, carrying `coalescedCount`. A
  full channel after coalescing drops oldest distinct keys and bumps `droppedCount` (surfaced via
  `desktop_list_watches`) so loss is observable, never silent.
- **Debounce** `structure_changed` per key (e.g. 100 ms quiet window) so a list repopulating row-by-row emits one
  event, not hundreds.
- Exact capacity/debounce constants are tunable **plan-time** parameters (behind named consts), not wire contract.

## 9. Subscription lifecycle

- **Create:** `desktop_watch` → register on STA → store `Subscription{ id, windowId, handle, kinds, scope,
  handlers, droppedCount, channel-registration }`.
- **Explicit end:** `desktop_unwatch(id)` → unregister handlers on STA → drop.
- **Window close (auto-evict) — reuse Phase 6.** `PerceptionManager` already funnels window death through
  `WindowManager.WindowInvalidated`. The watch registry **subscribes to that same event**: when a watched
  window's handle is invalidated, its subscriptions are auto-unregistered (best-effort — the UIA handlers may
  already be dead) and dropped, and a final `window_closed` event is emitted for the top-level window if that kind
  was subscribed. (This mirrors, and must coexist with, Phase 6's `_refs.EvictWindow` on the same signal.)
- **Connection teardown (the Phase-6 forward-note, now due).** On stdio there is ONE session; when it ends the
  process exits, so leak is moot. BUT the watch registry and its `IMcpServer` capture MUST be disposed cleanly
  (unregister all UIA handlers, complete the channel, stop the worker) via an `IHostedService.StopAsync` /
  `IDisposable` so a future multi-session transport doesn't root subscriptions. Registering the pump as a hosted
  service gives this for free.

## 10. Security & redaction (load-bearing)

- **Deny-list at subscribe:** `desktop_watch` on a credential/secure-desktop window → `TargetDenied` (reuse the
  perception deny-list). No event stream out of a credential store.
- **INV-5 on every payload `name`:** the payload-build (on the STA) runs the source through the existing
  `IsPassword` → `"[REDACTED]"` path; a `focus_changed`/`structure_changed` whose source is a password field
  emits `name:"[REDACTED]"`, never the value. Fail-closed: if password-ness can't be read, redact.
- **Focus-leak containment (§7):** focus events for other processes are dropped before a payload is built, so a
  watch on w1 cannot observe focus/text of an unrelated app.
- **No new value exposure:** payloads never carry element *values* (only controlType/name/bounds); detail requires
  an explicit `desktop_get_text`/`snapshot` call, which already redacts.

## 11. Concurrency & the single-STA invariant

Registration, unregistration, and payload-build all run on the one query STA (serial). The **only** new threads
are (a) the UIA COM callback threads (which only enqueue — no UIA reads) and (b) one worker task (which marshals
onto the STA for builds and calls `SendNotificationAsync` off it). Coalescing/debounce guarantee event
payload-builds are a *bounded, low-priority* load on the STA relative to the agent's interactive reads. No change
to the action-STA model. (If profiling later shows event-builds contending with reads, a dedicated read-only
"events STA" is a clean future refinement — noted, not v1.)

## 12. Non-goals / explicitly deferred

Multi-connection / HTTP/SSE (§2); `property_changed`/`text_changed` granular streams (§5); desktop-global
new-window watch; OCR/vision events (Phase 9); persistence of subscriptions across restarts; a bundled
watch-and-act macro.

## 13. Testing strategy

- **Headless (Category!=Desktop) pure-core units** — the bulk of the logic is made STA-free and testable:
  - `EventCoalescer` — keying/collapse/debounce/`coalescedCount`/`droppedCount` over a synthetic event stream.
  - `WatchPayloadBuilder` — maps a (captured event + a fake element reader) → wire payload; INV-5 redaction
    (password source → `[REDACTED]`), null-source (`window_closed`) → nulls, focus-process filter (§7).
  - `WatchRegistry` — create/unwatch idempotency, `WindowInvalidated` auto-evict, `list_watches`.
  - Wire-shape test (serialize the notification params; keys present/absent per JsonIgnore).
- **Desktop (Category=Desktop, maintainer/interactive-run)** — real UIA against the WPF TestApp: subscribe
  `window_opened`, click the `ModalButton`, assert a `window_opened` notification arrives; subscribe
  `structure_changed`, hit `RebuildItemsButton`, assert one coalesced event; `desktop_unwatch` stops delivery.
  (These need a captured `IMcpServer`/notification sink — use a test double sink that records notifications.)
- CI: headless gate as always; the Desktop event tests join the maintainer/interactive runner (Phase-9 CI item).

## 14. Open questions to resolve at plan time (spec-vs-plan boundary)

These are **implementation** confirmations, not design forks — the plan resolves each against live code:
1. Exact FlaUI 5.0.0 `Register*Event` signatures + `TreeScope` args + the handler delegate shapes; **how to
   attach a `CacheRequest` to an event registration** so the delivered source carries cached `RuntimeId`/
   `ProcessId`/`ControlType`/`Name`/`BoundingRectangle` (§6 — the linchpin; confirm FlaUI's cache API,
   `element.Cached*` accessors, and that these read without a live round-trip); and how to unregister
   (`RemoveAutomationEventHandler` / `Automation.UnregisterAllEvents` / disposal).
2. The precise `IMcpServer` injection point in the SDK's stdio session (tool-method param vs hosted-service
   resolve vs `IMcpServer` from DI) and the exact `SendNotificationAsync(method, params)` overload + params
   serialization path (reuse `ToolResponse`'s JSON conventions). *AGY-AFTER Seat D asserts `IMcpServer` is
   cleanly injectable into a background `IHostedService` pump for the single stdio session — treat as likely but
   MEASURE it first (build a spike that emits one notification); it is the one remaining feasibility risk.*
3. `Channel` capacity / debounce constants (start 256 / 100 ms; tune).
4. Whether `WindowInvalidated` currently carries enough (it passes `windowId` string) to key the watch-evict, or
   needs the same treatment as Phase 6's `EvictWindow` (it should be identical — same signal).

## 15a. Client-side delivery risk (AGY-AFTER round-2, Seat F — LOAD-BEARING)

Server-side push is necessary but **not sufficient**: an MCP *host* may silently drop unsolicited server→client
notifications unless it is wired to surface them to the model. If the host does not inject
`notifications/flaui/desktop_event` into the agent's conversation, the whole feature is inert — the agent is
deaf. This is the mirror of §14's server-side feasibility risk and is **the single biggest gate on real value**.
- **Plan-time spike (before building the pipeline):** confirm that the target host (Claude Code, and at least
  one other MCP client) actually delivers a server notification to the model's context. If it does → proceed with
  pure push.
- **Fallback design if hosts don't reliably surface push (keep in pocket):** a **drainable event queue** — the
  same capture→coalesce pipeline buffers events server-side per subscription, and the agent calls
  `desktop_drain_events(subscriptionId, max?)` to pull queued events on its own cadence (long-poll style). This
  makes the feature host-agnostic (no reliance on client push handling) at the cost of the agent having to poll a
  cheap drain — still far better than re-snapshotting, and it reuses the entire server-side pipeline. Decide
  push-only vs push+drain vs drain-only **after the spike**; the wire payload (§4) is identical either way.

## 16. Implementation-hardening constraints (AGY-AFTER round-2 — MUST be honored by the plan)

1. **`CacheRequest` must be PASSED to the registration, not `Activate()`d on the shared STA (Seat H, Critical).**
   UIA3 `CacheRequest.Activate()` sets a **thread-local** active cache; leaking it on the shared query STA (e.g.
   an exception between Activate and Dispose) would poison EVERY subsequent synchronous `desktop_snapshot`/
   `desktop_find` on that STA (unhydrated proxy elements / crashes). So the cache MUST be associated with the
   event registration as a parameter (`AddAutomationEventHandler(..., cacheRequest)`), never thread-activated on
   the shared STA. If FlaUI only exposes the `Activate()` form for events, wrap it in a guaranteed
   `using`/`finally` scoped to the single registration call AND verify (test) that a synchronous snapshot on the
   same STA immediately after is un-poisoned. (§14 open-question #1 must confirm the FlaUI API shape.)
2. **Registration is TRANSACTIONAL — all-or-nothing (Seat G, Critical).** `desktop_watch` for N event kinds must
   register them atomically: on ANY registration failure, unregister every handler already added for that call,
   then throw — so a partial failure never orphans a live UIA handler the caller has no `subscriptionId` for
   (a permanent event-spam + memory leak). §9 lifecycle amended accordingly.
3. **The worker pump loop is bulletproof (Seat G, Important).** The single event-processing worker wraps each
   per-item build/emit in `try/catch` (log-and-continue) so a freak `COMException` (reading a prop, minting a
   ref, or a broken pipe on `SendNotificationAsync`) can NEVER fault the worker `Task` and silently kill delivery
   for ALL subscriptions. (Mirrors `AutomationDispatcher`'s existing catch-all-on-background-thread discipline.)
   A `SendNotificationAsync` failure specifically (client gone / pipe broken) is treated as terminal-for-the-
   session (stop the pump cleanly), not a per-event crash.
4. **The COM capture handler null-tolerates missing cached props (Seat I, Minor).** Global `window_opened`/
   `focus_changed` fire for elevated/inaccessible apps (UAC, Task Manager); a non-elevated server's cache
   pre-fetch may return `null`/`0` for `ProcessId`/`Name`. The capture handler treats missing cached props as
   "drop this event" (can't PID-filter it safely) and NEVER throws on the callback thread.

## 15. Version & docs

v0.7.7 → **v0.8.0** (csproj + installer.iss). CHANGELOG `[0.8.0]` (Added: `desktop_watch`/`desktop_unwatch`/
`desktop_list_watches` — push UIA events as MCP notifications over stdio). ROADMAP: new Phase 8 delivered entry;
move "UIA event streaming (`desktop_watch`)" out of the v2 table into shipped, noting it landed over stdio (not
SSE). README: new "Event streaming" section + the three tools + the notification method name + the best-effort/
coalescing/redaction notes. SKILL.md: a "react instead of poll" driving note (watch → handle notification →
snapshot the `ref`).
