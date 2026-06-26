# FlaUI.Mcp Phase 3a — Pattern-Based Interaction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the "agent can act" increment (v0.3.0): 9 UIA-pattern interaction tools, an action-STA execution model that survives modal-blocking, and an opt-in read-only gate — with **no synthetic input** (that is Phase 4).

**Architecture:** Actions run on a **transient STA thread per call** (raw `Thread` + `TaskCompletionSource`, abandonable on modal block, capped) with their **own per-action `UIA3Automation`**; an element ref is re-resolved **cache-free** on that action STA (resolve the window via `FromHandle(hwnd)`, search roots = window + grafted popups) so no query-STA COM object ever crosses apartments. A stateless `Interactor` performs the pattern call.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`), FlaUI.UIA3 5.0.0, ModelContextProtocol 1.4.0, xUnit. Source of truth: [`docs/superpowers/specs/2026-06-26-flaui-mcp-phase3-interaction-design.md`](../specs/2026-06-26-flaui-mcp-phase3-interaction-design.md) (`8fd4524`).

**Conventions (verified in repo):**
- UIA/desktop tests carry `[Trait("Category", "Desktop")]`; headless CI runs `dotnet test --filter "Category!=Desktop"`. Pure-logic tests omit the trait.
- Desktop tests that mutate UI launch their **own** `TestAppFixture` (no shared fixture).
- Build the TestApp before running desktop tests: `dotnet build test/FlaUI.Mcp.TestApp`.
- `WindowHandle` is `readonly record struct WindowHandle(string Id)`.
- Tool wire-error boundary is `ToolResponse.Guard`; error codes serialize as their enum **name** (`ToolErrorCode.ToString()`).

**Out of scope (3b / later):** screenshot, get_bounds, get_text/set_caret/select_text_range, grid, snapshot_diff/stats/global, clipboard, wait_for/_stable, `VisionCapture`. Do **not** build these.

---

### Task 1: Add the two Phase-3a error codes

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Errors/ToolErrorCode.cs`
- Test: `test/FlaUI.Mcp.Tests/Errors/ToolExceptionTests.cs` (existing)

- [ ] **Step 1: Add the enum members.** Insert `WriteBlockedReadOnly` and `TooManyPendingActions` immediately before `Timeout` (mirrors how `TargetDenied` was added). Final enum body:

```csharp
public enum ToolErrorCode
{
    WindowNotFound,
    WindowHandleStale,
    RefNotFound,
    RefStaleUnresolvable,
    PatternUnsupported,
    ElementNotActionable,
    AmbiguousMatch,
    LaunchTimeout,
    AccessDeniedIntegrity,
    ActionBlockedPending,
    ElementDisappearedDuringAction,
    UacPromptDetected,
    TargetDenied,
    WriteBlockedReadOnly,
    TooManyPendingActions,
    Timeout
}
```

- [ ] **Step 2: Pin the wire names with a test.** Append to `ToolExceptionTests.cs`:

```csharp
[Fact]
public void New_phase3a_codes_serialize_by_name()
{
    Assert.Equal("WriteBlockedReadOnly", ToolErrorCode.WriteBlockedReadOnly.ToString());
    Assert.Equal("TooManyPendingActions", ToolErrorCode.TooManyPendingActions.ToString());
}
```

- [ ] **Step 3: Run** `dotnet test --filter "Category!=Desktop"` → PASS (new test green).
- [ ] **Step 4: Commit** `feat(errors): add WriteBlockedReadOnly + TooManyPendingActions codes`.

---

### Task 2: Task C — per-action transient-STA dispatcher + in-flight cap

The current action context (`StaThreadContext _action`) is a **single** long-lived STA thread draining a queue: a blocking `Invoke` parks it and a second action deadlocks. Replace the action path with a transient STA thread per call (abandonable), capped to bound parked-thread accumulation.

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Threading/AutomationDispatcher.cs`
- Test: `test/FlaUI.Mcp.Tests/Threading/AutomationDispatcherTests.cs` (existing — must stay green)

- [ ] **Step 1: Write the failing cap test.** Append to `AutomationDispatcherTests.cs`:

```csharp
[Fact]
public async Task Action_dispatcher_caps_in_flight_actions()
{
    using var d = new AutomationDispatcher();
    using var gate = new ManualResetEventSlim(false);
    // Fill the cap (5) with parked actions; each throws ActionBlockedPending but stays parked.
    var parked = new List<Task>();
    for (int i = 0; i < 5; i++)
        parked.Add(Assert.ThrowsAsync<ToolException>(
            () => d.RunActionAsync(() => { gate.Wait(); return 0; }, timeoutMs: 100)));
    await Task.WhenAll(parked); // all 5 timed out (parked), slots still held

    var ex = await Assert.ThrowsAsync<ToolException>(
        () => d.RunActionAsync(() => 0, timeoutMs: 1000));
    Assert.Equal(ToolErrorCode.TooManyPendingActions, ex.Code);

    gate.Set(); // release the 5 parked threads so they unwind and free their slots
}
```

- [ ] **Step 2: Run** `dotnet test --filter "Category!=Desktop"` → FAIL (no cap yet; 6th call runs).

- [ ] **Step 3: Rewrite `AutomationDispatcher` to the per-action model.** Full file:

```csharp
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Threading;

/// <summary>
/// Marshals UIA/COM work onto STA threads. Reads run on a single long-lived query STA that
/// must stay responsive. Each ACTION runs on its OWN transient STA thread with its own COM
/// state, so a potentially-blocking call (InvokePattern.Invoke parks until a modal it opened
/// is dismissed) can be abandoned on timeout (ACTION_BLOCKED_PENDING) WITHOUT starving the
/// next action — a single shared action thread would deadlock there. An Interlocked cap bounds
/// parked-thread accumulation from a runaway/injected loop until Phase 4's full action budget.
/// </summary>
public sealed class AutomationDispatcher : IDisposable
{
    private const int MaxPendingActions = 5;

    private readonly StaThreadContext _query = new("uia-query-sta");
    private int _pendingActions;

    public Task<T> RunQueryAsync<T>(Func<T> func) => _query.RunAsync(func);
    public Task RunQueryAsync(Action action) => _query.RunAsync(action);

    public Task<T> RunActionAsync<T>(Func<T> func, int timeoutMs)
    {
        if (Interlocked.Increment(ref _pendingActions) > MaxPendingActions)
        {
            Interlocked.Decrement(ref _pendingActions);
            throw new ToolException(
                ToolErrorCode.TooManyPendingActions,
                $"Too many actions ({MaxPendingActions}) are blocked pending; refusing to start another.",
                suggestedRecovery: "snapshot the window(s) and dismiss the pending modal dialog(s), then retry");
        }

        // Per-action STA thread. Completes its TCS via TrySet* only (never Set*) so a late
        // wake-up after abandonment cannot double-complete; the catch-all keeps a delayed
        // throw off the background thread (an unhandled one would crash the whole server).
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.TrySetResult(func()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
            finally { Interlocked.Decrement(ref _pendingActions); }
        })
        { Name = "uia-action-sta", IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return AwaitWithTimeout(tcs.Task, timeoutMs);
    }

    public Task RunActionAsync(Action action, int timeoutMs)
        => RunActionAsync(() => { action(); return true; }, timeoutMs);

    private static async Task<T> AwaitWithTimeout<T>(Task<T> work, int timeoutMs)
    {
        var done = await Task.WhenAny(work, Task.Delay(timeoutMs));
        if (done != work)
        {
            // Abandon the parked thread but observe its eventual fault so it never surfaces as
            // an unobserved task exception when the modal is finally dismissed.
            _ = work.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
            throw new ToolException(
                ToolErrorCode.ActionBlockedPending,
                "Action did not return within the timeout; it likely opened a modal dialog.",
                suggestedRecovery: "snapshot the window to see the dialog, then act on it");
        }
        return await work; // observe exceptions if it actually completed
    }

    public void Dispose() => _query.Dispose();
}
```

- [ ] **Step 4: Run** `dotnet test --filter "Category!=Desktop"` → PASS. Confirm the three existing dispatcher tests (`Query_and_action_run_on_distinct_STA_threads`, `Blocking_action_throws_ActionBlockedPending_after_timeout`, `Query_context_stays_responsive_while_an_action_is_blocked`) and the new cap test are all green.
- [ ] **Step 5: Commit** `feat(threading): per-action transient STA threads + in-flight cap (Task C)`.

---

### Task 3: Task A — cache the window HWND + an action-STA window primitive

The action STA cannot touch the query-STA `Window`. Cache each window's native handle at registration (read on the query STA) and add an action-STA primitive that bootstraps its own automation via `FromHandle`.

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Windows/WindowManager.cs`
- Test: `test/FlaUI.Mcp.Tests/Windows/WindowManagerTests.cs` (existing; class is already `[Trait("Category","Desktop")]`)

- [ ] **Step 1: Write the failing test.** Append to `WindowManagerTests.cs`:

```csharp
[Fact]
public async Task An_action_primitive_resolves_the_window_on_a_distinct_STA_thread()
{
    using var app = new FlaUI.Mcp.Tests.TestAppFixture();
    using var dispatcher = new AutomationDispatcher();
    using var mgr = new WindowManager(dispatcher);
    var handle = await mgr.OpenByPidAsync(app.Process.Id);

    var queryThread = await dispatcher.RunQueryAsync(() => Environment.CurrentManagedThreadId);
    var (title, actionThread) = await mgr.RunOnWindowActionAsync(handle,
        (win, _) => (win.AsWindow().Title, Environment.CurrentManagedThreadId), timeoutMs: 5000);

    Assert.Contains("TestApp", title);
    Assert.NotEqual(queryThread, actionThread); // resolved on a transient action STA, not the query STA
}
```

- [ ] **Step 2: Run** `dotnet build test/FlaUI.Mcp.TestApp && dotnet test --filter "FullyQualifiedName~An_action_primitive_resolves"` → FAIL (method missing).

- [ ] **Step 3: Implement.** In `WindowManager.cs`:
  (a) Add a hwnd cache field beside `_handles`:

```csharp
    private readonly ConcurrentDictionary<string, IntPtr> _hwnds = new();
```

  (b) In `Register`, capture the native handle (we are on the query STA here):

```csharp
    internal WindowHandle Register(Window window, int pid)
    {
        var id = $"w{Interlocked.Increment(ref _counter)}";
        _handles[id] = window;
        try { _hwnds[id] = window.Properties.NativeWindowHandle.ValueOrDefault; } catch { /* no hwnd */ }
        TryWatchProcessExit(id, pid);
        return new WindowHandle(id);
    }
```

  (c) In `Invalidate`, also drop the hwnd:

```csharp
    public void Invalidate(WindowHandle handle)
    {
        _handles.TryRemove(handle.Id, out _);
        _hwnds.TryRemove(handle.Id, out _);
        if (_watched.TryRemove(handle.Id, out var p))
            try { p.Dispose(); } catch { }
    }
```

  (d) Add the action-STA primitive. It creates its OWN `UIA3Automation` on the transient action thread and resolves the window by handle — never touching `_automation` or the cached `Window`:

```csharp
    /// <summary>Run a callback on a transient ACTION STA with the window and Desktop resolved
    /// by that thread's OWN automation (via the cached HWND) — so no query-STA COM object is
    /// marshaled across apartments. Used by all state-changing pattern actions.</summary>
    public Task<T> RunOnWindowActionAsync<T>(
        WindowHandle handle, Func<AutomationElement, AutomationElement, T> func, int timeoutMs)
    {
        if (!_hwnds.TryGetValue(handle.Id, out var hwnd) || hwnd == IntPtr.Zero)
            throw new ToolException(ToolErrorCode.WindowHandleStale,
                $"Handle {handle.Id} is no longer valid.", "re-list windows and re-open");
        return _dispatcher.RunActionAsync(() =>
        {
            using var automation = new UIA3Automation();
            var win = automation.FromHandle(hwnd);
            var desktop = automation.GetDesktop();
            return func(win, desktop);
        }, timeoutMs);
    }
```

- [ ] **Step 4: Run** the test from Step 2 → PASS. Then `dotnet test --filter "Category!=Desktop"` → still green.
- [ ] **Step 5: Commit** `feat(windows): cache window HWND + action-STA window primitive (Task A)`.

---

### Task 4: Task B-1 — extract popup-finding into a shared, stateless helper

`FindOwnerPopups` / `SearchRoots` are private to `PerceptionManager` (query path). The action path must build the **same** roots. Extract them verbatim into a stateless helper both paths call.

**Files:**
- Create: `src/FlaUI.Mcp.Core/Perception/PopupFinder.cs`
- Modify: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs`
- Test: `test/FlaUI.Mcp.Tests/Perception/PopupGraftingTests.cs` (existing — must stay green; it proves the query path still grafts popups)

- [ ] **Step 1: Create `PopupFinder.cs`** by moving the existing `SafePid`, `SafeRuntimeId`, `SafeRect`, `FindOwnerPopups`, and `SearchRoots` bodies out of `PerceptionManager` **unchanged** (only visibility changes: `SearchRoots` and `FindOwnerPopups` become `public static`):

```csharp
using FlaUI.Core.AutomationElements;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>Stateless popup/search-root construction shared by the query path
/// (PerceptionManager.SnapshotAsync/RunOnRefAsync) and the action path
/// (PerceptionManager.RunOnRefActionAsync). Context menus / dropdowns live at the Desktop or
/// as direct window children — both must appear as resolution search roots so a ref into a
/// grafted popup re-resolves identically on either STA.</summary>
public static class PopupFinder
{
    /// <summary>Window subtree first, then the owner-process popup subtrees.
    /// searchRoots[0] MUST be the window root (IndexPath is window-relative).</summary>
    public static IReadOnlyList<AutomationElement> SearchRoots(AutomationElement win, AutomationElement desktop)
    {
        var roots = new List<AutomationElement> { win };
        roots.AddRange(FindOwnerPopups(desktop, win));
        return roots;
    }

    public static IReadOnlyList<AutomationElement> FindOwnerPopups(AutomationElement desktop, AutomationElement targetWindow)
    {
        // ===== PASTE THE EXISTING FindOwnerPopups BODY FROM PerceptionManager VERBATIM =====
        // (the two-path Win32/#32768 + WPF-Popup scan, with the SafePid/SafeRuntimeId/SafeRect
        //  guards). Do not alter the heuristics — PopupGraftingTests is the oracle.
    }

    private static int SafePid(AutomationElement el)
    { try { return el.Properties.ProcessId.ValueOrDefault; } catch { return -1; } }

    private static int[] SafeRuntimeId(AutomationElement el)
    { try { return el.Properties.RuntimeId.ValueOrDefault ?? Array.Empty<int>(); } catch { return Array.Empty<int>(); } }

    private static System.Drawing.Rectangle SafeRect(AutomationElement el)
    { try { return el.BoundingRectangle; } catch { return System.Drawing.Rectangle.Empty; } }
}
```

  > **SHAPE-DIVERGENCE STOP:** copy the `FindOwnerPopups` body **exactly** (both scan paths, all class-name/control-type guards). If anything would change, STOP and report — `PopupGraftingTests` defines correct behavior.

- [ ] **Step 2: Update `PerceptionManager`** to delegate. Delete its private `SafePid`/`SafeRuntimeId`/`SafeRect`/`FindOwnerPopups`/`SearchRoots`, and replace call sites:
  - `RunOnRefAsync`: `_refs.Resolve(handle.Id, @ref, PopupFinder.SearchRoots(win, desktop))`
  - `SnapshotAsync`: `IReadOnlyList<AutomationElement> popups = PopupFinder.FindOwnerPopups(desktop, win);` and `_refs.Resolve(handle.Id, options.RootRef!, PopupFinder.SearchRoots(win, desktop))`
  - Keep `SafeProcessName` (denylist) where it is — it is unrelated to popups.

- [ ] **Step 3: Run** `dotnet build test/FlaUI.Mcp.TestApp && dotnet test` (full). Expected: `PopupGraftingTests` + `RefResolutionTests` still green (behavior unchanged; pure extraction).
- [ ] **Step 4: Commit** `refactor(perception): extract PopupFinder shared helper (Task B-1)`.

---

### Task 5: Task B-2 — extract a cache-free descriptor walk in RefRegistry

The action path must re-resolve from the descriptor **without** the cached (query-STA) element. Split `Resolve` so the descriptor walk (steps 2–3) is callable on its own.

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/RefRegistry.cs`
- Test: `test/FlaUI.Mcp.Tests/Perception/RefResolutionTests.cs` (existing — must stay green)

- [ ] **Step 1: Add a cache-free `ResolveDescriptor`** and make `Resolve` delegate to it. Replace the body of `Resolve` (keep its signature/doc) so that after the cache fast-path it calls the new method; add the new method holding the existing walk verbatim:

```csharp
    public AutomationElement Resolve(string windowId, string @ref, IReadOnlyList<AutomationElement> searchRoots)
    {
        var entry = Lookup(windowId, @ref); // REF_NOT_FOUND if absent
        var d = entry.Descriptor;

        // (1) cached fast-path — query-STA only. RuntimeId AND ControlType match AND not offscreen.
        if (entry.Cached is { } cached)
        {
            try
            {
                var rid = cached.Properties.RuntimeId.ValueOrDefault;
                if (rid != null && rid.AsEnumerable().SequenceEqual(d.RuntimeId) && cached.ControlType == d.ControlType
                    && !cached.Properties.IsOffscreen.ValueOrDefault)
                    return cached;
            }
            catch { /* element gone — fall through to the cache-free walk */ }
        }

        return ResolveDescriptor(d, searchRoots, @ref);
    }

    /// <summary>Cache-free re-resolution from a descriptor against caller-supplied roots
    /// (window first, then grafted popups). Used by the ACTION STA, which must NOT touch the
    /// query-STA cached element. Throws REF_STALE_UNRESOLVABLE if the element is gone.</summary>
    public AutomationElement ResolveDescriptor(ElementDescriptor d, IReadOnlyList<AutomationElement> searchRoots, string @ref)
    {
        // (2) descriptor re-walk per search root: AutomationId then Name+ControlType, scoped
        // under the nearest stable ancestor.
        foreach (var searchRoot in searchRoots)
        {
            if (searchRoot is null) continue;
            var scope = searchRoot;
            if (!string.IsNullOrEmpty(d.AncestorAutomationId))
            {
                var anc = TrySearch(searchRoot, cf => cf.ByAutomationId(d.AncestorAutomationId));
                if (anc is not null) scope = anc;
            }
            if (!string.IsNullOrEmpty(d.AutomationId))
            {
                var byAid = TrySearch(scope, cf => cf.ByAutomationId(d.AutomationId));
                if (byAid is not null) return byAid;
            }
            if (!string.IsNullOrEmpty(d.Name))
            {
                var byName = TrySearch(scope, cf => cf.ByName(d.Name).And(cf.ByControlType(d.ControlType)));
                if (byName is not null) return byName;
            }
        }

        // (3) IndexPath last-resort — window-relative only (searchRoots[0] is the window root).
        if (searchRoots.Count > 0)
        {
            var byPath = TryIndexPath(searchRoots[0], d.IndexPath);
            if (byPath is not null) return byPath;
        }

        throw new ToolException(ToolErrorCode.RefStaleUnresolvable,
            $"Ref '{@ref}' could not be re-resolved; the element appears to be gone.",
            "take a fresh desktop_snapshot");
    }
```

  > **SHAPE-DIVERGENCE STOP:** the walk logic (order, conditions, `.And()` usage, IndexPath-on-roots[0]) must be byte-identical to today's `Resolve` steps 2–3. `RefResolutionTests` is the oracle. `internal Entry Lookup` is unchanged — the action path reads `Lookup(windowId, ref).Descriptor`.

- [ ] **Step 2: Run** `dotnet build test/FlaUI.Mcp.TestApp && dotnet test --filter "FullyQualifiedName~RefResolution"` → all green (behavior preserved).
- [ ] **Step 3: Commit** `refactor(perception): extract cache-free ResolveDescriptor (Task B-2)`.

---

### Task 6: Interactor — the stateless pattern executor

**Files:**
- Create: `src/FlaUI.Mcp.Core/Interaction/Interactor.cs`
- Test: `test/FlaUI.Mcp.Tests/Interaction/InteractorTests.cs`

- [ ] **Step 1: Write failing tests** (Desktop). Use only existing targets (`OkButton` Invoke, `Input` Value, `Status` no-Invoke):

```csharp
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

[Trait("Category", "Desktop")]
public class InteractorTests
{
    private static AutomationElement Find(WindowManager mgr, WindowHandle h, string aid) =>
        mgr.RunWithWindowAndDesktopAsync(h, (win, _) =>
            win.FindFirstDescendant(cf => cf.ByAutomationId(aid))
            ?? throw new Xunit.Sdk.XunitException($"{aid} not found")).GetAwaiter().GetResult();

    [Fact]
    public async Task Invoke_clicks_a_button()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(app.Process.Id);
        await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        { Interactor.Invoke(Find(mgr, handle, "OkButton")); return true; });
        var status = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            win.FindFirstDescendant(cf => cf.ByAutomationId("Status")).Name);
        Assert.Equal("clicked", status);
    }

    [Fact]
    public async Task SetValue_writes_text_into_a_value_control()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(app.Process.Id);
        await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        { Interactor.SetValue(Find(mgr, handle, "Input"), "hello"); return true; });
        var val = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            win.FindFirstDescendant(cf => cf.ByAutomationId("Input")).AsTextBox().Text);
        Assert.Equal("hello", val);
    }

    [Fact]
    public async Task Invoke_on_an_element_without_the_pattern_throws_PatternUnsupported()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(app.Process.Id);
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            { Interactor.Invoke(Find(mgr, handle, "Status")); return true; })); // Text has no InvokePattern
        Assert.Equal(ToolErrorCode.PatternUnsupported, ex.Code);
    }
}
```

- [ ] **Step 2: Run** `dotnet build test/FlaUI.Mcp.TestApp && dotnet test --filter "FullyQualifiedName~InteractorTests"` → FAIL (no `Interactor`).

- [ ] **Step 3: Implement `Interactor`.** Each method checks pattern support → throws `PatternUnsupported` if absent (no synthetic fallback in Phase 3) → performs the pattern call:

```csharp
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Stateless executor of a single UIA control-pattern action against an
/// already-resolved element on the correct (action) STA. No synthetic input (Phase 4),
/// no ref resolution, no dispatcher awareness. Throws PATTERN_UNSUPPORTED when the element
/// lacks the requested pattern.</summary>
public static class Interactor
{
    private static ToolException Unsupported(string pattern) => new(
        ToolErrorCode.PatternUnsupported,
        $"Element does not support the {pattern} pattern.",
        "snapshot to see the element's available patterns, or use a different tool");

    public static void Invoke(AutomationElement el)
    {
        if (!el.Patterns.Invoke.IsSupported) throw Unsupported("Invoke");
        el.Patterns.Invoke.Pattern.Invoke();
    }

    public static void SetValue(AutomationElement el, string value)
    {
        if (!el.Patterns.Value.IsSupported) throw Unsupported("Value");
        if (el.Patterns.Value.Pattern.IsReadOnly.ValueOrDefault)
            throw new ToolException(ToolErrorCode.ElementNotActionable,
                "The value control is read-only.", "pick an editable control");
        el.Focus();
        el.Patterns.Value.Pattern.SetValue(value);
    }

    public static void Toggle(AutomationElement el)
    {
        if (!el.Patterns.Toggle.IsSupported) throw Unsupported("Toggle");
        el.Patterns.Toggle.Pattern.Toggle();
    }

    public static void Expand(AutomationElement el)
    {
        if (!el.Patterns.ExpandCollapse.IsSupported) throw Unsupported("ExpandCollapse");
        var p = el.Patterns.ExpandCollapse.Pattern;
        if (p.ExpandCollapseState.ValueOrDefault == ExpandCollapseState.Collapsed) p.Expand();
        else p.Collapse();
    }

    public static void Select(AutomationElement el)
    {
        if (!el.Patterns.SelectionItem.IsSupported) throw Unsupported("SelectionItem");
        el.Patterns.SelectionItem.Pattern.Select();
    }

    public static void ScrollIntoView(AutomationElement el)
    {
        if (!el.Patterns.ScrollItem.IsSupported) throw Unsupported("ScrollItem");
        el.Patterns.ScrollItem.Pattern.ScrollIntoView();
    }

    public static void Scroll(AutomationElement el, string direction, double amount)
    {
        if (!el.Patterns.Scroll.IsSupported) throw Unsupported("Scroll");
        var p = el.Patterns.Scroll.Pattern;
        var v = ScrollAmount.NoAmount; var h = ScrollAmount.NoAmount;
        switch (direction.Trim().ToLowerInvariant())
        {
            case "up": v = ScrollAmount.SmallDecrement; break;
            case "down": v = ScrollAmount.SmallIncrement; break;
            case "left": h = ScrollAmount.SmallDecrement; break;
            case "right": h = ScrollAmount.SmallIncrement; break;
            default:
                throw new ToolException(ToolErrorCode.ElementNotActionable,
                    $"Unknown scroll direction '{direction}'.", "use up|down|left|right");
        }
        int reps = Math.Clamp((int)Math.Round(amount <= 0 ? 1 : amount), 1, 50);
        for (int i = 0; i < reps; i++) p.Scroll(h, v);
    }

    public static void SetFocus(AutomationElement el) => el.Focus();

    public static void WindowTransform(Window win, string action)
    {
        switch (action.Trim().ToLowerInvariant())
        {
            case "maximize":
                if (!win.Patterns.Window.IsSupported || !win.Patterns.Window.Pattern.CanMaximize.ValueOrDefault)
                    throw new ToolException(ToolErrorCode.ElementNotActionable, "Window cannot maximize.", "try restore/minimize");
                win.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized); break;
            case "minimize":
                if (!win.Patterns.Window.IsSupported || !win.Patterns.Window.Pattern.CanMinimize.ValueOrDefault)
                    throw new ToolException(ToolErrorCode.ElementNotActionable, "Window cannot minimize.", "try restore/maximize");
                win.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Minimized); break;
            case "restore":
                if (!win.Patterns.Window.IsSupported)
                    throw new ToolException(ToolErrorCode.ElementNotActionable, "Window pattern unsupported.", "n/a");
                win.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Normal); break;
            default:
                throw new ToolException(ToolErrorCode.ElementNotActionable,
                    $"Unknown window action '{action}'.", "use maximize|minimize|restore");
        }
    }
}
```

  > **STATE-VERIFICATION (Step 0):** confirm FlaUI 5.0.0 member shapes before trusting them — open `FlaUI.Core` and verify `el.Patterns.Invoke.IsSupported`/`.Pattern.Invoke()`, `Patterns.Value.Pattern.{IsReadOnly,SetValue}`, `Patterns.Toggle`, `Patterns.ExpandCollapse.Pattern.{ExpandCollapseState,Expand,Collapse}`, `Patterns.SelectionItem.Pattern.Select`, `Patterns.ScrollItem.Pattern.ScrollIntoView`, `Patterns.Scroll.Pattern.Scroll(ScrollAmount,ScrollAmount)`, `Patterns.Window.Pattern.{CanMaximize,CanMinimize,SetWindowVisualState}`, and the `ExpandCollapseState`/`WindowVisualState`/`ScrollAmount` enums. Phase 1/2 confirmed FlaUI 5.0.0 needs `.And()` (no `&`) and `.Properties.X.ValueOrDefault`; pattern access is `el.Patterns.<Name>.IsSupported` / `.Pattern`. If any member differs, STOP and report `[expected] -> [actual]` rather than guessing.

- [ ] **Step 4: Run** the Interactor tests → PASS. `dotnet test --filter "Category!=Desktop"` → still green.
- [ ] **Step 5: Commit** `feat(interaction): stateless Interactor pattern executor`.

---

### Task 7: PerceptionManager.RunOnRefActionAsync — cross-STA resolve-and-act

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs`
- Test: `test/FlaUI.Mcp.Tests/Perception/ActionResolutionTests.cs`

- [ ] **Step 1: Write failing tests** (Desktop): a state-change through the action path, and the offscreen preflight.

```csharp
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class ActionResolutionTests
{
    private static string RefFor(string tree, string aid)
    {
        foreach (var line in tree.Split('\n'))
            if (line.Contains("aid=" + aid))
            { int lb = line.IndexOf('['), rb = line.IndexOf(']'); return line.Substring(lb + 1, rb - lb - 1); }
        throw new Xunit.Sdk.XunitException($"no ref for aid={aid}");
    }

    [Fact]
    public async Task Invoking_a_ref_on_the_action_STA_changes_UI_state()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry());
        var handle = await mgr.OpenByPidAsync(app.Process.Id);

        var snap = await perception.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });
        var okRef = RefFor(snap.Tree, "OkButton");

        await perception.RunOnRefActionAsync(handle, okRef, el => { Interactor.Invoke(el); return true; }, timeoutMs: 5000);

        var status = await perception.RunOnRefAsync(handle, RefFor(snap.Tree, "Status"), el => el.Name);
        Assert.Equal("clicked", status);
    }

    [Fact]
    public async Task An_offscreen_ref_is_rejected_before_invoking()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry());
        var handle = await mgr.OpenByPidAsync(app.Process.Id);

        var snap = await perception.SnapshotAsync(handle,
            new SnapshotOptions { FullProperties = true, IncludeOffscreen = true });
        var offRef = RefFor(snap.Tree, "OffscreenButton"); // IsOffscreen=true target

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            perception.RunOnRefActionAsync(handle, offRef, el => { Interactor.Invoke(el); return true; }, timeoutMs: 5000));
        Assert.Equal(ToolErrorCode.ElementNotActionable, ex.Code);
    }
}
```

- [ ] **Step 2: Run** `dotnet build test/FlaUI.Mcp.TestApp && dotnet test --filter "FullyQualifiedName~ActionResolutionTests"` → FAIL (method missing).

- [ ] **Step 3: Implement `RunOnRefActionAsync`** in `PerceptionManager`:

```csharp
    /// <summary>Resolve a ref and run a state-changing pattern action on a TRANSIENT action STA.
    /// The descriptor is read here (plain data, thread-safe); the element is re-resolved CACHE-FREE
    /// on the action STA against window+popup roots built by that STA's own automation — no query-STA
    /// COM object crosses apartments. An offscreen target is rejected before acting (offscreen Invoke
    /// can hang). On modal block past timeoutMs the call surfaces ACTION_BLOCKED_PENDING.</summary>
    public Task<T> RunOnRefActionAsync<T>(WindowHandle handle, string @ref, Func<AutomationElement, T> func, int timeoutMs)
    {
        var descriptor = _refs.Lookup(handle.Id, @ref).Descriptor; // REF_NOT_FOUND if absent (cheap, off-STA)
        return _windows.RunOnWindowActionAsync(handle, (win, desktop) =>
        {
            var roots = PopupFinder.SearchRoots(win, desktop);
            var el = _refs.ResolveDescriptor(descriptor, roots, @ref); // REF_STALE_UNRESOLVABLE if gone
            if (el.Properties.IsOffscreen.ValueOrDefault)
                throw new ToolException(ToolErrorCode.ElementNotActionable,
                    "Element is off-screen; cannot act on it reliably.", "desktop_scroll_into_view then retry");
            return func(el);
        }, timeoutMs);
    }
```

  > **NAME THE ORACLE:** `ActionResolutionTests.Invoking_a_ref_on_the_action_STA_changes_UI_state` (state changes) and `An_offscreen_ref_is_rejected_before_invoking` (preflight) define correct behavior. `Lookup` is `internal` in `RefRegistry`; `PerceptionManager` is in the same assembly, so the call compiles.

- [ ] **Step 4: Run** the action-resolution tests → PASS. `dotnet test --filter "Category!=Desktop"` → still green.
- [ ] **Step 5: Commit** `feat(perception): RunOnRefActionAsync cross-STA resolve-and-act`.

---

### Task 8: Read-only mode — ServerOptions + write gate + CLI flag

**Files:**
- Create: `src/FlaUI.Mcp.Server/ServerOptions.cs`
- Modify: `src/FlaUI.Mcp.Server/Tools/ToolResponse.cs`
- Modify: `src/FlaUI.Mcp.Server/Program.cs`
- Test: `test/FlaUI.Mcp.Tests/Server/ReadOnlyGateTests.cs`

- [ ] **Step 1: Write failing tests** (non-Desktop — pure seam):

```csharp
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

public class ReadOnlyGateTests
{
    [Fact]
    public async Task GuardWrite_blocks_when_read_only()
    {
        var json = await ToolResponse.GuardWrite(new ServerOptions(ReadOnly: true),
            () => Task.FromResult("UNREACHED"));
        Assert.Contains("WriteBlockedReadOnly", json);
    }

    [Fact]
    public async Task GuardWrite_runs_the_body_when_not_read_only()
    {
        var json = await ToolResponse.GuardWrite(new ServerOptions(ReadOnly: false),
            () => Task.FromResult("{\"ok\":true}"));
        Assert.Contains("ok", json);
    }

    [Theory]
    [InlineData(new[] { "--read-only-mode" }, true)]
    [InlineData(new string[0], false)]
    public void ServerOptions_parses_the_flag(string[] args, bool expected)
        => Assert.Equal(expected, ServerOptions.FromArgs(args).ReadOnly);
}
```

- [ ] **Step 2: Run** `dotnet test --filter "FullyQualifiedName~ReadOnlyGateTests"` → FAIL (types missing).

- [ ] **Step 3: Implement.** Create `ServerOptions.cs`:

```csharp
namespace FlaUI.Mcp.Server;

/// <summary>Process-wide server options parsed from argv. ReadOnly rejects every
/// non-read-only tool, independent of whether the MCP client honors destructiveHint.</summary>
public sealed record ServerOptions(bool ReadOnly)
{
    public static ServerOptions FromArgs(string[] args) =>
        new(ReadOnly: args.Contains("--read-only-mode"));
}
```

  Add `GuardWrite` to `ToolResponse` (alongside `Guard`; add `using FlaUI.Mcp.Server;`):

```csharp
    public static Task<string> GuardWrite(ServerOptions options, Func<Task<string>> body)
    {
        if (options.ReadOnly)
            return Guard(() => throw new ToolException(
                ToolErrorCode.WriteBlockedReadOnly,
                "Server is running in --read-only-mode; state-changing tools are disabled.",
                "restart the server without --read-only-mode to enable actions"));
        return Guard(body);
    }
```

  In `Program.cs`, register the options singleton (host branch only; installer verbs return earlier). After `var builder = Host.CreateApplicationBuilder(args);`:

```csharp
builder.Services.AddSingleton(ServerOptions.FromArgs(args));
```

- [ ] **Step 4: Run** the gate tests → PASS.
- [ ] **Step 5: Commit** `feat(server): --read-only-mode flag + GuardWrite write gate`.

---

### Task 9: TestApp interaction targets

Add deterministic targets the action tools/tests need. Existing targets reused: `OkButton` (Invoke→Status="clicked"), `Input` (Value), `ItemList`/`ItemA..C` (Select), `OffscreenButton` (offscreen), `MainWindow` (window_transform).

**Files:**
- Modify: `test/FlaUI.Mcp.TestApp/MainWindow.xaml`
- Modify: `test/FlaUI.Mcp.TestApp/MainWindow.xaml.cs`

- [ ] **Step 1: Add controls** to `MainWindow.xaml` inside the `StackPanel` (before the `Border`/`MenuTarget` block so layout stays clickable):

```xml
        <CheckBox x:Name="Check" AutomationProperties.AutomationId="Check" Content="toggle me" Margin="0,8,0,0"/>
        <Expander x:Name="Exp" AutomationProperties.AutomationId="Exp" Header="expander" Margin="0,4,0,0">
            <TextBlock Text="inner"/>
        </Expander>
        <Button x:Name="FocusReveal" AutomationProperties.AutomationId="FocusReveal"
                Content="focus reveals label" GotFocus="FocusReveal_GotFocus" Margin="0,4,0,0"/>
        <TextBlock x:Name="RevealedLabel" AutomationProperties.AutomationId="RevealedLabel"
                   Text="" Margin="0,2,0,0"/>
        <Button x:Name="ModalButton" AutomationProperties.AutomationId="ModalButton"
                Content="open modal" Click="ModalButton_Click" Margin="0,4,0,0"/>
```

- [ ] **Step 2: Add handlers** to `MainWindow.xaml.cs`:

```csharp
    private void FocusReveal_GotFocus(object sender, System.Windows.RoutedEventArgs e)
        => RevealedLabel.Text = "revealed";

    private void ModalButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dlg = new System.Windows.Window
        {
            Title = "Modal",
            Width = 200, Height = 120,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            Owner = this
        };
        var ok = new System.Windows.Controls.Button { Content = "OK" };
        System.Windows.Automation.AutomationProperties.SetAutomationId(ok, "ModalOk");
        ok.Click += (_, _) => dlg.Close();
        dlg.Content = ok;
        dlg.ShowDialog(); // BLOCKS the UI thread until closed — the deadlock-recovery target
    }
```

  > **STATE-VERIFICATION (Step 0):** open `MainWindow.xaml.cs` and confirm the existing handlers (`OkButton_Click` sets `Status.Text = "clicked"`, plus `RebuildItemsButton_Click`/`ClearItemsButton_Click`). If `OkButton_Click` sets a different string, the Task-6 `Invoke_clicks_a_button` assertion (`"clicked"`) must match what the handler actually sets — STOP and report the mismatch rather than changing the test blindly.

- [ ] **Step 3: Build** `dotnet build test/FlaUI.Mcp.TestApp` → succeeds.
- [ ] **Step 4: Commit** `test(testapp): add interaction targets (checkbox, expander, focus-reveal, modal)`.

---

### Task 10: InteractionTools — invoke + set_focus, with the deadlock-recovery proof

**Files:**
- Create: `src/FlaUI.Mcp.Server/Tools/InteractionTools.cs`
- Modify: `src/FlaUI.Mcp.Server/Program.cs` (DI registration)
- Test: `test/FlaUI.Mcp.Tests/Server/InteractionToolsTests.cs`

- [ ] **Step 1: Write failing tests** (Desktop). Include the critical deadlock-recovery proof:

```csharp
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

[Trait("Category", "Desktop")]
public class InteractionToolsTests
{
    private static InteractionTools Make(TestAppFixture app, out WindowHandle handle,
        out PerceptionManager p, out WindowManager m, out AutomationDispatcher d)
    {
        d = new AutomationDispatcher();
        m = new WindowManager(d);
        p = new PerceptionManager(m, new RefRegistry());
        var tools = new InteractionTools(p, m, new ServerOptions(ReadOnly: false));
        handle = m.OpenByPidAsync(app.Process.Id).GetAwaiter().GetResult();
        return tools;
    }

    private static string RefFor(string tree, string aid)
    {
        foreach (var line in tree.Split('\n'))
            if (line.Contains("aid=" + aid))
            { int lb = line.IndexOf('['), rb = line.IndexOf(']'); return line.Substring(lb + 1, rb - lb - 1); }
        throw new Xunit.Sdk.XunitException($"no ref for aid={aid}");
    }

    [Fact]
    public async Task Invoke_changes_state_and_set_focus_reveals_a_label()
    {
        using var app = new TestAppFixture();
        var tools = Make(app, out var handle, out var p, out var m, out var d); using (d) using (m)
        {
            var snap = await p.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });
            Assert.DoesNotContain("error", await tools.DesktopInvoke(handle.Id, RefFor(snap.Tree, "OkButton")));
            await tools.DesktopSetFocus(handle.Id, RefFor(snap.Tree, "FocusReveal"));
            var label = await p.RunOnRefAsync(handle, RefFor(snap.Tree, "RevealedLabel"), el => el.Name);
            Assert.Equal("revealed", label);
        }
    }

    // THE critical Task-C proof: a blocked action must NOT freeze the action context.
    [Fact]
    public async Task A_blocked_modal_action_does_not_deadlock_a_second_action()
    {
        using var app = new TestAppFixture();
        var tools = Make(app, out var handle, out var p, out var m, out var d); using (d) using (m)
        {
            var snap = await p.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });
            // Invoke ModalButton: ShowDialog blocks the UI thread -> ACTION_BLOCKED_PENDING.
            var blocked = await tools.DesktopInvoke(handle.Id, RefFor(snap.Tree, "ModalButton"), timeoutMs: 800);
            Assert.Contains("ActionBlockedPending", blocked);

            // The modal is now open. Snapshot it and click OK on a FRESH action thread.
            // A single-thread action queue would hang here forever.
            var modal = await m.OpenByTitleAsync("Modal");
            var modalSnap = await p.SnapshotAsync(modal, new SnapshotOptions { FullProperties = true });
            var dismiss = await tools.DesktopInvoke(modal.Id, RefFor(modalSnap.Tree, "ModalOk"), timeoutMs: 3000);
            Assert.DoesNotContain("error", dismiss);
        }
    }
}
```

- [ ] **Step 2: Run** `dotnet build test/FlaUI.Mcp.TestApp && dotnet test --filter "FullyQualifiedName~InteractionToolsTests"` → FAIL (no `InteractionTools`).

- [ ] **Step 3: Implement `InteractionTools`** (invoke + set_focus to start; later tasks extend this class). Action tools are NOT `ReadOnly` → SDK marks them destructive by default; route through `GuardWrite`:

```csharp
using System.ComponentModel;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Windows;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Tools;

[McpServerToolType]
public sealed class InteractionTools
{
    private const int DefaultActionTimeoutMs = 4000;
    private readonly PerceptionManager _perception;
    private readonly WindowManager _windows;
    private readonly ServerOptions _options;

    public InteractionTools(PerceptionManager perception, WindowManager windows, ServerOptions options)
    { _perception = perception; _windows = windows; _options = options; }

    private Task<string> Act(string window, string @ref,
        Action<FlaUI.Core.AutomationElements.AutomationElement> act, int timeoutMs)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            await _perception.RunOnRefActionAsync(new WindowHandle(window), @ref,
                el => { act(el); return true; }, timeoutMs);
            return ToolResponse.Ok(new { ok = true, pathUsed = "pattern" });
        });

    [McpServerTool(Destructive = true), Description("Invoke (activate) an element by ref via its UIA InvokePattern (e.g. click a button). If it opens a modal you get ActionBlockedPending — snapshot to see the dialog, then act on it.")]
    public Task<string> DesktopInvoke(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Element ref from a snapshot, e.g. e23.")] string @ref,
        [Description("Block timeout in ms (default 4000).")] int timeoutMs = DefaultActionTimeoutMs)
        => Act(window, @ref, Interactor.Invoke, timeoutMs);

    [McpServerTool(Destructive = true), Description("Set keyboard focus to an element by ref (UIA Focus). Often a prerequisite that reveals lazy-loaded content or enables downstream controls.")]
    public Task<string> DesktopSetFocus(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Element ref from a snapshot, e.g. e23.")] string @ref,
        [Description("Block timeout in ms (default 4000).")] int timeoutMs = DefaultActionTimeoutMs)
        => Act(window, @ref, Interactor.SetFocus, timeoutMs);
}
```

  Register in `Program.cs` (host branch): `builder.Services.AddSingleton<InteractionTools>();` (after `SnapshotTools`).

- [ ] **Step 4: Run** the InteractionTools tests → PASS (especially the deadlock-recovery proof). `dotnet test --filter "Category!=Desktop"` → still green.
- [ ] **Step 5: Commit** `feat(tools): desktop_invoke + desktop_set_focus + deadlock-recovery proof`.

---

### Task 11: InteractionTools — set_value, toggle, expand, select

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Tools/InteractionTools.cs`
- Test: `test/FlaUI.Mcp.Tests/Server/InteractionToolsTests.cs`

- [ ] **Step 1: Add a failing test** to `InteractionToolsTests`:

```csharp
    [Fact]
    public async Task SetValue_toggle_expand_select_round_trip()
    {
        using var app = new TestAppFixture();
        var tools = Make(app, out var handle, out var p, out var m, out var d); using (d) using (m)
        {
            var snap = await p.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });

            Assert.DoesNotContain("error", await tools.DesktopSetValue(handle.Id, RefFor(snap.Tree, "Input"), "typed"));
            Assert.Equal("typed", await p.RunOnRefAsync(handle, RefFor(snap.Tree, "Input"), el => el.AsTextBox().Text));

            Assert.DoesNotContain("error", await tools.DesktopToggle(handle.Id, RefFor(snap.Tree, "Check")));
            Assert.DoesNotContain("error", await tools.DesktopExpand(handle.Id, RefFor(snap.Tree, "Exp")));
            Assert.DoesNotContain("error", await tools.DesktopSelect(handle.Id, RefFor(snap.Tree, "ItemB")));
        }
    }
```

- [ ] **Step 2: Run** → FAIL (methods missing).
- [ ] **Step 3: Add the four tools** to `InteractionTools` (same `Act` helper; `set_value` takes a `value` arg):

```csharp
    [McpServerTool(Destructive = true), Description("Set an element's value by ref via UIA ValuePattern (fast text/value set; focuses first). ElementNotActionable if read-only, PatternUnsupported if no ValuePattern (no synthetic typing this phase).")]
    public Task<string> DesktopSetValue(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Element ref, e.g. e23.")] string @ref,
        [Description("The value to set.")] string value,
        [Description("Block timeout in ms (default 4000).")] int timeoutMs = DefaultActionTimeoutMs)
        => Act(window, @ref, el => Interactor.SetValue(el, value), timeoutMs);

    [McpServerTool(Destructive = true), Description("Toggle an element by ref via UIA TogglePattern (checkbox/switch).")]
    public Task<string> DesktopToggle(
        [Description("Window handle.")] string window, [Description("Element ref.")] string @ref,
        [Description("Block timeout in ms (default 4000).")] int timeoutMs = DefaultActionTimeoutMs)
        => Act(window, @ref, Interactor.Toggle, timeoutMs);

    [McpServerTool(Destructive = true), Description("Expand or collapse an element by ref via UIA ExpandCollapsePattern (tree node / expander / combo).")]
    public Task<string> DesktopExpand(
        [Description("Window handle.")] string window, [Description("Element ref.")] string @ref,
        [Description("Block timeout in ms (default 4000).")] int timeoutMs = DefaultActionTimeoutMs)
        => Act(window, @ref, Interactor.Expand, timeoutMs);

    [McpServerTool(Destructive = true), Description("Select an element by ref via UIA SelectionItemPattern (list item / radio / tab). Replaces the current selection (no multi-select this phase).")]
    public Task<string> DesktopSelect(
        [Description("Window handle.")] string window, [Description("Element ref.")] string @ref,
        [Description("Block timeout in ms (default 4000).")] int timeoutMs = DefaultActionTimeoutMs)
        => Act(window, @ref, Interactor.Select, timeoutMs);
```

- [ ] **Step 4: Run** → PASS; `dotnet test --filter "Category!=Desktop"` still green.
- [ ] **Step 5: Commit** `feat(tools): desktop_set_value/toggle/expand/select`.

---

### Task 12: InteractionTools — scroll + scroll_into_view

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Tools/InteractionTools.cs`
- Test: `test/FlaUI.Mcp.Tests/Server/InteractionToolsTests.cs`

- [ ] **Step 1: Add a failing test:**

```csharp
    [Fact]
    public async Task ScrollIntoView_and_scroll_do_not_error()
    {
        using var app = new TestAppFixture();
        var tools = Make(app, out var handle, out var p, out var m, out var d); using (d) using (m)
        {
            var snap = await p.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });
            Assert.DoesNotContain("error", await tools.DesktopScrollIntoView(handle.Id, RefFor(snap.Tree, "ItemC")));
            Assert.DoesNotContain("error", await tools.DesktopScroll(handle.Id, RefFor(snap.Tree, "ItemList"), "down", 1));
        }
    }
```

- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Add the two tools:**

```csharp
    [McpServerTool(Destructive = true), Description("Scroll an element into view by ref via UIA ScrollItemPattern (realize an item in a scrollable container, then re-snapshot).")]
    public Task<string> DesktopScrollIntoView(
        [Description("Window handle.")] string window, [Description("Element ref.")] string @ref,
        [Description("Block timeout in ms (default 4000).")] int timeoutMs = DefaultActionTimeoutMs)
        => Act(window, @ref, Interactor.ScrollIntoView, timeoutMs);

    [McpServerTool(Destructive = true), Description("Scroll a container by ref via UIA ScrollPattern. direction = up|down|left|right; amount = number of small scroll steps (1..50). Realize virtualized items, then re-snapshot.")]
    public Task<string> DesktopScroll(
        [Description("Window handle.")] string window, [Description("Container element ref.")] string @ref,
        [Description("up|down|left|right")] string direction,
        [Description("Number of small scroll steps (default 1).")] double amount = 1,
        [Description("Block timeout in ms (default 4000).")] int timeoutMs = DefaultActionTimeoutMs)
        => Act(window, @ref, el => Interactor.Scroll(el, direction, amount), timeoutMs);
```

- [ ] **Step 4: Run** → PASS; non-Desktop still green.
- [ ] **Step 5: Commit** `feat(tools): desktop_scroll + desktop_scroll_into_view`.

---

### Task 13: window_transform (window-scoped, action STA)

`window_transform` does not use a ref — it acts on the window handle via `RunOnWindowActionAsync`.

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Tools/InteractionTools.cs`
- Test: `test/FlaUI.Mcp.Tests/Server/InteractionToolsTests.cs`

- [ ] **Step 1: Add a failing test:**

```csharp
    [Fact]
    public async Task WindowTransform_maximize_then_restore()
    {
        using var app = new TestAppFixture();
        var tools = Make(app, out var handle, out var p, out var m, out var d); using (d) using (m)
        {
            Assert.DoesNotContain("error", await tools.DesktopWindowTransform(handle.Id, "maximize"));
            Assert.DoesNotContain("error", await tools.DesktopWindowTransform(handle.Id, "restore"));
        }
    }
```

- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Add the tool** (uses `RunOnWindowActionAsync`; `win` arrives as `AutomationElement`, `.AsWindow()` for the pattern):

```csharp
    [McpServerTool(Destructive = true), Description("Transform a window by handle via UIA Window/Transform patterns. action = maximize|minimize|restore. (Close is desktop_close_window; move/resize land later.)")]
    public Task<string> DesktopWindowTransform(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("maximize|minimize|restore")] string action,
        [Description("Block timeout in ms (default 4000).")] int timeoutMs = DefaultActionTimeoutMs)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            await _windows.RunOnWindowActionAsync(new WindowHandle(window),
                (win, _) => { Interactor.WindowTransform(win.AsWindow(), action); return true; }, timeoutMs);
            return ToolResponse.Ok(new { ok = true, action });
        });
```

- [ ] **Step 4: Run** → PASS; non-Desktop still green.
- [ ] **Step 5: Commit** `feat(tools): desktop_window_transform`.

---

### Task 14: Read-only enforcement coverage + full-suite verification

**Files:**
- Test: `test/FlaUI.Mcp.Tests/Server/InteractionToolsTests.cs`
- Verify only (no new product code unless a gap is found)

- [ ] **Step 1: Add a read-only enforcement test** (non-Desktop — a ReadOnly `ServerOptions` blocks before any UIA work, so `null!` deps are never touched):

```csharp
    [Fact]
    public async Task Read_only_mode_blocks_every_action_tool()
    {
        var tools = new InteractionTools(perception: null!, windows: null!, new ServerOptions(ReadOnly: true));
        Assert.Contains("WriteBlockedReadOnly", await tools.DesktopInvoke("w1", "e1"));
        Assert.Contains("WriteBlockedReadOnly", await tools.DesktopSetValue("w1", "e1", "x"));
        Assert.Contains("WriteBlockedReadOnly", await tools.DesktopToggle("w1", "e1"));
        Assert.Contains("WriteBlockedReadOnly", await tools.DesktopExpand("w1", "e1"));
        Assert.Contains("WriteBlockedReadOnly", await tools.DesktopSelect("w1", "e1"));
        Assert.Contains("WriteBlockedReadOnly", await tools.DesktopScrollIntoView("w1", "e1"));
        Assert.Contains("WriteBlockedReadOnly", await tools.DesktopScroll("w1", "e1", "down"));
        Assert.Contains("WriteBlockedReadOnly", await tools.DesktopSetFocus("w1", "e1"));
        Assert.Contains("WriteBlockedReadOnly", await tools.DesktopWindowTransform("w1", "maximize"));
    }
```

  This is also the **type-consistency oracle**: every tool method must route through `GuardWrite` before resolving anything (the `null!` deps are never touched when ReadOnly).

- [ ] **Step 2: Run** `dotnet test --filter "FullyQualifiedName~Read_only_mode_blocks"` → PASS. If any tool reaches a `null` deref instead of returning `WriteBlockedReadOnly`, that tool skipped `GuardWrite` — fix it to route through `GuardWrite` first.

- [ ] **Step 3: Full-suite verification.**
  - `dotnet build test/FlaUI.Mcp.TestApp`
  - `dotnet test --filter "Category!=Desktop"` → all green (headless CI subset).
  - `dotnet test` (full, on an interactive desktop) → all green (Desktop subset included).
  - Smoke the host: build the server and confirm `--read-only-mode` is accepted and the process starts as a stdio host without crashing.

- [ ] **Step 4: Commit** `test(tools): read-only enforcement coverage + full-suite green`.

---

## Execution / wrap

- 3a tools surface (9): `desktop_invoke`, `desktop_set_value`, `desktop_toggle`, `desktop_expand`, `desktop_select`, `desktop_scroll`, `desktop_scroll_into_view`, `desktop_set_focus`, `desktop_window_transform` — all `destructiveHint:true`, all gated by `--read-only-mode`.
- After Task 14, run **superpowers:finishing-a-development-branch**: merge `phase-3-interaction` → master `--no-ff`, bump `<Version>` 0.2.0→**0.3.0** in `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj` and `#define AppVersion` in `installer/flaui-mcp.iss`, update README (new interaction tools + `--read-only-mode` + safety note) and ROADMAP (mark 3a shipped), tag **`v0.3.0`**, push → release CI publishes; verify assets land. Live install + `/mcp` reconnect smoke is the user's.
- **Per AGY-AFTER:** route this finished plan to agy (web channel) before execution; fold the verdict.

## Self-review notes (author)

- **Spec coverage:** Task A→T3, Task B→T4/T5, Task C→T2, Interactor→T6, RunOnRefActionAsync→T7, 9 tools→T10–T13, `--read-only-mode`+`WriteBlockedReadOnly`→T8/T14, `TooManyPendingActions`+cap→T2, abandonment-safety (TrySet*/catch-all/finally/observe)→T2, popup-root parity→T4+T7, offscreen preflight→T6/T7, deadlock-recovery test→T10, destructiveHint→T10–T13 (`Destructive=true`). All spec requirements map to a task.
- **Residual / verify-at-execution (empirical, not fabricated):** exact FlaUI 5.0.0 pattern member shapes (Task 6 STATE-VERIFICATION gate) and the `McpServerTool(Destructive = true)` property name (Phase-2 reflection confirmed the attribute exposes Name/Title/Destructive/Idempotent/OpenWorld/ReadOnly/…). If `Destructive` is spelled differently, the implementer reports rather than guesses.
- **Out of scope confirmed:** no 3b tools, no synthetic input, no `VisionCapture`.
