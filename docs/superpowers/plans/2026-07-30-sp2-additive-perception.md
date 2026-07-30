# SP2 — Additive Perception Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close ROADMAP opportunistic items 3, 5 and 7 without changing any existing caller's outcome.

**Architecture:** Item 3 adds `scopeRef`/`includeOffscreen` to `desktop_wait_for_stable`, rooting the *poll* walk at an existing element via the `SnapshotOptions.RootRef` machinery that already ships, and makes the selector miss path stop asserting non-existence it cannot prove. Item 5 adds one new read-only tool that reuses `TerminalTabReader.EnumerateTabs` with no `Select`. Item 7 retires a ROADMAP item whose premise is false and pins the guarantee it wrongly assumed needed building.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`), FlaUI + UIA3, xUnit, ModelContextProtocol server SDK.

**Spec:** `docs/superpowers/specs/2026-07-30-sp2-additive-perception-design.md` (user-approved, AGY-AFTER panel GREEN at round 6, committed `2bdb609`).

---

## THE BINDING CONSTRAINT — read before every task

**SP2 may not break any existing consumer.** Items 3, 5 and 7 were grouped into one subproject for exactly this reason. Every new parameter is **appended last** with a **default equal to today's behaviour**; every new optional argument defaults so that existing call sites are byte-identical no-ops. A change that needs to break a caller belongs to a later subproject — stop and report instead.

## Ground truth — verified against the repo at `2bdb609`, do not re-derive

| Fact | Citation |
|---|---|
| `SnapshotOptions.RootRef` already exists; `BuildModelAsync` already resolves it | `SnapshotOptions.cs:6`; `PerceptionManager.cs:425-427` |
| A ref-rooted walk's root is exempt from **all four** drop-filters (they gate on `depth > 0`) | `SnapshotEngine.cs:62`, `:65`, `:66-70`; root visited at `:46`, forced included at `:74` |
| `WaitForStableAsync` polls with the cull ON and a **throwaway** registry | `WaitCoordinator.cs:133` (`PollOptions` at `:93`, `new RefRegistry()`) |
| `BuildModelAsync` resolves `RootRef` against the registry **passed in** — a throwaway breaks it | `PerceptionManager.cs:427` |
| The durable registry is `_refs`; `SnapshotAsync` and `SnapshotModelForWaitAsync` pass it | `PerceptionManager.cs:601`, `:609` |
| `PopupFinder.SearchRoots` = `{ win }` **then** popups, and `searchRoots[0]` MUST be the window | `PopupFinder.cs:12-17` |
| `refs.Resolve` defaults to `RefResolveMode.Lenient`, which **rebinds** by descriptor | `RefRegistry.cs:143-156`, `:163-164` |
| `RefResolveMode` is `{ Lenient, Strict }`, same namespace | `RefResolveMode.cs:9-13` |
| Tool descriptions capped at **1500** chars, enforced by reflection over every tool | `ToolTrapFactInvariantTests.cs:23`, `:46-50` |
| `SnapshotEngine.Render` is `public static` — headless reachable | `SnapshotEngine.cs:110` |
| The descriptor stores the **RAW** name, deliberately | `SnapshotEngine.cs:87`; documented at `PerceptionManager.cs:580` |

## File structure

**Modify:**
- `src/FlaUI.Mcp.Core/Perception/SnapshotOptions.cs` — add `RootResolveMode` (default `Lenient`)
- `src/FlaUI.Mcp.Core/Perception/RefRegistry.cs` — `Resolve` gains an optional `mode`, forwarded at `:155`
- `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` — `BuildModelAsync` gains `resolveRefs`; one popup scan; new `ListTerminalTabsAsync`
- `src/FlaUI.Mcp.Core/Perception/WaitCoordinator.cs` — `WaitForStableAsync` gains `scopeRef`/`includeOffscreen`; honest miss path
- `src/FlaUI.Mcp.Core/Perception/TerminalTabReader.cs` — new `TabListing` + `List`
- `src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs` — `DesktopWaitForStable` params + description
- `src/FlaUI.Mcp.Server/Tools/ContentTools.cs` — new `DesktopListTerminalTabs`
- `src/FlaUI.Mcp.Core/Perception/SnapshotEngine.cs` — one comment at `:87` (item 7)
- `docs/agent-contract.md`, `docs/architecture-and-safety.md`, `ROADMAP.md`
- `.claude/skills/driving-flaui-mcp/SKILL.md` **and** `plugins/flaui-mcp/skills/driving-flaui-mcp/SKILL.md` (byte-identical twins)

**Create:**
- `test/FlaUI.Mcp.Tests/Perception/TerminalTabListTests.cs`
- `test/FlaUI.Mcp.Tests/Perception/WaitStableScopeTests.cs`

**Modify (tests):**
- `test/FlaUI.Mcp.Tests/Perception/PasswordRedactionTests.cs` — item 7 tripwires

## Commands

```bash
# Build (run after deleting any test file — --no-build runs deleted tests from the stale DLL)
dotnet build FlaUI.Mcp.sln

# Headless gate. Baseline at ac430b0: 772 passed, 0 skipped.
dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "Category!=Desktop&Category!=KnownDefect"

# One Desktop class in isolation (MAIN THREAD ONLY — the full suite is ~10min vs Bash's 600s cap)
dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "FullyQualifiedName~TerminalTabListTests"
```

---

## Task 1: Item 7 — audit existing redaction coverage, then pin what is genuinely unpinned

**Why first:** it is the only item with no `src/` behaviour change, so it lands and reverts alone.

**Files:**
- Audit only (no edit): `DiffRedactionTests.cs`, `WaitNameOracleTests.cs`, `FindTests.cs`, `WatchPayloadBuilderTests.cs`, `SnapshotModelPinTests.cs`
- Modify: `test/FlaUI.Mcp.Tests/Perception/PasswordRedactionTests.cs`
- Modify: `src/FlaUI.Mcp.Core/Perception/SnapshotEngine.cs:87` (comment only)
- Modify: `ROADMAP.md:327-328`

- [ ] **Step 1: Audit which `[REDACTED]` surfaces are already pinned**

Run:
```bash
grep -rn "REDACTED" test/FlaUI.Mcp.Tests --include=*.cs
```

Write down, for each of these five wire surfaces, whether a test already asserts it:

| Surface | Code |
|---|---|
| snapshot render | `SnapshotEngine.cs:133` |
| `wait_for` match | `WaitCoordinator.cs:88` |
| diff output | `SnapshotDiff.cs:25` |
| watch payload | `WatchPayloadBuilder.cs:34` |
| find hit | `FindQuery.cs:63` |
| ref-failure diagnostic | `RefRegistry.cs:206-209` |

**Do not write a test for a surface that is already pinned.** A duplicate adds maintenance and false confidence. Report which surfaces you found unpinned before proceeding.

- [ ] **Step 2: Write the failing tests for the UNPINNED surfaces**

`PasswordRedactionTests.cs` currently holds one Desktop test (`Password_field_value_is_redacted_in_the_snapshot`). Add these **headless** facts. They construct a node whose `Name` actually carries a secret — a real conformant `PasswordBox` exposes an empty `Name`, so a fixture-driven version would pass even if the redaction did not exist (the same reasoning `WaitNameOracleTests.cs:11-14` records).

Add to the top of the file:
```csharp
using FlaUI.Core.Definitions;
using FlaUI.Mcp.Core.Perception;
```

Add inside the class:
```csharp
    private const string Secret = "hunter2-NEVER-LEAK";

    private static SnapshotNode PasswordNode(string name) => new(
        Ref: "e1", Depth: 1, Indent: "", ControlType: ControlType.Edit,
        AutomationId: "", Name: name, Bounds: new System.Drawing.Rectangle(0, 0, 10, 10),
        Enabled: true, Focusable: true, Focused: false, Selected: false,
        IsPassword: true, IsOffscreen: false,
        RuntimeId: System.Array.Empty<int>(), Patterns: System.Array.Empty<string>(), HelpText: "");

    /// <summary>The rendered tree is the largest wire surface. Asserting only the secret's ABSENCE
    /// would pass against an empty render, so both halves are asserted.</summary>
    [Fact]
    public void A_password_names_secret_never_reaches_the_rendered_snapshot()
    {
        var model = new SnapshotModel(new SnapshotItem[] { PasswordNode(Secret) });

        var rendered = SnapshotEngine.Render(model, new SnapshotOptions { InteractiveOnly = false });

        Assert.DoesNotContain(Secret, rendered);
        Assert.Contains("[REDACTED]", rendered);
    }

    /// <summary>The descriptor deliberately stores the RAW name because it is the resolver's only
    /// lookup key when AutomationId is absent (PerceptionManager.cs:580, RefRegistry.cs:181-182).
    /// This pins the OTHER half of that bargain: the raw name must never be echoed in the failure
    /// diagnostic. If someone ever adds Name to RefRegistry.Key, this fails.</summary>
    [Fact]
    public void A_password_names_secret_never_reaches_a_ref_resolution_failure_message()
    {
        var refs = new RefRegistry();
        var descriptor = new ElementDescriptor(
            System.Array.Empty<int>(), ControlType.Edit, "", Secret, null,
            System.Array.Empty<int>(), false);

        var ex = Assert.Throws<ToolException>(() =>
            refs.ResolveDescriptor(descriptor, System.Array.Empty<AutomationElement>(), "e1"));

        Assert.DoesNotContain(Secret, ex.Message);
        Assert.DoesNotContain(Secret, ex.SuggestedRecovery ?? "");
    }
```

Add whatever `using` lines the compiler demands for `ToolException`, `ElementDescriptor` and `AutomationElement` (`FlaUI.Mcp.Core.Errors`, `FlaUI.Core.AutomationElements`).

**If Step 1 found a surface already pinned, drop the corresponding test here and say so.** If `ResolveDescriptor` with an empty roots array does not throw `ToolException`, STOP and report `STATE_MISMATCH: ResolveDescriptor empty-roots behaviour` rather than adapting the assertion.

- [ ] **Step 3: Run to verify they PASS immediately, then mutation-verify**

Run:
```bash
dotnet build FlaUI.Mcp.sln && dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "FullyQualifiedName~PasswordRedactionTests&Category!=Desktop"
```
Expected: PASS. **These are tripwires on an already-true guarantee, so passing immediately is correct** — which means they prove nothing until mutated.

**Mutation-verify each one.** Temporarily change `SnapshotEngine.cs:133` from
`string shownName = n.IsPassword ? "[REDACTED]" : n.Name;` to `string shownName = n.Name;`, re-run, and confirm the render test FAILS. Then temporarily add `d.Name` into `RefRegistry.Key` (`:208-209`), re-run, confirm the diagnostic test FAILS. **Restore both.** A tripwire you did not watch fail is not a tripwire.

- [ ] **Step 4: Record why the ROADMAP item was wrong**

`SnapshotEngine.cs:87` — append to the `descriptor` line's context:
```csharp
                // The RAW name is deliberate and LOAD-BEARING: RefRegistry falls back to
                // Name+ControlType when AutomationId is absent (RefRegistry.cs:181-182) and the cached
                // fast path compares it (:334), so redacting it here would make an IsPassword element
                // with no AutomationId permanently REF_STALE_UNRESOLVABLE. Redaction happens at every
                // WIRE surface instead (render :133, match WaitCoordinator.cs:88, diff SnapshotDiff.cs:25,
                // find FindQuery.cs:63, watch WatchPayloadBuilder.cs:34) and Key() never echoes it (:206-209).
```

`ROADMAP.md:327-328` — replace the "Micro belt-and-suspenders" entry with an entry recording that the item is **retired**, that redacting the descriptor `Name` would break resolution for exactly the controls it targeted, that `PerceptionManager.cs:580` already documented this, and that the never-echoed guarantee is now pinned by `PasswordRedactionTests`. Keep it to the repo's terse style.

- [ ] **Step 5: Commit**

```bash
git add test/FlaUI.Mcp.Tests/Perception/PasswordRedactionTests.cs src/FlaUI.Mcp.Core/Perception/SnapshotEngine.cs ROADMAP.md
git commit -m "test(redaction): retire item 7's redaction, pin the guarantee it assumed"
```

---

## Task 2: Item 5 — `TerminalTabReader.List`, the pure read

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/TerminalTabReader.cs`
- Create: `test/FlaUI.Mcp.Tests/Perception/TerminalTabListTests.cs`

- [ ] **Step 1: Write the failing test**

Create `test/FlaUI.Mcp.Tests/Perception/TerminalTabListTests.cs`:
```csharp
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>desktop_list_terminal_tabs exists so an agent can learn a tabIndex WITHOUT selecting a tab.
/// The index space must match desktop_read_terminal_tab's, which it does by construction: both go through
/// TerminalTabReader.EnumerateTabs. A snapshot-derived ordinal could NOT be used, because five filters sit
/// between the raw child array and an emitted node and four of them can drop a TabItem
/// (SnapshotEngine.cs:62, :65, :66-70, :95-98).
///
/// HEADLESS here: this pins the projection shape only. The live WT read is Desktop-gated in Step 5.</summary>
public class TerminalTabListTests
{
    [Fact]
    public void The_listing_record_carries_index_title_and_reported_selection()
    {
        var t = new TerminalTabReader.TabListing(2, "pwsh", true);

        Assert.Equal(2, t.Index);
        Assert.Equal("pwsh", t.Title);
        Assert.True(t.Active);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run:
```bash
dotnet build FlaUI.Mcp.sln
```
Expected: FAIL — `'TerminalTabReader' does not contain a definition for 'TabListing'`.

- [ ] **Step 3: Add `TabListing` and `List`**

In `TerminalTabReader.cs`, after the existing `Result` record (`:14-16`):
```csharp
    /// <summary>One tab as reported by a PURE READ. Active means "this tab reported itself selected" —
    /// not ground truth: IsSelected swallows every COM fault to false (:62-66), so a tab whose
    /// SelectionItem pattern is unreadable reports Active:false exactly like an unselected one.</summary>
    public readonly record struct TabListing(int Index, string Title, bool Active);
```

Then, immediately before `Run` (`:98`):
```csharp
    /// <summary>Item 5: enumerate the tab strip WITHOUT selecting anything — no Select, no settle, no
    /// restore, no visible flicker. Shares EnumerateTabs with Run, which is the whole point: the index
    /// space is identical BY CONSTRUCTION rather than by agreement.
    ///
    /// NEVER call Select from here. That is load-bearing, not incidental — a settle loop or a
    /// "helpful" activation would silently turn a read-only tool into a destructive one, and the
    /// no-Select property cannot be proven at runtime (UIA event callbacks arrive on COM RPC threads,
    /// so a test asserting "no event fired" can pass before the event lands).
    ///
    /// ActiveTabIndex is -1 when NO tab reported itself selected, which conflates "nothing selected"
    /// with "selection state unreadable". Deliberate: see TabListing.</summary>
    public static (IReadOnlyList<TabListing> Tabs, int ActiveTabIndex) List(AutomationElement win)
    {
        var tabs = EnumerateTabs(win);
        var listing = new List<TabListing>(tabs.Count);
        int active = -1;
        for (int i = 0; i < tabs.Count; i++)
        {
            bool selected = IsSelected(tabs[i]);
            if (selected && active < 0) active = i;
            listing.Add(new TabListing(i, NameOf(tabs[i]), selected));
        }
        return (listing, active);
    }
```

- [ ] **Step 4: Run to verify it passes**

Run:
```bash
dotnet build FlaUI.Mcp.sln && dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "FullyQualifiedName~TerminalTabListTests&Category!=Desktop"
```
Expected: PASS.

- [ ] **Step 5: Add the Desktop test for the live read**

Append to `TerminalTabListTests.cs` a `[Trait("Category","Desktop")]` class that opens a real Windows Terminal window with **at least two tabs**, selects tab 1 (not 0) by hand or via `desktop_read_terminal_tab`, then calls `List` and asserts:
- every tab has a distinct `Index`, ascending from 0;
- `ActiveTabIndex` equals the tab that was active **before** the call;
- the count matches the tab strip.

Follow the fixture pattern already used by the existing Desktop terminal tests in `test/FlaUI.Mcp.Tests` (read one first and match it — do not invent a new fixture shape).

**Label the active-index assertion honestly in a comment:** it is necessary-but-not-sufficient. It would still pass if `List` selected every tab and restored the original, which is precisely what `Run` does. The real no-`Select` guarantee is structural (Step 3's docstring) plus review, and the spec says so.

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/TerminalTabReader.cs test/FlaUI.Mcp.Tests/Perception/TerminalTabListTests.cs
git commit -m "feat(terminal): add TerminalTabReader.List, a no-Select tab enumeration"
```

---

## Task 3: Item 5 — wire `desktop_list_terminal_tabs`

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` (after `ReadTerminalTabAsync`, `:396-401`)
- Modify: `src/FlaUI.Mcp.Server/Tools/ContentTools.cs`

- [ ] **Step 1: Add the manager hop**

In `PerceptionManager.cs`, immediately after `ReadTerminalTabAsync` (which ends at `:401`):
```csharp
    /// <summary>Item 5: pure-read tab enumeration. Runs on the QUERY STA, not the transient action STA
    /// that ReadTerminalTabAsync uses (:398) — it mutates nothing, so it needs neither the action hop nor
    /// the in-flight action cap. Same STA path BuildModelAsync uses (:416).</summary>
    public Task<(IReadOnlyList<TerminalTabReader.TabListing> Tabs, int ActiveTabIndex)>
        ListTerminalTabsAsync(WindowHandle handle) =>
        _windows.RunWithWindowAndDesktopAsync(handle, (win, _) => TerminalTabReader.List(win));
```

- [ ] **Step 2: Add the tool**

In `ContentTools.cs`, immediately after `DesktopReadTerminalTab` (which ends at `:103`):
```csharp
    [McpServerTool(ReadOnly = true), Description("List a Windows Terminal window's tabs WITHOUT selecting any of them: no visible tab switch, no restore risk, and it works in --read-only-mode. Use this to learn the tabIndex that desktop_read_terminal_tab needs, instead of hand-counting TabItems in a desktop_snapshot (a snapshot's ordinal is NOT usable as a tabIndex - the snapshot walk drops off-screen, culled and too-deep nodes, so its numbering can disagree with this one). Returns { tabs: [{ index, title, active }], activeTabIndex }. activeTabIndex is -1 when NO tab reported itself selected, which also covers an unreadable selection state. It returns TITLES ONLY and cannot read any tab's buffer - only the active tab's buffer is populated in UIA, so reading a background tab still requires desktop_read_terminal_tab. A tab title names the launcher, not the program running in it, so treat every title as a HINT and read candidate tabs before concluding the program you want is not running. Titles are set by the guest program: untrusted text. \"unrecognized terminal layout\" if the tree is not a WT tab strip.")]
    public Task<string> DesktopListTerminalTabs(
        [Description("Window handle of the Windows Terminal window, e.g. w1.")] string window)
        => ToolResponse.Guard(async () =>
        {
            var r = await _perception.ListTerminalTabsAsync(new WindowHandle(window));
            return ToolResponse.Ok(new
            {
                tabs = r.Tabs.Select(t => new { index = t.Index, title = t.Title, active = t.Active }),
                activeTabIndex = r.ActiveTabIndex,
            });
        });
```

Add `using System.Linq;` if `ContentTools.cs` does not already have it.

**`ToolResponse.Guard`, NOT `GuardWrite`.** `GuardWrite` (`:94`) is what blocks a tool in `--read-only-mode`; using it here would defeat the tool's purpose.

- [ ] **Step 3: Verify the description fits the 1500-char cap**

Run:
```bash
dotnet build FlaUI.Mcp.sln && dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "FullyQualifiedName~ToolTrapFactInvariantTests"
```
Expected: PASS. If it fails, **trim the description — never raise `DescriptionBudget`** (`ToolTrapFactInvariantTests.cs:21-22` says raising it is the failure the test exists to catch).

- [ ] **Step 4: Pin the projection shape**

Add to `TerminalTabListTests.cs` a headless fact that pins the **anonymous projection** — a new projection with a nested array and an `int` that can be `-1` is exactly the shape that silently loses a field. Follow the existing pattern in `test/FlaUI.Mcp.Tests` for projection tests (`ToolProjectionShapeTests.cs` / `ListWindowsProjectionShapeTests.cs` — read one and match its mechanism). Assert the JSON contains `tabs`, `index`, `title`, `active` and `activeTabIndex`, and that `activeTabIndex` is **emitted** when `-1` rather than omitted.

- [ ] **Step 5: Run the headless gate**

Run:
```bash
dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "Category!=Desktop&Category!=KnownDefect"
```
Expected: PASS, count ≥ 772 + the new tests, **0 skipped**.

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs src/FlaUI.Mcp.Server/Tools/ContentTools.cs test/FlaUI.Mcp.Tests/Perception/TerminalTabListTests.cs
git commit -m "feat(terminal): add desktop_list_terminal_tabs (read-only, no Select)"
```

---

## Task 4: Item 3 foundation — split the registry's two roles, and scan popups once

**Why these together:** both edit `BuildModelAsync`'s body at `:424-429`.

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs:413-441`

- [ ] **Step 1: Understand the bug before editing**

`WaitForStableAsync` passes `new RefRegistry()` (`WaitCoordinator.cs:133`) — deliberately, so per-poll walks do not grow the durable registry (`:18-19`). But `BuildModelAsync` resolves `RootRef` against **that same** registry (`:427`). Passing a caller's ref through unchanged would fail `REF_NOT_FOUND` on **every** call. Separately, `PopupFinder.FindOwnerPopups` runs at `:424` and **again** inside `:427`'s `SearchRoots` (`PopupFinder.cs:17`) — two desktop scans per poll.

- [ ] **Step 2: Rewrite the body**

Replace `BuildModelAsync`'s signature and the `:424-429` region:
```csharp
    /// <summary><paramref name="resolveRefs"/> is the registry the ROOT REF resolves against; refs minted
    /// by the walk always register into <paramref name="refs"/>. They are the same registry for every
    /// existing caller (default null => refs), and DIFFERENT only for the wait paths, which resolve a
    /// caller's durable ref while registering walked nodes into a throwaway so per-poll walks never grow
    /// the durable registry (WaitCoordinator.cs:18-19).</summary>
    public Task<(string SnapshotId, SnapshotModel Model)> BuildModelAsync(
        WindowHandle handle, SnapshotOptions options, RefRegistry refs, RefRegistry? resolveRefs = null)
    {
        return _windows.RunWithWindowAndDesktopAsync(handle, (win, desktop) =>
        {
            _windows.PruneClosedWindows(); // Phase 6 backstop: reclaim windows closed w/o a process exit
            var procName = SafeProcessName(win);
            if (PerceptionPolicy.IsDenied(procName))
                throw new ToolException(ToolErrorCode.TargetDenied,
                    $"Snapshotting windows owned by '{procName}' is blocked (credential store).",
                    "snapshot a different, non-sensitive window");
            // ONE popup scan per build. It used to run twice on the RootRef path -- once here and once
            // inside SearchRoots (PopupFinder.cs:17) -- and each scan is desktop.FindAllChildren() plus
            // ~6 cross-process reads per desktop child (PopupFinder.cs:35-47). The two consumers below
            // take DIFFERENT lists and must not be conflated: the grafting loop takes popups ALONE, while
            // ref resolution needs the WINDOW FIRST (PopupFinder.cs:12-13 -- searchRoots[0] MUST be the
            // window root, IndexPath is window-relative). Passing popups alone to Resolve makes every
            // window-rooted ref unresolvable; passing {win}+popups to the grafting loop makes the engine
            // visit the window a second time as its own popup root (SnapshotEngine.cs:47-54).
            IReadOnlyList<AutomationElement> popups = PopupFinder.FindOwnerPopups(desktop, win);
            bool isFullWindow = string.IsNullOrEmpty(options.RootRef);
            AutomationElement root;
            if (isFullWindow)
            {
                root = win;
            }
            else
            {
                var searchRoots = new List<AutomationElement> { win };
                searchRoots.AddRange(popups);
                root = (resolveRefs ?? refs).Resolve(
                    handle.Id, options.RootRef!, searchRoots, options.RootResolveMode);
            }
            var snapshotId = refs.BeginSnapshot(handle.Id);
            var model = SnapshotEngine.Build(root, popups, options, refs, handle.Id);
```

Leave the rest of the method (`isFullWindow` wakeable block, `return`) unchanged.

**SHAPE-DIVERGENCE STOP:** if making this compile would change any parameter's type, the argument order of `Resolve`, or the ORDER of `searchRoots`, stop and report `[original] -> [yours] because <reason>`. `searchRoots[0]` being the window is a documented invariant, not a style choice.

- [ ] **Step 3: Add `RootResolveMode` to `SnapshotOptions`**

In `SnapshotOptions.cs`, after `RootRef` (`:6`):
```csharp
    /// <summary>How RootRef resolves. Lenient (default = today) re-walks the descriptor and may rebind to
    /// a re-created element; Strict matches ONLY the exact element by live RuntimeId and never rebinds
    /// (RefRegistry.cs:160-162). The wait paths use Strict, because a caller who scopes by ref is asking
    /// about THAT instance -- silently following a replacement would report stability about an object the
    /// caller never named. Everything else keeps Lenient.</summary>
    public RefResolveMode RootResolveMode { get; init; } = RefResolveMode.Lenient;
```

- [ ] **Step 4: Add the optional mode to `RefRegistry.Resolve`**

In `RefRegistry.cs`, change `Resolve`'s signature (`:143`) and its fall-through (`:155`):
```csharp
    public AutomationElement Resolve(string windowId, string @ref, IReadOnlyList<AutomationElement> searchRoots,
        RefResolveMode mode = RefResolveMode.Lenient)
    {
        var entry = Lookup(windowId, @ref); // REF_NOT_FOUND if absent
        var d = entry.Descriptor;

        // (1) cached fast-path — query-STA only. RuntimeId AND ControlType match AND not offscreen AND Name matches.
        // VALID UNDER Strict TOO: it verifies the LIVE RuntimeId, which is exactly what Strict demands, so
        // it is deliberately left enabled rather than bypassed (that would cost a full re-walk per poll).
        if (entry.Cached is { } cached)
        {
            try { if (FastPathMatches(cached, d)) return cached; }
            catch { /* element gone — fall through to the cache-free walk */ }
        }

        return ResolveDescriptor(d, searchRoots, @ref, mode);
    }
```

**The `mode` on that last line is the whole fix.** Omit it and `Strict` silently degrades to a lenient rebind on exactly the path where identity matters, while looking correct.

- [ ] **Step 5: Verify every existing caller is untouched**

Run:
```bash
grep -rn "\.Resolve(" src/ | grep -v ResolveDescriptor
dotnet build FlaUI.Mcp.sln && dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "Category!=Desktop&Category!=KnownDefect"
```
Expected: build clean; headless **772 passed, 0 skipped** — the same count as the baseline, because nothing has changed behaviour yet. **A changed count here means you changed behaviour; stop and find out why.**

- [ ] **Step 6: Pin the registry split explicitly — the spec REQUIRES this**

The spec calls this "the highest-risk edit, because a default-argument mistake silently changes every existing call site" and requires an explicit pin. Running the existing suite is **not** sufficient: the suite covers this invariant only *incidentally*, so a regression would surface as some unrelated Desktop test failing for a reason nobody could attribute. Name the invariant in a test.

Add to `test/FlaUI.Mcp.Tests/Perception/WaitStableScopeTests.cs` a `[Trait("Category","Desktop")]` class against the TestApp fixture pinning **both halves** of the split:

1. **The durable half.** Take a `desktop_snapshot`, then resolve one of its refs (e.g. via `PerceptionManager.RunOnRefAsync`). It must succeed — proving `SnapshotAsync` still registers into the durable `_refs`. If `resolveRefs` were mis-defaulted, refs minted by a snapshot would stop resolving.
2. **The throwaway half — the reason the throwaway exists.** Run a `WaitForStableAsync` that completes at least two polls, then assert a ref that a *poll walk* would have minted is **NOT** resolvable in the durable registry. The per-poll walk must not grow `_refs`.

Match the fixture mechanism used by the existing SP1 wait Desktop tests (read `WaitGoneCullTests.cs` first). If the second half cannot be expressed because the throwaway's refs are unobservable from outside, assert the observable proxy instead — that the durable registry's resolvable-ref set is unchanged across the wait — and **say in a comment which form you used and why**.

Run:
```bash
dotnet build FlaUI.Mcp.sln && dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "FullyQualifiedName~WaitStableScopeTests"
```
Expected: PASS.

**Mutation-verify:** temporarily change `BuildModelAsync`'s `resolveRefs` default from `null` to a `new RefRegistry()`, confirm the durable-half test FAILS, and restore. A pin on a defaulted argument that you never watched fail is not a pin.

- [ ] **Step 7: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs src/FlaUI.Mcp.Core/Perception/SnapshotOptions.cs src/FlaUI.Mcp.Core/Perception/RefRegistry.cs
git commit -m "refactor(perception): split the registry's resolve/register roles, scan popups once"
```

---

## Task 5: Item 3 — `scopeRef` + `includeOffscreen` on `wait_for_stable`

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/WaitCoordinator.cs:123-148`
- Modify: `src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs:78-91`
- Create: `test/FlaUI.Mcp.Tests/Perception/WaitStableScopeTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `test/FlaUI.Mcp.Tests/Perception/WaitStableScopeTests.cs`. Pin the **precedence rules**, which are pure argument validation and therefore headless:
```csharp
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>Item 3's argument contract. scopeRef roots the POLL walk; by+value keeps working unchanged.
/// Supplying both is a caller bug and must be REFUSED, never silently resolved in favour of one --
/// silently picking a scope the caller did not ask for is the wrong-belief class SP1 spent eleven rounds
/// removing. HEADLESS: this is validation, not UIA.</summary>
public class WaitStableScopeTests
{
    [Fact]
    public async Task Supplying_both_scopeRef_and_a_selector_is_refused()
    {
        var coordinator = new WaitCoordinator(null!);

        var ex = await Assert.ThrowsAsync<ToolException>(() => coordinator.WaitForStableAsync(
            new FlaUI.Mcp.Core.Windows.WindowHandle("w1"), by: "automationId", value: "X",
            includeText: false, quietMs: 100, timeoutMs: 100, pollIntervalMs: 10,
            scopeRef: "e5", includeOffscreen: false));

        Assert.Equal(ToolErrorCode.InvalidArguments, ex.Code);
        Assert.Contains("scopeRef", ex.Message);
    }
}
```
**The validation must run BEFORE any UIA work** so `null!` never dereferences. If it does not, that is itself the bug: argument validation belongs at the top of the method.

- [ ] **Step 2: Run to verify it fails**

Run:
```bash
dotnet build FlaUI.Mcp.sln
```
Expected: FAIL — no such parameters `scopeRef`/`includeOffscreen`.

- [ ] **Step 3: Add the parameters and the scoped poll**

In `WaitCoordinator.cs`, replace `WaitForStableAsync`'s signature and the top of its body:
```csharp
    public async Task<WaitStableResult> WaitForStableAsync(WindowHandle handle, string? by, string? value,
        bool includeText, int quietMs, int timeoutMs, int pollIntervalMs,
        string? scopeRef = null, bool includeOffscreen = false)
    {
        bool scopeRequested = !string.IsNullOrEmpty(by) && !string.IsNullOrEmpty(value);
        bool refScoped = !string.IsNullOrEmpty(scopeRef);
        if (refScoped && scopeRequested)
            throw new ToolException(ToolErrorCode.InvalidArguments,
                "Pass either scopeRef or by+value to scope stability, not both.",
                "drop one — scopeRef roots the walk at that element; by+value re-finds a match each poll");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int needed = (int)System.Math.Ceiling((double)quietMs / System.Math.Max(1, pollIntervalMs));
        string? last = null; int stableCount = 0;

        // scopeRef roots the POLL walk only (Strict, so the wait cannot silently rebind to a re-created
        // element). The final snapshot below stays WHOLE-WINDOW: a caller who waited on a subtree almost
        // always wants the whole window next, and scoping it would force an immediate second full walk.
        var pollOptions = PollOptions with
        {
            IncludeOffscreen = includeOffscreen,
            RootRef = scopeRef,
            RootResolveMode = RefResolveMode.Strict,
        };
        while (true)
        {
            CountWalk();
            var (_, model) = await _perception.BuildModelAsync(
                handle, pollOptions, new RefRegistry(), resolveRefs: _perception.Refs);
            var sub = refScoped ? (IReadOnlyList<SnapshotNode>)model.Nodes.ToList() : Subtree(model, by, value);
```

Leave the rest of the loop (signature comparison, `stableCount`, the final `SnapshotModelForWaitAsync` at `:141-143`, the timeout return, `Task.Delay(SafeDelayMs(...))`) **unchanged**.

**`_perception.Refs` does not exist yet** — `PerceptionManager._refs` is private. Add a minimal internal accessor to `PerceptionManager`:
```csharp
    /// <summary>The DURABLE registry. Exposed so a wait can resolve a caller's ref against it while
    /// registering its own per-poll walk into a throwaway (BuildModelAsync's resolveRefs).</summary>
    internal RefRegistry Refs => _refs;
```
If `WaitCoordinator` is in a different assembly from `PerceptionManager`, make it `public` with the same docstring rather than adding an `InternalsVisibleTo` — report which you chose.

- [ ] **Step 4: Run to verify the test passes**

Run:
```bash
dotnet build FlaUI.Mcp.sln && dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "FullyQualifiedName~WaitStableScopeTests"
```
Expected: PASS.

- [ ] **Step 5: Thread the parameters through the tool**

In `SnapshotTools.cs`, append to `DesktopWaitForStable`'s parameter list (after `pollIntervalMs`, `:86`):
```csharp
        [Description("Optional ref to scope stability to (from a prior snapshot of this window). Cheaper than by+value: the poll walk is rooted here, not at the window. Mutually exclusive with by/value. Scopes the WAIT only - the returned snapshotId is still the whole window.")] string? scopeRef = null,
        [Description("Include off-screen elements and elements laid out past the window edge (default false, matching desktop_snapshot).")] bool includeOffscreen = false)
```
and pass them at the call (`:89`):
```csharp
            var r = await _wait.WaitForStableAsync(new WindowHandle(window), by, value, includeText, quietMs, timeoutMs, pollIntervalMs, scopeRef, includeOffscreen);
```

Then extend the tool's `Description` (`:78`) to state: `scopeRef` roots the poll walk and is mutually exclusive with `by`+`value`; it scopes the wait, **not** the returned snapshot; `includeOffscreen` exists and defaults to today's behaviour; and that `stable:true` means the subtree *was* quiet for the required consecutive polls — the returned snapshot is taken afterwards, so it is not a frozen view. Current length is ~265 chars against a 1500 cap, so there is ample room; **do not cut a trap to save space.**

- [ ] **Step 6: Run the gates**

Run:
```bash
dotnet build FlaUI.Mcp.sln && dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "Category!=Desktop&Category!=KnownDefect"
```
Expected: PASS, 0 skipped, including `ToolTrapFactInvariantTests`.

- [ ] **Step 7: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/WaitCoordinator.cs src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs test/FlaUI.Mcp.Tests/Perception/WaitStableScopeTests.cs
git commit -m "feat(wait): scope wait_for_stable by ref, resolved Strict"
```

---

## Task 6: Item 3 — make the selector miss path tell the truth

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/WaitCoordinator.cs`
- Modify: `test/FlaUI.Mcp.Tests/Perception/WaitStableScopeTests.cs`

- [ ] **Step 1: Understand both branches before editing**

Today, a selector that matches nothing in the **culled** model throws *"No element matched {by}={value} to scope stability"* (`:136`). That is a false statement when the element exists but was culled. It is **also** false after an unculled confirmation walk fails, because `UnculledPollOptions` keeps the `IsOffscreen` filter (`:104-105`) and `MaxDepth` still applies (`SnapshotEngine.cs:95-98`). So **both** messages must state what was *searched*, not what does not exist.

- [ ] **Step 2: Write the failing tests**

Add two facts asserting the two branches produce **different** messages, and that neither claims non-existence. Use the same `WaitCoordinator` walk counters the SP1 tests use (`:36-41`) to assert the success path pays **no** extra walk. Model the tests on the existing SP1 wait tests (`WaitGoneCullTests.cs`, `WaitDiagnosticTests.cs`) — read one first and match its fixture mechanism rather than inventing one.

Assert specifically:
- culled-but-present → message names the cull and the recovery names **both** `includeOffscreen` and `scopeRef`;
- genuinely absent → message does **not** assert the element does not exist, and the recovery names `includeOffscreen` alongside correcting the selector;
- both still carry `ToolErrorCode.SelectorNoMatch`;
- the success path's walk count is unchanged.

- [ ] **Step 3: Implement**

Replace the throw at `:135-136`:
```csharp
            if (scopeRequested && sub.Count == 0)
                throw await ScopeNotFound(handle, by!, value!, includeOffscreen);
```

**The helper is `async` deliberately — do NOT write the synchronous form.** `WaitForStableAsync` is already
`async` and `BuildModelAsync` dispatches to the STA, so `.GetAwaiter().GetResult()` here would be
sync-over-async: at best it blocks a pool thread, at worst it deadlocks against the dispatcher. `throw await`
is valid C# and reads fine.

Add this helper to the class:
```csharp
    /// <summary>The selector matched nothing in the polled model. Upgrade the message with ONE unculled
    /// walk before giving up, so an existing-but-culled scope is not reported as a missing one.
    ///
    /// The confirmation NEVER decides the outcome, only the wording -- if it throws, we still produce the
    /// unconfirmed answer. But ToolException PROPAGATES: a window that closed mid-wait surfaces
    /// WindowHandleStale / WindowNotFound (WindowManager.cs:181-199) and a denied target surfaces
    /// TargetDenied (PerceptionManager.cs:420-423). Those are TRUER than "your selector matched nothing",
    /// so swallowing them to report a selector problem would send the caller to edit a selector while
    /// their window is gone. Only raw COM/UIA faults are swallowed.
    ///
    /// No latch is needed (unlike the `gone` confirmation): this path throws and terminates the call, so
    /// there is no subsequent poll for the confirmation to double.</summary>
    private async Task<ToolException> ScopeNotFound(
        WindowHandle handle, string by, string value, bool includeOffscreen)
    {
        bool existsUnculled = false;
        if (!includeOffscreen)
        {
            try
            {
                CountConfirmationWalk();
                var (_, unculled) = await _perception
                    .BuildModelAsync(handle, UnculledPollOptions, new RefRegistry());
                existsUnculled = unculled.Nodes.Any(n => Matches(n, by, value));
            }
            catch (ToolException) { throw; }
            catch { /* raw COM/UIA fault: fall back to the unconfirmed message */ }
        }

        if (existsUnculled)
            return new ToolException(ToolErrorCode.SelectorNoMatch,
                $"{by}={value} matched an element that exists but was culled against the window bounds, so stability could not be scoped to it.",
                "pass includeOffscreen:true, or scope by scopeRef instead");

        return new ToolException(ToolErrorCode.SelectorNoMatch,
            $"No element matching {by}={value} was found in the searched tree to scope stability.",
            "correct the selector, or pass includeOffscreen:true to search off-screen and past-the-edge elements");
    }
```

**Do not name `MaxDepth` in the recovery.** `desktop_wait_for_stable` exposes no `maxDepth` parameter (`SnapshotTools.cs:79-91`), so naming it hands the caller a cause they cannot act on. Do not add such a parameter either — that is scope creep past SP2.

If the compiler objects to `throw await` at that site, hoist it (`var ex = await ScopeNotFound(...); throw ex;`) rather than reverting to a synchronous helper.

- [ ] **Step 4: Run the tests**

Run:
```bash
dotnet build FlaUI.Mcp.sln && dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "Category!=Desktop&Category!=KnownDefect"
```
Expected: PASS, 0 skipped.

- [ ] **Step 5: Mutation-verify the ToolException carve-out**

Temporarily change `catch (ToolException) { throw; }` to `catch (ToolException) { }` and confirm a test fails. If none does, **the carve-out is untested** — add a fact that drives the confirmation walk into a `ToolException` and asserts that code reaches the caller. Restore.

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/WaitCoordinator.cs test/FlaUI.Mcp.Tests/Perception/WaitStableScopeTests.cs
git commit -m "fix(wait): stop wait_for_stable blaming the selector for a culled scope"
```

---

## Task 7: Item 3 — MEASURE the ref-rooted per-poll cost

**No speedup may be claimed until this task records real numbers.** Inference lost to measurement repeatedly in this release.

**Files:**
- Modify: `ROADMAP.md` (record the measurement)

- [ ] **Step 1: Measure on a real window**

On a physical console, open a window with a reasonably large tree (the WPF TestApp fixture or VS Code). Using the running MCP server, measure and record:
1. `desktop_wait_for_stable` **unscoped** — `elapsedMs` and the node count from `desktop_snapshot_stats`.
2. `desktop_wait_for_stable` with `scopeRef` pointing at a **small** subtree — `elapsedMs` and that subtree's node count.
3. Both with the same `quietMs`/`pollIntervalMs`/`timeoutMs`.

- [ ] **Step 2: Record what you actually measured**

Append to `ROADMAP.md`'s opportunistic-hardening section: the two `elapsedMs` figures, the two node counts, and **the fixed per-poll floor** that survives rooting — one `PopupFinder.FindOwnerPopups` scan per build (`desktop.FindAllChildren()` plus ~6 cross-process reads per desktop child, `PopupFinder.cs:35-47`; the walk attribution puts the desktop/popup scan at ~593 ms of a 6098 ms walk, `ROADMAP.md:337-344`).

State plainly whether `wait_for_stable`'s default 5000 ms budget is now reachable for a small subtree. **If it is not, say so** — the honest negative is the useful result, and it tells the next person the remaining lever is the `CacheRequest` work, not more scoping.

- [ ] **Step 3: Commit**

```bash
git add ROADMAP.md
git commit -m "docs(roadmap): record the measured ref-rooted per-poll cost"
```

---

## Task 8: Documentation surfaces

A new public tool and two changed signatures are not done when the code compiles.

**Files:**
- Modify: `docs/agent-contract.md:73` (area)
- Modify: `docs/architecture-and-safety.md:36` (area)
- Modify: `.claude/skills/driving-flaui-mcp/SKILL.md` **and** `plugins/flaui-mcp/skills/driving-flaui-mcp/SKILL.md`
- Modify: `ROADMAP.md`

- [ ] **Step 1: `docs/agent-contract.md`**

That file has a table enumerating every tool with its annotation and params (the `desktop_read_terminal_tab` row is at `:73`). Add a `desktop_list_terminal_tabs` row marked **ReadOnly**, and extend the `desktop_wait_for_stable` row with `scopeRef` and `includeOffscreen`. Match the surrounding rows' style exactly.

- [ ] **Step 2: `docs/architecture-and-safety.md`**

Line `:36` groups tools that "change state and need no lease" — it lists `desktop_read_terminal_tab`. The new tool is a **read**, so it must **not** join that group. Place it with the perception tools and make the distinction explicit, since the two terminal tools now sit on opposite sides of that line.

- [ ] **Step 3: The driving skill — BOTH copies**

`.claude/skills/driving-flaui-mcp/SKILL.md:241` teaches enumerating tabs via `desktop_snapshot`. Update the recipe to reach for `desktop_list_terminal_tabs` first, keeping the "**tab title is a HINT, never a filter**" rule intact — that rule exists because of a real logged driver error (`ROADMAP.md:116-125`).

⚠ **Two traps, both already paid for in this project:**
1. The two copies are pinned **byte-identical** by `SkillLoadLineTests.The_two_tracked_copies_are_byte_identical`. Editing one alone reds the headless gate. Make the identical edit in both.
2. The `AUTOTRAIN:GROWTH` region is at **25 of a hard 30 lines**. Prefer the hand-authored floor; if it must go in GROWTH and would breach 30, compress or supersede an existing rule.

Run after editing:
```bash
dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "FullyQualifiedName~SkillLoadLineTests"
```
Expected: PASS.

- [ ] **Step 4: `ROADMAP.md`**

Mark **item 5 DONE**, naming the new tool and recording that both original options were rejected because four of five filters can drop a `TabItem` from the snapshot walk. Confirm **item 7**'s entry was already corrected in Task 1. Record **item 3**'s outcome.

**Do NOT add a `CHANGELOG.md` entry.** `scripts/release.ps1` owns the heading and drafts the body from commits at release time; a hand-written entry collides with it. Verified during SP1.

- [ ] **Step 5: Commit**

```bash
git add docs/agent-contract.md docs/architecture-and-safety.md .claude/skills/driving-flaui-mcp/SKILL.md plugins/flaui-mcp/skills/driving-flaui-mcp/SKILL.md ROADMAP.md
git commit -m "docs: document desktop_list_terminal_tabs and wait_for_stable's new params"
```

---

## Task 9: Scope audit + full gates

**Why this task exists:** SP1 shipped **half its scope** because its plan never mentioned item 2, and nobody checked the plan against the spec until afterwards.

- [ ] **Step 1: Audit the spec's scope against what actually landed**

Open the spec and walk its three item sections. For **each** of items 3, 5 and 7, name the commit that implemented it and the test that pins it. Then check these specific spec requirements, each of which is easy to skip:

| Requirement | Where |
|---|---|
| Two DISTINCT popup lists (model `popups`; resolver `{win} + popups`) | Task 4 |
| `resolveRefs` defaults so `:601`, `:609`, `:734` are no-ops | Task 4 |
| `RootResolveMode` defaults `Lenient`; `Strict` only for the wait | Tasks 4, 5 |
| The `mode` argument forwarded at `RefRegistry.cs:155` | Task 4 |
| Cached fast path left ENABLED under `Strict` | Task 4 |
| `scopeRef` mutually exclusive with `by`/`value` | Task 5 |
| Returned snapshot stays WHOLE-WINDOW | Task 5 |
| `ToolException` propagates from the confirmation walk | Task 6 |
| `MaxDepth` NOT named in any recovery | Task 6 |
| No speedup claimed without measurement | Task 7 |
| Item 7 audited existing coverage before writing tests | Task 1 |
| Both skill copies edited identically | Task 8 |

Report any gap **before** running the gates. A gap found here is cheap; found after merge it is not.

- [ ] **Step 2: Headless gate**

```bash
dotnet build FlaUI.Mcp.sln
dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "Category!=Desktop&Category!=KnownDefect"
```
Expected: **≥ 772 passed, 0 skipped.** A skip is a failure here.

- [ ] **Step 3: Desktop gate — MAIN THREAD ONLY**

Every Desktop class this branch touched must go green **in isolation on a physical console**. Run each with `--filter "FullyQualifiedName~<ClassName>"`.

⚠ **Never run the Desktop suite from a Bash/PowerShell tool call** — it takes ~10 minutes against a 600s cap (the A1a T8 trap). It also needs an input lease for input-driven tests; the lease is the **user's** to grant, and `--accept-risk` is **never** added on their behalf.

- [ ] **Step 4: Report**

State: the commit range, the headless count, which Desktop classes ran and their results, the measured numbers from Task 7, and anything left undone. If a Desktop class could not run, say which and why — do not report a partial gate as green.

---

## Self-review (performed while writing this plan)

**1. Spec coverage.** Every spec section maps to a task: item 3 → Tasks 4–7; item 5 → Tasks 2–3; item 7 → Task 1; documentation surfaces → Task 8; testing strategy → distributed, audited in Task 9. The spec's "verified NOT to exist" note (no tool-count assertion, no read-only-mode allowlist) is deliberately **not** a task — there is nothing to update.

**2. Placeholder scan.** No TBD/TODO. Every code step carries real code. Three steps intentionally say "read an existing test first and match its mechanism" (the Desktop terminal fixture, the projection-test mechanism, the SP1 wait-test fixture) rather than pasting a fixture this plan did not verify line-by-line — that is a deliberate anti-fabrication choice, not a placeholder, and each names the exact file to copy from.

**3. Type consistency.** `TabListing(int Index, string Title, bool Active)` is used identically in Tasks 2 and 3. `List` returns `(IReadOnlyList<TabListing> Tabs, int ActiveTabIndex)` in both. `BuildModelAsync`'s new `resolveRefs` is used in Tasks 4 and 5. `RootResolveMode` is defined in Task 4 and consumed in Task 5. `ScopeNotFound` is defined and used only in Task 6.

**4. Known residual.** Task 5 needs `PerceptionManager.Refs`, which does not exist yet; the step adds it and names the assembly-visibility decision the implementer must report. Surfaced rather than assumed.

**5. Folded from the plan's own panel review (round 1, then GREEN at round 2).**
- Task 6's `ScopeNotFound` is now specified `async` **outright**. The first draft offered a synchronous form with an async fallback; that is sync-over-async against an STA dispatcher, so the plan no longer presents it as an option at all.
- Task 4 gained **Step 6, the explicit registry-split pin**. The spec requires it ("the highest-risk edit… pin that `SnapshotAsync` and the diff path still register into the registry they always did") and the first draft leaned on the existing suite instead — the same silent-omission failure that cost SP1 half its scope, caught here for the price of one review round. It pins both halves (durable refs still resolve; the per-poll walk does not grow the durable registry) and requires mutation-verification of the defaulted argument.
- Verified during that round: the pasted `BuildModelAsync` and `WaitForStableAsync` blocks compile against the real files, `ElementDescriptor`'s positional order is correct, `ResolveDescriptor` with empty roots does throw `ToolException`, and the new tool description measures **1086** chars against the 1500 cap.
