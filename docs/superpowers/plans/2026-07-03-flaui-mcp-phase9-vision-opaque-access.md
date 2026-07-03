# Phase 9 — Vision & Opaque-App Access Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the agent perception + precise targeting on opaque (Chromium/Electron) and zero-accessibility (game/canvas/Citrix) windows via two native-first prongs — an **accessibility wake** that hydrates Chromium's UIA tree, and **OCR text targeting** that resolves visible text to click coordinates.

**Architecture:** Prong A (`desktop_wake_accessibility`/`desktop_release_accessibility`) reuses the Phase-8 `IUiaEventSource.Register` mechanism with a **no-op (null) sink** to activate Chromium AXMode and HOLD it, auto-releasing via the Phase-6 `WindowManager.WindowInvalidated` chokepoint — separate bookkeeping (`WakeRegistry`) with its own caps, never touching the watch channel/coalescer/drain. Prong B (`desktop_find_text`/`desktop_wait_for_text`) captures the window off the query STA (`ScreenCapture.CaptureRectangle`, already off-STA), runs on-box `Windows.Media.Ocr`, maps OCR bitmap pixels → screen px → window-fractions with a pure headless `CoordinateMapping` core, and fuzzy-matches with a pure `TextMatcher` core. A `wakeable` hint on `desktop_snapshot` routes the agent between the two prongs in one round-trip.

**Tech Stack:** C#/.NET 10 (`net10.0-windows`; Spike α confirms the WinRT-projection TFM/package), FlaUI.UIA3 5.0.0, ModelContextProtocol 1.4.0, `Windows.Media.Ocr` (WinRT, on-box), xUnit.

**Spec:** `docs/superpowers/specs/2026-07-03-flaui-mcp-phase9-vision-opaque-access-design.md` (committed `28b6dde`, user-approved). Section cites below (§N) refer to that spec.

**Branch:** Create `phase-9-vision-opaque-access` off `master` before Task 1 (per subagent-driven-development; do NOT implement on `master`).

---

## Execution notes for the controller (read before dispatching)

- **Spikes gate the build.** Tasks 1–2 are build-time spikes (Phase-8 precedent: spikes as gating plan tasks). **Task 1 (Spike α) MUST report GO before Tasks 7–11** (all OCR work). **Task 2 (Spike β) MUST report before Tasks 3–5** (wake) — wake is already spike-confirmed on VS Code (§4), so β only widens confidence and pins the minimal held event kind. If a spike returns NO-GO, STOP and escalate to the user (a NO-GO on α means the OCR prong needs a different engine — a spec change, not a plan patch).
- **Model tiering (per CODING SUBAGENT RULES):** Spikes α/β and the OCR-engine WinRT task (Task 9) touch COM/WinRT/threading + unknown API surface → **Opus**. Pure-core tasks (3, 6, 7, 8) with complete specs → **Sonnet**. Service/tool wiring against a named template (Tasks 4, 5, 10, 11) → **Sonnet**. Docs (Task 12) → **Sonnet/Haiku**. Every implementer dispatch includes a **STATE-VERIFICATION Step 0** (open the cited files, confirm the pasted context matches; on mismatch STOP + report `STATE_MISMATCH`) and a **SHAPE-DIVERGENCE STOP** (if compiling would change any wire/type shape, STOP + report `[spec] -> [yours] because <reason>`).
- **Oracle:** the pinning tests in each task ARE the oracle. "Tests are already written — implement until they pass." If a value looks wrong, surface the conflict; do not edit the test to match the code.
- **Build/test commands (repo gate as-is — do NOT invent stricter flags):**
  - Build: `dotnet build` — expected `Build succeeded.` with **0 Warning(s), 0 Error(s)** across the 5 projects.
  - Headless (CI-safe): `dotnet test --filter "Category!=Desktop"` — expected `Passed!` (all green).
  - Desktop (needs a live console; this box is RDP — see [[project-flaui-mcp-test-environment]]): `dotnet test --filter "Category=Desktop"`.
- **Two-stage review after each task:** spec-compliance review, then `ecc:csharp-reviewer` quality review. Fix loops until both are clean before marking the task complete.

---

## File Structure

**New (Prong A — wake):**
- `src/FlaUI.Mcp.Core/Watch/WakeRegistry.cs` — pure bookkeeping of held wake handles; own caps; evict-by-window.
- `src/FlaUI.Mcp.Core/Watch/WakeService.cs` — façade: resolve+deny-list window on STA, reserve a wake id, register a null-sink StructureChanged handler via `IUiaEventSource`, HOLD the disposable, auto-release on `WindowInvalidated`.
- `src/FlaUI.Mcp.Server/Tools/WakeTools.cs` — `desktop_wake_accessibility` / `desktop_release_accessibility` / `desktop_list_wakes`.

**New (Prong B — OCR):**
- `src/FlaUI.Mcp.Core/Vision/OcrWord.cs` — pure DTO: one recognized text run + its bitmap-space rect.
- `src/FlaUI.Mcp.Core/Vision/CoordinateMapping.cs` — pure math: bitmapPx → screenPx → windowPct (the §6 dealbreaker guard).
- `src/FlaUI.Mcp.Core/Vision/TextMatcher.cs` — pure fuzzy/exact matching of `query` against `OcrWord`s (§3).
- `src/FlaUI.Mcp.Core/Vision/IOcrEngine.cs` — engine seam (fake-able headless); the WinRT impl lives in it.
- `src/FlaUI.Mcp.Core/Vision/WindowsMediaOcrEngine.cs` — `Windows.Media.Ocr` impl (Spike α confirms the surface).
- `src/FlaUI.Mcp.Core/Vision/TextFinder.cs` — orchestrates capture-geometry → OCR → map → match into `TextMatch` results (pure given an `IOcrEngine` + a captured bitmap; no MCP types).
- `src/FlaUI.Mcp.Server/Tools/FindTextTools.cs` — `desktop_find_text` / `desktop_wait_for_text`.

**Modified (Prong routing hint):**
- `src/FlaUI.Mcp.Core/Perception/SnapshotResult.cs` — add `bool Wakeable`.
- `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` — compute wakeable on the STA in `SnapshotAsync`.
- `src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs` — emit `wakeable` in the JSON when true.

**Modified (wiring / docs):**
- `src/FlaUI.Mcp.Server/Program.cs` — DI for `WakeRegistry`/`WakeService`/`WakeTools`, `IOcrEngine`/`TextFinder`/`FindTextTools`.
- `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj` (and possibly `FlaUI.Mcp.Core.csproj`) — TFM/package per Spike α; `Version` 0.8.0 → 0.9.0.
- `CHANGELOG.md`, `ROADMAP.md`, `README.md`, `.claude/skills/driving-flaui-mcp/SKILL.md`, `install.ps1` (version).

---

## Task 1: Spike α — OCR engine feasibility (`Windows.Media.Ocr`) — GATES Tasks 7–11

**This is a throwaway spike.** Goal: prove a `net10.0-windows` project can reference and run `Windows.Media.Ocr` on a captured bitmap, and measure the coordinate round-trip on a scaled display. Deliverable is a **spike doc + a GO/NO-GO with the confirmed TFM/package + the exact WinRT call surface** — NOT production code (any prototype code is deleted after).

**Files:**
- Create: `docs/superpowers/spikes/2026-07-03-spikeA-winrt-ocr.md` (findings)
- Scratch prototype (throwaway; do NOT commit prototype `.cs`): a temp console project or a temporarily-added `[Trait("Category","Desktop")]` test.

- [ ] **Step 1: Determine the projection path.** Try, in order, and record which works with the repo's `dotnet build`:
  1. Bump `FlaUI.Mcp.Core.csproj` `TargetFramework` to `net10.0-windows10.0.19041.0` (enables built-in CsWinRT WinRT projections — `Windows.Media.Ocr`, `Windows.Graphics.Imaging.SoftwareBitmap` become referable with no package). Confirm the solution still builds 0/0 and existing headless tests still pass.
  2. If (1) is problematic, add `<PackageReference Include="Microsoft.Windows.SDK.Contracts">` (or `Microsoft.Windows.CsWinRT`) and record the exact version.

- [ ] **Step 2: Prototype capture → SoftwareBitmap → OCR.** Using a real `ScreenCapture.CaptureRectangle` PNG of a window with known text (launch Notepad, type "Submit Order 42"), decode to a `SoftwareBitmap` and run:
```csharp
// Confirm THIS exact surface compiles + runs; record any deviation in the spike doc.
using Windows.Media.Ocr;
using Windows.Graphics.Imaging;
var engine = OcrEngine.TryCreateFromUserProfileLanguages();   // may be null if no OCR language pack
OcrResult result = await engine.RecognizeAsync(softwareBitmap);
foreach (var line in result.Lines)
    foreach (var word in line.Words)
        Console.WriteLine($"'{word.Text}' @ {word.BoundingRect}"); // BoundingRect is in BITMAP pixels
```
Record: is `TryCreateFromUserProfileLanguages()` non-null on this box? What is `Word.BoundingRect`'s unit/origin (confirm it is top-left bitmap px, matching the §6 contract)? How is `SoftwareBitmap` obtained from a `System.Drawing.Bitmap`/PNG stream (e.g. `BitmapDecoder.CreateAsync` from an `IRandomAccessStream`)?

- [ ] **Step 3: Measure accuracy + the coordinate round-trip.** On a **150%-scaled** display: capture a window, OCR it, take a known control's text, apply the §6 mapping by hand (bitmapPx → screenPx using `CaptureResult.ScaleApplied`/`X`/`Y` → windowPct using `desktop_get_bounds`), and verify the derived screen point lands inside the control. Record accuracy on small UI text (does OCR read "Submit" reliably or mangle it — this justifies the fuzzy default, §3).

- [ ] **Step 4: Write the spike doc + VERDICT.** Document: the confirmed TFM/package; the exact `IOcrEngine` surface Task 9 must implement; the `SoftwareBitmap` decode path; whether OCR language packs are a prerequisite (and the failure mode if absent → Task 9/10 must return a clean `OcrUnavailable` error, not crash); accuracy notes; the measured coord round-trip result. End with **VERDICT: GO** (WinRT works) or **NO-GO** (needs Tesseract/other → escalate to user).

- [ ] **Step 5: Commit findings only** (delete the throwaway prototype; keep any TFM/package change ONLY if it is the confirmed path — otherwise revert it and let Task 9 apply it).
```bash
git add docs/superpowers/spikes/2026-07-03-spikeA-winrt-ocr.md
git commit -m "spike(vision): Windows.Media.Ocr feasibility on net10.0-windows + coord round-trip"
```

---

## Task 2: Spike β — wake generality & minimal held registration — GATES Tasks 3–5

**Throwaway spike.** The §4 wake is already confirmed on VS Code. β widens confidence to another Electron app and pins the **minimal event kind** the held registration needs.

**Files:**
- Create: `docs/superpowers/spikes/2026-07-03-spikeB-wake-generality.md`

- [ ] **Step 1: Reproduce on a 2nd Electron app.** With the installed server (or a Desktop test), on an Electron app other than VS Code if one is available (else document VS Code as the sole confirmed fixture and flag the generality risk): `desktop_snapshot` (record node count, expect opaque ~≤15), then `desktop_watch` with ONLY `["structure_changed"]`, then `desktop_snapshot` again (expect hydrated). Confirm StructureChanged-only is sufficient to wake (VS Code spike used it).

- [ ] **Step 2: Test a lighter activation (optional).** Note whether a one-shot `WM_GETOBJECT`/`UiaRootObjectId` HOLDS the tree awake without a live event handler (spec §4 open question). If uncertain or it doesn't HOLD, the answer is "held StructureChanged registration" — record that as the decision.

- [ ] **Step 3: Confirm release collapses the tree.** `desktop_unwatch` → `desktop_snapshot` → expect collapse to ~≤15 nodes (non-persistent wake, §4).

- [ ] **Step 4: Write the spike doc + DECISION.** Record: the minimal held event kind (default: `StructureChanged`); whether a lighter one-shot activation holds (default: no → use the held handler); generality notes. This DECISION feeds Task 4.

- [ ] **Step 5: Commit.**
```bash
git add docs/superpowers/spikes/2026-07-03-spikeB-wake-generality.md
git commit -m "spike(wake): StructureChanged-only wake generality + minimal held registration"
```

---

## Task 3: `WakeRegistry` — held-wake bookkeeping with separate caps

**Files:**
- Create: `src/FlaUI.Mcp.Core/Watch/WakeRegistry.cs`
- Test: `test/FlaUI.Mcp.Tests/Watch/WakeRegistryTests.cs`

**Context:** Mirrors `WatchRegistry` (`src/FlaUI.Mcp.Core/Watch/WatchRegistry.cs`) exactly in shape (locked dict, monotonic ids, evict-by-window, idempotent remove) but is a **separate** type with its **own caps** so waking never burns the watch quota (§4.3). Wake ids are prefixed `k` (for "wake" — distinct from watch `s`). Reuses `ToolErrorCode.TooManyWatches` (no new error code; the message names wakes).

- [ ] **Step 1: Write the failing test**

```csharp
// test/FlaUI.Mcp.Tests/Watch/WakeRegistryTests.cs
using System.Linq;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Watch;
using Xunit;

namespace FlaUI.Mcp.Tests.Watch;

public class WakeRegistryTests
{
    [Fact]
    public void Create_mints_monotonic_k_prefixed_never_reused_ids()
    {
        var r = new WakeRegistry();
        Assert.Equal("k1", r.Create("w1"));
        Assert.Equal("k2", r.Create("w2"));
        r.Remove("k1");
        Assert.Equal("k3", r.Create("w3")); // counter never resets
    }

    [Fact]
    public void Create_enforces_per_session_cap_TooManyWatches()
    {
        var r = new WakeRegistry();
        for (int i = 0; i < WakeRegistry.MaxPerSession; i++) r.Create("w" + i);
        var ex = Assert.Throws<ToolException>(() => r.Create("wZ"));
        Assert.Equal(ToolErrorCode.TooManyWatches, ex.Code);
    }

    [Fact]
    public void Remove_is_idempotent()
    {
        var r = new WakeRegistry();
        var a = r.Create("w1");
        Assert.True(r.Remove(a));
        Assert.False(r.Remove(a));
        Assert.False(r.Remove("k999"));
    }

    [Fact]
    public void RemoveByWindow_evicts_all_that_window_returns_ids()
    {
        var r = new WakeRegistry();
        var a = r.Create("w1");
        var b = r.Create("w1");
        var c = r.Create("w2");
        var evicted = r.RemoveByWindow("w1").OrderBy(x => x).ToArray();
        Assert.Equal(new[] { a, b }, evicted);
        Assert.True(r.TryGet(c, out _));
        Assert.False(r.TryGet(a, out _));
    }

    [Fact]
    public void List_reports_window_ids()
    {
        var r = new WakeRegistry();
        var a = r.Create("w1");
        var info = Assert.Single(r.List());
        Assert.Equal(a, info.WakeId);
        Assert.Equal("w1", info.WindowId);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "Category!=Desktop&FullyQualifiedName~WakeRegistryTests"`
Expected: FAIL to compile (`WakeRegistry` / `WakeInfo` do not exist).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/FlaUI.Mcp.Core/Watch/WakeRegistry.cs
using System.Collections.Generic;
using System.Linq;
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Watch;

/// <summary>Public view of one held wake (for desktop_list_wakes, §3).</summary>
public sealed record WakeInfo(string WakeId, string WindowId);

/// <summary>Pure bookkeeping of held accessibility-wake handles (Phase 9 §4.3). SEPARATE from WatchRegistry with
/// its OWN caps so waking a window's UIA tree never consumes the event-watch quota. Thread-safe (locked): the tool
/// layer and the Phase-6 WindowInvalidated evict path both touch it. UIA registration is NOT here — WakeService
/// drives the IUiaEventSource seam with a null sink.</summary>
public sealed class WakeRegistry
{
    // Waking is a baseline perception need (an agent may need Slack+VSCode+Teams+Chrome+Discord awake at once just
    // to SEE them), so the session cap is generous and there is NO per-window cap (one wake per window suffices; a
    // duplicate wake on the same window is allowed but wasteful — the tool layer may reuse instead, Task 5).
    public const int MaxPerSession = 32;

    private readonly object _gate = new();
    private int _counter;
    private readonly Dictionary<string, string> _windowByWake = new(); // wakeId -> windowId

    /// <summary>Reserve a new wake id, enforcing the per-session cap atomically. Throws TooManyWatches when the cap
    /// is reached. The caller (WakeService) then registers the UIA handler and, on failure, calls Remove(id).</summary>
    public string Create(string windowId)
    {
        lock (_gate)
        {
            if (_windowByWake.Count >= MaxPerSession)
                throw new ToolException(ToolErrorCode.TooManyWatches,
                    $"This session already holds {MaxPerSession} accessibility wakes (the per-session cap).",
                    "desktop_release_accessibility a wake you no longer need");
            var id = $"k{++_counter}";
            _windowByWake[id] = windowId;
            return id;
        }
    }

    /// <summary>Idempotent: true if the wake existed and was removed, false otherwise.</summary>
    public bool Remove(string wakeId)
    {
        lock (_gate) return _windowByWake.Remove(wakeId);
    }

    /// <summary>Evict every wake on a (closed) window; returns the removed ids (for handler cleanup).</summary>
    public IReadOnlyList<string> RemoveByWindow(string windowId)
    {
        lock (_gate)
        {
            var ids = _windowByWake.Where(kv => kv.Value == windowId).Select(kv => kv.Key).ToList();
            foreach (var id in ids) _windowByWake.Remove(id);
            return ids;
        }
    }

    /// <summary>The first active wake id on this window, if any (for idempotent reuse in the tool layer).</summary>
    public string? FirstByWindow(string windowId)
    {
        lock (_gate) return _windowByWake.Where(kv => kv.Value == windowId).Select(kv => kv.Key).FirstOrDefault();
    }

    public bool TryGet(string wakeId, out WakeInfo? info)
    {
        lock (_gate)
        {
            if (_windowByWake.TryGetValue(wakeId, out var win)) { info = new WakeInfo(wakeId, win); return true; }
            info = null; return false;
        }
    }

    public IReadOnlyList<WakeInfo> List()
    {
        lock (_gate) return _windowByWake.Select(kv => new WakeInfo(kv.Key, kv.Value)).ToList();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "Category!=Desktop&FullyQualifiedName~WakeRegistryTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Watch/WakeRegistry.cs test/FlaUI.Mcp.Tests/Watch/WakeRegistryTests.cs
git commit -m "feat(wake): WakeRegistry — held-wake bookkeeping with separate session cap"
```

---

## Task 4: `WakeService` — null-sink UIA registration + auto-release on window close

**Files:**
- Create: `src/FlaUI.Mcp.Core/Watch/WakeService.cs`
- Test: `test/FlaUI.Mcp.Tests/Watch/WakeServiceTests.cs`

**Context:** Models `WatchService` (`src/FlaUI.Mcp.Core/Watch/WatchService.cs`) but is far simpler because it feeds NOTHING downstream. It reuses the existing `IUiaEventSource.Register(spec, onCapture)` seam with a **no-op `onCapture`** — the COM callback drops every event at the edge (a "null sink"), so waking never touches the watch channel/coalescer/drain (§4.3). It registers a `WatchSubscriptionSpec` with `Kinds = [StructureChanged]` (or whatever Spike β confirmed minimal), holds the returned `IDisposable`, and subscribes to `WindowManager.WindowInvalidated` to auto-release on window close (§9). **STA-reentrancy (from WatchService.OnWindowInvalidated):** dispose the held registration off the query STA via `Task.Run` (the disposable self-marshals onto the STA and would deadlock if disposed inline on a synchronous WindowInvalidated fire).

**Oracle:** the tests below pin behavior against a **fake `IUiaEventSource`** (headless — no live UIA). Spike β pins the event kind.

- [ ] **Step 1: Write the failing test**

```csharp
// test/FlaUI.Mcp.Tests/Watch/WakeServiceTests.cs
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Watch;
using Xunit;

namespace FlaUI.Mcp.Tests.Watch;

public class WakeServiceTests
{
    // Records registrations + disposals; asserts the null-sink never forwards events anywhere observable.
    private sealed class FakeSource : IUiaEventSource
    {
        public readonly List<WatchSubscriptionSpec> Registered = new();
        public int DisposeCount;
        public IDisposable Register(WatchSubscriptionSpec spec, Action<CapturedEventMeta, object?> onCapture)
        {
            Registered.Add(spec);
            // Fire a bogus event to prove the sink swallows it (no throw, no side effect the service exposes).
            onCapture(new CapturedEventMeta(spec.SubscriptionId, WatchEventKind.StructureChanged, 0, "", DateTime.UtcNow), null);
            return new Disp(this);
        }
        private sealed class Disp : IDisposable
        {
            private readonly FakeSource _s; public Disp(FakeSource s) => _s = s;
            public void Dispose() => _s.DisposeCount++;
        }
    }

    [Fact]
    public async Task Wake_registers_structure_changed_only_and_holds_a_handle()
    {
        var src = new FakeSource();
        var reg = new WakeRegistry();
        var svc = new WakeService(src, reg, windowManager: null!); // null WM ok: no WindowInvalidated in this test path
        var wakeId = await svc.WakeAsync("w1", pid: 111);
        Assert.Equal("k1", wakeId);
        var spec = Assert.Single(src.Registered);
        Assert.Equal("w1", spec.WindowId);
        Assert.Equal(new[] { WatchEventKind.StructureChanged }, spec.Kinds);
        Assert.True(reg.TryGet(wakeId, out _));   // held in the registry
        Assert.Equal(0, src.DisposeCount);        // still held
    }

    [Fact]
    public async Task Release_disposes_the_handle_and_is_idempotent()
    {
        var src = new FakeSource();
        var reg = new WakeRegistry();
        var svc = new WakeService(src, reg, windowManager: null!);
        var wakeId = await svc.WakeAsync("w1", pid: 111);
        await svc.ReleaseAsync(wakeId);
        Assert.Equal(1, src.DisposeCount);
        Assert.False(reg.TryGet(wakeId, out _));
        await svc.ReleaseAsync(wakeId); // idempotent — no throw, no double dispose
        Assert.Equal(1, src.DisposeCount);
    }

    [Fact]
    public async Task Failed_registration_leaves_no_phantom()
    {
        var src = new ThrowingSource();
        var reg = new WakeRegistry();
        var svc = new WakeService(src, reg, windowManager: null!);
        await Assert.ThrowsAnyAsync<Exception>(() => svc.WakeAsync("w1", pid: 111));
        Assert.Empty(reg.List()); // id was reserved then removed on the failed register
    }

    private sealed class ThrowingSource : IUiaEventSource
    {
        public IDisposable Register(WatchSubscriptionSpec spec, Action<CapturedEventMeta, object?> onCapture)
            => throw new InvalidOperationException("boom");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "Category!=Desktop&FullyQualifiedName~WakeServiceTests"`
Expected: FAIL to compile (`WakeService` does not exist).

- [ ] **Step 3: Write minimal implementation**

> **Note:** `WakeAsync` takes `windowId` + a pre-resolved `pid`. The deny-list + PID read on the query STA is done by the TOOL layer (Task 5) — identical to how the tool guards, but kept OUT of this core type so `WakeService` stays headless-testable with a null `WindowManager`. `WindowManager` is used ONLY to hook `WindowInvalidated`; guard the subscription so a null WM (tests) is tolerated.

```csharp
// src/FlaUI.Mcp.Core/Watch/WakeService.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Core.Watch;

/// <summary>Prong A (§4): HOLD a UIA event registration on a window to keep Chromium/Electron AXMode active so its
/// native accessibility tree stays hydrated. Reuses the Phase-8 IUiaEventSource seam with a NULL SINK — the COM
/// callback drops every event, so waking never feeds the watch channel/coalescer/drain (§4.3). Separate accounting
/// (WakeRegistry, own cap). Auto-releases via WindowManager.WindowInvalidated (§9). Process-lifetime singleton on
/// stdio (Program.cs), so the '+=' never leaks.</summary>
public sealed class WakeService
{
    // Spike β decision: StructureChanged-only holds the tree awake. If β proved a different minimal kind, change
    // this ONE array (the rest is kind-agnostic).
    private static readonly WatchEventKind[] WakeKinds = { WatchEventKind.StructureChanged };

    private readonly IUiaEventSource _source;
    private readonly WakeRegistry _registry;
    private readonly WindowManager? _windowManager;

    private readonly object _gate = new();
    private readonly Dictionary<string, IDisposable> _registrations = new();

    public WakeService(IUiaEventSource source, WakeRegistry registry, WindowManager windowManager)
    {
        _source = source;
        _registry = registry;
        _windowManager = windowManager;
        if (_windowManager is not null)
            _windowManager.WindowInvalidated += OnWindowInvalidated;
    }

    /// <summary>Register + hold a null-sink wake on windowId (its PID pre-resolved by the caller for the spec).
    /// Reserves a wake id (TooManyWatches propagates) then registers transactionally (Remove on failure).</summary>
    public Task<string> WakeAsync(string windowId, int pid)
    {
        var id = _registry.Create(windowId); // TooManyWatches propagates
        try
        {
            // CoalesceScope/ScopeRef are irrelevant for a null sink; pass the window id as a stable scope string.
            var spec = new WatchSubscriptionSpec(id, windowId, pid, WakeKinds, null, windowId);
            var reg = _source.Register(spec, NullSink); // <- the wake: activate AXMode, drop the events
            lock (_gate) _registrations[id] = reg;
        }
        catch
        {
            _registry.Remove(id); // no phantom on a failed registration
            throw;
        }
        return Task.FromResult(id);
    }

    /// <summary>Release a held wake by id: dispose its registration (self-marshals onto the query STA), remove it.
    /// Idempotent.</summary>
    public Task ReleaseAsync(string wakeId)
    {
        IDisposable? reg;
        lock (_gate) _registrations.Remove(wakeId, out reg);
        try { reg?.Dispose(); } catch { /* best-effort: handler may already be dead (window gone) */ }
        _registry.Remove(wakeId);
        return Task.CompletedTask;
    }

    /// <summary>The first held wake id on this window, if any (for idempotent reuse by the tool layer).</summary>
    public string? ActiveWakeFor(string windowId) => _registry.FirstByWindow(windowId);

    /// <summary>desktop_list_wakes (§3).</summary>
    public IReadOnlyList<WakeInfo> List() => _registry.List();

    // The NULL SINK (§4.3): fires on a COM RPC thread. Waking registers the handler ONLY to activate AXMode; we
    // DROP the event storm here (236+ StructureChanged on a complex app) — it must NOT reach the watch pipeline.
    private static void NullSink(CapturedEventMeta _, object? __) { /* intentionally empty — drop the event */ }

    // Phase-6 close signal (fires OFF the query STA on the proc.Exited ThreadPool thread). Release every wake on
    // the closed window. STA-REENTRANCY (mirrors WatchService.OnWindowInvalidated): WindowInvalidated can fire ON
    // the query STA (PruneClosedWindows → Invalidate, synchronous); the disposable self-marshals onto that SAME
    // STA and would deadlock if disposed inline — offload the blocking dispose to the ThreadPool. Swallow all: a
    // throw here can be process-fatal on a raw ThreadPool thread.
    private void OnWindowInvalidated(string windowId)
    {
        try
        {
            foreach (var id in _registry.RemoveByWindow(windowId))
            {
                IDisposable? reg;
                lock (_gate) _registrations.Remove(id, out reg);
                if (reg is not null) _ = Task.Run(() => { try { reg.Dispose(); } catch { } });
            }
        }
        catch { /* invalidation-path robustness > surfacing a fault on a process-fatal thread */ }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "Category!=Desktop&FullyQualifiedName~WakeServiceTests"`
Expected: PASS (3 tests). Then full headless: `dotnet test --filter "Category!=Desktop"` → all green.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Watch/WakeService.cs test/FlaUI.Mcp.Tests/Watch/WakeServiceTests.cs
git commit -m "feat(wake): WakeService — null-sink UIA hold + auto-release on WindowInvalidated"
```

---

## Task 5: `WakeTools` + DI wiring — desktop_wake_accessibility / release / list_wakes

**Files:**
- Create: `src/FlaUI.Mcp.Server/Tools/WakeTools.cs`
- Modify: `src/FlaUI.Mcp.Server/Program.cs` (DI block after the Phase-8 watch block, `~line 76`)
- Test: `test/FlaUI.Mcp.Tests/Watch/DesktopWakeTests.cs` (`Category=Desktop` — the §4/§10a regression)

**Context:** The tool resolves the window + deny-list + reads the PID on the query STA (mirrors `WatchService.WatchAsync`'s guard block, `WatchService.cs:60-76`), then calls `WakeService.WakeAsync`. **Idempotent per window** (consumer-taste call, [[feedback-flaui-mcp-claude-is-consumer]]): if the window is already awake, return the existing wakeId rather than stacking a 2nd registration. All three tools are `ReadOnly = true` + lease-exempt (observation only, §8). Uses `WindowManager.RunWithWindowAndDesktopAsync` (seen in `WatchService.cs:61`) and `PerceptionPolicy.IsDenied` (`WatchService.cs:65`).

- [ ] **Step 0 (STATE-VERIFY):** Open `WatchService.cs:58-76`, `WatchTools.cs:39-63`, and `Program.cs:63-76`. Confirm the guard block shape (`RunWithWindowAndDesktopAsync`, `SafeProcessName`/`IsDenied`, `win.Properties.ProcessId.ValueOrDefault`) and the DI idiom match this task. On mismatch STOP + report `STATE_MISMATCH`.

- [ ] **Step 1: Write the implementation**

```csharp
// src/FlaUI.Mcp.Server/Tools/WakeTools.cs
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Watch;
using FlaUI.Mcp.Core.Windows;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Tools;

/// <summary>Prong A MCP surface (§3/§4): activate + HOLD a Chromium/Electron window's native UIA tree so
/// desktop_snapshot/find/interact work on it with full precision. ReadOnly + lease-exempt (observation only —
/// synthesizes no input). Waking is a held registration; it auto-releases when the window closes.</summary>
[McpServerToolType]
public sealed class WakeTools
{
    private readonly WindowManager _windows;
    private readonly WakeService _wake;
    public WakeTools(WindowManager windows, WakeService wake) { _windows = windows; _wake = wake; }

    [McpServerTool(ReadOnly = true), Description(
        "Activate and HOLD an opaque Chromium/Electron window's native accessibility tree so desktop_snapshot / " +
        "desktop_find / interaction tools can see and target its contents with full precision. Use when " +
        "desktop_snapshot returns one big empty Pane AND its 'wakeable' hint is true (VS Code, Slack, Teams, " +
        "Discord, Chrome). Returns {wakeId, window, alreadyAwake}. The wake is HELD until desktop_release_" +
        "accessibility or the window closes; while held, re-snapshot to get the hydrated tree. Idempotent: waking " +
        "an already-awake window returns the existing wakeId (alreadyAwake:true). NOTE: an editor's document TEXT " +
        "may stay behind a screen-reader gate even when woken — use desktop_find_text for that. ReadOnly + " +
        "lease-exempt. Caps: 32 wakes/session (TooManyWatches).")]
    public Task<string> DesktopWakeAccessibility(
        [Description("Window handle, e.g. w1.")] string window)
        => ToolResponse.Guard(async () =>
        {
            // Idempotent reuse: already awake -> return the held wake id.
            var existing = _wake.ActiveWakeFor(window);
            if (existing is not null)
                return ToolResponse.Ok(new { wakeId = existing, window, alreadyAwake = true });

            // Deny-list + PID read on the query STA (mirrors WatchService.WatchAsync guard).
            var pid = await _windows.RunWithWindowAndDesktopAsync(new WindowHandle(window), (win, desktop) =>
            {
                var procName = SafeProcessName(win);
                if (PerceptionPolicy.IsDenied(procName))
                    throw new ToolException(ToolErrorCode.TargetDenied,
                        $"Waking windows owned by '{procName}' is blocked (credential store).",
                        "wake a different, non-sensitive window");
                int p = -1;
                try { p = win.Properties.ProcessId.ValueOrDefault; } catch { }
                return p;
            });

            var wakeId = await _wake.WakeAsync(window, pid);
            return ToolResponse.Ok(new { wakeId, window, alreadyAwake = false });
        });

    [McpServerTool(ReadOnly = true), Description(
        "Release a held accessibility wake from desktop_wake_accessibility. The window's Chromium tree re-collapses " +
        "to opaque after release. Idempotent: an unknown/already-released wakeId returns ok:true.")]
    public Task<string> DesktopReleaseAccessibility(
        [Description("The wakeId from desktop_wake_accessibility, e.g. k1.")] string wakeId)
        => ToolResponse.Guard(async () =>
        {
            await _wake.ReleaseAsync(wakeId);
            return ToolResponse.Ok(new { ok = true, wakeId });
        });

    [McpServerTool(ReadOnly = true), Description(
        "List your active accessibility wakes (recover them after a context loss). Returns " +
        "wakes[{wakeId, window}].")]
    public Task<string> DesktopListWakes()
        => ToolResponse.Guard(() =>
        {
            var wakes = _wake.List().Select(w => new { wakeId = w.WakeId, window = w.WindowId });
            return Task.FromResult(ToolResponse.Ok(new { wakes }));
        });

    // Mirrors WatchService.SafeProcessName.
    private static string? SafeProcessName(AutomationElement el)
    {
        int pid;
        try { pid = el.Properties.ProcessId.ValueOrDefault; } catch { pid = -1; }
        if (pid < 0) return null;
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return null; }
    }
}
```

- [ ] **Step 2: Wire DI in `Program.cs`** — after the Phase-8 watch block (after `builder.Services.AddSingleton<...WatchTools>();`, `~line 76`), insert:

```csharp
// --- Phase 9 accessibility wake (Prong A; null-sink held UIA registration, separate caps) ---
builder.Services.AddSingleton<FlaUI.Mcp.Core.Watch.WakeRegistry>();
builder.Services.AddSingleton<FlaUI.Mcp.Core.Watch.WakeService>();
builder.Services.AddSingleton<FlaUI.Mcp.Server.Tools.WakeTools>();
```

- [ ] **Step 3: Write the Desktop regression test** (the §4 spike as a pinned test — runs only with a live console)

```csharp
// test/FlaUI.Mcp.Tests/Watch/DesktopWakeTests.cs
using System.Threading.Tasks;
using Xunit;
// NOTE: adapt the harness to how existing Desktop tests construct the server graph (see DesktopWatchTests.cs).
// The assertion contract:
//   1. Launch VS Code (or the Spike-β Electron fixture); snapshot -> assert opaque (NodeCount small, ~<=15).
//   2. desktop_wake_accessibility(window) -> snapshot -> assert hydrated (NodeCount jumps, e.g. a named TreeItem
//      present). This is the §4 spike: 14 -> 236 nodes.
//   3. desktop_release_accessibility(wakeId) -> snapshot -> assert re-collapsed (~<=15).

namespace FlaUI.Mcp.Tests.Watch;

[Trait("Category", "Desktop")]
public class DesktopWakeTests
{
    [Fact]
    public async Task Wake_hydrates_opaque_chromium_tree_and_release_recollapses()
    {
        // Arrange: build the WindowManager/PerceptionManager/WakeService graph as DesktopWatchTests does;
        // launch the Electron fixture and get its window handle.
        // Act/Assert per the 3-step contract above (snapshot NodeCount before < after; after-release ~= before).
        await Task.CompletedTask;
        Assert.True(true, "Replace with the live 3-step wake/hydrate/release assertion per DesktopWatchTests harness.");
    }
}
```

> Implementer: flesh this out against the real `DesktopWatchTests.cs` harness (same graph construction + fixture-launch helpers). The controller runs it with `--filter "Category=Desktop"` on a live console; it is NOT part of the CI-safe headless gate.

- [ ] **Step 4: Build + headless gate**

Run: `dotnet build` → 0 Warning(s), 0 Error(s). Then `dotnet test --filter "Category!=Desktop"` → all green (Desktop test is excluded here).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Tools/WakeTools.cs src/FlaUI.Mcp.Server/Program.cs test/FlaUI.Mcp.Tests/Watch/DesktopWakeTests.cs
git commit -m "feat(wake): desktop_wake_accessibility/release/list_wakes tools + DI (Prong A)"
```

---

## Task 6: `wakeable` hint on desktop_snapshot — route wake vs OCR in one round-trip

**Files:**
- Create: `src/FlaUI.Mcp.Core/Perception/WakeabilityHint.cs` (pure predicate)
- Modify: `src/FlaUI.Mcp.Core/Perception/SnapshotResult.cs` (add `bool Wakeable`)
- Modify: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` (compute on STA in `SnapshotAsync`)
- Modify: `src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs` (emit `wakeable` when true)
- Test: `test/FlaUI.Mcp.Tests/Perception/WakeabilityHintTests.cs`

**Context (§3 Seat E / R2):** An opaque window is a single empty `Pane` whether wakeable (Chromium) or not (a game). Expose `wakeable = IsChromiumClass AND tree-is-collapsed/empty` on the snapshot so the agent picks wake-vs-OCR in one round-trip. **Conditioned (R2 Seat E):** if the window already exposes real descendants (already accessible — a screen reader is running, or it launched `--force-renderer-accessibility`), OMIT the hint (nothing to wake). The predicate is PURE (class string + node count); the STA read supplies the window-root ClassName (`win.Properties.ClassName.ValueOrDefault`, as in `WindowManager.cs:382`).

- [ ] **Step 0 (STATE-VERIFY):** Open `SnapshotResult.cs` (confirm the record shape `SnapshotResult(string SnapshotId, string Tree, int NodeCount)`) and `PerceptionManager.SnapshotAsync` (confirm where the `SnapshotResult` is constructed and that the window root element is in scope on the STA to read ClassName). If either differs, STOP + report `STATE_MISMATCH`.

- [ ] **Step 1: Write the failing test**

```csharp
// test/FlaUI.Mcp.Tests/Perception/WakeabilityHintTests.cs
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class WakeabilityHintTests
{
    [Theory]
    [InlineData("Chrome_WidgetWin_1", 3, true)]    // Chromium class + collapsed tree -> wakeable
    [InlineData("Chrome_WidgetWin_0", 5, true)]    // Chromium variant
    [InlineData("Chrome_WidgetWin_1", 200, false)] // Chromium but ALREADY accessible (rich tree) -> omit
    [InlineData("Notepad", 3, false)]              // non-Chromium opaque (e.g. a game) -> not wakeable
    [InlineData(null, 3, false)]                   // no class -> not wakeable
    [InlineData("", 3, false)]
    public void Wakeable_iff_chromium_class_and_collapsed_tree(string? className, int nodeCount, bool expected)
        => Assert.Equal(expected, WakeabilityHint.IsWakeable(className, nodeCount));

    [Fact]
    public void Threshold_is_the_collapse_boundary()
    {
        // The §4 spike: opaque VS Code ~<=15 nodes; hydrated ~236. The boundary is CollapsedNodeThreshold.
        Assert.True(WakeabilityHint.IsWakeable("Chrome_WidgetWin_1", WakeabilityHint.CollapsedNodeThreshold));
        Assert.False(WakeabilityHint.IsWakeable("Chrome_WidgetWin_1", WakeabilityHint.CollapsedNodeThreshold + 1));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "Category!=Desktop&FullyQualifiedName~WakeabilityHintTests"`
Expected: FAIL to compile (`WakeabilityHint` does not exist).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/FlaUI.Mcp.Core/Perception/WakeabilityHint.cs
using System;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>Pure predicate for the desktop_snapshot 'wakeable' hint (Phase 9 §3). True iff the window is a
/// Chromium/Electron host (by Win32 ClassName) AND its accessibility tree is collapsed/empty (opaque) — i.e. it
/// WOULD benefit from desktop_wake_accessibility. A Chromium window that already exposes a rich tree (a screen
/// reader is active, or it launched --force-renderer-accessibility) is NOT flagged: there is nothing to wake.</summary>
public static class WakeabilityHint
{
    // §4 spike: opaque Chromium collapses to ~14-15 nodes (window frame + Min/Restore/Close + empty Panes);
    // hydrated is 200+. A generous boundary well below the hydrated count and above the opaque baseline.
    public const int CollapsedNodeThreshold = 20;

    public static bool IsWakeable(string? className, int nodeCount)
        => IsChromiumClass(className) && nodeCount <= CollapsedNodeThreshold;

    // Chromium/Electron top-level window class is "Chrome_WidgetWin_N" (N = 0/1). Case-insensitive prefix match.
    public static bool IsChromiumClass(string? className)
        => !string.IsNullOrEmpty(className)
           && className!.StartsWith("Chrome_WidgetWin_", StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 4: Thread `Wakeable` through `SnapshotResult`**

Modify `src/FlaUI.Mcp.Core/Perception/SnapshotResult.cs`:
```csharp
namespace FlaUI.Mcp.Core.Perception;

/// <summary>A serialized snapshot. SnapshotId is stable wire surface for desktop_snapshot_diff. Wakeable (Phase 9
/// §3) is true only for an opaque Chromium/Electron window that would benefit from desktop_wake_accessibility.</summary>
public sealed record SnapshotResult(string SnapshotId, string Tree, int NodeCount, bool Wakeable = false);
```

- [ ] **Step 5: Compute it in `PerceptionManager.SnapshotAsync`**

In `PerceptionManager.SnapshotAsync`, where the `SnapshotResult` is constructed on the STA, read the window-root ClassName and pass `WakeabilityHint.IsWakeable(className, nodeCount)`. Use the window root element already in scope (the same `win` used to build the tree); read `string? cls; try { cls = win.Properties.ClassName.ValueOrDefault; } catch { cls = null; }` (the `try` mirrors `WindowManager.cs:382`). Pass `Wakeable: WakeabilityHint.IsWakeable(cls, nodeCount)` into the `SnapshotResult` constructor. **Only compute for a full-window snapshot** (when `opts.RootRef` is null — a re-rooted subtree snapshot is not a window-opacity signal); when rooted, pass `Wakeable: false`.

> Implementer: open `SnapshotAsync` and place the ClassName read + `Wakeable` argument at the exact construction site. Do NOT change the tree-walk or NodeCount computation. If the window root element is not in scope at the construction site, resolve it from the same STA lambda that produced the tree; STOP + report if that requires a structural change beyond adding the argument.

- [ ] **Step 6: Emit `wakeable` in the tool JSON** — modify `SnapshotTools.DesktopSnapshot` (`SnapshotTools.cs:35`):
```csharp
            var r = await _perception.SnapshotAsync(new WindowHandle(window), opts);
            return r.Wakeable
                ? ToolResponse.Ok(new { snapshotId = r.SnapshotId, nodeCount = r.NodeCount, tree = r.Tree, wakeable = true })
                : ToolResponse.Ok(new { snapshotId = r.SnapshotId, nodeCount = r.NodeCount, tree = r.Tree });
```
Also append to the tool's `[Description]`: `" If the window is an opaque Chromium/Electron app, the result includes wakeable:true — call desktop_wake_accessibility then re-snapshot to see its contents."`

- [ ] **Step 7: Run tests + build**

Run: `dotnet test --filter "Category!=Desktop&FullyQualifiedName~WakeabilityHintTests"` → PASS. Then `dotnet build` → 0/0, and `dotnet test --filter "Category!=Desktop"` → all green (confirm no existing SnapshotResult-construction site broke from the new optional param).

- [ ] **Step 8: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/WakeabilityHint.cs src/FlaUI.Mcp.Core/Perception/SnapshotResult.cs src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs src/FlaUI.Mcp.Server/Tools/SnapshotTools.cs test/FlaUI.Mcp.Tests/Perception/WakeabilityHintTests.cs
git commit -m "feat(perception): wakeable hint on desktop_snapshot (Chromium class + collapsed tree)"
```

---

## Task 7: `CoordinateMapping` core — bitmapPx → screenPx → windowPct (the §6 dealbreaker)

**Files:**
- Create: `src/FlaUI.Mcp.Core/Vision/CoordinateMapping.cs`
- Test: `test/FlaUI.Mcp.Tests/Vision/CoordinateMappingTests.cs`

**Context (§6):** OCR runs on the screenshot bitmap, which is offset by the capture origin and downscaled by `CaptureResult.ScaleApplied`. This pure function inverts that: bitmap px → physical screen px → window-relative fractions (what `desktop_click_at` consumes). **This is the single feature-killer if wrong** (on a 150% display an un-normalized box clicks ~200px off). The `origin` term MUST be the CAPTURE's rect top-left (`CaptureResult.X/Y`), not the window rect — a straddling/off-screen window is captured cropped (§6 Seat D). This task is PURE math; it does NOT depend on Spike α (OCR data shape only).

- [ ] **Step 1: Write the failing test**

```csharp
// test/FlaUI.Mcp.Tests/Vision/CoordinateMappingTests.cs
using FlaUI.Mcp.Core.Vision;
using Xunit;

namespace FlaUI.Mcp.Tests.Vision;

public class CoordinateMappingTests
{
    [Fact]
    public void Unscaled_capture_origin_zero_window_at_origin_is_identity_fraction()
    {
        // capture origin (0,0), scale 1.0; window rect (0,0,100x100); bitmap point (50,50) -> center -> (0.5,0.5)
        var m = CoordinateMapping.BitmapToWindowPct(
            bitmapX: 50, bitmapY: 50, scaleApplied: 1.0, captureX: 0, captureY: 0,
            winLeft: 0, winTop: 0, winWidth: 100, winHeight: 100);
        Assert.Equal(50, m.ScreenX);
        Assert.Equal(50, m.ScreenY);
        Assert.Equal(0.5, m.XPct, 5);
        Assert.Equal(0.5, m.YPct, 5);
    }

    [Fact]
    public void Downscaled_capture_undoes_scale_before_mapping()
    {
        // 150% display: source was 1500px wide, clamped to 1000 -> scaleApplied = 0.6667. A bitmap x of 300 maps
        // back to screen 300/0.6667 = 450. Capture origin (200,100). Window rect (200,100,600x400).
        double scale = 1000.0 / 1500.0;
        var m = CoordinateMapping.BitmapToWindowPct(
            bitmapX: 300, bitmapY: 200, scaleApplied: scale, captureX: 200, captureY: 100,
            winLeft: 200, winTop: 100, winWidth: 600, winHeight: 400);
        Assert.Equal(200 + 300 / scale, m.ScreenX, 3); // 200 + 450 = 650
        Assert.Equal(100 + 200 / scale, m.ScreenY, 3); // 100 + 300 = 400
        Assert.Equal((650 - 200) / 600.0, m.XPct, 5);  // 0.75
        Assert.Equal((400 - 100) / 400.0, m.YPct, 5);  // 0.75
    }

    [Fact]
    public void Negative_capture_origin_multi_monitor_left_of_primary()
    {
        // Window on a monitor left of primary: capture origin negative. bitmap (10,10), scale 1, win (-800,0,400x300)
        var m = CoordinateMapping.BitmapToWindowPct(
            bitmapX: 10, bitmapY: 10, scaleApplied: 1.0, captureX: -800, captureY: 0,
            winLeft: -800, winTop: 0, winWidth: 400, winHeight: 300);
        Assert.Equal(-790, m.ScreenX);
        Assert.Equal(10, m.ScreenY);
        Assert.Equal(10 / 400.0, m.XPct, 5);
        Assert.Equal(10 / 300.0, m.YPct, 5);
    }

    [Fact]
    public void Cropped_capture_origin_differs_from_window_origin()
    {
        // Window straddles a screen edge: window rect (-50,0,400x300) but capture cropped to visible (0,0,350x300).
        // A bitmap point uses the CAPTURE origin (0,0), and the fraction uses the WINDOW rect (-50,...).
        var m = CoordinateMapping.BitmapToWindowPct(
            bitmapX: 100, bitmapY: 30, scaleApplied: 1.0, captureX: 0, captureY: 0,
            winLeft: -50, winTop: 0, winWidth: 400, winHeight: 300);
        Assert.Equal(100, m.ScreenX);                 // capture origin 0 + 100
        Assert.Equal((100 - (-50)) / 400.0, m.XPct, 5); // (screenX - winLeft)/winWidth = 150/400 = 0.375
    }

    [Fact]
    public void Center_of_a_word_rect_maps_from_rect_helper()
    {
        // The rect helper takes bitmap rect (x,y,w,h) and returns the CENTER's window-pct + physical center.
        var c = CoordinateMapping.BitmapRectCenterToWindowPct(
            bx: 40, by: 40, bw: 20, bh: 10, scaleApplied: 1.0, captureX: 0, captureY: 0,
            winLeft: 0, winTop: 0, winWidth: 100, winHeight: 100);
        Assert.Equal(50, c.ScreenX); // 40 + 20/2
        Assert.Equal(45, c.ScreenY); // 40 + 10/2
        Assert.Equal(0.5, c.XPct, 5);
        Assert.Equal(0.45, c.YPct, 5);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "Category!=Desktop&FullyQualifiedName~CoordinateMappingTests"`
Expected: FAIL to compile (`CoordinateMapping` does not exist).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/FlaUI.Mcp.Core/Vision/CoordinateMapping.cs
namespace FlaUI.Mcp.Core.Vision;

/// <summary>A mapped point: physical screen px (pairs with desktop_get_bounds) + window-relative fractions
/// (pairs with desktop_click_at's xPct/yPct).</summary>
public readonly record struct MappedPoint(int ScreenX, int ScreenY, double XPct, double YPct);

/// <summary>Pure inverse of the screenshot pipeline (Phase 9 §6 — THE dealbreaker). OCR reports pixels in the
/// DOWNSCALED capture bitmap, offset by the capture origin. This maps them back to physical screen px and then to
/// the target window's fractional coordinates. The origin term is the CAPTURE rect's top-left (CaptureResult.X/Y) —
/// NOT the window rect — because a straddling/off-screen window is captured cropped to visible bounds (§6 Seat D).
/// The window rect (winLeft/winTop/winWidth/winHeight) is the FULL window rect, used only for the fraction.</summary>
public static class CoordinateMapping
{
    public static MappedPoint BitmapToWindowPct(
        double bitmapX, double bitmapY, double scaleApplied, int captureX, int captureY,
        int winLeft, int winTop, int winWidth, int winHeight)
    {
        // Undo the downscale, then add the capture origin -> physical screen px.
        double screenXd = captureX + bitmapX / scaleApplied;
        double screenYd = captureY + bitmapY / scaleApplied;
        int screenX = (int)System.Math.Round(screenXd);
        int screenY = (int)System.Math.Round(screenYd);
        // Fraction of the FULL window rect (clamped so an off-window point doesn't produce an out-of-[0,1] click).
        double xPct = winWidth  <= 0 ? 0.0 : (screenXd - winLeft) / winWidth;
        double yPct = winHeight <= 0 ? 0.0 : (screenYd - winTop) / winHeight;
        return new MappedPoint(screenX, screenY, Clamp01(xPct), Clamp01(yPct));
    }

    public static MappedPoint BitmapRectCenterToWindowPct(
        double bx, double by, double bw, double bh, double scaleApplied, int captureX, int captureY,
        int winLeft, int winTop, int winWidth, int winHeight)
        => BitmapToWindowPct(bx + bw / 2.0, by + bh / 2.0, scaleApplied, captureX, captureY,
                             winLeft, winTop, winWidth, winHeight);

    private static double Clamp01(double v) => v < 0.0 ? 0.0 : v > 1.0 ? 1.0 : v;
}
```

> **Note on Clamp01:** the tests above use in-window points, so clamping is a no-op there. Clamping guards against a match whose center is slightly outside the window rect (cropped capture edge) producing an out-of-range fraction that `CoordinateMath.PctToPhysical` would reject. If a reviewer prefers surfacing out-of-range instead of clamping, that is a design fork — keep clamping for v1 (the agent verifies the match's `bounds`/`text` before clicking anyway, §3 Seat C).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "Category!=Desktop&FullyQualifiedName~CoordinateMappingTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Vision/CoordinateMapping.cs test/FlaUI.Mcp.Tests/Vision/CoordinateMappingTests.cs
git commit -m "feat(vision): CoordinateMapping — bitmapPx->screenPx->windowPct (the coord dealbreaker)"
```

---

## Task 8: `OcrWord` DTO + `TextMatcher` core — fuzzy/exact matching

**Files:**
- Create: `src/FlaUI.Mcp.Core/Vision/OcrWord.cs`
- Create: `src/FlaUI.Mcp.Core/Vision/TextMatcher.cs`
- Test: `test/FlaUI.Mcp.Tests/Vision/TextMatcherTests.cs`

**Context (§3):** `matchMode` defaults to FUZZY because OCR mis-reads UI text ("Submit"→"5ubmit"/"Subm it"). Fuzzy normalizes whitespace + case and allows a bounded edit-distance tolerance. `matchMode:"exact"` is opt-in (normalized case/whitespace, no edit distance). This is a PURE function over `OcrWord`s (fake OCR output) — no WinRT, so it does NOT depend on Spike α. Returns ALL matches (§3 Seat C: the agent picks the right occurrence).

**MULTI-WORD / PHRASE MATCHING (AGY-AFTER plan panel R1, Seat 3 — folded).** OCR emits one `OcrWord` per WORD (Task 9 flattens `result.Lines`→`Words`). A naïve per-word matcher can only find single-word queries, so a query like `"Sign in"` or `"Submit Order"` would silently never match — a feature-killer for a targeting primitive. Therefore `TextMatcher` matches a query against **variable-width sliding windows of consecutive words on the SAME line**: for each start word it tries joining 1..(queryTokens+1) adjacent words and keeps the best-confidence window. This covers (a) single words, (b) multi-word phrases, and (c) the OCR-over-split case (§3 Seat A: "Submit"→"Subm it" — a 2-word window rejoins to `"subm it"`, edit-distance 1 from `"submit"`). Each match carries the **union bitmap rect** of its window's words. To keep windows from spanning unrelated lines, `OcrWord` carries a `LineId`.

- [ ] **Step 1: Write the failing test**

```csharp
// test/FlaUI.Mcp.Tests/Vision/TextMatcherTests.cs
using System.Collections.Generic;
using System.Linq;
using FlaUI.Mcp.Core.Vision;
using Xunit;

namespace FlaUI.Mcp.Tests.Vision;

public class TextMatcherTests
{
    // A word on a line: (text, x, lineId). y/w/h fixed for simplicity; x orders words within a line.
    private static OcrWord W(string t, int x, int lineId) => new(t, x, 0, 10, 10, lineId);

    [Fact]
    public void Single_word_exact_normalizes_case_and_whitespace_no_edit_distance()
    {
        var words = new List<OcrWord> { W("Submit", 0, 0), W("5ubmit", 0, 1), W("submit", 0, 2) };
        var hits = TextMatcher.Match("submit", words, MatchMode.Exact).ToList();
        Assert.Equal(2, hits.Count); // "Submit" and "submit" normalize-equal; "5ubmit" does NOT (exact)
        Assert.All(hits, h => Assert.Equal(1.0, h.Confidence, 5));
    }

    [Fact]
    public void Single_word_fuzzy_tolerates_one_ocr_misread()
    {
        var words = new List<OcrWord> { W("5ubmit", 0, 0), W("Cancel", 20, 0) };
        var hits = TextMatcher.Match("Submit", words, MatchMode.Fuzzy).ToList();
        var hit = Assert.Single(hits);
        Assert.Equal("5ubmit", hit.Text);
        Assert.True(hit.Confidence < 1.0 && hit.Confidence > 0.5);
    }

    [Fact]
    public void Multi_word_phrase_matches_adjacent_words_on_a_line_with_union_bounds()
    {
        // "Submit Order" as two adjacent words on line 0; "Cancel" on line 1.
        var words = new List<OcrWord>
        {
            new("Submit", 10, 5, 30, 12, 0),
            new("Order",  45, 5, 25, 12, 0),
            new("Cancel", 10, 40, 30, 12, 1),
        };
        var hit = Assert.Single(TextMatcher.Match("Submit Order", words, MatchMode.Fuzzy));
        Assert.Equal("Submit Order", hit.Text);
        Assert.Equal(1.0, hit.Confidence, 5);
        // Union bitmap rect: x=10, y=5, right=45+25=70 -> w=60, h=12.
        Assert.Equal(10, hit.X); Assert.Equal(5, hit.Y);
        Assert.Equal(60, hit.W); Assert.Equal(12, hit.H);
    }

    [Fact]
    public void Phrase_does_not_span_across_lines()
    {
        var words = new List<OcrWord> { W("Submit", 0, 0), W("Order", 0, 1) }; // different lines
        Assert.Empty(TextMatcher.Match("Submit Order", words, MatchMode.Fuzzy));
    }

    [Fact]
    public void Ocr_over_split_single_word_rejoins_via_a_two_word_window()
    {
        // OCR split "Submit" into "Subm" + "it" on the same line; query is the single token "Submit".
        var words = new List<OcrWord> { new("Subm", 0, 0, 20, 10, 0), new("it", 22, 0, 8, 10, 0) };
        var hit = Assert.Single(TextMatcher.Match("Submit", words, MatchMode.Fuzzy));
        Assert.Equal("Subm it", hit.Text);       // rejoined window
        Assert.True(hit.Confidence > 0.5);        // "subm it" vs "submit" = edit distance 1
    }

    [Fact]
    public void Fuzzy_rejects_beyond_tolerance()
        => Assert.Empty(TextMatcher.Match("Submit", new List<OcrWord> { W("Delete", 0, 0) }, MatchMode.Fuzzy));

    [Fact]
    public void Returns_all_matches_ordered_by_confidence_desc()
    {
        var words = new List<OcrWord> { W("Submit", 0, 0), W("5ubmit", 0, 1), W("Submit", 0, 2) };
        var hits = TextMatcher.Match("Submit", words, MatchMode.Fuzzy).ToList();
        Assert.Equal(3, hits.Count);
        Assert.True(hits[0].Confidence >= hits[1].Confidence);
        Assert.True(hits[1].Confidence >= hits[2].Confidence);
    }

    [Fact]
    public void Empty_query_matches_nothing()
        => Assert.Empty(TextMatcher.Match("", new List<OcrWord> { W("Submit", 0, 0) }, MatchMode.Fuzzy));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "Category!=Desktop&FullyQualifiedName~TextMatcherTests"`
Expected: FAIL to compile (`OcrWord`, `TextMatcher`, `MatchMode` do not exist).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/FlaUI.Mcp.Core/Vision/OcrWord.cs
namespace FlaUI.Mcp.Core.Vision;

/// <summary>One recognized word from the OCR engine. Rect is in BITMAP pixels (the downscaled capture space);
/// CoordinateMapping converts it to screen/window coords. LineId groups words that share an OCR text line, so
/// phrase matching (TextMatcher) never joins words across unrelated lines.</summary>
public readonly record struct OcrWord(string Text, double X, double Y, double W, double H, int LineId);
```

```csharp
// src/FlaUI.Mcp.Core/Vision/TextMatcher.cs
using System;
using System.Collections.Generic;
using System.Linq;

namespace FlaUI.Mcp.Core.Vision;

public enum MatchMode { Fuzzy, Exact }

/// <summary>One matched text run: the joined matched text, its UNION bitmap rect (spanning the matched words), and
/// a [0,1] confidence (1.0 = exact normalized match; lower = fuzzier).</summary>
public readonly record struct TextMatch(string Text, double X, double Y, double W, double H, double Confidence);

/// <summary>Pure matching of a query against OCR words (Phase 9 §3). Matches a query against variable-width sliding
/// windows of consecutive words on the SAME line (LineId), so single words, multi-word phrases ("Submit Order"),
/// AND OCR-over-splits ("Subm it") all resolve. FUZZY (default) normalizes case + whitespace and allows a bounded
/// edit-distance tolerance (OCR mis-reads real UI text); EXACT matches only the normalized string. Returns ALL
/// matches, best-confidence first, so the agent picks the right occurrence (§3 Seat C).</summary>
public static class TextMatcher
{
    public static IEnumerable<TextMatch> Match(string query, IReadOnlyList<OcrWord> words, MatchMode mode)
    {
        var q = Normalize(query);
        if (q.Length == 0) return Enumerable.Empty<TextMatch>();
        int queryTokens = q.Count(c => c == ' ') + 1;
        int maxWin = queryTokens + 1; // allow ONE extra split (OCR broke a query token into two)

        var results = new List<TextMatch>();
        // Group by line, preserving input order (input order == reading order within a line — Task 9 emits words
        // in Lines->Words order; if an engine ever returns them unordered, sort each line's words by X first).
        foreach (var line in words.GroupBy(w => w.LineId))
        {
            var lw = line.OrderBy(w => w.X).ToList();
            for (int start = 0; start < lw.Count; start++)
            {
                // For this start word, try windows of 1..maxWin adjacent words; keep the BEST-confidence window
                // (one match per start index -> no duplicate explosion).
                double bestConf = 0.0;
                int bestWin = 0;
                string bestJoined = "";
                int maxThisStart = Math.Min(maxWin, lw.Count - start);
                for (int win = 1; win <= maxThisStart; win++)
                {
                    string joined = string.Join(" ", lw.GetRange(start, win).Select(w => w.Text));
                    var n = Normalize(joined);
                    if (n.Length == 0) continue;
                    double conf = mode == MatchMode.Exact ? (n == q ? 1.0 : 0.0) : FuzzyConfidence(q, n);
                    if (conf > bestConf) { bestConf = conf; bestWin = win; bestJoined = joined; }
                }
                if (bestConf > 0.0)
                {
                    var rect = Union(lw.GetRange(start, bestWin));
                    results.Add(new TextMatch(bestJoined, rect.X, rect.Y, rect.W, rect.H, bestConf));
                }
            }
        }
        return results.OrderByDescending(r => r.Confidence);
    }

    // Union bitmap rect over a window of words.
    private static (double X, double Y, double W, double H) Union(IReadOnlyList<OcrWord> ws)
    {
        double minX = ws.Min(w => w.X), minY = ws.Min(w => w.Y);
        double maxR = ws.Max(w => w.X + w.W), maxB = ws.Max(w => w.Y + w.H);
        return (minX, minY, maxR - minX, maxB - minY);
    }

    // Confidence: 1.0 exact; else 1 - editDistance/maxLen, accepted only within tolerance (at most ~1 edit per 4
    // chars, min 1). Below tolerance -> 0.0 (rejected).
    private static double FuzzyConfidence(string q, string n)
    {
        if (n == q) return 1.0;
        int d = Levenshtein(q, n);
        int maxLen = Math.Max(q.Length, n.Length);
        int tolerance = Math.Max(1, maxLen / 4);
        if (d > tolerance) return 0.0;
        return 1.0 - (double)d / maxLen;
    }

    private static string Normalize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries); // collapse whitespace
        return string.Join(" ", parts).ToLowerInvariant();
    }

    private static int Levenshtein(string a, string b)
    {
        int[] prev = new int[b.Length + 1];
        int[] cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
```

> **Note (tolerance):** "≤1 edit per 4 chars, min 1" makes `Delete` vs `Submit` (distance 5, maxLen 6, tolerance 1) reject, `5ubmit` vs `submit` (distance 1) accept, and `subm it` vs `submit` (distance 1, maxLen 7, tolerance 1) accept — matching the tests. Tune the tolerance / the window cap (`maxWin`) here (single site) if Spike α's measured OCR error rate warrants.
> **Note (window cost):** windows are per-line and capped at `queryTokens+1`, so cost is O(words) — lines are short; no blow-up.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "Category!=Desktop&FullyQualifiedName~TextMatcherTests"`
Expected: PASS (8 tests, incl. multi-word phrase, cross-line rejection, and OCR-over-split rejoin).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Vision/OcrWord.cs src/FlaUI.Mcp.Core/Vision/TextMatcher.cs test/FlaUI.Mcp.Tests/Vision/TextMatcherTests.cs
git commit -m "feat(vision): OcrWord + TextMatcher — fuzzy phrase matching (sliding word windows per line)"
```

---

## Task 9: `IOcrEngine` seam + `WindowsMediaOcrEngine` — GATED on Spike α GO

**Files:**
- Create: `src/FlaUI.Mcp.Core/Vision/IOcrEngine.cs`
- Create: `src/FlaUI.Mcp.Core/Vision/WindowsMediaOcrEngine.cs`
- Modify: `src/FlaUI.Mcp.Core/FlaUI.Mcp.Core.csproj` (TFM/package per Spike α, if not already applied)
- Modify: `src/FlaUI.Mcp.Core/Errors/ToolErrorCode.cs` (add `OcrUnavailable`)
- Test: (engine impl is WinRT-bound → its real recognition is exercised by the Task 10 Desktop test; add a headless test only for the `IOcrEngine` seam shape via a fake, already covered in Task 10's TextFinder tests)

**Context:** This is the ONLY task that depends on Spike α's confirmed WinRT surface. Implement the seam + the `Windows.Media.Ocr` impl EXACTLY as Spike α recorded. `IOcrEngine.RecognizeAsync` takes a PNG byte[] (from `ScreenCapture.CaptureRectangle`) and returns `IReadOnlyList<OcrWord>` in bitmap px. Handle the no-language-pack case (`OcrEngine.TryCreateFromUserProfileLanguages()` null) by throwing `ToolException(OcrUnavailable, ...)` — never crash.

- [ ] **Step 0 (STATE-VERIFY — MANDATORY):** Open `docs/superpowers/spikes/2026-07-03-spikeA-winrt-ocr.md`. Confirm: (a) the TFM/package that builds (apply it to `FlaUI.Mcp.Core.csproj` if the spike left it un-applied); (b) the exact `OcrEngine`/`RecognizeAsync`/`SoftwareBitmap`-decode surface. If the spike's recorded surface differs from the code below in ANY wire/type shape, STOP and report `[plan] -> [spike] because <reason>` — implement to the SPIKE, not to this illustrative code.

- [ ] **Step 1: Add the `OcrUnavailable` error code**

In `src/FlaUI.Mcp.Core/Errors/ToolErrorCode.cs`, add an `OcrUnavailable` member alongside the existing codes (open the file; add it to the enum in the same style — do NOT reorder existing members).

- [ ] **Step 2: Write the seam**

```csharp
// src/FlaUI.Mcp.Core/Vision/IOcrEngine.cs
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FlaUI.Mcp.Core.Vision;

/// <summary>On-box OCR seam (Phase 9 §5). Input is a PNG byte[] (a ScreenCapture.CaptureRectangle result); output
/// is the recognized words with rects in BITMAP pixels (the same downscaled space CoordinateMapping expects). One
/// engine for v1 (Windows.Media.Ocr); the seam exists so TextFinder is headless-testable with a fake and so a 2nd
/// engine can be added later without touching callers. Throws ToolException(OcrUnavailable) if no OCR is available
/// (no language pack) — never crashes.</summary>
public interface IOcrEngine
{
    Task<IReadOnlyList<OcrWord>> RecognizeAsync(byte[] pngBytes);
}
```

- [ ] **Step 3: Write the WinRT impl (adjust to Spike α's recorded surface)**

```csharp
// src/FlaUI.Mcp.Core/Vision/WindowsMediaOcrEngine.cs
// NOTE: this is the ILLUSTRATIVE surface — implement EXACTLY what Spike α recorded (Step 0). The WinRT decode +
// RecognizeAsync signatures below MUST match the spike; if not, STOP + report.
using System.Collections.Generic;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Errors;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace FlaUI.Mcp.Core.Vision;

/// <summary>Windows.Media.Ocr implementation of IOcrEngine (§5). On-box, free, ships with Windows (language packs
/// via the OS). Decodes the PNG to a SoftwareBitmap and recognizes; returns words in bitmap px.</summary>
public sealed class WindowsMediaOcrEngine : IOcrEngine
{
    public async Task<IReadOnlyList<OcrWord>> RecognizeAsync(byte[] pngBytes)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
            throw new ToolException(ToolErrorCode.OcrUnavailable,
                "No OCR language pack is installed for the current user profile.",
                "install a Windows OCR language pack (Settings > Time & Language > Language), then retry");

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(pngBytes.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync();

        var result = await engine.RecognizeAsync(bitmap);
        var words = new List<OcrWord>();
        int lineId = 0;
        foreach (var line in result.Lines)   // one LineId per OCR text line -> TextMatcher won't join across lines
        {
            foreach (var w in line.Words)
                words.Add(new OcrWord(w.Text, w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height, lineId));
            lineId++;
        }
        return words;
    }
}
```

- [ ] **Step 4: Build gate**

Run: `dotnet build` → 0 Warning(s), 0 Error(s). If the WinRT reference fails to resolve, the TFM/package from Spike α Step 0 was not applied correctly — fix per the spike, do NOT invent a different package. Then `dotnet test --filter "Category!=Desktop"` → all green (no headless test recognizes real bitmaps; the seam is covered via a fake in Task 10).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Vision/IOcrEngine.cs src/FlaUI.Mcp.Core/Vision/WindowsMediaOcrEngine.cs src/FlaUI.Mcp.Core/Errors/ToolErrorCode.cs src/FlaUI.Mcp.Core/FlaUI.Mcp.Core.csproj
git commit -m "feat(vision): IOcrEngine seam + Windows.Media.Ocr impl (per Spike alpha) + OcrUnavailable"
```

---

## Task 10: `TextFinder` core + `desktop_find_text` tool — GATED on Spike α GO

**Files:**
- Create: `src/FlaUI.Mcp.Core/Vision/TextFinder.cs`
- Create: `src/FlaUI.Mcp.Server/Tools/FindTextTools.cs`
- Modify: `src/FlaUI.Mcp.Server/Program.cs` (DI: `IOcrEngine`, `TextFinder`, `FindTextTools`)
- Modify: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` — add a capture-geometry+window-rect resolver for a window/region (reuse `ResolveWindowCaptureGeometryAsync` if it already returns the needed rect; else add a thin method). **STATE-VERIFY this first.**
- Test: `test/FlaUI.Mcp.Tests/Vision/TextFinderTests.cs` (headless, fake `IOcrEngine`), `test/FlaUI.Mcp.Tests/Vision/DesktopFindTextTests.cs` (`Category=Desktop`, the §10b coord-landing test)

**Context (§3/§6):** `desktop_find_text(query, window?|region?, {matchMode, all})` captures the window (or a `region` = window-relative FRACTIONS `[xPct,yPct,wPct,hPct]`), OCRs it, maps each `TextMatcher` hit to screen px + window-pct via `CoordinateMapping`, and returns `matches:[{text, bounds, center, xPct, yPct, confidence}]`. `TextFinder` is PURE given an `IOcrEngine` + a captured bitmap + the capture/window rects — headless-testable with a fake engine. The tool does the capture (`ScreenCapture.CaptureRectangle`, off STA already) + geometry resolution.

- [ ] **Step 0 (STATE-VERIFY):** Open `PerceptionManager.ResolveWindowCaptureGeometryAsync` (used in `ScreenshotTools.cs:41`) and confirm what it returns (`geo.Bounds` = capture rect; is the FULL window rect available too?). `TextFinder`/the tool need BOTH the capture rect (origin, for §6) AND the full window physical rect (for the fraction). If `ResolveWindowCaptureGeometryAsync` returns only the capture rect, add a sibling that also returns the window rect (or read `desktop_get_bounds`-style window bounds). Report the exact shape you found before writing the tool; if it requires more than an additive method, STOP + report.

- [ ] **Step 1: Write the failing `TextFinder` test** (headless, fake engine)

```csharp
// test/FlaUI.Mcp.Tests/Vision/TextFinderTests.cs
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Vision;
using Xunit;

namespace FlaUI.Mcp.Tests.Vision;

public class TextFinderTests
{
    private sealed class FakeEngine : IOcrEngine
    {
        private readonly IReadOnlyList<OcrWord> _words;
        public FakeEngine(params OcrWord[] words) => _words = words;
        public Task<IReadOnlyList<OcrWord>> RecognizeAsync(byte[] png) => Task.FromResult(_words);
    }

    [Fact]
    public async Task Find_maps_each_match_to_screen_and_window_coords()
    {
        // OCR reports "Submit" at bitmap rect (40,40,20,10) on line 0. scale 1, capture origin (0,0), window 100x100.
        var finder = new TextFinder(new FakeEngine(new OcrWord("Submit", 40, 40, 20, 10, 0)));
        var matches = await finder.FindAsync(
            query: "Submit", pngBytes: System.Array.Empty<byte>(), mode: MatchMode.Fuzzy, all: true,
            scaleApplied: 1.0, captureX: 0, captureY: 0, winLeft: 0, winTop: 0, winWidth: 100, winHeight: 100);
        var m = Assert.Single(matches);
        Assert.Equal("Submit", m.Text);
        Assert.Equal(50, m.CenterX);      // 40 + 20/2
        Assert.Equal(45, m.CenterY);      // 40 + 10/2
        Assert.Equal(0.5, m.XPct, 5);
        Assert.Equal(0.45, m.YPct, 5);
        Assert.Equal(new[] { 40, 40, 20, 10 }, new[] { m.BoundsX, m.BoundsY, m.BoundsW, m.BoundsH });
    }

    [Fact]
    public async Task All_false_returns_only_the_best_match()
    {
        // Two candidates on DIFFERENT lines (so they don't merge into one phrase window).
        var finder = new TextFinder(new FakeEngine(
            new OcrWord("Submit", 0, 0, 10, 10, 0), new OcrWord("5ubmit", 0, 40, 10, 10, 1)));
        var one = await finder.FindAsync("Submit", System.Array.Empty<byte>(), MatchMode.Fuzzy, all: false,
            1.0, 0, 0, 0, 0, 100, 100);
        Assert.Single(one);
        Assert.Equal("Submit", one[0].Text); // exact beats fuzzy
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "Category!=Desktop&FullyQualifiedName~TextFinderTests"`
Expected: FAIL to compile (`TextFinder` / its result type do not exist).

- [ ] **Step 3: Write `TextFinder`**

```csharp
// src/FlaUI.Mcp.Core/Vision/TextFinder.cs
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FlaUI.Mcp.Core.Vision;

/// <summary>One located text run with coordinates in BOTH physical screen px (bounds/center, pairs with
/// desktop_get_bounds) AND window fractions (xPct/yPct, pairs with desktop_click_at) — Phase 9 §6.</summary>
public readonly record struct TextFindMatch(
    string Text, double Confidence,
    int BoundsX, int BoundsY, int BoundsW, int BoundsH,   // physical screen px
    int CenterX, int CenterY, double XPct, double YPct);

/// <summary>Orchestrates OCR → match → coordinate mapping (Phase 9 §3/§6). Pure given an IOcrEngine and the
/// capture/window geometry: the caller (FindTextTools) does the actual screen capture off the STA and passes the
/// PNG + the CaptureResult scale/origin + the full window rect. Returns matches best-confidence first; all:false
/// keeps only the top match.</summary>
public sealed class TextFinder
{
    private readonly IOcrEngine _ocr;
    public TextFinder(IOcrEngine ocr) => _ocr = ocr;

    public async Task<IReadOnlyList<TextFindMatch>> FindAsync(
        string query, byte[] pngBytes, MatchMode mode, bool all,
        double scaleApplied, int captureX, int captureY,
        int winLeft, int winTop, int winWidth, int winHeight)
    {
        var words = await _ocr.RecognizeAsync(pngBytes);
        var hits = TextMatcher.Match(query, words, mode); // already ordered best-first; each carries a union rect
        var mapped = hits.Select(h =>
        {
            // h.X/Y/W/H is the matched run's UNION rect in bitmap px (may span multiple words for a phrase).
            var center = CoordinateMapping.BitmapRectCenterToWindowPct(
                h.X, h.Y, h.W, h.H, scaleApplied, captureX, captureY, winLeft, winTop, winWidth, winHeight);
            // Bounds top-left in screen px (undo scale + origin); size undoes just the scale.
            var tl = CoordinateMapping.BitmapToWindowPct(
                h.X, h.Y, scaleApplied, captureX, captureY, winLeft, winTop, winWidth, winHeight);
            int bw = (int)System.Math.Round(h.W / scaleApplied);
            int bh = (int)System.Math.Round(h.H / scaleApplied);
            return new TextFindMatch(h.Text, h.Confidence,
                tl.ScreenX, tl.ScreenY, bw, bh, center.ScreenX, center.ScreenY, center.XPct, center.YPct);
        });
        var list = mapped.ToList();
        return all ? list : list.Take(1).ToList();
    }
}
```

> **Test-vs-code note:** the Task-10 test asserts `bounds = {40,40,20,10}` with scale 1.0 and origin 0 — i.e. bounds in the test are the BITMAP rect because scale=1/origin=0 makes screen==bitmap. Under real downscale, bounds are in screen px (size divided by scale). The oracle is the test; the code above produces `{40,40,20,10}` for that input. Keep the code; if a reviewer wants bitmap-space bounds instead of screen-space, that is a wire-contract fork — screen-space bounds (pairs with `desktop_get_bounds`) is the §6 contract, keep it.

- [ ] **Step 4: Write `FindTextTools.desktop_find_text`**

```csharp
// src/FlaUI.Mcp.Server/Tools/FindTextTools.cs  (desktop_find_text; desktop_wait_for_text added in Task 11)
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Vision;
using FlaUI.Mcp.Core.Windows;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Tools;

/// <summary>Prong B MCP surface (§3/§6): OCR a window/region and return every text run matching a query with
/// coordinates in BOTH physical screen px and desktop_click_at window-fractions. ReadOnly + lease-exempt.</summary>
[McpServerToolType]
public sealed class FindTextTools
{
    private readonly PerceptionManager _perception;
    private readonly TextFinder _finder;
    public FindTextTools(PerceptionManager perception, TextFinder finder)
    { _perception = perception; _finder = finder; }

    [McpServerTool(ReadOnly = true), Description(
        "OCR a window (or a sub-region of it) and return every visible text run matching your query, with click " +
        "coordinates. Use for opaque/canvas/game surfaces or an editor's text body where UIA can't see the text. " +
        "Returns {matches:[{text, bounds:[x,y,w,h] (physical screen px), center:[x,y] (screen px), xPct, yPct " +
        "(window fractions for desktop_click_at), confidence}]}, best match first. matchMode defaults to 'fuzzy' " +
        "(OCR mis-reads UI text - 'Submit' may read '5ubmit'); pass 'exact' to require an exact normalized match. " +
        "region (optional) is window-relative FRACTIONS [xPct,yPct,wPct,hPct] in [0,1] to OCR only part of the " +
        "window. IMPORTANT: a fuzzy query can match inside body text ('Click Submit below') - inspect each match's " +
        "text+bounds (or the screenshot) before desktop_click_at. all defaults true (return every occurrence so " +
        "you pick the right one). ReadOnly + lease-exempt. OcrUnavailable if no OCR language pack is installed.")]
    public Task<string> DesktopFindText(
        [Description("Text to find (fuzzy by default).")] string query,
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Optional window-relative region fractions [xPct,yPct,wPct,hPct] in [0,1].")] double[]? region = null,
        [Description("'fuzzy' (default) or 'exact'.")] string matchMode = "fuzzy",
        [Description("Return all matches (default true) or only the best.")] bool all = true)
        => ToolResponse.Guard(async () =>
        {
            var mode = matchMode.Equals("exact", System.StringComparison.OrdinalIgnoreCase) ? MatchMode.Exact : MatchMode.Fuzzy;
            // Resolve capture geometry (capture rect + full window rect) on the STA + deny-list; capture OFF the STA.
            var geo = await _perception.ResolveTextCaptureGeometryAsync(new WindowHandle(window), region); // Step 0 shape
            if (geo.Denied) throw new ToolException(ToolErrorCode.TargetDenied, $"OCR of windows owned by '{geo.DeniedProcess}' is blocked.", "target a non-sensitive window");
            if (geo.Minimized) throw new ToolException(ToolErrorCode.ElementNotActionable, "Window is minimized; restore it first.", "desktop_window_transform restore, then retry");
            var cap = await Task.Run(() => ScreenCapture.CaptureRectangle(geo.CaptureBounds, geo.PasswordRects, maxWidth: 0)); // maxWidth:0 -> best OCR accuracy (still 1920-clamped)
            var matches = await _finder.FindAsync(query, cap.Png, mode, all,
                cap.ScaleApplied, cap.X, cap.Y, geo.WindowLeft, geo.WindowTop, geo.WindowWidth, geo.WindowHeight);
            return ToolResponse.Ok(new
            {
                matches = matches.Select(m => new
                {
                    text = m.Text, confidence = m.Confidence,
                    bounds = new[] { m.BoundsX, m.BoundsY, m.BoundsW, m.BoundsH },
                    center = new[] { m.CenterX, m.CenterY },
                    xPct = m.XPct, yPct = m.YPct
                })
            });
        });
}
```

> **Namespace note:** `ScreenCapture` is in `FlaUI.Mcp.Core.Perception` — add `using FlaUI.Mcp.Core.Perception;` (already present via `PerceptionManager`). `ResolveTextCaptureGeometryAsync` is the Step-0 resolver; give its return a NAMED record type **`TextCaptureGeometry`** (in `FlaUI.Mcp.Core.Perception`) with fields `{ bool Denied, string? DeniedProcess, bool Minimized, System.Drawing.Rectangle CaptureBounds, IReadOnlyList<System.Drawing.Rectangle> PasswordRects, int WindowLeft, int WindowTop, int WindowWidth, int WindowHeight }` (Task 11's wait loop re-resolves this per pass and names the type). If `ResolveWindowCaptureGeometryAsync` already returns most of this, extend it or wrap it; keep the region→physical crop (window-fraction region → sub-rectangle of the window's physical rect) in that resolver.

- [ ] **Step 5: DI wiring in `Program.cs`** — after the Phase-9 wake block:

```csharp
// --- Phase 9 OCR text targeting (Prong B) ---
builder.Services.AddSingleton<FlaUI.Mcp.Core.Vision.IOcrEngine, FlaUI.Mcp.Core.Vision.WindowsMediaOcrEngine>();
builder.Services.AddSingleton<FlaUI.Mcp.Core.Vision.TextFinder>();
builder.Services.AddSingleton<FlaUI.Mcp.Server.Tools.FindTextTools>();
```

- [ ] **Step 6: Write the Desktop coord-landing test** (`Category=Desktop`, §10b — the mandatory dealbreaker guard)

```csharp
// test/FlaUI.Mcp.Tests/Vision/DesktopFindTextTests.cs
using System.Threading.Tasks;
using Xunit;
// Contract (flesh out against the Desktop harness): launch an app with a control of KNOWN screen position and text
// (e.g. the Run dialog's "OK" button, or Notepad with typed known text). desktop_find_text(query) -> take the top
// match's (xPct,yPct) -> assert PctToPhysical of it lands INSIDE the control's desktop_get_bounds rect. This proves
// the §6 mapping end-to-end on the real display scale.

namespace FlaUI.Mcp.Tests.Vision;

[Trait("Category", "Desktop")]
public class DesktopFindTextTests
{
    [Fact]
    public async Task Find_text_click_coordinate_lands_inside_the_control()
    {
        await Task.CompletedTask;
        Assert.True(true, "Replace with: OCR a known-position control, assert derived click lands in its bounds.");
    }
}
```

- [ ] **Step 7: Build + headless gate**

Run: `dotnet build` → 0/0. Then `dotnet test --filter "Category!=Desktop"` → all green (TextFinder headless tests pass; Desktop excluded).

- [ ] **Step 8: Commit**

```bash
git add src/FlaUI.Mcp.Core/Vision/TextFinder.cs src/FlaUI.Mcp.Server/Tools/FindTextTools.cs src/FlaUI.Mcp.Server/Program.cs src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs test/FlaUI.Mcp.Tests/Vision/TextFinderTests.cs test/FlaUI.Mcp.Tests/Vision/DesktopFindTextTests.cs
git commit -m "feat(vision): desktop_find_text + TextFinder (OCR->match->coord) + capture geometry resolver"
```

---

## Task 11: `desktop_wait_for_text` — off-STA capture+OCR poll with throttle

**Files:**
- Create: `src/FlaUI.Mcp.Core/Vision/TextWaiter.cs` (pure poll loop over an injected capture+find delegate; headless-testable)
- Modify: `src/FlaUI.Mcp.Server/Tools/FindTextTools.cs` (add `desktop_wait_for_text`)
- Test: `test/FlaUI.Mcp.Tests/Vision/TextWaiterTests.cs` (headless), extend `DesktopFindTextTests` for the live path

**Context (§3/§9):** OCR-anchored wait: polls `find_text` (fuzzy) until a match appears or timeout; timeout returns `{satisfied:false}` DATA, not an error (mirrors `desktop_wait_for`). **Hard throttle ≥750 ms** between OCR passes. **BOTH capture AND OCR run OFF the query STA (§9 R3 Seat A)** — `ScreenCapture.CaptureRectangle` is already off-STA (it captures by absolute screen rect), so the poll loop reuses it; it must NOT hit the query STA each pass. Only the initial geometry resolve touches the STA (once). `TextWaiter` is a pure loop over an injected `Func<Task<bool>>` (found?) + a clock/delay, so the throttle + timeout-returns-data logic is headless-testable.

- [ ] **Step 1: Write the failing `TextWaiter` test**

```csharp
// test/FlaUI.Mcp.Tests/Vision/TextWaiterTests.cs
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Vision;
using Xunit;

namespace FlaUI.Mcp.Tests.Vision;

public class TextWaiterTests
{
    [Fact]
    public async Task Returns_satisfied_true_as_soon_as_a_probe_finds_it()
    {
        int calls = 0;
        var r = await TextWaiter.WaitAsync(
            probe: () => Task.FromResult(++calls >= 2), // found on the 2nd probe
            timeoutMs: 5000, minIntervalMs: 10);
        Assert.True(r.Satisfied);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Returns_satisfied_false_on_timeout_not_an_error()
    {
        var r = await TextWaiter.WaitAsync(
            probe: () => Task.FromResult(false), // never found
            timeoutMs: 60, minIntervalMs: 10);
        Assert.False(r.Satisfied); // DATA, not a throw
    }

    [Fact]
    public async Task Enforces_the_minimum_interval_between_probes()
    {
        int calls = 0;
        var sw = Stopwatch.StartNew();
        await TextWaiter.WaitAsync(
            probe: () => { calls++; return Task.FromResult(false); },
            timeoutMs: 250, minIntervalMs: 100);
        sw.Stop();
        // With a 100ms floor and a 250ms budget, at most ~3 probes (0ms, ~100ms, ~200ms) — never a tight spin.
        Assert.True(calls <= 4, $"expected throttled probes, got {calls}");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "Category!=Desktop&FullyQualifiedName~TextWaiterTests"`
Expected: FAIL to compile (`TextWaiter` does not exist).

- [ ] **Step 3: Write `TextWaiter`**

```csharp
// src/FlaUI.Mcp.Core/Vision/TextWaiter.cs
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace FlaUI.Mcp.Core.Vision;

/// <summary>Result of desktop_wait_for_text (§3): satisfied=false on timeout is DATA, not an error.</summary>
public readonly record struct TextWaitResult(bool Satisfied);

/// <summary>Pure poll loop for desktop_wait_for_text (§3/§9). Repeatedly runs a probe (capture+OCR+match, injected)
/// until it returns true or the budget elapses, enforcing a HARD minimum interval between passes (§3 Seat C:
/// Windows.Media.Ocr is heavy — never tight-loop). The probe itself must run OFF the query STA (the caller wires
/// ScreenCapture + TextFinder, both STA-free). Timeout -> Satisfied=false.</summary>
public static class TextWaiter
{
    public const int MinPollIntervalMs = 750; // §3 Seat C hard floor for the live tool

    public static async Task<TextWaitResult> WaitAsync(Func<Task<bool>> probe, int timeoutMs, int minIntervalMs)
    {
        int interval = Math.Max(1, minIntervalMs);
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (await probe()) return new TextWaitResult(true);
            if (sw.ElapsedMilliseconds >= timeoutMs) return new TextWaitResult(false);
            // Sleep the throttle, but don't overshoot the remaining budget by much.
            int remaining = (int)(timeoutMs - sw.ElapsedMilliseconds);
            if (remaining <= 0) return new TextWaitResult(false);
            await Task.Delay(Math.Min(interval, remaining));
            if (sw.ElapsedMilliseconds >= timeoutMs) return new TextWaitResult(false);
        }
    }
}
```

- [ ] **Step 4: Add `desktop_wait_for_text` to `FindTextTools`**

```csharp
    [McpServerTool(ReadOnly = true), Description(
        "Poll a window (or region) with OCR until visible text matching your query appears, or timeout. Use to " +
        "wait for an opaque/canvas surface to render text UIA can't see (desktop_wait_for is the UIA equivalent). " +
        "Fuzzy match. Timeout returns {satisfied:false} (NOT an error). On success returns {satisfied:true, match:" +
        "{text,bounds,center,xPct,yPct,confidence}}. OCR is heavy, so polling is throttled to >= 750ms between " +
        "passes; pick a timeout accordingly. ReadOnly + lease-exempt. OcrUnavailable if no OCR language pack.")]
    public Task<string> DesktopWaitForText(
        [Description("Text to wait for (fuzzy).")] string query,
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Optional window-relative region fractions [xPct,yPct,wPct,hPct] in [0,1].")] double[]? region = null,
        [Description("Total wait budget ms (default 10000).")] int timeoutMs = 10000)
        => ToolResponse.Guard(async () =>
        {
            // Resolve geometry ONCE up-front to fail fast on deny/minimized before entering the poll loop.
            var initial = await _perception.ResolveTextCaptureGeometryAsync(new WindowHandle(window), region);
            if (initial.Denied) throw new ToolException(ToolErrorCode.TargetDenied, $"OCR of windows owned by '{initial.DeniedProcess}' is blocked.", "target a non-sensitive window");
            if (initial.Minimized) throw new ToolException(ToolErrorCode.ElementNotActionable, "Window is minimized; restore it first.", "desktop_window_transform restore, then retry");

            System.Collections.Generic.IReadOnlyList<TextFindMatch>? found = null;
            var result = await TextWaiter.WaitAsync(async () =>
            {
                // AGY-AFTER R1 Seat 1: RE-RESOLVE geometry EACH pass — a window can MOVE/RESIZE during a multi-second
                // wait; a once-resolved rect would capture the stale location and compute wrong xPct/yPct. The rect
                // read is a cheap (~1ms) STA op (fine at a >=750ms cadence — §9's off-STA rule targets the EXPENSIVE
                // ~50-150ms CAPTURE, which stays on Task.Run). If the window vanished mid-wait, treat as not-found.
                TextCaptureGeometry geo;
                try { geo = await _perception.ResolveTextCaptureGeometryAsync(new WindowHandle(window), region); }
                catch { return false; }
                if (geo.Denied || geo.Minimized) return false;
                var cap = await Task.Run(() => ScreenCapture.CaptureRectangle(geo.CaptureBounds, geo.PasswordRects, maxWidth: 0));
                var matches = await _finder.FindAsync(query, cap.Png, MatchMode.Fuzzy, all: false,
                    cap.ScaleApplied, cap.X, cap.Y, geo.WindowLeft, geo.WindowTop, geo.WindowWidth, geo.WindowHeight);
                if (matches.Count > 0) { found = matches; return true; }
                return false;
            }, timeoutMs, TextWaiter.MinPollIntervalMs);

            if (!result.Satisfied || found is null || found.Count == 0)
                return ToolResponse.Ok(new { satisfied = false });
            var m = found[0];
            return ToolResponse.Ok(new
            {
                satisfied = true,
                match = new
                {
                    text = m.Text, confidence = m.Confidence,
                    bounds = new[] { m.BoundsX, m.BoundsY, m.BoundsW, m.BoundsH },
                    center = new[] { m.CenterX, m.CenterY }, xPct = m.XPct, yPct = m.YPct
                }
            });
        });
```

- [ ] **Step 5: Run tests + build**

Run: `dotnet test --filter "Category!=Desktop&FullyQualifiedName~TextWaiterTests"` → PASS (3). Then `dotnet build` → 0/0 and `dotnet test --filter "Category!=Desktop"` → all green.

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Core/Vision/TextWaiter.cs src/FlaUI.Mcp.Server/Tools/FindTextTools.cs test/FlaUI.Mcp.Tests/Vision/TextWaiterTests.cs
git commit -m "feat(vision): desktop_wait_for_text — throttled off-STA OCR poll, timeout returns data"
```

---

## Task 12: Version bump 0.8.0 → 0.9.0 + docs

**Files:**
- Modify: `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj` (`<Version>0.8.0</Version>` → `0.9.0`)
- Modify: `install.ps1` (installer version, if it pins a version — STATE-VERIFY)
- Modify: `CHANGELOG.md` (add `[0.9.0] - 2026-07-03` section)
- Modify: `ROADMAP.md` (Phase 9 shipped; OCR/vision row)
- Modify: `README.md` (new "Opaque apps: wake + find_text" section + Known-limitations update)
- Modify: `.claude/skills/driving-flaui-mcp/SKILL.md` (§7 decision guidance: wake-first for Chromium, find_text for the residual)

**Context (§13):** The `feedback-readme-before-push` rule requires the README reflect the new tools before any push. Per [[feedback-readme-before-push]].

- [ ] **Step 0 (STATE-VERIFY):** Open `README.md` (find the tools list + Known-limitations anchor), `CHANGELOG.md` (top format of the latest `[0.8.0]` entry), `ROADMAP.md` (phase rows), `install.ps1` (does it hardcode a version?), and `SKILL.md` (the §7-style decision section). Confirm the shapes below match; report any mismatch.

- [ ] **Step 1: Bump `<Version>` to `0.9.0`** in `FlaUI.Mcp.Server.csproj`; update `install.ps1` version if pinned.

- [ ] **Step 2: Add the CHANGELOG entry** (`[0.9.0] - 2026-07-03`): Prong A — `desktop_wake_accessibility`/`desktop_release_accessibility`/`desktop_list_wakes` (activate + hold an opaque Chromium/Electron UIA tree; auto-release on close); `wakeable` hint on `desktop_snapshot`. Prong B — `desktop_find_text`/`desktop_wait_for_text` (on-box `Windows.Media.Ocr` targeting with screen-px + window-fraction coordinates). Note OCR is targeting, not reading (the model reads screenshots); an editor's document text may stay behind a screen-reader gate even when woken.

- [ ] **Step 3: Update ROADMAP** — mark Phase 9 shipped; add the OCR/vision + wake row.

- [ ] **Step 4: Update README** — add an "Opaque apps: wake + find_text" section (the §7 decision tree: rich UIA → snapshot; opaque Chromium (`wakeable:true`) → wake then snapshot/find/interact; zero-accessibility (game/canvas/editor text) → find_text + click_at, read via screenshot). Update Known-limitations: OCR needs a language pack (`OcrUnavailable`); OCR targets, doesn't read; the process-coarse deny-list can be punched through by OCR into RDP/Citrix wrappers (§8 Seat B); an editor's document text may stay gated.

- [ ] **Step 5: Update SKILL.md** — the wake-first-for-Chromium / find_text-for-the-residual decision guidance (§7), plus the ephemerality note (wake is held; released tree re-collapses) and the "verify a fuzzy match's text/bounds before clicking" caution.

- [ ] **Step 6: Final build + full test**

Run: `dotnet build` → 0/0. `dotnet test --filter "Category!=Desktop"` → all green. (Desktop suite run separately by the controller on a live console.)

- [ ] **Step 7: Commit**

```bash
git add src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj install.ps1 CHANGELOG.md ROADMAP.md README.md .claude/skills/driving-flaui-mcp/SKILL.md
git commit -m "docs(release): Phase 9 — wake + find_text; version 0.8.0 -> 0.9.0"
```

---

## Final step (after all tasks): whole-branch review + finish

- [ ] Dispatch a final `ecc:csharp-reviewer` over the entire branch diff (`git diff master...HEAD`).
- [ ] Route the finished branch to agy for a merge-gate panel review (AGY-AFTER, per the working-with-agy rules) BEFORE presenting the merge/cut decision to the user.
- [ ] Use **superpowers:finishing-a-development-branch** to merge/PR, then (on user GO) cut **v0.9.0** (tag + push → release.yml → GitHub Release), install, and live-smoke (wake VS Code → snapshot hydrates; `desktop_find_text` on a canvas/game; coord-landing).

---

## Self-Review (plan vs spec §14 coverage)

| Spec section | Task(s) |
| --- | --- |
| §2 two-prong principle | Prong A: 3–5; Prong B: 7–11 |
| §3 `desktop_wake_accessibility`/`release` | 3, 4, 5 |
| §3 `desktop_find_text` (fuzzy default, region=fractions, all, verify-before-click) | 8 (matcher), 10 (tool) |
| §3 `desktop_wait_for_text` (timeout=data, ≥750ms throttle, off-STA) | 11 |
| §3 `wakeable` hint (class AND collapsed) | 6 |
| §4 wake mechanism (null-sink, held, exempt caps, auto-release) | 4 (null sink + WindowInvalidated), 3 (separate caps) |
| §4 editor-text-gate caveat | documented in tool descriptions (5, 12) |
| §5 OCR engine (`Windows.Media.Ocr`, one engine, feasibility spike) | 1 (spike), 9 (impl) |
| §6 coordinate contract (scale+origin, cropped/negative/mixed-DPI, screen+pct return) | 7 (pure guard), 10 (Desktop landing test) |
| §7 wake-vs-OCR-vs-UIA routing | 6 (hint), 12 (SKILL.md), tool descriptions |
| §8 security (ReadOnly+lease-exempt, deny-list, OCR redaction via capture, RDP bypass doc) | 5, 10, 11 (ReadOnly+deny), 10/11 (capture redaction reuse), 12 (limitation doc) |
| §9 lifecycle/threading (STA-marshal, WindowInvalidated, off-STA capture+OCR) | 4 (STA-reentrancy), 11 (off-STA poll) |
| §10 testing (headless coord+match guards, Desktop wake+coord+timeout) | 3,4,6,7,8,11 headless; 5,10 Desktop |
| §11 forks (dedicated wake primitive; both coord returns; one engine; skip read_text dump; redaction best-effort) | resolved per spec leans (5, 7/10, 9, — , 12) |
| §12 spikes α/β gate the plan | 1, 2 |
| §13 version/docs | 12 |

**Placeholder scan:** no "TBD/TODO" in shipped code; the two Desktop tests (5, 10) and the wait Desktop path are intentionally stubbed with an explicit contract for the implementer to flesh out against the existing `DesktopWatchTests.cs`/harness (they cannot be authored blind without the live harness in front of the implementer) — flagged, not hidden. Spike-gated WinRT surface (Task 9) is explicitly marked "implement to the spike" with a STATE-VERIFY Step 0.

**Type consistency:** `OcrWord`(Text,X,Y,W,H) → `TextMatch`(Word,Confidence) → `TextFindMatch`(Text,Confidence,Bounds*,Center*,XPct,YPct); `MappedPoint`(ScreenX,ScreenY,XPct,YPct); `CoordinateMapping.BitmapToWindowPct`/`BitmapRectCenterToWindowPct`; `WakeRegistry`(k-ids)/`WakeInfo`(WakeId,WindowId)/`WakeService.WakeAsync`/`ReleaseAsync`/`ActiveWakeFor`/`List`; `IOcrEngine.RecognizeAsync(byte[])`; `TextFinder.FindAsync(...)`; `TextWaiter.WaitAsync(probe,timeoutMs,minIntervalMs)`/`MinPollIntervalMs`. Names are consistent across tasks.

## AGY-AFTER plan-panel record (2 rounds → converged)

Team-panel review of the FINISHED plan (cascade f0977c1d), folded WITH my own verification. **R1 (2 CONFIRMED defects, both folded):** (Seat 1) Task 11 wait loop resolved geometry ONCE before the poll → a window moved/resized mid-wait would be clicked at a stale coordinate → **fixed: re-resolve geometry EACH pass** (cheap ~1ms STA read at the ≥750ms cadence; the expensive capture stays off-STA via `Task.Run`). (Seat 3) `TextMatcher` did whole-query Levenshtein against single words → multi-word queries ("Submit Order") could never match → **fixed: fuzzy PHRASE matching via variable-width sliding word windows per line** (`OcrWord` carries `LineId`; `TextMatch` carries a union bitmap rect; `maxWin = queryTokens+1` also rejoins OCR over-splits). agy's companion claim that single-word-inside-a-line also fails was VERIFIED FALSE (OCR emits per-word, so a standalone word already matches) — folded only the valid multi-word half. Seats 2/4/5 (threading/security/release): no new findings. **R2 (post-fold, all 4 seats "no new findings" → clean full panel = STOP):** sliding-window algorithm + union math + per-pass geometry re-bind confirmed airtight; type shapes thread consistently; the `ActiveWakeFor`/`WakeAsync` idempotent-reuse "race" independently confirmed benign (stdio MCP calls serialize per-connection; a duplicate wake is a cap-bounded self-releasing handle). PANEL VERDICT R2: GO. Two rounds, converged.

**Known gaps flagged for resolution:**
1. **`ResolveTextCaptureGeometryAsync` shape** (Task 10 Step 0) — the exact resolver return type is confirmed at implementation time against `ResolveWindowCaptureGeometryAsync`; the plan pins the fields it must expose (capture rect + full window rect + deny/minimized + password rects). If it needs more than an additive method, the implementer STOPs.
2. **`SnapshotAsync` construction site** (Task 6 Step 5) — the window-root ClassName read placement is confirmed against the real method; additive-only.
3. **WinRT TFM/package** (Task 1/9) — the single load-bearing external unknown; a build-time spike GATES it, and NO-GO escalates to the user (spec change, not plan patch).
