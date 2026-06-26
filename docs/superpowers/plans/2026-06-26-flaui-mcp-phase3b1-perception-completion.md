# FlaUI.Mcp Phase 3b-1 — Perception Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete the read-only perception surface (screenshot, bounds, snapshot stats/diff, focus, wait conditions) on the existing split query/action STA dispatcher, all tools `readOnlyHint:true, destructiveHint:false`.

**Architecture:** A Task-0 `SnapshotEngine` node-model refactor (project `SnapshotNode`s from `Walk`, render the text byte-identically from the model) unblocks stats/diff/stability. New read-only tools layer on the model + the option-C ref engine. Bounds/selector/focus resolution run on the long-lived query STA; bitmap capture + PNG encode run on the query STA (GDI screen-scrape is fast). Wait loops issue one short `RunQueryAsync` tick per poll with a throwaway `RefRegistry` (no real-registry growth) and `Task.Delay` between ticks. No synthetic input — deferred to Phase 4.

**Tech Stack:** C#/.NET 10 (`net10.0-windows`), FlaUI.UIA3 5.0.0 (`FlaUI.Core.Capturing.Capture`, `CaptureImage.Bitmap`), ModelContextProtocol 1.4.0 (`CallToolResult` + `ImageContentBlock.FromBytes` + `TextContentBlock`), xUnit. P/Invoke for DPI (`MonitorFromPoint`/`GetDpiForMonitor`), virtual-screen metrics (`GetSystemMetrics`), per-window rect (`GetWindowRect`), headless detection (`OpenInputDesktop`). PNG via `System.Drawing` (transitively from FlaUI.Core).

**Release:** v0.4.0. Scope is **3b-1 only**. Branch: `phase-3b-perception` (spec committed `abe9ee0`).

---

## Ground-truth references (verified against merged code on `phase-3b-perception`)

- `src/FlaUI.Mcp.Core/Perception/SnapshotEngine.cs` — `public static (string Tree, int NodeCount) Walk(AutomationElement root, IReadOnlyList<AutomationElement> popupRoots, SnapshotOptions options, RefRegistry refs, string windowId)` (L23). Inner `Visit` recursion L61-126; `[Active Overlays]` header L50; depth-limit line L114-116; `FormatLine(indent,@ref,el,ct,name,aid,options)` L138-164 (reads `BoundingRectangle` L141, `IsEnabled` L142, `IsKeyboardFocusable` L143, `IsPassword` L151, patterns L154); `IsInteresting` L129-136; `SupportedPatterns` L166-185; `Safe<T>` L196-199; `RidEqual` L188-194.
- `src/FlaUI.Mcp.Core/Perception/RefRegistry.cs` — `internal sealed record Entry(ElementDescriptor Descriptor, AutomationElement? Cached)` L14; `Register` L34-46; `Lookup` L50-60; `Resolve` fast-path L78-88 (guard L83 = `rid.AsEnumerable().SequenceEqual(d.RuntimeId) && cached.ControlType == d.ControlType && !cached.Properties.IsOffscreen.ValueOrDefault`); `ResolveDescriptor` L96-131; `BeginSnapshot` L22-31.
- `src/FlaUI.Mcp.Core/Perception/ElementDescriptor.cs` — `record ElementDescriptor(IReadOnlyList<int> RuntimeId, ControlType ControlType, string AutomationId, string Name, string? AncestorAutomationId, IReadOnlyList<int> IndexPath)` L8-14.
- `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` — ctor `(WindowManager windows, RefRegistry refs)` L14; `RunOnRefAsync` L23-28; `SnapshotAsync` L59-78 (denylist L65-69, `BeginSnapshot` L75, `Walk` L76); `SafeProcessName` L50-57.
- `src/FlaUI.Mcp.Core/Perception/SnapshotResult.cs` — `record SnapshotResult(string SnapshotId, string Tree, int NodeCount)` L5.
- `src/FlaUI.Mcp.Core/Perception/SnapshotOptions.cs` — `RootRef`, `MaxDepth=40`, `InteractiveOnly=true`, `FullProperties=false`, `IncludeOffscreen=false`.
- `src/FlaUI.Mcp.Core/Perception/PopupFinder.cs` — `SearchRoots(win, desktop)` L14, `FindOwnerPopups(desktop, win)` L26.
- `src/FlaUI.Mcp.Core/Windows/WindowManager.cs` — `record WindowInfo(string Title, string ProcessName, int Pid, bool IsForeground)` L12; `ListWindowsAsync()` L30-40; `EnumTopLevel()` L147-164 (returns `List<(IntPtr Hwnd, string Title, int Pid)>`, z-ordered); `RunWithWindowAndDesktopAsync` L75-82; `OpenByPidAsync` L42-61; P/Invokes L124-142; `_hwnds` dict L19.
- `src/FlaUI.Mcp.Core/Threading/AutomationDispatcher.cs` — `RunQueryAsync<T>(Func<T>)` L20, `RunActionAsync<T>(Func<T>, int)` L23.
- `src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs` — `[McpServerToolType] sealed class`, ctor `(PerceptionManager)` L12, `DesktopSnapshot` L16-33 (`ToolResponse.Guard`).
- `src/FlaUI.Mcp.Server/Tools/WindowTools.cs` — ctor `(WindowManager)` L13, `DesktopListWindows` L16-17, `DesktopOpenWindow` L22-35.
- `src/FlaUI.Mcp.Server/Tools/InteractionTools.cs` — `Act`/`GuardWrite` pattern L21-28; tool attribute style L30-35.
- `src/FlaUI.Mcp.Server/Tools/ToolResponse.cs` — `Ok(object)` L13, `Guard(Func<Task<string>>)` L15, `GuardWrite` L30, private `Json` options L11.
- `src/FlaUI.Mcp.Core/Errors/ToolErrorCode.cs` — enum L3-21 (ends with `Timeout`); `InvalidArguments` is NOT present — **it must be added in Task 1** alongside the 6 new codes (the spec §3.3/§3.6 use it; verify there is no existing `InvalidArguments` before adding).
- `src/FlaUI.Mcp.Server/Program.cs` — DI singletons L25-31; `WithToolsFromAssembly()` L36.
- `src/FlaUI.Mcp.Server/ServerOptions.cs` — STATE-VERIFY its exact ctor shape before constructing it in tests (used in Task 7/9 setups).
- `test/FlaUI.Mcp.Tests/TestAppFixture.cs` — `.Process`; tests are `[Trait("Category","Desktop")] IClassFixture<TestAppFixture>`.
- `test/FlaUI.Mcp.TestApp/MainWindow.xaml` — controls: `Input`, `Secret` (PasswordBox, `MainWindow.SecretValue="hunter2-NEVER-LEAK"`), `OkButton`, `Status`, `ItemList`, `Check`, `Exp`, `FocusReveal`/`RevealedLabel`, `FreezeButton` (`FreezeMs=2000`), `MenuTarget`. Root `StackPanel` is unnamed; Window `AutomationId="MainWindow"`, Height 640 Width 600.

**Test commands** (run from repo root `C:\Users\user\Development\c#\aidesktop`):
- Build: `dotnet build -c Debug` → expected `Build succeeded` `0 Error(s)`.
- Non-Desktop gate: `dotnet test --filter "Category!=Desktop"` → all green (currently 65 passing).
- Desktop test (physical console / connected RDP, one class at a time): `dotnet test --filter "FullyQualifiedName~<ClassName>"`.
- **Important (`InvariantGlobalization=true`, `WriteIndented=false`):** the server serializes compact JSON. Tests parse with `System.Text.Json`; assert on parsed values, not raw substrings, except where noted.

---

## File Structure

**New (Core/Perception):** `SnapshotNode.cs` (model + `SnapshotModel`), `SnapshotCache.cs`, `SnapshotDiff.cs`, `WaitCoordinator.cs`, `ScreenCapture.cs`, `Geometry/DpiHelper.cs`.
**New (Server/Tools):** `ScreenshotTools.cs` (`desktop_screenshot`, `desktop_get_bounds`).
**Extend:** `SnapshotEngine.cs` (Build/Render), `ElementDescriptor.cs` (`Focused`), `RefRegistry.cs` (Name guard), `PerceptionManager.cs` (`BuildModelAsync`, cache wiring, stats/diff/focus/capture helpers), `SnapshotTools.cs` (`snapshot_stats`/`snapshot_diff`/`wait_for`/`wait_for_stable`/`get_focused_element`), `WindowTools.cs` + `WindowManager.cs` (`list_windows` extension, focused-element + query passthrough), `ToolErrorCode.cs`, `ToolResponse.cs` (image helper), `Program.cs` (DI).
**New tests:** under `test/FlaUI.Mcp.Tests/Perception` and `/Server`. TestApp gains controls for wait tests (Tasks 9-10).

---

### Task 0: `SnapshotEngine` node-model refactor (byte-identical) — PREREQUISITE

**Files:**
- Create: `src/FlaUI.Mcp.Core/Perception/SnapshotNode.cs`
- Modify: `src/FlaUI.Mcp.Core/Perception/SnapshotEngine.cs`
- Test: `test/FlaUI.Mcp.Tests/Perception/SnapshotModelPinTests.cs` (Desktop)

The pin asserts the **old inline `Walk`** and the **new `Render(Build(...))`** produce byte-identical text against the live TestApp tree in the SAME run — then `Walk` collapses to delegate.

- [ ] **Step 1: Create the node model**

`src/FlaUI.Mcp.Core/Perception/SnapshotNode.cs`:
```csharp
using FlaUI.Core.Definitions;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>One rendered item of a snapshot: an element node or a structural marker
/// ([Active Overlays] header / depth-limit line). Render(...) reproduces the byte-for-byte legacy
/// text; Stats/Diff/stability consume only the element nodes.</summary>
public abstract record SnapshotItem;

/// <summary>An element surfaced in the snapshot. Field reads mirror the pre-refactor FormatLine
/// exactly (same Safe() fallbacks, same order) so rendering stays byte-identical. Value is read
/// only for ValuePattern-bearing elements (bounded cost) and is NOT rendered — it feeds Diff.</summary>
public sealed record SnapshotNode(
    string Ref,
    int Depth,
    string Indent,
    ControlType ControlType,
    string AutomationId,
    string Name,
    string? Value,
    System.Drawing.Rectangle Bounds,
    bool Enabled,
    bool Focusable,
    bool Focused,
    bool IsPassword,
    bool IsOffscreen,
    IReadOnlyList<int> RuntimeId,
    IReadOnlyList<string> Patterns,
    string HelpText) : SnapshotItem;

public sealed record OverlaysHeaderItem : SnapshotItem;

public sealed record DepthLimitItem(string Indent, int MoreCount, int MaxDepth) : SnapshotItem;

/// <summary>The projected snapshot: ordered render items. NodeCount counts element nodes only
/// (matching the legacy Walk count) so structural markers don't inflate it.</summary>
public sealed record SnapshotModel(IReadOnlyList<SnapshotItem> Items)
{
    public IEnumerable<SnapshotNode> Nodes => Items.OfType<SnapshotNode>();
    public int NodeCount => Items.Count(i => i is SnapshotNode);
}
```
(`Focused` is included now so Task 2 only wires the read; it defaults to false until then. Rendering ignores it until Task 2.)

- [ ] **Step 2: Write the failing pin test**

`test/FlaUI.Mcp.Tests/Perception/SnapshotModelPinTests.cs`:
```csharp
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class SnapshotModelPinTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public SnapshotModelPinTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Render_of_built_model_is_byte_identical_to_Walk_text()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var (walkText, render, count, nodeCount) = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            var opts = new SnapshotOptions();
            var refsA = new RefRegistry(); refsA.BeginSnapshot(handle.Id);
            var (text, c) = SnapshotEngine.Walk(win, System.Array.Empty<AutomationElement>(), opts, refsA, handle.Id);
            var refsB = new RefRegistry(); refsB.BeginSnapshot(handle.Id);
            var model = SnapshotEngine.Build(win, System.Array.Empty<AutomationElement>(), opts, refsB, handle.Id);
            return (text, SnapshotEngine.Render(model, opts), c, model.NodeCount);
        });

        Assert.Equal(walkText, render);
        Assert.Equal(count, nodeCount);
    }
}
```

- [ ] **Step 3: Run — verify it fails to compile** — Run: `dotnet test --filter "FullyQualifiedName~SnapshotModelPinTests"` → Expected: BUILD FAIL (`SnapshotEngine.Build`/`Render` missing).

- [ ] **Step 4: Add `Build` + `Render` to `SnapshotEngine` WITHOUT touching `Walk`**

Add these members to `SnapshotEngine` (keep `Walk` exactly as-is for now). `Build` mirrors `Walk`'s traversal/culling/registration precisely but appends `SnapshotItem`s; `Render`+`FormatNode` reproduce the legacy text:
```csharp
public static SnapshotModel Build(
    AutomationElement root,
    IReadOnlyList<AutomationElement> popupRoots,
    SnapshotOptions options,
    RefRegistry refs,
    string windowId)
{
    var items = new List<SnapshotItem>();

    var popupRids = new List<int[]>();
    foreach (var p in popupRoots)
    {
        var prid = Safe(() => p.Properties.RuntimeId.ValueOrDefault, (int[]?)null);
        if (prid != null) popupRids.Add(prid);
    }

    var rootBounds = Safe(() => root.BoundingRectangle, System.Drawing.Rectangle.Empty);
    Visit(root, depth: 0, indexPath: Array.Empty<int>(), ancestorAid: null, indent: "", cullBounds: rootBounds);

    if (popupRoots.Count > 0)
    {
        items.Add(new OverlaysHeaderItem());
        for (int i = 0; i < popupRoots.Count; i++)
        {
            var popupBounds = Safe(() => popupRoots[i].BoundingRectangle, System.Drawing.Rectangle.Empty);
            Visit(popupRoots[i], depth: 0, indexPath: new[] { -1 - i }, ancestorAid: null, indent: "  ", cullBounds: popupBounds);
        }
    }
    return new SnapshotModel(items);

    void Visit(AutomationElement el, int depth, int[] indexPath, string? ancestorAid, string indent,
        System.Drawing.Rectangle cullBounds)
    {
        int[] rid = Safe(() => el.Properties.RuntimeId.ValueOrDefault, (int[]?)null) ?? Array.Empty<int>();

        if (depth > 0)
            foreach (var prid in popupRids)
                if (RidEqual(rid, prid)) return;

        if (depth > 0 && !options.IncludeOffscreen
            && Safe(() => el.Properties.IsOffscreen.ValueOrDefault, false)) return;

        if (depth > 0 && !options.IncludeOffscreen && cullBounds.Width > 0 && cullBounds.Height > 0)
        {
            var rect0 = Safe(() => el.BoundingRectangle, System.Drawing.Rectangle.Empty);
            if (rect0.Width <= 0 || rect0.Height <= 0 || !rect0.IntersectsWith(cullBounds)) return;
        }

        string aid = Safe(() => el.AutomationId, "");
        ControlType ct = Safe(() => el.ControlType, ControlType.Custom);
        string name = Safe(() => el.Name, "");

        bool include = depth == 0 || !options.InteractiveOnly || IsInteresting(el, ct, name);
        string childIndent = indent;
        if (include)
        {
            var descriptor = new ElementDescriptor(
                RuntimeId: rid, ControlType: ct, AutomationId: aid, Name: name,
                AncestorAutomationId: ancestorAid, IndexPath: indexPath);
            var @ref = refs.Register(windowId, descriptor, el);

            var rect = Safe(() => el.BoundingRectangle, System.Drawing.Rectangle.Empty);
            bool enabled = Safe(() => el.IsEnabled, false);
            bool focusable = Safe(() => el.Properties.IsKeyboardFocusable.ValueOrDefault, false);
            bool isPassword = Safe(() => el.Properties.IsPassword.ValueOrDefault, false);
            bool offscreen = Safe(() => el.Properties.IsOffscreen.ValueOrDefault, false);
            var patterns = SupportedPatterns(el);
            string help = Safe(() => el.HelpText, "");
            string? value = patterns.Contains("Value")
                ? Safe(() => (string?)el.Patterns.Value.Pattern.Value.ValueOrDefault, null)
                : null;

            items.Add(new SnapshotNode(
                Ref: @ref, Depth: depth, Indent: indent, ControlType: ct, AutomationId: aid,
                Name: name, Value: value, Bounds: rect, Enabled: enabled, Focusable: focusable,
                Focused: false, IsPassword: isPassword, IsOffscreen: offscreen, RuntimeId: rid,
                Patterns: patterns, HelpText: help));
            childIndent = indent + "  ";
        }

        var nextAncestor = string.IsNullOrEmpty(aid) ? ancestorAid : aid;
        AutomationElement[] children = Safe(() => el.FindAllChildren(), Array.Empty<AutomationElement>());
        if (depth >= options.MaxDepth)
        {
            if (children.Length > 0)
                items.Add(new DepthLimitItem(childIndent, children.Length, options.MaxDepth));
            return;
        }
        for (int i = 0; i < children.Length; i++)
        {
            var nextPath = new int[indexPath.Length + 1];
            Array.Copy(indexPath, nextPath, indexPath.Length);
            nextPath[^1] = i;
            Visit(children[i], depth + 1, nextPath, nextAncestor, childIndent, cullBounds);
        }
    }
}

public static string Render(SnapshotModel model, SnapshotOptions options)
{
    var sb = new StringBuilder();
    foreach (var item in model.Items)
    {
        switch (item)
        {
            case SnapshotNode n: sb.AppendLine(FormatNode(n, options)); break;
            case OverlaysHeaderItem: sb.AppendLine("[Active Overlays]"); break;
            case DepthLimitItem d:
                sb.Append(d.Indent).Append("… ").Append(d.MoreCount)
                  .Append(" more (depth limit ").Append(d.MaxDepth).AppendLine(")");
                break;
        }
    }
    return sb.ToString();
}

private static string FormatNode(SnapshotNode n, SnapshotOptions options)
{
    var state = new List<string>();
    if (n.Enabled) state.Add("enabled");
    if (n.Focusable) state.Add("focusable");
    string shownName = n.IsPassword ? "[REDACTED]" : n.Name;

    var sb = new StringBuilder();
    sb.Append(n.Indent).Append('[').Append(n.Ref).Append("] ").Append(n.ControlType).Append(' ')
      .Append('"').Append(shownName).Append('"')
      .Append(" @{").Append(n.Bounds.X).Append(',').Append(n.Bounds.Y).Append(',')
      .Append(n.Bounds.Width).Append(',').Append(n.Bounds.Height).Append('}')
      .Append(" {").Append(string.Join(", ", state)).Append('}');
    if (n.Patterns.Count > 0) sb.Append(" [").Append(string.Join(",", n.Patterns)).Append(']');
    if (options.FullProperties)
        sb.Append(" aid=").Append(n.AutomationId).Append(" help=\"").Append(n.HelpText).Append('"');
    return sb.ToString();
}
```

- [ ] **Step 5: Run the pin test — verify it passes** — Run: `dotnet test --filter "FullyQualifiedName~SnapshotModelPinTests"` → Expected: PASS (byte-identical + matching count against the live tree).

- [ ] **Step 6: Collapse `Walk` to delegate** — Replace the body of `Walk` (L30-126) with:
```csharp
var model = Build(root, popupRoots, options, refs, windowId);
return (Render(model, options), model.NodeCount);
```
Delete the now-unused legacy inline `Visit` local function and the private `FormatLine(string,string,AutomationElement,...)` method (its logic now lives in `FormatNode`). Keep `IsInteresting`, `SupportedPatterns`, `RidEqual`, `Safe`.

- [ ] **Step 7: Run the perception regression suite** — Run: `dotnet test --filter "Category!=Desktop"`, then `dotnet test --filter "FullyQualifiedName~SnapshotEngineTests|FullyQualifiedName~SnapshotModelPinTests|FullyQualifiedName~OffscreenCullTests|FullyQualifiedName~PasswordRedactionTests|FullyQualifiedName~PopupGraftingTests"` → Expected: all green (output unchanged).

- [ ] **Step 8: Commit**
```bash
git add src/FlaUI.Mcp.Core/Perception/SnapshotNode.cs src/FlaUI.Mcp.Core/Perception/SnapshotEngine.cs test/FlaUI.Mcp.Tests/Perception/SnapshotModelPinTests.cs
git commit -m "refactor(perception): project SnapshotModel from Walk, render byte-identical (Task 0)"
```

---

### Task 1: Add the new error codes

**Files:** Modify `src/FlaUI.Mcp.Core/Errors/ToolErrorCode.cs`; Test `test/FlaUI.Mcp.Tests/Errors/ToolErrorCodeAdditionsTests.cs`

- [ ] **Step 1: STATE-VERIFY** — open `ToolErrorCode.cs`; confirm the enum ends at `Timeout` (L20) and that `InvalidArguments` is absent. If `InvalidArguments` already exists, drop it from the add-list below.

- [ ] **Step 2: Write the failing test**

`test/FlaUI.Mcp.Tests/Errors/ToolErrorCodeAdditionsTests.cs`:
```csharp
using FlaUI.Mcp.Core.Errors;
using Xunit;

namespace FlaUI.Mcp.Tests.Errors;

public class ToolErrorCodeAdditionsTests
{
    [Theory]
    [InlineData("CaptureUnavailable")]
    [InlineData("SnapshotNotFound")]
    [InlineData("SnapshotWindowMismatch")]
    [InlineData("SelectorNoMatch")]
    [InlineData("NoFocusedElement")]
    [InlineData("NotImplemented")]
    [InlineData("InvalidArguments")]
    public void New_codes_are_defined(string name)
        => Assert.True(Enum.IsDefined(typeof(ToolErrorCode), Enum.Parse<ToolErrorCode>(name)));
}
```

- [ ] **Step 3: Run — verify it fails** — Run: `dotnet test --filter "FullyQualifiedName~ToolErrorCodeAdditionsTests"` → Expected: FAIL (parse throws).

- [ ] **Step 4: Add the codes** — append after `Timeout` (L20):
```csharp
    Timeout,
    InvalidArguments,
    CaptureUnavailable,
    SnapshotNotFound,
    SnapshotWindowMismatch,
    SelectorNoMatch,
    NoFocusedElement,
    NotImplemented
```

- [ ] **Step 5: Run — verify it passes** — Expected: PASS (7 cases).

- [ ] **Step 6: Commit**
```bash
git add src/FlaUI.Mcp.Core/Errors/ToolErrorCode.cs test/FlaUI.Mcp.Tests/Errors/ToolErrorCodeAdditionsTests.cs
git commit -m "feat(errors): add perception-completion error codes (Task 1)"
```

---

### Task 2: `focused` descriptor flag (always-on)

**Files:** Modify `ElementDescriptor.cs`, `SnapshotEngine.cs` (Build reads `HasKeyboardFocus`, FormatNode renders `focused`); Test `test/FlaUI.Mcp.Tests/Perception/FocusedFlagTests.cs` (Desktop).

Renders inside the brace group after `focusable` (`{enabled, focusable, focused}`), omitted when false.

- [ ] **Step 1: Write the failing test**

`test/FlaUI.Mcp.Tests/Perception/FocusedFlagTests.cs`:
```csharp
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class FocusedFlagTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public FocusedFlagTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Focused_control_renders_the_focused_flag()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var tree = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            win.AsWindow().Focus();
            var ok = win.FindFirstDescendant(cf => cf.ByAutomationId("OkButton"));
            ok!.Focus();
            var refs = new RefRegistry(); refs.BeginSnapshot(handle.Id);
            var model = SnapshotEngine.Build(win, System.Array.Empty<AutomationElement>(), new SnapshotOptions(), refs, handle.Id);
            return SnapshotEngine.Render(model, new SnapshotOptions());
        });

        Assert.Contains("focused", tree);
    }
}
```

- [ ] **Step 2: Run — verify it fails** — Expected: FAIL (`focused` never rendered).

- [ ] **Step 3: Implement** — In `ElementDescriptor.cs`:
```csharp
public sealed record ElementDescriptor(
    IReadOnlyList<int> RuntimeId,
    ControlType ControlType,
    string AutomationId,
    string Name,
    string? AncestorAutomationId,
    IReadOnlyList<int> IndexPath,
    bool Focused = false);
```
(Defaulted so existing `new ElementDescriptor(...)` call sites compile unchanged.)

In `SnapshotEngine.Build`, inside the `include` block read focus and carry it onto both the descriptor and the node:
```csharp
bool focused = Safe(() => el.Properties.HasKeyboardFocus.ValueOrDefault, false);
var descriptor = new ElementDescriptor(
    RuntimeId: rid, ControlType: ct, AutomationId: aid, Name: name,
    AncestorAutomationId: ancestorAid, IndexPath: indexPath, Focused: focused);
```
and change the `new SnapshotNode(...)` to pass `Focused: focused` (replacing `Focused: false`).

In `FormatNode`, after the `focusable` line:
```csharp
if (n.Focusable) state.Add("focusable");
if (n.Focused) state.Add("focused");
```

- [ ] **Step 4: Run — verify it passes** — Run: `dotnet test --filter "FullyQualifiedName~FocusedFlagTests|FullyQualifiedName~SnapshotModelPinTests"` → Expected: PASS (pin stays valid — `Walk` now delegates to `Build`/`Render`, so both sides change together).

- [ ] **Step 5: Commit**
```bash
git add src/FlaUI.Mcp.Core/Perception/ElementDescriptor.cs src/FlaUI.Mcp.Core/Perception/SnapshotEngine.cs test/FlaUI.Mcp.Tests/Perception/FocusedFlagTests.cs
git commit -m "feat(perception): always-on focused descriptor flag (Task 2)"
```

---

### Task 3: `RefRegistry.Resolve` recycle-guard Name compare

**Files:** Modify `src/FlaUI.Mcp.Core/Perception/RefRegistry.cs`; Test `test/FlaUI.Mcp.Tests/Perception/RecycleGuardTests.cs` (Desktop).

Strengthen the existing fast-path (L83) so a same-ControlType RuntimeId-recycle whose live `Name` no longer matches the descriptor falls through to `ResolveDescriptor`.

- [ ] **Step 1: Write the failing test (predicate-level, strict)** — expose the guard predicate so the divergence is directly asserted.

`test/FlaUI.Mcp.Tests/Perception/RecycleGuardTests.cs`:
```csharp
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class RecycleGuardTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public RecycleGuardTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Fast_path_rejects_a_cached_element_whose_live_name_diverges()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var (matchesSame, matchesDiverged) = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            var status = win.FindFirstDescendant(cf => cf.ByAutomationId("Status"))!;
            var rid = status.Properties.RuntimeId.ValueOrDefault;
            var liveName = status.Name;
            var same = new ElementDescriptor(rid, status.ControlType, "Status", liveName, null, System.Array.Empty<int>());
            var diverged = new ElementDescriptor(rid, status.ControlType, "Status", liveName + "-X", null, System.Array.Empty<int>());
            return (RefRegistry.FastPathMatches(status, same), RefRegistry.FastPathMatches(status, diverged));
        });

        Assert.True(matchesSame);       // same identity + same Name → fast-path keeps the cache
        Assert.False(matchesDiverged);  // diverged Name → fall through to ResolveDescriptor
    }
}
```

- [ ] **Step 2: Run — verify it fails** — Expected: BUILD FAIL (`RefRegistry.FastPathMatches` missing).

- [ ] **Step 3: Implement** — In `RefRegistry.cs` add the predicate + a local `Safe`, and call it from `Resolve`:
```csharp
internal static bool FastPathMatches(AutomationElement cached, ElementDescriptor d)
{
    var rid = cached.Properties.RuntimeId.ValueOrDefault;
    return rid != null && rid.AsEnumerable().SequenceEqual(d.RuntimeId)
        && cached.ControlType == d.ControlType
        && !cached.Properties.IsOffscreen.ValueOrDefault
        && string.Equals(Safe(() => cached.Name, ""), d.Name, System.StringComparison.Ordinal);
}

private static T Safe<T>(Func<T> read, T fallback) { try { return read(); } catch { return fallback; } }
```
Replace the fast-path body (L80-86) with:
```csharp
if (entry.Cached is { } cached)
{
    try { if (FastPathMatches(cached, d)) return cached; }
    catch { /* element gone — fall through to the cache-free walk */ }
}
```
Add `using FlaUI.Core.AutomationElements;` if not already imported (it is, L1).

- [ ] **Step 4: Run — verify it passes** — Run: `dotnet test --filter "FullyQualifiedName~RecycleGuardTests|FullyQualifiedName~RefResolutionTests|FullyQualifiedName~RefRegistryTests"` → Expected: all PASS (existing resolution unaffected; the Name check only adds a fall-through on divergence).

- [ ] **Step 5: Commit**
```bash
git add src/FlaUI.Mcp.Core/Perception/RefRegistry.cs test/FlaUI.Mcp.Tests/Perception/RecycleGuardTests.cs
git commit -m "fix(perception): strengthen ref recycle guard with live Name compare (Task 3)"
```

---

### Task 4: `BuildModelAsync` + `SnapshotCache` (model store by snapshotId)

**Files:** Create `src/FlaUI.Mcp.Core/Perception/SnapshotCache.cs`; Modify `PerceptionManager.cs`, `Program.cs`, `SnapshotToolsTests.cs`; Test `test/FlaUI.Mcp.Tests/Perception/SnapshotCacheTests.cs` (Desktop).

`stats(snapshotId)` and `diff(baseline)` need a prior snapshot's model. Cache models by snapshotId (bounded LRU). `SnapshotAsync` builds once, caches, renders.

- [ ] **Step 1: Write the failing test**

`test/FlaUI.Mcp.Tests/Perception/SnapshotCacheTests.cs`:
```csharp
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class SnapshotCacheTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public SnapshotCacheTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Snapshot_caches_its_model_retrievable_by_id()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var cache = new SnapshotCache();
        var perception = new PerceptionManager(mgr, new RefRegistry(), cache);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var r = await perception.SnapshotAsync(handle, new SnapshotOptions());
        Assert.True(cache.TryGet(r.SnapshotId, out var model));
        Assert.True(model!.NodeCount > 0);
        Assert.False(cache.TryGet("w999:1", out _));
    }
}
```

- [ ] **Step 2: Run — verify it fails** — Expected: BUILD FAIL (`SnapshotCache` missing; ctor arity).

- [ ] **Step 3: Create `SnapshotCache`**

`src/FlaUI.Mcp.Core/Perception/SnapshotCache.cs`:
```csharp
namespace FlaUI.Mcp.Core.Perception;

/// <summary>Bounded store of recent snapshot models keyed by snapshotId (e.g. "w1:4"). Lets
/// snapshot_stats(snapshotId) and snapshot_diff(baseline) read a prior snapshot without re-walking.
/// Insertion-order LRU; thread-safe.</summary>
public sealed class SnapshotCache
{
    private const int Capacity = 32;
    private readonly object _gate = new();
    private readonly LinkedList<string> _order = new();
    private readonly Dictionary<string, SnapshotModel> _byId = new();

    public void Put(string snapshotId, SnapshotModel model)
    {
        lock (_gate)
        {
            if (_byId.ContainsKey(snapshotId)) _order.Remove(snapshotId);
            _byId[snapshotId] = model;
            _order.AddFirst(snapshotId);
            while (_order.Count > Capacity)
            {
                var evict = _order.Last!.Value;
                _order.RemoveLast();
                _byId.Remove(evict);
            }
        }
    }

    public bool TryGet(string snapshotId, out SnapshotModel? model)
    {
        lock (_gate) { return _byId.TryGetValue(snapshotId, out model); }
    }
}
```

- [ ] **Step 4: Wire `PerceptionManager`** — replace the fields/ctor (L11-18) and `SnapshotAsync` (L59-78):
```csharp
private readonly WindowManager _windows;
private readonly RefRegistry _refs;
private readonly SnapshotCache _cache;

public PerceptionManager(WindowManager windows, RefRegistry refs, SnapshotCache cache)
{
    _windows = windows;
    _refs = refs;
    _cache = cache;
}
```
Add the model primitive and rewrite `SnapshotAsync`:
```csharp
/// <summary>Build the projected model on the query STA using the supplied registry (the real one
/// for a durable snapshot, or a throwaway for transient wait polls). Applies the same denylist +
/// root-ref resolution as SnapshotAsync.</summary>
public Task<(string SnapshotId, SnapshotModel Model)> BuildModelAsync(
    WindowHandle handle, SnapshotOptions options, RefRegistry refs) =>
    _windows.RunWithWindowAndDesktopAsync(handle, (win, desktop) =>
    {
        var procName = SafeProcessName(win);
        if (PerceptionPolicy.IsDenied(procName))
            throw new ToolException(ToolErrorCode.TargetDenied,
                $"Snapshotting windows owned by '{procName}' is blocked (credential store).",
                "snapshot a different, non-sensitive window");

        IReadOnlyList<AutomationElement> popups = PopupFinder.FindOwnerPopups(desktop, win);
        AutomationElement root = string.IsNullOrEmpty(options.RootRef)
            ? win
            : refs.Resolve(handle.Id, options.RootRef!, PopupFinder.SearchRoots(win, desktop));
        var snapshotId = refs.BeginSnapshot(handle.Id);
        var model = SnapshotEngine.Build(root, popups, options, refs, handle.Id);
        return (snapshotId, model);
    });

public async Task<SnapshotResult> SnapshotAsync(WindowHandle handle, SnapshotOptions options)
{
    var (snapshotId, model) = await BuildModelAsync(handle, options, _refs);
    _cache.Put(snapshotId, model);
    return new SnapshotResult(snapshotId, SnapshotEngine.Render(model, options), model.NodeCount);
}
```
Keep `RunOnRefAsync`, `RunOnRefActionAsync`, `SafeProcessName`.

- [ ] **Step 5: DI + fix existing ctor call sites** — In `Program.cs` after the `RefRegistry` line (L28) add:
```csharp
builder.Services.AddSingleton<FlaUI.Mcp.Core.Perception.SnapshotCache>();
```
Update `SnapshotToolsTests.cs:22` and `:41` from `new PerceptionManager(mgr, refs)` / `new PerceptionManager(mgr, new RefRegistry())` to pass `new SnapshotCache()` as the third arg.

- [ ] **Step 6: Run — verify it passes** — Run: `dotnet test --filter "FullyQualifiedName~SnapshotCacheTests|FullyQualifiedName~SnapshotToolsTests"` and `dotnet test --filter "Category!=Desktop"` → Expected: all PASS.

- [ ] **Step 7: Commit**
```bash
git add src/FlaUI.Mcp.Core/Perception/SnapshotCache.cs src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs src/FlaUI.Mcp.Server/Program.cs test/FlaUI.Mcp.Tests/Server/SnapshotToolsTests.cs test/FlaUI.Mcp.Tests/Perception/SnapshotCacheTests.cs
git commit -m "feat(perception): SnapshotCache + BuildModelAsync model primitive (Task 4)"
```

---

### Task 5: `DpiHelper` + `desktop_get_bounds`

**Files:** Create `src/FlaUI.Mcp.Core/Perception/Geometry/DpiHelper.cs`, `src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs`; Modify `Program.cs`; Test `test/FlaUI.Mcp.Tests/Perception/GetBoundsTests.cs` (Desktop).

- [ ] **Step 1: Create `DpiHelper`**

`src/FlaUI.Mcp.Core/Perception/Geometry/DpiHelper.cs`:
```csharp
using System.Runtime.InteropServices;

namespace FlaUI.Mcp.Core.Perception.Geometry;

/// <summary>Effective per-monitor DPI scale for a screen point. Informational only (design §2):
/// bounds are already physical px, so dpiScale is metadata, never load-bearing for targeting.</summary>
public static class DpiHelper
{
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
    [DllImport("Shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    public static double ScaleForPoint(int x, int y)
    {
        try
        {
            var mon = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return 1.0;
            if (GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out uint dpiX, out _) != 0) return 1.0;
            return dpiX <= 0 ? 1.0 : dpiX / 96.0;
        }
        catch { return 1.0; }
    }
}
```

- [ ] **Step 2: Write the failing test**

`test/FlaUI.Mcp.Tests/Perception/GetBoundsTests.cs`:
```csharp
using System.Linq;
using System.Text.Json;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class GetBoundsTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public GetBoundsTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Get_bounds_matches_snapshot_bounds_for_Ok_button()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var tools = new ScreenshotTools(perception);
        var window = new WindowTools(mgr, perception);

        var opened = await window.DesktopOpenWindow("pid", _app.Process.Id.ToString());
        var handle = JsonDocument.Parse(opened).RootElement.GetProperty("handle").GetString()!;

        var snapJson = await new SnapshotTools(perception, new WaitCoordinator(perception)).DesktopSnapshot(handle, fullProperties: true);
        var tree = JsonDocument.Parse(snapJson).RootElement.GetProperty("tree").GetString()!;
        var line = tree.Split('\n').First(l => l.Contains("aid=OkButton"));
        var @ref = line[(line.IndexOf('[') + 1)..line.IndexOf(']')];

        var json = await tools.DesktopGetBounds(handle, @ref);
        using var doc = JsonDocument.Parse(json);
        var b = doc.RootElement.GetProperty("bounds");
        Assert.True(b.GetProperty("w").GetInt32() > 0);
        Assert.True(b.GetProperty("h").GetInt32() > 0);
        Assert.True(doc.RootElement.GetProperty("dpiScale").GetDouble() > 0);
        Assert.False(doc.RootElement.GetProperty("isOffscreen").GetBoolean());
    }
}
```
(This test depends on the Task-8 `WindowTools(mgr, perception)` ctor and the Task-9 `SnapshotTools(perception, wait)` ctor. **Sequencing note:** GetBounds is functionally independent, but its TEST constructs those types. To keep tests compiling at Task 5, use the ctors as they exist NOW: `new WindowTools(mgr)` and `new SnapshotTools(perception)`. The subagent executing Task 5 MUST use the current ctors; Tasks 8/9 will update this test's constructor calls when they change those ctors. STATE-VERIFY current ctor arity before writing the test.)

- [ ] **Step 3: Run — verify it fails** — Expected: BUILD FAIL (`ScreenshotTools`/`DesktopGetBounds` missing).

- [ ] **Step 4: Implement** — `src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs`:
```csharp
using System.ComponentModel;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Perception.Geometry;
using FlaUI.Mcp.Core.Windows;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Tools;

[McpServerToolType]
public sealed class ScreenshotTools
{
    private readonly PerceptionManager _perception;
    public ScreenshotTools(PerceptionManager perception) => _perception = perception;

    [McpServerTool(ReadOnly = true), Description("Get an element's absolute physical-pixel screen bounds {x,y,w,h} (signed, multi-monitor safe), its monitor dpiScale (informational), and isOffscreen (UIA scrolled/virtualized-out, NOT occlusion).")]
    public Task<string> DesktopGetBounds(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Element ref from a snapshot, e.g. e23.")] string @ref)
        => ToolResponse.Guard(async () =>
        {
            var r = await _perception.RunOnRefAsync(new WindowHandle(window), @ref, el =>
            {
                var rect = el.BoundingRectangle;
                return (rect.X, rect.Y, rect.Width, rect.Height, el.Properties.IsOffscreen.ValueOrDefault);
            });
            var dpi = DpiHelper.ScaleForPoint(r.X, r.Y);
            return ToolResponse.Ok(new
            {
                bounds = new { x = r.X, y = r.Y, w = r.Width, h = r.Height },
                dpiScale = dpi,
                isOffscreen = r.Item5
            });
        });
}
```

- [ ] **Step 5: DI** — In `Program.cs` after `SnapshotTools` (L30): `builder.Services.AddSingleton<ScreenshotTools>();`.

- [ ] **Step 6: Run — verify it passes** — Run: `dotnet test --filter "FullyQualifiedName~GetBoundsTests"` → Expected: PASS.

- [ ] **Step 7: Commit**
```bash
git add src/FlaUI.Mcp.Core/Perception/Geometry/DpiHelper.cs src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs src/FlaUI.Mcp.Server/Program.cs test/FlaUI.Mcp.Tests/Perception/GetBoundsTests.cs
git commit -m "feat(perception): DpiHelper + desktop_get_bounds (Task 5)"
```

---

### Task 6: `desktop_snapshot_stats`

**Files:** Modify `SnapshotEngine.cs` (`IsInteractiveNode`), `PerceptionManager.cs` (stats helper), `SnapshotTools.cs` (tool); Test `test/FlaUI.Mcp.Tests/Server/SnapshotStatsTests.cs` (Desktop).

`window` form walks a FULL tree (`InteractiveOnly=false, IncludeOffscreen=true`) so offscreen/total are real; `snapshotId` form tallies the cached model as-snapshotted. Exactly one of `window`/`snapshotId`.

- [ ] **Step 1: Expose interactive predicate** — In `SnapshotEngine.cs`:
```csharp
public static bool IsInteractiveNode(SnapshotNode n)
    => InteractiveTypes.Contains(n.ControlType)
       || (n.ControlType == ControlType.Text && !string.IsNullOrWhiteSpace(n.Name))
       || n.Focusable
       || n.Patterns.Count > 0;
```

- [ ] **Step 2: Stats helper on `PerceptionManager`**:
```csharp
public sealed record SnapshotStats(string SnapshotId, int Total, int Interactive, int Offscreen,
    int Redacted, IReadOnlyDictionary<string, int> ByControlType);

private static SnapshotStats Tally(string snapshotId, SnapshotModel model)
{
    var nodes = model.Nodes.ToList();
    return new SnapshotStats(
        snapshotId,
        Total: nodes.Count,
        Interactive: nodes.Count(SnapshotEngine.IsInteractiveNode),
        Offscreen: nodes.Count(n => n.IsOffscreen),
        Redacted: nodes.Count(n => n.IsPassword),
        ByControlType: nodes.GroupBy(n => n.ControlType.ToString()).ToDictionary(g => g.Key, g => g.Count()));
}

public async Task<SnapshotStats> StatsByWindowAsync(WindowHandle handle)
{
    var opts = new SnapshotOptions { InteractiveOnly = false, IncludeOffscreen = true };
    var (snapshotId, model) = await BuildModelAsync(handle, opts, _refs);
    _cache.Put(snapshotId, model);
    return Tally(snapshotId, model);
}

public SnapshotStats StatsBySnapshotId(string snapshotId)
{
    if (!_cache.TryGet(snapshotId, out var model) || model is null)
        throw new ToolException(ToolErrorCode.SnapshotNotFound,
            $"Snapshot '{snapshotId}' is not in the cache (evicted or never taken).",
            "take a fresh desktop_snapshot and use its snapshotId");
    return Tally(snapshotId, model);
}
```

- [ ] **Step 3: Write the failing test**

`test/FlaUI.Mcp.Tests/Server/SnapshotStatsTests.cs`:
```csharp
using System.Text.Json;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

[Trait("Category", "Desktop")]
public class SnapshotStatsTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public SnapshotStatsTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Stats_by_window_counts_controls_and_redactions()
    {
        var (snap, handle) = await Setup();
        var json = await snap.DesktopSnapshotStats(handle, null);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("total").GetInt32() > 0);
        Assert.True(doc.RootElement.GetProperty("interactive").GetInt32() > 0);
        Assert.Equal(1, doc.RootElement.GetProperty("redacted").GetInt32()); // the Secret PasswordBox
        Assert.True(doc.RootElement.GetProperty("byControlType").TryGetProperty("Button", out _));
    }

    [Fact]
    public async Task Stats_requires_exactly_one_of_window_or_snapshotId()
    {
        var (snap, _) = await Setup();
        var json = await snap.DesktopSnapshotStats(null, null);
        Assert.Equal("InvalidArguments", JsonDocument.Parse(json).RootElement.GetProperty("error").GetString());
    }

    private async Task<(SnapshotTools, string)> Setup()
    {
        var dispatcher = new AutomationDispatcher();
        var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var snap = new SnapshotTools(perception, new WaitCoordinator(perception)); // see Task 9 ctor note
        var window = new WindowTools(mgr, perception); // see Task 8 ctor note
        var opened = await window.DesktopOpenWindow("pid", _app.Process.Id.ToString());
        var handle = JsonDocument.Parse(opened).RootElement.GetProperty("handle").GetString()!;
        return (snap, handle);
    }
}
```
(Ctor note: if executed before Tasks 8/9, use `new WindowTools(mgr)` and `new SnapshotTools(perception)`; the later tasks update these call sites. STATE-VERIFY current arity.)

- [ ] **Step 4: Run — verify it fails** — Expected: BUILD FAIL (`DesktopSnapshotStats` missing).

- [ ] **Step 5: Add the tool to `SnapshotTools.cs`** (add `using FlaUI.Mcp.Core.Errors;`):
```csharp
[McpServerTool(ReadOnly = true), Description("Cheap orientation: control counts (total/interactive/offscreen/redacted) and a per-ControlType histogram, WITHOUT the full tree. Supply exactly one of window (fresh full walk) or snapshotId (a prior cached snapshot).")]
public Task<string> DesktopSnapshotStats(
    [Description("Window handle, e.g. w1. Provide this OR snapshotId, not both.")] string? window = null,
    [Description("A prior snapshotId, e.g. w1:4. Provide this OR window, not both.")] string? snapshotId = null)
    => ToolResponse.Guard(async () =>
    {
        if (string.IsNullOrEmpty(window) == string.IsNullOrEmpty(snapshotId))
            throw new ToolException(ToolErrorCode.InvalidArguments,
                "Provide exactly one of 'window' or 'snapshotId'.", "pass a window handle or a snapshotId");
        var s = string.IsNullOrEmpty(window)
            ? _perception.StatsBySnapshotId(snapshotId!)
            : await _perception.StatsByWindowAsync(new WindowHandle(window!));
        return ToolResponse.Ok(new
        {
            snapshotId = s.SnapshotId, total = s.Total, interactive = s.Interactive,
            offscreen = s.Offscreen, redacted = s.Redacted, byControlType = s.ByControlType
        });
    });
```

- [ ] **Step 6: Run — verify it passes** — Run: `dotnet test --filter "FullyQualifiedName~SnapshotStatsTests"` → Expected: PASS (2).

- [ ] **Step 7: Commit**
```bash
git add src/FlaUI.Mcp.Core/Perception/SnapshotEngine.cs src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs test/FlaUI.Mcp.Tests/Server/SnapshotStatsTests.cs
git commit -m "feat(perception): desktop_snapshot_stats (Task 6)"
```

---

### Task 7: `desktop_snapshot_diff`

**Files:** Create `src/FlaUI.Mcp.Core/Perception/SnapshotDiff.cs`; Modify `PerceptionManager.cs`, `SnapshotTools.cs`; Test `test/FlaUI.Mcp.Tests/Server/SnapshotDiffTests.cs` (Desktop).

Explicit `baselineSnapshotId` (required). Identity key = RuntimeId else `(ControlType, AutomationId, Name)`. `changed` = same identity, differing Name/Value/Enabled/Focused. Refs in the result come from `currentSnapshotId`.

> **Oracle (design §3.5 + §4):** `changed.was/now` includes `name,value,enabled` (§3.5) and a `focused` transition counts as a change (§4). `value` reads ValuePattern only (LegacyIAccessible fallback not modeled; non-ValuePattern controls compare null==null).

- [ ] **Step 1: Create the diff core**

`src/FlaUI.Mcp.Core/Perception/SnapshotDiff.cs`:
```csharp
namespace FlaUI.Mcp.Core.Perception;

public sealed record NodeState(string Name, string? Value, bool Enabled, bool Focused);
public sealed record DiffDescriptor(string Ref, string ControlType, string AutomationId, string Name);
public sealed record ChangedEntry(string Ref, NodeState Was, NodeState Now);
public sealed record SnapshotDiffResult(
    string BaselineSnapshotId, string CurrentSnapshotId,
    IReadOnlyList<DiffDescriptor> Added,
    IReadOnlyList<DiffDescriptor> Removed,
    IReadOnlyList<ChangedEntry> Changed);

public static class SnapshotDiff
{
    private static string Identity(SnapshotNode n)
        => n.RuntimeId.Count > 0
            ? "rid:" + string.Join(",", n.RuntimeId)
            : $"key:{n.ControlType}|{n.AutomationId}|{n.Name}";

    private static DiffDescriptor Desc(SnapshotNode n) => new(n.Ref, n.ControlType.ToString(), n.AutomationId, n.Name);
    private static NodeState State(SnapshotNode n) => new(n.Name, n.Value, n.Enabled, n.Focused);

    public static SnapshotDiffResult Compute(string baselineId, SnapshotModel baseline,
        string currentId, SnapshotModel current)
    {
        var baseById = new Dictionary<string, SnapshotNode>();
        foreach (var n in baseline.Nodes) baseById[Identity(n)] = n;
        var curById = new Dictionary<string, SnapshotNode>();
        foreach (var n in current.Nodes) curById[Identity(n)] = n;

        var added = current.Nodes.Where(n => !baseById.ContainsKey(Identity(n))).Select(Desc).ToList();
        var removed = baseline.Nodes.Where(n => !curById.ContainsKey(Identity(n))).Select(Desc).ToList();
        var changed = new List<ChangedEntry>();
        foreach (var n in current.Nodes)
        {
            if (!baseById.TryGetValue(Identity(n), out var b)) continue;
            var was = State(b); var now = State(n);
            if (was != now) changed.Add(new ChangedEntry(n.Ref, was, now));
        }
        return new SnapshotDiffResult(baselineId, currentId, added, removed, changed);
    }
}
```

- [ ] **Step 2: `PerceptionManager.DiffAsync`**:
```csharp
public async Task<SnapshotDiffResult> DiffAsync(WindowHandle handle, string baselineSnapshotId)
{
    if (!_cache.TryGet(baselineSnapshotId, out var baseline) || baseline is null)
        throw new ToolException(ToolErrorCode.SnapshotNotFound,
            $"Baseline snapshot '{baselineSnapshotId}' is not in the cache.", "re-take the baseline snapshot");
    var baseWindowId = baselineSnapshotId.Split(':')[0];
    if (!string.Equals(baseWindowId, handle.Id, System.StringComparison.Ordinal))
        throw new ToolException(ToolErrorCode.SnapshotWindowMismatch,
            $"Baseline '{baselineSnapshotId}' belongs to window '{baseWindowId}', not '{handle.Id}'.",
            "pass a baselineSnapshotId taken from the same window");
    var (currentId, current) = await BuildModelAsync(handle, new SnapshotOptions(), _refs);
    _cache.Put(currentId, current);
    return SnapshotDiff.Compute(baselineSnapshotId, baseline, currentId, current);
}
```
**Window-mismatch ordering:** `SnapshotNotFound` is checked first (a foreign baseline that was never cached for this process surfaces as not-found). `SnapshotWindowMismatch` triggers only when the baseline IS cached but its windowId prefix differs from the diff target — e.g. two windows of the same server session. The test below exercises the not-found path; the mismatch path is covered by a unit-level test in Step 3.

- [ ] **Step 3: Write the failing test**

`test/FlaUI.Mcp.Tests/Server/SnapshotDiffTests.cs`:
```csharp
using System.Linq;
using System.Text.Json;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

[Trait("Category", "Desktop")]
public class SnapshotDiffTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public SnapshotDiffTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Diff_detects_a_changed_status_label_after_invoke()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var snap = new SnapshotTools(perception, new WaitCoordinator(perception));
        var inter = new InteractionTools(perception, mgr, ServerOptions.FromArgs(System.Array.Empty<string>()));
        var window = new WindowTools(mgr, perception);
        var opened = await window.DesktopOpenWindow("pid", _app.Process.Id.ToString());
        var handle = JsonDocument.Parse(opened).RootElement.GetProperty("handle").GetString()!;

        var baseJson = await snap.DesktopSnapshot(handle, fullProperties: true);
        var baselineId = JsonDocument.Parse(baseJson).RootElement.GetProperty("snapshotId").GetString()!;
        var tree = JsonDocument.Parse(baseJson).RootElement.GetProperty("tree").GetString()!;
        var okRef = RefFor(tree, "aid=OkButton");

        await inter.DesktopInvoke(handle, okRef); // sets Status.Text = "clicked: ..."

        var diffJson = await snap.DesktopSnapshotDiff(handle, baselineId);
        using var doc = JsonDocument.Parse(diffJson);
        Assert.Equal(baselineId, doc.RootElement.GetProperty("baselineSnapshotId").GetString());
        Assert.True(doc.RootElement.GetProperty("changed").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Diff_rejects_a_missing_baseline()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var snap = new SnapshotTools(perception, new WaitCoordinator(perception));
        var window = new WindowTools(mgr, perception);
        var opened = await window.DesktopOpenWindow("pid", _app.Process.Id.ToString());
        var handle = JsonDocument.Parse(opened).RootElement.GetProperty("handle").GetString()!;
        var json = await snap.DesktopSnapshotDiff(handle, handle + ":999");
        Assert.Equal("SnapshotNotFound", JsonDocument.Parse(json).RootElement.GetProperty("error").GetString());
    }

    private static string RefFor(string tree, string needle)
    {
        var line = tree.Split('\n').First(l => l.Contains(needle));
        return line[(line.IndexOf('[') + 1)..line.IndexOf(']')];
    }
}
```
(STATE-VERIFY `ServerOptions.FromArgs` exists and returns `ServerOptions` — confirmed referenced in `Program.cs:22`. Adjust if the API differs.)

- [ ] **Step 4: Run — verify it fails** — Expected: BUILD FAIL (`DesktopSnapshotDiff` missing).

- [ ] **Step 5: Add the tool to `SnapshotTools.cs`**:
```csharp
[McpServerTool(ReadOnly = true), Description("Diff a window's CURRENT tree against an explicit baseline snapshotId. Returns added/removed/changed (Name/Value/Enabled/Focused) keyed by identity (RuntimeId, else ControlType+AutomationId+Name). Refs in the result belong to the new currentSnapshotId.")]
public Task<string> DesktopSnapshotDiff(
    [Description("Window handle, e.g. w1.")] string window,
    [Description("The REQUIRED baseline snapshotId to diff against, e.g. w1:2.")] string baselineSnapshotId)
    => ToolResponse.Guard(async () =>
    {
        var d = await _perception.DiffAsync(new WindowHandle(window), baselineSnapshotId);
        return ToolResponse.Ok(new
        {
            baselineSnapshotId = d.BaselineSnapshotId,
            currentSnapshotId = d.CurrentSnapshotId,
            added = d.Added.Select(a => new { @ref = a.Ref, controlType = a.ControlType, automationId = a.AutomationId, name = a.Name }),
            removed = d.Removed.Select(a => new { @ref = a.Ref, controlType = a.ControlType, automationId = a.AutomationId, name = a.Name }),
            changed = d.Changed.Select(c => new { @ref = c.Ref, was = c.Was, now = c.Now })
        });
    });
```

- [ ] **Step 6: Run — verify it passes** — Run: `dotnet test --filter "FullyQualifiedName~SnapshotDiffTests"` → Expected: PASS (2).

- [ ] **Step 7: Commit**
```bash
git add src/FlaUI.Mcp.Core/Perception/SnapshotDiff.cs src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs test/FlaUI.Mcp.Tests/Server/SnapshotDiffTests.cs
git commit -m "feat(perception): desktop_snapshot_diff with explicit baseline (Task 7)"
```

---

### Task 8: `desktop_list_windows` extension (`includeBounds`/`includeStats`/`zOrder`)

**Files:** Modify `WindowManager.cs` (richer list + `RunQueryAsync` passthrough), `WindowTools.cs` (params + PerceptionManager dep), `WindowToolsTests.cs`; Test `test/FlaUI.Mcp.Tests/Server/ListWindowsExtensionTests.cs` (Desktop).

Default output unchanged (back-compat). `includeBounds` adds `bounds` (`GetWindowRect`) + `zOrder` (EnumWindows index). `includeStats` adds per-window `stats` (best-effort; null+note on failure).

- [ ] **Step 1: Richer list on `WindowManager`** — extend `WindowInfo` (existing fields first for serialization back-compat) and add the bounds/zorder list method + a query passthrough used later by ScreenCapture:
```csharp
public sealed record WindowBounds(int X, int Y, int W, int H);
public sealed record WindowInfo(string Title, string ProcessName, int Pid, bool IsForeground,
    WindowBounds? Bounds = null, int? ZOrder = null);
```
Add P/Invoke next to the others (after L142):
```csharp
[StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
[DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
```
Add the overload + keep the parameterless one delegating, and a generic query passthrough:
```csharp
public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync() => ListWindowsAsync(false);

public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(bool includeBounds) =>
    _dispatcher.RunQueryAsync<IReadOnlyList<WindowInfo>>(() =>
    {
        var foreground = GetForegroundWindow();
        var list = new List<WindowInfo>();
        int z = 0;
        foreach (var (hwnd, title, pid) in EnumTopLevel())
        {
            WindowBounds? b = null;
            if (includeBounds && GetWindowRect(hwnd, out var r))
                b = new WindowBounds(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            list.Add(new WindowInfo(title, SafeProcessName(pid), pid, hwnd == foreground,
                b, includeBounds ? z : (int?)null));
            z++;
        }
        return list;
    });

/// <summary>Run an arbitrary read on the query STA (used by full-desktop capture, which needs an STA
/// hop without resolving a specific window).</summary>
public Task<T> RunOnQueryAsync<T>(Func<T> func) => _dispatcher.RunQueryAsync(func);
```
Replace the existing `ListWindowsAsync()` body (L30-40) — its logic now lives in `ListWindowsAsync(bool)`.

- [ ] **Step 2: Write the failing test**

`test/FlaUI.Mcp.Tests/Server/ListWindowsExtensionTests.cs`:
```csharp
using System.Linq;
using System.Text.Json;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

[Trait("Category", "Desktop")]
public class ListWindowsExtensionTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public ListWindowsExtensionTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Default_list_has_no_bounds_but_includeBounds_adds_them()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var tools = new WindowTools(mgr, perception);

        var plain = await tools.DesktopListWindows();
        Assert.DoesNotContain("\"bounds\"", plain);

        var rich = await tools.DesktopListWindows(includeBounds: true);
        using var doc = JsonDocument.Parse(rich);
        var any = doc.RootElement.EnumerateArray().First();
        Assert.True(any.TryGetProperty("bounds", out _));
        Assert.True(any.TryGetProperty("zOrder", out _));
    }
}
```

- [ ] **Step 3: Run — verify it fails** — Expected: BUILD FAIL (`WindowTools` ctor; `DesktopListWindows(bool)`).

- [ ] **Step 4: Implement `WindowTools` extension** — replace fields/ctor + `DesktopListWindows`:
```csharp
using FlaUI.Mcp.Core.Perception;
// ...
private readonly WindowManager _windows;
private readonly PerceptionManager _perception;
public WindowTools(WindowManager windows, PerceptionManager perception)
{ _windows = windows; _perception = perception; }

[McpServerTool(ReadOnly = true), Description("List top-level desktop windows (title, process, pid, isForeground). Opt-in: includeBounds adds absolute physical-px bounds + zOrder (0=topmost, for occlusion reasoning); includeStats adds a per-window control-count stats block (SLOW — walks each window; null+note on failure).")]
public Task<string> DesktopListWindows(
    [Description("Add bounds + zOrder to each window (default false).")] bool includeBounds = false,
    [Description("Add a per-window stats block — SLOW, walks each window (default false).")] bool includeStats = false)
    => ToolResponse.Guard(async () =>
    {
        var windows = await _windows.ListWindowsAsync(includeBounds || includeStats);
        if (!includeStats && !includeBounds)
            return ToolResponse.Ok(windows.Select(w => new { title = w.Title, processName = w.ProcessName, pid = w.Pid, isForeground = w.IsForeground }));
        if (!includeStats)
            return ToolResponse.Ok(windows.Select(w => new { title = w.Title, processName = w.ProcessName, pid = w.Pid, isForeground = w.IsForeground, bounds = w.Bounds, zOrder = w.ZOrder }));

        var enriched = new List<object>();
        foreach (var w in windows)
        {
            object? stats = null; string? note = null;
            try
            {
                var handle = await _windows.OpenByPidAsync(w.Pid);
                var s = await _perception.StatsByWindowAsync(handle);
                stats = new { total = s.Total, interactive = s.Interactive, offscreen = s.Offscreen, redacted = s.Redacted };
            }
            catch (System.Exception ex) { note = ex.Message; }
            enriched.Add(new { title = w.Title, processName = w.ProcessName, pid = w.Pid, isForeground = w.IsForeground, bounds = w.Bounds, zOrder = w.ZOrder, stats, note });
        }
        return ToolResponse.Ok(enriched);
    });
```

- [ ] **Step 5: Fix existing call sites** — In `WindowToolsTests.cs`, update any `new WindowTools(mgr)` to `new WindowTools(mgr, new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache()))`. (DI in `Program.cs` resolves the new ctor automatically — both singletons are registered.)

- [ ] **Step 6: Run — verify it passes** — Run: `dotnet test --filter "FullyQualifiedName~ListWindowsExtensionTests|FullyQualifiedName~WindowToolsTests"` → Expected: all PASS (default output unchanged).

- [ ] **Step 7: Commit**
```bash
git add src/FlaUI.Mcp.Core/Windows/WindowManager.cs src/FlaUI.Mcp.Server/Tools/WindowTools.cs test/FlaUI.Mcp.Tests/Server/ListWindowsExtensionTests.cs test/FlaUI.Mcp.Tests/Server/WindowToolsTests.cs
git commit -m "feat(perception): desktop_list_windows includeBounds/includeStats/zOrder (Task 8)"
```

---

### Task 9: `desktop_wait_for` + `WaitCoordinator`

**Files:** Create `src/FlaUI.Mcp.Core/Perception/WaitCoordinator.cs`; Modify `PerceptionManager.cs`, `SnapshotTools.cs`, `Program.cs`, TestApp (delayed-reveal control), and all existing `new SnapshotTools(perception)` call sites; Test `test/FlaUI.Mcp.Tests/Server/WaitForTests.cs` (Desktop).

Selector predicate against a FRESH full tree each poll using a **throwaway `RefRegistry`** (no durable-registry growth). Timeout ⇒ `{satisfied:false}` (never throws). On satisfaction, one durable snapshot issues the returned ref + snapshotId.

- [ ] **Step 1: Durable wait-snapshot helper on `PerceptionManager`**:
```csharp
public async Task<(string SnapshotId, SnapshotModel Model)> SnapshotModelForWaitAsync(
    WindowHandle handle, SnapshotOptions options)
{
    var (snapshotId, model) = await BuildModelAsync(handle, options, _refs);
    _cache.Put(snapshotId, model);
    return (snapshotId, model);
}
```

- [ ] **Step 2: Create `WaitCoordinator`**

`src/FlaUI.Mcp.Core/Perception/WaitCoordinator.cs`:
```csharp
using System.Diagnostics;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Core.Perception;

public sealed record WaitForResult(bool Satisfied, string? Ref, int ElapsedMs, string? SnapshotId);
public sealed record WaitStableResult(bool Stable, int ElapsedMs, string? SnapshotId);

/// <summary>Polling read-only wait conditions. Each poll issues ONE short query-STA Build with a
/// THROWAWAY RefRegistry (no growth on the durable registry) and Task.Delays off-STA between polls.
/// Timeouts are normal results, never exceptions.</summary>
public sealed class WaitCoordinator
{
    private readonly PerceptionManager _perception;
    public WaitCoordinator(PerceptionManager perception) => _perception = perception;

    internal static bool Matches(SnapshotNode n, string by, string value) => by switch
    {
        "automationId" => string.Equals(n.AutomationId, value, System.StringComparison.Ordinal),
        "name" => string.Equals(n.Name, value, System.StringComparison.Ordinal),
        "controlType" => string.Equals(n.ControlType.ToString(), value, System.StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    public async Task<WaitForResult> WaitForAsync(WindowHandle handle, string by, string value,
        string until, string? equals, int timeoutMs, int pollIntervalMs)
    {
        if (until == "valueEquals" && equals is null)
            throw new ToolException(ToolErrorCode.InvalidArguments,
                "until:valueEquals requires 'equals'.", "pass equals=<expected value>");

        var sw = Stopwatch.StartNew();
        var fullTree = new SnapshotOptions { InteractiveOnly = false, IncludeOffscreen = true };
        while (true)
        {
            var (_, model) = await _perception.BuildModelAsync(handle, fullTree, new RefRegistry());
            var match = model.Nodes.FirstOrDefault(n => Matches(n, by, value));
            bool satisfied = until switch
            {
                "exists" => match is not null,
                "gone" => match is null,
                "enabled" => match is { Enabled: true },
                "valueEquals" => match is not null && string.Equals(match.Value ?? match.Name, equals, System.StringComparison.Ordinal),
                _ => match is not null
            };
            if (satisfied)
            {
                var (snapId, real) = await _perception.SnapshotModelForWaitAsync(handle, fullTree);
                var realMatch = real.Nodes.FirstOrDefault(n => Matches(n, by, value));
                return new WaitForResult(true, realMatch?.Ref, (int)sw.ElapsedMilliseconds, snapId);
            }
            if (sw.ElapsedMilliseconds >= timeoutMs)
                return new WaitForResult(false, null, (int)sw.ElapsedMilliseconds, null);
            await Task.Delay(pollIntervalMs);
        }
    }
}
```

- [ ] **Step 3: TestApp delayed-reveal control** — In `MainWindow.xaml`, give the root `<StackPanel ... x:Name="RootPanel">` a name (add `x:Name="RootPanel"` to the `StackPanel` at L6) and after `FreezeButton` (L53) add:
```xml
<Button x:Name="DelayRevealButton" AutomationProperties.AutomationId="DelayRevealButton"
        Content="reveal after delay" Click="DelayRevealButton_Click" Margin="0,4,0,0"/>
```
In `MainWindow.xaml.cs` after `FreezeButton_Click`:
```csharp
// Adds a NEW labeled control ~600ms after click — exercises wait_for(until:exists).
private void DelayRevealButton_Click(object sender, RoutedEventArgs e)
{
    var timer = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(600) };
    timer.Tick += (_, _) =>
    {
        timer.Stop();
        var tb = new System.Windows.Controls.TextBlock { Text = "delayed" };
        System.Windows.Automation.AutomationProperties.SetAutomationId(tb, "DelayedLabel");
        RootPanel.Children.Add(tb);
    };
    timer.Start();
}
```

- [ ] **Step 4: Add the `WaitCoordinator` to `SnapshotTools` + the tool, and migrate ctor call sites** — replace `SnapshotTools` fields/ctor:
```csharp
private readonly PerceptionManager _perception;
private readonly WaitCoordinator _wait;
public SnapshotTools(PerceptionManager perception, WaitCoordinator wait)
{ _perception = perception; _wait = wait; }
```
Add the tool:
```csharp
[McpServerTool(ReadOnly = true), Description("Poll a window until a selector condition holds. by=automationId|name|controlType, value=target, until=exists|enabled|gone|valueEquals (equals required for valueEquals). Timeout returns {satisfied:false} (NOT an error). On success returns the matched ref + a fresh snapshotId. Polls are transient (no ref growth).")]
public Task<string> DesktopWaitFor(
    [Description("Window handle, e.g. w1.")] string window,
    [Description("Selector kind: automationId|name|controlType.")] string by,
    [Description("Match target for 'by'.")] string value,
    [Description("Condition: exists|enabled|gone|valueEquals (default exists).")] string until = "exists",
    [Description("Required iff until=valueEquals: the expected value.")] string? equals = null,
    [Description("Total wait budget in ms (default 5000).")] int timeoutMs = 5000,
    [Description("Poll interval in ms (default 500).")] int pollIntervalMs = 500)
    => ToolResponse.Guard(async () =>
    {
        var r = await _wait.WaitForAsync(new WindowHandle(window), by, value, until, equals, timeoutMs, pollIntervalMs);
        return ToolResponse.Ok(new { satisfied = r.Satisfied, @ref = r.Ref, elapsedMs = r.ElapsedMs, snapshotId = r.SnapshotId });
    });
```
Migrate every `new SnapshotTools(perception)` to `new SnapshotTools(perception, new WaitCoordinator(perception))` — call sites: `SnapshotToolsTests.cs:23` and `:41`, `GetBoundsTests`, `SnapshotStatsTests`, `SnapshotDiffTests`. In `Program.cs` add (before `SnapshotTools` L30): `builder.Services.AddSingleton<FlaUI.Mcp.Core.Perception.WaitCoordinator>();`.

- [ ] **Step 5: Write the failing test**

`test/FlaUI.Mcp.Tests/Server/WaitForTests.cs`:
```csharp
using System.Linq;
using System.Text.Json;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

[Trait("Category", "Desktop")]
public class WaitForTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public WaitForTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Missing_control_times_out_as_data_not_exception()
    {
        var (snap, _, handle) = await Setup();
        var json = await snap.DesktopWaitFor(handle, "automationId", "NoSuchControl", "exists", null, 1000, 250);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("satisfied").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Delayed_control_becomes_satisfied_with_a_ref()
    {
        var (snap, inter, handle) = await Setup();
        var baseJson = await snap.DesktopSnapshot(handle, fullProperties: true);
        var tree = JsonDocument.Parse(baseJson).RootElement.GetProperty("tree").GetString()!;
        var line = tree.Split('\n').First(l => l.Contains("aid=DelayRevealButton"));
        var btn = line[(line.IndexOf('[') + 1)..line.IndexOf(']')];
        await inter.DesktopInvoke(handle, btn);

        var json = await snap.DesktopWaitFor(handle, "automationId", "DelayedLabel", "exists", null, 5000, 500);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("satisfied").GetBoolean());
        Assert.StartsWith("e", doc.RootElement.GetProperty("ref").GetString());
    }

    private async Task<(SnapshotTools, InteractionTools, string)> Setup()
    {
        var dispatcher = new AutomationDispatcher();
        var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var snap = new SnapshotTools(perception, new WaitCoordinator(perception));
        var inter = new InteractionTools(perception, mgr, ServerOptions.FromArgs(System.Array.Empty<string>()));
        var window = new WindowTools(mgr, perception);
        var opened = await window.DesktopOpenWindow("pid", _app.Process.Id.ToString());
        var handle = JsonDocument.Parse(opened).RootElement.GetProperty("handle").GetString()!;
        return (snap, inter, handle);
    }
}
```

- [ ] **Step 6: Run — verify it fails then passes** — Run: `dotnet test --filter "FullyQualifiedName~WaitForTests"` plus the migrated suites → Expected: all PASS.

- [ ] **Step 7: Commit**
```bash
git add src/FlaUI.Mcp.Core/Perception/WaitCoordinator.cs src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs src/FlaUI.Mcp.Server/Program.cs test/FlaUI.Mcp.TestApp/MainWindow.xaml test/FlaUI.Mcp.TestApp/MainWindow.xaml.cs test/FlaUI.Mcp.Tests/Server/WaitForTests.cs test/FlaUI.Mcp.Tests/Server/SnapshotToolsTests.cs test/FlaUI.Mcp.Tests/Server/SnapshotStatsTests.cs test/FlaUI.Mcp.Tests/Server/SnapshotDiffTests.cs test/FlaUI.Mcp.Tests/Perception/GetBoundsTests.cs
git commit -m "feat(perception): desktop_wait_for + WaitCoordinator (Task 9)"
```

---

### Task 10: `desktop_wait_for_stable`

**Files:** Modify `WaitCoordinator.cs`, `SnapshotTools.cs`, TestApp (animated `Ticker`); Test `test/FlaUI.Mcp.Tests/Server/WaitForStableTests.cs` (Desktop).

Structural signature = ordered `(ControlType, AutomationId, Depth)` over the scoped subtree; `includeText` folds Name/Value. Stable when unchanged across `ceil(quietMs/pollIntervalMs)` consecutive polls. Timeout ⇒ `{stable:false}`. Optional subtree scope via `by`+`value`.

- [ ] **Step 1: Add to `WaitCoordinator`**:
```csharp
private static string Signature(IEnumerable<SnapshotNode> nodes, bool includeText)
    => string.Join("\n", nodes.Select(n => includeText
        ? $"{n.ControlType}:{n.AutomationId}:{n.Depth}:{n.Name}:{n.Value}"
        : $"{n.ControlType}:{n.AutomationId}:{n.Depth}"));

private static IReadOnlyList<SnapshotNode> Subtree(SnapshotModel model, string? by, string? value)
{
    var nodes = model.Nodes.ToList();
    if (string.IsNullOrEmpty(by) || string.IsNullOrEmpty(value)) return nodes;
    int start = nodes.FindIndex(n => Matches(n, by, value));
    if (start < 0) return System.Array.Empty<SnapshotNode>();
    var scope = nodes[start];
    var sub = new List<SnapshotNode> { scope };
    for (int i = start + 1; i < nodes.Count && nodes[i].Depth > scope.Depth; i++) sub.Add(nodes[i]);
    return sub;
}

public async Task<WaitStableResult> WaitForStableAsync(WindowHandle handle, string? by, string? value,
    bool includeText, int quietMs, int timeoutMs, int pollIntervalMs)
{
    var sw = Stopwatch.StartNew();
    var fullTree = new SnapshotOptions { InteractiveOnly = false, IncludeOffscreen = true };
    int needed = (int)System.Math.Ceiling((double)quietMs / System.Math.Max(1, pollIntervalMs));
    string? last = null; int stableCount = 0;
    bool scopeRequested = !string.IsNullOrEmpty(by) && !string.IsNullOrEmpty(value);

    while (true)
    {
        var (_, model) = await _perception.BuildModelAsync(handle, fullTree, new RefRegistry());
        var sub = Subtree(model, by, value);
        if (scopeRequested && sub.Count == 0)
            throw new ToolException(ToolErrorCode.SelectorNoMatch,
                $"No element matched {by}={value} to scope stability.", "widen or correct the selector");
        var sig = Signature(sub, includeText);
        stableCount = sig == last ? stableCount + 1 : 0;
        last = sig;
        if (stableCount >= needed)
        {
            var (snapId, _) = await _perception.SnapshotModelForWaitAsync(handle, fullTree);
            return new WaitStableResult(true, (int)sw.ElapsedMilliseconds, snapId);
        }
        if (sw.ElapsedMilliseconds >= timeoutMs)
            return new WaitStableResult(false, (int)sw.ElapsedMilliseconds, null);
        await Task.Delay(pollIntervalMs);
    }
}
```

- [ ] **Step 2: TestApp animated `Ticker`** — In `MainWindow.xaml`, after `DelayRevealButton`:
```xml
<TextBlock x:Name="Ticker" AutomationProperties.AutomationId="Ticker" Text="0" Margin="0,4,0,0"/>
```
In `MainWindow.xaml.cs` constructor, after `Secret.Password = SecretValue;`:
```csharp
var ticker = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(120) };
int tick = 0;
ticker.Tick += (_, _) => Ticker.Text = (++tick).ToString();
ticker.Start();
```

- [ ] **Step 3: Add the tool to `SnapshotTools.cs`**:
```csharp
[McpServerTool(ReadOnly = true), Description("Poll until a window subtree stops structurally changing. Optional scope via by+value (default whole window). includeText folds Name/Value into the signature (use to wait on a status-text settle; do NOT use on a window with a live clock/counter). Timeout returns {stable:false}.")]
public Task<string> DesktopWaitForStable(
    [Description("Window handle, e.g. w1.")] string window,
    [Description("Optional scope selector kind: automationId|name|controlType.")] string? by = null,
    [Description("Optional scope match value (with 'by').")] string? value = null,
    [Description("Fold Name/Value into the stability signature (default false).")] bool includeText = false,
    [Description("Quiet window in ms the signature must hold (default 500).")] int quietMs = 500,
    [Description("Total wait budget in ms (default 5000).")] int timeoutMs = 5000,
    [Description("Poll interval in ms (default 500).")] int pollIntervalMs = 500)
    => ToolResponse.Guard(async () =>
    {
        var r = await _wait.WaitForStableAsync(new WindowHandle(window), by, value, includeText, quietMs, timeoutMs, pollIntervalMs);
        return ToolResponse.Ok(new { stable = r.Stable, elapsedMs = r.ElapsedMs, snapshotId = r.SnapshotId });
    });
```

- [ ] **Step 4: Write the failing test**

`test/FlaUI.Mcp.Tests/Server/WaitForStableTests.cs`:
```csharp
using System.Text.Json;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

[Trait("Category", "Desktop")]
public class WaitForStableTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public WaitForStableTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Structural_signature_is_stable_despite_a_live_ticker()
    {
        var (snap, handle) = await Setup();
        var json = await snap.DesktopWaitForStable(handle, null, null, includeText: false, quietMs: 500, timeoutMs: 5000, pollIntervalMs: 250);
        Assert.True(JsonDocument.Parse(json).RootElement.GetProperty("stable").GetBoolean());
    }

    [Fact]
    public async Task IncludeText_on_a_live_ticker_times_out_as_unstable()
    {
        var (snap, handle) = await Setup();
        var json = await snap.DesktopWaitForStable(handle, null, null, includeText: true, quietMs: 500, timeoutMs: 1500, pollIntervalMs: 250);
        Assert.False(JsonDocument.Parse(json).RootElement.GetProperty("stable").GetBoolean());
    }

    private async Task<(SnapshotTools, string)> Setup()
    {
        var dispatcher = new AutomationDispatcher();
        var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var snap = new SnapshotTools(perception, new WaitCoordinator(perception));
        var window = new WindowTools(mgr, perception);
        var opened = await window.DesktopOpenWindow("pid", _app.Process.Id.ToString());
        var handle = JsonDocument.Parse(opened).RootElement.GetProperty("handle").GetString()!;
        return (snap, handle);
    }
}
```

- [ ] **Step 5: Run — verify it fails then passes** — Run: `dotnet test --filter "FullyQualifiedName~WaitForStableTests"` → Expected: PASS (2).

- [ ] **Step 6: Commit**
```bash
git add src/FlaUI.Mcp.Core/Perception/WaitCoordinator.cs src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs test/FlaUI.Mcp.TestApp/MainWindow.xaml test/FlaUI.Mcp.TestApp/MainWindow.xaml.cs test/FlaUI.Mcp.Tests/Server/WaitForStableTests.cs
git commit -m "feat(perception): desktop_wait_for_stable (Task 10)"
```

---

### Task 11: `desktop_get_focused_element`

**Files:** Modify `WindowManager.cs` (focused-element query), `PerceptionManager.cs` (focus helper), `SnapshotTools.cs` (tool); Test `test/FlaUI.Mcp.Tests/Server/GetFocusedElementTests.cs` (Desktop).

UIA focused element on the query STA; one-element model → ref + rendered descriptor line + owning window. `AccessDeniedIntegrity` on secure desktop; `NoFocusedElement` otherwise.

- [ ] **Step 1: STATE-VERIFY** — confirm FlaUI `UIA3Automation` exposes `FocusedElement()` (FlaUI.Core `AutomationBase.FocusedElement()`). If named differently, adjust Step 2.

- [ ] **Step 2: Focused-element primitive on `WindowManager`** (it owns `_automation`):
```csharp
public Task<T?> RunOnFocusedElementAsync<T>(Func<FlaUI.Core.AutomationElements.AutomationElement, T> func) where T : class =>
    _dispatcher.RunQueryAsync<T?>(() =>
    {
        var focused = _automation.FocusedElement();
        return focused is null ? null : func(focused);
    });
```

- [ ] **Step 3: Focus helper on `PerceptionManager`**:
```csharp
public sealed record FocusedElementInfo(string Ref, string DescriptorLine, string Title, int Pid, string? WindowHandle);

public async Task<FocusedElementInfo> GetFocusedElementAsync()
{
    FocusedElementInfo? info;
    try
    {
        info = await _windows.RunOnFocusedElementAsync<FocusedElementInfo>(el =>
        {
            var refs = new RefRegistry();
            const string ns = "focus";
            refs.BeginSnapshot(ns);
            var opts = new SnapshotOptions { MaxDepth = 0, InteractiveOnly = false, IncludeOffscreen = true };
            var model = SnapshotEngine.Build(el, System.Array.Empty<AutomationElement>(), opts, refs, ns);
            var node = model.Nodes.First();
            var line = SnapshotEngine.Render(model, opts).TrimEnd('\r', '\n');
            int pid = -1; string title = "";
            try { pid = el.Properties.ProcessId.ValueOrDefault; } catch { }
            try { title = el.Properties.Name.ValueOrDefault ?? ""; } catch { }
            return new FocusedElementInfo(node.Ref, line, title, pid, null);
        });
    }
    catch (System.UnauthorizedAccessException)
    {
        throw new ToolException(ToolErrorCode.AccessDeniedIntegrity,
            "Cannot read the focused element (secure/UAC desktop).", "dismiss the secure prompt and retry");
    }
    if (info is null)
        throw new ToolException(ToolErrorCode.NoFocusedElement,
            "No element currently has UIA focus.", "click or tab to a control, then retry");
    return info;
}
```

- [ ] **Step 4: Write the failing test**

`test/FlaUI.Mcp.Tests/Server/GetFocusedElementTests.cs`:
```csharp
using System.Text.Json;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

[Trait("Category", "Desktop")]
public class GetFocusedElementTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public GetFocusedElementTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Returns_the_focused_control_ref_after_focusing_Ok()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var snap = new SnapshotTools(perception, new WaitCoordinator(perception));
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            win.AsWindow().Focus();
            win.FindFirstDescendant(cf => cf.ByAutomationId("OkButton"))!.Focus();
            return true;
        });

        var json = await snap.DesktopGetFocusedElement();
        using var doc = JsonDocument.Parse(json);
        Assert.StartsWith("e", doc.RootElement.GetProperty("ref").GetString());
        Assert.Contains("Button", doc.RootElement.GetProperty("descriptor").GetString());
    }
}
```

- [ ] **Step 5: Run — verify it fails** — Expected: BUILD FAIL (`DesktopGetFocusedElement` missing).

- [ ] **Step 6: Add the tool to `SnapshotTools.cs`**:
```csharp
[McpServerTool(ReadOnly = true), Description("O(1) 'where am I': return the UIA-focused element's ref + descriptor line + owning window (title/pid), far cheaper than a full snapshot. AccessDeniedIntegrity on a secure/UAC desktop; NoFocusedElement when nothing is focused.")]
public Task<string> DesktopGetFocusedElement()
    => ToolResponse.Guard(async () =>
    {
        var f = await _perception.GetFocusedElementAsync();
        return ToolResponse.Ok(new
        {
            @ref = f.Ref,
            descriptor = f.DescriptorLine,
            window = new { handle = f.WindowHandle, title = f.Title, pid = f.Pid }
        });
    });
```

- [ ] **Step 7: Run — verify it passes** — Run: `dotnet test --filter "FullyQualifiedName~GetFocusedElementTests"` → Expected: PASS.

- [ ] **Step 8: Commit**
```bash
git add src/FlaUI.Mcp.Core/Windows/WindowManager.cs src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs test/FlaUI.Mcp.Tests/Server/GetFocusedElementTests.cs
git commit -m "feat(perception): desktop_get_focused_element (Task 11)"
```

---

### Task 12: `ScreenCapture` core (FlaUI Capture + headless detect + redaction + downscale + PNG)

**Files:** Create `src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs`; Test `test/FlaUI.Mcp.Tests/Perception/ScreenCaptureTests.cs` (Desktop).

- [ ] **Step 0: STATE-VERIFY `System.Drawing`** — `CaptureImage.Bitmap` is `System.Drawing.Bitmap`. Confirm the Core project compiles `Bitmap`/`Graphics`/`ImageFormat` (transitive via FlaUI.UIA3→FlaUI.Core). If unresolved, add to `FlaUI.Mcp.Core.csproj`: `<PackageReference Include="System.Drawing.Common" Version="9.0.0" />`. Do NOT add preemptively. Also confirm `Capture.Element`/`Capture.Rectangle` accept a single arg (settings optional) — if not, pass `null` for the `CaptureSettings` parameter.

- [ ] **Step 1: Create `ScreenCapture`**

`src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs`:
```csharp
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;

namespace FlaUI.Mcp.Core.Perception;

public sealed record CaptureResult(byte[] Png, int X, int Y, int W, int H, double ScaleApplied, int Redactions);

/// <summary>Screen-region capture (no occlusion handling — callers focus-first). Wraps FlaUI's
/// Capture, paints capture-time password redactions with LIVE bounds, downscales to maxWidth, and
/// PNG-encodes. Headless/disconnected sessions are detected before capture so we never hand back a
/// black frame. Must run on the query STA (UIA element bounds are read here).</summary>
public static class ScreenCapture
{
    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr hDesktop);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    public static bool IsDesktopRenderable()
    {
        var h = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (h == IntPtr.Zero) return false;
        CloseDesktop(h);
        return true;
    }

    public static Rectangle VirtualScreenBounds() => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    public static CaptureResult CaptureElement(AutomationElement element,
        IReadOnlyList<Rectangle> passwordBoundsAbsolute, int maxWidth)
    {
        var bounds = element.BoundingRectangle;
        using var cap = Capture.Element(element);
        return Encode(cap.Bitmap, bounds, passwordBoundsAbsolute, maxWidth);
    }

    public static CaptureResult CaptureRectangle(Rectangle absolute,
        IReadOnlyList<Rectangle> blackoutAbsolute, int maxWidth)
    {
        using var cap = Capture.Rectangle(absolute);
        return Encode(cap.Bitmap, absolute, blackoutAbsolute, maxWidth);
    }

    private static CaptureResult Encode(Bitmap src, Rectangle captureBounds,
        IReadOnlyList<Rectangle> redactAbsolute, int maxWidth)
    {
        double scale = (maxWidth > 0 && src.Width > maxWidth) ? (double)maxWidth / src.Width : 1.0;
        int outW = System.Math.Max(1, (int)System.Math.Round(src.Width * scale));
        int outH = System.Math.Max(1, (int)System.Math.Round(src.Height * scale));

        using var outBmp = new Bitmap(outW, outH);
        using (var g = Graphics.FromImage(outBmp))
        {
            g.DrawImage(src, new Rectangle(0, 0, outW, outH));
            int painted = 0;
            using var black = new SolidBrush(Color.Black);
            foreach (var r in redactAbsolute)
            {
                var rel = new Rectangle(
                    (int)System.Math.Round((r.X - captureBounds.X) * scale),
                    (int)System.Math.Round((r.Y - captureBounds.Y) * scale),
                    (int)System.Math.Round(r.Width * scale),
                    (int)System.Math.Round(r.Height * scale));
                if (rel.Width <= 0 || rel.Height <= 0) continue;
                g.FillRectangle(black, rel);
                painted++;
            }
            using var ms = new MemoryStream();
            outBmp.Save(ms, ImageFormat.Png);
            return new CaptureResult(ms.ToArray(), captureBounds.X, captureBounds.Y,
                captureBounds.Width, captureBounds.Height, scale, painted);
        }
    }
}
```

- [ ] **Step 2: Write the failing test**

`test/FlaUI.Mcp.Tests/Perception/ScreenCaptureTests.cs`:
```csharp
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

[Trait("Category", "Desktop")]
public class ScreenCaptureTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public ScreenCaptureTests(TestAppFixture app) => _app = app;

    [Fact]
    public void Desktop_is_renderable_on_a_connected_session()
        => Assert.True(ScreenCapture.IsDesktopRenderable());

    [Fact]
    public async Task Captures_a_png_of_the_window_with_a_redaction_over_the_password_box()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        var result = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            var secret = win.FindFirstDescendant(cf => cf.ByAutomationId("Secret"))!;
            return ScreenCapture.CaptureElement(win, new[] { secret.BoundingRectangle }, maxWidth: 1600);
        });
        Assert.True(result.Png.Length > 100);
        Assert.Equal(1, result.Redactions);
        Assert.Equal(0x89, result.Png[0]);                 // PNG magic
        Assert.Equal((byte)'P', result.Png[1]);
    }
}
```

- [ ] **Step 3: Run — verify it fails then passes** — Run: `dotnet test --filter "FullyQualifiedName~ScreenCaptureTests"` → after Step 1, Expected: PASS (2).

- [ ] **Step 4: Commit**
```bash
git add src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs test/FlaUI.Mcp.Tests/Perception/ScreenCaptureTests.cs
git commit -m "feat(perception): ScreenCapture engine (capture+redact+downscale+png) (Task 12)"
```

---

### Task 13: `desktop_screenshot` tool + `ToolResponse` image helper

**Files:** Modify `ToolResponse.cs` (image helper → `CallToolResult`), `ScreenshotTools.cs` (tool), `PerceptionManager.cs` (capture orchestration on the query STA); Test `test/FlaUI.Mcp.Tests/Server/ScreenshotToolTests.cs` (Desktop).

Returns native `ImageContentBlock` + `TextContentBlock` via `CallToolResult`. `output:"file"` → `NotImplemented`. Minimized → `ElementNotActionable`. Denylisted → `TargetDenied`. Headless → `CaptureUnavailable`.

- [ ] **Step 1: Image helper on `ToolResponse`** (add `using ModelContextProtocol.Protocol;`):
```csharp
public static async Task<CallToolResult> GuardImage(Func<Task<CallToolResult>> body)
{
    try { return await body(); }
    catch (ToolException ex)
    {
        return new CallToolResult { IsError = true, Content = new List<ContentBlock> {
            new TextContentBlock { Text = JsonSerializer.Serialize(
                new { error = ex.Code.ToString(), message = ex.Message, suggestedRecovery = ex.SuggestedRecovery }, Json) } } };
    }
    catch (Exception ex)
    {
        return new CallToolResult { IsError = true, Content = new List<ContentBlock> {
            new TextContentBlock { Text = JsonSerializer.Serialize(
                new { error = "INTERNAL", message = ex.Message, suggestedRecovery = (string?)"re-check arguments and retry" }, Json) } } };
    }
}

public static CallToolResult Image(byte[] png, object metadata) => new()
{
    Content = new List<ContentBlock>
    {
        ImageContentBlock.FromBytes(png, "image/png"),
        new TextContentBlock { Text = JsonSerializer.Serialize(metadata, Json) }
    }
};
```

- [ ] **Step 2: Capture orchestration on `PerceptionManager`** — capture WHERE the element is live (query STA), returning the encoded result + flags:
```csharp
public sealed record ScreenshotOutcome(CaptureResult? Result, bool Minimized, bool Denied, string? DeniedProcess);

public Task<ScreenshotOutcome> CaptureWindowAsync(WindowHandle handle, string? @ref, int maxWidth) =>
    _windows.RunWithWindowAndDesktopAsync(handle, (win, desktop) =>
    {
        var procName = SafeProcessName(win);
        if (PerceptionPolicy.IsDenied(procName))
            return new ScreenshotOutcome(null, false, true, procName);

        try
        {
            var wp = win.Patterns.Window.PatternOrDefault;
            if (wp is not null && wp.WindowVisualState.ValueOrDefault == FlaUI.Core.Definitions.WindowVisualState.Minimized)
                return new ScreenshotOutcome(null, true, false, null);
        }
        catch { }

        var target = string.IsNullOrEmpty(@ref)
            ? (AutomationElement)win
            : _refs.Resolve(handle.Id, @ref!, PopupFinder.SearchRoots(win, desktop));

        var pwBounds = new List<System.Drawing.Rectangle>();
        try { foreach (var d in win.FindAllDescendants()) { try { if (d.Properties.IsPassword.ValueOrDefault) pwBounds.Add(d.BoundingRectangle); } catch { } } } catch { }

        var result = ScreenCapture.CaptureElement(target, pwBounds, maxWidth);
        return new ScreenshotOutcome(result, false, false, null);
    });

public Task<CaptureResult> CaptureVirtualDesktopAsync(int maxWidth) =>
    _windows.RunOnQueryAsync(() =>
    {
        var vbounds = ScreenCapture.VirtualScreenBounds();
        // Full-desktop: best-effort. Per-field redaction across all windows is impractical here; the
        // tool description recommends window-scoped capture when secrets are plausible. redactions:0.
        return ScreenCapture.CaptureRectangle(vbounds, System.Array.Empty<System.Drawing.Rectangle>(), maxWidth);
    });
```
(STATE-VERIFY: `win.Patterns.Window.PatternOrDefault?.WindowVisualState` accessor names against FlaUI 5 — the interaction layer uses `win.AsWindow()` patterns; mirror whatever compiles. `win` here is `Window`; `Window` derives from `AutomationElement` so `FindAllDescendants()` and the `(AutomationElement)win` cast are valid.)

- [ ] **Step 3: The tool** — add to `ScreenshotTools.cs` (add `using ModelContextProtocol.Protocol;`, `using ModelContextProtocol.Server;` already present, `using FlaUI.Mcp.Core.Errors;`):
```csharp
[McpServerTool(ReadOnly = true), Description("Capture a window, an element (window+ref), or the full virtual desktop as a PNG. Returns a native image block + JSON metadata {bounds,dpiScale,scaleApplied,redactions}. Password fields are redacted at capture time (window-scoped capture recommended when secrets are plausible). output must be 'inline' (file→NotImplemented). Focus the window first (no occlusion handling). Minimized→ElementNotActionable.")]
public Task<CallToolResult> DesktopScreenshot(
    [Description("Window handle, e.g. w1. Omit (and omit ref) for the full virtual desktop.")] string? window = null,
    [Description("Element ref to capture (requires window).")] string? @ref = null,
    [Description("Only 'inline' is implemented (default). 'file' returns NotImplemented.")] string output = "inline",
    [Description("Downscale so width <= maxWidth (default 1600; 0 disables).")] int maxWidth = 1600)
    => ToolResponse.GuardImage(async () =>
    {
        if (output != "inline")
            throw new ToolException(ToolErrorCode.NotImplemented, "Only output:'inline' is supported in v0.4.0.", "omit output or pass output:'inline'");
        if (!ScreenCapture.IsDesktopRenderable())
            throw new ToolException(ToolErrorCode.CaptureUnavailable, "The desktop session is disconnected or locked; rendering is unavailable.", "reconnect to restore rendering");

        CaptureResult result;
        if (string.IsNullOrEmpty(window))
            result = await _perception.CaptureVirtualDesktopAsync(maxWidth);
        else
        {
            var outcome = await _perception.CaptureWindowAsync(new WindowHandle(window!), @ref, maxWidth);
            if (outcome.Denied)
                throw new ToolException(ToolErrorCode.TargetDenied, $"Capturing windows owned by '{outcome.DeniedProcess}' is blocked.", "capture a non-sensitive window");
            if (outcome.Minimized)
                throw new ToolException(ToolErrorCode.ElementNotActionable, "Window is minimized; restore it first.", "desktop_window_transform restore, then retry");
            result = outcome.Result!;
        }
        var dpi = DpiHelper.ScaleForPoint(result.X, result.Y);
        return ToolResponse.Image(result.Png, new
        {
            bounds = new { x = result.X, y = result.Y, w = result.W, h = result.H },
            dpiScale = dpi, scaleApplied = result.ScaleApplied, redactions = result.Redactions
        });
    });
```

- [ ] **Step 4: Write the failing test**

`test/FlaUI.Mcp.Tests/Server/ScreenshotToolTests.cs`:
```csharp
using System.Linq;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Tools;
using ModelContextProtocol.Protocol;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

[Trait("Category", "Desktop")]
public class ScreenshotToolTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public ScreenshotToolTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Window_screenshot_returns_image_block_plus_metadata()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var tools = new ScreenshotTools(perception);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var result = await tools.DesktopScreenshot(handle.Id);
        Assert.False(result.IsError);
        Assert.Contains(result.Content, c => c is ImageContentBlock);
        var text = result.Content.OfType<TextContentBlock>().First().Text;
        Assert.Contains("\"redactions\":1", text); // the Secret PasswordBox
    }

    [Fact]
    public async Task Output_file_returns_NotImplemented()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var tools = new ScreenshotTools(perception);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        var result = await tools.DesktopScreenshot(handle.Id, output: "file");
        Assert.True(result.IsError);
        Assert.Contains("NotImplemented", result.Content.OfType<TextContentBlock>().First().Text);
    }
}
```

- [ ] **Step 5: Run — verify it fails then passes** — Run: `dotnet test --filter "FullyQualifiedName~ScreenshotToolTests"` → after Steps 1-3 Expected: PASS (2). (`redactions:1` asserts the live `Secret` PasswordBox is blacked.)

- [ ] **Step 6: Commit**
```bash
git add src/FlaUI.Mcp.Server/Tools/ToolResponse.cs src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs test/FlaUI.Mcp.Tests/Server/ScreenshotToolTests.cs
git commit -m "feat(perception): desktop_screenshot native image + capture-time redaction (Task 13)"
```

---

### Task 14: Final audit, full Desktop verification, docs

**Files:** Modify `ROADMAP.md`, `README.md`, `FlaUI.Mcp.Server.csproj` (version).

- [ ] **Step 1: readOnlyHint audit** — `Grep` each new tool method (`DesktopScreenshot`, `DesktopGetBounds`, `DesktopSnapshotStats`, `DesktopSnapshotDiff`, `DesktopWaitFor`, `DesktopWaitForStable`, `DesktopGetFocusedElement`, `DesktopListWindows`) for the preceding `[McpServerTool(ReadOnly = true)`. None may be `Destructive`. Run `dotnet build -c Debug` → `0 Error(s)`.

- [ ] **Step 2: Non-Desktop gate** — Run: `dotnet test --filter "Category!=Desktop"` → Expected: all green (65 + `ToolErrorCodeAdditionsTests`).

- [ ] **Step 3: Full Desktop suite (physical console / connected RDP), one class at a time** — `SnapshotModelPinTests`, `FocusedFlagTests`, `RecycleGuardTests`, `SnapshotCacheTests`, `GetBoundsTests`, `SnapshotStatsTests`, `SnapshotDiffTests`, `ListWindowsExtensionTests`, `WaitForTests`, `WaitForStableTests`, `GetFocusedElementTests`, `ScreenCaptureTests`, `ScreenshotToolTests`; regressions `SnapshotEngineTests`, `SnapshotToolsTests`, `OffscreenCullTests`, `PasswordRedactionTests`, `PopupGraftingTests`, `InteractionToolsTests`. Expected: all green. (Per the debug-workflow rule: bounded foreground runs, parse TRX counters, loop-kill orphan testhost/TestApp before/after.)

- [ ] **Step 4: Update `ROADMAP.md`** — mark Phase 3b-1 tools shipped; note 3b-2 (grid/text/clipboard, state-mutating) remains a forward spec; record backlog: screenshot occlusion (PrintWindow), full-desktop per-field redaction, wait_for LegacyIAccessible value fallback.

- [ ] **Step 5: Update `README.md`** — add the 7 new tools + the `desktop_list_windows` extension to the tool list; add a screenshot/redaction safety note; bump the stated version to v0.4.0. (Standing rule: never push a stale README.)

- [ ] **Step 6: Bump version** — in `FlaUI.Mcp.Server.csproj` set `<Version>0.4.0</Version>`.

- [ ] **Step 7: Commit**
```bash
git add ROADMAP.md README.md src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj
git commit -m "docs: Phase 3b-1 perception completion — v0.4.0 (Task 14)"
```

- [ ] **Step 8: Finish the branch** — Use superpowers:finishing-a-development-branch (verify tests, then merge/PR per user choice). Tag at release per the Phase-3a flow.

---

## Self-Review

**Spec coverage:** §3.1 screenshot → Tasks 12/13; §3.2 get_bounds → Task 5; §3.3 snapshot_stats → Task 6; §3.4 list_windows extension → Task 8; §3.5 snapshot_diff → Task 7; §3.6 wait_for → Task 9; §3.7 wait_for_stable → Task 10; §3.8 get_focused_element → Task 11; §4 focused flag → Task 2; §5 recycle guard → Task 3; §6 screenshot security (redaction/denylist/headless) → Tasks 12/13; §7 error codes → Task 1; §8 Task-0 node model + component placement → Task 0 + Tasks 4/9/12. All sections mapped.

**Flagged deviations (resolved in-plan, not placeholders):**
1. **`InvalidArguments` code** — used by §3.3/§3.6 but NOT in the current enum; added in Task 1 (verify-then-add).
2. **wait_for `valueEquals` fallback** — `Value ?? Name` (ValuePattern→Name); LegacyIAccessiblePattern.Value not modeled — noted in Task 9 + ROADMAP backlog.
3. **Diff `value`** — ValuePattern only (LegacyIAccessible not modeled); non-ValuePattern controls compare null==null. Noted Task 7.
4. **Full-desktop per-field redaction** — best-effort `redactions:0`; window-scoped capture recommended (documented in the tool description). Noted Task 13.
5. **STATE-VERIFY hooks** embedded where FlaUI/SDK accessor names must be confirmed at implementation time (`FocusedElement()`, `Window.Patterns.Window`, `Capture.*` settings overload, `System.Drawing.Common` transitive ref, `ServerOptions` ctor). These are verification steps, not guesses.

**Type consistency:** `SnapshotModel`/`SnapshotNode`/`SnapshotItem` (Task 0) consumed identically by Stats (6), Diff (7), WaitCoordinator (9-10), focus (11). `PerceptionManager(WindowManager, RefRegistry, SnapshotCache)` introduced in Task 4, used by every later setup. `SnapshotTools(PerceptionManager, WaitCoordinator)` introduced in Task 9 — Tasks 5/6/7 tests note they must use current ctor arity until Task 9 migrates them. `WindowTools(WindowManager, PerceptionManager)` introduced in Task 8. `ScreenshotTools(PerceptionManager)` stable.

**Ordering:** Task 1 (codes) precedes all throwers. Task 0 (model) precedes 4/6/7/9/10. Task 4 (cache/BuildModelAsync) precedes 6/7/9/10. Task 5 creates `ScreenshotTools`; Task 13 extends it. Tasks 8/9 change shared ctors and migrate earlier tests in the same task. The order respects every dependency.
