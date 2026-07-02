# FlaUI.Mcp — Phase 6: RefRegistry window-close eviction (design)

**Status:** design (spec) · **Date:** 2026-07-03 · **Target release:** v0.7.6
**Supersedes/depends on:** builds on the v0.7.3 `desktop_find` durable-ref work (which introduced
`cached: el` COM-pinning refs) and the v0.7.3a INV-8 ref-resolution hardening.

## 1. Problem

`RefRegistry` (`src/FlaUI.Mcp.Core/Perception/RefRegistry.cs`) is a **process-wide singleton**
(`Program.cs:30`, `AddSingleton`). It holds three dictionaries keyed by `windowId`:

- `_byWindow`   : `windowId -> (ref -> Entry)`, where `Entry = (ElementDescriptor, AutomationElement? Cached)`
- `_counter`    : `windowId -> int` (monotonic ref counter, never reset)
- `_snapshotSeq` : `windowId -> int` (monotonic snapshot sequence)

`BeginSnapshot(windowId)` replaces a *single* window's ref map (so refs don't grow unboundedly **within**
one window), and `desktop_find`'s `Register` is additive within a window. But **nothing removes a
window's entries when that window closes.** Over a long, exploration-heavy session that launches/closes
many windows (or issues many additive finds), `_byWindow` accumulates one entry per `windowId` ever
seen, each `Entry` potentially pinning a live `AutomationElement` — a COM/UIA-provider RCW.

**Impact:** monotonic managed-memory growth + pinned UIA provider objects across a long session. This is
a *memory-hygiene* defect, not an OS-stability timebomb — the pinned objects are COM RCWs, not GDI/USER
handles; dropping the managed reference lets the GC finalize the RCW (the finalizer marshals the COM
release). `ROADMAP.md:138` scopes this as "RefRegistry eviction on window close."

**Second store with the same shape (in scope):** `WindowManager` (`Core/Windows/WindowManager.cs`) holds
`_handles : windowId -> Window` (a live FlaUI/COM element — a COM pin), `_hwnds : windowId -> IntPtr`,
and `_watched : windowId -> Process`. These are cleared only by `Invalidate(handle)`, whose only callers
are the `proc.Exited` watcher and our `CloseAsync`. A window closed by the **user or the app** (process
still alive) leaves `_handles`' COM pin resident forever — the same leak class. The eviction path below
routes through `Invalidate`, so it cleans both stores.

**Explicitly out of scope:**
- **Per-connection registry.** The `RefRegistry` doc-comment mentions "per-connection in Phase 6," but on
  the current **stdio** transport the process lifetime *is* the connection lifetime, so per-connection
  isolation is unobservable. It ships with HTTP/SSE multi-connection transport, which `ROADMAP.md:137`
  already defers alongside this. (agy concurred: YAGNI on stdio.)
- **`SnapshotCache`.** Already a bounded pure-LRU store (`Capacity = 32`) of **immutable data models with
  no COM pins** — it does not leak. Left untouched.

## 2. Goals / non-goals

**Goals**
- G1. A closed window's `RefRegistry` entry (all three dicts) is reclaimed, releasing its cached COM pins.
- G2. `WindowManager`'s per-window COM pin (`_handles`) is reclaimed on the same close signal.
- G3. No object-permanence regression: a ref is only ever evicted when its window is **genuinely gone**
  (never for a live, backgrounded, or idle window). A ref for a still-open window keeps working.
- G4. Headless-testable core: the eviction and liveness-sweep *logic* must run on the RDP CI box (no real
  Desktop/UIA), via injected seams.

**Non-goals**
- N1. Not bounding memory for *simultaneously-open* windows (that is OS-bounded by real UI; the pin-drop
  alternative that would tighten it was rejected — see §7).
- N2. Not introducing any background thread, timer, or UIA event subscription.
- N3. Not changing ref/snapshot identity semantics, INV-5 (redaction), or INV-8 (strict resolution).

## 3. Design

Two eviction paths, both funnelled through the **existing** `WindowManager.Invalidate(handle)`
chokepoint, so there is exactly one place that means "this window is gone."

### 3.1 Push path (primary) — close signal → event → evict

`WindowManager` gains an event raised **inside** `Invalidate`, *after* it clears its own dicts:

```csharp
/// <summary>Raised when a window handle is invalidated (process exit or close_window). Carries the
/// windowId. Subscribers must be thread-safe: this can fire on a ThreadPool thread (proc.Exited).</summary>
public event Action<string>? WindowInvalidated;

public void Invalidate(WindowHandle handle)
{
    // Gate the broadcast on state actually being removed: a double-invalidate (proc.Exited racing
    // CloseAsync, or a sweep hitting an already-gone id) must not fire phantom teardown events.
    // Single '|' (not '||') so both removals always run.
    bool removed = _handles.TryRemove(handle.Id, out _)
                 | _hwnds.TryRemove(handle.Id, out _);
    if (_watched.TryRemove(handle.Id, out var p))
    {
        try { p.Dispose(); } catch { }
        removed = true;
    }
    if (removed)
        WindowInvalidated?.Invoke(handle.Id);   // NEW — after own-state teardown, only if state was live
}
```

`RefRegistry` gains the eviction sink:

```csharp
/// <summary>Evict all state for a closed window (refs + counter + snapshot seq), releasing any cached
/// COM element pins to the GC. Idempotent: a windowId with no entries is a no-op. Thread-safe; may be
/// called off the query STA (e.g. from a process-exit callback) — it only drops managed references and
/// never invokes a COM method on the cached element, so the release marshals safely on GC finalization.
/// Dropping _counter/_snapshotSeq is safe because windowId is a monotonic "w{n}" id that is never
/// reused, so a future window can never inherit a stale counter and alias an old ref.</summary>
public void EvictWindow(string windowId)
{
    lock (_gate)
    {
        _byWindow.Remove(windowId);
        _counter.Remove(windowId);
        _snapshotSeq.Remove(windowId);
    }
}
```

**Wiring** (single subscription, process-lifetime, both singletons) in the `PerceptionManager`
constructor — dependency direction Perception → Windows, which already holds:

```csharp
public PerceptionManager(WindowManager windows, RefRegistry refs, SnapshotCache cache)
{
    _windows = windows; _refs = refs; _cache = cache;
    _windows.WindowInvalidated += _refs.EvictWindow;   // NEW
}
```

### 3.2 Pull path (backstop) — on-access liveness sweep

The push path misses windows closed by the **user or the app** while the process stays alive (neither
`proc.Exited` nor `CloseAsync` fires). A cheap on-access sweep in `WindowManager` reconciles its known
handles against live-window state and routes any dead handle through the **same** `Invalidate` (so both
stores are cleaned by one code path):

```csharp
[DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);

/// <summary>Best-effort memory hygiene: invalidate any tracked handle whose HWND is no longer a live
/// window (user/app closed it without a process exit). Routes through Invalidate so RefRegistry +
/// _handles are both reclaimed. <paramref name="isAlive"/> is injectable for headless tests (default =
/// Win32 IsWindow). Snapshot the pairs first so we don't mutate _hwnds while enumerating it.</summary>
internal void PruneClosedWindows(Func<IntPtr, bool>? isAlive = null)
{
    isAlive ??= IsWindow;
    foreach (var (id, hwnd) in _hwnds.ToArray())
        if (hwnd == IntPtr.Zero || !isAlive(hwnd))
            Invalidate(new WindowHandle(id));
}
```

**Call sites — the sweep must run on the tight-loop paths, not just `list_windows`.** An
exploration-heavy agent commonly loops `open dialog window → snapshot → close dialog → snapshot main`
over *separate* top-level windows **without** ever re-calling `list_windows`; those dialog windows don't
fire `proc.Exited` (the host process stays alive), so hooking the sweep only into `ListWindowsAsync`
would leak their COM pins in exactly the sessions this phase targets. So call `PruneClosedWindows()` at
the **entry of every registry-growing / window-resolving read**:

1. `desktop_snapshot` (`BuildModelAsync`) — before window resolution.
2. `desktop_find` (`FindAsync`) — before window resolution.
3. `ListWindowsAsync` — already enumerating.

`IsWindow` is microseconds and `_hwnds` is small (bounded by windows-opened-since-last-sweep), so paying
the sweep before each snapshot/find is negligible and bounds memory in the tight loop. The sweep needs no
STA — it is pure Win32 (`IsWindow`) + `ConcurrentDictionary` ops — so it can run at the top of the
`PerceptionManager` snapshot/find flow (e.g. `_windows.PruneClosedWindows();`) or inside the existing
`RunQueryAsync`/`ListWindowsAsync` lambda; the plan fixes the exact call sites. It never evicts the
window being snapshotted/found (that window is alive, so `IsWindow` keeps it).

```csharp
public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(bool includeBounds) =>
    _dispatcher.RunQueryAsync<IReadOnlyList<WindowInfo>>(() =>
    {
        PruneClosedWindows();          // NEW — best-effort hygiene before enumerating
        var foreground = GetForegroundWindow();
        // …unchanged…
    });
```

### 3.3 Data flow (summary)

```
process exit           ─┐
close_window           ─┼─►  WindowManager.Invalidate(id) ──► clears _handles/_hwnds/_watched
snapshot/find/list ────┘        (PruneClosedWindows sweep → dead hwnds → Invalidate)  │
                                                                            ▼
                                              WindowInvalidated(id) event ──► RefRegistry.EvictWindow(id)
                                                                            │        (3 dicts dropped)
                                                                            ▼
                                              cached AutomationElement RCWs + Window pin → GC-eligible
```

## 4. Edge cases & invariants

- **Idempotency.** `EvictWindow` on an unknown/already-evicted `windowId` is a no-op. `PruneClosedWindows`
  may call `Invalidate` on the same id twice across successive sweeps — harmless.
- **Thread-safety.** `Invalidate` (hence the event, hence `EvictWindow`) may run on a ThreadPool thread
  via `proc.Exited`. `EvictWindow` takes `_gate` (consistent with every other `RefRegistry` mutator) and
  performs **only** dictionary removals — no COM method is called on the cached element from any thread.
  The COM release is deferred to GC finalization, which marshals to the owning apartment.
- **No re-entrancy.** The event is raised *after* `WindowManager` finishes its own teardown; the sole
  subscriber (`RefRegistry.EvictWindow`) touches only `RefRegistry` and never calls back into
  `WindowManager`. No sweep→invalidate→sweep loop is possible (`PruneClosedWindows` isn't a subscriber).
- **Enumeration safety.** `PruneClosedWindows` snapshots `_hwnds.ToArray()` before iterating, so the
  `Invalidate`-driven `_hwnds.TryRemove` inside the loop can't corrupt the enumerator.
- **Object permanence (G3).** Eviction only ever targets a window that is gone (real close signal, or
  `IsWindow==false`). A ref for a still-open window is never evicted, so `desktop_click(e1)` on a
  visibly-open background window keeps working. Using a ref *after* its window is gone yields the existing
  `REF_NOT_FOUND` → "take a fresh desktop_snapshot" (unchanged, correct).
- **`windowId` never reused → dropping counters is safe.** `windowId` is `w{Interlocked.Increment}`
  (`WindowManager.cs:110`), not the HWND, so a recycled OS handle never re-adopts an evicted window's
  `windowId`. Therefore removing `_counter`/`_snapshotSeq` cannot let a future window inherit a stale
  counter and alias a previously-issued ref. (This is why we can safely evict all three dicts rather than
  preserving the counters.)
- **HWND recycling within the sweep (bounded residual).** If a closed window's HWND is recycled to a new
  window before a sweep observes it, `IsWindow` returns true and the sweep skips eviction; that window's
  memory lingers until the next real signal (process exit) or a later sweep after the recycled window also
  closes. This is memory-only best-effort — it introduces **no** correctness hazard, because ref
  resolution still verifies live `RuntimeId`/descriptor identity (INV-8) and fails closed on mismatch.
- **INV-5 / INV-8 untouched.** Eviction removes whole-window state; it does not alter redaction or the
  strict/lenient resolution paths.

## 5. Components (units of change)

| # | Unit | File | Change |
|---|------|------|--------|
| U1 | `RefRegistry.EvictWindow(string)` | `Core/Perception/RefRegistry.cs` | New public method (3-dict removal under `_gate`) |
| U2 | `WindowManager.WindowInvalidated` event + raise | `Core/Windows/WindowManager.cs` | New event; `Invalidate` raises it after teardown |
| U3 | `WindowManager.PruneClosedWindows(Func<IntPtr,bool>?)` + `IsWindow` P/Invoke | `Core/Windows/WindowManager.cs` | New sweep; called at entry of `desktop_snapshot`/`desktop_find`/`ListWindowsAsync` |
| U4 | Subscription wiring | `Core/Perception/PerceptionManager.cs` | Ctor: `_windows.WindowInvalidated += _refs.EvictWindow` |
| U5 | Version + docs | `*.csproj`, `installer.iss`, `CHANGELOG.md`, `ROADMAP.md` | 0.7.5 → 0.7.6; mark Phase 6 |

## 6. Testing

**Headless (runs on the RDP CI box):**
- `RefRegistry.EvictWindow`:
  - register refs for `w1` and `w2`; `EvictWindow("w1")`; `Lookup("w1", ref)` throws `REF_NOT_FOUND`;
    `w2`'s refs still resolve.
  - `EvictWindow` on an unknown id → no throw; on an already-evicted id → no throw (idempotent).
  - after evicting `w1`, a fresh `BeginSnapshot("w1")` + `Register` starts a clean map (no stale carry).
- `WindowManager.PruneClosedWindows` sweep logic via the injected predicate: with a fake `isAlive` that
  reports a subset dead, assert `Invalidate` fired for exactly the dead ids (observed through the
  `WindowInvalidated` event and/or `_handles` removal). *(Requires `WindowManager` to be constructable
  headless; the existing `WindowManagerTests` establish this. If a real dispatcher/UIA is needed, the
  sweep's pure logic is extracted behind the predicate seam so the decision — which ids are dead — is
  unit-tested without touching real HWNDs. The plan resolves the exact seam.)*
- Event → eviction wiring: raising `WindowInvalidated("w1")` (or calling `Invalidate`) results in `w1`'s
  refs being `REF_NOT_FOUND`, proving the `PerceptionManager` subscription is connected.

**Category=Desktop (maintainer-run, live):**
- Launch an app, snapshot/find (mint refs), close it → subsequent ref use is `REF_NOT_FOUND` and the
  registry no longer holds the window (validates the live process-exit/close signal end-to-end).

## 7. Alternatives considered (and why rejected)

- **LRU cap on retained windows/refs (my original backstop idea).** Rejected — a size cap can evict a
  **live, still-open** window's refs while the agent explores a deep dialog tree, producing
  `REF_STALE_UNRESOLVABLE` on a window that is plainly still open. That gaslights the consuming agent and
  breaks trust in refs (agy's object-permanence critique — accepted).
- **"Drop the pin, keep the string" (agy's reframe).** Keep every ref forever; null the `Cached` COM
  element on all non-active windows at each window-switch, re-resolving via the descriptor on revisit.
  Elegant and bounds live-window COM tightly, but it **breaks re-resolution of identity-less elements**
  (no `AutomationId`, no `Name`) in backgrounded windows — which is the exact case `cached: el` was added
  for (`PerceptionManager.cs:277-279`: descriptor re-resolution alone cannot re-find them). It would
  reintroduce a permanence break for that subset — the very failure it set out to avoid. Rejected as the
  core mechanism; a future, *identity-safe* pin-drop (only drop pins for elements that carry a
  re-resolvable identity) remains a possible follow-on optimization if live-window COM is ever measured to
  bite, but is YAGNI now.
- **Salt the `windowId` to make HWND reuse impossible.** Unnecessary — `windowId` is already a
  never-reused `w{counter}`, so the aliasing hazard the salt would prevent does not exist.

## 8. Rollout

Additive, backward-compatible: no wire-contract change, no new tool, no changed error codes. Behavior
change is purely internal memory reclamation + the existing `REF_NOT_FOUND` surfacing sooner for
already-closed windows. Version 0.7.5 → 0.7.6; `CHANGELOG [0.7.6]`; `ROADMAP` Phase 6 marked, and the
"Not phased here … RefRegistry eviction on window close" line updated to reflect delivery.
