# Phase 3b-2 — Structured Content & Clipboard (v0.5.0) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the first state-mutating *content* surface — `desktop_get_grid_cell`, `desktop_grid_select`, `desktop_get_text`, `desktop_clipboard_get`, `desktop_clipboard_set` — on the merged v0.4.0 infra, no synthetic input.

**Architecture:** Targeted reads run on a new offscreen-tolerant timeout-guarded transient STA (`PerceptionManager.RunOnRefReadAsync`) and replicate the snapshot security floor (denylist + `IsPassword` redaction). The one cell write reuses the Phase-3a action path (`RunOnRefActionAsync` + a new `Interactor.GridSelect`). Clipboard is window-less global OS state via raw Win32 P/Invoke on a plain `Task` (no STA).

**Tech Stack:** C#/.NET 10 (`net10.0-windows`), FlaUI.UIA3 5.0.0, ModelContextProtocol 1.4.0, xUnit. Spec: `docs/superpowers/specs/2026-06-27-flaui-mcp-phase3b2-structured-content-design.md` (HEAD `36ff5f1`). Branch: `phase-3b2-content`.

**FlaUI-5 API STATE-VERIFICATION (do this once, before Task 3):** This plan cites the FlaUI pattern API in the style the codebase already uses (`el.Patterns.X.PatternOrDefault`, `el.Patterns.X.IsSupported`, `prop.ValueOrDefault` — see `Interactor.cs`, `PerceptionManager.cs`). Before implementing Task 3, confirm via DLL reflection (as Phase 3a did) the exact members: `IGridPattern.RowCount`/`ColumnCount` (AutomationProperty&lt;int&gt;) and `GetItem(int row, int column)` → `AutomationElement`; `ITextPattern.DocumentRange` and `GetSelection()` → `ITextRange[]`; `ITextRange.GetText(int maxLength)` → `string`; `ISelectionItemPattern.Select()`. If any member name/shape differs from this plan, **STOP and report `[plan] -> [actual] because <reason>`** — do not silently adapt.

**Per-task verification commands (repo gate — use as-is, invent no stricter flags):**
- Build: `dotnet build FlaUI.Mcp.sln` → expect `Build succeeded` / `0 Error(s)`.
- Non-Desktop: `dotnet test --filter "Category!=Desktop"` → expect `Failed: 0`.
- Desktop (bounded, per the known under-load hang): run ONE class at a time, e.g.
  `dotnet test --filter "Category=Desktop&FullyQualifiedName~ContentToolsTests"`; **loop-kill orphans first/after**: `for i in 1 2 3; do (taskkill //F //T //IM testhost.exe; taskkill //F //T //IM FlaUI.Mcp.TestApp.exe) 2>/dev/null; sleep 1; done`. Expect `Failed: 0`.

---

## File Structure

| File | New? | Responsibility |
| --- | --- | --- |
| `src/FlaUI.Mcp.Core/Errors/ToolErrorCode.cs` | modify | +`ClipboardUnavailable`, `GridCellOutOfRange` |
| `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` | modify | +`RunOnRefReadAsync`, +`GetGridCellAsync`, +`GetTextAsync`, +`GridCellInfo`/`TextReadResult` records, +denylist helper |
| `src/FlaUI.Mcp.Core/Interaction/Interactor.cs` | modify | +`GridSelect(grid,row,col)` |
| `src/FlaUI.Mcp.Core/Interaction/ClipboardAccess.cs` | **new** | raw Win32 clipboard get/set on a plain `Task` (no STA) |
| `src/FlaUI.Mcp.Server/Tools/ContentTools.cs` | **new** | `desktop_get_grid_cell`, `desktop_grid_select`, `desktop_get_text` |
| `src/FlaUI.Mcp.Server/Tools/ClipboardTools.cs` | **new** | `desktop_clipboard_get`, `desktop_clipboard_set` |
| `src/FlaUI.Mcp.Server/Program.cs` | modify | register `ClipboardAccess`, `ContentTools`, `ClipboardTools` |
| `test/FlaUI.Mcp.TestApp/MainWindow.xaml(.cs)` | modify | +`TextDoc` multiline TextBox, +`Grid` DataGrid (reuse existing `Secret` PasswordBox) |
| `test/FlaUI.Mcp.Tests/**` | new files | error-code wire test, ContentTools Desktop tests, ClipboardTools tests, read-only-mode block |

---

## Task 1: New error codes

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Errors/ToolErrorCode.cs:27`
- Test: `test/FlaUI.Mcp.Tests/Errors/ToolExceptionTests.cs:29`

- [ ] **Step 1: Write the failing test** — append to `ToolExceptionTests.cs` after the closing brace of `New_phase3a_codes_serialize_by_name` (after line 29), inside the class:

```csharp
    [Fact]
    public void New_phase3b2_codes_serialize_by_name()
    {
        Assert.Equal("ClipboardUnavailable", ToolErrorCode.ClipboardUnavailable.ToString());
        Assert.Equal("GridCellOutOfRange", ToolErrorCode.GridCellOutOfRange.ToString());
    }
```

- [ ] **Step 2: Run to verify it fails** — `dotnet build FlaUI.Mcp.sln` → expect compile error (the enum members don't exist yet).

- [ ] **Step 3: Implement** — in `ToolErrorCode.cs`, change the final two lines (currently `    NoFocusedElement,` / `    NotImplemented` at L26-27) to add the two members after `NotImplemented`:

```csharp
    NoFocusedElement,
    NotImplemented,
    ClipboardUnavailable,
    GridCellOutOfRange
```

- [ ] **Step 4: Run** — `dotnet test --filter "Category!=Desktop"` → expect `Failed: 0`.

- [ ] **Step 5: Commit** — `git add -A && git commit -m "feat(errors): add ClipboardUnavailable + GridCellOutOfRange codes"`

---

## Task 2: TestApp targets (DataGrid + multiline TextBox)

The Desktop tests in later tasks need a grid, a multiline text field, and a password field. The `Secret` PasswordBox already exists (`MainWindow.xaml:8`, seeded with `MainWindow.SecretValue`). Add a `Grid` DataGrid and a `TextDoc` multiline TextBox, and grow the window so nothing is pushed off-screen.

**Files:**
- Modify: `test/FlaUI.Mcp.TestApp/MainWindow.xaml`
- Modify: `test/FlaUI.Mcp.TestApp/MainWindow.xaml.cs`

> No unit test here (XAML); it is verified by build + the Desktop tests in Tasks 3-5. This task must land before Task 3.

- [ ] **Step 1: Grow the window** — in `MainWindow.xaml:4` change `Height="640"` to `Height="880"` (the two new controls add ~200px; keeps `MenuTarget` on-screen).

- [ ] **Step 2: Add the controls** — in `MainWindow.xaml`, insert immediately after the `Secret` PasswordBox line (after line 8), before the `OkButton`:

```xml
        <TextBox x:Name="TextDoc" AutomationProperties.AutomationId="TextDoc"
                 AcceptsReturn="True" Height="60" Margin="0,0,0,8"
                 Text="line one&#10;line two&#10;line three"/>
        <DataGrid x:Name="Grid" AutomationProperties.AutomationId="Grid"
                  AutoGenerateColumns="True" IsReadOnly="True" CanUserAddRows="False"
                  Height="100" Margin="0,0,0,8"/>
```

- [ ] **Step 3: Populate the grid** — in `MainWindow.xaml.cs`, in the constructor after `Secret.Password = SecretValue;` (line 14), add:

```csharp
        Grid.ItemsSource = new[]
        {
            new { Name = "r0c0", Value = "r0c1" },
            new { Name = "r1c0", Value = "r1c1" },
            new { Name = "r2c0", Value = "r2c1" },
        };
```

- [ ] **Step 4: Build** — `dotnet build FlaUI.Mcp.sln` → expect `Build succeeded`. (Optional manual smoke: launch the TestApp and confirm the grid shows two columns `Name`/`Value` and three rows.)

- [ ] **Step 5: Commit** — `git add -A && git commit -m "test(fixture): add DataGrid + multiline TextBox to TestApp"`

---

## Task 3: get_grid_cell (read) — RunOnRefReadAsync + GetGridCellAsync + tool

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs`
- Create: `src/FlaUI.Mcp.Server/Tools/ContentTools.cs`
- Modify: `src/FlaUI.Mcp.Server/Program.cs:34`
- Test: `test/FlaUI.Mcp.Tests/Perception/ContentToolsTests.cs` (new)

- [ ] **Step 1: Write the failing test** — create `test/FlaUI.Mcp.Tests/Perception/ContentToolsTests.cs`:

```csharp
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class ContentToolsTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public ContentToolsTests(TestAppFixture app) => _app = app;

    private async Task<(PerceptionManager mgr, WindowHandle handle, string gridRef)> SnapshotAndFindGridAsync(WindowManager w, RefRegistry refs)
    {
        var mgr = new PerceptionManager(w, refs, new SnapshotCache());
        var handle = await w.OpenByPidAsync(_app.Process.Id);
        var snap = await mgr.SnapshotAsync(handle, new SnapshotOptions { InteractiveOnly = false, IncludeOffscreen = true, FullProperties = true });
        var gridRef = RefForAid(snap.Tree, "Grid");
        return (mgr, handle, gridRef);
    }

    // Find the e-ref for a control by its AutomationId. Match the AID TOKEN, not a bare substring
    // ("Grid" alone would also match the DataGrid ControlType). STATE-VERIFY the exact FullProperties
    // AID rendering against RefResolutionTests.cs / the SnapshotEngine renderer before relying on the
    // "aid=" token; if the renderer emits a different marker, match that one.
    private static string RefForAid(string tree, string aid)
    {
        var line = tree.Split('\n').First(l => l.Contains($"aid={aid}"));
        return line.TrimStart().Split(']')[0].TrimStart('[');
    }

    [Fact]
    public async Task Get_grid_cell_reads_a_known_cell_value()
    {
        using var d = new AutomationDispatcher();
        using var w = new WindowManager(d);
        var (mgr, handle, gridRef) = await SnapshotAndFindGridAsync(w, new RefRegistry());
        var cell = await mgr.GetGridCellAsync(handle, gridRef, 0, 0, 4000);
        Assert.Equal("r0c0", cell.Value);
        Assert.False(cell.IsPassword);
    }

    [Fact]
    public async Task Get_grid_cell_out_of_range_throws_GridCellOutOfRange()
    {
        using var d = new AutomationDispatcher();
        using var w = new WindowManager(d);
        var (mgr, handle, gridRef) = await SnapshotAndFindGridAsync(w, new RefRegistry());
        var ex = await Assert.ThrowsAsync<FlaUI.Mcp.Core.Errors.ToolException>(
            () => mgr.GetGridCellAsync(handle, gridRef, 99, 0, 4000));
        Assert.Equal(FlaUI.Mcp.Core.Errors.ToolErrorCode.GridCellOutOfRange, ex.Code);
    }
}
```

> NOTE: `TestAppFixture`, `WindowManager(d)`, `RefRegistry()`, `SnapshotCache()`, `OpenByPidAsync`, and `SnapshotResult.Tree` already exist (see `RecycleGuardTests.cs` and `SnapshotTools.cs`). `FullProperties=true` appends AutomationId so `Grid` appears in the line. If `Grid` does not appear, STOP and report rather than guessing a different lookup.

- [ ] **Step 2: Run to verify it fails** — `dotnet build FlaUI.Mcp.sln` → expect compile error (`GetGridCellAsync` undefined).

- [ ] **Step 3a: Implement `RunOnRefReadAsync` + denylist helper + `GetGridCellAsync`** — in `PerceptionManager.cs`, add these members (place `RunOnRefReadAsync` right after `RunOnRefActionAsync`, which ends at L50; it is that method MINUS the offscreen throw):

```csharp
    /// <summary>Resolve a ref and run a READ on a TRANSIENT STA (timeout-guarded), cache-free
    /// like the action path but WITHOUT the offscreen preflight — reads are allowed on
    /// off-screen elements (matching desktop_snapshot includeOffscreen). GetItem/GetText can
    /// force the target app to realize layout, so the abandonable transient STA + timeout
    /// protects the long-lived query STA. Shares the action in-flight cap (MaxPendingActions).</summary>
    public Task<T> RunOnRefReadAsync<T>(WindowHandle handle, string @ref, Func<AutomationElement, T> func, int timeoutMs)
    {
        var descriptor = _refs.Lookup(handle.Id, @ref).Descriptor;
        return _windows.RunOnWindowActionAsync(handle, (win, desktop) =>
        {
            var roots = PopupFinder.SearchRoots(win, desktop);
            var el = _refs.ResolveDescriptor(descriptor, roots, @ref);
            return func(el);
        }, timeoutMs);
    }

    // Replicate the snapshot security floor for targeted reads (they bypass SnapshotEngine).
    private static void EnsureAllowed(AutomationElement el)
    {
        var procName = SafeProcessName(el);
        if (PerceptionPolicy.IsDenied(procName))
            throw new ToolException(ToolErrorCode.TargetDenied,
                $"Reading content from windows owned by '{procName}' is blocked (credential store).",
                "target a different, non-sensitive element");
    }

    public Task<GridCellInfo> GetGridCellAsync(WindowHandle handle, string @ref, int row, int col, int timeoutMs) =>
        RunOnRefReadAsync(handle, @ref, el =>
        {
            EnsureAllowed(el);
            try
            {
                var gp = el.Patterns.Grid.PatternOrDefault
                    ?? throw new ToolException(ToolErrorCode.PatternUnsupported, "Element does not support the Grid pattern.", "pick a grid/table element");
                int rows = gp.RowCount.ValueOrDefault, cols = gp.ColumnCount.ValueOrDefault;
                if (row < 0 || col < 0 || row >= rows || col >= cols)
                    throw new ToolException(ToolErrorCode.GridCellOutOfRange, $"Cell ({row},{col}) is outside the {rows}x{cols} grid.", "use in-range 0-based row/col");
                var cell = gp.GetItem(row, col)
                    ?? throw new ToolException(ToolErrorCode.GridCellOutOfRange, $"Grid has no realized cell at ({row},{col}).", "scroll the grid to realize the row, then retry");
                // Defensive UIA reads — a dynamically-realized cell from a faulty provider can throw
                // COMException on a property/pattern access; mirror EvaluateSelectorValueAsync's
                // try/catch-per-read so a flaky cell degrades gracefully, never leaks as INTERNAL.
                bool isPwd = false;
                try { isPwd = cell.Properties.IsPassword.ValueOrDefault; } catch { }
                string value;
                if (isPwd) value = "[REDACTED]";
                else
                {
                    string? v = null;
                    try { v = cell.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault; } catch { }
                    if (string.IsNullOrEmpty(v)) { try { v = cell.Name; } catch { } }
                    value = v ?? string.Empty;
                }
                string ct = "Unknown", aid = string.Empty;
                try { ct = cell.ControlType.ToString(); } catch { }
                try { aid = cell.Properties.AutomationId.ValueOrDefault ?? string.Empty; } catch { }
                return new GridCellInfo(value, ct, aid, isPwd);
            }
            catch (System.UnauthorizedAccessException)
            { throw new ToolException(ToolErrorCode.AccessDeniedIntegrity, "Cannot read the target (higher-integrity/elevated window).", "run the target at the same integrity level"); }
        }, timeoutMs);
```

- [ ] **Step 3b: Add the record** — at the bottom of `PerceptionManager.cs` (next to `FocusedElementInfo`/`CaptureGeometry`):

```csharp
public sealed record GridCellInfo(string Value, string ControlType, string AutomationId, bool IsPassword);
```

- [ ] **Step 3c: Create `ContentTools.cs`** with the grid-cell read tool (the other two methods are added in Tasks 4-5):

```csharp
using System.ComponentModel;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Windows;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Tools;

[McpServerToolType]
public sealed class ContentTools
{
    private const int DefaultTimeoutMs = 4000;
    private readonly PerceptionManager _perception;
    private readonly WindowManager _windows;
    private readonly ServerOptions _options;

    public ContentTools(PerceptionManager perception, WindowManager windows, ServerOptions options)
    { _perception = perception; _windows = windows; _options = options; }

    [McpServerTool(ReadOnly = true), Description("Read one grid/table cell by (row,col) without snapshotting the whole grid. ref = a Grid/Table element; row/col are 0-based. Returns the cell value (Value pattern else Name), controlType, automationId, isPassword. GridCellOutOfRange if out of bounds; PatternUnsupported if not a grid. To ACT on a cell, re-snapshot with rootRef=<grid ref>.")]
    public Task<string> DesktopGetGridCell(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Grid element ref from a snapshot, e.g. e23.")] string @ref,
        [Description("0-based row index.")] int row,
        [Description("0-based column index.")] int col,
        [Description("Read timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.Guard(async () =>
        {
            var c = await _perception.GetGridCellAsync(new WindowHandle(window), @ref, row, col, timeoutMs);
            return ToolResponse.Ok(new { value = c.Value, controlType = c.ControlType, automationId = c.AutomationId, isPassword = c.IsPassword });
        });
}
```

- [ ] **Step 3d: Register in DI** — in `Program.cs`, after line 34 (`builder.Services.AddSingleton<InteractionTools>();`) add:

```csharp
builder.Services.AddSingleton<ContentTools>();
```

- [ ] **Step 4: Run** — `dotnet build FlaUI.Mcp.sln` (expect `Build succeeded`), then the bounded Desktop run for `ContentToolsTests` (loop-kill orphans first) → expect `Failed: 0` (2 tests). Then `dotnet test --filter "Category!=Desktop"` → `Failed: 0`.

- [ ] **Step 5: Commit** — `git add -A && git commit -m "feat(content): desktop_get_grid_cell + RunOnRefReadAsync"`

---

## Task 4: get_text (read)

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs`
- Modify: `src/FlaUI.Mcp.Server/Tools/ContentTools.cs`
- Test: `test/FlaUI.Mcp.Tests/Perception/ContentToolsTests.cs`

- [ ] **Step 1: Write the failing test** — add to `ContentToolsTests.cs`:

```csharp
    [Fact]
    public async Task Get_text_reads_full_truncates_and_redacts()
    {
        using var d = new AutomationDispatcher();
        using var w = new WindowManager(d);
        var refs = new RefRegistry();
        var mgr = new PerceptionManager(w, refs, new SnapshotCache());
        var handle = await w.OpenByPidAsync(_app.Process.Id);
        var snap = await mgr.SnapshotAsync(handle, new SnapshotOptions { InteractiveOnly = false, IncludeOffscreen = true, FullProperties = true });
        string RefFor(string aid) => RefForAid(snap.Tree, aid);

        var doc = await mgr.GetTextAsync(handle, RefFor("TextDoc"), selectionOnly: false, maxLength: 10000, 4000);
        Assert.Contains("line one", doc.Text);
        Assert.False(doc.Truncated);
        Assert.False(doc.IsPassword);

        var trunc = await mgr.GetTextAsync(handle, RefFor("TextDoc"), selectionOnly: false, maxLength: 4, 4000);
        Assert.True(trunc.Truncated);
        Assert.Equal(4, trunc.Text.Length);

        var pwd = await mgr.GetTextAsync(handle, RefFor("Secret"), selectionOnly: false, maxLength: 10000, 4000);
        Assert.Equal("[REDACTED]", pwd.Text);
        Assert.True(pwd.IsPassword);
        Assert.DoesNotContain("hunter2", pwd.Text);
    }
```

- [ ] **Step 2: Run to verify it fails** — `dotnet build FlaUI.Mcp.sln` → compile error (`GetTextAsync` undefined).

- [ ] **Step 3a: Implement `GetTextAsync` + `TextReadResult`** — in `PerceptionManager.cs`, add the reader (after `GetGridCellAsync`):

```csharp
    public Task<TextReadResult> GetTextAsync(WindowHandle handle, string @ref, bool selectionOnly, int maxLength, int timeoutMs) =>
        RunOnRefReadAsync(handle, @ref, el =>
        {
            EnsureAllowed(el);
            // Password short-circuit FIRST — never ask the provider for a secret's text/selection.
            // Read IsPassword defensively (a COMException here must not bypass clean handling and
            // surface as INTERNAL); if it can't be read it's a flaky non-password field → proceed.
            bool isPwd = false;
            try { isPwd = el.Properties.IsPassword.ValueOrDefault; } catch { }
            if (isPwd) return new TextReadResult("[REDACTED]", false, true);
            try
            {
                var tp = el.Patterns.Text.PatternOrDefault
                    ?? throw new ToolException(ToolErrorCode.PatternUnsupported, "Element does not support the Text pattern.", "pick a text/document element");
                int cap = System.Math.Clamp(maxLength, 1, 200000);
                string raw;
                if (selectionOnly)
                {
                    try
                    {
                        var sel = tp.GetSelection();
                        raw = (sel is { Length: > 0 }) ? sel[0].GetText(cap + 1) : string.Empty;
                    }
                    catch { raw = string.Empty; } // GetSelection is brittle (throws when no selection)
                }
                else raw = tp.DocumentRange.GetText(cap + 1);

                bool truncated = raw.Length > cap;
                if (truncated) raw = raw.Substring(0, cap);
                return new TextReadResult(raw, truncated, false);
            }
            catch (System.UnauthorizedAccessException)
            { throw new ToolException(ToolErrorCode.AccessDeniedIntegrity, "Cannot read the target (higher-integrity/elevated window).", "run the target at the same integrity level"); }
        }, timeoutMs);
```

And the record at the bottom of the file:

```csharp
public sealed record TextReadResult(string Text, bool Truncated, bool IsPassword);
```

- [ ] **Step 3b: Add the tool** — in `ContentTools.cs`, add inside the class:

```csharp
    [McpServerTool(ReadOnly = true), Description("Read an element's text via UIA TextPattern. selectionOnly=true reads the current selection (empty if none). maxLength caps output (default 10000, 1..200000); truncated=true if the text exceeded it. A password field returns text=\"[REDACTED]\", isPassword=true. Off-screen targets ARE readable. PatternUnsupported if no TextPattern.")]
    public Task<string> DesktopGetText(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Element ref from a snapshot, e.g. e23.")] string @ref,
        [Description("Read only the current selection (default false = full text).")] bool selectionOnly = false,
        [Description("Max chars (default 10000).")] int maxLength = 10000,
        [Description("Read timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.Guard(async () =>
        {
            var t = await _perception.GetTextAsync(new WindowHandle(window), @ref, selectionOnly, maxLength, timeoutMs);
            return ToolResponse.Ok(new { text = t.Text, truncated = t.Truncated, isPassword = t.IsPassword });
        });
```

- [ ] **Step 4: Run** — build, then bounded Desktop `ContentToolsTests` → `Failed: 0`; then `Category!=Desktop` → `Failed: 0`.

- [ ] **Step 5: Commit** — `git add -A && git commit -m "feat(content): desktop_get_text (TextPattern read + redaction + maxLength)"`

---

## Task 5: grid_select (write) — Interactor.GridSelect + tool

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Interaction/Interactor.cs`
- Modify: `src/FlaUI.Mcp.Server/Tools/ContentTools.cs`
- Test: `test/FlaUI.Mcp.Tests/Perception/ContentToolsTests.cs`

- [ ] **Step 1: Write the failing test** — add to `ContentToolsTests.cs` (drive through the same action path the tool uses; asserting selection state via re-snapshot is brittle, so assert no-error + correct error on out-of-range):

```csharp
    [Fact]
    public async Task Grid_select_selects_a_cell_without_error()
    {
        using var d = new AutomationDispatcher();
        using var w = new WindowManager(d);
        var (mgr, handle, gridRef) = await SnapshotAndFindGridAsync(w, new RefRegistry());
        var ok = await mgr.RunOnRefActionAsync(handle, gridRef,
            el => { Interactor.GridSelect(el, 1, 0); return true; }, 4000);
        Assert.True(ok);
    }

    [Fact]
    public async Task Grid_select_out_of_range_throws_GridCellOutOfRange()
    {
        using var d = new AutomationDispatcher();
        using var w = new WindowManager(d);
        var (mgr, handle, gridRef) = await SnapshotAndFindGridAsync(w, new RefRegistry());
        var ex = await Assert.ThrowsAsync<FlaUI.Mcp.Core.Errors.ToolException>(
            () => mgr.RunOnRefActionAsync(handle, gridRef, el => { Interactor.GridSelect(el, 0, 99); return true; }, 4000));
        Assert.Equal(FlaUI.Mcp.Core.Errors.ToolErrorCode.GridCellOutOfRange, ex.Code);
    }
```

(The `using FlaUI.Mcp.Core.Interaction;` import is already at the top of the file from Task 3.)

- [ ] **Step 2: Run to verify it fails** — build → compile error (`Interactor.GridSelect` undefined).

- [ ] **Step 3a: Implement `Interactor.GridSelect`** — in `Interactor.cs`, add after `Select` (the method ends at L52). It bounds-checks, fetches the cell, runs the offscreen-CELL preflight (the action path only guards the *grid* ref), then selects:

```csharp
    public static void GridSelect(AutomationElement grid, int row, int col)
    {
        var gp = grid.Patterns.Grid.PatternOrDefault ?? throw Unsupported("Grid");
        int rows = gp.RowCount.ValueOrDefault, cols = gp.ColumnCount.ValueOrDefault;
        if (row < 0 || col < 0 || row >= rows || col >= cols)
            throw new ToolException(ToolErrorCode.GridCellOutOfRange,
                $"Cell ({row},{col}) is outside the {rows}x{cols} grid.", "use in-range 0-based row/col");
        var cell = gp.GetItem(row, col) ?? throw new ToolException(ToolErrorCode.GridCellOutOfRange,
            $"Grid has no realized cell at ({row},{col}).", "scroll the grid to realize the row, then retry");
        if (cell.Properties.IsOffscreen.ValueOrDefault)
            throw new ToolException(ToolErrorCode.ElementNotActionable,
                "Target cell is off-screen.", "scroll the grid to bring the cell on-screen, then retry");
        var sel = cell.Patterns.SelectionItem.PatternOrDefault ?? throw Unsupported("SelectionItem");
        sel.Select();
    }
```

(`Unsupported` already exists in `Interactor.cs:13` and reports `PatternUnsupported`; `ToolErrorCode` is already imported.)

- [ ] **Step 3b: Add the tool** — in `ContentTools.cs`, add the write tool using the same `GuardWrite → RunOnRefActionAsync` shape as `InteractionTools.Act`:

```csharp
    [McpServerTool(Destructive = true), Description("Select a grid/table cell by (row,col) via UIA SelectionItemPattern. ref = the Grid element; row/col 0-based. GridCellOutOfRange if out of bounds; ElementNotActionable if the cell is off-screen (scroll first); PatternUnsupported if the cell isn't selectable. Blocked in --read-only-mode.")]
    public Task<string> DesktopGridSelect(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Grid element ref, e.g. e23.")] string @ref,
        [Description("0-based row index.")] int row,
        [Description("0-based column index.")] int col,
        [Description("Block timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            await _perception.RunOnRefActionAsync(new WindowHandle(window), @ref,
                el => { Interactor.GridSelect(el, row, col); return true; }, timeoutMs);
            return ToolResponse.Ok(new { ok = true, pathUsed = "pattern" });
        });
```

- [ ] **Step 4: Run** — build, bounded Desktop `ContentToolsTests` → `Failed: 0`; `Category!=Desktop` → `Failed: 0`.

- [ ] **Step 5: Commit** — `git add -A && git commit -m "feat(content): desktop_grid_select (Interactor.GridSelect + offscreen-cell preflight)"`

---

## Task 6: ClipboardAccess (Win32) + clipboard_get

**Files:**
- Create: `src/FlaUI.Mcp.Core/Interaction/ClipboardAccess.cs`
- Create: `src/FlaUI.Mcp.Server/Tools/ClipboardTools.cs`
- Modify: `src/FlaUI.Mcp.Server/Program.cs`
- Test: `test/FlaUI.Mcp.Tests/Interaction/ClipboardAccessTests.cs` (new)

- [ ] **Step 1: Write the failing test** — create `test/FlaUI.Mcp.Tests/Interaction/ClipboardAccessTests.cs` (Desktop-tagged: touches the real OS clipboard, kept out of the headless gate):

```csharp
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

[Trait("Category", "Desktop")]
public class ClipboardAccessTests
{
    [Fact]
    public async Task Set_then_get_round_trips_text()
    {
        var probe = "flaui-mcp-clip-" + System.Guid.NewGuid().ToString("N");
        await ClipboardAccess.SetTextAsync(probe);
        Assert.Equal(probe, await ClipboardAccess.GetTextAsync());
    }

    [Fact]
    public async Task Set_empty_then_get_returns_empty()
    {
        await ClipboardAccess.SetTextAsync("");
        Assert.Equal("", await ClipboardAccess.GetTextAsync());
    }
}
```

- [ ] **Step 2: Run to verify it fails** — build → compile error (`ClipboardAccess` undefined).

- [ ] **Step 3a: Implement `ClipboardAccess`** — create `src/FlaUI.Mcp.Core/Interaction/ClipboardAccess.cs`. Raw Win32, plain `Task` (no STA — raw user32 clipboard is apartment-agnostic), with the exact memory-ownership protocol from spec §2.5:

```csharp
using System;
using System.Runtime.InteropServices;
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Window-less global clipboard (CF_UNICODETEXT only) via raw Win32 P/Invoke.
/// No STA (raw user32 clipboard is apartment-agnostic) — runs on a plain Task. SetText follows
/// the strict ownership protocol: the OS owns the HGLOBAL only after a successful SetClipboardData;
/// every other exit path GlobalFrees it.</summary>
public static class ClipboardAccess
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const int OpenRetries = 5;
    private const int RetryDelayMs = 50;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);

    private static bool TryOpen()
    {
        for (int i = 0; i < OpenRetries; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            System.Threading.Thread.Sleep(RetryDelayMs);
        }
        return false;
    }

    public static Task<string> GetTextAsync() => Task.Run(() =>
    {
        Console.Error.WriteLine("[audit] desktop_clipboard_get: reading the global clipboard.");
        if (!TryOpen())
            throw new ToolException(ToolErrorCode.ClipboardUnavailable, "Could not open the clipboard (locked by another process).", "retry in a moment");
        try
        {
            IntPtr h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return string.Empty; // empty OR non-text formats
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) return string.Empty;
            try { return Marshal.PtrToStringUni(p) ?? string.Empty; }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    });

    public static Task SetTextAsync(string text) => Task.Run(() =>
    {
        if (string.IsNullOrEmpty(text))
        {
            if (!TryOpen())
                throw new ToolException(ToolErrorCode.ClipboardUnavailable, "Could not open the clipboard (locked by another process).", "retry in a moment");
            try { EmptyClipboard(); } finally { CloseClipboard(); }
            return;
        }

        IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(((uint)text.Length + 1) * 2));
        if (h == IntPtr.Zero)
            throw new ToolException(ToolErrorCode.ClipboardUnavailable, "Could not allocate clipboard memory.", "retry in a moment");
        bool osOwnsHandle = false;
        bool opened = false;
        try
        {
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero)
                throw new ToolException(ToolErrorCode.ClipboardUnavailable, "Could not lock clipboard memory.", "retry in a moment");
            try
            {
                // Copy the UTF-16 chars + a single wide-null terminator.
                Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
                Marshal.WriteInt16(p, text.Length * 2, 0);
            }
            finally { GlobalUnlock(h); }

            if (!TryOpen())
                throw new ToolException(ToolErrorCode.ClipboardUnavailable, "Could not open the clipboard (locked by another process).", "retry in a moment");
            opened = true;
            EmptyClipboard();
            if (SetClipboardData(CF_UNICODETEXT, h) != IntPtr.Zero)
                osOwnsHandle = true; // OS now owns h — must NOT free it
            else
                throw new ToolException(ToolErrorCode.ClipboardUnavailable, "SetClipboardData failed.", "retry in a moment");
        }
        finally
        {
            if (!osOwnsHandle && h != IntPtr.Zero) GlobalFree(h);
            if (opened) CloseClipboard();
        }
    });
}
```

- [ ] **Step 3b: Create `ClipboardTools.cs`** with the read tool:

```csharp
using System.ComponentModel;
using FlaUI.Mcp.Core.Interaction;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Tools;

[McpServerToolType]
public sealed class ClipboardTools
{
    private readonly ServerOptions _options;
    public ClipboardTools(ServerOptions options) => _options = options;

    [McpServerTool(ReadOnly = true), Description("Read the system clipboard as text (CF_UNICODETEXT). Returns {text} (empty if the clipboard holds no text). WARNING: this can surface secrets a user copied (no redaction possible at this layer). ClipboardUnavailable if the clipboard is locked.")]
    public Task<string> DesktopClipboardGet()
        => ToolResponse.Guard(async () => ToolResponse.Ok(new { text = await ClipboardAccess.GetTextAsync() }));
}
```

- [ ] **Step 3c: Register in DI** — in `Program.cs`, after the `ContentTools` registration add:

```csharp
builder.Services.AddSingleton<ClipboardTools>();
```

(`ClipboardAccess` is static — no DI registration needed.)

- [ ] **Step 4: Run** — build, bounded Desktop `ClipboardAccessTests` → `Failed: 0`; `Category!=Desktop` → `Failed: 0`.

- [ ] **Step 5: Commit** — `git add -A && git commit -m "feat(clipboard): ClipboardAccess (Win32) + desktop_clipboard_get"`

---

## Task 7: clipboard_set (write)

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Tools/ClipboardTools.cs`
- Test: `test/FlaUI.Mcp.Tests/Interaction/ClipboardToolsReadOnlyTests.cs` (new, non-Desktop)

- [ ] **Step 1: Write the failing test** — `clipboard_set` must be blocked in `--read-only-mode`. Create `test/FlaUI.Mcp.Tests/Interaction/ClipboardToolsReadOnlyTests.cs` (non-Desktop — uses a read-only `ServerOptions`, never touches UIA):

```csharp
using System.Text.Json;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class ClipboardToolsReadOnlyTests
{
    [Fact]
    public async Task Clipboard_set_is_blocked_in_read_only_mode()
    {
        var tools = new ClipboardTools(new ServerOptions(ReadOnly: true));
        var json = await tools.DesktopClipboardSet("anything");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("WriteBlockedReadOnly", doc.RootElement.GetProperty("error").GetString());
    }
}
```

> STATE-VERIFY the `ServerOptions` constructor before writing this test. `InteractionTools` is built with a `ServerOptions` exposing `.ReadOnly` (see `ToolResponse.GuardWrite` and Phase-3a `ReadOnlyGateTests`). If `ServerOptions` is a positional record `ServerOptions(bool ReadOnly)`, the above compiles; if the real shape differs, build it the way `ReadOnlyGateTests` does — and if it can't be constructed that simply, **STOP and report** rather than guessing.

- [ ] **Step 2: Run to verify it fails** — build → compile error (`DesktopClipboardSet` undefined).

- [ ] **Step 3: Implement** — in `ClipboardTools.cs`, add:

```csharp
    [McpServerTool(Destructive = true), Description("Write text to the system clipboard (CF_UNICODETEXT). Useful to stage text the user (or a later Phase-4 paste) can insert. Blocked in --read-only-mode. ClipboardUnavailable if the clipboard is locked.")]
    public Task<string> DesktopClipboardSet(
        [Description("The text to place on the clipboard.")] string text)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            await ClipboardAccess.SetTextAsync(text);
            return ToolResponse.Ok(new { ok = true });
        });
```

- [ ] **Step 4: Run** — `dotnet test --filter "Category!=Desktop"` → `Failed: 0`. (The round-trip is already covered Desktop-side in Task 6; optionally re-run `ClipboardAccessTests` to confirm set+get still green.)

- [ ] **Step 5: Commit** — `git add -A && git commit -m "feat(clipboard): desktop_clipboard_set (GuardWrite + Win32 ownership protocol)"`

---

## Task 8: Docs + version bump + wrap

**Files:**
- Modify: `README.md`, `ROADMAP.md`
- Modify: `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj:20` (`<Version>`)
- Modify: `installer/flaui-mcp.iss:4` (`#define AppVersion`)

- [ ] **Step 1: README** — add the 5 new tools to the tool table/section (mirror the Phase-3a/3b-1 entries): `desktop_get_grid_cell`, `desktop_grid_select`, `desktop_get_text`, `desktop_clipboard_get`, `desktop_clipboard_set`. Note the clipboard exfil caveat and that `get_text`/`get_grid_cell` honor the credential denylist + `IsPassword` redaction. (Per the "update README before every push" rule — the repo is public.)

- [ ] **Step 2: ROADMAP** — mark Phase 3b-2 shipped (v0.5.0): move `desktop_get_grid_cell`/`desktop_grid_select`/`desktop_get_text` and `desktop_clipboard_get`/`desktop_clipboard_set` from "pending → v0.5.0" to shipped; note `set_caret`/`select_text_range` deferred to Phase 4.

- [ ] **Step 3: Version bump** — `FlaUI.Mcp.Server.csproj:20` `<Version>0.4.0</Version>` → `0.5.0`; `installer/flaui-mcp.iss:4` `#define AppVersion "0.4.0"` → `"0.5.0"` (keep them in lockstep — the 3b-1 wrap missed the .iss).

- [ ] **Step 4: Full verification** — `dotnet build FlaUI.Mcp.sln` (`0 Error(s)`); `dotnet test --filter "Category!=Desktop"` (`Failed: 0`); then the new Desktop classes in bounded chunks (loop-kill orphans between): `ContentToolsTests`, `ClipboardAccessTests` → each `Failed: 0`.

- [ ] **Step 5: Commit** — `git add -A && git commit -m "docs+release: Phase 3b-2 structured content & clipboard (v0.5.0)"`

- [ ] **Step 6: Finish the branch** — run **superpowers:finishing-a-development-branch**: verify tests, then merge `phase-3b2-content` → `master` (per user choice), and tag `v0.5.0` when the user is ready to release (tag triggers `release.yml`).

---

## Self-Review

**Spec coverage:** §1 5-tool surface → Tasks 3-7. §2.1 get_grid_cell → T3. §2.2 grid_select (bounds→GetItem→offscreen→SelectionItem, "SelectionItem" naming) → T5. §2.3 get_text (password-first, maxLength+1 off-by-one, GetSelection try/catch, isPassword payload) → T4. §2.4/2.5 clipboard (Win32, osOwnsHandle, empty fast-path, NULL→"") → T6/T7. §3 RunOnRefReadAsync (offscreen-tolerant) + shared cap → T3. §5 denylist + IsPassword on targeted reads → T3/T4 (`EnsureAllowed`, password short-circuit). §6 codes + AccessDeniedIntegrity mapping → T1 + T3/T4 catch. §7 tests → each task. §8 sequencing reads-before-writes → task order. §4 components → File Structure table.

**Placeholder scan:** none — every code step shows full code; FlaUI API members flagged for STATE-VERIFICATION up front; `ServerOptions` ctor flagged for verification in T7.

**Type consistency:** `GridCellInfo(Value,ControlType,AutomationId,IsPassword)`, `TextReadResult(Text,Truncated,IsPassword)`, `RunOnRefReadAsync<T>(handle,ref,func,timeoutMs)`, `GetGridCellAsync(handle,ref,row,col,timeoutMs)`, `GetTextAsync(handle,ref,selectionOnly,maxLength,timeoutMs)`, `Interactor.GridSelect(grid,row,col)`, `ClipboardAccess.GetTextAsync()/SetTextAsync(text)` — names consistent across tasks. Tool JSON: get_grid_cell `{value,controlType,automationId,isPassword}`, get_text `{text,truncated,isPassword}`, grid_select `{ok,pathUsed}`, clipboard_get `{text}`, clipboard_set `{ok}` — match spec §2.

**Known risk:** the Desktop snapshot-line ref-extraction helper in the tests assumes `Grid`/`TextDoc`/`Secret` appear in the snapshot with `FullProperties=true`. If a control's AutomationId isn't surfaced, the implementer should STOP and report (flagged in T3 NOTE) rather than guess a different lookup.
