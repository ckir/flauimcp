# FlaUI.Mcp — Phase 6: RefRegistry window-close eviction — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reclaim a closed window's `RefRegistry` state (and `WindowManager`'s per-window COM pin) so a long, exploration-heavy session no longer grows managed memory / pinned UIA providers unboundedly.

**Architecture:** Two eviction paths funnel through the single existing `WindowManager.Invalidate(handle)` chokepoint. (1) **Push:** `Invalidate` raises a new `WindowInvalidated` event (gated on state actually being removed); `PerceptionManager` subscribes `RefRegistry.EvictWindow` to it in its constructor. (2) **Pull backstop:** a cheap on-access `PruneClosedWindows` sweep (Win32 `IsWindow`) reconciles tracked handles against live windows and routes dead ones through the same `Invalidate`, called at the entry of the snapshot family (`BuildModelAsync`), `FindAsync`, and `ListWindowsAsync`. No background thread, timer, or UIA event subscription. `windowId` is a never-reused `w{n}`, so dropping all three registry dicts is alias-safe.

**Tech Stack:** C# / .NET 10, FlaUI/UIA3, xUnit. Source spec: `docs/superpowers/specs/2026-07-03-flaui-mcp-phase6-refregistry-eviction-design.md`.

**Testability seam (resolved from spec §6):** Constructing a real `WindowManager` requires live UIA (every existing `WindowManager` test is `[Trait("Category","Desktop")]`). So:
- **Headless (CI, `Category!=Desktop`):** `RefRegistry.EvictWindow` (pure dict ops) and the pure liveness decision extracted as `WindowManager.DeadWindowIds(...)` (a `static` — callable without constructing `WindowManager`, so no UIA).
- **Category=Desktop (maintainer-run, live):** the `Invalidate`→`WindowInvalidated`→`EvictWindow` wiring and the `PruneClosedWindows` sweep firing `Invalidate`, since both need a real `WindowManager` instance.

---

## File Structure

| File | Responsibility | Change |
|------|----------------|--------|
| `src/FlaUI.Mcp.Core/Perception/RefRegistry.cs` | Ref store | **Add** `EvictWindow(string)` (U1) |
| `src/FlaUI.Mcp.Core/Windows/WindowManager.cs` | Window lifecycle | **Add** `WindowInvalidated` event + gated raise in `Invalidate` (U2); **add** `IsWindow` P/Invoke, `PruneClosedWindows`, static `DeadWindowIds` (U3) |
| `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` | Perception façade | Ctor subscription (U4); sweep calls at `BuildModelAsync`/`FindAsync` entry (U4) |
| `test/FlaUI.Mcp.Tests/Perception/RefRegistryTests.cs` | Headless ref-store tests | **Append** `EvictWindow` tests (U1) |
| `test/FlaUI.Mcp.Tests/Windows/WindowLivenessTests.cs` | Headless liveness-decision tests | **Create** — `DeadWindowIds` unit tests (U3) |
| `test/FlaUI.Mcp.Tests/Windows/WindowManagerTests.cs` | Desktop-category live tests | **Append** wiring + sweep integration tests (U2/U3/U4) |
| `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj` | Version | 0.7.5 → 0.7.6 (U5) |
| `installer/flaui-mcp.iss` | Installer version | 0.7.5 → 0.7.6 (U5) |
| `CHANGELOG.md` | Changelog | Add `[0.7.6]` (U5) |
| `ROADMAP.md` | Roadmap | Mark Phase 6 delivered (U5) |

**Build command (whole solution):** `dotnet build -c Release`  → expect `Build succeeded` with `0 Error(s)`.
**Headless test gate (what CI runs):** `dotnet test -c Release --filter "Category!=Desktop"` → expect `Passed!` with `Failed: 0`.

---

## Task 1: `RefRegistry.EvictWindow` (U1) — headless TDD

Evict all state for a closed window (all three dicts) under `_gate`, releasing cached COM pins to the GC. Idempotent. This is the push-path sink.

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/RefRegistry.cs`
- Test: `test/FlaUI.Mcp.Tests/Perception/RefRegistryTests.cs`

- [ ] **Step 0: State-verify**

Open `src/FlaUI.Mcp.Core/Perception/RefRegistry.cs` and confirm: it is `public sealed class RefRegistry`; it declares `private readonly object _gate = new();`, `private readonly Dictionary<string, Dictionary<string, Entry>> _byWindow = new();`, `private readonly Dictionary<string, int> _counter = new();`, `private readonly Dictionary<string, int> _snapshotSeq = new();`; and it has `public string BeginSnapshot(string windowId)`, `public string Register(string windowId, ElementDescriptor descriptor, AutomationElement? cached)`, `internal Entry Lookup(string windowId, string @ref)` (throws `ToolException(ToolErrorCode.RefNotFound, …)` when absent). If any differ, STOP and report `STATE_MISMATCH: <what>`.

- [ ] **Step 1: Write the failing tests**

Append to `test/FlaUI.Mcp.Tests/Perception/RefRegistryTests.cs`, inside the existing `RefRegistryTests` class (before the closing brace). These reuse the file's existing `Desc(string aid)` helper and `using`s (`FlaUI.Mcp.Core.Errors`, `FlaUI.Mcp.Core.Perception`, `Xunit`):

```csharp
    [Fact]
    public void EvictWindow_removes_all_refs_for_that_window()
    {
        var r = new RefRegistry();
        r.BeginSnapshot("w1");
        var e1 = r.Register("w1", Desc("a"), cached: null); // "e1"
        r.EvictWindow("w1");
        var ex = Assert.Throws<ToolException>(() => r.Lookup("w1", e1));
        Assert.Equal(ToolErrorCode.RefNotFound, ex.Code);
    }

    [Fact]
    public void EvictWindow_leaves_other_windows_intact()
    {
        var r = new RefRegistry();
        r.BeginSnapshot("w1");
        r.BeginSnapshot("w2");
        var w1Ref = r.Register("w1", Desc("a"), cached: null);
        var w2Ref = r.Register("w2", Desc("b"), cached: null);
        r.EvictWindow("w1");
        Assert.Throws<ToolException>(() => r.Lookup("w1", w1Ref)); // w1 gone
        Assert.Equal("b", r.Lookup("w2", w2Ref).Descriptor.AutomationId); // w2 survives
    }

    [Fact]
    public void EvictWindow_is_idempotent_for_unknown_and_repeated_ids()
    {
        var r = new RefRegistry();
        r.EvictWindow("never-registered"); // no throw
        r.BeginSnapshot("w1");
        r.Register("w1", Desc("a"), cached: null);
        r.EvictWindow("w1");
        r.EvictWindow("w1"); // double-evict: still no throw
    }

    [Fact]
    public void After_EvictWindow_a_fresh_snapshot_of_that_window_starts_clean()
    {
        var r = new RefRegistry();
        r.BeginSnapshot("w1");
        r.Register("w1", Desc("a"), cached: null); // e1
        r.Register("w1", Desc("b"), cached: null); // e2
        r.EvictWindow("w1");
        // Counter dropped with the window: a fresh snapshot restarts refs from e1 (windowId "w1"
        // is never reused by WindowManager, so no live stale ref can alias this new e1).
        r.BeginSnapshot("w1");
        Assert.Equal("e1", r.Register("w1", Desc("c"), cached: null));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~RefRegistryTests.EvictWindow"`
Expected: FAIL to compile with `'RefRegistry' does not contain a definition for 'EvictWindow'` (CS1061).

- [ ] **Step 3: Implement `EvictWindow`**

In `src/FlaUI.Mcp.Core/Perception/RefRegistry.cs`, add the method immediately after `BeginSnapshot` (before `Register`):

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

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~RefRegistryTests.EvictWindow"`
Expected: PASS, `Failed: 0` (4 new tests pass; existing `RefRegistryTests` still green under `~RefRegistryTests`).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/RefRegistry.cs test/FlaUI.Mcp.Tests/Perception/RefRegistryTests.cs
git commit -m "feat(perception): RefRegistry.EvictWindow — reclaim a closed window's refs (Phase 6 U1)"
```

---

## Task 2: `WindowManager.WindowInvalidated` event + gated raise (U2)

`Invalidate` gains a `WindowInvalidated` event, raised *after* it clears its own dicts and *only* when state was actually removed (so a double-invalidate — `proc.Exited` racing `CloseAsync`, or a sweep hitting an already-gone id — fires no phantom teardown).

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Windows/WindowManager.cs`
- Test: `test/FlaUI.Mcp.Tests/Windows/WindowManagerTests.cs` (Desktop-category)

- [ ] **Step 0: State-verify**

Open `src/FlaUI.Mcp.Core/Windows/WindowManager.cs` and confirm the current `Invalidate` body is exactly:

```csharp
    public void Invalidate(WindowHandle handle)
    {
        _handles.TryRemove(handle.Id, out _);
        _hwnds.TryRemove(handle.Id, out _);
        if (_watched.TryRemove(handle.Id, out var p))
            try { p.Dispose(); } catch { }
    }
```

Also confirm the fields `private readonly ConcurrentDictionary<string, Window> _handles`, `_hwnds`, `_watched` exist, and that `proc.Exited += (_, _) => Invalidate(new WindowHandle(id));` and `CloseAsync`'s `finally { Invalidate(handle); }` are the callers. If the `Invalidate` body differs, STOP and report `STATE_MISMATCH: <what>`.

- [ ] **Step 1: Write the failing test (Desktop-category)**

Append to `test/FlaUI.Mcp.Tests/Windows/WindowManagerTests.cs`, inside the `WindowManagerTests` class (the class is already `[Trait("Category","Desktop")]` and has `IClassFixture<TestAppFixture>` with `_app`):

```csharp
    [Fact]
    public async Task Invalidate_raises_WindowInvalidated_once_and_only_when_state_was_removed()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var fired = new List<string>();
        mgr.WindowInvalidated += id => fired.Add(id);

        mgr.Invalidate(handle);            // live state → fires once
        mgr.Invalidate(handle);            // already gone → no phantom fire

        Assert.Equal(new[] { handle.Id }, fired);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~WindowManagerTests.Invalidate_raises_WindowInvalidated"`
Expected: FAIL to compile with `'WindowManager' does not contain a definition for 'WindowInvalidated'` (CS1061). *(On the headless CI box this Desktop-category test is not run; it is verified on a maintainer's interactive session. It must still compile — the compile failure above is the red state.)*

- [ ] **Step 3: Add the event and raise it (gated)**

In `src/FlaUI.Mcp.Core/Windows/WindowManager.cs`, add the event declaration just after the field block (after `private int _counter;`, before the constructor):

```csharp
    /// <summary>Raised when a window handle is invalidated (process exit, close_window, or a
    /// PruneClosedWindows sweep observing a dead HWND). Carries the windowId. Fires at most once per
    /// invalidation and only when tracked state was actually removed. Subscribers must be thread-safe:
    /// this can fire on a ThreadPool thread (via proc.Exited).</summary>
    public event Action<string>? WindowInvalidated;
```

Replace the `Invalidate` method body with the gated version:

```csharp
    public void Invalidate(WindowHandle handle)
    {
        // Exactly-once gate. _handles is populated UNCONDITIONALLY by Register for every id, and
        // ConcurrentDictionary.TryRemove returns true to EXACTLY ONE caller per key — so electing
        // _handles as the SOLE gate makes WindowInvalidated fire at most once even when two threads
        // invalidate the same handle concurrently (proc.Exited racing CloseAsync, or a sweep racing
        // proc.Exited). _hwnds/_watched are best-effort (populated inside try/catch) so they cannot
        // serve as the gate — remove them unconditionally (still disposing the watched process), but
        // do NOT let their removal set `removed`.
        bool removed = _handles.TryRemove(handle.Id, out _);
        _hwnds.TryRemove(handle.Id, out _);
        if (_watched.TryRemove(handle.Id, out var p))
            try { p.Dispose(); } catch { }
        if (removed)
            WindowInvalidated?.Invoke(handle.Id);
    }
```

> **Intentional divergence from spec §3.1 (surfaced):** the spec sketched the gate as
> `removed = _handles.TryRemove(...) | _hwnds.TryRemove(...)` (plus `removed = true` in the `_watched`
> branch). That bitwise-`|` form double-fires under a genuine concurrency interleave — Thread A wins the
> `_handles` removal while Thread B wins the `_hwnds` removal, so **both** compute `removed = true` and
> **both** raise the event, violating the "fires at most once" contract the same comment asserts. (Benign
> at runtime because `EvictWindow` is idempotent, but the contract should hold.) Electing the
> unconditionally-populated `_handles` as the single gate closes the race. The sequential T2 test passes
> either way; deterministic concurrent-race coverage isn't practical as a unit test, so the guarantee
> rests on the single-key-`TryRemove` argument above. *(AGY-AFTER general-adversarial finding, verified
> and folded.)*

- [ ] **Step 4: Verify build + headless suite still green**

Run: `dotnet build -c Release`
Expected: `Build succeeded`, `0 Error(s)`.

Run: `dotnet test -c Release --filter "Category!=Desktop"`
Expected: `Passed!`, `Failed: 0` (no regression; the new Desktop test is excluded here and verified live by the maintainer).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Windows/WindowManager.cs test/FlaUI.Mcp.Tests/Windows/WindowManagerTests.cs
git commit -m "feat(windows): WindowInvalidated event, gated on state removal (Phase 6 U2)"
```

---

## Task 3: `PruneClosedWindows` sweep + `DeadWindowIds` decision + `IsWindow` P/Invoke (U3)

The pull backstop. The pure "which tracked handles are dead" decision is extracted as a `static` (`DeadWindowIds`) so it is unit-tested headless without constructing `WindowManager`; `PruneClosedWindows` applies it and routes each dead id through `Invalidate`.

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Windows/WindowManager.cs`
- Test (headless): `test/FlaUI.Mcp.Tests/Windows/WindowLivenessTests.cs` (create)
- Test (Desktop): `test/FlaUI.Mcp.Tests/Windows/WindowManagerTests.cs` (append)

- [ ] **Step 0: State-verify**

Confirm `src/FlaUI.Mcp.Core/Windows/WindowManager.cs` already imports `System.Runtime.InteropServices` (it does — used by the existing `[DllImport("user32.dll")]` blocks) and already declares `[DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);` but does **not** already declare `IsWindow`. Confirm `_hwnds` is `ConcurrentDictionary<string, IntPtr>`. If `IsWindow` already exists or `_hwnds` differs, STOP and report `STATE_MISMATCH: <what>`.

- [ ] **Step 1: Write the failing headless tests for `DeadWindowIds`**

Create `test/FlaUI.Mcp.Tests/Windows/WindowLivenessTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Windows;

// Headless: exercises the pure liveness DECISION only (no UIA, no WindowManager instance),
// so it runs on the CI box (Category!=Desktop). The sweep→Invalidate→evict wiring is covered
// live in WindowManagerTests (Category=Desktop).
public class WindowLivenessTests
{
    private static KeyValuePair<string, IntPtr> Pair(string id, int hwnd) => new(id, new IntPtr(hwnd));

    [Fact]
    public void DeadWindowIds_returns_ids_whose_hwnd_is_not_alive()
    {
        var tracked = new[] { Pair("w1", 10), Pair("w2", 20), Pair("w3", 30) };
        var alive = new HashSet<IntPtr> { new IntPtr(10), new IntPtr(30) };
        var dead = WindowManager.DeadWindowIds(tracked, h => alive.Contains(h));
        Assert.Equal(new[] { "w2" }, dead);
    }

    [Fact]
    public void DeadWindowIds_treats_a_zero_hwnd_as_dead_without_calling_the_predicate()
    {
        var tracked = new[] { Pair("w1", 0) };
        var dead = WindowManager.DeadWindowIds(tracked, _ => throw new Exception("must not be called for IntPtr.Zero"));
        Assert.Equal(new[] { "w1" }, dead);
    }

    [Fact]
    public void DeadWindowIds_returns_empty_when_all_alive()
    {
        var tracked = new[] { Pair("w1", 10), Pair("w2", 20) };
        var dead = WindowManager.DeadWindowIds(tracked, _ => true);
        Assert.Empty(dead);
    }

    [Fact]
    public void DeadWindowIds_returns_empty_for_no_tracked_handles()
    {
        var dead = WindowManager.DeadWindowIds(Array.Empty<KeyValuePair<string, IntPtr>>(), _ => false);
        Assert.Empty(dead);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~WindowLivenessTests"`
Expected: FAIL to compile with `'WindowManager' does not contain a definition for 'DeadWindowIds'` (CS0117).

- [ ] **Step 3: Add `IsWindow` P/Invoke, `DeadWindowIds`, and `PruneClosedWindows`**

In `src/FlaUI.Mcp.Core/Windows/WindowManager.cs`, add the P/Invoke next to the existing `user32.dll` imports (e.g. immediately after the `IsWindowVisible` import):

```csharp
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);
```

Add these two methods (place them right after the `Invalidate` method):

```csharp
    /// <summary>Pure liveness decision (no UIA / no instance state beyond the passed pairs) so it is
    /// unit-testable headless: given the tracked (windowId, hwnd) pairs and an aliveness predicate,
    /// return the windowIds whose HWND is gone (IntPtr.Zero, or !isAlive). IntPtr.Zero is dead without
    /// consulting the predicate.</summary>
    internal static IReadOnlyList<string> DeadWindowIds(
        IReadOnlyCollection<KeyValuePair<string, IntPtr>> tracked, Func<IntPtr, bool> isAlive)
    {
        var dead = new List<string>();
        foreach (var (id, hwnd) in tracked)
            if (hwnd == IntPtr.Zero || !isAlive(hwnd))
                dead.Add(id);
        return dead;
    }

    /// <summary>Best-effort memory hygiene: invalidate any tracked handle whose HWND is no longer a live
    /// window (user/app closed it without a process exit, so neither proc.Exited nor CloseAsync fired).
    /// Routes each dead id through Invalidate, so _handles/_hwnds/_watched AND (via the WindowInvalidated
    /// event) RefRegistry are all reclaimed on one path. Pure Win32 (IsWindow) + ConcurrentDictionary ops
    /// — needs no STA, safe to call off the query STA. Snapshots _hwnds first so the Invalidate-driven
    /// TryRemove inside the loop can't corrupt the enumeration. <paramref name="isAlive"/> is injectable
    /// for tests (default = Win32 IsWindow).</summary>
    internal void PruneClosedWindows(Func<IntPtr, bool>? isAlive = null)
    {
        isAlive ??= IsWindow;
        foreach (var id in DeadWindowIds(_hwnds.ToArray(), isAlive))
            Invalidate(new WindowHandle(id));
    }
```

- [ ] **Step 4: Run the headless tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~WindowLivenessTests"`
Expected: PASS, `Failed: 0` (4 tests).

- [ ] **Step 5: Write the Desktop-category sweep integration test**

Append to `test/FlaUI.Mcp.Tests/Windows/WindowManagerTests.cs`:

```csharp
    [Fact]
    public async Task PruneClosedWindows_invalidates_a_tracked_handle_a_fake_predicate_reports_dead()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id); // registers _hwnds[handle.Id]

        var fired = new List<string>();
        mgr.WindowInvalidated += id => fired.Add(id);

        mgr.PruneClosedWindows(isAlive: _ => false); // force "everything dead"

        Assert.Contains(handle.Id, fired);
        var ex = await Assert.ThrowsAsync<ToolException>(
            () => mgr.RunOnWindowAsync(handle, w => w.Title));
        Assert.Equal(ToolErrorCode.WindowHandleStale, ex.Code); // handle really was invalidated
    }
```

- [ ] **Step 6: Verify build + headless suite green**

Run: `dotnet build -c Release`
Expected: `Build succeeded`, `0 Error(s)`.

Run: `dotnet test -c Release --filter "Category!=Desktop"`
Expected: `Passed!`, `Failed: 0`.

- [ ] **Step 7: Commit**

```bash
git add src/FlaUI.Mcp.Core/Windows/WindowManager.cs test/FlaUI.Mcp.Tests/Windows/WindowLivenessTests.cs test/FlaUI.Mcp.Tests/Windows/WindowManagerTests.cs
git commit -m "feat(windows): PruneClosedWindows on-access sweep + headless DeadWindowIds decision (Phase 6 U3)"
```

---

## Task 4: Wire the subscription + sweep call sites (U4)

`PerceptionManager` subscribes `RefRegistry.EvictWindow` to `WindowInvalidated` in its constructor, and calls `PruneClosedWindows()` at the entry of the registry-growing / window-resolving reads. `BuildModelAsync` is the chokepoint for the whole snapshot family (snapshot, wait, stats, diff, get-focused all route through it), so one insertion there covers `desktop_snapshot` and its siblings; `FindAsync` covers `desktop_find`; `ListWindowsAsync` covers `desktop_list_windows`.

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs`
- Modify: `src/FlaUI.Mcp.Core/Windows/WindowManager.cs` (sweep inside `ListWindowsAsync`)
- Test: `test/FlaUI.Mcp.Tests/Windows/WindowManagerTests.cs` (Desktop-category end-to-end)

- [ ] **Step 0: State-verify**

Confirm in `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs`:
- the constructor is exactly `public PerceptionManager(WindowManager windows, RefRegistry refs, SnapshotCache cache) { _windows = windows; _refs = refs; _cache = cache; }` with fields `_windows`, `_refs`, `_cache`;
- `BuildModelAsync` is expression-bodied: `public Task<(string SnapshotId, SnapshotModel Model)> BuildModelAsync(WindowHandle handle, SnapshotOptions options, RefRegistry refs) => _windows.RunWithWindowAndDesktopAsync(handle, (win, desktop) => { … });`
- `FindAsync` is expression-bodied: `public Task<FindResult> FindAsync(WindowHandle handle, FindQuery query, int max, string? scopeRef) => _windows.RunWithWindowAndDesktopAsync(handle, (win, desktop) => { … });`

Confirm in `src/FlaUI.Mcp.Core/Windows/WindowManager.cs` that `ListWindowsAsync(bool includeBounds)` is expression-bodied `=> _dispatcher.RunQueryAsync<IReadOnlyList<WindowInfo>>(() => { var foreground = GetForegroundWindow(); … });`.

If any signature/shape differs, STOP and report `STATE_MISMATCH: <what>`.

- [ ] **Step 1: Write the failing end-to-end test (Desktop-category)**

Append to `test/FlaUI.Mcp.Tests/Windows/WindowManagerTests.cs`. This proves the constructor wiring: an invalidation of a window whose ref lives in the `RefRegistry` makes that ref `REF_NOT_FOUND`.

```csharp
    [Fact]
    public async Task Closing_a_window_evicts_its_RefRegistry_entry_via_the_PerceptionManager_wiring()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var refs = new FlaUI.Mcp.Core.Perception.RefRegistry();
        // Constructing PerceptionManager subscribes refs.EvictWindow to mgr.WindowInvalidated.
        _ = new FlaUI.Mcp.Core.Perception.PerceptionManager(
            mgr, refs, new FlaUI.Mcp.Core.Perception.SnapshotCache());

        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        refs.BeginSnapshot(handle.Id);
        var elRef = refs.Register(handle.Id,
            new FlaUI.Mcp.Core.Perception.ElementDescriptor(
                Array.Empty<int>(), FlaUI.Core.Definitions.ControlType.Button, "a", "a", null, Array.Empty<int>()),
            cached: null);

        Assert.Equal("a", refs.Lookup(handle.Id, elRef).Descriptor.AutomationId); // present before close

        mgr.Invalidate(handle); // fires WindowInvalidated → refs.EvictWindow(handle.Id)

        var ex = Assert.Throws<ToolException>(() => refs.Lookup(handle.Id, elRef));
        Assert.Equal(ToolErrorCode.RefNotFound, ex.Code); // evicted through the wiring
    }
```

Confirm the test file's `using`s cover `FlaUI.Mcp.Core.Errors` (for `ToolException`/`ToolErrorCode`) — it already imports `FlaUI.Mcp.Core.Errors` and `FlaUI.Mcp.Core.Windows`; the fully-qualified `FlaUI.Mcp.Core.Perception.*` references above avoid needing a new `using`.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~WindowManagerTests.Closing_a_window_evicts"`
Expected: FAIL — the ref still resolves after `Invalidate` (the subscription does not exist yet), so `Assert.Throws<ToolException>` fails. *(Desktop-category — verified on the maintainer's interactive session; must compile on CI.)*

- [ ] **Step 3: Add the constructor subscription**

In `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs`, change the constructor body to subscribe:

```csharp
    public PerceptionManager(WindowManager windows, RefRegistry refs, SnapshotCache cache)
    {
        _windows = windows;
        _refs = refs;
        _cache = cache;
        // Phase 6: close signal → evict the window's refs. Safe here because on stdio WindowManager,
        // RefRegistry, and PerceptionManager are ALL process-lifetime singletons (Program.cs), so this
        // subscription lives exactly as long as its target — no leak. PHASE-7 NOTE: when HTTP/SSE makes
        // RefRegistry per-connection while WindowManager stays a singleton, this '+=' would root every
        // dropped connection's RefRegistry via the delegate; that phase must make PerceptionManager
        // IDisposable and '-=' unsubscribe on connection teardown. Do NOT add that now (YAGNI — no
        // second connection exists on stdio).
        _windows.WindowInvalidated += _refs.EvictWindow;
    }
```

- [ ] **Step 4: Add the sweep at the snapshot/find entry points**

In `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs`, convert `BuildModelAsync` from expression body to a block that sweeps first (the sweep is off-STA-safe; it never evicts the live target window):

```csharp
    public Task<(string SnapshotId, SnapshotModel Model)> BuildModelAsync(
        WindowHandle handle, SnapshotOptions options, RefRegistry refs)
    {
        _windows.PruneClosedWindows(); // Phase 6 backstop: reclaim windows closed w/o a process exit
        return _windows.RunWithWindowAndDesktopAsync(handle, (win, desktop) =>
        {
            var procName = SafeProcessName(win);
            if (PerceptionPolicy.IsDenied(procName))
                throw new ToolException(ToolErrorCode.TargetDenied,
                    $"Snapshotting windows owned by '{procName}' is blocked (credential store).",
                    "snapshot a different, non-sensitive window");
            IReadOnlyList<AutomationElement> popups = PopupFinder.FindOwnerPopups(desktop, win);
            AutomationElement root = string.IsNullOrEmpty(options.RootRef)
                ? win : refs.Resolve(handle.Id, options.RootRef!, PopupFinder.SearchRoots(win, desktop));
            var snapshotId = refs.BeginSnapshot(handle.Id);
            var model = SnapshotEngine.Build(root, popups, options, refs, handle.Id);
            return (snapshotId, model);
        });
    }
```

Likewise convert `FindAsync` to a block that sweeps first (paste the existing lambda body unchanged — only the outer shape changes from `=>` to `{ prune; return …; }`):

```csharp
    public Task<FindResult> FindAsync(WindowHandle handle, FindQuery query, int max, string? scopeRef)
    {
        _windows.PruneClosedWindows(); // Phase 6 backstop
        return _windows.RunWithWindowAndDesktopAsync(handle, (win, desktop) =>
        {
            var procName = SafeProcessName(win);
            if (PerceptionPolicy.IsDenied(procName))
                throw new ToolException(ToolErrorCode.TargetDenied,
                    $"Finding in windows owned by '{procName}' is blocked (credential store).",
                    "target a different, non-sensitive window");

            var spec = new FindQuerySpec(query);
            bool hasCtConstraint = FindQuerySpec.TryParseControlType(query.ControlType, out var wantedCt);
            if (!string.IsNullOrWhiteSpace(query.ControlType) && !hasCtConstraint)
                throw new ToolException(ToolErrorCode.InvalidArguments,
                    $"Unknown controlType '{query.ControlType}'.",
                    "use a UIA ControlType name, e.g. Button, Edit, ListItem");

            AutomationElement root = string.IsNullOrEmpty(scopeRef)
                ? win
                : _refs.Resolve(handle.Id, scopeRef!, PopupFinder.SearchRoots(win, desktop));

            // Native condition for the indexed props (AutomationId, ControlType, exact Name). Name
            // "contains" and enabledOnly are not indexed-expressible -> post-filter.
            FlaUI.Core.Conditions.ConditionBase? Build(FlaUI.Core.Conditions.ConditionFactory cf)
            {
                FlaUI.Core.Conditions.ConditionBase? c = null;
                if (!string.IsNullOrEmpty(query.AutomationId)) c = cf.ByAutomationId(query.AutomationId);
                if (hasCtConstraint) c = c is null ? cf.ByControlType(wantedCt) : c.And(cf.ByControlType(wantedCt));
                if (!string.IsNullOrEmpty(query.Name) && string.Equals(query.NameMatch, "eq", System.StringComparison.Ordinal))
                    c = c is null ? cf.ByName(query.Name) : c.And(cf.ByName(query.Name));
                return c;
            }

            // hasNative iff a native-expressible constraint exists (NOT name-contains / enabledOnly).
            // When absent, match ALL via the no-arg overload (repo idiom PerceptionManager.cs:226) -
            // NOT a TrueCondition/double-negation surrogate.
            bool hasNative = !string.IsNullOrEmpty(query.AutomationId) || hasCtConstraint
                || (!string.IsNullOrEmpty(query.Name) && string.Equals(query.NameMatch, "eq", System.StringComparison.Ordinal));
            AutomationElement[] raw;
            try
            {
                raw = (hasNative ? root.FindAllDescendants(cf => Build(cf)!) : root.FindAllDescendants()).ToArray();
            }
            catch { raw = System.Array.Empty<AutomationElement>(); }

            var matches = new List<FindMatch>();
            int total = 0;
            foreach (var el in raw)
            {
                bool isPwd = RedactionPolicy.IsPasswordOrFailClosed(() => el.Properties.IsPassword.ValueOrDefault);
                string rawName = SafeRead(() => el.Name, "") ?? string.Empty;
                string name = isPwd ? "[REDACTED]" : rawName;
                bool enabled = SafeRead(() => el.IsEnabled, false);
                if (!spec.MatchesPostFilter(name, enabled)) continue;
                total++;
                if (matches.Count >= max) continue;

                int[] rid = SafeRead(() => el.Properties.RuntimeId.ValueOrDefault, (int[]?)null) ?? System.Array.Empty<int>();
                var ctEnum = SafeRead(() => el.ControlType, FlaUI.Core.Definitions.ControlType.Custom);
                string aid = SafeRead(() => el.AutomationId, "") ?? string.Empty;
                var b = SafeRead(() => el.BoundingRectangle, System.Drawing.Rectangle.Empty);
                bool offscreen = SafeRead(() => el.Properties.IsOffscreen.ValueOrDefault, false);
                bool hasFocus = SafeRead(() => el.Properties.HasKeyboardFocus.ValueOrDefault, false);

                var descriptor = new ElementDescriptor(rid, ctEnum, aid, rawName,
                    SnapshotEngine.NearestAncestorAutomationId(el), System.Array.Empty<int>(), hasFocus);
                var @ref = _refs.Register(handle.Id, descriptor, cached: el);
                matches.Add(new FindMatch(@ref, aid, name, ctEnum.ToString(),
                    new[] { b.X, b.Y, b.Width, b.Height }, offscreen, enabled, hasFocus));
            }
            return new FindResult(matches, total, total > max);
        });
    }
```

> Note for the implementer: the `FindAsync` lambda body above must be **byte-for-byte the current body** — do not alter any logic, comments, or reads. Only the method's outer shape changes (`=>` → `{ _windows.PruneClosedWindows(); return …; }`). If the current body differs from the paste above, keep the current body and wrap it; report `STATE_MISMATCH` only if the *signature* differs.

- [ ] **Step 5: Add the sweep inside `ListWindowsAsync`**

In `src/FlaUI.Mcp.Core/Windows/WindowManager.cs`, add `PruneClosedWindows()` as the first statement inside the `ListWindowsAsync(bool includeBounds)` query lambda:

```csharp
    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(bool includeBounds) =>
        _dispatcher.RunQueryAsync<IReadOnlyList<WindowInfo>>(() =>
        {
            PruneClosedWindows(); // Phase 6 backstop: reclaim windows closed w/o a process exit
            // PURE Win32 — no UIA. A UIA Title/ProcessId read on the query STA blocks with no
            // timeout on ANY momentarily-unresponsive desktop window; Win32 GetWindowText does not.
            var foreground = GetForegroundWindow();
            var list = new List<WindowInfo>(); int z = 0;
            foreach (var (hwnd, title, pid) in EnumTopLevel())
            {
                WindowBounds? b = null;
                if (includeBounds && GetWindowRect(hwnd, out var r))
                    b = new WindowBounds(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                list.Add(new WindowInfo(title, SafeProcessName(pid), pid, hwnd == foreground, b, includeBounds ? z : (int?)null));
                z++;
            }
            return list;
        });
```

- [ ] **Step 6: Verify build + headless suite green**

Run: `dotnet build -c Release`
Expected: `Build succeeded`, `0 Error(s)`.

Run: `dotnet test -c Release --filter "Category!=Desktop"`
Expected: `Passed!`, `Failed: 0`.

- [ ] **Step 7: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs src/FlaUI.Mcp.Core/Windows/WindowManager.cs test/FlaUI.Mcp.Tests/Windows/WindowManagerTests.cs
git commit -m "feat(perception): wire WindowInvalidated→EvictWindow + sweep at snapshot/find/list entry (Phase 6 U4)"
```

---

## Task 5: Version bump + docs (U5)

Additive, backward-compatible change: no wire-contract change, no new tool, no changed error codes. Bump 0.7.5 → 0.7.6 and record it.

**Files:**
- Modify: `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj`
- Modify: `installer/flaui-mcp.iss`
- Modify: `CHANGELOG.md`
- Modify: `ROADMAP.md`

- [ ] **Step 0: State-verify**

Confirm `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj` line ~20 is `<Version>0.7.5</Version>`; `installer/flaui-mcp.iss` line ~4 is `#define AppVersion "0.7.5"`; `CHANGELOG.md` top entry is `## [0.7.5] - 2026-07-02`; `ROADMAP.md` contains the line `Not phased here (separate follow-on, not v1-blocking): HTTP/SSE transport with its hard auth-token gate; RefRegistry eviction on window close.` If any differs, STOP and report `STATE_MISMATCH: <what>`.

- [ ] **Step 1: Bump the project version**

In `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj`: `<Version>0.7.5</Version>` → `<Version>0.7.6</Version>`.

- [ ] **Step 2: Bump the installer version**

In `installer/flaui-mcp.iss`: `#define AppVersion "0.7.5"` → `#define AppVersion "0.7.6"`.

- [ ] **Step 3: Add the CHANGELOG entry**

In `CHANGELOG.md`, insert directly above `## [0.7.5] - 2026-07-02`:

```markdown
## [0.7.6] - 2026-07-03

### Fixed
- **RefRegistry no longer leaks a closed window's refs.** Over a long, exploration-heavy session that
  launches/closes many windows (or issues many additive `desktop_find`s), the per-window ref maps —
  each potentially pinning a cached UIA/COM element — accumulated for every window ever seen and were
  never reclaimed. Closing a window now evicts its ref state (refs + counter + snapshot seq),
  releasing the pinned COM elements to the GC. Two paths feed the single `Invalidate` chokepoint: a
  push signal (process exit / `desktop_close_window`) via a new `WindowInvalidated` event, and an
  on-access liveness sweep (Win32 `IsWindow`) at the entry of `desktop_snapshot`, `desktop_find`, and
  `desktop_list_windows` that catches windows closed by the user/app without a process exit. Purely
  internal memory reclamation — no wire-contract, tool, or error-code change; a ref used after its
  window is gone yields the existing `REF_NOT_FOUND` (→ take a fresh snapshot), now surfaced sooner.
```

- [ ] **Step 4: Update the ROADMAP**

In `ROADMAP.md`, change the deferral line (currently `Not phased here (separate follow-on, not v1-blocking): HTTP/SSE transport with its hard auth-token gate; RefRegistry eviction on window close.`) to reflect delivery:

```markdown
Not phased here (separate follow-on, not v1-blocking): HTTP/SSE transport with its hard
auth-token gate.
    - **Phase 6 — RefRegistry eviction on window close** ✅ **(shipped v0.7.6).** A closed window's
      ref state (and `WindowManager`'s per-window COM pin) is reclaimed via a `WindowInvalidated`
      push signal through the existing `Invalidate` chokepoint plus an on-access `IsWindow` liveness
      sweep at the snapshot/find/list entry points. `windowId` is a never-reused `w{n}`, so dropping
      all three registry dicts is alias-safe; no background thread, timer, or UIA event pump.
```

- [ ] **Step 5: Verify build + headless suite green**

Run: `dotnet build -c Release`
Expected: `Build succeeded`, `0 Error(s)` (confirms the new `<Version>` parses).

Run: `dotnet test -c Release --filter "Category!=Desktop"`
Expected: `Passed!`, `Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj installer/flaui-mcp.iss CHANGELOG.md ROADMAP.md
git commit -m "chore(release): v0.7.6 — Phase 6 RefRegistry eviction (version + CHANGELOG + ROADMAP)"
```

---

## Self-Review

**Spec coverage (spec §5 units):**
- U1 `RefRegistry.EvictWindow(string)` → Task 1. ✓
- U2 `WindowManager.WindowInvalidated` event + gated raise → Task 2. ✓
- U3 `PruneClosedWindows` + `IsWindow` P/Invoke (+ `DeadWindowIds` seam) → Task 3. ✓
- U4 subscription wiring + sweep call sites (`BuildModelAsync`/`FindAsync`/`ListWindowsAsync`) → Task 4. ✓
- U5 version + docs → Task 5. ✓

**Spec §6 testing coverage:**
- `EvictWindow` unit tests (evict removes, sibling intact, idempotent unknown/double, fresh-snapshot-clean) → Task 1 Step 1. ✓
- `PruneClosedWindows` decision via injected predicate → Task 3 Step 1 (headless `DeadWindowIds`) + Task 3 Step 5 (Desktop sweep). ✓
- Event → eviction wiring (ref becomes `REF_NOT_FOUND`) → Task 4 Step 1 (Desktop). ✓ *(See audit note on headless vs Desktop placement.)*
- Category=Desktop live end-to-end → Task 3 Step 5 + Task 4 Step 1. ✓

**Spec §4 edge cases mapped:** idempotency (T1), thread-safety `_gate` + no-COM-call (T1 impl comment), gated broadcast / no phantom fire (T2 test), enumeration safety `_hwnds.ToArray()` (T3 impl), `windowId` never reused → counters droppable (T1 test `After_EvictWindow…starts_clean`), no re-entrancy loop (`PruneClosedWindows` not a subscriber — T3/T4 impl). ✓

**Type/signature consistency:** `EvictWindow(string)` matches `Action<string>` (T1↔T4 subscription); `WindowInvalidated` is `event Action<string>?` (T2↔T4); `PruneClosedWindows(Func<IntPtr,bool>?)` and `DeadWindowIds(IReadOnlyCollection<KeyValuePair<string,IntPtr>>, Func<IntPtr,bool>)` consistent T3↔T4; `new WindowHandle(id)` valid (`readonly record struct`); `ElementDescriptor(int[], ControlType, string, string, string?, int[])` 6-arg ctor matches the existing `Desc` helper usage. ✓

**Placeholder scan:** none — every code step carries full code; no TBD/TODO. ✓

---

## Exhaustiveness Self-Audit (per plan discipline)

1. **Under-specified "what":** none left vague — DTO/event shapes (`Action<string>`), method signatures, the `|` vs `||` gating, and the `_hwnds.ToArray()` snapshot are all pinned with exact code. The `BuildModelAsync` insertion is deliberately a **superset** of spec §3.2's "desktop_snapshot" (it also covers wait/stats/diff/get-focused, which all route through `BuildModelAsync`) — an intentional, strictly-safer refinement, flagged here so it is not read as scope drift.

2. **Placeholders / decide-later:** none.

3. **Missing cases / state combos:** covered — double-invalidate (gated), zero-hwnd (dead without predicate), unknown-id evict (no-op), sweep-during-diff (target alive ⇒ untouched; target dead ⇒ correct `WindowHandleStale`), off-STA call safety.

4. **Requirement → task mapping:** every §5 unit and §6 test maps to a task (table above).

**Flagged gaps (resolved, with WHERE):**

- **G-A (headless vs Desktop test placement — the one real divergence from spec §6):** Spec §6 listed the *sweep* and the *event→eviction wiring* under "Headless." They are **not** cleanly headless: constructing a `WindowManager` requires live UIA (confirmed — all 20+ existing `new WindowManager(...)` call sites are in `[Trait("Category","Desktop")]` classes). This plan resolves the spec's own hedge ("the plan resolves the exact seam") by splitting: the **pure decision** (`DeadWindowIds`) and **`EvictWindow`** are headless (CI-run); the **event raise**, the **sweep-fires-`Invalidate`**, and the **ctor-wiring end-to-end** are `Category=Desktop` (maintainer-run on an interactive session), consistent with the existing test architecture. This is a placement change, **not** a coverage gap — every behavior is still tested. No new test-only seeding seam is added to `WindowManager` (avoids widening its surface); `DeadWindowIds` is the minimal seam. **Resolved in-plan.**

- **G-B (`internal` visibility):** `DeadWindowIds` and `PruneClosedWindows` are `internal`; the headless `WindowLivenessTests` and the Desktop `WindowManagerTests` reach them via the existing `[assembly: InternalsVisibleTo("FlaUI.Mcp.Tests")]` (`src/FlaUI.Mcp.Core/Properties/AssemblyInfo.cs`, verified present). **Resolved.**

- **G-C (CI cannot prove the wiring):** because the wiring tests are `Category=Desktop`, the headless CI gate green does **not** by itself prove the push-path end-to-end. This matches the project's standing model (README/ROADMAP: "CI proves only the headless half; the Desktop suite is the maintainer's interactive verify"). The maintainer must run `dotnet test --filter "Category=Desktop&FullyQualifiedName~WindowManagerTests"` on an unlocked session before cutting v0.7.6, plus the live smoke (launch → snapshot → close → ref is `REF_NOT_FOUND`). **Resolved as an explicit release-gate step, below.**

- **G-D (`FindAsync` block-body paste risk):** Task 4 Step 4 pastes the full current `FindAsync` body. If the on-disk body has drifted from the paste, the Step-0 rule is "keep the current body, only wrap the outer shape" — the paste is illustrative of the wrap, not an authority over the logic. **Resolved via the implementer note in Step 4.**

**Release gate (maintainer, before tagging v0.7.6):** headless `dotnet test -c Release --filter "Category!=Desktop"` green in CI **and** local `dotnet test --filter "Category=Desktop&FullyQualifiedName~WindowManagerTests"` green on an interactive session **and** a live dogfood smoke (open app → snapshot/find mints a ref → close the window by hand → the ref returns `REF_NOT_FOUND`; open several dialogs in a loop without `list_windows` → memory does not climb per closed dialog). Then the standard cut: version already bumped in Task 5, `git tag v0.7.6`, push, release workflow builds the 4 assets.
