# Phase 10 #1 — Opt-in window handles on `desktop_list_windows` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add opt-in `desktop_list_windows(includeHandles:true)` that returns each window's reusable `wN` handle inline (eliminating the mandatory `desktop_open_window` round-trip), minted lazily so the list stays pure-Win32/non-blocking, with an HWND-recycle guard so a recycled handle can never inject UIA into a different process.

**Architecture:** Fork **1a (lazy handle)** — chosen by the user, re-confirmed by AGY-FIRST (cascade `0cb93949`). `includeHandles` mints an id + records `(hwnd,pid)` + a process-exit watch using the pure-Win32 `(hwnd,pid)` already in hand; the UIA `FromHandle().AsWindow()` is **deferred** to first read-use. Two mandates from grounding against the current `WindowManager.cs` (see spec §#1 "FORK DECIDED"): **(M1)** the exactly-once `WindowInvalidated` gate moves off `_handles` (now a lazy COM cache, absent for a never-read lazy handle) onto a **new dedicated `_ids` (id→pid) record written unconditionally by every mint path** — NOT onto `_hwnds` (best-effort/try-catch, would regress the eager path); **(M2)** lazy resolution runs a raw Win32 `GetWindowThreadProcessId(hwnd)==recordedPid` check **before** binding `FromHandle`, throwing `WindowHandleStale` on mismatch.

**Tech Stack:** C#/.NET 10 (`net10.0-windows10.0.19041.0`), FlaUI/UIA3, ModelContextProtocol SDK 1.4.0, xUnit. Build: `dotnet build`; headless tests: `dotnet test --filter "Category!=Desktop"`. Desktop-category tests need a real console (this box is RDP-headless — run them in bounded chunks / validate live at smoke).

**Design decision — Design B (least-invasive), locked from the M1 consumer audit:**
- `WindowInvalidated` subscribers (all reclaim per-window state, must keep firing exactly once): `PerceptionManager.cs:44`→`EvictWindow`, `WatchService.cs:51`, `WakeService.cs:33`.
- `_hwnds` (id→IntPtr, best-effort) stays exactly as today and keeps feeding `DeadWindowIds`/`PruneClosedWindows` — **untouched**, so `WindowLivenessTests` and the Phase-6 close-eviction path are preserved. A lazy handle **does** populate `_hwnds` (the hwnd is a pure-Win32 value, always available), so `PruneClosedWindows` reclaims a lazy handle whose window closes without a process exit — satisfying spec §#1's crux.
- New `_ids` (id→pid): written unconditionally at the eager `Register` site AND the lazy mint site → the single exactly-once gate and the home for the recorded pid M2 needs.
- New `_byHwnd` (hwnd→current id): reverse map so `list(includeHandles)` polling **reuses** ids (mint-or-reuse) instead of leaking a fresh `wN` per call; the reuse is gated on a pid match so a recycled HWND mints fresh.
- `_handles` (id→Window) degrades to a **lazy COM-wrapper cache**: eager mints populate it as today; lazy mints leave it empty until first read resolves it via `ResolveWindow`.

---

## Files

- Modify: `src/FlaUI.Mcp.Core/Windows/WindowManager.cs` — `WindowInfo.Handle` field (T1); `_ids`/`_byHwnd` state + `Register` write + `Invalidate` gate move (T2); `LazyHandleFor` mint-or-reuse + `CanReuseHandle` pure static (T3); `ResolveWindow` lazy resolution + `HwndStillOwnedBy` pure static + `RunOnWindowAsync`/`RunWithWindowAndDesktopAsync` rewire (T4); `ListWindowsAsync(bool,bool)` overload + loop wiring (T5).
- Modify: `src/FlaUI.Mcp.Server/Tools/WindowTools.cs` — `includeHandles` param + description (T5).
- Test: `test/FlaUI.Mcp.Tests/Windows/WindowInfoSerializationTests.cs` (new, headless) — `Handle` JsonIgnore-when-null (T1).
- Test: `test/FlaUI.Mcp.Tests/Windows/WindowLivenessTests.cs` (headless) — `CanReuseHandle` (T3) + `HwndStillOwnedBy` (T4) pure statics.
- Test: `test/FlaUI.Mcp.Tests/Windows/WindowManagerTests.cs` (Desktop) — lazy resolve, recycle-refusal, mint-or-reuse, `list includeHandles` end-to-end (T4/T5).
- Modify: `.claude/skills/driving-flaui-mcp/SKILL.md` — `includeHandles` note (T6).
- Modify: `CHANGELOG.md`, `README.md`, `docs/ops-manual.md`, `ROADMAP.md`, `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj`, `installer/flaui-mcp.iss` — version 0.10.1→0.11.0 + docs (T7).

---

### Task 1: `WindowInfo.Handle` wire field

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Windows/WindowManager.cs:14-17`
- Test: `test/FlaUI.Mcp.Tests/Windows/WindowInfoSerializationTests.cs` (create)

- [ ] **Step 0 — State-verify.** Open `WindowManager.cs`; confirm `WindowInfo` is the record at lines 14-17 with `Bounds` and `ZOrder` as the two trailing `JsonIgnore(WhenWritingNull)` optional params. If it differs, STOP and report `STATE_MISMATCH`.

- [ ] **Step 1 — Write the failing test.**

`test/FlaUI.Mcp.Tests/Windows/WindowInfoSerializationTests.cs`:
```csharp
using System.Text.Json;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Windows;

// Headless: pure wire-shape check on the WindowInfo record (Category != Desktop).
public class WindowInfoSerializationTests
{
    [Fact]
    public void Handle_is_omitted_when_null_default_response_is_unchanged()
    {
        var wi = new WindowInfo("Untitled - Notepad", "notepad", 1234, true);
        var json = JsonSerializer.Serialize(wi);
        Assert.DoesNotContain("Handle", json);
        Assert.DoesNotContain("handle", json);
    }

    [Fact]
    public void Handle_is_emitted_when_set()
    {
        var wi = new WindowInfo("Untitled - Notepad", "notepad", 1234, true, Handle: "w7");
        var json = JsonSerializer.Serialize(wi);
        Assert.Contains("\"Handle\":\"w7\"", json);
    }
}
```

- [ ] **Step 2 — Run, verify it fails.** `dotnet test --filter "FullyQualifiedName~WindowInfoSerializationTests"` → FAIL to compile (`WindowInfo` has no `Handle` param).

- [ ] **Step 3 — Add the field.** In `WindowManager.cs`, replace the `WindowInfo` record (lines 14-17) with:
```csharp
public sealed record WindowInfo(
    string Title, string ProcessName, int Pid, bool IsForeground,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WindowBounds? Bounds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ZOrder = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Handle = null);
```

- [ ] **Step 4 — Run, verify pass.** `dotnet test --filter "FullyQualifiedName~WindowInfoSerializationTests"` → 2/2 PASS. Full headless build stays green: `dotnet build` → 5/0/0.

- [ ] **Step 5 — Commit.**
```bash
git add src/FlaUI.Mcp.Core/Windows/WindowManager.cs test/FlaUI.Mcp.Tests/Windows/WindowInfoSerializationTests.cs
git commit -m "feat(windows): additive WindowInfo.Handle wire field (JsonIgnore-when-null)"
```

---

### Task 2: Dedicated `_ids` gate + `_byHwnd` reverse map; move the exactly-once `WindowInvalidated` gate off `_handles`

**Oracle:** the invariant in `WindowManager.cs:116-135` — `WindowInvalidated` must fire **at most once** per invalidation, and only when tracked state was actually removed. The gate must be a dict written **unconditionally** by every mint path. Eager `Register` behavior (all of `_handles`/`_hwnds`/`_watched` + the event) must be **byte-for-byte preserved** for eager handles.

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Windows/WindowManager.cs` (fields near line 23; `Invalidate` 116-135; `Register` 165-172)

- [ ] **Step 0 — State-verify.** Confirm the fields block (lines 23-26): `_handles`, `_hwnds`, `_watched`, `_counter`; `Invalidate` gates `removed` on `_handles.TryRemove` (line 125); `Register` (165-172) sets `_handles[id]` then best-effort `_hwnds[id]` then `TryWatchProcessExit`. If different, STOP → `STATE_MISMATCH`.

- [ ] **Step 1 — Add the two new fields.** After the `_hwnds` field declaration (line 24) add:
```csharp
    // Exactly-once invalidation gate + recorded pid. Written UNCONDITIONALLY by every mint path
    // (eager Register AND lazy list-mint) — unlike _handles (a lazy COM cache, absent for a never-read
    // lazy handle) and _hwnds/_watched (best-effort, inside try/catch). So _ids is the only honest gate.
    private readonly ConcurrentDictionary<string, int> _ids = new();
    // Reverse map hwnd -> current wN, so list(includeHandles) polling reuses ids (mint-or-reuse) instead
    // of leaking a fresh handle per call. Populated/cleaned alongside _hwnds.
    private readonly ConcurrentDictionary<IntPtr, string> _byHwnd = new();
```

- [ ] **Step 2 — Move the gate in `Invalidate`.** Replace the body of `Invalidate` (lines 116-135) with:
```csharp
    public void Invalidate(WindowHandle handle)
    {
        // Exactly-once gate on _ids — the ONLY dict written UNCONDITIONALLY by every mint path (eager
        // Register AND lazy list-mint). ConcurrentDictionary.TryRemove returns true to EXACTLY ONE caller
        // per key, so electing _ids as the sole gate makes WindowInvalidated fire at most once even under
        // concurrent invalidation (proc.Exited racing CloseAsync, or a sweep racing proc.Exited). _handles
        // is now a lazy COM cache (may be absent for a never-read lazy handle); _hwnds/_watched are
        // best-effort — none can serve as the gate. Remove all three unconditionally; do NOT let their
        // removal set `removed`. Capture the hwnd before removing _hwnds so the _byHwnd reverse entry is
        // cleaned (only if it still points at THIS id — a recycled hwnd may already point elsewhere).
        bool removed = _ids.TryRemove(handle.Id, out _);
        _handles.TryRemove(handle.Id, out _);
        if (_hwnds.TryRemove(handle.Id, out var hwnd))
            ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<IntPtr, string>>)_byHwnd)
                .Remove(new System.Collections.Generic.KeyValuePair<IntPtr, string>(hwnd, handle.Id));
        if (_watched.TryRemove(handle.Id, out var p))
            try { p.Dispose(); } catch { }
        if (removed)
            // Swallow subscriber faults: this can fire on a raw ThreadPool thread (proc.Exited), where an
            // unhandled exception is process-fatal, and from CloseAsync's finally, where it would mask an
            // in-flight exception. Invalidation-path robustness > surfacing a subscriber bug here.
            try { WindowInvalidated?.Invoke(handle.Id); } catch { }
    }
```
(The `ICollection.Remove(KeyValuePair)` overload removes the reverse entry **only if** the value still equals `handle.Id`, so a recycled hwnd already re-pointed to a newer id is left intact.)

- [ ] **Step 3 — Write `_ids` in `Register`.** In `Register` (165-172), add the unconditional `_ids` write immediately after the id is computed:
```csharp
    internal WindowHandle Register(Window window, int pid)
    {
        var id = $"w{Interlocked.Increment(ref _counter)}";
        _ids[id] = pid;                 // unconditional gate/identity write (mirrors the lazy mint site)
        _handles[id] = window;
        try
        {
            var hwnd = window.Properties.NativeWindowHandle.ValueOrDefault;
            _hwnds[id] = hwnd;
            if (hwnd != IntPtr.Zero) _byHwnd[hwnd] = id;
        }
        catch { /* no hwnd */ }
        TryWatchProcessExit(id, pid);
        return new WindowHandle(id);
    }
```

- [ ] **Step 4 — Build + full headless regression.** `dotnet build` → 5/0/0; `dotnet test --filter "Category!=Desktop"` → all green (no behavior change for eager handles; `WindowLivenessTests` untouched). This task is a pure internal-invariant refactor; its live exactly-once behavior is covered by the existing Desktop `WindowManagerTests` (run in a bounded chunk if a console is available, else validated at smoke).

- [ ] **Step 5 — Commit.**
```bash
git add src/FlaUI.Mcp.Core/Windows/WindowManager.cs
git commit -m "refactor(windows): dedicated _ids exactly-once gate + _byHwnd reverse map (prep lazy handles)"
```

---

### Task 3: `LazyHandleFor` — mint-or-reuse (pure-Win32, no UIA) + `CanReuseHandle` pure static

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Windows/WindowManager.cs` (new private method + new internal static)
- Test: `test/FlaUI.Mcp.Tests/Windows/WindowLivenessTests.cs`

- [ ] **Step 0 — State-verify.** Confirm `TryWatchProcessExit(string id, int pid)` exists (174-184) and `_counter` is `int` incremented via `Interlocked.Increment` (167). Confirm T2's `_ids`/`_byHwnd` fields are present. If not, STOP → `STATE_MISMATCH`.

- [ ] **Step 1 — Write the failing test** (append to `WindowLivenessTests.cs`):
```csharp
    [Fact]
    public void CanReuseHandle_reuses_only_when_recorded_pid_matches_the_enumerated_pid()
    {
        Assert.True(WindowManager.CanReuseHandle(cachedPid: 1234, enumeratedPid: 1234));
    }

    [Fact]
    public void CanReuseHandle_mints_fresh_when_pid_differs_hwnd_recycled_to_a_new_process()
    {
        Assert.False(WindowManager.CanReuseHandle(cachedPid: 1234, enumeratedPid: 5678));
    }
```

- [ ] **Step 2 — Run, verify it fails.** `dotnet test --filter "FullyQualifiedName~WindowLivenessTests.CanReuseHandle"` → FAIL to compile (`CanReuseHandle` undefined).

- [ ] **Step 3 — Implement.** Add near `DeadWindowIds` (after line 149) the pure static, and add `LazyHandleFor` after `Register`:
```csharp
    /// <summary>Reuse a cached handle for an enumerated (hwnd,pid) only when the cached id's recorded pid
    /// equals the currently-enumerated pid. A mismatch means the OS recycled the HWND integer to a
    /// DIFFERENT process, so a fresh id must be minted. Pure/headless.</summary>
    internal static bool CanReuseHandle(int cachedPid, int enumeratedPid) => cachedPid == enumeratedPid;
```
```csharp
    /// <summary>Lazy handle for `list includeHandles` (fork 1a). Reuse the existing wN for this HWND when
    /// its recorded pid still matches (same live window); else mint a fresh wN — first sight, or the HWND
    /// was recycled (drop the stale id first so refs/watches/wakes for the gone window are reclaimed).
    /// PURE Win32 + dict writes, NO UIA touch, so ListWindowsAsync stays non-blocking; the UIA Window is
    /// bound later, lazily, by ResolveWindow (with the M2 pid-reverify). Populates _hwnds so
    /// PruneClosedWindows reclaims this handle if its window closes without a process exit. Runs on the
    /// query STA (called from inside ListWindowsAsync).</summary>
    private string LazyHandleFor(IntPtr hwnd, int pid)
    {
        if (_byHwnd.TryGetValue(hwnd, out var existing)
            && _ids.TryGetValue(existing, out var cachedPid)
            && CanReuseHandle(cachedPid, pid))
            return existing;
        // Recycled (stale id bound to this hwnd but a different pid): reclaim the gone window's state.
        if (existing is not null) Invalidate(new WindowHandle(existing));
        var id = $"w{Interlocked.Increment(ref _counter)}";
        _ids[id] = pid;
        _hwnds[id] = hwnd;
        _byHwnd[hwnd] = id;
        TryWatchProcessExit(id, pid);
        return id;
    }
```
> **PANEL NOTE (for AGY-AFTER / reviewer):** `LazyHandleFor` calls `Invalidate` re-entrantly inside the `ListWindowsAsync` query-STA loop when a recycled hwnd is detected. `Invalidate` is dict-ops + `WindowInvalidated?.Invoke`; the three subscribers self-marshal (`PostToQuerySta`) or are thread-safe, so this is believed safe — flag for pressure-test.

- [ ] **Step 4 — Run, verify pass.** `dotnet test --filter "FullyQualifiedName~WindowLivenessTests"` → all PASS (4 original + 2 new). `dotnet build` → 5/0/0. (`LazyHandleFor` is not yet called — wired in T5 — so no behavior change ships in this commit.)

- [ ] **Step 5 — Commit.**
```bash
git add src/FlaUI.Mcp.Core/Windows/WindowManager.cs test/FlaUI.Mcp.Tests/Windows/WindowLivenessTests.cs
git commit -m "feat(windows): LazyHandleFor mint-or-reuse (pure-Win32) + CanReuseHandle recycle guard"
```

---

### Task 4: `ResolveWindow` — lazy resolution with M2 pid-reverify-before-bind; rewire the two read paths

**Oracle:** M2 (spec §#1). The raw Win32 `GetWindowThreadProcessId(hwnd)==recordedPid` check MUST run BEFORE `FromHandle().AsWindow()`; on mismatch throw `WindowHandleStale` and NEVER call `FromHandle`. Eager handles (already in `_handles`) MUST behave exactly as today.

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Windows/WindowManager.cs` (`RunOnWindowAsync` 95-102, `RunWithWindowAndDesktopAsync` 107-114; new `ResolveWindow` + `HwndStillOwnedBy`)
- Test: `test/FlaUI.Mcp.Tests/Windows/WindowLivenessTests.cs` (headless static) + `WindowManagerTests.cs` (Desktop)

- [ ] **Step 0 — State-verify.** Confirm `RunOnWindowAsync`/`RunWithWindowAndDesktopAsync` currently guard with `if (!_handles.TryGetValue(handle.Id, out var w)) throw WindowHandleStale` and that `GetWindowThreadProcessId(IntPtr, out uint)` is already `[DllImport]`-declared (line 217-218). If not, STOP → `STATE_MISMATCH`.

- [ ] **Step 1 — Write the failing headless test** (append to `WindowLivenessTests.cs`):
```csharp
    [Fact]
    public void HwndStillOwnedBy_true_when_current_pid_matches_recorded()
        => Assert.True(WindowManager.HwndStillOwnedBy(recordedPid: 42, currentPidForHwnd: 42));

    [Fact]
    public void HwndStillOwnedBy_false_when_hwnd_recycled_to_a_different_pid()
        => Assert.False(WindowManager.HwndStillOwnedBy(recordedPid: 42, currentPidForHwnd: 99));

    [Fact]
    public void HwndStillOwnedBy_false_for_a_dead_hwnd_zero_pid()
        => Assert.False(WindowManager.HwndStillOwnedBy(recordedPid: 42, currentPidForHwnd: 0));
```

- [ ] **Step 2 — Run, verify it fails.** `dotnet test --filter "FullyQualifiedName~WindowLivenessTests.HwndStillOwnedBy"` → FAIL to compile.

- [ ] **Step 3 — Implement `HwndStillOwnedBy` + `ResolveWindow`, rewire the reads.** Add near `CanReuseHandle`:
```csharp
    /// <summary>M2 pure identity check for lazy resolution: the HWND recorded at mint time must still
    /// belong to the recorded pid before UIA binds to it (a recycled HWND may now host a different,
    /// possibly sensitive process). The live GetWindowThreadProcessId read is the caller's; this is the
    /// pure decision. currentPidForHwnd==0 (dead/invalid hwnd) never matches a real recorded pid.</summary>
    internal static bool HwndStillOwnedBy(int recordedPid, int currentPidForHwnd) =>
        currentPidForHwnd == recordedPid;
```
Add (private, near the read methods):
```csharp
    /// <summary>Resolve a handle to its UIA Window ON THE QUERY STA. Eager handles hit the cached
    /// _handles Window directly (unchanged). A lazily-minted handle (list includeHandles) has no _handles
    /// entry yet: bind it now from the recorded HWND — but ONLY after the M2 Win32 pid-reverify confirms
    /// the HWND still belongs to the recorded pid, so a recycled HWND can never inject UIA into a
    /// different process. Single query STA ⇒ the check-then-bind is race-free and GetOrAdd is safe.</summary>
    private Window ResolveWindow(WindowHandle handle)
    {
        if (_handles.TryGetValue(handle.Id, out var cached)) return cached;
        if (!_ids.TryGetValue(handle.Id, out var recordedPid)
            || !_hwnds.TryGetValue(handle.Id, out var hwnd) || hwnd == IntPtr.Zero)
            throw new ToolException(ToolErrorCode.WindowHandleStale,
                $"Handle {handle.Id} is no longer valid.", "re-list windows and re-open");
        GetWindowThreadProcessId(hwnd, out uint curPid);
        if (!HwndStillOwnedBy(recordedPid, (int)curPid))
            throw new ToolException(ToolErrorCode.WindowHandleStale,
                $"Handle {handle.Id} no longer refers to its original window (its HWND was recycled).",
                "re-list windows and re-open");
        var bound = _handles.GetOrAdd(handle.Id, _ => _automation.FromHandle(hwnd).AsWindow());
        // Close the Invalidate-vs-lazy-bind race (AGY-AFTER seat B): a ThreadPool proc.Exited can run
        // Invalidate BETWEEN the _ids check above and this GetOrAdd — consuming the exactly-once gate and
        // clearing _hwnds while _handles was still empty. Our GetOrAdd would then insert an ORPHANED COM
        // wrapper that (a) leaks (the gate is spent, so it is never evicted) AND (b) keeps resolving on the
        // fast _handles path above with NO pid-reverify, silently defeating the invalidation. Re-check the
        // gate after binding: if it is gone, evict our own insert and fail closed. (ids are monotonic /
        // never-reused, so removing our own orphan is unambiguous; TryRemove is idempotent vs Invalidate's.)
        if (!_ids.ContainsKey(handle.Id))
        {
            _handles.TryRemove(handle.Id, out _);
            throw new ToolException(ToolErrorCode.WindowHandleStale,
                $"Handle {handle.Id} was invalidated during resolution.", "re-list windows and re-open");
        }
        return bound;
    }
```
Rewrite `RunOnWindowAsync` and `RunWithWindowAndDesktopAsync` bodies to delegate to `ResolveWindow`:
```csharp
    public Task<T> RunOnWindowAsync<T>(WindowHandle handle, Func<Window, T> func) =>
        _dispatcher.RunQueryAsync(() => func(ResolveWindow(handle)));

    public Task<T> RunWithWindowAndDesktopAsync<T>(WindowHandle handle, Func<Window, AutomationElement, T> func) =>
        _dispatcher.RunQueryAsync(() => func(ResolveWindow(handle), _automation.GetDesktop()));
```

- [ ] **Step 4 — Run, verify pass.** `dotnet test --filter "FullyQualifiedName~WindowLivenessTests"` → all PASS. `dotnet build` → 5/0/0; `dotnet test --filter "Category!=Desktop"` → green (eager reads unchanged).

- [ ] **Step 5 — Add the Desktop-category live test** (append to `WindowManagerTests.cs`, `[Trait("Category","Desktop")]` per the file's existing convention — match the surrounding attribute exactly):
```csharp
    // A lazily-minted handle (via ListWindowsAsync includeHandles, Task 5) resolves to a live window on
    // first read WITHOUT a prior desktop_open_window. Uses the test app fixture window.
    [Fact]
    [Trait("Category", "Desktop")]
    public async Task Lazy_handle_from_list_resolves_on_first_read()
    {
        var list = await _manager.ListWindowsAsync(includeBounds: false, includeHandles: true);
        var mine = list.First(w => w.Pid == _fixture.Pid);
        Assert.NotNull(mine.Handle);
        // First read binds UIA lazily + passes the pid-reverify → no throw.
        var title = await _manager.RunOnWindowAsync(new WindowHandle(mine.Handle!), w => w.Title);
        Assert.False(string.IsNullOrEmpty(title));
    }
```
> If `WindowManagerTests` field/fixture names differ (`_manager`/`_fixture`/`Pid`), the implementer MUST match the file's actual names — read them first; do not invent.

- [ ] **Step 6 — Commit.**
```bash
git add src/FlaUI.Mcp.Core/Windows/WindowManager.cs test/FlaUI.Mcp.Tests/Windows/WindowLivenessTests.cs test/FlaUI.Mcp.Tests/Windows/WindowManagerTests.cs
git commit -m "feat(windows): lazy ResolveWindow with M2 pid-reverify-before-bind (HWND-recycle guard)"
```

---

### Task 5: Wire `includeHandles` through `ListWindowsAsync` + `WindowTools`

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Windows/WindowManager.cs` (`ListWindowsAsync` overloads 42-61)
- Modify: `src/FlaUI.Mcp.Server/Tools/WindowTools.cs:16-19`
- Test: `test/FlaUI.Mcp.Tests/Windows/WindowManagerTests.cs` (Desktop)

- [ ] **Step 0 — State-verify.** Confirm `ListWindowsAsync()`→`ListWindowsAsync(false)` (42) and the 1-arg `ListWindowsAsync(bool includeBounds)` (44-61) holds the `RunQueryAsync` body with the `EnumTopLevel` loop building `WindowInfo`. Confirm `WindowTools.DesktopListWindows(bool includeBounds=false)` (17-19) calls `_windows.ListWindowsAsync(includeBounds)`. If different, STOP → `STATE_MISMATCH`.

- [ ] **Step 1 — Add the two-arg overload + wire the loop.** Replace lines 42-61 with:
```csharp
    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync() => ListWindowsAsync(false, false);

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(bool includeBounds) => ListWindowsAsync(includeBounds, false);

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(bool includeBounds, bool includeHandles) =>
        _dispatcher.RunQueryAsync<IReadOnlyList<WindowInfo>>(() =>
        {
            PruneClosedWindows(); // Phase 6 backstop: reclaim windows closed w/o a process exit
            // PURE Win32 — no UIA. A UIA Title/ProcessId read on the query STA blocks with no
            // timeout on ANY momentarily-unresponsive desktop window; Win32 GetWindowText does not.
            // includeHandles mints via LazyHandleFor (pure Win32 + dict writes) — still no UIA touch.
            var foreground = GetForegroundWindow();
            var list = new List<WindowInfo>(); int z = 0;
            foreach (var (hwnd, title, pid) in EnumTopLevel())
            {
                WindowBounds? b = null;
                if (includeBounds && GetWindowRect(hwnd, out var r))
                    b = new WindowBounds(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                string? handle = includeHandles ? LazyHandleFor(hwnd, pid) : null;
                list.Add(new WindowInfo(title, SafeProcessName(pid), pid, hwnd == foreground, b,
                    includeBounds ? z : (int?)null, handle));
                z++;
            }
            return list;
        });
```

- [ ] **Step 2 — Add the tool param.** Replace `WindowTools.DesktopListWindows` (16-19) with:
```csharp
    [McpServerTool(ReadOnly = true), Description("List top-level desktop windows (Title, ProcessName, Pid, IsForeground). Opt-in includeBounds adds absolute physical-px Bounds + ZOrder (0=topmost, for occlusion reasoning). Opt-in includeHandles adds a reusable handle (e.g. w1) to each window so you can snapshot/find/interact directly, skipping a separate desktop_open_window call. Pure Win32 — never blocks on an unresponsive window. For per-window control counts, open a window and call desktop_snapshot_stats.")]
    public Task<string> DesktopListWindows(
        [Description("Add Bounds + ZOrder to each window (default false).")] bool includeBounds = false,
        [Description("Add a reusable handle (wN) to each window, so you can act/read without desktop_open_window (default false).")] bool includeHandles = false)
        => ToolResponse.Guard(async () => ToolResponse.Ok(await _windows.ListWindowsAsync(includeBounds, includeHandles)));
```

- [ ] **Step 3 — Build + headless regression.** `dotnet build` → 5/0/0; `dotnet test --filter "Category!=Desktop"` → green. Default response (`includeHandles:false`) is byte-identical (handle null → omitted, verified by T1's serialization test).

- [ ] **Step 4 — Desktop live test: mint-or-reuse + direct use** (append to `WindowManagerTests.cs`):
```csharp
    [Fact]
    [Trait("Category", "Desktop")]
    public async Task ListWindows_includeHandles_reuses_the_same_wN_across_calls()
    {
        var first = await _manager.ListWindowsAsync(false, includeHandles: true);
        var second = await _manager.ListWindowsAsync(false, includeHandles: true);
        var h1 = first.First(w => w.Pid == _fixture.Pid).Handle;
        var h2 = second.First(w => w.Pid == _fixture.Pid).Handle;
        Assert.Equal(h1, h2); // mint-or-reuse keyed by hwnd — no fresh id per poll
    }
```

- [ ] **Step 5 — Commit.**
```bash
git add src/FlaUI.Mcp.Core/Windows/WindowManager.cs src/FlaUI.Mcp.Server/Tools/WindowTools.cs test/FlaUI.Mcp.Tests/Windows/WindowManagerTests.cs
git commit -m "feat(windows): desktop_list_windows includeHandles → inline reusable wN (Phase 10 #1)"
```

---

### Task 6: Fold in the dogfood skill — `desktop_list_windows(includeHandles)` note

**Files:**
- Modify: `.claude/skills/driving-flaui-mcp/SKILL.md` (Orientation section, ~lines 32-38)

- [ ] **Step 0 — State-verify.** Confirm the "## Orientation (read-only, always safe)" numbered list exists with item 1 = `desktop_list_windows`, item 2 = `desktop_open_window`. If different, STOP → `STATE_MISMATCH`.

- [ ] **Step 1 — Edit.** Under Orientation item 1, append guidance. Replace the item-1 line:
```
1. `desktop_list_windows` — Title/ProcessName/Pid; exactly one `IsForeground:true`. Hang-proof.
```
with:
```
1. `desktop_list_windows` — Title/ProcessName/Pid; exactly one `IsForeground:true`. Hang-proof.
   Pass **`includeHandles:true`** to get a reusable `wN` on each window inline — act/read (snapshot,
   find, interaction) **directly, skipping step 2's `desktop_open_window`**. Handles are minted lazily
   (the list stays pure-Win32/non-blocking) and reused across polls; the UIA binding happens on first
   use, guarded so a recycled HWND fails `WindowHandleStale` rather than acting on the wrong window.
```

- [ ] **Step 2 — Verify.** No build (docs only). Confirm the note reads cleanly and mentions `includeHandles:true` + the skip-`desktop_open_window` win.

- [ ] **Step 3 — Commit.**
```bash
git add .claude/skills/driving-flaui-mcp/SKILL.md
git commit -m "docs(skill): driving-flaui-mcp — desktop_list_windows includeHandles inline wN"
```

---

### Task 7: Version bump 0.10.1→0.11.0 + CHANGELOG + README/ops-manual/ROADMAP

> This release also carries the 4 already-landed next-release polish commits (overlay `on|off` verb, `--help`, discoverability, their docs). The CHANGELOG `[Unreleased]` section already holds those — rename it to `[0.11.0]` and add the window-handles entry.

**Files:**
- Modify: `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj` (`<Version>`), `installer/flaui-mcp.iss` (`AppVersion`)
- Modify: `CHANGELOG.md`, `README.md`, `docs/ops-manual.md`, `ROADMAP.md`

- [ ] **Step 0 — State-verify.** Grep the csproj for `<Version>0.10.1` and the iss for `AppVersion` `0.10.1`; confirm `CHANGELOG.md` has a `## [Unreleased]` section (from the polish commits). Report the exact strings found before editing.

- [ ] **Step 1 — Bump versions.** csproj `<Version>0.10.1</Version>`→`0.11.0`; iss `AppVersion` `0.10.1`→`0.11.0`.

- [ ] **Step 2 — CHANGELOG.** Rename `## [Unreleased]` → `## [0.11.0] - 2026-07-04` and add, under an `### Added` subhead, an entry for opt-in `desktop_list_windows(includeHandles:true)` → inline reusable `wN` (lazy fork-1a mint, non-blocking list, HWND-recycle pid-reverify guard, mint-or-reuse across polls). Keep the existing overlay/`--help` bullets already in that section.

- [ ] **Step 3 — README + ops-manual + ROADMAP.** README: in the window/orientation area, one line that `desktop_list_windows` takes `includeHandles` to return `wN` inline (skip `desktop_open_window`). ops-manual: no CLI change (this is an MCP tool param, not a CLI verb) — only touch if a tools table lists params. ROADMAP: mark Phase 10 #1 shipped in v0.11.0.

- [ ] **Step 4 — Build + full headless.** `dotnet build` → 5/0/0; `dotnet test --filter "Category!=Desktop"` → all green.

- [ ] **Step 5 — Commit.**
```bash
git add src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj installer/flaui-mcp.iss CHANGELOG.md README.md docs/ops-manual.md ROADMAP.md
git commit -m "release(0.11.0): desktop_list_windows includeHandles + version/docs bump"
```

---

## After all tasks

1. Dispatch a **final whole-branch `ecc:csharp-reviewer`** over the WindowManager diff (lifecycle/COM/race focus — this is concurrency-sensitive code; the `LazyHandleFor` re-entrant `Invalidate` and the STA `GetOrAdd` are the hot spots).
2. **AGY-AFTER capstone merge-gate panel** on the branch (as prior releases).
3. Use **superpowers:finishing-a-development-branch** for the merge decision.
4. Release cut on USER go-ahead: `git tag v0.11.0` → `git push origin master --tags` → release.yml (4 assets). This cut bundles the 4 unpushed polish commits.
5. **Controller live-smoke needs a real console — ASK USER TO EXIT RDP:** `desktop_list_windows includeHandles:true` returns a `wN`; snapshot/act on that `wN` directly with NO `desktop_open_window`; poll twice → same `wN` (reuse); a stale `wN` after its window closes fails `WindowHandleStale`.

## Self-review (writing-plans)

- **Spec coverage:** fork 1a (all tasks), M1 dedicated gate (T2), M2 pid-reverify-before-bind (T4), lazy-populates-`_hwnds`-for-prune (T3/spec crux), mint-or-reuse keyed by hwnd (T3/T5), HWND-recycle at list-time (T3 pid check) + use-time (T4), dogfood skill fold-in (T6). All covered.
- **Type consistency:** `_ids` `ConcurrentDictionary<string,int>`; `_byHwnd` `ConcurrentDictionary<IntPtr,string>`; `WindowInfo.Handle` `string?`; `ResolveWindow` returns `Window`; statics `CanReuseHandle(int,int)`/`HwndStillOwnedBy(int,int)`/`DeadWindowIds(...)` all `internal static`. `ListWindowsAsync(bool,bool)` is the impl; 0-/1-arg delegate.
- **Test env:** pure logic (statics, wire shape) is headless; instance/UIA behavior is `Category=Desktop` (bounded-chunk / smoke). No synthetic input involved, so RDP-headless is not a blocker for the read-only handle path.
