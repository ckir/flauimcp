# FlaUI.Mcp Phase 3b-1 — Perception Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete the read-only perception surface (screenshot, bounds, snapshot stats/diff, focus, wait conditions) on the existing split query/action STA dispatcher, all tools `readOnlyHint:true, destructiveHint:false`.

**Architecture:** A Task-0 `SnapshotEngine` node-model refactor (project `SnapshotNode`s from `Walk`, render text byte-identically from the model) unblocks stats/diff/stability. New read-only tools layer on the model + the option-C ref engine. Bounds/selector/focus/password-rect resolution run on the long-lived query STA; **bitmap capture + redaction + PNG encode run OFF the STA on a worker** (spec §8 — never block the single query dispatcher on a BitBlt). Wait loops issue one short `RunQueryAsync` tick per poll with a throwaway `RefRegistry` (no real-registry growth) and `Task.Delay` between ticks, culling offscreen subtrees to bound cost. No synthetic input — deferred to Phase 4.

**Tech Stack:** C#/.NET 10 (`net10.0-windows`), FlaUI.UIA3 5.0.0 (`FlaUI.Core.Capturing.Capture.Rectangle`, `CaptureImage.Bitmap`), ModelContextProtocol 1.4.0 (`CallToolResult` + `ImageContentBlock.FromBytes` + `TextContentBlock`), xUnit. P/Invoke for DPI (`MonitorFromPoint`/`GetDpiForMonitor`), virtual-screen metrics (`GetSystemMetrics`), per-window rect (`GetWindowRect`), headless detection (`OpenInputDesktop`). PNG via `System.Drawing` (transitively from FlaUI.Core).

**Release:** v0.4.0. Scope is **3b-1 only**. Branch: `phase-3b-perception` (spec committed `abe9ee0`).

**Revision note (post agy review, plan commit ≥`9781456`):** 13 adversarial-review fixes folded in — single-walk Task-0 pin, ctor/DI front-loading (Task 4 scaffolding), off-STA capture, full-desktop denylist blackout, popup-inclusive password redaction, `Value` removed from the default walk, `list_windows` `includeStats` dropped, diff composite identity, chainable `get_focused_element`, `valueEquals` LegacyIAccessible fallback, `CaptureUnavailable` exception mapping, hard `maxWidth` ceiling 1920, `suggestedRecovery` on every envelope.

---

## Ground-truth references (verified against merged code on `phase-3b-perception`)

- `SnapshotEngine.cs` — `Walk(AutomationElement root, IReadOnlyList<AutomationElement> popupRoots, SnapshotOptions options, RefRegistry refs, string windowId)` returns `(string Tree, int NodeCount)` (L23). Inner `Visit` L61-126; `[Active Overlays]` header L50; depth-limit line L114-116; `FormatLine` L138-164 (reads `BoundingRectangle` L141, `IsEnabled` L142, `IsKeyboardFocusable` L143, `IsPassword` L151, patterns L154); `IsInteresting` L129-136; `SupportedPatterns` L166-185; `Safe<T>` L196-199; `RidEqual` L188-194.
- `RefRegistry.cs` — `Entry(ElementDescriptor Descriptor, AutomationElement? Cached)` L14; `Register` L34-46; `Lookup` L50-60; `Resolve` fast-path L78-88 (guard L83); `ResolveDescriptor` L96-131; `BeginSnapshot` L22-31.
- `ElementDescriptor.cs` — `record ElementDescriptor(IReadOnlyList<int> RuntimeId, ControlType ControlType, string AutomationId, string Name, string? AncestorAutomationId, IReadOnlyList<int> IndexPath)` L8-14.
- `PerceptionManager.cs` — ctor `(WindowManager windows, RefRegistry refs)` L14; `RunOnRefAsync` L23-28; `SnapshotAsync` L59-78; `SafeProcessName` L50-57.
- `SnapshotResult.cs` — `record SnapshotResult(string SnapshotId, string Tree, int NodeCount)`.
- `SnapshotOptions.cs` — `RootRef`, `MaxDepth=40`, `InteractiveOnly=true`, `FullProperties=false`, `IncludeOffscreen=false`.
- `PopupFinder.cs` — `SearchRoots(win, desktop)` L14 (window + owner popups), `FindOwnerPopups(desktop, win)` L26.
- `WindowManager.cs` — `record WindowInfo(string Title, string ProcessName, int Pid, bool IsForeground)` L12; `ListWindowsAsync()` L30-40; `EnumTopLevel()` L147-164 (z-ordered); `RunWithWindowAndDesktopAsync` L75-82; `OpenByPidAsync` L42-61; P/Invokes L124-142; `_hwnds` dict L19.
- `AutomationDispatcher.cs` — `RunQueryAsync<T>(Func<T>)` L20.
- `SnapshotTools.cs` — `[McpServerToolType] sealed class`, ctor `(PerceptionManager)` L12, `DesktopSnapshot` L16-33 (`ToolResponse.Guard`).
- `WindowTools.cs` — ctor `(WindowManager)` L13, `DesktopListWindows` L16-17, `DesktopOpenWindow` L22-35.
- `InteractionTools.cs` — `Act`/`GuardWrite` pattern L21-28; attribute style L30-35.
- `ToolResponse.cs` — `Ok(object)` L13, `Guard` L15, `GuardWrite` L30, private `Json` L11.
- `ToolErrorCode.cs` — enum L3-21 ends at `Timeout`; **`InvalidArguments` is NOT present** (add in Task 1).
- `Program.cs` — DI L25-31; `WithToolsFromAssembly()` L36.
- `ServerOptions.cs` — STATE-VERIFY ctor/`FromArgs` shape (referenced `Program.cs:22`).
- `TestAppFixture.cs` — `.Process`; tests `[Trait("Category","Desktop")] IClassFixture<TestAppFixture>`.
- `MainWindow.xaml` — `Input`, `Secret` (PasswordBox, `MainWindow.SecretValue="hunter2-NEVER-LEAK"`), `OkButton`, `Status`, `ItemList`, `Check`, `Exp`, `FocusReveal`/`RevealedLabel`, `FreezeButton` (`FreezeMs=2000`), `MenuTarget`. Root `StackPanel` unnamed; Window `AutomationId="MainWindow"`, 640×600.

**Test commands** (repo root): build `dotnet build -c Debug` (→ `0 Error(s)`); unit gate `dotnet test --filter "Category!=Desktop"` (65 passing); Desktop `dotnet test --filter "FullyQualifiedName~<Class>"` one class at a time on a connected session. Server serializes compact JSON (`WriteIndented=false`); assert on parsed values.

---

## File Structure

**New (Core/Perception):** `SnapshotNode.cs`, `SnapshotCache.cs`, `SnapshotDiff.cs`, `WaitCoordinator.cs`, `ScreenCapture.cs`, `Geometry/DpiHelper.cs`.
**New (Server/Tools):** `ScreenshotTools.cs`.
**Extend:** `SnapshotEngine.cs`, `ElementDescriptor.cs`, `RefRegistry.cs`, `PerceptionManager.cs`, `SnapshotTools.cs`, `WindowTools.cs`, `WindowManager.cs`, `ToolErrorCode.cs`, `ToolResponse.cs`, `Program.cs`.
**New tests:** under `Perception/` and `Server/`. TestApp gains controls in Tasks 9-10.

---

### Task 0: `SnapshotEngine` node-model refactor (single-walk pin) — PREREQUISITE

**Files:** Create `SnapshotNode.cs`; Modify `SnapshotEngine.cs`; Test `test/FlaUI.Mcp.Tests/Perception/SnapshotModelPinTests.cs` (Desktop).

The pin reads each element's COM properties **once** and compares the legacy formatter against the model formatter over those identical reads — no second live walk (fix for the two-walk drift). `Value` is deliberately NOT read here (perf — see §6/§8 of the review); diff compares Name/Enabled/Focused.

- [ ] **Step 1: Create the node model**

`src/FlaUI.Mcp.Core/Perception/SnapshotNode.cs`:
```csharp
using FlaUI.Core.Definitions;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>One rendered item: an element node or a structural marker ([Active Overlays] /
/// depth-limit). Render(...) reproduces byte-for-byte legacy text; Stats/Diff/stability consume
/// only the element nodes. Value is intentionally absent — reading ValuePattern.Value on every
/// node freezes the STA on virtualized grids; diff compares Name/Enabled/Focused.</summary>
public abstract record SnapshotItem;

public sealed record SnapshotNode(
    string Ref,
    int Depth,
    string Indent,
    ControlType ControlType,
    string AutomationId,
    string Name,
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

public sealed record SnapshotModel(IReadOnlyList<SnapshotItem> Items)
{
    public IEnumerable<SnapshotNode> Nodes => Items.OfType<SnapshotNode>();
    public int NodeCount => Items.Count(i => i is SnapshotNode);
}
```
(`Focused` defaults to false in Build until Task 2 wires the read.)

- [ ] **Step 2: Write the failing single-walk pin test**

`test/FlaUI.Mcp.Tests/Perception/SnapshotModelPinTests.cs`:
```csharp
using System.Linq;
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

    // Legacy formatter reproduced over node fields — pins FormatNode against the OLD FormatLine
    // logic with ZERO second COM walk (the model came from a single Build traversal).
    private static string Legacy(SnapshotNode n)
    {
        var state = new System.Collections.Generic.List<string>();
        if (n.Enabled) state.Add("enabled");
        if (n.Focusable) state.Add("focusable");
        if (n.Focused) state.Add("focused");
        string shown = n.IsPassword ? "[REDACTED]" : n.Name;
        var sb = new System.Text.StringBuilder();
        sb.Append(n.Indent).Append('[').Append(n.Ref).Append("] ").Append(n.ControlType).Append(' ')
          .Append('"').Append(shown).Append('"')
          .Append(" @{").Append(n.Bounds.X).Append(',').Append(n.Bounds.Y).Append(',')
          .Append(n.Bounds.Width).Append(',').Append(n.Bounds.Height).Append('}')
          .Append(" {").Append(string.Join(", ", state)).Append('}');
        if (n.Patterns.Count > 0) sb.Append(" [").Append(string.Join(",", n.Patterns)).Append(']');
        return sb.ToString();
    }

    [Fact]
    public async Task FormatNode_matches_legacy_format_per_node_from_one_walk()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var (renderLines, legacyLines, count, nodeCount) = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            var opts = new SnapshotOptions();
            var refs = new RefRegistry(); refs.BeginSnapshot(handle.Id);
            var model = SnapshotEngine.Build(win, System.Array.Empty<AutomationElement>(), opts, refs, handle.Id);
            var rendered = SnapshotEngine.Render(model, opts).Split('\n').Where(l => l.Length > 0 && l.Contains("[e")).ToArray();
            var legacy = model.Nodes.Select(Legacy).ToArray();
            return (rendered, legacy, model.NodeCount, model.Nodes.Count());
        });

        Assert.Equal(legacyLines.Length, renderLines.Length);
        Assert.Equal(count, nodeCount);
    }
}
```
(Pins the formatter equivalence purely; the existing Contains-based `SnapshotEngineTests` remain the traversal backstop. The fixture is static at Task 0 — no animated controls yet — so even the structural output is deterministic.)

- [ ] **Step 3: Run — verify it fails to compile** — `dotnet test --filter "FullyQualifiedName~SnapshotModelPinTests"` → Expected: BUILD FAIL (`Build`/`Render` missing).

- [ ] **Step 4: Add `Build` + `Render` to `SnapshotEngine` WITHOUT touching `Walk`**

Add these members (keep `Walk` as-is for now). `Build` mirrors `Walk`'s traversal/culling/registration; `Render`+`FormatNode` reproduce the legacy text:
```csharp
public static SnapshotModel Build(
    AutomationElement root, IReadOnlyList<AutomationElement> popupRoots,
    SnapshotOptions options, RefRegistry refs, string windowId)
{
    var items = new List<SnapshotItem>();
    var popupRids = new List<int[]>();
    foreach (var p in popupRoots)
    {
        var prid = Safe(() => p.Properties.RuntimeId.ValueOrDefault, (int[]?)null);
        if (prid != null) popupRids.Add(prid);
    }
    var rootBounds = Safe(() => root.BoundingRectangle, System.Drawing.Rectangle.Empty);
    Visit(root, 0, Array.Empty<int>(), null, "", rootBounds);
    if (popupRoots.Count > 0)
    {
        items.Add(new OverlaysHeaderItem());
        for (int i = 0; i < popupRoots.Count; i++)
        {
            var pb = Safe(() => popupRoots[i].BoundingRectangle, System.Drawing.Rectangle.Empty);
            Visit(popupRoots[i], 0, new[] { -1 - i }, null, "  ", pb);
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
        if (depth > 0 && !options.IncludeOffscreen && Safe(() => el.Properties.IsOffscreen.ValueOrDefault, false)) return;
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
            var descriptor = new ElementDescriptor(rid, ct, aid, name, ancestorAid, indexPath);
            var @ref = refs.Register(windowId, descriptor, el);
            var rect = Safe(() => el.BoundingRectangle, System.Drawing.Rectangle.Empty);
            bool enabled = Safe(() => el.IsEnabled, false);
            bool focusable = Safe(() => el.Properties.IsKeyboardFocusable.ValueOrDefault, false);
            bool isPassword = Safe(() => el.Properties.IsPassword.ValueOrDefault, false);
            bool offscreen = Safe(() => el.Properties.IsOffscreen.ValueOrDefault, false);
            var patterns = SupportedPatterns(el);
            string help = Safe(() => el.HelpText, "");
            items.Add(new SnapshotNode(@ref, depth, indent, ct, aid, name, rect, enabled, focusable,
                false, isPassword, offscreen, rid, patterns, help));
            childIndent = indent + "  ";
        }
        var nextAncestor = string.IsNullOrEmpty(aid) ? ancestorAid : aid;
        AutomationElement[] children = Safe(() => el.FindAllChildren(), Array.Empty<AutomationElement>());
        if (depth >= options.MaxDepth)
        {
            if (children.Length > 0) items.Add(new DepthLimitItem(childIndent, children.Length, options.MaxDepth));
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
        switch (item)
        {
            case SnapshotNode n: sb.AppendLine(FormatNode(n, options)); break;
            case OverlaysHeaderItem: sb.AppendLine("[Active Overlays]"); break;
            case DepthLimitItem d:
                sb.Append(d.Indent).Append("… ").Append(d.MoreCount)
                  .Append(" more (depth limit ").Append(d.MaxDepth).AppendLine(")");
                break;
        }
    return sb.ToString();
}

private static string FormatNode(SnapshotNode n, SnapshotOptions options)
{
    var state = new List<string>();
    if (n.Enabled) state.Add("enabled");
    if (n.Focusable) state.Add("focusable");
    if (n.Focused) state.Add("focused");
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
Note: Task 0's `FormatNode` already emits `focused` (harmless — `Focused` is always false until Task 2 reads it, so the brace group is unchanged). This keeps the legacy/format pin exact across Task 2.

- [ ] **Step 5: Run the pin — verify it passes** — Expected: PASS.

- [ ] **Step 6: Collapse `Walk` to delegate** — Replace `Walk`'s body (L30-126) with:
```csharp
var model = Build(root, popupRoots, options, refs, windowId);
return (Render(model, options), model.NodeCount);
```
Delete the legacy inline `Visit` and the private `FormatLine(string,string,AutomationElement,...)`. Keep `IsInteresting`, `SupportedPatterns`, `RidEqual`, `Safe`.

- [ ] **Step 7: Regression** — `dotnet test --filter "Category!=Desktop"`, then `FullyQualifiedName~SnapshotEngineTests|SnapshotModelPinTests|OffscreenCullTests|PasswordRedactionTests|PopupGraftingTests` → all green.

- [ ] **Step 8: Commit**
```bash
git add src/FlaUI.Mcp.Core/Perception/SnapshotNode.cs src/FlaUI.Mcp.Core/Perception/SnapshotEngine.cs test/FlaUI.Mcp.Tests/Perception/SnapshotModelPinTests.cs
git commit -m "refactor(perception): project SnapshotModel from Walk, single-walk byte-identical pin (Task 0)"
```

---

### Task 1: Add the new error codes

**Files:** Modify `ToolErrorCode.cs`; Test `Errors/ToolErrorCodeAdditionsTests.cs`

- [ ] **Step 1: STATE-VERIFY** — enum ends at `Timeout`; `InvalidArguments` absent (else drop it below).
- [ ] **Step 2: Write the failing test**
```csharp
using FlaUI.Mcp.Core.Errors;
using Xunit;
namespace FlaUI.Mcp.Tests.Errors;
public class ToolErrorCodeAdditionsTests
{
    [Theory]
    [InlineData("InvalidArguments")][InlineData("CaptureUnavailable")][InlineData("SnapshotNotFound")]
    [InlineData("SnapshotWindowMismatch")][InlineData("SelectorNoMatch")][InlineData("NoFocusedElement")]
    [InlineData("NotImplemented")]
    public void New_codes_are_defined(string name)
        => Assert.True(Enum.IsDefined(typeof(ToolErrorCode), Enum.Parse<ToolErrorCode>(name)));
}
```
- [ ] **Step 3: Run — verify it fails.**
- [ ] **Step 4: Add the codes** — after `Timeout` (L20): `Timeout,` then `InvalidArguments, CaptureUnavailable, SnapshotNotFound, SnapshotWindowMismatch, SelectorNoMatch, NoFocusedElement, NotImplemented`.
- [ ] **Step 5: Run — verify it passes (7 cases).**
- [ ] **Step 6: Commit**
```bash
git add src/FlaUI.Mcp.Core/Errors/ToolErrorCode.cs test/FlaUI.Mcp.Tests/Errors/ToolErrorCodeAdditionsTests.cs
git commit -m "feat(errors): add perception-completion error codes (Task 1)"
```

---

### Task 2: `focused` descriptor flag (always-on)

**Files:** Modify `ElementDescriptor.cs`, `SnapshotEngine.cs`; Test `Perception/FocusedFlagTests.cs` (Desktop).

- [ ] **Step 1: Failing test** — `test/FlaUI.Mcp.Tests/Perception/FocusedFlagTests.cs`:
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
            win.FindFirstDescendant(cf => cf.ByAutomationId("OkButton"))!.Focus();
            var refs = new RefRegistry(); refs.BeginSnapshot(handle.Id);
            var model = SnapshotEngine.Build(win, System.Array.Empty<AutomationElement>(), new SnapshotOptions(), refs, handle.Id);
            return SnapshotEngine.Render(model, new SnapshotOptions());
        });
        Assert.Contains("focused", tree);
    }
}
```
- [ ] **Step 2: Run — verify it fails** (no control ever focused → flag never set).
- [ ] **Step 3: Implement** — `ElementDescriptor.cs` gains `bool Focused = false` as the last (defaulted) field. In `Build`, in the `include` block:
```csharp
bool focused = Safe(() => el.Properties.HasKeyboardFocus.ValueOrDefault, false);
var descriptor = new ElementDescriptor(rid, ct, aid, name, ancestorAid, indexPath, focused);
```
and pass `focused` to `new SnapshotNode(... focusable, focused, isPassword ...)` (the slot currently hard-coded `false`). `FormatNode` already renders `focused` (Task 0).
- [ ] **Step 4: Run — verify it passes**; re-run `SnapshotModelPinTests` (still green).
- [ ] **Step 5: Commit**
```bash
git add src/FlaUI.Mcp.Core/Perception/ElementDescriptor.cs src/FlaUI.Mcp.Core/Perception/SnapshotEngine.cs test/FlaUI.Mcp.Tests/Perception/FocusedFlagTests.cs
git commit -m "feat(perception): always-on focused descriptor flag (Task 2)"
```

---

### Task 3: `RefRegistry.Resolve` recycle-guard Name compare

**Files:** Modify `RefRegistry.cs`; Test `Perception/RecycleGuardTests.cs` (Desktop).

- [ ] **Step 1: Failing test** — `test/FlaUI.Mcp.Tests/Perception/RecycleGuardTests.cs`:
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
        var (same, diverged) = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            var status = win.FindFirstDescendant(cf => cf.ByAutomationId("Status"))!;
            var rid = status.Properties.RuntimeId.ValueOrDefault;
            var live = status.Name;
            var dSame = new ElementDescriptor(rid, status.ControlType, "Status", live, null, System.Array.Empty<int>());
            var dDiff = new ElementDescriptor(rid, status.ControlType, "Status", live + "-X", null, System.Array.Empty<int>());
            return (RefRegistry.FastPathMatches(status, dSame), RefRegistry.FastPathMatches(status, dDiff));
        });
        Assert.True(same);
        Assert.False(diverged);
    }
}
```
- [ ] **Step 2: Run — verify it fails** (`FastPathMatches` missing).
- [ ] **Step 3: Implement** — add to `RefRegistry`:
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
Replace the fast-path body (L80-86):
```csharp
if (entry.Cached is { } cached)
{
    try { if (FastPathMatches(cached, d)) return cached; }
    catch { /* element gone — fall through */ }
}
```
- [ ] **Step 4: Run** — `RecycleGuardTests|RefResolutionTests|RefRegistryTests` → all PASS.
- [ ] **Step 5: Commit**
```bash
git add src/FlaUI.Mcp.Core/Perception/RefRegistry.cs test/FlaUI.Mcp.Tests/Perception/RecycleGuardTests.cs
git commit -m "fix(perception): strengthen ref recycle guard with live Name compare (Task 3)"
```

---

### Task 4: Scaffolding — `SnapshotCache` + `WaitCoordinator` + `BuildModelAsync` + ALL final ctors

**Files:** Create `SnapshotCache.cs`, `WaitCoordinator.cs`; Modify `PerceptionManager.cs`, `SnapshotTools.cs`, `Program.cs`, `SnapshotToolsTests.cs`; Test `Perception/SnapshotCacheTests.cs` (Desktop).

**Front-loads every ctor/DI change** so all later tasks add methods to existing types without touching signatures (fixes the cross-task isolated-compile hazard). `WaitCoordinator` is introduced as a real class with its ctor + `Matches` helper; its wait methods are added in Tasks 9-10. `WindowTools` does **not** change (its `list_windows` extension is Win32-only — Task 8).

- [ ] **Step 1: Failing test** — `test/FlaUI.Mcp.Tests/Perception/SnapshotCacheTests.cs`:
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
- [ ] **Step 2: Run — verify it fails** (ctor arity / `SnapshotCache`).
- [ ] **Step 3: Create `SnapshotCache`** — `src/FlaUI.Mcp.Core/Perception/SnapshotCache.cs`:
```csharp
namespace FlaUI.Mcp.Core.Perception;

/// <summary>Bounded store of recent snapshot models keyed by snapshotId. Cached SnapshotModels are
/// immutable records; eviction is pure LRU (a diff/stats requesting an evicted baseline gets a clean
/// SnapshotNotFound). Separate from RefRegistry — BeginSnapshot never touches this cache.</summary>
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
            _byId[snapshotId] = model; _order.AddFirst(snapshotId);
            while (_order.Count > Capacity) { var e = _order.Last!.Value; _order.RemoveLast(); _byId.Remove(e); }
        }
    }
    public bool TryGet(string snapshotId, out SnapshotModel? model)
    { lock (_gate) { return _byId.TryGetValue(snapshotId, out model); } }
}
```
- [ ] **Step 4: Create `WaitCoordinator` shell** — `src/FlaUI.Mcp.Core/Perception/WaitCoordinator.cs`:
```csharp
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Core.Perception;

public sealed record WaitForResult(bool Satisfied, string? Ref, int ElapsedMs, string? SnapshotId);
public sealed record WaitStableResult(bool Stable, int ElapsedMs, string? SnapshotId);

/// <summary>Polling read-only wait conditions. Each poll issues ONE short query-STA Build with a
/// THROWAWAY RefRegistry (no durable-registry growth) and Task.Delays off-STA. Offscreen subtrees
/// are culled (IncludeOffscreen=false) to bound per-poll cost. (Wait methods added in Tasks 9-10.)</summary>
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

    // Poll-time walk options: surface any element by selector but cull offscreen subtrees for perf.
    internal static SnapshotOptions PollOptions => new() { InteractiveOnly = false, IncludeOffscreen = false };
}
```
(`_perception` is unused until Tasks 9-10; suppress the warning by referencing it there. If the repo treats unused-field as an error, add the wait methods' skeletons in this task instead.)
- [ ] **Step 5: Wire `PerceptionManager`** — replace fields/ctor + `SnapshotAsync`; add `BuildModelAsync`:
```csharp
private readonly WindowManager _windows;
private readonly RefRegistry _refs;
private readonly SnapshotCache _cache;
public PerceptionManager(WindowManager windows, RefRegistry refs, SnapshotCache cache)
{ _windows = windows; _refs = refs; _cache = cache; }

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
            ? win : refs.Resolve(handle.Id, options.RootRef!, PopupFinder.SearchRoots(win, desktop));
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

// Durable wait-snapshot (used by Tasks 9-10 on satisfaction): registers refs + caches.
public async Task<(string SnapshotId, SnapshotModel Model)> SnapshotModelForWaitAsync(
    WindowHandle handle, SnapshotOptions options)
{
    var (snapshotId, model) = await BuildModelAsync(handle, options, _refs);
    _cache.Put(snapshotId, model);
    return (snapshotId, model);
}
```
- [ ] **Step 6: Wire `SnapshotTools` ctor** — replace fields/ctor:
```csharp
private readonly PerceptionManager _perception;
private readonly WaitCoordinator _wait;
public SnapshotTools(PerceptionManager perception, WaitCoordinator wait)
{ _perception = perception; _wait = wait; }
```
- [ ] **Step 7: DI + migrate call sites** — `Program.cs`: after `RefRegistry` (L28) add `AddSingleton<...SnapshotCache>()` and `AddSingleton<...WaitCoordinator>()`. Update `SnapshotToolsTests.cs:23`/`:41` to `new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache())` and `new SnapshotTools(perception, new WaitCoordinator(perception))`.
- [ ] **Step 8: Run** — `SnapshotCacheTests|SnapshotToolsTests` + `dotnet test --filter "Category!=Desktop"` → all PASS.
- [ ] **Step 9: Commit**
```bash
git add src/FlaUI.Mcp.Core/Perception/SnapshotCache.cs src/FlaUI.Mcp.Core/Perception/WaitCoordinator.cs src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs src/FlaUI.Mcp.Server/Program.cs test/FlaUI.Mcp.Tests/Server/SnapshotToolsTests.cs test/FlaUI.Mcp.Tests/Perception/SnapshotCacheTests.cs
git commit -m "feat(perception): scaffolding — SnapshotCache, WaitCoordinator, final ctors, BuildModelAsync (Task 4)"
```

---

### Task 5: `DpiHelper` + `desktop_get_bounds`

**Files:** Create `Geometry/DpiHelper.cs`, `ScreenshotTools.cs`; Modify `Program.cs`; Test `Perception/GetBoundsTests.cs` (Desktop). All ctors now exist (Task 4), so tests use final arities directly.

- [ ] **Step 1: Create `DpiHelper`** — `src/FlaUI.Mcp.Core/Perception/Geometry/DpiHelper.cs`:
```csharp
using System.Runtime.InteropServices;
namespace FlaUI.Mcp.Core.Perception.Geometry;
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
- [ ] **Step 2: Failing test** — `test/FlaUI.Mcp.Tests/Perception/GetBoundsTests.cs`:
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
        var window = new WindowTools(mgr);
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
        Assert.True(doc.RootElement.GetProperty("dpiScale").GetDouble() > 0);
        Assert.False(doc.RootElement.GetProperty("isOffscreen").GetBoolean());
    }
}
```
- [ ] **Step 3: Run — verify it fails** (`ScreenshotTools` missing).
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
            return ToolResponse.Ok(new { bounds = new { x = r.X, y = r.Y, w = r.Width, h = r.Height }, dpiScale = dpi, isOffscreen = r.Item5 });
        });
}
```
- [ ] **Step 5: DI** — `Program.cs` after `SnapshotTools` (L30): `AddSingleton<ScreenshotTools>()`.
- [ ] **Step 6: Run — verify it passes.**
- [ ] **Step 7: Commit**
```bash
git add src/FlaUI.Mcp.Core/Perception/Geometry/DpiHelper.cs src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs src/FlaUI.Mcp.Server/Program.cs test/FlaUI.Mcp.Tests/Perception/GetBoundsTests.cs
git commit -m "feat(perception): DpiHelper + desktop_get_bounds (Task 5)"
```

---

### Task 6: `desktop_snapshot_stats`

**Files:** Modify `SnapshotEngine.cs` (`IsInteractiveNode`), `PerceptionManager.cs`, `SnapshotTools.cs`; Test `Server/SnapshotStatsTests.cs` (Desktop).

`window` form walks a FULL tree (`InteractiveOnly=false, IncludeOffscreen=true`) so offscreen/total are real (intentionally a fuller view than a pruned snapshot — documented in the tool description); `snapshotId` form tallies the cached model as-snapshotted.

- [ ] **Step 1: Predicate** — `SnapshotEngine.cs`:
```csharp
public static bool IsInteractiveNode(SnapshotNode n)
    => InteractiveTypes.Contains(n.ControlType)
       || (n.ControlType == ControlType.Text && !string.IsNullOrWhiteSpace(n.Name))
       || n.Focusable || n.Patterns.Count > 0;
```
- [ ] **Step 2: `PerceptionManager`**:
```csharp
public sealed record SnapshotStats(string SnapshotId, int Total, int Interactive, int Offscreen,
    int Redacted, IReadOnlyDictionary<string, int> ByControlType);
private static SnapshotStats Tally(string id, SnapshotModel m)
{
    var nodes = m.Nodes.ToList();
    return new SnapshotStats(id, nodes.Count, nodes.Count(SnapshotEngine.IsInteractiveNode),
        nodes.Count(n => n.IsOffscreen), nodes.Count(n => n.IsPassword),
        nodes.GroupBy(n => n.ControlType.ToString()).ToDictionary(g => g.Key, g => g.Count()));
}
public async Task<SnapshotStats> StatsByWindowAsync(WindowHandle handle)
{
    var (id, model) = await BuildModelAsync(handle, new SnapshotOptions { InteractiveOnly = false, IncludeOffscreen = true }, _refs);
    _cache.Put(id, model);
    return Tally(id, model);
}
public SnapshotStats StatsBySnapshotId(string id)
{
    if (!_cache.TryGet(id, out var model) || model is null)
        throw new ToolException(ToolErrorCode.SnapshotNotFound,
            $"Snapshot '{id}' is not in the cache (evicted or never taken).", "take a fresh desktop_snapshot and use its snapshotId");
    return Tally(id, model);
}
```
- [ ] **Step 3: Failing test** — `test/FlaUI.Mcp.Tests/Server/SnapshotStatsTests.cs`:
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
        Assert.Equal(1, doc.RootElement.GetProperty("redacted").GetInt32());
        Assert.True(doc.RootElement.GetProperty("byControlType").TryGetProperty("Button", out _));
    }
    [Fact]
    public async Task Stats_requires_exactly_one_arg()
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
        var snap = new SnapshotTools(perception, new WaitCoordinator(perception));
        var window = new WindowTools(mgr);
        var opened = await window.DesktopOpenWindow("pid", _app.Process.Id.ToString());
        var handle = JsonDocument.Parse(opened).RootElement.GetProperty("handle").GetString()!;
        return (snap, handle);
    }
}
```
- [ ] **Step 4: Run — verify it fails.**
- [ ] **Step 5: Tool** (`SnapshotTools.cs`, add `using FlaUI.Mcp.Core.Errors;`):
```csharp
[McpServerTool(ReadOnly = true), Description("Cheap orientation: control counts (total/interactive/offscreen/redacted, FULL tree) + a per-ControlType histogram, without the tree text. Supply exactly one of window (fresh full walk — a fuller view than a pruned desktop_snapshot) or snapshotId (a prior cached snapshot, tallied as-snapshotted).")]
public Task<string> DesktopSnapshotStats(
    [Description("Window handle. Provide this OR snapshotId.")] string? window = null,
    [Description("A prior snapshotId, e.g. w1:4. Provide this OR window.")] string? snapshotId = null)
    => ToolResponse.Guard(async () =>
    {
        if (string.IsNullOrEmpty(window) == string.IsNullOrEmpty(snapshotId))
            throw new ToolException(ToolErrorCode.InvalidArguments, "Provide exactly one of 'window' or 'snapshotId'.", "pass a window handle or a snapshotId");
        var s = string.IsNullOrEmpty(window) ? _perception.StatsBySnapshotId(snapshotId!) : await _perception.StatsByWindowAsync(new WindowHandle(window!));
        return ToolResponse.Ok(new { snapshotId = s.SnapshotId, total = s.Total, interactive = s.Interactive, offscreen = s.Offscreen, redacted = s.Redacted, byControlType = s.ByControlType });
    });
```
- [ ] **Step 6: Run — verify it passes.**
- [ ] **Step 7: Commit**
```bash
git add src/FlaUI.Mcp.Core/Perception/SnapshotEngine.cs src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs test/FlaUI.Mcp.Tests/Server/SnapshotStatsTests.cs
git commit -m "feat(perception): desktop_snapshot_stats (Task 6)"
```

---

### Task 7: `desktop_snapshot_diff`

**Files:** Create `SnapshotDiff.cs`; Modify `PerceptionManager.cs`, `SnapshotTools.cs`; Test `Server/SnapshotDiffTests.cs` (Desktop).

Explicit `baselineSnapshotId` (required). **Identity = composite `(ControlType, AutomationId, RuntimeId)`** (fall back to `(ControlType, AutomationId, Name)` when no RuntimeId) — so a recycled RuntimeId on a different control does NOT collide (review §7 fix). `changed` = same identity, differing **Name/Enabled/Focused** (Value is not in the model — review §6).

- [ ] **Step 1: Diff core** — `src/FlaUI.Mcp.Core/Perception/SnapshotDiff.cs`:
```csharp
namespace FlaUI.Mcp.Core.Perception;

public sealed record NodeState(string Name, bool Enabled, bool Focused);
public sealed record DiffDescriptor(string Ref, string ControlType, string AutomationId, string Name);
public sealed record ChangedEntry(string Ref, NodeState Was, NodeState Now);
public sealed record SnapshotDiffResult(string BaselineSnapshotId, string CurrentSnapshotId,
    IReadOnlyList<DiffDescriptor> Added, IReadOnlyList<DiffDescriptor> Removed, IReadOnlyList<ChangedEntry> Changed);

public static class SnapshotDiff
{
    // Composite identity: ControlType + AutomationId always; RuntimeId when present (so a recycled
    // RuntimeId on a different control type/automationId can't alias), else Name.
    private static string Identity(SnapshotNode n)
        => n.RuntimeId.Count > 0
            ? $"{n.ControlType}|{n.AutomationId}|rid:{string.Join(",", n.RuntimeId)}"
            : $"{n.ControlType}|{n.AutomationId}|name:{n.Name}";
    private static DiffDescriptor Desc(SnapshotNode n) => new(n.Ref, n.ControlType.ToString(), n.AutomationId, n.Name);
    private static NodeState State(SnapshotNode n) => new(n.Name, n.Enabled, n.Focused);

    public static SnapshotDiffResult Compute(string baselineId, SnapshotModel baseline, string currentId, SnapshotModel current)
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
        throw new ToolException(ToolErrorCode.SnapshotNotFound, $"Baseline snapshot '{baselineSnapshotId}' is not in the cache.", "re-take the baseline snapshot");
    var baseWindowId = baselineSnapshotId.Split(':')[0];
    if (!string.Equals(baseWindowId, handle.Id, System.StringComparison.Ordinal))
        throw new ToolException(ToolErrorCode.SnapshotWindowMismatch, $"Baseline '{baselineSnapshotId}' belongs to window '{baseWindowId}', not '{handle.Id}'.", "pass a baselineSnapshotId from the same window");
    var (currentId, current) = await BuildModelAsync(handle, new SnapshotOptions(), _refs);
    _cache.Put(currentId, current);
    return SnapshotDiff.Compute(baselineSnapshotId, baseline, currentId, current);
}
```
- [ ] **Step 3: Failing test** — `test/FlaUI.Mcp.Tests/Server/SnapshotDiffTests.cs`:
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
        var (snap, inter, window, handle) = await Setup();
        var baseJson = await snap.DesktopSnapshot(handle, fullProperties: true);
        var baselineId = JsonDocument.Parse(baseJson).RootElement.GetProperty("snapshotId").GetString()!;
        var tree = JsonDocument.Parse(baseJson).RootElement.GetProperty("tree").GetString()!;
        var okRef = RefFor(tree, "aid=OkButton");
        await inter.DesktopInvoke(handle, okRef);
        var diffJson = await snap.DesktopSnapshotDiff(handle, baselineId);
        using var doc = JsonDocument.Parse(diffJson);
        Assert.Equal(baselineId, doc.RootElement.GetProperty("baselineSnapshotId").GetString());
        Assert.True(doc.RootElement.GetProperty("changed").GetArrayLength() >= 1);
    }
    [Fact]
    public async Task Diff_rejects_a_missing_baseline()
    {
        var (snap, _, _, handle) = await Setup();
        var json = await snap.DesktopSnapshotDiff(handle, handle + ":999");
        Assert.Equal("SnapshotNotFound", JsonDocument.Parse(json).RootElement.GetProperty("error").GetString());
    }
    private static string RefFor(string tree, string needle)
    { var line = tree.Split('\n').First(l => l.Contains(needle)); return line[(line.IndexOf('[') + 1)..line.IndexOf(']')]; }
    private async Task<(SnapshotTools, InteractionTools, WindowTools, string)> Setup()
    {
        var dispatcher = new AutomationDispatcher();
        var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var snap = new SnapshotTools(perception, new WaitCoordinator(perception));
        var inter = new InteractionTools(perception, mgr, ServerOptions.FromArgs(System.Array.Empty<string>()));
        var window = new WindowTools(mgr);
        var opened = await window.DesktopOpenWindow("pid", _app.Process.Id.ToString());
        var handle = JsonDocument.Parse(opened).RootElement.GetProperty("handle").GetString()!;
        return (snap, inter, window, handle);
    }
}
```
(STATE-VERIFY `ServerOptions.FromArgs` — referenced `Program.cs:22`.)
- [ ] **Step 4: Run — verify it fails.**
- [ ] **Step 5: Tool** (`SnapshotTools.cs`):
```csharp
[McpServerTool(ReadOnly = true), Description("Diff a window's CURRENT tree against an explicit baseline snapshotId. Returns added/removed/changed (Name/Enabled/Focused) keyed by composite identity (ControlType+AutomationId+RuntimeId, else +Name). Result refs belong to the new currentSnapshotId.")]
public Task<string> DesktopSnapshotDiff(
    [Description("Window handle, e.g. w1.")] string window,
    [Description("REQUIRED baseline snapshotId to diff against, e.g. w1:2.")] string baselineSnapshotId)
    => ToolResponse.Guard(async () =>
    {
        var d = await _perception.DiffAsync(new WindowHandle(window), baselineSnapshotId);
        return ToolResponse.Ok(new
        {
            baselineSnapshotId = d.BaselineSnapshotId, currentSnapshotId = d.CurrentSnapshotId,
            added = d.Added.Select(a => new { @ref = a.Ref, controlType = a.ControlType, automationId = a.AutomationId, name = a.Name }),
            removed = d.Removed.Select(a => new { @ref = a.Ref, controlType = a.ControlType, automationId = a.AutomationId, name = a.Name }),
            changed = d.Changed.Select(c => new { @ref = c.Ref, was = c.Was, now = c.Now })
        });
    });
```
- [ ] **Step 6: Run — verify it passes.**
- [ ] **Step 7: Commit**
```bash
git add src/FlaUI.Mcp.Core/Perception/SnapshotDiff.cs src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs test/FlaUI.Mcp.Tests/Server/SnapshotDiffTests.cs
git commit -m "feat(perception): desktop_snapshot_diff with composite identity (Task 7)"
```

---

### Task 8: `desktop_list_windows` extension (`includeBounds`/`zOrder` only)

**Files:** Modify `WindowManager.cs`, `WindowTools.cs`; Test `Server/ListWindowsExtensionTests.cs` (Desktop).

**`includeStats` is intentionally NOT added** — walking each window via UIA reintroduces the hang-on-unresponsive-window the Win32 enumeration (commit `57710bd`) eliminated (review §6). Per-window stats stay a deliberate, scoped `snapshot_stats` call. `WindowTools` ctor is unchanged (Win32-only path → no PerceptionManager dependency). Default output unchanged (back-compat).

- [ ] **Step 1: `WindowManager`** — extend `WindowInfo` + add the bounds/zorder overload + a query passthrough (for full-desktop capture in Task 13):
```csharp
public sealed record WindowBounds(int X, int Y, int W, int H);
public sealed record WindowInfo(string Title, string ProcessName, int Pid, bool IsForeground,
    WindowBounds? Bounds = null, int? ZOrder = null);
```
P/Invoke (after L142):
```csharp
[StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
[DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
```
```csharp
public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync() => ListWindowsAsync(false);
public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(bool includeBounds) =>
    _dispatcher.RunQueryAsync<IReadOnlyList<WindowInfo>>(() =>
    {
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
/// <summary>Run an arbitrary read on the query STA (full-desktop capture needs an STA hop without a window).</summary>
public Task<T> RunOnQueryAsync<T>(Func<T> func) => _dispatcher.RunQueryAsync(func);
```
Replace the old `ListWindowsAsync()` body (L30-40) — logic now in `ListWindowsAsync(bool)`.
- [ ] **Step 2: Failing test** — `test/FlaUI.Mcp.Tests/Server/ListWindowsExtensionTests.cs`:
```csharp
using System.Linq;
using System.Text.Json;
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
        var tools = new WindowTools(mgr);
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
- [ ] **Step 3: Run — verify it fails.**
- [ ] **Step 4: `WindowTools.DesktopListWindows`** (ctor unchanged):
```csharp
[McpServerTool(ReadOnly = true), Description("List top-level desktop windows (title, process, pid, isForeground). Opt-in includeBounds adds absolute physical-px bounds + zOrder (0=topmost, for occlusion reasoning). Pure Win32 — never blocks on an unresponsive window. For per-window control counts, open a window and call desktop_snapshot_stats.")]
public Task<string> DesktopListWindows(
    [Description("Add bounds + zOrder to each window (default false).")] bool includeBounds = false)
    => ToolResponse.Guard(async () =>
    {
        var windows = await _windows.ListWindowsAsync(includeBounds);
        return includeBounds
            ? ToolResponse.Ok(windows.Select(w => new { title = w.Title, processName = w.ProcessName, pid = w.Pid, isForeground = w.IsForeground, bounds = w.Bounds, zOrder = w.ZOrder }))
            : ToolResponse.Ok(windows.Select(w => new { title = w.Title, processName = w.ProcessName, pid = w.Pid, isForeground = w.IsForeground }));
    });
```
- [ ] **Step 5: Run** — `ListWindowsExtensionTests|WindowToolsTests` → all PASS (default unchanged).
- [ ] **Step 6: Commit**
```bash
git add src/FlaUI.Mcp.Core/Windows/WindowManager.cs src/FlaUI.Mcp.Server/Tools/WindowTools.cs test/FlaUI.Mcp.Tests/Server/ListWindowsExtensionTests.cs
git commit -m "feat(perception): desktop_list_windows includeBounds/zOrder (Task 8)"
```

---

### Task 9: `desktop_wait_for` (WaitCoordinator)

**Files:** Modify `WaitCoordinator.cs`, `PerceptionManager.cs` (live valueEquals read), `SnapshotTools.cs`, TestApp (delayed-reveal); Test `Server/WaitForTests.cs` (Desktop).

Selector against a FRESH tree each poll (`PollOptions` = InteractiveOnly=false, IncludeOffscreen=false) using a throwaway `RefRegistry`. Timeout ⇒ `{satisfied:false}`. `valueEquals` re-reads the **one matched element live** through `ValuePattern → Name → LegacyIAccessiblePattern.Value` (spec fallback; bounded to one element/poll — no per-node cost).

- [ ] **Step 1: Live value read on `PerceptionManager`** (full spec fallback chain, one element):
```csharp
public Task<string?> ReadSelectorValueAsync(WindowHandle handle, string by, string value) =>
    _windows.RunWithWindowAndDesktopAsync<string?>(handle, (win, desktop) =>
    {
        AutomationElement? Match()
        {
            try
            {
                return by switch
                {
                    "automationId" => win.FindFirstDescendant(cf => cf.ByAutomationId(value)),
                    "name" => win.FindFirstDescendant(cf => cf.ByName(value)),
                    "controlType" => win.FindAllDescendants().FirstOrDefault(e => { try { return e.ControlType.ToString().Equals(value, System.StringComparison.OrdinalIgnoreCase); } catch { return false; } }),
                    _ => null
                };
            }
            catch { return null; }
        }
        var el = Match();
        if (el is null) return null;
        try { var vp = el.Patterns.Value.PatternOrDefault; if (vp is not null) return vp.Value.ValueOrDefault; } catch { }
        try { var nm = el.Name; if (!string.IsNullOrEmpty(nm)) return nm; } catch { }
        try { var la = el.Patterns.LegacyIAccessible.PatternOrDefault; if (la is not null) return la.Value.ValueOrDefault; } catch { }
        return null;
    });
```
(STATE-VERIFY FlaUI accessor `el.Patterns.LegacyIAccessible.PatternOrDefault.Value` — adjust to the FlaUI 5 name if different.)
- [ ] **Step 2: `WaitCoordinator.WaitForAsync`**:
```csharp
public async Task<WaitForResult> WaitForAsync(WindowHandle handle, string by, string value,
    string until, string? equals, int timeoutMs, int pollIntervalMs)
{
    if (until == "valueEquals" && equals is null)
        throw new ToolException(ToolErrorCode.InvalidArguments, "until:valueEquals requires 'equals'.", "pass equals=<expected value>");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (true)
    {
        var (_, model) = await _perception.BuildModelAsync(handle, PollOptions, new RefRegistry());
        var match = model.Nodes.FirstOrDefault(n => Matches(n, by, value));
        bool satisfied;
        if (until == "valueEquals")
        {
            var live = match is null ? null : await _perception.ReadSelectorValueAsync(handle, by, value);
            satisfied = match is not null && string.Equals(live, equals, System.StringComparison.Ordinal);
        }
        else satisfied = until switch
        {
            "exists" => match is not null, "gone" => match is null, "enabled" => match is { Enabled: true }, _ => match is not null
        };
        if (satisfied)
        {
            var (snapId, real) = await _perception.SnapshotModelForWaitAsync(handle, PollOptions);
            var realMatch = real.Nodes.FirstOrDefault(n => Matches(n, by, value));
            return new WaitForResult(true, realMatch?.Ref, (int)sw.ElapsedMilliseconds, snapId);
        }
        if (sw.ElapsedMilliseconds >= timeoutMs) return new WaitForResult(false, null, (int)sw.ElapsedMilliseconds, null);
        await Task.Delay(pollIntervalMs);
    }
}
```
(Add `using FlaUI.Mcp.Core.Errors;` and `using System.Linq;` to WaitCoordinator.)
- [ ] **Step 3: TestApp delayed-reveal** — name the root `<StackPanel ... x:Name="RootPanel">` (L6) and after `FreezeButton` (L53) add `<Button x:Name="DelayRevealButton" AutomationProperties.AutomationId="DelayRevealButton" Content="reveal after delay" Click="DelayRevealButton_Click" Margin="0,4,0,0"/>`. In `MainWindow.xaml.cs`:
```csharp
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
- [ ] **Step 4: Tool** (`SnapshotTools.cs`):
```csharp
[McpServerTool(ReadOnly = true), Description("Poll a window until a selector condition holds. by=automationId|name|controlType, value=target, until=exists|enabled|gone|valueEquals (equals required for valueEquals; compares ValuePattern→Name→LegacyIAccessible). Timeout returns {satisfied:false} (NOT an error). On success returns the matched ref + a fresh snapshotId. Polls are transient (no ref growth).")]
public Task<string> DesktopWaitFor(
    [Description("Window handle, e.g. w1.")] string window,
    [Description("automationId|name|controlType.")] string by,
    [Description("Match target for 'by'.")] string value,
    [Description("exists|enabled|gone|valueEquals (default exists).")] string until = "exists",
    [Description("Required iff until=valueEquals.")] string? equals = null,
    [Description("Total wait budget ms (default 5000).")] int timeoutMs = 5000,
    [Description("Poll interval ms (default 500).")] int pollIntervalMs = 500)
    => ToolResponse.Guard(async () =>
    {
        var r = await _wait.WaitForAsync(new WindowHandle(window), by, value, until, equals, timeoutMs, pollIntervalMs);
        return ToolResponse.Ok(new { satisfied = r.Satisfied, @ref = r.Ref, elapsedMs = r.ElapsedMs, snapshotId = r.SnapshotId });
    });
```
- [ ] **Step 5: Failing test → pass** — `test/FlaUI.Mcp.Tests/Server/WaitForTests.cs`:
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
    public async Task Missing_control_times_out_as_data()
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
        var window = new WindowTools(mgr);
        var opened = await window.DesktopOpenWindow("pid", _app.Process.Id.ToString());
        var handle = JsonDocument.Parse(opened).RootElement.GetProperty("handle").GetString()!;
        return (snap, inter, handle);
    }
}
```
- [ ] **Step 6: Commit**
```bash
git add src/FlaUI.Mcp.Core/Perception/WaitCoordinator.cs src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs test/FlaUI.Mcp.TestApp/MainWindow.xaml test/FlaUI.Mcp.TestApp/MainWindow.xaml.cs test/FlaUI.Mcp.Tests/Server/WaitForTests.cs
git commit -m "feat(perception): desktop_wait_for with culled polls + Legacy value fallback (Task 9)"
```

---

### Task 10: `desktop_wait_for_stable`

**Files:** Modify `WaitCoordinator.cs`, `SnapshotTools.cs`, TestApp (`Ticker`); Test `Server/WaitForStableTests.cs` (Desktop).

Structural signature = ordered `(ControlType, AutomationId, Depth)` over the scoped subtree; `includeText` folds `Name`. Stable across `ceil(quietMs/pollIntervalMs)` polls. Timeout ⇒ `{stable:false}`. Same culled `PollOptions`.

- [ ] **Step 1: `WaitCoordinator`** (Value dropped from the signature — node has no Value; includeText uses Name):
```csharp
private static string Signature(IEnumerable<SnapshotNode> nodes, bool includeText)
    => string.Join("\n", nodes.Select(n => includeText
        ? $"{n.ControlType}:{n.AutomationId}:{n.Depth}:{n.Name}"
        : $"{n.ControlType}:{n.AutomationId}:{n.Depth}"));
private static IReadOnlyList<SnapshotNode> Subtree(SnapshotModel model, string? by, string? value)
{
    var nodes = model.Nodes.ToList();
    if (string.IsNullOrEmpty(by) || string.IsNullOrEmpty(value)) return nodes;
    int start = nodes.FindIndex(n => Matches(n, by, value));
    if (start < 0) return System.Array.Empty<SnapshotNode>();
    var scope = nodes[start]; var sub = new List<SnapshotNode> { scope };
    for (int i = start + 1; i < nodes.Count && nodes[i].Depth > scope.Depth; i++) sub.Add(nodes[i]);
    return sub;
}
public async Task<WaitStableResult> WaitForStableAsync(WindowHandle handle, string? by, string? value,
    bool includeText, int quietMs, int timeoutMs, int pollIntervalMs)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    int needed = (int)System.Math.Ceiling((double)quietMs / System.Math.Max(1, pollIntervalMs));
    string? last = null; int stableCount = 0;
    bool scopeRequested = !string.IsNullOrEmpty(by) && !string.IsNullOrEmpty(value);
    while (true)
    {
        var (_, model) = await _perception.BuildModelAsync(handle, PollOptions, new RefRegistry());
        var sub = Subtree(model, by, value);
        if (scopeRequested && sub.Count == 0)
            throw new ToolException(ToolErrorCode.SelectorNoMatch, $"No element matched {by}={value} to scope stability.", "widen or correct the selector");
        var sig = Signature(sub, includeText);
        stableCount = sig == last ? stableCount + 1 : 0; last = sig;
        if (stableCount >= needed)
        {
            var (snapId, _) = await _perception.SnapshotModelForWaitAsync(handle, PollOptions);
            return new WaitStableResult(true, (int)sw.ElapsedMilliseconds, snapId);
        }
        if (sw.ElapsedMilliseconds >= timeoutMs) return new WaitStableResult(false, (int)sw.ElapsedMilliseconds, null);
        await Task.Delay(pollIntervalMs);
    }
}
```
- [ ] **Step 2: TestApp `Ticker`** — after `DelayRevealButton`: `<TextBlock x:Name="Ticker" AutomationProperties.AutomationId="Ticker" Text="0" Margin="0,4,0,0"/>`. In the ctor after `Secret.Password = SecretValue;`:
```csharp
var ticker = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(120) };
int tick = 0; ticker.Tick += (_, _) => Ticker.Text = (++tick).ToString(); ticker.Start();
```
- [ ] **Step 3: Tool** (`SnapshotTools.cs`):
```csharp
[McpServerTool(ReadOnly = true), Description("Poll until a window subtree stops structurally changing. Optional scope via by+value (default whole window). includeText folds Name into the signature (wait on a status-text settle; do NOT use on a window with a live clock/counter). Timeout returns {stable:false}.")]
public Task<string> DesktopWaitForStable(
    [Description("Window handle, e.g. w1.")] string window,
    [Description("Optional scope kind: automationId|name|controlType.")] string? by = null,
    [Description("Optional scope value (with 'by').")] string? value = null,
    [Description("Fold Name into the signature (default false).")] bool includeText = false,
    [Description("Quiet window ms (default 500).")] int quietMs = 500,
    [Description("Total budget ms (default 5000).")] int timeoutMs = 5000,
    [Description("Poll interval ms (default 500).")] int pollIntervalMs = 500)
    => ToolResponse.Guard(async () =>
    {
        var r = await _wait.WaitForStableAsync(new WindowHandle(window), by, value, includeText, quietMs, timeoutMs, pollIntervalMs);
        return ToolResponse.Ok(new { stable = r.Stable, elapsedMs = r.ElapsedMs, snapshotId = r.SnapshotId });
    });
```
- [ ] **Step 4: Failing test → pass** — `test/FlaUI.Mcp.Tests/Server/WaitForStableTests.cs`:
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
    public async Task Structure_is_stable_despite_a_live_ticker()
    {
        var (snap, handle) = await Setup();
        var json = await snap.DesktopWaitForStable(handle, null, null, false, 500, 5000, 250);
        Assert.True(JsonDocument.Parse(json).RootElement.GetProperty("stable").GetBoolean());
    }
    [Fact]
    public async Task IncludeText_on_a_live_ticker_times_out_unstable()
    {
        var (snap, handle) = await Setup();
        var json = await snap.DesktopWaitForStable(handle, null, null, true, 500, 1500, 250);
        Assert.False(JsonDocument.Parse(json).RootElement.GetProperty("stable").GetBoolean());
    }
    private async Task<(SnapshotTools, string)> Setup()
    {
        var dispatcher = new AutomationDispatcher();
        var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var snap = new SnapshotTools(perception, new WaitCoordinator(perception));
        var window = new WindowTools(mgr);
        var opened = await window.DesktopOpenWindow("pid", _app.Process.Id.ToString());
        var handle = JsonDocument.Parse(opened).RootElement.GetProperty("handle").GetString()!;
        return (snap, handle);
    }
}
```
- [ ] **Step 5: Commit**
```bash
git add src/FlaUI.Mcp.Core/Perception/WaitCoordinator.cs src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs test/FlaUI.Mcp.TestApp/MainWindow.xaml test/FlaUI.Mcp.TestApp/MainWindow.xaml.cs test/FlaUI.Mcp.Tests/Server/WaitForStableTests.cs
git commit -m "feat(perception): desktop_wait_for_stable (Task 10)"
```

---

### Task 11: `desktop_get_focused_element` (chainable)

**Files:** Modify `WindowManager.cs`, `PerceptionManager.cs`, `SnapshotTools.cs`; Test `Server/GetFocusedElementTests.cs` (Desktop).

Returns the focused element's ref **scoped to its owning window's handle** so it is actionable (review §7). Resolves the owning top-level HWND, opens/registers it as a `WindowHandle`, snapshots that window, and returns the matching ref. `window.handle` is null only when the owning window genuinely can't be resolved (then ref is in a focus-only namespace — documented). `AccessDeniedIntegrity` on secure desktop; `NoFocusedElement` otherwise.

- [ ] **Step 1: STATE-VERIFY** — FlaUI `UIA3Automation.FocusedElement()`; element→owning-window via `el.Properties.NativeWindowHandle` walking up, or `automation.FromHandle(rootHwnd)`. Confirm accessor names.
- [ ] **Step 2: `WindowManager`** — focused element + its top-level HWND, registered as a handle:
```csharp
public Task<(WindowHandle Handle, string Title, int Pid)?> ResolveFocusedWindowAsync() =>
    _dispatcher.RunQueryAsync<(WindowHandle, string, int)?>(() =>
    {
        var focused = _automation.FocusedElement();
        if (focused is null) return null;
        IntPtr hwnd;
        try { hwnd = new IntPtr(focused.Properties.NativeWindowHandle.ValueOrDefault.ToInt64()); } catch { hwnd = IntPtr.Zero; }
        // Walk to the top-level owner if the element itself has no hwnd.
        if (hwnd == IntPtr.Zero)
            try { var top = focused.Parent; while (top != null && top.Parent != null && top.Parent.ControlType != FlaUI.Core.Definitions.ControlType.Pane) top = top.Parent; hwnd = top is null ? IntPtr.Zero : new IntPtr(top.Properties.NativeWindowHandle.ValueOrDefault.ToInt64()); } catch { }
        int pid = -1; string title = "";
        try { pid = focused.Properties.ProcessId.ValueOrDefault; } catch { }
        try { title = focused.Properties.Name.ValueOrDefault ?? ""; } catch { }
        if (hwnd == IntPtr.Zero) return null;
        var handle = Register(_automation.FromHandle(hwnd).AsWindow(), pid);
        return (handle, title, pid);
    });
```
(STATE-VERIFY `NativeWindowHandle` value type — it may already be `IntPtr`; simplify the conversion accordingly.)
- [ ] **Step 3: `PerceptionManager.GetFocusedElementAsync`** — snapshot the owning window, find the focused node:
```csharp
public sealed record FocusedElementInfo(string Ref, string DescriptorLine, string Title, int Pid, string? WindowHandle);
public async Task<FocusedElementInfo> GetFocusedElementAsync()
{
    (WindowHandle Handle, string Title, int Pid)? owner;
    try { owner = await _windows.ResolveFocusedWindowAsync(); }
    catch (System.UnauthorizedAccessException)
    { throw new ToolException(ToolErrorCode.AccessDeniedIntegrity, "Cannot read the focused element (secure/UAC desktop).", "dismiss the secure prompt and retry"); }
    if (owner is null)
        throw new ToolException(ToolErrorCode.NoFocusedElement, "No element currently has UIA focus.", "click or tab to a control, then retry");
    var o = owner.Value;
    // Snapshot the owning window (full tree) and pick the focused node so the ref is actionable.
    var (snapId, model) = await BuildModelAsync(o.Handle, new SnapshotOptions { InteractiveOnly = false, IncludeOffscreen = true }, _refs);
    _cache.Put(snapId, model);
    var node = model.Nodes.FirstOrDefault(n => n.Focused) ?? model.Nodes.First();
    var line = SnapshotEngine.Render(new SnapshotModel(new[] { (SnapshotItem)node }), new SnapshotOptions()).TrimEnd('\r', '\n');
    return new FocusedElementInfo(node.Ref, line, o.Title, o.Pid, o.Handle.Id);
}
```
- [ ] **Step 4: Failing test** — `test/FlaUI.Mcp.Tests/Server/GetFocusedElementTests.cs`:
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
    public async Task Returns_focused_ref_and_window_handle_after_focusing_Ok()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var snap = new SnapshotTools(perception, new WaitCoordinator(perception));
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        { win.AsWindow().Focus(); win.FindFirstDescendant(cf => cf.ByAutomationId("OkButton"))!.Focus(); return true; });
        var json = await snap.DesktopGetFocusedElement();
        using var doc = JsonDocument.Parse(json);
        Assert.StartsWith("e", doc.RootElement.GetProperty("ref").GetString());
        Assert.StartsWith("w", doc.RootElement.GetProperty("window").GetProperty("handle").GetString());
    }
}
```
- [ ] **Step 5: Tool** (`SnapshotTools.cs`):
```csharp
[McpServerTool(ReadOnly = true), Description("O(1) 'where am I': return the UIA-focused element's ref + descriptor line + owning window handle/title/pid. The ref is scoped to the returned window handle so you can act on it. AccessDeniedIntegrity on a secure/UAC desktop; NoFocusedElement when nothing is focused.")]
public Task<string> DesktopGetFocusedElement()
    => ToolResponse.Guard(async () =>
    {
        var f = await _perception.GetFocusedElementAsync();
        return ToolResponse.Ok(new { @ref = f.Ref, descriptor = f.DescriptorLine, window = new { handle = f.WindowHandle, title = f.Title, pid = f.Pid } });
    });
```
- [ ] **Step 6: Run — verify it passes.**
- [ ] **Step 7: Commit**
```bash
git add src/FlaUI.Mcp.Core/Windows/WindowManager.cs src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs test/FlaUI.Mcp.Tests/Server/GetFocusedElementTests.cs
git commit -m "feat(perception): desktop_get_focused_element (chainable handle) (Task 11)"
```

---

### Task 12: `ScreenCapture` core (off-STA: by-rectangle + redact + clamp + PNG)

**Files:** Create `ScreenCapture.cs`; Test `Perception/ScreenCaptureTests.cs` (Desktop).

Pure capture engine, **no UIA element reads** (bounds + redaction rects are passed in, resolved by the STA caller). Captures by absolute screen rectangle (`Capture.Rectangle`) so it can run OFF the STA. Hard `maxWidth` ceiling 1920. Maps FlaUI capture COM failures to `CaptureUnavailable`.

- [ ] **Step 0: STATE-VERIFY** `System.Drawing` (transitive via FlaUI.Core; add `System.Drawing.Common` to Core csproj only if the build can't resolve `Bitmap`/`Graphics`/`ImageFormat`). Confirm `Capture.Rectangle(System.Drawing.Rectangle)` single-arg (or pass `null` settings).
- [ ] **Step 1: Create `ScreenCapture`** — `src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs`:
```csharp
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FlaUI.Core.Capturing;
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Perception;

public sealed record CaptureResult(byte[] Png, int X, int Y, int W, int H, double ScaleApplied, int Redactions);

/// <summary>Screen-region capture (no occlusion handling — callers focus-first; no UIA element reads).
/// Captures by absolute screen rectangle so it can run OFF the query STA (spec §8). Paints black
/// redaction rects (live bounds passed in), clamps width to a hard ceiling, PNG-encodes.</summary>
public static class ScreenCapture
{
    private const int MaxCaptureWidth = 1920; // hard ceiling — bounds base64 payload even if maxWidth<=0

    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr hDesktop);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    public static bool IsDesktopRenderable()
    {
        var h = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (h == IntPtr.Zero) return false;
        CloseDesktop(h); return true;
    }
    public static Rectangle VirtualScreenBounds() => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    public static CaptureResult CaptureRectangle(Rectangle absolute, IReadOnlyList<Rectangle> redactAbsolute, int maxWidth)
    {
        CaptureImage cap;
        try { cap = Capture.Rectangle(absolute); }
        catch (System.Exception ex) when (ex is COMException or System.Runtime.InteropServices.ExternalException)
        { throw new ToolException(ToolErrorCode.CaptureUnavailable, "Screen capture failed (session may be disconnected/locked).", "reconnect to restore rendering"); }
        using (cap) return Encode(cap.Bitmap, absolute, redactAbsolute, maxWidth);
    }

    private static CaptureResult Encode(Bitmap src, Rectangle captureBounds, IReadOnlyList<Rectangle> redactAbsolute, int maxWidth)
    {
        int cap = maxWidth <= 0 ? MaxCaptureWidth : System.Math.Min(maxWidth, MaxCaptureWidth);
        double scale = src.Width > cap ? (double)cap / src.Width : 1.0;
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
                    (int)System.Math.Round((r.X - captureBounds.X) * scale), (int)System.Math.Round((r.Y - captureBounds.Y) * scale),
                    (int)System.Math.Round(r.Width * scale), (int)System.Math.Round(r.Height * scale));
                if (rel.Width <= 0 || rel.Height <= 0) continue;
                g.FillRectangle(black, rel); painted++;
            }
            using var ms = new MemoryStream();
            outBmp.Save(ms, ImageFormat.Png);
            return new CaptureResult(ms.ToArray(), captureBounds.X, captureBounds.Y, captureBounds.Width, captureBounds.Height, scale, painted);
        }
    }
}
```
- [ ] **Step 2: Failing test** — `test/FlaUI.Mcp.Tests/Perception/ScreenCaptureTests.cs`:
```csharp
using System.Drawing;
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
    public void Desktop_is_renderable_on_a_connected_session() => Assert.True(ScreenCapture.IsDesktopRenderable());
    [Fact]
    public async Task Captures_a_png_of_a_window_rect_with_one_redaction()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        var (winRect, secretRect) = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            var secret = win.FindFirstDescendant(cf => cf.ByAutomationId("Secret"))!;
            return (win.BoundingRectangle, secret.BoundingRectangle);
        });
        var result = ScreenCapture.CaptureRectangle(winRect, new[] { secretRect }, 1600);
        Assert.True(result.Png.Length > 100);
        Assert.Equal(1, result.Redactions);
        Assert.Equal(0x89, result.Png[0]);
        Assert.Equal((byte)'P', result.Png[1]);
    }
}
```
- [ ] **Step 3: Run — verify it passes (2).**
- [ ] **Step 4: Commit**
```bash
git add src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs test/FlaUI.Mcp.Tests/Perception/ScreenCaptureTests.cs
git commit -m "feat(perception): off-STA ScreenCapture (by-rect, redact, clamp, png) (Task 12)"
```

---

### Task 13: `desktop_screenshot` tool + `ToolResponse` image helper

**Files:** Modify `ToolResponse.cs`, `ScreenshotTools.cs`, `PerceptionManager.cs`; Test `Server/ScreenshotToolTests.cs` (Desktop).

**STA collects** geometry only (target rect, password rects across `PopupFinder.SearchRoots`, minimized/denied flags); **off-STA** does `ScreenCapture.CaptureRectangle` + redaction + PNG. **Full-desktop blacks the whole Win32 box of every denylisted window** (review §5). `output:"file"`→`NotImplemented`; minimized→`ElementNotActionable`; denylisted→`TargetDenied`; headless→`CaptureUnavailable`.

- [ ] **Step 1: `ToolResponse` image helpers** (add `using ModelContextProtocol.Protocol;`):
```csharp
public static async Task<CallToolResult> GuardImage(Func<Task<CallToolResult>> body)
{
    try { return await body(); }
    catch (ToolException ex)
    { return ErrResult(ex.Code.ToString(), ex.Message, ex.SuggestedRecovery); }
    catch (Exception ex)
    { return ErrResult("INTERNAL", ex.Message, "re-check arguments and retry"); }
}
private static CallToolResult ErrResult(string code, string message, string? recovery) => new()
{ IsError = true, Content = new List<ContentBlock> { new TextContentBlock { Text = JsonSerializer.Serialize(new { error = code, message, suggestedRecovery = recovery }, Json) } } };
public static CallToolResult Image(byte[] png, object metadata) => new()
{ Content = new List<ContentBlock> { ImageContentBlock.FromBytes(png, "image/png"), new TextContentBlock { Text = JsonSerializer.Serialize(metadata, Json) } } };
```
(`suggestedRecovery` is always populated — never null in our throws; the INTERNAL path passes a default.)
- [ ] **Step 2: `PerceptionManager` — STA geometry collection** (no capture here):
```csharp
public sealed record CaptureGeometry(System.Drawing.Rectangle Bounds, IReadOnlyList<System.Drawing.Rectangle> PasswordRects, bool Minimized, bool Denied, string? DeniedProcess);

public Task<CaptureGeometry> ResolveWindowCaptureGeometryAsync(WindowHandle handle, string? @ref) =>
    _windows.RunWithWindowAndDesktopAsync(handle, (win, desktop) =>
    {
        var procName = SafeProcessName(win);
        if (PerceptionPolicy.IsDenied(procName))
            return new CaptureGeometry(default, System.Array.Empty<System.Drawing.Rectangle>(), false, true, procName);
        try
        {
            var wp = win.Patterns.Window.PatternOrDefault;
            if (wp is not null && wp.WindowVisualState.ValueOrDefault == FlaUI.Core.Definitions.WindowVisualState.Minimized)
                return new CaptureGeometry(default, System.Array.Empty<System.Drawing.Rectangle>(), true, false, null);
        }
        catch { }
        var target = string.IsNullOrEmpty(@ref) ? (AutomationElement)win : _refs.Resolve(handle.Id, @ref!, PopupFinder.SearchRoots(win, desktop));
        // Password rects across the window AND its grafted popups (context menus / dropdowns) — review §5.
        var pw = new List<System.Drawing.Rectangle>();
        foreach (var rootEl in PopupFinder.SearchRoots(win, desktop))
        {
            try { foreach (var d in rootEl.FindAllDescendants()) { try { if (d.Properties.IsPassword.ValueOrDefault) pw.Add(d.BoundingRectangle); } catch { } } } catch { }
        }
        return new CaptureGeometry(target.BoundingRectangle, pw, false, false, null);
    });

// Full-desktop: the absolute boxes of every denylisted-process window, to black out (review §5).
public async Task<IReadOnlyList<System.Drawing.Rectangle>> DenylistedWindowBoxesAsync()
{
    var windows = await _windows.ListWindowsAsync(includeBounds: true);
    var boxes = new List<System.Drawing.Rectangle>();
    foreach (var w in windows)
        if (PerceptionPolicy.IsDenied(w.ProcessName) && w.Bounds is { } b)
            boxes.Add(new System.Drawing.Rectangle(b.X, b.Y, b.W, b.H));
    return boxes;
}
```
(STATE-VERIFY `win.Patterns.Window.PatternOrDefault.WindowVisualState` accessor in FlaUI 5.)
- [ ] **Step 3: Tool** (`ScreenshotTools.cs`, add `using ModelContextProtocol.Protocol;`, `using FlaUI.Mcp.Core.Errors;`) — STA geometry, then OFF-STA capture:
```csharp
[McpServerTool(ReadOnly = true), Description("Capture a window, an element (window+ref), or the full virtual desktop as a PNG. Returns a native image block + JSON {bounds,dpiScale,scaleApplied,redactions}. Password fields are redacted at capture time (window/element scope covers popups; full-desktop blacks denylisted credential windows). output must be 'inline' (file→NotImplemented). Focus the window first (no occlusion handling). Minimized→ElementNotActionable. Width is clamped to 1920.")]
public Task<CallToolResult> DesktopScreenshot(
    [Description("Window handle, e.g. w1. Omit (and omit ref) for the full virtual desktop.")] string? window = null,
    [Description("Element ref to capture (requires window).")] string? @ref = null,
    [Description("Only 'inline' is implemented (default). 'file' returns NotImplemented.")] string output = "inline",
    [Description("Downscale so width <= maxWidth (default 1600; 0 disables, but a hard 1920 ceiling always applies).")] int maxWidth = 1600)
    => ToolResponse.GuardImage(async () =>
    {
        if (output != "inline")
            throw new ToolException(ToolErrorCode.NotImplemented, "Only output:'inline' is supported in v0.4.0.", "omit output or pass output:'inline'");
        if (!ScreenCapture.IsDesktopRenderable())
            throw new ToolException(ToolErrorCode.CaptureUnavailable, "The desktop session is disconnected or locked.", "reconnect to restore rendering");

        CaptureResult result;
        if (string.IsNullOrEmpty(window))
        {
            var boxes = await _perception.DenylistedWindowBoxesAsync();          // STA (Win32 list)
            var vbounds = ScreenCapture.VirtualScreenBounds();
            result = await Task.Run(() => ScreenCapture.CaptureRectangle(vbounds, boxes, maxWidth)); // OFF-STA
        }
        else
        {
            var geo = await _perception.ResolveWindowCaptureGeometryAsync(new WindowHandle(window!), @ref); // STA
            if (geo.Denied) throw new ToolException(ToolErrorCode.TargetDenied, $"Capturing windows owned by '{geo.DeniedProcess}' is blocked.", "capture a non-sensitive window");
            if (geo.Minimized) throw new ToolException(ToolErrorCode.ElementNotActionable, "Window is minimized; restore it first.", "desktop_window_transform restore, then retry");
            result = await Task.Run(() => ScreenCapture.CaptureRectangle(geo.Bounds, geo.PasswordRects, maxWidth)); // OFF-STA
        }
        var dpi = DpiHelper.ScaleForPoint(result.X, result.Y);
        return ToolResponse.Image(result.Png, new
        { bounds = new { x = result.X, y = result.Y, w = result.W, h = result.H }, dpiScale = dpi, scaleApplied = result.ScaleApplied, redactions = result.Redactions });
    });
```
- [ ] **Step 4: Failing test** — `test/FlaUI.Mcp.Tests/Server/ScreenshotToolTests.cs`:
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
    public async Task Window_screenshot_returns_image_plus_metadata_with_redaction()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var tools = new ScreenshotTools(perception);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);
        var result = await tools.DesktopScreenshot(handle.Id);
        Assert.False(result.IsError);
        Assert.Contains(result.Content, c => c is ImageContentBlock);
        Assert.Contains("\"redactions\":1", result.Content.OfType<TextContentBlock>().First().Text);
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
- [ ] **Step 5: Run — verify it passes (2).**
- [ ] **Step 6: Commit**
```bash
git add src/FlaUI.Mcp.Server/Tools/ToolResponse.cs src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs test/FlaUI.Mcp.Tests/Server/ScreenshotToolTests.cs
git commit -m "feat(perception): desktop_screenshot off-STA + popup/denylist redaction (Task 13)"
```

---

### Task 14: Final audit, full Desktop verification, docs

**Files:** Modify `ROADMAP.md`, `README.md`, `FlaUI.Mcp.Server.csproj`.

- [ ] **Step 1: Audits** — `Grep` each new tool for `[McpServerTool(ReadOnly = true)` (none `Destructive`). `Grep` every `throw new ToolException(` added this phase for a non-null `suggestedRecovery` 3rd arg. `dotnet build -c Debug` → `0 Error(s)`.
- [ ] **Step 2: Non-Desktop gate** — `dotnet test --filter "Category!=Desktop"` → green (65 + `ToolErrorCodeAdditionsTests`).
- [ ] **Step 3: Full Desktop suite (connected session), one class at a time** — `SnapshotModelPinTests`, `FocusedFlagTests`, `RecycleGuardTests`, `SnapshotCacheTests`, `GetBoundsTests`, `SnapshotStatsTests`, `SnapshotDiffTests`, `ListWindowsExtensionTests`, `WaitForTests`, `WaitForStableTests`, `GetFocusedElementTests`, `ScreenCaptureTests`, `ScreenshotToolTests`; regressions `SnapshotEngineTests`, `SnapshotToolsTests`, `OffscreenCullTests`, `PasswordRedactionTests`, `PopupGraftingTests`, `InteractionToolsTests`. (Debug-workflow rule: bounded foreground runs, TRX counters, loop-kill orphans.)
- [ ] **Step 4: `ROADMAP.md`** — mark 3b-1 shipped; 3b-2 (grid/text/clipboard) stays a forward spec; backlog: screenshot occlusion (PrintWindow), full-desktop per-field redaction for non-denied windows, snapshot/diff value-change detection (needs opt-in value reads), `wait_for_stable` scope-by-ref.
- [ ] **Step 5: `README.md`** — add the 7 tools + `list_windows includeBounds`; screenshot/redaction safety note; v0.4.0.
- [ ] **Step 6: Version** — `FlaUI.Mcp.Server.csproj` `<Version>0.4.0</Version>`.
- [ ] **Step 7: Commit**
```bash
git add ROADMAP.md README.md src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj
git commit -m "docs: Phase 3b-1 perception completion — v0.4.0 (Task 14)"
```
- [ ] **Step 8: Finish the branch** — Use superpowers:finishing-a-development-branch.

---

## Self-Review

**Spec coverage:** §3.1 screenshot → Tasks 12/13; §3.2 get_bounds → 5; §3.3 stats → 6; §3.4 list_windows → 8; §3.5 diff → 7; §3.6 wait_for → 9; §3.7 wait_for_stable → 10; §3.8 get_focused_element → 11; §4 focused → 2; §5 recycle guard → 3; §6 screenshot security → 12/13; §7 codes → 1; §8 node model + off-STA capture → 0/4/12/13. All mapped.

**Review fixes folded (13):** single-walk pin (Task 0); ctor/DI front-loaded (Task 4); off-STA by-rectangle capture (12/13); full-desktop denylist blackout (13); popup-inclusive password rects (13); `Value` removed from the walk, diff on Name/Enabled/Focused (0/7); `includeStats` dropped from list_windows (8); diff composite identity (7); chainable get_focused_element (11); `valueEquals` ValuePattern→Name→Legacy (9); `CaptureUnavailable` exception mapping (12); hard `maxWidth` 1920 ceiling (12); `suggestedRecovery` audit (14).

**Deviations / deferrals (explicit):** value-change detection in diff and `wait_for_stable` scope-by-ref are deferred (ROADMAP backlog); full-desktop per-field redaction for non-denied windows is deferred (denylist blackout is the floor); inline-only screenshot held with a 1920 ceiling (user decision). STATE-VERIFY hooks mark every FlaUI/SDK accessor to confirm at implementation time (`FocusedElement`, `NativeWindowHandle`, `Window.Patterns.Window`, `LegacyIAccessible`, `Capture.Rectangle` settings, `ServerOptions.FromArgs`, `System.Drawing.Common`).

**Type/ordering consistency:** all ctor changes land in Task 4; every later task adds methods only. `SnapshotModel`/`SnapshotNode` (no `Value`) flow consistently into stats (6), diff (7), wait (9/10), focus (11). `CaptureResult`/`CaptureGeometry` shared by 12/13. Task 1 precedes throwers; Task 0 precedes model consumers; Task 4 precedes tool tasks; Task 12 precedes 13.
