# Spike A findings — FlaUI 5.0.0 UIA event registration + CacheRequest API

**Status:** ✅ GATE CLEARED. Resolves plan §14.1 + §16.1. Probe `_SpikeAProbe.cs` compiled (compiler pins every
signature) and PASSED as a Desktop test (`Passed [11 s]`), then was deleted. All FlaUI calls below are copy-paste
ready for Task 7 (`Uia3EventSource`). Package: **FlaUI.Core / FlaUI.UIA3 5.0.0**, `lib/net8.0-windows7.0` (no
net10 lib ships; net8 is the highest and is what the `net10.0-windows` build resolves via compat).

## Confirmed signatures (compiler-verified)

```csharp
using FlaUI.Core;                 // CacheRequest
using FlaUI.Core.Definitions;     // TreeScope, AutomationElementMode, StructureChangeType
// automation is AutomationBase, reachable as `desktop.Automation` (AutomationElement.Automation).

// ── REGISTER (returns a handler object you keep to unregister) ──
// window_opened / window_closed — global, on the Desktop root:
var h1 = desktop.RegisterAutomationEvent(
    automation.EventLibrary.Window.WindowOpenedEvent, TreeScope.Descendants,
    (AutomationElement element, EventId evtId) => { /* COM thread */ });
var h2 = desktop.RegisterAutomationEvent(
    automation.EventLibrary.Window.WindowClosedEvent, TreeScope.Descendants, (element, evtId) => { });

// structure_changed — scoped to the window (or scope element) subtree:
var h3 = win.RegisterStructureChangedEvent(TreeScope.Subtree,
    (AutomationElement element, StructureChangeType changeType, int[] runtimeId) => { });

// focus_changed — global, on the automation (NOT on an element):
var h4 = automation.RegisterFocusChangedEvent((AutomationElement element) => { });
```

- `EventId` group accessor: `automation.EventLibrary.Window.WindowOpenedEvent` / `.WindowClosedEvent`.
- `RegisterAutomationEvent` returns concrete `FlaUI.UIA3.EventHandlers.UIA3AutomationEventHandler`
  (`: FlaUI.Core.EventHandlers.AutomationEventHandlerBase, IAutomationEventHandler`).
- Delegate shapes (exact): automation-event `Action<AutomationElement, EventId>`; structure
  `Action<AutomationElement, StructureChangeType, int[]>`; focus `Action<AutomationElement>`.

## UNREGISTER path (XML-doc confirmed; Task 7 must self-marshal onto the query STA)

Keep each register's returned handler and pass it to the matching un-register on the SAME STA it was registered on
(UIA is apartment-affine — a Dispose from an MTA/ThreadPool thread throws `RPC_E_WRONG_THREAD`, so Task 7's
disposable must `dispatcher.RunQueryAsync(...)` the unregister, mirroring `PerceptionManager.cs:38`):

```csharp
// CORRECTED (Task 7b reflection-verified: the Unregister* methods are on FrameworkAutomationElement, NOT on
// AutomationElement — register/unregister are split across two interfaces in FlaUI 5.0.0):
element.FrameworkAutomationElement.UnregisterAutomationEventHandler(AutomationEventHandlerBase handler);       // window_opened/closed
element.FrameworkAutomationElement.UnregisterStructureChangedEventHandler(StructureChangedEventHandlerBase h); // structure_changed
automation.UnregisterFocusChangedEvent(FocusChangedEventHandlerBase handler);       // focus_changed (this ONE is on AutomationBase — exact)
// Blunt fallback (used by the probe for cleanup): automation.UnregisterAllEvents();
```

The register return value (`AutomationEventHandlerBase`/`StructureChangedEventHandlerBase`/`FocusChangedEventHandlerBase`)
is assignable to the matching un-register parameter directly. NOTE: the element-side Unregister* methods are reached
via `element.FrameworkAutomationElement.*`, not `element.*` (compiler-confirmed in Task 7b `6c281a7`); only the
focus unregister is directly on `automation` (`AutomationBase`).

## CacheRequest — `Activate()`-only (the §16.1 decision)

**There is NO `Register*` overload that takes a `CacheRequest` parameter.** The only mechanism is the thread-local
`CacheRequest.Activate()`. So Task 7 MUST scope it tightly around the registration call on the query STA:

```csharp
var cr = new CacheRequest {
    AutomationElementMode = AutomationElementMode.None,   // cache-only elements (cheapest)
    TreeScope = TreeScope.Element,
};
cr.Add(automation.PropertyLibrary.Element.ProcessId);
cr.Add(automation.PropertyLibrary.Element.RuntimeId);
using (cr.Activate()) {           // <-- TIGHT scope: only the Register call inside
    handler = desktop.RegisterAutomationEvent(evtId, scope, callback);
}                                  // <-- popped immediately; the shared query STA is un-poisoned after
```

**Poison-guard PROVEN:** after a tightly-scoped `using(cr.Activate())`, a fresh hydrated read on the SAME query STA
returned `ControlType=Button, Name=OK` (`hydratedAfterActivate='Button|OK'`) — NOT cache-only blanks. Leaking the
Activate (not disposing) would poison subsequent reads; the `using` prevents it. `CacheRequest` members:
`Add(PropertyId)`, `Activate()→IDisposable`, `Push`/`Pop`, `Current`, `AutomationElementMode`, `TreeScope`.

## Cached property read in the callback — WORKS, no live round-trip

Inside the `window_opened` COM callback, `element.Properties.ProcessId.ValueOrDefault` returned the cached PID
(`20024`, == the app that owns the modal) and `element.Properties.RuntimeId.ValueOrDefault` returned `42,17434590`
— **no throw, no live cross-apartment fetch** (the props were in the cache request active at registration). So the
`.Properties.X.ValueOrDefault` accessor reads the cached value when the element was delivered under a cache request.
Join RuntimeId as `string.Join(",", rid)`; guard with try/catch and treat null/throw as undeliverable (§16.4).

## Empirical results (probe run)

```
openedFired=True  openedPid=20024  openedRid=42,17434590  openedReadThrew=False
openedHandlerType=FlaUI.UIA3.EventHandlers.UIA3AutomationEventHandler
structureFired=True   focusFired=True
hydratedAfterActivate='Button|OK'   (un-poisoned ✓)
closedFired=False  (see caveat)
```

## Caveat / flag for Task 7 + Task 10 — UIA `WindowClosed` did not deliver in the probe

Registering `WindowClosedEvent` globally on the Desktop root and closing the `ShowDialog` modal (via its `ModalOk`
button) did **not** deliver a callback within 5 s (`closedFired=False`). So the dead-source cached-read path is
**unverified**, and Task 7 must NOT depend on the UIA `WindowClosed` event as the reliable close signal. This is
consistent with the design, which already treats `window_closed` as potentially source-less
(`IEventSourceReader.HasSource=false` → null ref/name/bounds) and drives the reliable close via the **Phase-6
`WindowManager.WindowInvalidated` chokepoint** — Task 8 `OnWindowInvalidated` enqueues a *synthetic* `window_closed`
(null source). Action for Task 10: assert `window_closed` delivery via the synthetic (WindowInvalidated) path, not
the raw UIA event; optionally probe a non-modal child-window close to see if UIA `WindowClosed` delivers there.

## What Task 7 can now write directly

- Register all 3 kinds with the exact calls above, each under a tight `using(cr.Activate())`.
- Read cached `ProcessId`/`RuntimeId` off the callback element via `.Properties.X.ValueOrDefault` (fail-soft).
- Keep the returned handler; unregister via the matching `Unregister*` **self-marshaled onto the query STA**.
- No `CacheRequest` param exists — the `Activate()` scoping is mandatory, and the poison-guard is satisfied by the
  `using` block, verified above.
