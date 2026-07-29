# SP0 + SP1 — tripwires, then the `wait_for` contract — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `desktop_wait_for` stop disagreeing with `desktop_find` about whether an element exists — without raising per-poll cost and without changing `desktop_snapshot`.

**Architecture:** Split the one `IncludeOffscreen` flag into two independent filters so the spatial cull can be disabled without disabling the `IsOffscreen` filter. Polling keeps today's culled walk; an unculled walk runs only at a decision point — once when `gone` would satisfy, once at an `exists`/`enabled` timeout. A per-call latch stops the `gone` confirmation degenerating into a double walk every poll.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`), FlaUI + UIA3, xUnit 2.9.3, `System.Text.Json`. Core internals are visible to tests via `[assembly: InternalsVisibleTo("FlaUI.Mcp.Tests")]` (`src/FlaUI.Mcp.Core/Properties/AssemblyInfo.cs:2`).

**Spec:** `docs/superpowers/specs/2026-07-29-sp0-sp1-wait-for-contract-design.md` (approved; panel GREEN at round 4, plus one post-GREEN correction folded — see Task 6's note on emit-null).

---

## Ground rules for the implementer

**Every line citation below was grep-verified against the repo on 2026-07-29.** If a cited line does not say what this plan claims, **STOP and report `STATE_MISMATCH: <what>`** rather than adapting. The code moves; the plan does not self-heal.

**Shape-divergence stop.** If making something compile would change the shape, type, or wire encoding of any value shown here — even trivially — STOP and report `[original] -> [yours] because <reason>`. "It compiles" is not justification. `elementBounds` as an object instead of an `int[]` is exactly the kind of silent adaptation that passes compilation and breaks a contract.

**The oracle.** For SP1's behaviour, the pinning test is `WaitForCullDefectTests` (`test/FlaUI.Mcp.Tests/Perception/WaitForCullDefectTests.cs`), which currently ends in `Assert.Fail` by design. If a value looks wrong, the oracle wins — surface the conflict, do not edit the test to match the code.

### Commands

```bash
# Headless (what CI runs) — from repo root
dotnet test -c Release --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"

# Desktop suite — physical console + an input lease required; NEVER in CI
dotnet test --filter "Category=Desktop&Category!=KnownDefect&FullyQualifiedName!~PopupGrafting"

# The fail-by-design defect test, in isolation
dotnet test --filter "FullyQualifiedName~WaitForCullDefectTests"
```

Desktop tests need a real interactive session. Do not attempt them over RDP: `SendInput` returns success while the keystroke silently fails to land, so a green run there proves nothing.

---

## File structure

| File | Change | Responsibility after |
|---|---|---|
| `src/FlaUI.Mcp.Core/Perception/SnapshotOptions.cs` | Modify (add 1 property after `:15`) | Carries the two now-independent filter flags |
| `src/FlaUI.Mcp.Core/Perception/SnapshotEngine.cs` | Modify `:66` | Spatial cull becomes independently gated |
| `src/FlaUI.Mcp.Core/Perception/WaitCoordinator.cs` | Modify `:7`, `:26`, `:69-101`; add walk counter | The whole mechanism: confirmation walk, latch, diagnostic |
| `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` | Modify `:467-493` (`FindAsync`), `:557-571` (`EvaluateSelectorValueAsync`) | Both stop rooting at bare `win` |
| `src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs` | Modify `:58-71` | `wait_for` gains a param and 3 wire fields |
| `test/FlaUI.Mcp.Tests/Perception/WaitContractShapeTests.cs` | **Create** | SP0 layer 1 — headless record pins |
| `test/FlaUI.Mcp.Tests/Perception/ToolProjectionShapeTests.cs` | **Create** | SP0 layer 2 — Desktop projection pins |
| `test/FlaUI.Mcp.Tests/Perception/CullFlagSplitTests.cs` | **Create** | The flag split, incl. snapshot-unchanged |
| `test/FlaUI.Mcp.Tests/Perception/WaitGoneCullTests.cs` | **Create** | `gone` correctness + the latch |
| `test/FlaUI.Mcp.Tests/Perception/WaitDiagnosticTests.cs` | **Create** | Timeout diagnostic + the false-blame cases |
| `test/FlaUI.Mcp.Tests/Perception/PopupRootCoverageTests.cs` | **Create** | Popup fix: dedup + per-root isolation |
| `test/FlaUI.Mcp.Tests/Perception/WaitForCullDefectTests.cs` | Modify, then retire trait | Becomes a passing regression test |

---

# SP0 — tripwires

SP0 ships and reverts alone, and **must land before any SP1 task**. It changes no behaviour.

## Task 1: Headless record-shape pins

**Files:**
- Test: `test/FlaUI.Mcp.Tests/Perception/WaitContractShapeTests.cs` (create)

Layer 1 of two. This catches a property deleted from a record. It cannot see the tool projection — Task 2 covers that.

- [ ] **Step 1: Write the failing test**

```csharp
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>SP0 layer 1: pins the RECORD shapes SP1 is about to extend. Headless, CI-gated.
/// The tool-layer projection is pinned separately in ToolProjectionShapeTests (Desktop) —
/// a record pin cannot see the anonymous projection, which is where fields actually get dropped.</summary>
public class WaitContractShapeTests
{
    [Fact]
    public void WaitForResult_carries_its_four_established_members()
    {
        var r = new WaitForResult(Satisfied: true, Ref: "e7", ElapsedMs: 42, SnapshotId: "w1:3");

        Assert.True(r.Satisfied);
        Assert.Equal("e7", r.Ref);
        Assert.Equal(42, r.ElapsedMs);
        Assert.Equal("w1:3", r.SnapshotId);
    }

    [Fact]
    public void WaitForResult_allows_null_ref_and_snapshotId_on_an_unsatisfied_result()
    {
        // A timeout returns nulls, NOT omitted members. SP1's new fields follow this same
        // convention, so it is pinned here rather than assumed.
        var r = new WaitForResult(Satisfied: false, Ref: null, ElapsedMs: 5000, SnapshotId: null);

        Assert.False(r.Satisfied);
        Assert.Null(r.Ref);
        Assert.Null(r.SnapshotId);
    }

    [Fact]
    public void TextReadResult_carries_truncatedFrom()
    {
        var t = new TextReadResult("abc", Truncated: true, IsPassword: false, TruncatedFrom: "head");

        Assert.Equal("abc", t.Text);
        Assert.True(t.Truncated);
        Assert.False(t.IsPassword);
        Assert.Equal("head", t.TruncatedFrom);
    }
}
```

- [ ] **Step 2: Run it and watch it PASS**

Run: `dotnet test --filter "FullyQualifiedName~WaitContractShapeTests"`
Expected: `Passed! - Failed: 0, Passed: 3`

This test passes on first write — it pins what already exists. That is correct for a tripwire and is **not** a TDD violation: there is no new behaviour to drive out. Its value is that it goes RED in Task 6 if someone changes the record incompatibly.

- [ ] **Step 3: Commit**

```bash
git add test/FlaUI.Mcp.Tests/Perception/WaitContractShapeTests.cs
git commit -m "test(sp0): pin WaitForResult and TextReadResult record shapes"
```

## Task 2: Desktop tool-projection pins

**Files:**
- Test: `test/FlaUI.Mcp.Tests/Perception/ToolProjectionShapeTests.cs` (create)

Layer 2. This is the tripwire that matters — it asserts on the JSON the tool actually returns, so it catches a field dropped from the anonymous projection at `SnapshotTools.cs:70`.

- [ ] **Step 1: Read the fixture pattern first**

Open `test/FlaUI.Mcp.Tests/Perception/PopupGraftingTests.cs` and copy its fixture wiring (`IClassFixture<TestAppFixture>`, `AutomationDispatcher`, `WindowManager`, `OpenByPidAsync`).

`SnapshotTools`' constructor is `SnapshotTools(PerceptionManager perception, WaitCoordinator wait)` (`src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs:14`, verified). The test project must reference the Server assembly — check `test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj` for a `ProjectReference` to `FlaUI.Mcp.Server`. If it is absent, add it; the Server already grants `InternalsVisibleTo("FlaUI.Mcp.Tests")` (`src/FlaUI.Mcp.Server/Properties/AssemblyInfo.cs:3`), so the reference is the only missing piece.

- [ ] **Step 2: Write the failing test**

```csharp
using System.Text.Json;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>SP0 layer 2: pins the WIRE shape of desktop_wait_for. The tool projects through an
/// ANONYMOUS object (SnapshotTools.cs:70), so a field added to WaitForResult alone never reaches
/// the wire — and ToolResponse.cs:12 sets no DefaultIgnoreCondition, so nulls are EMITTED, not
/// omitted. Both facts are load-bearing for SP1 and are pinned here.</summary>
[Trait("Category", "Desktop")]
public class ToolProjectionShapeTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public ToolProjectionShapeTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Wait_for_timeout_emits_all_four_keys_including_explicit_nulls()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var wait = new WaitCoordinator(perception);
        var tools = new SnapshotTools(perception, wait);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        // A selector that cannot match, with a short budget: a deterministic timeout.
        string json = await tools.DesktopWaitFor(
            window: handle.Id, by: "automationId", value: "NoSuchElement_SP0",
            until: "exists", equals: null, timeoutMs: 600, pollIntervalMs: 200);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("satisfied").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("ref").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("snapshotId").ValueKind);
        Assert.True(root.GetProperty("elapsedMs").GetInt32() >= 0);
    }
}
```

- [ ] **Step 3: Run it**

Run: `dotnet test --filter "FullyQualifiedName~ToolProjectionShapeTests"`
Expected: `Passed! - Failed: 0, Passed: 1`

If `ref`/`snapshotId` come back **absent** rather than `Null`, the serializer configuration differs from `ToolResponse.cs:12` as read. **STOP and report `STATE_MISMATCH`** — SP1's emit-null decision depends on this.

- [ ] **Step 4: Commit**

```bash
git add test/FlaUI.Mcp.Tests/Perception/ToolProjectionShapeTests.cs
git commit -m "test(sp0): pin desktop_wait_for wire shape incl. explicit nulls"
```

---

# SP1 — the `wait_for` contract

Do not begin until SP0 is committed.

## Task 3: Split the cull flag

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/SnapshotOptions.cs:15` (append after)
- Modify: `src/FlaUI.Mcp.Core/Perception/SnapshotEngine.cs:66`
- Test: `test/FlaUI.Mcp.Tests/Perception/CullFlagSplitTests.cs` (create)

The whole mechanism rests on being able to drop the spatial cull while keeping the `IsOffscreen` filter. `desktop_snapshot` must come out bit-identical.

- [ ] **Step 1: Write the failing test**

```csharp
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>The two filters behind SnapshotOptions are independent. SpatialOffscreenButton sits at
/// Canvas.Left=5000 and reports IsOffscreen=FALSE, so ONLY the spatial cull removes it; OffscreenButton
/// reports IsOffscreen=TRUE. Turning off the spatial cull must surface the first and NOT the second.</summary>
[Trait("Category", "Desktop")]
public class CullFlagSplitTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public CullFlagSplitTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Disabling_only_the_spatial_cull_surfaces_the_spatial_element_but_not_the_offscreen_one()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var snap = await perception.SnapshotAsync(handle,
            new SnapshotOptions { FullProperties = true, CullToWindowBounds = false });

        Assert.Contains("aid=SpatialOffscreenButton", snap.Tree);   // spatial cull was the only thing hiding it
        Assert.DoesNotContain("aid=OffscreenButton", snap.Tree);    // IsOffscreen filter still applies
    }

    [Fact]
    public async Task Default_options_are_unchanged_by_the_split()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var snap = await perception.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true });

        Assert.DoesNotContain("aid=SpatialOffscreenButton", snap.Tree);
        Assert.DoesNotContain("aid=OffscreenButton", snap.Tree);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~CullFlagSplitTests"`
Expected: FAIL — compile error, `'SnapshotOptions' does not contain a definition for 'CullToWindowBounds'`.

- [ ] **Step 3: Add the property**

In `src/FlaUI.Mcp.Core/Perception/SnapshotOptions.cs`, immediately after the `IncludeOffscreen` property (`:15`), inside the record:

```csharp
    /// <summary>Cull elements whose bounding rectangle does not intersect the WALK ROOT's rectangle
    /// (the window for a full-window walk; each popup's own rect for a grafted popup subtree).
    /// Default true = today's behaviour. Independent of IncludeOffscreen, which gates the separate
    /// UIA IsOffscreen property filter. Set false to keep the IsOffscreen filter while dropping the
    /// spatial cull — the wait paths do exactly that. Only consulted when IncludeOffscreen is false,
    /// because IncludeOffscreen=true already disables BOTH filters.</summary>
    public bool CullToWindowBounds { get; init; } = true;
```

- [ ] **Step 4: Gate the cull on it**

In `src/FlaUI.Mcp.Core/Perception/SnapshotEngine.cs`, line 66 currently reads:

```csharp
            if (depth > 0 && !options.IncludeOffscreen && cullBounds.Width > 0 && cullBounds.Height > 0)
```

Replace with:

```csharp
            if (depth > 0 && !options.IncludeOffscreen && options.CullToWindowBounds && cullBounds.Width > 0 && cullBounds.Height > 0)
```

Do **not** touch line 65 (the `IsOffscreen` filter). Do **not** touch lines 50-54 (each popup is visited with its own bounds as `cullBounds` — that is what keeps popups exempt from the window rect, and it must keep working).

- [ ] **Step 5: Run the new tests**

Run: `dotnet test --filter "FullyQualifiedName~CullFlagSplitTests"`
Expected: `Passed! - Failed: 0, Passed: 2`

- [ ] **Step 6: Prove `desktop_snapshot` is untouched**

Run: `dotnet test --filter "FullyQualifiedName~OffscreenCullTests"`
Expected: `Passed! - Failed: 0, Passed: 3`

`OffscreenCullTests.cs` must pass **without being edited**. If it needed an edit, the split changed snapshot behaviour and is wrong — STOP and report.

- [ ] **Step 7: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/SnapshotOptions.cs src/FlaUI.Mcp.Core/Perception/SnapshotEngine.cs test/FlaUI.Mcp.Tests/Perception/CullFlagSplitTests.cs
git commit -m "feat(perception): make the spatial cull independently gateable

CullToWindowBounds defaults true, so desktop_snapshot is unchanged.
Only consulted when IncludeOffscreen is false."
```

## Task 4: Walk counter on `WaitCoordinator`

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/WaitCoordinator.cs`
- Test: extends `test/FlaUI.Mcp.Tests/Perception/WaitGoneCullTests.cs` in Task 5

The latch's whole point is *not doing a second walk*. That is unobservable without a counter, and a wall-clock proxy is forbidden — a timing-shaped assertion is exactly what masked a real teardown defect for two rounds during A1a.

- [ ] **Step 1: Add the counter**

In `src/FlaUI.Mcp.Core/Perception/WaitCoordinator.cs`, inside the class after the constructor (`:16`):

```csharp
    // Test observability for the latch (Task 5): the latch's contract is "one walk per poll after the
    // first satisfy-attempt, never two", which is only assertable by counting. Incremented on EVERY
    // BuildModelAsync issued by this coordinator. internal + InternalsVisibleTo("FlaUI.Mcp.Tests")
    // (src/FlaUI.Mcp.Core/Properties/AssemblyInfo.cs:2). NOT thread-safe by design: one wait call owns
    // one coordinator in the tests that read it, and production never reads it.
    internal int WalkCount { get; private set; }
    private void CountWalk() => WalkCount++;
```

- [ ] **Step 2: Call it at every walk site**

In `WaitForAsync` (`:69-101`) and `WaitForStableAsync` (`:44-67`), add `CountWalk();` immediately before **each** `await _perception.BuildModelAsync(...)` and each `await _perception.SnapshotModelForWaitAsync(...)`. As of today those sites are `:53`, `:61`, `:85` and `:94`. Every walk added by Tasks 5 and 6 must also be counted.

- [ ] **Step 3: Build**

Run: `dotnet build -c Release`
Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/WaitCoordinator.cs
git commit -m "test(perception): count walks in WaitCoordinator for latch observability"
```

## Task 5: `gone` confirmation walk + the latch

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/WaitCoordinator.cs:26`, `:69-101`
- Test: `test/FlaUI.Mcp.Tests/Perception/WaitGoneCullTests.cs` (create)

The correctness fix. Today `"gone" => match is null` (`:89`) reports *gone* for a merely-culled element, handing control back on a wrong belief.

- [ ] **Step 1: Write the failing tests**

```csharp
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>SpatialOffscreenButton exists in the UIA tree with IsOffscreen=false and a rect outside
/// the window. It is NOT gone. Before SP1, until="gone" satisfied on it immediately.</summary>
[Trait("Category", "Desktop")]
public class WaitGoneCullTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public WaitGoneCullTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task Gone_does_not_satisfy_on_a_spatially_culled_element_that_still_exists()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var wait = new WaitCoordinator(perception);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var r = await wait.WaitForAsync(handle, "automationId", "SpatialOffscreenButton",
            until: "gone", equals: null, timeoutMs: 1500, pollIntervalMs: 300);

        Assert.False(r.Satisfied);
    }

    [Fact]
    public async Task Gone_still_satisfies_for_an_element_that_genuinely_is_not_there()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var wait = new WaitCoordinator(perception);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var r = await wait.WaitForAsync(handle, "automationId", "NoSuchElement_SP1",
            until: "gone", equals: null, timeoutMs: 2000, pollIntervalMs: 300);

        Assert.True(r.Satisfied);
    }

    [Fact]
    public async Task The_latch_stops_the_confirmation_walk_from_doubling_every_poll()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var wait = new WaitCoordinator(perception);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        // ~5 polls against a permanently culled element. Without the latch every poll would walk
        // TWICE (culled says absent -> confirmation finds it), so the count would be about 2x polls.
        var r = await wait.WaitForAsync(handle, "automationId", "SpatialOffscreenButton",
            until: "gone", equals: null, timeoutMs: 1500, pollIntervalMs: 300);

        Assert.False(r.Satisfied);
        // One walk per poll, plus the single confirmation that flipped the latch. Generous upper
        // bound: assert we are nowhere near the 2x-per-poll shape, without pinning an exact count
        // that a timing wobble would flake.
        Assert.InRange(wait.WalkCount, 2, 9);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~WaitGoneCullTests"`
Expected: FAIL — `Gone_does_not_satisfy...` fails with `Assert.False() Failure: Expected: False, Actual: True` (today's false positive).

- [ ] **Step 3: Add the unculled poll options**

In `WaitCoordinator.cs`, immediately after `PollOptions` (`:26`):

```csharp
    /// <summary>PollOptions with the spatial cull disabled and the IsOffscreen filter KEPT. Used only
    /// at a decision point — the `gone` confirmation and the exists/enabled timeout diagnostic — never
    /// for steady-state polling, so per-poll cost is unchanged.</summary>
    internal static SnapshotOptions UnculledPollOptions =>
        new() { InteractiveOnly = false, IncludeOffscreen = false, CullToWindowBounds = false };
```

- [ ] **Step 4: Implement the confirmation walk and the latch**

In `WaitForAsync`, replace the whole `else` branch and the satisfy block (`:83-97`) with the following. `until` values are enumerated **complete** — `exists`, `gone`, `enabled`, and the default arm — do not elide any.

```csharp
            else
            {
                var pollOptions = latched ? UnculledPollOptions : PollOptions;
                CountWalk();
                var (_, model) = await _perception.BuildModelAsync(handle, pollOptions, new RefRegistry());
                var match = model.Nodes.FirstOrDefault(n => Matches(n, by, value));
                satisfied = until switch
                {
                    "exists" => match is not null,
                    "gone" => match is null,
                    "enabled" => match is { Enabled: true },
                    _ => match is not null
                };

                // `gone` is the one predicate where the spatial cull causes a false POSITIVE: a culled
                // element is absent from the model, so `match is null` reports it destroyed when it is
                // merely clipped. Before satisfying, confirm against a walk without the spatial cull.
                // Skipped when the poll walk is ALREADY unculled (latched, or the caller opted in) —
                // the confirmation would be byte-identical to the poll we just did.
                if (satisfied && until == "gone" && !latched)
                {
                    CountWalk();
                    bool stillThere;
                    try
                    {
                        var (_, unculled) = await _perception.BuildModelAsync(handle, UnculledPollOptions, new RefRegistry());
                        stillThere = unculled.Nodes.Any(n => Matches(n, by, value));
                    }
                    catch
                    {
                        // Best-effort: a window closed or denied mid-wait must not convert a completed
                        // wait into a throw. Could not confirm -> keep the culled walk's answer.
                        stillThere = false;
                    }

                    if (stillThere)
                    {
                        satisfied = false;
                        // LATCH: this element is present-but-culled, so every later poll would repeat
                        // this same double walk. Switch to the unculled walk for the rest of the call —
                        // one walk per poll, not two, and only after the expensive case is proven.
                        latched = true;
                    }
                }
            }
            if (satisfied)
            {
                CountWalk();
                var (snapId, real) = await _perception.SnapshotModelForWaitAsync(handle, PollOptions);
                var realMatch = real.Nodes.FirstOrDefault(n => Matches(n, by, value));
                return new WaitForResult(true, realMatch?.Ref, (int)sw.ElapsedMilliseconds, snapId);
            }
```

Declare the latch alongside the stopwatch at the top of `WaitForAsync`, immediately after the `sw` declaration (`:74`):

```csharp
            bool latched = false; // per-call; never outlives this wait
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test --filter "FullyQualifiedName~WaitGoneCullTests"`
Expected: `Passed! - Failed: 0, Passed: 3`

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/WaitCoordinator.cs test/FlaUI.Mcp.Tests/Perception/WaitGoneCullTests.cs
git commit -m "fix(wait): stop 'gone' satisfying on a spatially culled element

A culled element was absent from the poll model, so `match is null`
reported it destroyed when it was only clipped -- a false POSITIVE that
handed control back on a wrong belief.

Confirm with one unculled walk before satisfying, and latch to the
unculled walk afterwards so the confirmation cannot double every poll."
```

## Task 6: Timeout diagnostic for `exists` / `enabled`

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/WaitCoordinator.cs:7`, `:98`
- Modify: `src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs:70`
- Test: `test/FlaUI.Mcp.Tests/Perception/WaitDiagnosticTests.cs` (create)

Semantics do not change. Only the failure message does.

**Emit null, do not omit.** `SnapshotTools.cs:70` projects through an **anonymous** object, so `[JsonIgnore]` on `WaitForResult` would never be consulted — the record is not what gets serialized. `ToolResponse.cs:12` sets no `DefaultIgnoreCondition`, so this tool already emits `"ref":null,"snapshotId":null` on every timeout. The new fields match that.

- [ ] **Step 1: Write the failing tests**

```csharp
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>The diagnostic must MEASURE the geometry, never infer it from presence across two walks.
/// Blaming the wrong cause confidently is worse than the bare timeout it replaces.</summary>
[Trait("Category", "Desktop")]
public class WaitDiagnosticTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public WaitDiagnosticTests(TestAppFixture app) => _app = app;

    private static async Task<(WaitCoordinator Wait, WindowHandle Handle)> ArrangeAsync(
        AutomationDispatcher dispatcher, WindowManager mgr, int pid)
    {
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        return (new WaitCoordinator(perception), await mgr.OpenByPidAsync(pid));
    }

    [Fact]
    public async Task Exists_timing_out_on_a_culled_element_reports_the_reason_and_both_rects()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var (wait, handle) = await ArrangeAsync(dispatcher, mgr, _app.Process.Id);

        var r = await wait.WaitForAsync(handle, "automationId", "SpatialOffscreenButton",
            until: "exists", equals: null, timeoutMs: 900, pollIntervalMs: 300);

        Assert.False(r.Satisfied);
        Assert.Equal("outsideWindowBounds", r.UnsatisfiedBecause);
        Assert.NotNull(r.ElementBounds);
        Assert.NotNull(r.WindowBounds);
        Assert.Equal(4, r.ElementBounds!.Length);   // {X, Y, Width, Height}
        Assert.Equal(4, r.WindowBounds!.Length);
    }

    [Fact]
    public async Task A_selector_that_matches_nothing_gets_no_geometry_blame()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var (wait, handle) = await ArrangeAsync(dispatcher, mgr, _app.Process.Id);

        var r = await wait.WaitForAsync(handle, "automationId", "NoSuchElement_SP1",
            until: "exists", equals: null, timeoutMs: 900, pollIntervalMs: 300);

        Assert.False(r.Satisfied);
        Assert.Null(r.UnsatisfiedBecause);
        Assert.Null(r.ElementBounds);
        Assert.Null(r.WindowBounds);
    }

    [Fact]
    public async Task Enabled_timing_out_on_a_disabled_in_window_element_is_not_blamed_on_geometry()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var (wait, handle) = await ArrangeAsync(dispatcher, mgr, _app.Process.Id);

        // DisabledButton is inside the window and IsEnabled=false, so it is present in BOTH walks.
        // A presence-keyed rule would blame geometry here; the intersection test must not.
        var r = await wait.WaitForAsync(handle, "automationId", "DisabledButton",
            until: "enabled", equals: null, timeoutMs: 900, pollIntervalMs: 300);

        Assert.False(r.Satisfied);
        Assert.Null(r.UnsatisfiedBecause);
    }
}
```

**Fixture prerequisite.** The third test needs an `AutomationId="DisabledButton"` control with `IsEnabled="False"` inside the window. **Before writing the test, grep the fixture:**

```bash
grep -n "DisabledButton" test/FlaUI.Mcp.TestApp/MainWindow.xaml
```

If absent, add it to `MainWindow.xaml` inside the existing `RootPanel` `StackPanel`:

```xml
<Button x:Name="DisabledButton"
        AutomationProperties.AutomationId="DisabledButton"
        Content="Disabled" IsEnabled="False" Width="120" Margin="4"/>
```

Then re-run `FixtureIntegrityTests` (Step 5) — the fixture has a hard 2-D budget and an element pushed past the window edge is exactly the defect this whole spec exists to fix.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~WaitDiagnosticTests"`
Expected: FAIL — compile error, `'WaitForResult' does not contain a definition for 'UnsatisfiedBecause'`.

- [ ] **Step 3: Extend the record**

`WaitCoordinator.cs:7` currently reads:

```csharp
public sealed record WaitForResult(bool Satisfied, string? Ref, int ElapsedMs, string? SnapshotId);
```

Replace with:

```csharp
/// <summary>UnsatisfiedBecause is a CLOSED token set, not free text — SP1 defines exactly one token,
/// "outsideWindowBounds". The three diagnostic members are optional positional parameters so every
/// existing `new WaitForResult(a, b, c, d)` call site keeps compiling unchanged. Bounds are
/// int[] {X, Y, Width, Height} — the same shape FindMatch.Bounds uses (FindQuery.cs:28, built at
/// PerceptionManager.cs:534). NOT the {x,y,w,h} object form ScreenshotTools uses for capture geometry.</summary>
public sealed record WaitForResult(bool Satisfied, string? Ref, int ElapsedMs, string? SnapshotId,
    string? UnsatisfiedBecause = null, int[]? ElementBounds = null, int[]? WindowBounds = null);
```

- [ ] **Step 4: Implement the diagnostic**

In `WaitForAsync`, replace the timeout return (`:98`, `if (sw.ElapsedMilliseconds >= timeoutMs) return new WaitForResult(false, null, (int)sw.ElapsedMilliseconds, null);`) with:

```csharp
            if (sw.ElapsedMilliseconds >= timeoutMs)
            {
                // Diagnose ONCE, on the failure path only. Skipped when the poll walk was already
                // unculled: the diagnostic walk would be identical to the poll that just failed.
                if (until != "valueEquals" && !latched && !alreadyUnculled)
                {
                    try
                    {
                        CountWalk();
                        var (_, diag) = await _perception.BuildModelAsync(handle, UnculledPollOptions, new RefRegistry());
                        var hit = diag.Nodes.FirstOrDefault(n => Matches(n, by, value));
                        var root = diag.Nodes.FirstOrDefault();   // depth-0 node IS the window: PollOptions sets no RootRef
                        if (hit is not null && root is not null)
                        {
                            // MEASURE the geometry. Do NOT infer "it was culled" from the element being
                            // absent in the culled walk and present here: an element that rendered in the
                            // gap between the last poll and this walk satisfies that inference while
                            // sitting on screen, and a merely-disabled in-window element is present in
                            // both walks. The intersection test is the actual cull condition.
                            if (!root.Bounds.IntersectsWith(hit.Bounds))
                                return new WaitForResult(false, null, (int)sw.ElapsedMilliseconds, null,
                                    "outsideWindowBounds",
                                    new[] { hit.Bounds.X, hit.Bounds.Y, hit.Bounds.Width, hit.Bounds.Height },
                                    new[] { root.Bounds.X, root.Bounds.Y, root.Bounds.Width, root.Bounds.Height });
                        }
                    }
                    catch { /* best-effort: a failed diagnostic degrades to the bare result, never throws */ }
                }
                return new WaitForResult(false, null, (int)sw.ElapsedMilliseconds, null);
            }
```

`alreadyUnculled` is the caller's opt-in from Task 7. **Until Task 7 lands, declare it beside `latched` as `bool alreadyUnculled = false;`** so this task compiles standalone; Task 7 replaces the initialiser.

- [ ] **Step 5: Run the tests**

Run: `dotnet test --filter "FullyQualifiedName~WaitDiagnosticTests"`
Expected: `Passed! - Failed: 0, Passed: 3`

Run: `dotnet test --filter "FullyQualifiedName~FixtureIntegrityTests"`
Expected: `Passed! - Failed: 0, Passed: 1` — proves the new fixture control did not overflow the window.

- [ ] **Step 6: Add the fields to the wire**

`SnapshotTools.cs:70` currently reads:

```csharp
            return ToolResponse.Ok(new { satisfied = r.Satisfied, @ref = r.Ref, elapsedMs = r.ElapsedMs, snapshotId = r.SnapshotId });
```

Replace with:

```csharp
            return ToolResponse.Ok(new
            {
                satisfied = r.Satisfied, @ref = r.Ref, elapsedMs = r.ElapsedMs, snapshotId = r.SnapshotId,
                unsatisfiedBecause = r.UnsatisfiedBecause, elementBounds = r.ElementBounds, windowBounds = r.WindowBounds
            });
```

Update the tool `[Description]` at `:58`, appending before the closing quote:

```
 On an exists/enabled timeout where the element resolves but lies outside the window rect, returns unsatisfiedBecause="outsideWindowBounds" plus elementBounds and windowBounds ({x,y,w,h} arrays) so a geometry failure is not mistaken for a timing one.
```

- [ ] **Step 7: Extend the SP0 projection pin**

Add to `ToolProjectionShapeTests.cs` from Task 2:

```csharp
    [Fact]
    public async Task Wait_for_timeout_emits_the_three_diagnostic_keys_as_explicit_nulls_when_undetermined()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var wait = new WaitCoordinator(perception);
        var tools = new SnapshotTools(perception, wait);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        string json = await tools.DesktopWaitFor(
            window: handle.Id, by: "automationId", value: "NoSuchElement_SP0",
            until: "exists", equals: null, timeoutMs: 600, pollIntervalMs: 200);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("unsatisfiedBecause").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("elementBounds").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("windowBounds").ValueKind);
    }
```

Run: `dotnet test --filter "FullyQualifiedName~ToolProjectionShapeTests"`
Expected: `Passed! - Failed: 0, Passed: 2`

- [ ] **Step 8: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/WaitCoordinator.cs src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs test/FlaUI.Mcp.Tests/Perception/WaitDiagnosticTests.cs test/FlaUI.Mcp.Tests/Perception/ToolProjectionShapeTests.cs test/FlaUI.Mcp.TestApp/MainWindow.xaml
git commit -m "feat(wait): explain an exists/enabled timeout caused by geometry

A wait on an element laid out past the window edge timed out with a bare
satisfied:false, so the natural next move was to raise the timeout, which
can never work. Return the reason and both rects instead.

The reason is emitted only on a direct !IntersectsWith measurement, never
inferred from presence across two walks -- that inference cannot tell
culling from lateness, and blames geometry for a merely-disabled control."
```

## Task 7: The additive `includeOffscreen` parameter

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/WaitCoordinator.cs` (`WaitForAsync` signature)
- Modify: `src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs:59-71`
- Test: extends `test/FlaUI.Mcp.Tests/Perception/WaitDiagnosticTests.cs`

This is what closes the defect's headline symptom. Task 6 explains why a wait on a culled element fails; this lets a caller actually perform it.

- [ ] **Step 1: Write the failing test**

Append to `WaitDiagnosticTests.cs`:

```csharp
    [Fact]
    public async Task Opting_in_lets_exists_satisfy_on_a_spatially_culled_element()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var (wait, handle) = await ArrangeAsync(dispatcher, mgr, _app.Process.Id);

        var r = await wait.WaitForAsync(handle, "automationId", "SpatialOffscreenButton",
            until: "exists", equals: null, timeoutMs: 1500, pollIntervalMs: 300,
            includeOffscreen: true);

        Assert.True(r.Satisfied);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~Opting_in_lets_exists"`
Expected: FAIL — compile error, no `includeOffscreen` parameter.

- [ ] **Step 3: Add the parameter**

Change `WaitForAsync`'s signature (`WaitCoordinator.cs:69-70`) to append one **optional** parameter, so every existing call site keeps compiling:

```csharp
    public async Task<WaitForResult> WaitForAsync(WindowHandle handle, string by, string value,
        string until, string? equals, int timeoutMs, int pollIntervalMs, bool includeOffscreen = false)
```

Replace the `alreadyUnculled` placeholder from Task 6 with:

```csharp
            bool alreadyUnculled = includeOffscreen;
```

And make the poll options honour it — replace the `pollOptions` line from Task 5 with:

```csharp
                var pollOptions = (latched || includeOffscreen)
                    ? (includeOffscreen
                        ? PollOptions with { IncludeOffscreen = true }   // caller opted out of BOTH filters
                        : UnculledPollOptions)                            // latched: spatial cull only
                    : PollOptions;
```

Note the asymmetry and do not "simplify" it: `includeOffscreen: true` disables **both** filters, matching what the same parameter means on `desktop_snapshot`. The latch disables **only** the spatial cull.

Guard the `gone` confirmation the same way — the condition from Task 5 becomes:

```csharp
                if (satisfied && until == "gone" && !latched && !includeOffscreen)
```

- [ ] **Step 4: Thread it through the tool**

In `SnapshotTools.cs`, add a parameter after `pollIntervalMs` (`:66`):

```csharp
        [Description("Reach elements UIA reports off-screen AND elements laid out past the window edge (default false, matching desktop_snapshot).")] bool includeOffscreen = false)
```

and pass it at `:69`:

```csharp
            var r = await _wait.WaitForAsync(new WindowHandle(window), by, value, until, equals, timeoutMs, pollIntervalMs, includeOffscreen);
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test --filter "FullyQualifiedName~WaitDiagnosticTests"`
Expected: `Passed! - Failed: 0, Passed: 4`

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/WaitCoordinator.cs src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs test/FlaUI.Mcp.Tests/Perception/WaitDiagnosticTests.cs
git commit -m "feat(wait): add includeOffscreen to desktop_wait_for

Additive, default false, so no existing caller changes. Mirrors the
parameter desktop_snapshot already exposes and carries the same meaning."
```

## Task 8: Popup coverage for `find` and `valueEquals`

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/SnapshotEngine.cs:184` (visibility only)
- Modify: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs:467-493`, `:557-571`
- Test: `test/FlaUI.Mcp.Tests/Perception/PopupRootCoverageTests.cs` (create)

Backlog: `docs/fix-the-tool-backlog/value-and-find-paths-miss-desktop-level-popups.md`.

Both paths root at bare `win` while `BuildModelAsync` passes owner-popups (`:424`, `:429`). Every other perception and action path already uses `PopupFinder.SearchRoots` (`:53`, `:67`, `:85`, `:104`, `:230`, `:251`, `:273`, `:642`, `:644`).

- [ ] **Step 1: Measure the popup flavour FIRST**

This is the measurement gate. Whether a WPF window-child menu reproduces the blindness is empirical, and the `MenuTarget` fixture probably does **not** — a window-child popup *is* a descendant of `win` (`PopupFinder.cs:21-25`, `:58-60`).

Add this probe to `PopupRootCoverageTests.cs`, run it, read the assertion message, then **delete the probe** before committing. It is a measurement instrument, not a test — it asserts a falsehood so the runner prints the answer.

```csharp
    // TEMPORARY PROBE — run once, read the message, then DELETE this method.
    // Answers: is the fixture's WPF context menu a descendant of `win` (window-child) or a
    // desktop-level popup? PopupFinder.cs:21-25 says both shapes exist; which one this OS build
    // produces decides whether the fixture can reproduce the blindness at all.
    [Fact]
    public async Task PROBE_is_the_fixture_menu_reachable_from_the_window_root()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        await mgr.FocusAsync(handle);
        await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            var target = win.FindFirstDescendant(cf => cf.ByAutomationId("MenuTarget"))!;
            target.RightClick();
            return true;
        });
        await Task.Delay(400);

        var (fromWindow, fromPopupRoots) = await mgr.RunWithWindowAndDesktopAsync(handle, (win, desktop) =>
        {
            int viaWin = win.FindAllDescendants(cf => cf.ByAutomationId("MenuAlpha")).Length;
            int viaPopups = PopupFinder.FindOwnerPopups(desktop, win)
                .Sum(p => p.FindAllDescendants(cf => cf.ByAutomationId("MenuAlpha")).Length);
            return (viaWin, viaPopups);
        });

        // Deliberately fails so the runner prints the measurement.
        Assert.True(false, $"PROBE RESULT: viaWindowRoot={fromWindow}, viaPopupRoots={fromPopupRoots}");
    }
```

Run: `dotnet test --filter "FullyQualifiedName~PROBE_is_the_fixture_menu"`

Read `PROBE RESULT` from the failure message and **record both numbers in Task 8 Step 8's commit message.** Then delete the probe method.

Interpreting it:
- `viaWindowRoot >= 1` → the menu is a **window child**. The fixture **cannot** reproduce the blindness; write only the dedup test. Note that `viaWindowRoot >= 1 && viaPopupRoots >= 1` is the **dedup** case — the element is reachable twice, which is exactly what Step 5's `RidEqual` dedup exists to collapse.
- `viaWindowRoot == 0 && viaPopupRoots >= 1` → **desktop-level**. The fixture *does* reproduce it; add a blindness test asserting `find` returns the item after the fix.
- Both zero → the menu did not open. Do not proceed; the arrange is broken, not the theory.

- If it **does** find it: the fixture cannot reproduce the blindness; write only the dedup test below, and say so.
- If it does **not**: write both the dedup test and a blindness test.

Do not fabricate a repro either way.

- [ ] **Step 2: Write the dedup test**

```csharp
using System.Linq;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>Routing find through PopupFinder.SearchRoots means a WINDOW-CHILD popup is reachable from
/// two roots — via `win` and via the popup root. SnapshotEngine prunes that duplication for snapshots
/// (pinned by PopupGraftingTests.cs:34-37); find has no such pruning, so it must dedup by RuntimeId.</summary>
[Trait("Category", "Desktop")]
public class PopupRootCoverageTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    public PopupRootCoverageTests(TestAppFixture app) => _app = app;

    [Fact]
    public async Task A_popup_element_is_returned_exactly_once_across_roots()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        await mgr.FocusAsync(handle);
        await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            var target = win.FindFirstDescendant(cf => cf.ByAutomationId("MenuTarget"))!;
            target.RightClick();
            return true;
        });
        await Task.Delay(400); // let the menu open

        // FindQuery is a POSITIONAL record (FindQuery.cs:13-19) — object-initializer syntax does not
        // compile. Order: AutomationId, Name, NameMatch, ControlType, EnabledOnly, IgnoreCase=false.
        // NameMatch is non-nullable and must be supplied even when Name is null.
        var query = new FindQuery("MenuAlpha", null, "eq", null, false);
        var r = await perception.FindAsync(handle, query, max: 20, scopeRef: null);

        Assert.Equal(1, r.Matches.Count(m => m.AutomationId == "MenuAlpha"));
        Assert.Equal(1, r.TotalMatches);
    }

    /// <summary>Step 6 rewrites EvaluateSelectorValueAsync's search into a multi-root loop, and it is
    /// the ONLY consumer of that method — until="valueEquals". Without this, a typo in that loop shows
    /// up as a valueEquals that silently never satisfies, which is indistinguishable from a slow app.
    /// Note this asserts coverage, not the fix: if the fixture's menu is a window child (see the probe
    /// in Step 1), valueEquals could already reach it before Step 6. It still guards the rewrite.</summary>
    [Fact]
    public async Task ValueEquals_reaches_an_element_inside_an_open_popup()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var wait = new WaitCoordinator(perception);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        await mgr.FocusAsync(handle);
        await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            var target = win.FindFirstDescendant(cf => cf.ByAutomationId("MenuTarget"))!;
            target.RightClick();
            return true;
        });
        await Task.Delay(400);

        // MenuAlpha's Header is "Alpha" (MainWindow.xaml:100), and the value read falls back
        // ValuePattern -> Name, so a MenuItem with no ValuePattern yields its Name.
        var r = await wait.WaitForAsync(handle, "automationId", "MenuAlpha",
            until: "valueEquals", equals: "Alpha", timeoutMs: 1500, pollIntervalMs: 300);

        Assert.True(r.Satisfied);
    }
}
```

If the third assertion fails because the menu item's value is not literally `"Alpha"`, read what
`EvaluateSelectorValueAsync` actually returned before changing the expectation — the fallback chain is
`ValuePattern → Name`, and a WPF `MenuItem`'s UIA `Name` normally is its `Header`. Adjust the expected
string to the measured one and say so in the commit; do **not** weaken the assertion to
`Assert.NotNull`.

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~PopupRootCoverageTests"`
Expected: FAIL on `TotalMatches` being 2 once Step 5 lands — **or** PASS today, because `find` cannot see the popup at all. Either outcome is informative; record which.

- [ ] **Step 4: Make `RidEqual` reusable**

`SnapshotEngine.cs:184` is `private static bool RidEqual(int[] a, int[] b)`. Change `private` to `internal`:

```csharp
    internal static bool RidEqual(int[] a, int[] b)
```

Do not duplicate the logic, and do not reach for `HashSet<int[]>` — its default comparer tests **reference** equality, so it would never dedup and the bug would look like the fix simply not working.

- [ ] **Step 5: Route `FindAsync` through all roots**

In `PerceptionManager.cs`, replace the root selection at `:467-469`:

```csharp
            AutomationElement root = string.IsNullOrEmpty(scopeRef)
                ? win
                : _refs.Resolve(handle.Id, scopeRef!, PopupFinder.SearchRoots(win, desktop));
```

with a root **list**:

```csharp
            // A popup can live at the desktop level (Win32 #32768) or as a window child (WPF/.NET 10) —
            // PopupFinder.cs:21-25. Rooting at bare `win` reaches only the second, so find disagreed with
            // snapshot about whether a menu item exists.
            IReadOnlyList<AutomationElement> roots = string.IsNullOrEmpty(scopeRef)
                ? PopupFinder.SearchRoots(win, desktop)
                : new[] { _refs.Resolve(handle.Id, scopeRef!, PopupFinder.SearchRoots(win, desktop)) };
```

Then replace the single search at `:488-493`:

```csharp
            AutomationElement[] raw;
            try
            {
                raw = (hasNative ? root.FindAllDescendants(cf => Build(cf)!) : root.FindAllDescendants()).ToArray();
            }
            catch { raw = System.Array.Empty<AutomationElement>(); }
```

with a per-root loop that concatenates **in root order** and dedups:

```csharp
            // PER-ROOT isolation: one broad catch around the whole loop would let a transient
            // ElementNotAvailableException in ANY popup (a tooltip closing mid-search) discard every
            // valid match from the window and the other popups. A root that throws contributes nothing.
            // Root order is preserved and dedup happens BEFORE the `max` cap below, so truncation never
            // silently prefers window matches over popup ones.
            var rawList = new List<AutomationElement>();
            var seenRids = new List<int[]>();
            foreach (var r in roots)
            {
                AutomationElement[] perRoot;
                try
                {
                    perRoot = (hasNative ? r.FindAllDescendants(cf => Build(cf)!) : r.FindAllDescendants()).ToArray();
                }
                catch { continue; }

                foreach (var el in perRoot)
                {
                    var rid = SafeRead(() => el.Properties.RuntimeId.ValueOrDefault, (int[]?)null) ?? System.Array.Empty<int>();
                    // A window-child popup is reachable from BOTH `win` and its own popup root.
                    if (rid.Length > 0 && seenRids.Any(s => SnapshotEngine.RidEqual(s, rid))) continue;
                    if (rid.Length > 0) seenRids.Add(rid);
                    rawList.Add(el);
                }
            }
            AutomationElement[] raw = rawList.ToArray();
```

The post-filter loop at `:497-535` is unchanged — it still applies the `IsPassword` redaction before the match decision, so popup elements pass the same security floor.

- [ ] **Step 6: Route `EvaluateSelectorValueAsync` through all roots**

At `:557-571`, replace the single-root `Match()` with a per-root scan that keeps the `NotOffscreen` filter and isolates failures:

```csharp
            AutomationElement? Match()
            {
                foreach (var r in PopupFinder.SearchRoots(win, desktop))
                {
                    AutomationElement? hit = null;
                    try
                    {
                        hit = by switch
                        {
                            "automationId" => r.FindAllDescendants(cf => cf.ByAutomationId(value)).FirstOrDefault(NotOffscreen),
                            "name" => r.FindAllDescendants(cf => cf.ByName(value)).FirstOrDefault(NotOffscreen),
                            "controlType" => r.FindAllDescendants().FirstOrDefault(e => { try { return NotOffscreen(e) && e.ControlType.ToString().Equals(value, System.StringComparison.OrdinalIgnoreCase); } catch { return false; } }),
                            _ => null
                        };
                    }
                    catch { continue; }   // per-root isolation
                    if (hit is not null) return hit;
                }
                return null;
            }
```

`SearchRoots[0]` is `win` (`PopupFinder.cs:16`), so a window match still wins first and existing behaviour is preserved. No dedup is needed here — the first hit wins.

- [ ] **Step 7: Run the tests**

Run: `dotnet test --filter "FullyQualifiedName~PopupRootCoverageTests"`
Expected: `Passed! - Failed: 0, Passed: 2` — the dedup test and the `valueEquals` coverage test. If it reports 3, the temporary probe from Step 1 was not deleted; delete it.

Run: `dotnet test --filter "FullyQualifiedName~FindTests"`
Expected: all pass, unedited. `find`'s existing contract must not move.

Run: `dotnet test --filter "FullyQualifiedName~PopupGrafting"`
Expected: `Passed! - Failed: 0, Passed: 1` — snapshot dedup still holds.

- [ ] **Step 8: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs src/FlaUI.Mcp.Core/Perception/SnapshotEngine.cs test/FlaUI.Mcp.Tests/Perception/PopupRootCoverageTests.cs
git commit -m "fix(perception): search popup roots in find and valueEquals

Both rooted at bare win while BuildModelAsync passes owner-popups, so
snapshot and wait_for(exists) could see a menu item that find and
wait_for(valueEquals) could not.

Per-root try/catch so one transient popup fault cannot zero every match,
and RuntimeId dedup before the max cap so truncation does not silently
prefer window matches. Measured popup flavour: <RECORD THE RESULT HERE>."
```

## Task 9: Measure the unculled walk's cost

**Files:**
- Test: temporary; commit only the recorded numbers

The spec requires this before SP1's live tests are called green. After the latch flips the cost is *concentrated*: every remaining poll of that call does a full unculled walk.

- [ ] **Step 1: Measure**

Run the `gone` latch test against the fixture and record `WalkCount` plus process allocation:

```bash
dotnet test --filter "FullyQualifiedName~The_latch_stops_the_confirmation_walk" -l "console;verbosity=detailed"
```

Record: walks issued, and `GC.GetTotalAllocatedBytes()` around the wait. **Measure walk count and allocation, not wall-clock** — a timing-shaped assertion is what masked a real teardown defect for two rounds in A1a.

- [ ] **Step 2: Judge**

The fixture's out-of-window subtree is a single button, so this measures the floor, not the ceiling. State plainly in the commit whether the number is a floor measurement and what a large-subtree app would multiply it by.

If it looks severe, do **not** silently adopt the targeted-liveness optimisation — it changes semantics (a walk re-matches the *selector* each poll, so an app that recreates the element under a new `RuntimeId` currently keeps the wait alive; a held reference would report the original gone and satisfy). Surface it to the user as a decision.

- [ ] **Step 3: Record it in the spec**

Append the measured numbers to the "Open question" section of `docs/superpowers/specs/2026-07-29-sp0-sp1-wait-for-contract-design.md`, replacing "has **not** been measured".

```bash
git add docs/superpowers/specs/2026-07-29-sp0-sp1-wait-for-contract-design.md
git commit -m "docs(spec): record the measured unculled-walk cost"
```

## Task 10: Retire the defect test and its backlog entry

**Files:**
- Modify: `test/FlaUI.Mcp.Tests/Perception/WaitForCullDefectTests.cs`
- Delete: `docs/fix-the-tool-backlog/wait-for-cull-disagrees-with-find.md`

- [ ] **Step 1: Read the test as it stands**

Open `test/FlaUI.Mcp.Tests/Perception/WaitForCullDefectTests.cs`. It ends in `Assert.Fail` by design and carries both `[Trait("Category","Desktop")]` and `[Trait("Category","KnownDefect")]`.

- [ ] **Step 2: Complete the assertion**

Replace the `Assert.Fail(...)` with the behaviour SP1 now guarantees: `find` returns the element, and `wait_for(exists)` still does not satisfy by default **but now explains itself**, while opting in satisfies.

```csharp
        Assert.Equal(1, found.TotalMatches);                       // find sees it, as it always did
        Assert.False(defaultWait.Satisfied);                       // default semantics unchanged
        Assert.Equal("outsideWindowBounds", defaultWait.UnsatisfiedBecause);   // ...but no longer a bare timeout
        Assert.True(optedIn.Satisfied);                            // includeOffscreen:true performs the wait
```

Keep the variable names already in the file; if they differ from `found` / `defaultWait` / `optedIn`, use the file's own names rather than renaming.

- [ ] **Step 3: Run it while it is still trait-gated**

Run: `dotnet test --filter "Category=KnownDefect"`
Expected: `Passed! - Failed: 0`

- [ ] **Step 4: Strip the KnownDefect trait**

Remove `[Trait("Category", "KnownDefect")]`, keeping `[Trait("Category", "Desktop")]` — this test needs a live window, so it stays out of CI.

- [ ] **Step 5: Delete the backlog entry**

```bash
git rm docs/fix-the-tool-backlog/wait-for-cull-disagrees-with-find.md
```

- [ ] **Step 6: Full gates**

```bash
dotnet test -c Release --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"
dotnet test --filter "Category=Desktop&Category!=KnownDefect&FullyQualifiedName!~PopupGrafting"
dotnet test --filter "FullyQualifiedName~PopupGrafting"
```

Expected: headless green; Desktop green with **0 skipped** (a skip reads as success under xUnit, which exits 0 on a skip — check the skipped count, not just the exit code).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "test(wait): retire the wait-for-cull defect test and its backlog entry

The repro now asserts the fixed behaviour: find and wait_for agree,
the default refusal explains itself, and includeOffscreen performs
the wait. KnownDefect trait stripped; Desktop retained."
```

## Task 11: Update the driving skill and docs

**Files:**
- Modify: `.claude/skills/driving-flaui-mcp/SKILL.md` (GROWTH region only)
- Modify: `ROADMAP.md`

- [ ] **Step 1: Record the `gone` distinction for consuming agents**

The spec requires this be stated where a driver will see it. Inside the `AUTOTRAIN:GROWTH` markers **only** — never touch the hand-authored floor — add one line, and compress an existing line if the region would exceed its 30-line hard cap:

```
- `wait_for until=gone` means gone from the tree, not "off screen": an element with IsOffscreen=true still satisfies instantly, one merely past the window edge no longer does. Pass includeOffscreen:true to wait on either.
```

- [ ] **Step 2: Tick the ROADMAP items**

Mark opportunistic item 6 (JSON-shape tests) done, and both filed defects resolved. Leave items 3, 5, 7, 9, 4, 8, 10 untouched — they are SP2/SP3/SP4.

- [ ] **Step 3: Commit**

```bash
git add .claude/skills/driving-flaui-mcp/SKILL.md ROADMAP.md
git commit -m "docs: record the wait_for gone/includeOffscreen distinction"
```

---

## Self-review

**Spec coverage.** SP0 both layers → Tasks 1, 2. Flag split → Task 3. `gone` + latch → Task 5. Latch observability → Task 4. Timeout diagnostic + direct intersection test → Task 6. Emit-null wire rule → Task 6 Steps 3, 6, 7. `int[]` rect shape → Task 6 Step 3. Best-effort degradation → Task 5 Step 4, Task 6 Step 4. `includeOffscreen` param + bypass guard → Task 7. Popup fix, per-root isolation, dedup, root-order-before-cap → Task 8. Popup flavour measurement gate → Task 8 Step 1. Cost measurement gate → Task 9. Retire the defect test → Task 10. Behaviour disclosure → Task 11. **No spec section is unmapped.**

**Placeholder scan.** One deliberate placeholder: `<RECORD THE RESULT HERE>` in Task 8's commit message, which is an instruction to record a measurement, not a deferred decision. The two measurement gates (Tasks 8 Step 1, 9) are gates by design — the spec forbids fabricating either answer.

**Type consistency.** `WaitForResult` gains `UnsatisfiedBecause` / `ElementBounds` / `WindowBounds` in Task 6 and is used under those exact names in Tasks 6, 7, 10. `CullToWindowBounds` is defined in Task 3 and used in Tasks 5 and 7. `UnculledPollOptions` is defined in Task 5 Step 3 and used in Tasks 5, 6. `WalkCount` / `CountWalk` are defined in Task 4 and used in Tasks 5, 6, 9. `RidEqual` becomes `internal` in Task 8 Step 4 before its use in Step 5. `alreadyUnculled` is introduced as a placeholder in Task 6 Step 4 and given its real initialiser in Task 7 Step 3 — flagged explicitly at both sites.

**Ordering hazard, called out.** Task 6 depends on Task 5's `latched` variable and `UnculledPollOptions`, and Task 7 rewrites two lines Task 5 and Task 6 introduce. Execute 3 → 4 → 5 → 6 → 7 in order. Tasks 8–11 are independent of that chain.

**Plan review, folded.** An adversarial pass over this document returned two findings, both accepted:

1. **`EvaluateSelectorValueAsync` was changed but untested.** Task 8 Step 6 rewrites its search into a multi-root loop, and `until="valueEquals"` is its only consumer — so a typo there would surface as a `valueEquals` that silently never satisfies, indistinguishable from a slow app. Task 8 Step 2 now carries `ValueEquals_reaches_an_element_inside_an_open_popup`.
2. **Task 8 Step 1 told the implementer to "write a throwaway probe" without giving the code**, violating this plan's own no-placeholders standard. The probe is now written out in full, with the three result interpretations enumerated and an explicit instruction to delete it before committing.

Findings on citations, compilation, ordering, type consistency and missing tasks: none.
