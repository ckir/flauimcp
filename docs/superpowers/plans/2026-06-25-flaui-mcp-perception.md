# FlaUI.Mcp Phase 2 — Perception Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the agent a token-efficient way to *perceive* a window — a `desktop_snapshot` tool that walks a window's UIA tree into an indented, ref-tagged text snapshot, backed by an option-C `RefRegistry` whose refs survive UIA tree mutation.

**Architecture:** A stateless `SnapshotEngine` walks an `AutomationElement` subtree (plus grafted owner-process popups) and registers each surfaced element into a session-singleton `RefRegistry` (option-C descriptors: RuntimeId + ControlType + AutomationId/Name + nearest stable ancestor + index path). A thin `PerceptionManager` Core façade orchestrates this on the existing query STA via one new `WindowManager` primitive (`RunWithWindowAndDesktopAsync`), paralleling how `WindowTools` calls `WindowManager` today. A new `SnapshotTools` MCP adapter exposes `desktop_snapshot`. Refs are namespaced per window handle and per-snapshot scoped (a new snapshot supersedes a window's old refs; a held stale ref returns `REF_NOT_FOUND`); descriptor re-resolution recovers a ref after the tree mutates (`AutomationId` under the nearest stable ancestor), the core option-C guarantee.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`), FlaUI.UIA3 5.0.0, ModelContextProtocol 1.4.0, xUnit. Builds on Phase 1 Core (`AutomationDispatcher`, `WindowManager`, `ToolException`/`ToolErrorCode`, `WindowHandle`).

**Scope (locked — strict phasing):** SnapshotEngine + RefRegistry (incl. `Resolve`) + `desktop_snapshot` with popup-grafting, `interactiveOnly`, `fullProperties`, always-on bounding rects, per-snapshot scoping. **Out of scope (later phases):** `desktop_snapshot_diff`/`_stats`/`_global` (Phase 5), the vision/screenshot path and DPI/screenshot-pixel coordinate mapping (Phase 4), the `Interactor` and synthetic input (Phase 3). Bounding rects here are **UIA `BoundingRectangle` screen coordinates** — the screenshot-pixel-space mapping is a Phase 4 concern; emit raw UIA rects for now.

---

## FlaUI 5.0.0 API verification gate (applies to EVERY UIA task)

The code blocks below name FlaUI members. **Already proven to compile in this repo** (Phase 1 `WindowManager.cs`): `automation.GetDesktop()`, `element.FindAllChildren()`, `child.AsWindow()`, `element.Properties.<Prop>.ValueOrDefault` (e.g. `ProcessId`), `element.BoundingRectangle`, `window.Focus()/SetForeground()/Close()/Title`.

**Assumed but NOT yet used in-repo — VERIFY BY COMPILE before trusting, and if a member differs STOP and report `[assumed] -> [actual] because <reason>` rather than silently adapting:** `element.ControlType` (`FlaUI.Core.Definitions.ControlType`), `element.Name`, `element.AutomationId`, `element.IsEnabled`, `element.HelpText`, `element.Properties.RuntimeId.ValueOrDefault` (`int[]`), `element.Properties.ClassName.ValueOrDefault`, `element.Properties.IsKeyboardFocusable.ValueOrDefault`, `element.Patterns.<Name>.IsSupported` (bool) for Invoke/Value/Toggle/ExpandCollapse/Selection/SelectionItem/ScrollItem/Scroll/Grid/Text/Window/Transform, `element.FindFirstDescendant(Func<ConditionFactory, ConditionBase>)` with `cf.ByAutomationId(string)`, `element.RightClick()`, `element.AsButton().Invoke()`. If `element.ControlType` is not a direct property, use `element.Properties.ControlType.ValueOrDefault`. **"It compiles" is the gate; do not invent a member that doesn't exist — report the divergence.**

Wrap every per-element UIA property read in a try/catch that skips the element on a COM fault (transient elements vanish mid-walk); a single bad element must never abort the whole snapshot.

## File Structure

**Core (`src/FlaUI.Mcp.Core/Perception/`)**
- `ElementDescriptor.cs` — option-C descriptor value type.
- `SnapshotOptions.cs` / `SnapshotResult.cs` — request knobs / result.
- `RefRegistry.cs` — session-singleton ref store: per-window namespacing, per-snapshot scoping, option-C `Resolve`.
- `SnapshotEngine.cs` — stateless static tree-walker + line formatter + popup grafting.
- `PerceptionManager.cs` — Core façade (ctor `WindowManager` + `RefRegistry`): `SnapshotAsync`, `RunOnRefAsync`, owner-popup discovery.

**Core (`src/FlaUI.Mcp.Core/Windows/WindowManager.cs`)** — add one primitive: `RunWithWindowAndDesktopAsync<T>`.

**Server (`src/FlaUI.Mcp.Server/`)**
- `Tools/ToolResponse.cs` — extracted `Ok`/`Guard` helper (DRY; `WindowTools` refactored onto it).
- `Tools/SnapshotTools.cs` — `desktop_snapshot` MCP adapter.
- `Program.cs` — register `RefRegistry`, `PerceptionManager`, `SnapshotTools` singletons.

**TestApp (`test/FlaUI.Mcp.TestApp/MainWindow.xaml(.cs)`)** — add a mutating ListBox + Add/Clear buttons (ref re-resolution target) and a Border with a ContextMenu (popup-grafting target).

**Tests (`test/FlaUI.Mcp.Tests/Perception/` and `/Server/`)** — value-type units (non-UIA), plus `[Trait("Category","Desktop")]` UIA tests so the CI filter `Category!=Desktop` stays green.

> **Branch:** create `phase-2-perception` off `master` before Task 1 (do NOT implement on `master`). `RefRegistry` is a process-wide singleton in this phase; per-connection isolation is deferred to Phase 6 (HTTP transport).

---

### Task 1: Perception value types

**Files:**
- Create: `src/FlaUI.Mcp.Core/Perception/ElementDescriptor.cs`
- Create: `src/FlaUI.Mcp.Core/Perception/SnapshotOptions.cs`
- Create: `src/FlaUI.Mcp.Core/Perception/SnapshotResult.cs`
- Test: `test/FlaUI.Mcp.Tests/Perception/PerceptionValueTypesTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class PerceptionValueTypesTests
{
    [Fact]
    public void SnapshotOptions_has_perception_friendly_defaults()
    {
        var o = new SnapshotOptions();
        Assert.True(o.InteractiveOnly);
        Assert.False(o.FullProperties);
        Assert.Equal(40, o.MaxDepth);
        Assert.Null(o.RootRef);
    }

    [Fact]
    public void ElementDescriptor_carries_its_option_c_keys()
    {
        var d = new ElementDescriptor(
            RuntimeId: new[] { 7, 42 },
            ControlType: FlaUI.Core.Definitions.ControlType.Button,
            AutomationId: "OkButton",
            Name: "OK",
            AncestorAutomationId: "MainWindow",
            IndexPath: new[] { 0, 1 });
        Assert.Equal("OkButton", d.AutomationId);
        Assert.Equal("MainWindow", d.AncestorAutomationId);
        Assert.Equal(new[] { 0, 1 }, d.IndexPath);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~PerceptionValueTypesTests"`
Expected: FAIL — `ElementDescriptor`/`SnapshotOptions` do not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

`ElementDescriptor.cs`:
```csharp
using FlaUI.Core.Definitions;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>Option-C element descriptor: re-resolvable across UIA tree mutation.
/// RuntimeId is an ephemeral fast-path only; AutomationId under the nearest stable
/// ancestor is the primary re-resolution key.</summary>
public sealed record ElementDescriptor(
    IReadOnlyList<int> RuntimeId,
    ControlType ControlType,
    string AutomationId,
    string Name,
    string? AncestorAutomationId,
    IReadOnlyList<int> IndexPath);
```

`SnapshotOptions.cs`:
```csharp
namespace FlaUI.Mcp.Core.Perception;

public sealed record SnapshotOptions
{
    /// <summary>Optional ref (from a prior snapshot of the same window) to root the walk at.</summary>
    public string? RootRef { get; init; }
    public int MaxDepth { get; init; } = 40;
    /// <summary>Prune non-interactive container/decoration noise (Playwright-style). Default true.</summary>
    public bool InteractiveOnly { get; init; } = true;
    /// <summary>Append AutomationId/HelpText to each line. Default false.</summary>
    public bool FullProperties { get; init; } = false;
}
```

`SnapshotResult.cs`:
```csharp
namespace FlaUI.Mcp.Core.Perception;

/// <summary>A serialized snapshot. SnapshotId is stable wire surface for the future
/// desktop_snapshot_diff (Phase 5); nothing consumes it yet.</summary>
public sealed record SnapshotResult(string SnapshotId, string Tree, int NodeCount);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~PerceptionValueTypesTests"`
Expected: PASS — 2 tests.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/ElementDescriptor.cs src/FlaUI.Mcp.Core/Perception/SnapshotOptions.cs src/FlaUI.Mcp.Core/Perception/SnapshotResult.cs test/FlaUI.Mcp.Tests/Perception/PerceptionValueTypesTests.cs
git commit -m "feat(perception): ElementDescriptor + SnapshotOptions/Result value types"
```

---

### Task 2: RefRegistry — namespacing, per-snapshot scoping, REF_NOT_FOUND

Pure in-memory bookkeeping only. `Resolve`'s UIA element-search lands in Task 6 (with its Desktop tests + TestApp target). This task gives `SnapshotEngine` (Task 4) the `BeginSnapshot`/`Register` it needs.

**Files:**
- Create: `src/FlaUI.Mcp.Core/Perception/RefRegistry.cs`
- Test: `test/FlaUI.Mcp.Tests/Perception/RefRegistryTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using FlaUI.Core.Definitions;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class RefRegistryTests
{
    private static ElementDescriptor Desc(string aid) =>
        new(Array.Empty<int>(), ControlType.Button, aid, aid, null, Array.Empty<int>());

    [Fact]
    public void Register_assigns_sequential_refs_then_BeginSnapshot_keeps_the_counter_climbing()
    {
        var r = new RefRegistry();
        r.BeginSnapshot("w1");
        Assert.Equal("e1", r.Register("w1", Desc("a"), cached: null));
        Assert.Equal("e2", r.Register("w1", Desc("b"), cached: null));
        // New snapshot of w1 clears its refs but the counter does NOT reset, so
        // a stale held ref ("e1") can never silently alias a new element.
        r.BeginSnapshot("w1");
        Assert.Equal("e3", r.Register("w1", Desc("c"), cached: null));
    }

    [Fact]
    public void Refs_are_namespaced_per_window_handle()
    {
        var r = new RefRegistry();
        r.BeginSnapshot("w1");
        r.BeginSnapshot("w2");
        Assert.Equal("e1", r.Register("w1", Desc("a"), cached: null));
        Assert.Equal("e1", r.Register("w2", Desc("a"), cached: null)); // independent counter
    }

    [Fact]
    public void A_ref_from_a_superseded_snapshot_is_REF_NOT_FOUND()
    {
        var r = new RefRegistry();
        r.BeginSnapshot("w1");
        var stale = r.Register("w1", Desc("a"), cached: null); // "e1"
        r.BeginSnapshot("w1"); // supersedes — clears w1's map
        var ex = Assert.Throws<ToolException>(() => r.Lookup("w1", stale));
        Assert.Equal(ToolErrorCode.RefNotFound, ex.Code);
        Assert.False(string.IsNullOrEmpty(ex.SuggestedRecovery));
    }

    [Fact]
    public void Lookup_of_unknown_window_is_REF_NOT_FOUND()
    {
        var r = new RefRegistry();
        var ex = Assert.Throws<ToolException>(() => r.Lookup("w9", "e1"));
        Assert.Equal(ToolErrorCode.RefNotFound, ex.Code);
    }

    [Fact]
    public void BeginSnapshot_returns_a_window_scoped_snapshot_id()
    {
        var r = new RefRegistry();
        var id1 = r.BeginSnapshot("w1");
        var id2 = r.BeginSnapshot("w1");
        Assert.StartsWith("w1:", id1);
        Assert.NotEqual(id1, id2);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~RefRegistryTests"`
Expected: FAIL — `RefRegistry` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>Option-C ref store. Refs (e1, e2, …) are namespaced per window handle and
/// per-snapshot scoped: BeginSnapshot clears a window's refs but never resets its
/// counter, so a stale held ref can never silently alias a new element — it misses
/// with REF_NOT_FOUND. Accessed only from the dispatcher's single query STA, but
/// guarded for safety. Process-wide singleton this phase (per-connection in Phase 6).</summary>
public sealed class RefRegistry
{
    private sealed record Entry(ElementDescriptor Descriptor, AutomationElement? Cached);

    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, Entry>> _byWindow = new();
    private readonly Dictionary<string, int> _counter = new();
    private readonly Dictionary<string, int> _snapshotSeq = new();

    /// <summary>Clear a window's refs (supersede prior snapshot) and return a new snapshot id.</summary>
    public string BeginSnapshot(string windowId)
    {
        lock (_gate)
        {
            _byWindow[windowId] = new Dictionary<string, Entry>();
            int seq = _snapshotSeq.TryGetValue(windowId, out var s) ? s + 1 : 1;
            _snapshotSeq[windowId] = seq;
            return $"{windowId}:{seq}";
        }
    }

    /// <summary>Register an element; returns its fresh ref (e.g. "e23").</summary>
    public string Register(string windowId, ElementDescriptor descriptor, AutomationElement? cached)
    {
        lock (_gate)
        {
            if (!_byWindow.TryGetValue(windowId, out var map))
                _byWindow[windowId] = map = new Dictionary<string, Entry>();
            int n = _counter.TryGetValue(windowId, out var c) ? c + 1 : 1;
            _counter[windowId] = n;
            var @ref = $"e{n}";
            map[@ref] = new Entry(descriptor, cached);
            return @ref;
        }
    }

    /// <summary>Throw REF_NOT_FOUND if the ref isn't live for this window; else return its entry.
    /// (Element re-resolution is added in Task 6.)</summary>
    internal Entry Lookup(string windowId, string @ref)
    {
        lock (_gate)
        {
            if (_byWindow.TryGetValue(windowId, out var map) && map.TryGetValue(@ref, out var e))
                return e;
            throw new ToolException(ToolErrorCode.RefNotFound,
                $"Ref '{@ref}' is not in the current snapshot of window '{windowId}'.",
                "take a fresh desktop_snapshot and use a ref from it");
        }
    }
}
```

> Note: `Lookup` returns the private `Entry` type, so it is `internal` and reached by the test via `InternalsVisibleTo`. Confirm `test/FlaUI.Mcp.Tests` already sees Core internals; if not, add `[assembly: InternalsVisibleTo("FlaUI.Mcp.Tests")]` to a Core file (e.g. a new `src/FlaUI.Mcp.Core/Properties/AssemblyInfo.cs`). **STATE-VERIFY (Step 0):** grep the repo for an existing `InternalsVisibleTo` before adding one; Phase 1 used only public surfaces, so it is likely absent.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~RefRegistryTests"`
Expected: PASS — 5 tests.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/RefRegistry.cs test/FlaUI.Mcp.Tests/Perception/RefRegistryTests.cs
# plus AssemblyInfo.cs if you added InternalsVisibleTo
git commit -m "feat(perception): RefRegistry namespacing + per-snapshot scoping + REF_NOT_FOUND"
```

---

### Task 3: WindowManager.RunWithWindowAndDesktopAsync primitive

The single new seam to UIA: run a callback on the query STA with the resolved `Window` **and** the Desktop element (needed later for owner-process popups). Reuses the existing stale-handle check.

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Windows/WindowManager.cs` (add one method after `RunOnWindowAsync`, around line 63)
- Test: `test/FlaUI.Mcp.Tests/Perception/PerceptionPrimitiveTests.cs`

- [ ] **Step 0 (STATE-VERIFY):** Open `WindowManager.cs`; confirm `RunOnWindowAsync<T>` (lines ~56–63), the private `_dispatcher`, `_automation`, and `_handles` fields, and `_automation.GetDesktop()` usage (line ~30) exist as cited. If they differ, STOP and report `STATE_MISMATCH`.

- [ ] **Step 1: Write the failing test**

```csharp
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class PerceptionPrimitiveTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public PerceptionPrimitiveTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task RunWithWindowAndDesktop_hands_back_the_window_and_a_desktop()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var (title, desktopHasChildren) = await mgr.RunWithWindowAndDesktopAsync(
            handle, (win, desktop) => (win.Title, desktop.FindAllChildren().Length > 0));

        Assert.Contains("TestApp", title);
        Assert.True(desktopHasChildren);
    }

    [Fact]
    public async Task RunWithWindowAndDesktop_on_a_stale_handle_throws_WindowHandleStale()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            mgr.RunWithWindowAndDesktopAsync(new WindowHandle("w999"), (w, d) => true));
        Assert.Equal(ToolErrorCode.WindowHandleStale, ex.Code);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~PerceptionPrimitiveTests"`
Expected: FAIL — `RunWithWindowAndDesktopAsync` does not exist.

- [ ] **Step 3: Write minimal implementation** — insert into `WindowManager` (after `RunOnWindowAsync`):

```csharp
    /// <summary>Run a read callback on the query STA with the resolved window AND the Desktop
    /// element (the snapshot engine needs the Desktop to graft owner-process popups, which are
    /// children of the Desktop — not the target window). Reuses the stale-handle guard.</summary>
    public Task<T> RunWithWindowAndDesktopAsync<T>(WindowHandle handle, Func<Window, AutomationElement, T> func) =>
        _dispatcher.RunQueryAsync(() =>
        {
            if (!_handles.TryGetValue(handle.Id, out var w))
                throw new ToolException(ToolErrorCode.WindowHandleStale,
                    $"Handle {handle.Id} is no longer valid.", "re-list windows and re-open");
            return func(w, _automation.GetDesktop());
        });
```

> `Func<Window, AutomationElement, T>` needs `using FlaUI.Core.AutomationElements;` — already imported in `WindowManager.cs` (line 3). **SHAPE-DIVERGENCE STOP:** if `_automation.GetDesktop()` returns a type that isn't assignable to `AutomationElement`, report it rather than changing the signature.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~PerceptionPrimitiveTests"`
Expected: PASS — 2 tests (Desktop session required).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Windows/WindowManager.cs test/FlaUI.Mcp.Tests/Perception/PerceptionPrimitiveTests.cs
git commit -m "feat(perception): WindowManager.RunWithWindowAndDesktopAsync query-STA primitive"
```

---

### Task 4: SnapshotEngine — walk, interactiveOnly prune, bounds, patterns

Stateless walker. Registers each surfaced element into `RefRegistry` and returns the indented tree + node count. Popup grafting param is present but exercised empty here (filled in Task 7).

**Files:**
- Create: `src/FlaUI.Mcp.Core/Perception/SnapshotEngine.cs`
- Test: `test/FlaUI.Mcp.Tests/Perception/SnapshotEngineTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class SnapshotEngineTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public SnapshotEngineTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Walk_surfaces_interactive_controls_with_refs_bounds_and_patterns()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var refs = new RefRegistry();
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var (tree, count) = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            refs.BeginSnapshot(handle.Id);
            return SnapshotEngine.Walk(win, Array.Empty<FlaUI.Core.AutomationElements.AutomationElement>(),
                new SnapshotOptions(), refs, handle.Id);
        });

        Assert.True(count > 0);
        Assert.Contains("[e", tree);              // refs assigned
        Assert.Contains("Button", tree);          // OkButton surfaced
        Assert.Contains("\"OK\"", tree);          // its name
        Assert.Contains("@{", tree);              // bounding rect present on every line
        Assert.Contains("Invoke", tree);          // button advertises InvokePattern
        // interactiveOnly default prunes the unnamed StackPanel container:
        Assert.DoesNotContain("Pane \"\"", tree);
    }

    [Fact]
    public async Task FullProperties_appends_automation_ids()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var refs = new RefRegistry();
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var (tree, _) = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            refs.BeginSnapshot(handle.Id);
            return SnapshotEngine.Walk(win, Array.Empty<FlaUI.Core.AutomationElements.AutomationElement>(),
                new SnapshotOptions { FullProperties = true }, refs, handle.Id);
        });

        Assert.Contains("aid=OkButton", tree);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~SnapshotEngineTests"`
Expected: FAIL — `SnapshotEngine` does not exist.

- [ ] **Step 3: Write minimal implementation** (VERIFY every FlaUI member per the gate above):

```csharp
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>Walks a UIA subtree into an indented, ref-tagged text snapshot. Stateless;
/// registers each surfaced element into the supplied RefRegistry. Runs on the caller's
/// thread — callers MUST invoke it on the query STA (see WindowManager primitive).</summary>
public static class SnapshotEngine
{
    // Curated "meaningful" roles surfaced under interactiveOnly. Containers/decoration are
    // pruned from OUTPUT but still recursed THROUGH so their interactive descendants appear.
    private static readonly HashSet<ControlType> InteractiveTypes = new()
    {
        ControlType.Button, ControlType.CheckBox, ControlType.ComboBox, ControlType.Edit,
        ControlType.Hyperlink, ControlType.ListItem, ControlType.MenuItem, ControlType.RadioButton,
        ControlType.Slider, ControlType.Spinner, ControlType.SplitButton, ControlType.Tab,
        ControlType.TabItem, ControlType.TreeItem, ControlType.Document, ControlType.List,
        ControlType.Menu, ControlType.Tree, ControlType.DataGrid, ControlType.Table,
    };

    public static (string Tree, int NodeCount) Walk(
        AutomationElement root,
        IReadOnlyList<AutomationElement> popupRoots,
        SnapshotOptions options,
        RefRegistry refs,
        string windowId)
    {
        var sb = new StringBuilder();
        int count = 0;
        Visit(root, depth: 0, indexPath: Array.Empty<int>(), ancestorAid: null, indent: "");

        if (popupRoots.Count > 0)
        {
            sb.AppendLine("[Popups]");
            for (int i = 0; i < popupRoots.Count; i++)
                Visit(popupRoots[i], depth: 0, indexPath: new[] { -1 - i }, ancestorAid: null, indent: "  ");
        }
        return (sb.ToString(), count);

        void Visit(AutomationElement el, int depth, int[] indexPath, string? ancestorAid, string indent)
        {
            string aid = Safe(() => el.AutomationId, "");
            ControlType ct = Safe(() => el.ControlType, ControlType.Custom);
            string name = Safe(() => el.Name, "");

            bool include = depth == 0 || !options.InteractiveOnly || IsInteresting(el, ct, name);
            string childIndent = indent;
            if (include)
            {
                var descriptor = new ElementDescriptor(
                    RuntimeId: Safe(() => (IReadOnlyList<int>)(el.Properties.RuntimeId.ValueOrDefault ?? Array.Empty<int>()), Array.Empty<int>()),
                    ControlType: ct, AutomationId: aid, Name: name,
                    AncestorAutomationId: ancestorAid, IndexPath: indexPath);
                var @ref = refs.Register(windowId, descriptor, el);
                sb.AppendLine(FormatLine(indent, @ref, el, ct, name, aid, options));
                count++;
                childIndent = indent + "  ";
            }

            if (depth >= options.MaxDepth) return;
            var nextAncestor = string.IsNullOrEmpty(aid) ? ancestorAid : aid;
            AutomationElement[] children = Safe(() => el.FindAllChildren(), Array.Empty<AutomationElement>());
            for (int i = 0; i < children.Length; i++)
            {
                var nextPath = new int[indexPath.Length + 1];
                Array.Copy(indexPath, nextPath, indexPath.Length);
                nextPath[^1] = i;
                Visit(children[i], depth + 1, nextPath, nextAncestor, childIndent);
            }
        }
    }

    private static bool IsInteresting(AutomationElement el, ControlType ct, string name)
    {
        if (InteractiveTypes.Contains(ct)) return true;
        if (ct == ControlType.Text && !string.IsNullOrWhiteSpace(name)) return true; // named labels inform
        if (Safe(() => el.Properties.IsKeyboardFocusable.ValueOrDefault, false)) return true;
        // any actionable pattern makes it interesting
        return SupportedPatterns(el).Length > 0;
    }

    private static string FormatLine(string indent, string @ref, AutomationElement el,
        ControlType ct, string name, string aid, SnapshotOptions options)
    {
        var r = Safe(() => el.BoundingRectangle, System.Drawing.Rectangle.Empty);
        bool enabled = Safe(() => el.IsEnabled, false);
        bool focusable = Safe(() => el.Properties.IsKeyboardFocusable.ValueOrDefault, false);
        var state = new List<string>();
        if (enabled) state.Add("enabled");
        if (focusable) state.Add("focusable");

        var patterns = SupportedPatterns(el);
        var sb = new StringBuilder();
        sb.Append(indent).Append('[').Append(@ref).Append("] ").Append(ct).Append(' ')
          .Append('"').Append(name).Append('"')
          .Append(" @{").Append(r.X).Append(',').Append(r.Y).Append(',').Append(r.Width).Append(',').Append(r.Height).Append('}')
          .Append(" {").Append(string.Join(", ", state)).Append('}');
        if (patterns.Length > 0) sb.Append(" [").Append(string.Join(",", patterns)).Append(']');
        if (options.FullProperties)
            sb.Append(" aid=").Append(aid).Append(" help=\"").Append(Safe(() => el.HelpText, "")).Append('"');
        return sb.ToString();
    }

    private static string[] SupportedPatterns(AutomationElement el)
    {
        var p = el.Patterns;
        var checks = new (string Name, Func<bool> Supported)[]
        {
            ("Invoke", () => p.Invoke.IsSupported),
            ("Value", () => p.Value.IsSupported),
            ("Toggle", () => p.Toggle.IsSupported),
            ("ExpandCollapse", () => p.ExpandCollapse.IsSupported),
            ("Selection", () => p.Selection.IsSupported),
            ("SelectionItem", () => p.SelectionItem.IsSupported),
            ("ScrollItem", () => p.ScrollItem.IsSupported),
            ("Scroll", () => p.Scroll.IsSupported),
            ("Grid", () => p.Grid.IsSupported),
            ("Text", () => p.Text.IsSupported),
            ("Window", () => p.Window.IsSupported),
            ("Transform", () => p.Transform.IsSupported),
        };
        return checks.Where(c => Safe(c.Supported, false)).Select(c => c.Name).ToArray();
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); } catch { return fallback; }
    }
}
```

> **VERIFY before committing:** the `p.<Pattern>.IsSupported` shape and `el.ControlType`/`el.Name`/`el.AutomationId`/`el.IsEnabled`/`el.HelpText` against FlaUI 5.0.0. If `IsSupported` lives elsewhere (e.g. `p.Invoke.IsSupported` is a `bool` property vs method) adjust the lambda but keep the `(name, bool)` output shape. If a curated `ControlType` member doesn't exist in 5.0.0 (e.g. `DataGrid`), drop it and report `[assumed]->[removed]`. Do NOT invent members.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet build test/FlaUI.Mcp.TestApp && dotnet test --filter "FullyQualifiedName~SnapshotEngineTests"`
Expected: PASS — 2 tests.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/SnapshotEngine.cs test/FlaUI.Mcp.Tests/Perception/SnapshotEngineTests.cs
git commit -m "feat(perception): SnapshotEngine walk + interactiveOnly prune + bounds + patterns"
```

---

### Task 5: PerceptionManager + desktop_snapshot tool + DI wiring

Wire the Core façade, the MCP adapter, and DI so `desktop_snapshot` works end-to-end. Extract a shared `ToolResponse` (`Ok`/`Guard`) and refactor `WindowTools` onto it (DRY); the existing `WindowToolsTests` are the regression oracle.

**Files:**
- Create: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs`
- Create: `src/FlaUI.Mcp.Server/Tools/ToolResponse.cs`
- Modify: `src/FlaUI.Mcp.Server/Tools/WindowTools.cs` (use `ToolResponse`; behavior unchanged)
- Create: `src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs`
- Modify: `src/FlaUI.Mcp.Server/Program.cs` (3 singletons, after line 21)
- Test: `test/FlaUI.Mcp.Tests/Server/SnapshotToolsTests.cs`

- [ ] **Step 0 (STATE-VERIFY):** Open `WindowTools.cs` and confirm the private `Ok`/`Guard` helpers (lines ~56–73) and the `JsonSerializerOptions Json` field (line 13) match. Open `Program.cs` and confirm the singleton block at lines 18–21. If either differs, STOP and report `STATE_MISMATCH`.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

[Trait("Category", "Desktop")]
public class SnapshotToolsTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public SnapshotToolsTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task DesktopSnapshot_returns_a_tree_with_refs_for_an_open_window()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var refs = new RefRegistry();
        var perception = new PerceptionManager(mgr, refs);
        var snap = new SnapshotTools(perception);
        var window = new WindowTools(mgr);

        var opened = await window.DesktopOpenWindow("pid", _app.Process.Id.ToString());
        var handle = JsonDocument.Parse(opened).RootElement.GetProperty("handle").GetString()!;

        var json = await snap.DesktopSnapshot(handle);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("nodeCount").GetInt32() > 0);
        Assert.StartsWith(handle + ":", doc.RootElement.GetProperty("snapshotId").GetString());
        Assert.Contains("Button", doc.RootElement.GetProperty("tree").GetString());
    }

    [Fact]
    public async Task DesktopSnapshot_on_a_stale_handle_returns_a_structured_error()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var snap = new SnapshotTools(new PerceptionManager(mgr, new RefRegistry()));
        var json = await snap.DesktopSnapshot("w999");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("WindowHandleStale", doc.RootElement.GetProperty("error").GetString());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~SnapshotToolsTests"`
Expected: FAIL — `PerceptionManager`/`SnapshotTools` do not exist.

- [ ] **Step 3: Write minimal implementation**

`PerceptionManager.cs`:
```csharp
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>Core façade for perception. Orchestrates SnapshotEngine + RefRegistry on the
/// query STA via WindowManager. RunOnRefAsync (option-C resolution) is added in Task 6.</summary>
public sealed class PerceptionManager
{
    private readonly WindowManager _windows;
    private readonly RefRegistry _refs;

    public PerceptionManager(WindowManager windows, RefRegistry refs)
    {
        _windows = windows;
        _refs = refs;
    }

    public Task<SnapshotResult> SnapshotAsync(WindowHandle handle, SnapshotOptions options) =>
        _windows.RunWithWindowAndDesktopAsync(handle, (win, desktop) =>
        {
            // popup roots: empty until Task 7 grafts owner-process menus.
            IReadOnlyList<AutomationElement> popups = Array.Empty<AutomationElement>();
            AutomationElement root = win; // RootRef rooting added in Task 6
            var snapshotId = _refs.BeginSnapshot(handle.Id);
            var (tree, count) = SnapshotEngine.Walk(root, popups, options, _refs, handle.Id);
            return new SnapshotResult(snapshotId, tree, count);
        });
}
```

`ToolResponse.cs`:
```csharp
using System.Text.Json;
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Server.Tools;

/// <summary>Shared MCP tool response helpers: compact JSON serialization and the
/// ToolException -> structured-error boundary. A single bad call never escapes unmapped.</summary>
internal static class ToolResponse
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static string Ok(object payload) => JsonSerializer.Serialize(payload, Json);

    public static async Task<string> Guard(Func<Task<string>> body)
    {
        try { return await body(); }
        catch (ToolException ex)
        {
            return JsonSerializer.Serialize(
                new { error = ex.Code.ToString(), message = ex.Message, suggestedRecovery = ex.SuggestedRecovery }, Json);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(
                new { error = "INTERNAL", message = ex.Message, suggestedRecovery = (string?)"re-check arguments and retry" }, Json);
        }
    }
}
```

`SnapshotTools.cs`:
```csharp
using System.ComponentModel;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Windows;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Tools;

[McpServerToolType]
public sealed class SnapshotTools
{
    private readonly PerceptionManager _perception;
    public SnapshotTools(PerceptionManager perception) => _perception = perception;

    [McpServerTool, Description("Walk a window's accessibility tree into an indented, ref-tagged snapshot. " +
        "Each line: [e23] Button \"OK\" @{x,y,w,h} {enabled, focusable} [Invoke]. Use the e-refs with later interaction tools.")]
    public Task<string> DesktopSnapshot(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Optional ref to root the snapshot at (from a prior snapshot of this window).")] string? root = null,
        [Description("Max tree depth (default 40).")] int maxDepth = 40,
        [Description("Prune non-interactive container/decoration noise (default true).")] bool interactiveOnly = true,
        [Description("Append AutomationId/HelpText to each line (default false).")] bool fullProperties = false)
        => ToolResponse.Guard(async () =>
        {
            var opts = new SnapshotOptions
            {
                RootRef = root, MaxDepth = maxDepth,
                InteractiveOnly = interactiveOnly, FullProperties = fullProperties,
            };
            var r = await _perception.SnapshotAsync(new WindowHandle(window), opts);
            return ToolResponse.Ok(new { snapshotId = r.SnapshotId, nodeCount = r.NodeCount, tree = r.Tree });
        });
}
```

Refactor `WindowTools.cs`: delete its private `Ok`, `Guard`, and `Json` members and the `using System.Text.Json;`; replace each `Guard(...)`/`Ok(...)` call with `ToolResponse.Guard(...)`/`ToolResponse.Ok(...)`. **No behavior change** — the wire output is byte-identical (same anonymous shapes, same compact options). Leave every tool method body otherwise untouched.

`Program.cs` — insert after line 21 (`AddSingleton<WindowTools>()`):
```csharp
builder.Services.AddSingleton<FlaUI.Mcp.Core.Perception.RefRegistry>();
builder.Services.AddSingleton<FlaUI.Mcp.Core.Perception.PerceptionManager>();
builder.Services.AddSingleton<SnapshotTools>();
```
(`WithToolsFromAssembly()` already discovers `[McpServerToolType]` classes, so `SnapshotTools` is auto-registered as a tool once it's a resolvable singleton.)

- [ ] **Step 4: Run test to verify it passes (and prove no regression)**

Run: `dotnet test --filter "FullyQualifiedName~SnapshotToolsTests"`  → PASS (2 tests)
Run: `dotnet test --filter "Category!=Desktop"`  → PASS, 0 failed (the WindowTools refactor must not break the non-UIA suite; full WindowTools verification is in the Desktop suite).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs src/FlaUI.Mcp.Server/Tools/ToolResponse.cs src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs src/FlaUI.Mcp.Server/Tools/WindowTools.cs src/FlaUI.Mcp.Server/Program.cs test/FlaUI.Mcp.Tests/Server/SnapshotToolsTests.cs
git commit -m "feat(perception): PerceptionManager + desktop_snapshot tool + DI; extract ToolResponse"
```

---

### Task 6: Option-C re-resolution — RefRegistry.Resolve + RunOnRefAsync + RootRef

The core option-C guarantee. Add the TestApp mutating list first, then the resolution logic, then the first-class ref-engine Desktop tests.

**Files:**
- Modify: `test/FlaUI.Mcp.TestApp/MainWindow.xaml` (add ListBox + Add/Clear buttons)
- Modify: `test/FlaUI.Mcp.TestApp/MainWindow.xaml.cs` (button handlers)
- Modify: `src/FlaUI.Mcp.Core/Perception/RefRegistry.cs` (add `Resolve`)
- Modify: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` (add `RunOnRefAsync`; honor `RootRef`)
- Test: `test/FlaUI.Mcp.Tests/Perception/RefResolutionTests.cs`

- [ ] **Step 1: Extend the TestApp** (do this first so the target exists)

`MainWindow.xaml` — add inside the `<StackPanel>` after the `Status` TextBlock:
```xml
        <ListBox x:Name="ItemList" AutomationProperties.AutomationId="ItemList" Height="120" Margin="0,8,0,0">
            <ListBoxItem AutomationProperties.AutomationId="ItemA" Content="A"/>
            <ListBoxItem AutomationProperties.AutomationId="ItemB" Content="B"/>
            <ListBoxItem AutomationProperties.AutomationId="ItemC" Content="C"/>
            <ListBoxItem Content="NamedOnly"/>
        </ListBox>
        <Button x:Name="RebuildItemsButton" Content="Rebuild Items"
                AutomationProperties.AutomationId="RebuildItemsButton" Click="RebuildItemsButton_Click" Margin="0,8,0,0"/>
        <Button x:Name="ClearItemsButton" Content="Clear Items"
                AutomationProperties.AutomationId="ClearItemsButton" Click="ClearItemsButton_Click" Margin="0,4,0,0"/>
```

`MainWindow.xaml.cs` — add handlers (keep the existing `OkButton_Click`):
```csharp
    // Clear and re-create the items as NEW ListBoxItem objects (same AutomationIds). This destroys
    // the old elements, so a held ref's cached UIA element goes invalid and its RuntimeId no longer
    // matches — forcing the option-C descriptor RE-WALK (the cache fast-path can't short-circuit).
    private void RebuildItemsButton_Click(object sender, RoutedEventArgs e)
    {
        ItemList.Items.Clear();
        foreach (var (aid, content) in new[] { ("ItemA", "A"), ("ItemB", "B"), ("ItemC", "C") })
        {
            var item = new System.Windows.Controls.ListBoxItem { Content = content };
            System.Windows.Automation.AutomationProperties.SetAutomationId(item, aid);
            ItemList.Items.Add(item);
        }
    }

    private void ClearItemsButton_Click(object sender, RoutedEventArgs e) => ItemList.Items.Clear();
```

- [ ] **Step 2: Write the failing test**

```csharp
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

// Each test launches its OWN TestApp: these tests MUTATE the list, so a shared IClassFixture would
// leak state across tests and make them order-dependent.
[Trait("Category", "Desktop")]
public class RefResolutionTests
{
    // Snapshot with fullProperties, then find the ref whose line carries aid=<automationId>.
    private static string RefFor(string tree, string automationId)
    {
        foreach (var line in tree.Split('\n'))
            if (line.Contains("aid=" + automationId))
            {
                int lb = line.IndexOf('['), rb = line.IndexOf(']');
                return line.Substring(lb + 1, rb - lb - 1);
            }
        throw new Xunit.Sdk.XunitException($"no ref line for aid={automationId} in:\n{tree}");
    }

    private static void Invoke(WindowManager mgr, WindowHandle h, string automationId) =>
        mgr.RunWithWindowAndDesktopAsync(h, (win, _) =>
        {
            var el = win.FindFirstDescendant(cf => cf.ByAutomationId(automationId))
                     ?? throw new Xunit.Sdk.XunitException($"control {automationId} not found");
            el.AsButton().Invoke();
            return true;
        }).GetAwaiter().GetResult();

    [Fact]
    public async Task The_automationId_branch_recovers_a_ref_even_when_the_cached_element_is_dead()
    {
        // GENUINE end-to-end: rebuilding the list destroys ItemB's element (new object => new
        // RuntimeId), so the cache fast-path CANNOT short-circuit — re-resolution must take the
        // AutomationId-under-ItemList branch to recover the new ItemB. (This is the test agy round 2
        // flagged: a mere insert wouldn't change the RuntimeId, so the cache would mask the re-walk.)
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry());
        var handle = await mgr.OpenByPidAsync(app.Process.Id);

        var snap = await perception.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });
        var refB = RefFor(snap.Tree, "ItemB");

        Invoke(mgr, handle, "RebuildItemsButton"); // destroys + recreates ItemB
        await Task.Delay(300);

        var aid = await perception.RunOnRefAsync(handle, refB, el => el.AutomationId);
        Assert.Equal("ItemB", aid);
    }

    [Fact]
    public async Task The_automationId_branch_ignores_a_stale_IndexPath()
    {
        // DETERMINISTIC: null cache (fast-path skipped) + a deliberately WRONG IndexPath. Resolution
        // must ignore the bad index and recover ItemB by AutomationId under ItemList — proving the
        // AutomationId branch wins over a shifted index, with zero reliance on a real RuntimeId change.
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var refs = new RefRegistry();
        var handle = await mgr.OpenByPidAsync(app.Process.Id);

        refs.BeginSnapshot(handle.Id);
        var descriptor = new ElementDescriptor(
            RuntimeId: Array.Empty<int>(),
            ControlType: FlaUI.Core.Definitions.ControlType.ListItem,
            AutomationId: "ItemB", Name: "B", AncestorAutomationId: "ItemList",
            IndexPath: new[] { 99 }); // wrong on purpose
        var refX = refs.Register(handle.Id, descriptor, cached: null);

        var aid = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            refs.Resolve(handle.Id, refX, new AutomationElement[] { win }).AutomationId);

        Assert.Equal("ItemB", aid);
    }

    [Fact]
    public async Task The_name_plus_controltype_branch_resolves_an_element_lacking_an_automationId()
    {
        // DETERMINISTIC: null cache + NO AutomationId, pointing at the AutomationId-less "NamedOnly"
        // item under ItemList — so resolution MUST take the Name+ControlType branch. No reliance on a
        // mutation happening to change a RuntimeId (the round-1 version could pass purely via cache).
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var refs = new RefRegistry();
        var handle = await mgr.OpenByPidAsync(app.Process.Id);

        refs.BeginSnapshot(handle.Id);
        var descriptor = new ElementDescriptor(
            RuntimeId: Array.Empty<int>(),
            ControlType: FlaUI.Core.Definitions.ControlType.ListItem,
            AutomationId: "", Name: "NamedOnly", AncestorAutomationId: "ItemList",
            IndexPath: Array.Empty<int>());
        var refX = refs.Register(handle.Id, descriptor, cached: null);

        var name = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            refs.Resolve(handle.Id, refX, new AutomationElement[] { win }).Name);

        Assert.Equal("NamedOnly", name);
    }

    [Fact]
    public async Task A_genuinely_removed_element_yields_REF_STALE_UNRESOLVABLE()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry());
        var handle = await mgr.OpenByPidAsync(app.Process.Id);

        var snap = await perception.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });
        var refB = RefFor(snap.Tree, "ItemB");

        Invoke(mgr, handle, "ClearItemsButton"); // ItemB truly gone
        await Task.Delay(300);

        var ex = await Assert.ThrowsAsync<ToolException>(() => perception.RunOnRefAsync(handle, refB, el => el.Name));
        Assert.Equal(ToolErrorCode.RefStaleUnresolvable, ex.Code);
    }

    [Fact]
    public async Task A_new_snapshot_supersedes_the_prior_refs()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry());
        var handle = await mgr.OpenByPidAsync(app.Process.Id);

        var first = await perception.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });
        var oldRef = RefFor(first.Tree, "ItemB");
        await perception.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true }); // supersede

        var ex = await Assert.ThrowsAsync<ToolException>(() => perception.RunOnRefAsync(handle, oldRef, el => el.Name));
        Assert.Equal(ToolErrorCode.RefNotFound, ex.Code);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet build test/FlaUI.Mcp.TestApp && dotnet test --filter "FullyQualifiedName~RefResolutionTests"`
Expected: FAIL — `PerceptionManager.RunOnRefAsync` / `RefRegistry.Resolve` do not exist.

- [ ] **Step 4: Write the implementation**

Add `Resolve` to `RefRegistry` (VERIFY FlaUI `FindFirstDescendant`/`ConditionFactory.ByAutomationId`/`RuntimeId` per the gate):
```csharp
    /// <summary>Option-C resolution. Order: (1) cached element if its RuntimeId AND ControlType
    /// still match (the ControlType check guards against UIA RuntimeId recycling under
    /// virtualization); (2) AutomationId (or Name+ControlType) scoped under the nearest stable
    /// ancestor; (3) IndexPath as a last-resort fuzzy hint; else REF_STALE_UNRESOLVABLE. The
    /// caller supplies the search roots to walk IN ORDER — the window subtree first, then any
    /// grafted popup subtrees (context menus / dropdowns live at the Desktop, not under the
    /// window). Searching those small, process-correct subtrees — never the whole Desktop —
    /// avoids cross-application false matches on a shared Name+ControlType. searchRoots[0] MUST be
    /// the window root (IndexPath is window-relative). Throws REF_NOT_FOUND if the ref isn't live
    /// for this window. Must be called on the query STA.</summary>
    public AutomationElement Resolve(string windowId, string @ref, IReadOnlyList<AutomationElement> searchRoots)
    {
        var entry = Lookup(windowId, @ref); // REF_NOT_FOUND if absent
        var d = entry.Descriptor;

        // (1) cached fast-path — RuntimeId AND ControlType must still match.
        if (entry.Cached is { } cached)
        {
            try
            {
                var rid = cached.Properties.RuntimeId.ValueOrDefault;
                if (rid != null && rid.AsEnumerable().SequenceEqual(d.RuntimeId) && cached.ControlType == d.ControlType)
                    return cached;
            }
            catch { /* element gone — fall through to search */ }
        }

        // (2) descriptor re-walk per search root: AutomationId then Name+ControlType, scoped under
        // the nearest stable ancestor. Each root is small and process-correct (window subtree, then
        // grafted popup subtrees) — never the whole Desktop, so no cross-app false positives.
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
                var byName = TrySearch(scope, cf => cf.ByName(d.Name) & cf.ByControlType(d.ControlType));
                if (byName is not null) return byName;
            }
        }

        // (3) IndexPath last-resort — window-relative only (searchRoots[0] is the window root;
        // popup index paths are sentinel-negative and abort cleanly in TryIndexPath).
        if (searchRoots.Count > 0)
        {
            var byPath = TryIndexPath(searchRoots[0], d.IndexPath);
            if (byPath is not null) return byPath;
        }

        throw new ToolException(ToolErrorCode.RefStaleUnresolvable,
            $"Ref '{@ref}' could not be re-resolved; the element appears to be gone.",
            "take a fresh desktop_snapshot");
    }

    private static AutomationElement? TrySearch(AutomationElement root,
        Func<FlaUI.Core.Conditions.ConditionFactory, FlaUI.Core.Conditions.ConditionBase> cond)
    {
        try { return root.FindFirstDescendant(cond); } catch { return null; }
    }

    private static AutomationElement? TryIndexPath(AutomationElement root, IReadOnlyList<int> path)
    {
        try
        {
            var cur = root;
            foreach (var i in path)
            {
                var kids = cur.FindAllChildren();
                if (i < 0 || i >= kids.Length) return null;
                cur = kids[i];
            }
            return cur;
        }
        catch { return null; }
    }
```
Add the needed `using FlaUI.Core.AutomationElements;` (already present from Task 2) and ensure `ConditionFactory`/`ConditionBase` resolve (namespace `FlaUI.Core.Conditions`). **VERIFY** the `cf.ByName(...) & cf.ByControlType(...)` operator-overload composition compiles in FlaUI 5.0.0 — FlaUI composes conditions with the bitwise `&`/`|` operators, not a `.And()` method; if it differs, adjust but keep the AutomationId-first ordering.

Add `RunOnRefAsync` to `PerceptionManager` and honor `RootRef` in `SnapshotAsync`:
```csharp
    /// <summary>Resolve a ref to its live element on the query STA and run a read over it.
    /// The element never crosses the STA boundary (COM is thread-affine) — only the
    /// projection T returns.</summary>
    public Task<T> RunOnRefAsync<T>(WindowHandle handle, string @ref, Func<AutomationElement, T> func) =>
        _windows.RunWithWindowAndDesktopAsync(handle, (win, desktop) =>
        {
            var el = _refs.Resolve(handle.Id, @ref, SearchRoots(win, desktop));
            return func(el);
        });

    // Roots to re-resolve a ref against, in order. Window subtree only for now; Task 7 appends the
    // owner-process popup subtrees so popup refs resolve too. searchRoots[0] is always the window.
    private static IReadOnlyList<AutomationElement> SearchRoots(AutomationElement win, AutomationElement desktop)
        => new AutomationElement[] { win };
```
And in `SnapshotAsync`, replace `AutomationElement root = win;` with RootRef support:
```csharp
            AutomationElement root = string.IsNullOrEmpty(options.RootRef)
                ? win
                : _refs.Resolve(handle.Id, options.RootRef!, SearchRoots(win, desktop));
            var snapshotId = _refs.BeginSnapshot(handle.Id);
```
> **Ordering note (SHAPE-DIVERGENCE STOP):** resolve `RootRef` **before** `BeginSnapshot` — `BeginSnapshot` clears the window's refs, which would make the `RootRef` lookup miss if done after. Keep this order.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet build test/FlaUI.Mcp.TestApp && dotnet test --filter "FullyQualifiedName~RefResolutionTests"`
Expected: PASS — 5 tests.

- [ ] **Step 6: Commit**

```bash
git add test/FlaUI.Mcp.TestApp/MainWindow.xaml test/FlaUI.Mcp.TestApp/MainWindow.xaml.cs src/FlaUI.Mcp.Core/Perception/RefRegistry.cs src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs test/FlaUI.Mcp.Tests/Perception/RefResolutionTests.cs
git commit -m "feat(perception): option-C RefRegistry.Resolve + RunOnRefAsync + RootRef rooting"
```

---

### Task 7: Popup-grafting

Win32 context menus (`#32768`) and WPF popups are children of the Desktop, not the window. Discover owner-process popups and graft them under `[Popups]`.

**Files:**
- Modify: `test/FlaUI.Mcp.TestApp/MainWindow.xaml` (add a Border with a ContextMenu)
- Modify: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` (discover owner popups; pass to `Walk`)
- Test: `test/FlaUI.Mcp.Tests/Perception/PopupGraftingTests.cs`

- [ ] **Step 1: Extend the TestApp** — add inside the `<StackPanel>`:
```xml
        <Border x:Name="MenuTarget" AutomationProperties.AutomationId="MenuTarget"
                Background="#FFE0E0E0" Height="36" Margin="0,8,0,0">
            <TextBlock Text="right-click me" HorizontalAlignment="Center" VerticalAlignment="Center"/>
            <Border.ContextMenu>
                <ContextMenu>
                    <MenuItem Header="Alpha" AutomationProperties.AutomationId="MenuAlpha"/>
                    <MenuItem Header="Beta" AutomationProperties.AutomationId="MenuBeta"/>
                </ContextMenu>
            </Border.ContextMenu>
        </Border>
```

- [ ] **Step 2: Write the failing test**

```csharp
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class PopupGraftingTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public PopupGraftingTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task A_context_menu_opened_by_right_click_is_grafted_under_Popups()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry());
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        await mgr.FocusAsync(handle);
        await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            var target = win.FindFirstDescendant(cf => cf.ByAutomationId("MenuTarget"))!;
            target.RightClick();
            return true;
        });
        await Task.Delay(400); // let the menu open as a desktop-level popup

        var snap = await perception.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });
        Assert.Contains("[Popups]", snap.Tree);
        Assert.Contains("aid=MenuAlpha", snap.Tree);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet build test/FlaUI.Mcp.TestApp && dotnet test --filter "FullyQualifiedName~PopupGraftingTests"`
Expected: FAIL — popups not discovered (no `[Popups]` node), assertion fails.

- [ ] **Step 4: Write the implementation** — in `PerceptionManager`, replace the empty popups line in `SnapshotAsync`, extend the Task-6 `SearchRoots` helper, and add the finder:

```csharp
        _windows.RunWithWindowAndDesktopAsync(handle, (win, desktop) =>
        {
            IReadOnlyList<AutomationElement> popups = FindOwnerPopups(desktop, win);
            AutomationElement root = string.IsNullOrEmpty(options.RootRef)
                ? win
                : _refs.Resolve(handle.Id, options.RootRef!, SearchRoots(win, desktop));
            var snapshotId = _refs.BeginSnapshot(handle.Id);
            var (tree, count) = SnapshotEngine.Walk(root, popups, options, _refs, handle.Id);
            return new SnapshotResult(snapshotId, tree, count);
        });
```
Extend the Task-6 `SearchRoots` helper so popup refs re-resolve, and add the finder + guards:
```csharp
    // Window subtree first, then the owner-process popup subtrees (so a ref to a grafted
    // context-menu item re-resolves). Each popup root is small and process-correct — Resolve never
    // searches the whole Desktop. REPLACES the window-only version added in Task 6.
    private static IReadOnlyList<AutomationElement> SearchRoots(AutomationElement win, AutomationElement desktop)
    {
        var roots = new List<AutomationElement> { win };
        roots.AddRange(FindOwnerPopups(desktop, win));
        return roots;
    }

    private static int SafePid(AutomationElement el)
    {
        try { return el.Properties.ProcessId.ValueOrDefault; } catch { return -1; }
    }

    private static int[] SafeRuntimeId(AutomationElement el)
    {
        try { return el.Properties.RuntimeId.ValueOrDefault ?? Array.Empty<int>(); }
        catch { return Array.Empty<int>(); }
    }

    private static System.Drawing.Rectangle SafeRect(AutomationElement el)
    {
        try { return el.BoundingRectangle; } catch { return System.Drawing.Rectangle.Empty; }
    }

    // Context menus / dropdowns are children of the Desktop, not the window. Graft those owned by
    // the SAME process as the target window, excluding the target window itself. Match Win32 menus
    // ("#32768") AND WPF popup hosts (a WPF ContextMenu/Popup/ComboBox dropdown is hosted in a
    // top-level window whose className starts with "HwndWrapper", ControlType Window/Pane — NOT
    // Menu — so a "#32768"-only predicate misses every WPF menu; the target window is also an
    // HwndWrapper, so the RuntimeId exclusion distinguishes them). WPF reuses HwndWrapper for MANY
    // transient surfaces, so also skip tooltips and offscreen / zero-size hosts to keep [Popups]
    // from filling with hidden utility windows.
    private static IReadOnlyList<AutomationElement> FindOwnerPopups(AutomationElement desktop, AutomationElement targetWindow)
    {
        var found = new List<AutomationElement>();
        int ownerPid = SafePid(targetWindow);
        if (ownerPid < 0) return found;
        int[] targetRid = SafeRuntimeId(targetWindow);

        AutomationElement[] children;
        try { children = desktop.FindAllChildren(); } catch { return found; }
        foreach (var c in children)
        {
            try
            {
                if (c.Properties.ProcessId.ValueOrDefault != ownerPid) continue;
                if (SafeRuntimeId(c).AsEnumerable().SequenceEqual(targetRid)) continue; // skip the window itself
                if (c.ControlType == FlaUI.Core.Definitions.ControlType.ToolTip) continue;
                if (c.Properties.IsOffscreen.ValueOrDefault) continue;                  // hidden utility windows
                var rect = SafeRect(c);
                if (rect.Width <= 0 || rect.Height <= 0) continue;                      // zero-size hosts
                var cls = c.Properties.ClassName.ValueOrDefault ?? "";
                bool looksPopup =
                    cls == "#32768"                                              // Win32 context menu
                    || cls.StartsWith("HwndWrapper", StringComparison.Ordinal)   // WPF popup/menu host
                    || cls.Contains("Popup", StringComparison.OrdinalIgnoreCase)
                    || c.ControlType == FlaUI.Core.Definitions.ControlType.Menu;
                if (looksPopup) found.Add(c);
            }
            catch { /* transient — skip */ }
        }
        return found;
    }
```
> **VERIFY against the actual TestApp menu (it's WPF):** confirm the opened context menu's desktop-child host reports a `HwndWrapper*` className under the owner pid, is on-screen (`IsOffscreen == false`) with a non-zero rect, and isn't `ControlType.ToolTip`. Also VERIFY `el.Properties.IsOffscreen` and `ControlType.ToolTip` exist in FlaUI 5.0.0. If the host is surfaced differently (e.g. empty className, or only a `Menu`/`MenuItem` descendant), widen the predicate empirically and report `[assumed]->[observed]`. Residual accepted risks (v1): a same-process *secondary on-screen window* would still be grafted, and a *visible tooltip at snapshot time* could slip through; both acceptable for the single-window TestApp. Do NOT loosen to "all desktop children of the pid" without the className/RuntimeId/visibility guards.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet build test/FlaUI.Mcp.TestApp && dotnet test --filter "FullyQualifiedName~PopupGraftingTests"`
Expected: PASS — 1 test.

- [ ] **Step 6: Commit**

```bash
git add test/FlaUI.Mcp.TestApp/MainWindow.xaml src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs test/FlaUI.Mcp.Tests/Perception/PopupGraftingTests.cs
git commit -m "feat(perception): graft owner-process popups (#32768/menus) under [Popups]"
```

---

### Task 8: Full-suite verification + wrap (no commit unless fixes needed)

- [ ] **Step 1: Build the TestApp** — `dotnet build test/FlaUI.Mcp.TestApp` → succeeds.

- [ ] **Step 2: Full suite (interactive desktop session)** — `dotnet test`
Expected: PASS, 0 failed. New tests: PerceptionValueTypes (2), RefRegistry (5), PerceptionPrimitive (2), SnapshotEngine (2), SnapshotTools (2), RefResolution (5), PopupGrafting (1) = 19 added on top of the Phase-1 + install suites.

- [ ] **Step 3: CI filter (non-UIA only)** — `dotnet test --filter "Category!=Desktop"`
Expected: PASS, 0 failed. Only PerceptionValueTypes (2) + RefRegistry (5) are non-Desktop here, plus the existing 37 install/Phase-1 non-UIA tests = 44. (Confirm the WindowTools `ToolResponse` refactor kept this green.)

- [ ] **Step 4: Live smoke (optional, recommended)** — start the stdio host, open a window, snapshot it; confirm `desktop_snapshot` returns a ref-tagged tree for a real app (e.g. Notepad). If wired through the installed/`dotnet run` server, list → open → snapshot.

- [ ] **Step 5: Update the durable execution index** — record each task's commit SHA and set the resume point to "Phase 2 merged → write Phase 3 (Interaction) plan" in `project_flaui_mcp_execution.md` (and its `MEMORY.md` pointer), per the power-failure-resilience rule.

- [ ] **Step 6: Finish the branch** — use superpowers:finishing-a-development-branch (final code review → merge `phase-2-perception` to `master`).

---

## Self-Review (filled by the plan author)

**Spec coverage:** SnapshotEngine ✓ (T4) · option-C RefRegistry + 4-step resolution + per-snapshot scoping ✓ (T2, T6) · popup-grafting under `[Popups]` ✓ (T7) · `interactiveOnly` ✓ (T4) · `fullProperties` ✓ (T4) · always-on bounding rects ✓ (T4) · refs namespaced per window ✓ (T2) · `desktop_snapshot` tool ✓ (T5) · `snapshotId` wire field ✓ (T2/T5) · ref-engine first-class tests incl. AutomationId-shift + Name-fallback + REF_STALE_UNRESOLVABLE + supersede ✓ (T6). Deferred per locked scope: diff/stats/global (P5), vision/screenshot-pixel bounds (P4), Interactor (P3). LegacyIAccessiblePattern surfacing (spec mentions) is **not** in this plan — note as a P3/P5 follow-up if MSAA-only controls show gaps. Raw `IndexPath` last-resort (resolution step 3) is implemented but only exercised indirectly — no dedicated test (would be brittle); accepted documented limitation.

**Review round (agy, web relay, 2026-06-25):** Incorporated — (1) `Resolve` now searches window→desktop so grafted-popup refs re-resolve [agy #1/#8]; (2) `FindOwnerPopups` matches WPF `HwndWrapper*` hosts + excludes the target window by RuntimeId, not just `#32768` [agy #5]; (3) condition composition uses the `&` operator, not `.And()` [agy #6a]; (4) cached fast-path also checks `ControlType` to guard RuntimeId recycling [agy #2]; (5) added a Name-fallback test + AutomationId-less `NamedOnly` item [agy #3]. Rejected: agy #7 (claimed `desktop_snapshot` needs `Resolve` to be end-to-end — false; snapshot depends only on `Register`/`Walk`, fully live at T5; only `RootRef`/`RunOnRefAsync` need `Resolve`, correctly at T6). Held as judgment call: agy #4 (named `Text` "bloat") — labels are signal for an agent mapping fields, like Playwright's a11y tree; kept, with a tighten-if-needed note.

**Review round 2 (agy, web relay, 2026-06-25):** Incorporated — (1) `Resolve` now takes an ordered list of small, process-correct search roots (window subtree, then popup subtrees) instead of the whole Desktop, killing the cross-app false-match AND the redundant re-search [r2 #1]; (2) `FindOwnerPopups` skips tooltips + offscreen + zero-size hosts so the `HwndWrapper` match doesn't bloat `[Popups]` with hidden utility windows [r2 #2]; (3) the ref-engine tests were rebuilt — the round-1 "re-resolve after insert" tests were FALSE-CONFIDENCE (an insert doesn't change the existing item's RuntimeId, so the cache fast-path masked the re-walk; agy flagged the Name test, and it applied equally to the AutomationId test): replaced by deterministic null-cache branch tests (AutomationId-ignores-stale-IndexPath; Name+ControlType) PLUS one genuinely cache-invalidating end-to-end recovery test (rebuild destroys the element) [r2 #5]. r2 #3/#4/#6 validated round-1 changes (no action). **Self-caught while applying r2:** `RefResolutionTests` used a shared `IClassFixture` but mutates the list — switched to a per-test TestApp so the tests aren't order-dependent.

**Placeholder scan:** none — every code step is complete.

**Type consistency:** `SnapshotEngine.Walk` returns `(string Tree, int NodeCount)` everywhere; `RefRegistry.Register(windowId, ElementDescriptor, AutomationElement?)` and `Resolve(windowId, ref, searchRoot)` consistent across T2/T4/T6; `PerceptionManager.SnapshotAsync`/`RunOnRefAsync` signatures stable T5→T6→T7; `SnapshotResult(SnapshotId, Tree, NodeCount)` consistent. `Lookup` is `internal` (test reaches via `InternalsVisibleTo`).

**Known risks for the executor:** (1) FlaUI 5.0.0 member shapes (pattern `IsSupported`, `ControlType` property vs `Properties.ControlType`, `ConditionFactory` chaining) — the verification gate forces compile-confirm + STOP-on-divergence. (2) Right-click/context-menu timing is the flakiest test (T7) — the 400 ms settle + `Category=Desktop` serialization mitigate; the popup predicate may need empirical widening. (3) All UIA tests need an interactive desktop session (same constraint as Phase 1).
