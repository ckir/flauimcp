# SP-A — Human-Attention Toolset Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Windows foreground-lock *legible* — replace the generic `ElementDisappearedDuringAction` abort on a not-foreground keyboard target with an enriched, leak-safe `TargetNotForeground` result, add a pluggable attention-signal seam (flash + opt-in TTS), a blocking `desktop_wait_for_foreground` resume primitive, and an honest long-lease disclaiming warning.

**Architecture:** A pure Core decision (`ForegroundGate`) builds the leak-safe payload; a Core `IAttentionSignal` seam (mirroring the existing `IActionOverlay` Null/real DI split) fans out to `FlashSignal` (always on) and `TtsSignal` (autosound-gated, channel-wide debounced). The keyboard input tools (`desktop_type`/`desktop_key`) consult the gate after focus+resolve and before the `SendInput` `Task.Run`; on a not-foreground target they return `TargetNotForeground` and fire the signal instead of attempting a send that would throw the generic error. `desktop_wait_for_foreground` blocks on a dedicated non-STA thread via `SetWinEventHook`.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`), xUnit (`Category!=Desktop` headless gate), Win32 P/Invoke (`FlashWindowEx`, `SetWinEventHook`), `System.Speech` for TTS, ModelContextProtocol SDK tools.

**Reference — source spec:** `docs/superpowers/specs/2026-07-05-flaui-mcp-human-attention-toolset-design.md` (this plan is the oracle-bearing companion; where a value is a "plan-level constant" the spec named, this plan pins it).

**Build ordering (SEAT-I fold — mandatory):** SP-A and SP-B both edit the SAME four files (`ServerOptions.cs`, `Install/CliRouter.cs`, `Program.cs`, `Install/ConfigArgsMerge.cs`). They MUST be executed **strictly sequentially — SP-A fully, then SP-B** — never as parallel subagents (that guarantees merge conflicts / clobbered edits). SP-A owns `ConfigArgsMerge` + `ApplyMerge` (Task 9); SP-B reuses them.

---

## Scope note — which tools the gate wraps (grounded, read before Task 6)

The spec §4.1 parenthetically lists `desktop_type`/`desktop_click`/`desktop_key`. Grounding against the real code (`src/FlaUI.Mcp.Core/Interaction/Win32SyntheticInput.cs`) shows the **not-foreground** re-verify exists *only* on the keyboard path:

- `KeyType` / `KeyChord` call `Reverify(expectedForegroundRoot, _env.GetForegroundRoot())` — a **foreground** check.
- `MouseClick` / `MouseDrag` call `Reverify(expected, _env.HitTestRoot(point).Root)` — a **point/occlusion** check, NOT foreground. A mouse click at absolute coordinates lands (and *activates* the window) regardless of foreground — a click is a *remedy* for the lock, not a victim of it.

Therefore the enriched `TargetNotForeground` gate is scoped to **`desktop_type` and `desktop_key`** (the tools whose failure cause is genuinely "not foreground"). `desktop_click`/`desktop_click_at`/`desktop_drag` keep their existing point-hit-test re-verify unchanged. This is the faithful reading of "the not-foreground cause specifically" (spec §4.1/§6), not a divergence.

## Plan-level constants (the spec deferred these to plan; pinned here)

| Constant | Value | Where | Spec ref |
|---|---|---|---|
| TTS speech stack | `System.Speech.Synthesis.SpeechSynthesizer` (in-proc, plays to default device) | `TtsSignal` (Server) | §4.4 |
| TTS debounce | channel-wide token bucket: **≤ 3 utterances per rolling 30 s** | `TtsDebounce` (Core) | §4.4 |
| `wait_for_foreground` hard cap | **45000 ms** (request clamped to this max; also the default) | `WaitForForeground` (Core) | §4.5 |
| `MaxConcurrentWaiters` | **1** | `WaitForForeground` | §4.5 |
| Long-lease warning threshold | lease **> 60 minutes** fires the warning | `LeaseWriter.Grant` | §4.6 |

## File structure

- Create `src/FlaUI.Mcp.Core/Attention/ForegroundGate.cs` — pure decision + leak-safe payload builder.
- Create `src/FlaUI.Mcp.Core/Attention/IAttentionSignal.cs` — seam + `NullAttentionSignal` + `CompositeAttentionSignal`.
- Create `src/FlaUI.Mcp.Core/Attention/TtsDebounce.cs` — pure channel-wide token bucket.
- Create `src/FlaUI.Mcp.Core/Attention/WaitForForeground.cs` — pure cap/single-waiter decisions + the event-driven wait service seam.
- Create `src/FlaUI.Mcp.Server/Attention/FlashSignal.cs` — `FlashWindowEx` channel.
- Create `src/FlaUI.Mcp.Server/Attention/TtsSignal.cs` — `System.Speech` channel behind the debounce.
- Create `src/FlaUI.Mcp.Server/Attention/Win32ForegroundWaiter.cs` — `SetWinEventHook` dedicated-thread waiter (real `IForegroundWaiter`).
- Modify `src/FlaUI.Mcp.Server/ServerOptions.cs` — add `Autosound` flag.
- Modify `src/FlaUI.Mcp.Server/Program.cs` — DI-bind `IAttentionSignal` + the waiter.
- Modify `src/FlaUI.Mcp.Server/Tools/InputTools.cs` — consult the gate in `desktop_type`/`desktop_key`; fire the signal.
- Modify `src/FlaUI.Mcp.Core/Windows/WindowManager.cs` + `src/FlaUI.Mcp.Server/Tools/WindowTools.cs` — enrich `desktop_focus_window`; add `desktop_wait_for_foreground`.
- Modify `src/FlaUI.Mcp.Server/Install/CliRouter.cs` + `McpServerEntry.cs` — `autosound on|off` verb + non-destructive flag merge.
- Modify `src/FlaUI.Mcp.Server/Lease/LeaseWriter.cs` + `CliRouter.cs` — long-lease disclaiming warning + `--accept-risk`.
- Docs: `README.md`, `CHANGELOG.md`, `.claude/skills/driving-flaui-mcp/SKILL.md`, version bump.

## Build / test commands (RESPECT THE REPO GATE AS-IS)

- Build: `dotnet build -clp:ErrorsOnly` → expect `Build succeeded. 0 Warning(s) 0 Error(s)` (5 projects).
- Headless tests: `dotnet test --filter "Category!=Desktop" --no-build` → expect `Passed!  - Failed: 0` (current baseline **416** passing; each task adds to it).
- Desktop tests are `Category=Desktop`, console-only (the dev/CI box is headless/RDP) — run at the console smoke, never gate CI on them.

---

### Task 1: `ForegroundGate` — pure not-foreground decision + leak-safe payload

**Files:**
- Create: `src/FlaUI.Mcp.Core/Attention/ForegroundGate.cs`
- Test: `test/FlaUI.Mcp.Tests/Attention/ForegroundGateTests.cs`

**Context (Step 0 — STATE-VERIFY before editing):** Open `src/FlaUI.Mcp.Core/Interaction/Win32PlatformEnvironment.cs` and confirm `IPlatformEnvironment.ResolveRoot(nint root)` returns a `PointTarget(nint Root, string? ProcessName, string? WindowClass)` and `GetForegroundRoot()` returns `nint` (0 when none). Confirm `PointTarget` lives in the `FlaUI.Mcp.Core.Interaction` namespace (grep `record.*PointTarget`). If either differs, STOP and report `STATE_MISMATCH: <what>`.

**Oracle:** spec §4.1 result shape + Title-leak rule ("process name only by default; a title ONLY for a modal owned by the exact target HWND via `GetWindow(target, GW_OWNER)`; else no title").

- [ ] **Step 1: Write the failing test**

```csharp
using FlaUI.Mcp.Core.Attention;
using Xunit;

namespace FlaUI.Mcp.Tests.Attention;

public class ForegroundGateTests
{
    [Fact]
    public void Target_is_foreground_returns_null_result()
    {
        // expected == live foreground → no problem, gate passes (null = "go ahead").
        var r = ForegroundGate.Evaluate(targetRoot: 100, foregroundRoot: 100, "w1",
            resolveProcess: _ => "notepad", ownerHwnd: _ => 0);
        Assert.Null(r);
    }

    [Fact]
    public void Not_foreground_returns_process_name_only_never_title()
    {
        var r = ForegroundGate.Evaluate(targetRoot: 100, foregroundRoot: 200, "w1",
            resolveProcess: h => h == 200 ? "chrome" : "notepad",
            ownerHwnd: _ => 0 /* foreground is NOT an owned modal of target */);
        Assert.NotNull(r);
        Assert.Equal("w1", r!.TargetWindow);
        Assert.Equal("chrome", r.CurrentForeground.Process);
        Assert.Null(r.CurrentForeground.Title);            // leak rule: no title
        Assert.Equal("call-wait-for-foreground", r.RecommendedAction);
        Assert.Contains("desktop_wait_for_foreground", r.Recovery);
    }

    [Fact]
    public void Title_disclosed_only_for_modal_owned_by_exact_target()
    {
        // foreground(200) is a modal whose GW_OWNER == target(100): a title MAY be returned.
        var r = ForegroundGate.Evaluate(targetRoot: 100, foregroundRoot: 200, "w1",
            resolveProcess: _ => "notepad",
            ownerHwnd: h => h == 200 ? 100 : 0,
            resolveTitle: _ => "Save changes?");
        Assert.NotNull(r);
        Assert.Equal("Save changes?", r!.CurrentForeground.Title);
    }

    [Fact]
    public void No_foreground_window_recommends_wait_not_launch()
    {
        // foregroundRoot 0 (nothing foreground) is still "not our target" → wait.
        var r = ForegroundGate.Evaluate(targetRoot: 100, foregroundRoot: 0, "w1",
            resolveProcess: _ => null, ownerHwnd: _ => 0);
        Assert.NotNull(r);
        Assert.Equal("call-wait-for-foreground", r!.RecommendedAction);
        Assert.Null(r.CurrentForeground.Process);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ForegroundGateTests" --no-build` (after a build) — Expected: FAIL, `ForegroundGate` does not exist (CS0103).

- [ ] **Step 3: Write minimal implementation**

```csharp
using System;

namespace FlaUI.Mcp.Core.Attention;

/// <summary>The leak-safe foreground identity surfaced when a keyboard action can't reach its target.
/// Process name only by default; Title is non-null ONLY for a modal owned by the exact target HWND.</summary>
public readonly record struct ForegroundIdentity(string Handle, string? Process, string? Title);

/// <summary>The enriched, JSON-serializable not-foreground result (spec §4.1). Replaces the generic
/// ElementDisappearedDuringAction for the not-foreground cause on the keyboard path.</summary>
public sealed record TargetNotForeground(
    string TargetWindow,
    ForegroundIdentity CurrentForeground,
    string RecommendedAction,   // "call-wait-for-foreground" | "launch-fresh"
    string Recovery);

/// <summary>Pure not-foreground decision + leak-safe payload builder. No Win32 here — the caller injects
/// the live foreground root, a process-name resolver, an owner-HWND resolver, and (only if an owned modal
/// is detected) a title resolver. Returns null when the target IS the foreground (action may proceed).</summary>
public static class ForegroundGate
{
    public static TargetNotForeground? Evaluate(
        nint targetRoot,
        nint foregroundRoot,
        string targetWindowId,
        Func<nint, string?> resolveProcess,
        Func<nint, nint> ownerHwnd,
        Func<nint, string?>? resolveTitle = null)
    {
        if (targetRoot != 0 && targetRoot == foregroundRoot) return null; // target holds foreground → go

        string? process = foregroundRoot != 0 ? resolveProcess(foregroundRoot) : null;

        // Title leak rule (spec §4.1): disclose a title ONLY when the foreground window is a modal
        // OWNED by the exact target HWND — owner-HWND identity, never process equality.
        string? title = null;
        if (foregroundRoot != 0 && targetRoot != 0 && resolveTitle is not null
            && ownerHwnd(foregroundRoot) == targetRoot)
            title = resolveTitle(foregroundRoot);

        return new TargetNotForeground(
            targetWindowId, DescribeForeground(foregroundRoot, targetRoot, resolveProcess, ownerHwnd, resolveTitle),
            "call-wait-for-foreground",
            "Call `desktop_wait_for_foreground` on this window — do NOT yield the chat turn to wait for the human to click it.");
    }

    /// <summary>Build the leak-safe foreground identity (spec §4.1 leak rule) — process name only, title
    /// ONLY for a modal owned by the exact target HWND. Shared so EVERY tool that reports currentForeground
    /// (the type/key gate, desktop_focus_window, desktop_wait_for_foreground) produces an IDENTICAL shape
    /// (SEAT-D wire-contract fold). `foregroundRoot`==0 → handle "0", no process/title.</summary>
    public static ForegroundIdentity DescribeForeground(
        nint foregroundRoot, nint targetRoot,
        Func<nint, string?> resolveProcess, Func<nint, nint> ownerHwnd, Func<nint, string?>? resolveTitle = null)
    {
        string? process = foregroundRoot != 0 ? resolveProcess(foregroundRoot) : null;
        string? title = null;
        if (foregroundRoot != 0 && targetRoot != 0 && resolveTitle is not null
            && ownerHwnd(foregroundRoot) == targetRoot)
            title = resolveTitle(foregroundRoot);
        return new ForegroundIdentity(foregroundRoot == 0 ? "0" : "0x" + foregroundRoot.ToString("x"), process, title);
    }
}
```

Note: `recommendedAction` is always `call-wait-for-foreground` here because the gate fires on an *existing, resolved* target window (which exists and is visible — spec §4.1). `launch-fresh` is emitted by callers that have no live window to wait on (none in this plan's keyboard path); the enum value is defined so a future caller can select it.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet build -clp:ErrorsOnly && dotnet test --filter "FullyQualifiedName~ForegroundGateTests" --no-build` — Expected: PASS (4/4).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Attention/ForegroundGate.cs test/FlaUI.Mcp.Tests/Attention/ForegroundGateTests.cs
git commit -m "feat(attention): ForegroundGate pure not-foreground decision + leak-safe payload (SP-A T1)"
```

---

### Task 2: `IAttentionSignal` seam + Null + Composite fan-out

**Files:**
- Create: `src/FlaUI.Mcp.Core/Attention/IAttentionSignal.cs`
- Test: `test/FlaUI.Mcp.Tests/Attention/AttentionSignalTests.cs`

**Context (Step 0):** Open `src/FlaUI.Mcp.Core/Interaction/IActionOverlay.cs` and confirm the Null-object pattern used (`NullActionOverlay.Instance` singleton, `Enabled` bool, best-effort `PreviewAsync`). Mirror that shape. Confirm `WindowHandle` is `readonly record struct WindowHandle(string Id)` in `FlaUI.Mcp.Core.Windows`. If either differs, STOP and report `STATE_MISMATCH`.

**Oracle:** spec §4.2 — `void Signal(WindowHandle target)` best-effort/never-throws; composable fan-out; Null no-op.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Generic;
using FlaUI.Mcp.Core.Attention;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Attention;

public class AttentionSignalTests
{
    private sealed class Rec : IAttentionSignal
    {
        public readonly List<string> Fired = new();
        public bool Enabled { get; init; } = true;
        public void Signal(WindowHandle t) => Fired.Add(t.Id);
    }

    [Fact]
    public void Null_signal_is_disabled_and_no_ops()
    {
        var n = NullAttentionSignal.Instance;
        Assert.False(n.Enabled);
        n.Signal(new WindowHandle("w1")); // must not throw
    }

    [Fact]
    public void Composite_fans_out_to_every_enabled_child()
    {
        var a = new Rec(); var b = new Rec();
        var c = new CompositeAttentionSignal(new IAttentionSignal[] { a, b });
        Assert.True(c.Enabled);
        c.Signal(new WindowHandle("w7"));
        Assert.Equal(new[] { "w7" }, a.Fired);
        Assert.Equal(new[] { "w7" }, b.Fired);
    }

    [Fact]
    public void Composite_never_throws_when_a_child_throws()
    {
        var boom = new ThrowingSignal(); var ok = new Rec();
        var c = new CompositeAttentionSignal(new IAttentionSignal[] { boom, ok });
        c.Signal(new WindowHandle("w1"));  // boom throws internally; ok must still fire
        Assert.Equal(new[] { "w1" }, ok.Fired);
    }

    private sealed class ThrowingSignal : IAttentionSignal
    { public bool Enabled => true; public void Signal(WindowHandle t) => throw new System.Exception("boom"); }

    [Fact]
    public void Composite_disabled_when_no_enabled_children()
    {
        var c = new CompositeAttentionSignal(new IAttentionSignal[] { new Rec { Enabled = false } });
        Assert.False(c.Enabled);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~AttentionSignalTests" --no-build` — Expected: FAIL (types missing).

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Collections.Generic;
using System.Linq;
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Core.Attention;

/// <summary>The attention-signal seam (spec §4.2). Signal is best-effort and MUST NEVER throw — a failed
/// signal must not turn a tool result into an error. Mirrors IActionOverlay's Null/real DI split.</summary>
public interface IAttentionSignal
{
    bool Enabled { get; }
    void Signal(WindowHandle target);
}

/// <summary>Default binding when no channels are active: nothing.</summary>
public sealed class NullAttentionSignal : IAttentionSignal
{
    public static readonly NullAttentionSignal Instance = new();
    private NullAttentionSignal() { }
    public bool Enabled => false;
    public void Signal(WindowHandle target) { }
}

/// <summary>Fans a Signal out to every child; swallows a child fault so one bad channel never breaks the
/// others or the tool. Enabled iff any child is enabled.</summary>
public sealed class CompositeAttentionSignal : IAttentionSignal
{
    private readonly IReadOnlyList<IAttentionSignal> _children;
    public CompositeAttentionSignal(IReadOnlyList<IAttentionSignal> children) => _children = children;
    public bool Enabled => _children.Any(c => c.Enabled);
    public void Signal(WindowHandle target)
    {
        foreach (var c in _children)
            try { if (c.Enabled) c.Signal(target); } catch { /* best-effort: never throw from the signal path */ }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet build -clp:ErrorsOnly && dotnet test --filter "FullyQualifiedName~AttentionSignalTests" --no-build` — Expected: PASS (5/5).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Attention/IAttentionSignal.cs test/FlaUI.Mcp.Tests/Attention/AttentionSignalTests.cs
git commit -m "feat(attention): IAttentionSignal seam + Null + Composite fan-out (SP-A T2)"
```

---

### Task 3: `TtsDebounce` — pure channel-wide token bucket

**Files:**
- Create: `src/FlaUI.Mcp.Core/Attention/TtsDebounce.cs`
- Test: `test/FlaUI.Mcp.Tests/Attention/TtsDebounceTests.cs`

**Oracle:** spec §4.4 — cooldown is a **global token-bucket agnostic to the target** (NOT per-target — a per-target debounce is trivially evaded by oscillating targets `w1→w2→w1`). Constant: ≤ 3 utterances per rolling 30 s.

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using FlaUI.Mcp.Core.Attention;
using Xunit;

namespace FlaUI.Mcp.Tests.Attention;

public class TtsDebounceTests
{
    [Fact]
    public void Allows_up_to_capacity_then_blocks_within_window()
    {
        var t0 = DateTime.UtcNow;
        var d = new TtsDebounce(capacity: 3, window: TimeSpan.FromSeconds(30));
        Assert.True(d.TryTake(t0));
        Assert.True(d.TryTake(t0));
        Assert.True(d.TryTake(t0));
        Assert.False(d.TryTake(t0));                       // 4th within window → blocked
    }

    [Fact]
    public void Is_target_agnostic_oscillating_targets_do_not_evade()
    {
        // The bucket takes no target argument at all — proven by capacity being global.
        var t0 = DateTime.UtcNow;
        var d = new TtsDebounce(3, TimeSpan.FromSeconds(30));
        d.TryTake(t0); d.TryTake(t0); d.TryTake(t0);
        Assert.False(d.TryTake(t0));                       // still blocked regardless of any target churn
    }

    [Fact]
    public void Refills_as_the_window_slides()
    {
        var t0 = DateTime.UtcNow;
        var d = new TtsDebounce(3, TimeSpan.FromSeconds(30));
        d.TryTake(t0); d.TryTake(t0); d.TryTake(t0);
        Assert.True(d.TryTake(t0 + TimeSpan.FromSeconds(31))); // first three aged out
    }
}
```

- [ ] **Step 2: Run test to verify it fails** — Run: `dotnet test --filter "FullyQualifiedName~TtsDebounceTests" --no-build` — Expected: FAIL (type missing).

- [ ] **Step 3: Write minimal implementation**

```csharp
using System;
using System.Collections.Generic;

namespace FlaUI.Mcp.Core.Attention;

/// <summary>Channel-wide (target-agnostic) sliding-window rate limiter for TTS utterances (spec §4.4).
/// Deliberately takes NO target argument so a prompt-injected agent oscillating targets cannot evade it.
/// Thread-safe (locks internally): the signal path may fire from concurrent tool calls.</summary>
public sealed class TtsDebounce
{
    private readonly int _capacity;
    private readonly TimeSpan _window;
    private readonly Queue<DateTime> _stamps = new();
    private readonly object _lock = new();

    public TtsDebounce(int capacity, TimeSpan window) { _capacity = capacity; _window = window; }

    /// <summary>Take one token at time `now`; true if allowed. Ages out stamps older than the window first.</summary>
    public bool TryTake(DateTime now)
    {
        lock (_lock)
        {
            while (_stamps.Count > 0 && now - _stamps.Peek() >= _window) _stamps.Dequeue();
            if (_stamps.Count >= _capacity) return false;
            _stamps.Enqueue(now);
            return true;
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes** — Run: `dotnet build -clp:ErrorsOnly && dotnet test --filter "FullyQualifiedName~TtsDebounceTests" --no-build` — Expected: PASS (3/3).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Attention/TtsDebounce.cs test/FlaUI.Mcp.Tests/Attention/TtsDebounceTests.cs
git commit -m "feat(attention): TtsDebounce channel-wide token bucket (SP-A T3)"
```

---

### Task 4: `FlashSignal` (Server) — FlashWindowEx channel

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Windows/WindowManager.cs` (add public `TryGetHwnd`)
- Create: `src/FlaUI.Mcp.Server/Attention/FlashSignal.cs`
- Test: `test/FlaUI.Mcp.Tests/Attention/FlashSignalTests.cs`

**Context (Step 0):** `FlashSignal` needs to resolve a `WindowHandle` (`wN`) to its HWND. Open `src/FlaUI.Mcp.Core/Windows/WindowManager.cs` and confirm `_hwnds` is a private `ConcurrentDictionary<string, IntPtr>` with **no public HWND getter**. If a public getter already exists, use it and skip adding one. STOP + report `STATE_MISMATCH` if `_hwnds` is not `ConcurrentDictionary<string, IntPtr>`.

**Oracle:** spec §4.3 — `FlashWindowEx` with `FLASHW_TIMERNOFG`; steals no focus, needs no foreground rights, cross-process with just the HWND; never throws.

- [ ] **Step 1: Add `WindowManager.TryGetHwnd`** (in `WindowManager.cs`, near `ResolveWindow`):

```csharp
/// <summary>Best-effort HWND lookup for a handle WITHOUT binding UIA (pure dict read). Used by the
/// attention signals (flash / wait-for-foreground), which need only the raw HWND, never a COM Window.
/// Does NOT run the M2 pid-reverify (the signal is a benign flash/observe, not an input write).</summary>
public bool TryGetHwnd(WindowHandle handle, out IntPtr hwnd)
    => _hwnds.TryGetValue(handle.Id, out hwnd) && hwnd != IntPtr.Zero;
```

- [ ] **Step 2: Write the failing test** (headless: the signal must be construct-safe and never throw for an unknown handle):

```csharp
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Attention;
using FlaUI.Mcp.Core.Threading;
using Xunit;

namespace FlaUI.Mcp.Tests.Attention;

public class FlashSignalTests
{
    [Fact]
    public void Enabled_true_and_signal_never_throws_on_unknown_handle()
    {
        var wm = new WindowManager(new AutomationDispatcher());
        var f = new FlashSignal(wm);
        Assert.True(f.Enabled);
        f.Signal(new WindowHandle("w-nonexistent")); // no HWND → no-op, must not throw
        wm.Dispose();
    }
}
```

Note: if constructing a real `WindowManager`/`AutomationDispatcher` is not headless-safe in CI (it creates a `UIA3Automation` on the query STA), check whether existing `test/FlaUI.Mcp.Tests/Windows/WindowManagerTests.cs` constructs one; grounding shows it exists and exercises pure statics. If direct construction throws in CI, instead extract an `IHwndSource { bool TryGetHwnd(WindowHandle, out IntPtr); }` interface that `WindowManager` implements, have `FlashSignal` depend on `IHwndSource`, and inject a fake in this test. Prefer the interface if direct construction is not viable.

- [ ] **Step 3: Run test to verify it fails** — Expected: FAIL (`FlashSignal` missing).

- [ ] **Step 4: Write minimal implementation**

```csharp
using System;
using System.Runtime.InteropServices;
using FlaUI.Mcp.Core.Attention;
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Server.Attention;

/// <summary>Always-available attention channel (spec §4.3): flashes the target's taskbar button until the
/// window comes to the foreground (FLASHW_TIMERNOFG). Steals no focus, needs no foreground rights, works
/// cross-process with just the HWND. Best-effort — never throws.</summary>
public sealed class FlashSignal : IAttentionSignal
{
    private readonly WindowManager _windows;
    public FlashSignal(WindowManager windows) => _windows = windows;
    public bool Enabled => true;

    public void Signal(WindowHandle target)
    {
        try
        {
            if (!_windows.TryGetHwnd(target, out var hwnd)) return; // no HWND (e.g. closed) → nothing to flash
            var fw = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = hwnd,
                dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
                uCount = uint.MaxValue,
                dwTimeout = 0,
            };
            FlashWindowEx(ref fw);
        }
        catch { /* flash is best-effort; never break a tool result */ }
    }

    private const uint FLASHW_TRAY = 0x2, FLASHW_TIMERNOFG = 0xC;
    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO pfwi);
}
```

- [ ] **Step 5: Run test + build** — Run: `dotnet build -clp:ErrorsOnly && dotnet test --filter "FullyQualifiedName~FlashSignalTests" --no-build` — Expected: PASS. Also run the full headless suite to confirm no regression: `dotnet test --filter "Category!=Desktop" --no-build`.

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Core/Windows/WindowManager.cs src/FlaUI.Mcp.Server/Attention/FlashSignal.cs test/FlaUI.Mcp.Tests/Attention/FlashSignalTests.cs
git commit -m "feat(attention): FlashSignal FlashWindowEx channel + WindowManager.TryGetHwnd (SP-A T4)"
```

---

### Task 5: `TtsSignal` (Server) — System.Speech channel behind the debounce

**Files:**
- Create: `src/FlaUI.Mcp.Server/Attention/TtsSignal.cs`
- Modify: `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj` (add `System.Speech` package)
- Test: `test/FlaUI.Mcp.Tests/Attention/TtsSignalTests.cs`

**Context (Step 0):** Open `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj` and note the `<PackageReference>` block + `<TargetFramework>`. Confirm it targets `net10.0-windows`. Add `<PackageReference Include="System.Speech" Version="9.0.0" />` (System.Speech ships as a Windows-only NuGet; pin the current major that restores — if 9.0.0 fails to restore, use the latest `System.Speech` that does and note the version in the commit). If the csproj already references it, skip.

**Oracle:** spec §4.4 — spoken content names **only the target app** (never the cross-process foreground title); debounce lives INSIDE the channel; when autosound off, `TtsSignal` is never constructed (Task 6 DI) — so `Enabled => true`.

- [ ] **Step 1: Write the failing test** (the speakable-line builder is the pure, testable part — assert it never contains a foreground title and names the target app):

```csharp
using FlaUI.Mcp.Server.Attention;
using Xunit;

namespace FlaUI.Mcp.Tests.Attention;

public class TtsSignalTests
{
    [Fact]
    public void Utterance_names_the_target_app_only()
    {
        var line = TtsSignal.Utterance("Character Map");
        Assert.Contains("Character Map", line);
    }

    [Fact]
    public void Utterance_falls_back_when_app_name_unknown()
    {
        var line = TtsSignal.Utterance(null);
        Assert.False(string.IsNullOrWhiteSpace(line));
        Assert.DoesNotContain("null", line);
    }
}
```

- [ ] **Step 2: Run test to verify it fails** — Expected: FAIL.

- [ ] **Step 3: Write minimal implementation** (speak on a background thread — `SpeechSynthesizer.Speak` needs no foreground; guard the whole thing):

```csharp
using System;
using System.Speech.Synthesis;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Attention;
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Server.Attention;

/// <summary>Opt-in spoken attention channel (spec §4.4). Only constructed when `--autosound` is on, so
/// Enabled is always true here. Debounced channel-wide (target-agnostic) via TtsDebounce. Speaks ONLY the
/// target app's name — never the cross-process foreground title (leak rule §4.1). Best-effort; never throws.</summary>
public sealed class TtsSignal : IAttentionSignal
{
    private readonly Func<WindowHandle, string?> _appNameOf;   // maps target handle → its own app name (already known)
    private readonly TtsDebounce _debounce;
    private readonly Func<DateTime> _clock;

    public TtsSignal(Func<WindowHandle, string?> appNameOf, TtsDebounce debounce, Func<DateTime>? clock = null)
    { _appNameOf = appNameOf; _debounce = debounce; _clock = clock ?? (() => DateTime.UtcNow); }

    public bool Enabled => true;

    public static string Utterance(string? appName) =>
        string.IsNullOrWhiteSpace(appName) ? "Please switch to the window the assistant is waiting on."
                                           : $"Please click {appName}.";

    public void Signal(WindowHandle target)
    {
        try
        {
            if (!_debounce.TryTake(_clock())) return;   // channel-wide rate cap
            var line = Utterance(_appNameOf(target));
            _ = Task.Run(() =>
            {
                try { using var s = new SpeechSynthesizer(); s.Speak(line); }
                catch { /* no audio device / synth failure → silent */ }
            });
        }
        catch { /* never throw from the signal path */ }
    }
}
```

- [ ] **Step 4: Run test + full headless suite** — `dotnet build -clp:ErrorsOnly && dotnet test --filter "Category!=Desktop" --no-build` — Expected: PASS, no regression.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj src/FlaUI.Mcp.Server/Attention/TtsSignal.cs test/FlaUI.Mcp.Tests/Attention/TtsSignalTests.cs
git commit -m "feat(attention): TtsSignal System.Speech channel behind channel-wide debounce (SP-A T5)"
```

---

### Task 6: Enriched gate in `desktop_type` / `desktop_key` + fire the signal + DI

**Files:**
- Modify: `src/FlaUI.Mcp.Server/ServerOptions.cs` (add `Autosound` flag)
- Modify: `src/FlaUI.Mcp.Server/Program.cs` (DI-bind `IAttentionSignal`)
- Modify: `src/FlaUI.Mcp.Server/Tools/InputTools.cs` (consult the gate in `DesktopType` + `DesktopKey`)
- Modify: `src/FlaUI.Mcp.Core/Windows/WindowManager.cs` (add `TryGetAppName`, `OwnerHwnd`, `WindowTitle`)
- Test: `test/FlaUI.Mcp.Tests/Interaction/ForegroundGateWiringTests.cs`

**Context (Step 0 — CRITICAL, verify all three):**
1. `ServerOptions` is a `record` with signature `ServerOptions(bool ReadOnly, bool AllowElevation, bool Overlay = false, int OverlayMs = 500)` and a `FromArgs(string[])` static. Confirm verbatim; if changed, STOP + `STATE_MISMATCH`.
2. In `InputTools.cs`, `DesktopType` resolves `ActionTarget target` then calls `await PreviewSyntheticAsync(...)` and `await Task.Run(() => _guard.KeyType(...))` (grounding: around line 197-198); `DesktopKey` resolves `target` then `await PreviewSyntheticAsync(...)` / `await Task.Run(() => _guard.KeyChord(...))` (around line 361-362). The gate is inserted **between** the resolve and the preview/`Task.Run`. Confirm those call sites exist.
3. `InputTools` ctor injects `IPlatformEnvironment _env`, `WindowManager _windows`, `IActionOverlay? overlay = null`; it does NOT yet inject `IAttentionSignal`. Add an `IAttentionSignal? attention = null` ctor param defaulting to `NullAttentionSignal.Instance` (mirroring the existing `overlay` param).

**Oracle:** spec §4.1 (gate replaces the generic failure, fires before the send, emits the signal on a `call-wait-for-foreground` result) + §6 (generic `ElementDisappearedDuringAction` REMAINS for the genuine mid-send steal — so the gate must NOT remove or weaken `Win32SyntheticInput.Reverify`).

**SHAPE-DIVERGENCE STOP:** the `TargetNotForeground` record is returned as a **tool result**, not thrown. It must serialize through `ToolResponse.Ok(...)`. Do NOT map it into the `ToolException`/error channel — it is a *successful* structured result describing why the action didn't fire (spec §6 "returned cleanly through ToolResponse"). If making it compile pushes you toward throwing it, STOP and report.

- [ ] **Step 1: Add `Autosound` to `ServerOptions`**

```csharp
public sealed record ServerOptions(bool ReadOnly, bool AllowElevation, bool Overlay = false, int OverlayMs = 500, bool Autosound = false)
{
    public static ServerOptions FromArgs(string[] args) =>
        new(ReadOnly: args.Contains("--read-only-mode"),
            AllowElevation: args.Contains("--unsafe-allow-elevation"),
            Overlay: args.Contains("--overlay"),
            OverlayMs: ParseOverlayMs(args),
            Autosound: args.Contains("--autosound"));
    // ParseOverlayMs unchanged
}
```

- [ ] **Step 2: Add the three Win32 helpers to `WindowManager`** (`TryGetAppName`, `OwnerHwnd`, `WindowTitle`). Grounding: `System.Diagnostics.Process`, `StringBuilder`, `GetWindowThreadProcessId`, `GetWindowTextLengthW`, `GetWindowTextW` are already imported/declared in `WindowManager.cs`; add only `GetWindow` + the `GW_OWNER` const:

```csharp
/// <summary>The target handle's own process base-name (the app the agent already chose to act on), for the
/// TTS utterance. Null when unknown. Pure Win32 — no UIA, safe off-STA.</summary>
public string? TryGetAppName(WindowHandle handle)
{
    if (!_hwnds.TryGetValue(handle.Id, out var hwnd) || hwnd == IntPtr.Zero) return null;
    GetWindowThreadProcessId(hwnd, out uint pid);
    try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; } catch { return null; }
}

/// <summary>GW_OWNER of an HWND (0 if none). Used by the leak rule to detect a modal owned by the exact
/// target window. Pure Win32.</summary>
public IntPtr OwnerHwnd(IntPtr hwnd) { try { return GetWindow(hwnd, GW_OWNER); } catch { return IntPtr.Zero; } }
private const uint GW_OWNER = 4;
[DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

/// <summary>Cached window caption via Win32 GetWindowText (does NOT block on a hung window). Only ever
/// consulted for an owner-verified modal (leak rule). Null/empty → null.</summary>
public string? WindowTitle(IntPtr hwnd)
{
    try { int len = GetWindowTextLengthW(hwnd); if (len == 0) return null;
          var sb = new StringBuilder(len + 1); GetWindowTextW(hwnd, sb, sb.Capacity);
          var s = sb.ToString(); return string.IsNullOrEmpty(s) ? null : s; }
    catch { return null; }
}
```

- [ ] **Step 3: DI-bind `IAttentionSignal` in `Program.cs`** (after the `IActionOverlay` registration, before/near the core singletons — `WindowManager` is registered just below, so this factory resolving `WindowManager` is fine at build time):

```csharp
// SP-A attention signals: flash is always available; TTS only when --autosound is on. Composite fans out.
builder.Services.AddSingleton(_ =>
    new FlaUI.Mcp.Core.Attention.TtsDebounce(capacity: 3, window: System.TimeSpan.FromSeconds(30)));
builder.Services.AddSingleton<FlaUI.Mcp.Core.Attention.IAttentionSignal>(sp =>
{
    var o = sp.GetRequiredService<ServerOptions>();
    var wm = sp.GetRequiredService<WindowManager>();
    var channels = new System.Collections.Generic.List<FlaUI.Mcp.Core.Attention.IAttentionSignal>
        { new FlaUI.Mcp.Server.Attention.FlashSignal(wm) };
    if (o.Autosound)
        channels.Add(new FlaUI.Mcp.Server.Attention.TtsSignal(
            h => wm.TryGetAppName(h),
            sp.GetRequiredService<FlaUI.Mcp.Core.Attention.TtsDebounce>()));
    return new FlaUI.Mcp.Core.Attention.CompositeAttentionSignal(channels);
});
```

- [ ] **Step 4: Add the ctor param + gate helper to `InputTools`**

Constructor change:
```csharp
private readonly IAttentionSignal _attention;
public InputTools(PerceptionManager perception, WindowManager windows, ServerOptions options,
    InputGuard guard, IPlatformEnvironment env, IActionOverlay? overlay = null,
    IAttentionSignal? attention = null)
{ _perception = perception; _windows = windows; _options = options; _guard = guard; _env = env;
  _overlay = overlay ?? NullActionOverlay.Instance; _attention = attention ?? NullAttentionSignal.Instance; }
```

Gate helper (returns the serialized `TargetNotForeground` result string, or null to proceed):
```csharp
// SP-A: the leak-safe not-foreground gate for the KEYBOARD path only (desktop_type/desktop_key).
// target.Root is the resolved target window's root; compare to the live OS foreground. On a mismatch,
// fire the attention signal and return the enriched result — do NOT attempt the send that would throw
// the generic ElementDisappearedDuringAction. Returns null when the target holds foreground (proceed).
private string? ForegroundGateReply(ActionTarget target, string windowId)
{
    var result = FlaUI.Mcp.Core.Attention.ForegroundGate.Evaluate(
        targetRoot: target.Root,
        foregroundRoot: _env.GetForegroundRoot(),
        targetWindowId: windowId,
        resolveProcess: h => _env.ResolveRoot(h).ProcessName,
        ownerHwnd: h => _windows.OwnerHwnd(h),
        resolveTitle: h => _windows.WindowTitle(h));
    if (result is null) return null;
    _attention.Signal(new WindowHandle(windowId));       // flash (+ speak if autosound) — best-effort
    return ToolResponse.Ok(new { targetNotForeground = result });
}
```
(Grounding: `_env.ResolveRoot(h)` returns `PointTarget` whose process field is `.ProcessName`; `_env.GetForegroundRoot()` returns the live foreground GA_ROOT — both confirmed in `Win32PlatformEnvironment.cs`.)

- [ ] **Step 5: Insert the gate into `DesktopType`** — right before `await PreviewSyntheticAsync(...)` (grounding line ~197). `target` is already resolved above (focus was attempted during resolve):

```csharp
// SP-A not-foreground gate (keyboard path): if the resolved target isn't the OS foreground, return the
// enriched TargetNotForeground + flash — instead of the generic mid-send abort.
var fgReply = ForegroundGateReply(target, window);
if (fgReply is not null) return fgReply;

await PreviewSyntheticAsync(target, ElementRect(target));
await Task.Run(() => _guard.KeyType(text ?? string.Empty, target, interKeyDelayMs));
```

- [ ] **Step 6: Insert the gate into `DesktopKey`** — after `target` is resolved, before `PreviewSyntheticAsync` (grounding line ~361). Skip for the no-ref foreground path (there `target.Root` IS the foreground by construction, so `Evaluate` returns null; guard explicitly for clarity):

```csharp
// SP-A gate for the ref/selector keyboard path. The no-ref foreground path targets the foreground by
// construction, so the gate is a no-op there (Evaluate returns null); guard for clarity.
// `window!` is SAFE here (reviewed): the earlier DesktopKey guards (grounding InputTools.cs ~L334-339)
// already THROW InvalidArguments when haveRef/haveSel and window is null/empty, so inside this branch
// `window` is guaranteed non-null. desktop_key has no process/title targeting — only ref/window/selector.
if (haveRef || haveSel)
{
    var fgReply = ForegroundGateReply(target, window!);
    if (fgReply is not null) return fgReply;
}
await PreviewSyntheticAsync(target, ElementRect(target));
```

- [ ] **Step 7: Write the wiring test** (headless — assert the DI/serialization shape: a not-foreground evaluation round-trips through `ToolResponse.Ok` as a RESULT, not an error):

```csharp
using System.Text.Json;
using FlaUI.Mcp.Core.Attention;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class ForegroundGateWiringTests
{
    [Fact]
    public void TargetNotForeground_serializes_as_a_result_not_an_error()
    {
        var r = ForegroundGate.Evaluate(100, 200, "w1",
            resolveProcess: _ => "chrome", ownerHwnd: _ => 0)!;
        var json = ToolResponse.Ok(new { targetNotForeground = r });
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("targetNotForeground", out var tnf));
        Assert.False(doc.RootElement.TryGetProperty("error", out _));      // NOT the error channel
        Assert.Equal("w1", tnf.GetProperty("targetWindow").GetString());
        Assert.Equal("call-wait-for-foreground", tnf.GetProperty("recommendedAction").GetString());
        var cf = tnf.GetProperty("currentForeground");
        Assert.Equal("chrome", cf.GetProperty("process").GetString());
    }
}
```

- [ ] **Step 8: Build + full headless suite** — `dotnet build -clp:ErrorsOnly && dotnet test --filter "Category!=Desktop" --no-build` — Expected: PASS, no regression. If any existing `InputToolsTests` now returns `targetNotForeground` instead of its expected result, its fixture window was not foreground during the test — investigate that specific fixture (the gate only fires when `target.Root != foreground`); do NOT weaken the gate to make a test pass without understanding why.

- [ ] **Step 9: Commit**

```bash
git add src/FlaUI.Mcp.Server/ServerOptions.cs src/FlaUI.Mcp.Server/Program.cs src/FlaUI.Mcp.Server/Tools/InputTools.cs src/FlaUI.Mcp.Core/Windows/WindowManager.cs test/FlaUI.Mcp.Tests/Interaction/ForegroundGateWiringTests.cs
git commit -m "feat(attention): enriched TargetNotForeground gate in desktop_type/desktop_key + signal + DI (SP-A T6)"
```

---

### Task 7: Enrich `desktop_focus_window` with the same why-not

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Windows/WindowManager.cs` (add `FocusWithWhyNotAsync` + `FocusResult`)
- Modify: `src/FlaUI.Mcp.Server/Tools/WindowTools.cs` (`DesktopFocusWindow` surfaces `currentForeground`/`recommendedAction`; add `IPlatformEnvironment` dep)
- Test: `test/FlaUI.Mcp.Tests/Windows/FocusWhyNotTests.cs`

**Context (Step 0):** `FocusAsync(WindowHandle)` returns `Task<bool>` (`foregroundGained`) — grounding line ~464. `DesktopFocusWindow` returns `ToolResponse.Ok(new { ok = true, foregroundGained = gained })` — grounding `WindowTools.cs` line ~52-57. Current `WindowTools` ctor is `WindowTools(WindowManager windows, ServerOptions options)`. Confirm all three.

**Oracle:** spec §4.1 last bullet — `desktop_focus_window` returns the same enriched why-not (keeps `foregroundGained`, ADDS `currentForeground`/`recommendedAction` under the same leak rule). Backward-compatible: `foregroundGained` stays.

- [ ] **Step 1: Add `FocusResult` + `FocusWithWhyNotAsync`** to `WindowManager` (leave `FocusAsync` for internal callers):

```csharp
public readonly record struct FocusResult(bool ForegroundGained, IntPtr TargetHwnd, IntPtr ForegroundHwnd);

/// <summary>Focus + report the truth for the tool layer (spec §4.1): the gained bool PLUS the raw HWNDs so
/// the tool can build the leak-safe currentForeground. Runs on the query STA like FocusAsync.</summary>
public Task<FocusResult> FocusWithWhyNotAsync(WindowHandle handle) =>
    RunOnWindowAsync(handle, w =>
    {
        w.Focus(); w.SetForeground();
        IntPtr hwnd = IntPtr.Zero;
        try { hwnd = w.Properties.NativeWindowHandle.ValueOrDefault; } catch { }
        var fg = GetForegroundWindow();
        return new FocusResult(hwnd != IntPtr.Zero && fg == hwnd, hwnd, fg);
    });
```

- [ ] **Step 2: Write the failing test** (the leak-safe shaping is `ForegroundGate.Evaluate`, already covered; here assert the tool composes it — test the mapping helper `WindowTools.FocusReply`):

```csharp
using System.Text.Json;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Windows;

public class FocusWhyNotTests
{
    [Fact]
    public void Gained_reply_has_no_whynot()
    {
        var json = WindowTools.FocusReply(new FocusResult(true, 100, 100), "w1",
            _ => "notepad", _ => 0, _ => null);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("foregroundGained").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("currentForeground", out _));
    }

    [Fact]
    public void Not_gained_reply_carries_leaksafe_whynot()
    {
        var json = WindowTools.FocusReply(new FocusResult(false, 100, 200), "w1",
            h => h == 200 ? "chrome" : "notepad", _ => 0, _ => null);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("foregroundGained").GetBoolean());
        var cf = doc.RootElement.GetProperty("currentForeground");
        Assert.Equal("chrome", cf.GetProperty("process").GetString());
        Assert.Equal("call-wait-for-foreground", doc.RootElement.GetProperty("recommendedAction").GetString());
    }
}
```

- [ ] **Step 3: Run test to verify it fails** — Expected: FAIL (`FocusReply` / `FocusResult` missing).

- [ ] **Step 4: Implement `WindowTools.FocusReply` + rewire `DesktopFocusWindow`**:

```csharp
// SP-A: build the focus reply. On success just { ok, foregroundGained }. On the foreground-lock ceiling,
// ADD the leak-safe currentForeground + recommendedAction (spec §4.1), reusing ForegroundGate.
public static string FocusReply(FocusResult fr, string windowId,
    System.Func<nint, string?> resolveProcess, System.Func<nint, nint> ownerHwnd, System.Func<nint, string?> resolveTitle)
{
    if (fr.ForegroundGained)
        return ToolResponse.Ok(new { ok = true, foregroundGained = true });
    var g = FlaUI.Mcp.Core.Attention.ForegroundGate.Evaluate(
        fr.TargetHwnd, fr.ForegroundHwnd, windowId, resolveProcess, ownerHwnd, resolveTitle);
    return ToolResponse.Ok(new
    {
        ok = true,
        foregroundGained = false,
        currentForeground = g!.CurrentForeground,
        recommendedAction = g.RecommendedAction,
        recovery = g.Recovery,
    });
}
```

`WindowTools` ctor gains `IPlatformEnvironment env` (store as `_env`); `DesktopFocusWindow` body becomes:
```csharp
=> ToolResponse.GuardWrite(_options, async () =>
{
    var fr = await _windows.FocusWithWhyNotAsync(new WindowHandle(window));
    return WindowTools.FocusReply(fr, window,
        h => _env.ResolveRoot(h).ProcessName, _windows.OwnerHwnd, _windows.WindowTitle);
});
```
(`IPlatformEnvironment` is DI-registered — `Program.cs` line ~53. `WindowTools` is `AddSingleton<WindowTools>()`, so the container resolves the new ctor param automatically. Add `using FlaUI.Mcp.Core.Interaction;` to `WindowTools.cs`.)

- [ ] **Step 5: Build + headless suite** — `dotnet build -clp:ErrorsOnly && dotnet test --filter "Category!=Desktop" --no-build` — Expected: PASS. Existing `WindowTools`/`WindowManager` tests: `foregroundGained` preserved, so no consumer breaks. If any test constructs `WindowTools(windows, options)` directly with two args, update it to pass a platform env (real `Win32PlatformEnvironment` or a fake).

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Core/Windows/WindowManager.cs src/FlaUI.Mcp.Server/Tools/WindowTools.cs test/FlaUI.Mcp.Tests/Windows/FocusWhyNotTests.cs
git commit -m "feat(attention): desktop_focus_window enriched why-not (currentForeground/recommendedAction) (SP-A T7)"
```

---

### Task 8: `desktop_wait_for_foreground` — blocking resume primitive

**Files:**
- Create: `src/FlaUI.Mcp.Core/Attention/WaitForForeground.cs` (pure cap/single-waiter decisions + `IForegroundWaiter` seam + `WaitResult`)
- Create: `src/FlaUI.Mcp.Server/Attention/Win32ForegroundWaiter.cs` (real `SetWinEventHook` waiter on a dedicated thread)
- Modify: `src/FlaUI.Mcp.Server/Program.cs` (DI-bind the waiter + gate)
- Modify: `src/FlaUI.Mcp.Server/Tools/WindowTools.cs` (the tool)
- Test: `test/FlaUI.Mcp.Tests/Attention/WaitForForegroundTests.cs`

**Context (Step 0):** Confirm there is no existing `desktop_wait_for_foreground` tool (grep). Confirm `WindowManager.TryGetHwnd` exists (added T4). The other `desktop_wait_for_*` tools (`desktop_wait_for`, `desktop_wait_for_stable`, `desktop_wait_for_text`) live in their own tool type — grep `desktop_wait_for` to find which type hosts them and place the new tool there for consistency (if it's a `SnapshotTools`/dedicated wait type rather than `WindowTools`, add it there; the ctor deps below apply wherever it lands). Confirm `TooManyPendingActions` is an existing `ToolErrorCode` value (grounding: it is, in `ToolErrorCode.cs`).

**Oracle:** spec §4.5 — event-driven on a **dedicated non-STA thread** (`SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` + `EVENT_OBJECT_DESTROY`), **never** `Thread.Sleep`/spin on a shared STA; `timeoutMs` **server-capped to 45000**; `MaxConcurrentWaiters = 1`; **lease-exempt**; returns `{ foregroundGained, reason: "gained"|"timeout"|"window-destroyed", currentForeground }`.

- [ ] **Step 1: Write the failing test** (pure parts: cap clamp + single-waiter gate):

```csharp
using FlaUI.Mcp.Core.Attention;
using Xunit;

namespace FlaUI.Mcp.Tests.Attention;

public class WaitForForegroundTests
{
    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(45000, 45000)]
    [InlineData(300000, 45000)]   // over cap → clamped
    [InlineData(0, 45000)]        // 0 / absent → the cap (default)
    [InlineData(-5, 45000)]       // garbage → cap
    public void Timeout_is_capped_at_45s(int requested, int expected)
        => Assert.Equal(expected, WaitForForeground.ClampTimeout(requested));

    [Fact]
    public void Single_waiter_gate_rejects_a_concurrent_second_waiter()
    {
        var gate = new WaitForForeground.WaiterGate();
        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());   // MaxConcurrentWaiters = 1
        gate.Exit();
        Assert.True(gate.TryEnter());
    }
}
```

- [ ] **Step 2: Run test to verify it fails** — Expected: FAIL.

- [ ] **Step 3: Implement the pure core + seam**

```csharp
using System;
using System.Threading;

namespace FlaUI.Mcp.Core.Attention;

/// <summary>Why a wait ended (spec §4.5).</summary>
public enum WaitReason { Gained, Timeout, WindowDestroyed }

/// <summary>The blocking wait outcome; currentForeground filled leak-safe by the tool.</summary>
public readonly record struct WaitResult(bool ForegroundGained, WaitReason Reason);

/// <summary>The event-driven waiter seam. The real impl (Win32ForegroundWaiter) hooks
/// EVENT_SYSTEM_FOREGROUND + EVENT_OBJECT_DESTROY on a dedicated non-STA thread; a fake drives tests.</summary>
public interface IForegroundWaiter
{
    WaitResult Wait(IntPtr targetHwnd, int timeoutMs);
}

public static class WaitForForeground
{
    public const int HardCapMs = 45000;

    /// <summary>Clamp the requested timeout to (0, HardCap]; 0/negative/over-cap → HardCap (spec §4.5).</summary>
    public static int ClampTimeout(int requestedMs)
        => requestedMs > 0 && requestedMs <= HardCapMs ? requestedMs : HardCapMs;

    /// <summary>MaxConcurrentWaiters = 1 (spec §4.5 DoS guard). Non-reentrant single-slot gate.</summary>
    public sealed class WaiterGate
    {
        private int _busy;
        public bool TryEnter() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;
        public void Exit() => Interlocked.Exchange(ref _busy, 0);
    }
}
```

- [ ] **Step 4: Run test to verify it passes** — `dotnet build -clp:ErrorsOnly && dotnet test --filter "FullyQualifiedName~WaitForForegroundTests" --no-build` — Expected: PASS.

- [ ] **Step 5: Implement the real Win32 waiter** (`src/FlaUI.Mcp.Server/Attention/Win32ForegroundWaiter.cs`):

```csharp
using System;
using System.Runtime.InteropServices;
using System.Threading;
using FlaUI.Mcp.Core.Attention;

namespace FlaUI.Mcp.Server.Attention;

/// <summary>Real IForegroundWaiter (spec §4.5): runs a private message loop on a DEDICATED background thread
/// (NOT the query/action STA — hooking there would starve every other tool) and installs a WinEvent hook for
/// EVENT_SYSTEM_FOREGROUND + EVENT_OBJECT_DESTROY. Returns as soon as the target gains foreground, the target
/// HWND is destroyed, or the timeout elapses.</summary>
public sealed class Win32ForegroundWaiter : IForegroundWaiter
{
    public WaitResult Wait(IntPtr targetHwnd, int timeoutMs)
    {
        if (GetForegroundWindow() == targetHwnd) return new WaitResult(true, WaitReason.Gained);
        if (!IsWindow(targetHwnd)) return new WaitResult(false, WaitReason.WindowDestroyed);

        WaitResult result = new(false, WaitReason.Timeout);
        var done = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            WinEventDelegate cb = (hook, ev, hwnd, idObj, idChild, thr, time) =>
            {
                if (ev == EVENT_SYSTEM_FOREGROUND && GetForegroundWindow() == targetHwnd)
                { result = new WaitResult(true, WaitReason.Gained); PostQuitMessage(0); }
                else if (ev == EVENT_OBJECT_DESTROY && hwnd == targetHwnd && idObj == OBJID_WINDOW)
                { result = new WaitResult(false, WaitReason.WindowDestroyed); PostQuitMessage(0); }
            };
            var hFg = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, cb, 0, 0, WINEVENT_OUTOFCONTEXT);
            var hDes = SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_DESTROY, IntPtr.Zero, cb, 0, 0, WINEVENT_OUTOFCONTEXT);
            // Re-check after hooks are installed (close the race where it gained/died between the fast path and here).
            if (GetForegroundWindow() == targetHwnd) result = new WaitResult(true, WaitReason.Gained);
            else if (!IsWindow(targetHwnd)) result = new WaitResult(false, WaitReason.WindowDestroyed);
            else
            {
                var timer = SetTimer(IntPtr.Zero, IntPtr.Zero, (uint)timeoutMs, IntPtr.Zero);
                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    // Match OUR timer id (SEAT-C fold): STA initializes COM, which can post its own
                    // WM_TIMER to this thread's queue; accepting any WM_TIMER could abort the wait early.
                    if (msg.message == WM_TIMER && msg.wParam == timer) break;   // our timeout only
                    TranslateMessage(ref msg); DispatchMessage(ref msg);
                }
                if (timer != IntPtr.Zero) KillTimer(IntPtr.Zero, timer);
            }
            if (hFg != IntPtr.Zero) UnhookWinEvent(hFg);
            if (hDes != IntPtr.Zero) UnhookWinEvent(hDes);
            GC.KeepAlive(cb);
            done.Set();
        }) { IsBackground = true, Name = "flaui-mcp-fg-wait" };
        thread.SetApartmentState(ApartmentState.STA); // WinEvent hooks want a message pump; STA on a DEDICATED thread is safe
        thread.Start();
        done.Wait(timeoutMs + 2000);                  // hard ceiling so a wedged hook can't leak the caller
        return result;
    }

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_OBJECT_DESTROY = 0x8001, WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int OBJID_WINDOW = 0; private const uint WM_TIMER = 0x0113;
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventDelegate cb, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, IntPtr id);
}
```

- [ ] **Step 6: DI-bind + add the tool.** In `Program.cs`: `builder.Services.AddSingleton<FlaUI.Mcp.Core.Attention.IForegroundWaiter, FlaUI.Mcp.Server.Attention.Win32ForegroundWaiter>();` and `builder.Services.AddSingleton<FlaUI.Mcp.Core.Attention.WaitForForeground.WaiterGate>();`. Add the tool to the host type (ctor gains `IForegroundWaiter _foregroundWaiter`, `WaitForForeground.WaiterGate _waiterGate`, `IAttentionSignal _attention`, and — if not already present — `WindowManager _windows`, `IPlatformEnvironment _env`):

```csharp
[McpServerTool(ReadOnly = true), Description("Block until a window gains the OS foreground (the human clicks it), the window is closed, or timeout. Flashes the window first. Lease-EXEMPT (no synthetic input). timeoutMs is server-capped to 45s — on a \"timeout\" result, CALL THIS TOOL AGAIN to keep waiting (do NOT yield the chat turn). Returns { foregroundGained, reason: \"gained\"|\"timeout\"|\"window-destroyed\", currentForeground }. Use after a targetNotForeground result to let the human bring your target forward.")]
public Task<string> DesktopWaitForForeground(
    [Description("Window handle, e.g. w1.")] string window,
    [Description("Max ms to block (server-capped to 45000).")] int timeoutMs = 45000)
    => ToolResponse.Guard(() =>
    {
        if (!_windows.TryGetHwnd(new WindowHandle(window), out var hwnd))
            throw new ToolException(ToolErrorCode.WindowHandleStale, $"Handle {window} is no longer valid.", "re-list windows and re-open");
        if (!_waiterGate.TryEnter())
            throw new ToolException(ToolErrorCode.TooManyPendingActions, "Another wait_for_foreground is already in progress.", "wait for it to finish, or retry shortly");
        try
        {
            _attention.Signal(new WindowHandle(window));                 // flash (+ speak if autosound)
            var r = _foregroundWaiter.Wait(hwnd, WaitForForeground.ClampTimeout(timeoutMs));
            var fg = _env.GetForegroundRoot();
            // SEAT-D fold: build currentForeground via the SHARED leak-safe helper so this tool's shape is
            // IDENTICAL to desktop_type/desktop_focus_window — incl. the owner-modal title rule (an owned
            // "Save As" modal blocking the target discloses its title; any unrelated window stays process-only).
            var cf = FlaUI.Mcp.Core.Attention.ForegroundGate.DescribeForeground(
                foregroundRoot: fg, targetRoot: hwnd,
                resolveProcess: h => _env.ResolveRoot(h).ProcessName,
                ownerHwnd: _windows.OwnerHwnd, resolveTitle: _windows.WindowTitle);
            return Task.FromResult(ToolResponse.Ok(new
            {
                foregroundGained = r.ForegroundGained,
                reason = r.Reason switch { WaitReason.Gained => "gained", WaitReason.WindowDestroyed => "window-destroyed", _ => "timeout" },
                currentForeground = cf,
            }));
        }
        finally { _waiterGate.Exit(); }
    });
```

- [ ] **Step 7: Build + full headless suite** — `dotnet build -clp:ErrorsOnly && dotnet test --filter "Category!=Desktop" --no-build` — Expected: PASS (pure cap + gate tests green; the real waiter is exercised at the Desktop smoke).

- [ ] **Step 8: Commit**

```bash
git add src/FlaUI.Mcp.Core/Attention/WaitForForeground.cs src/FlaUI.Mcp.Server/Attention/Win32ForegroundWaiter.cs src/FlaUI.Mcp.Server/Program.cs src/FlaUI.Mcp.Server/Tools/WindowTools.cs test/FlaUI.Mcp.Tests/Attention/WaitForForegroundTests.cs
git commit -m "feat(attention): desktop_wait_for_foreground event-driven resume primitive (SP-A T8)"
```

---

### Task 9: `flaui-mcp autosound on|off` + non-destructive multi-flag config merge

**Files:**
- Create: `src/FlaUI.Mcp.Server/Install/ConfigArgsMerge.cs`
- Modify: `src/FlaUI.Mcp.Server/Install/McpServerEntry.cs` (add `AutosoundArgs`)
- Modify: `src/FlaUI.Mcp.Server/Install/CliRouter.cs` (`autosound` verb; migrate `overlay` to the merge)
- Modify: `AgyConfigWriter.cs`, `ClaudeCodeConfigWriter.cs`, `GenericMcpConfigWriter.cs` (merge overload)
- Test: `test/FlaUI.Mcp.Tests/Install/ConfigArgsMergeTests.cs`, extend `test/FlaUI.Mcp.Tests/Install/CliRouterTests.cs`

**Context (Step 0 — CRITICAL):** Grounding shows the CURRENT `overlay` verb passes `McpServerEntry.OverlayArgs` as `extra` into `Apply(...)` → `McpServerEntry.ForExe(exePath, extra).ToJsonNode()` which **replaces the entire `args` array**. So `overlay on` then `autosound on` today would DROP `--overlay`. This task introduces a **non-destructive flag merge** (spec §4.4 ops fold / SP-B §3.2) shared by all flag verbs. Confirm `McpServerEntry.OverlayArgs` is `{ "--overlay", "--overlay-ms=800" }` and `ForExe(exePath, args).ToJsonNode()` builds the full `args` array (grounding: `McpServerEntry.cs` lines 12-21). STOP + `STATE_MISMATCH` if not.

**Oracle:** spec §4.4 — `autosound on` re-registers with `--autosound`; off removes it; off by default; CLI output must say "run `/mcp` to reconnect". Merge preserves sibling flags.

- [ ] **Step 1: Write the failing merge test**

```csharp
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class ConfigArgsMergeTests
{
    [Fact]
    public void Adds_flag_group_preserving_siblings()
    {
        var merged = ConfigArgsMerge.Apply(new[] { "--overlay", "--overlay-ms=800" },
            add: new[] { "--autosound" }, removeAnyOf: new[] { "--autosound" });
        Assert.Contains("--overlay", merged);
        Assert.Contains("--overlay-ms=800", merged);
        Assert.Contains("--autosound", merged);
    }

    [Fact]
    public void Removes_only_the_named_group()
    {
        var merged = ConfigArgsMerge.Apply(new[] { "--overlay", "--overlay-ms=800", "--autosound" },
            add: System.Array.Empty<string>(), removeAnyOf: new[] { "--autosound" });
        Assert.Contains("--overlay", merged);
        Assert.DoesNotContain("--autosound", merged);
    }

    [Fact]
    public void Is_idempotent_no_duplicate_flags()
    {
        var merged = ConfigArgsMerge.Apply(new[] { "--autosound" },
            add: new[] { "--autosound" }, removeAnyOf: new[] { "--autosound" });
        Assert.Single(System.Array.FindAll(merged, a => a == "--autosound"));
    }

    [Fact]
    public void Group_removal_drops_bare_and_valued_members_by_prefix()
    {
        // The --overlay GROUP has two distinct members: bare "--overlay" and valued "--overlay-ms=N".
        // "--overlay-ms=800" does NOT start with "--overlay=" (it starts with "--overlay-ms="), so the
        // removal list MUST name BOTH prefixes (SEAT-E fold). This is the exact list the overlay verb uses.
        var merged = ConfigArgsMerge.Apply(new[] { "--overlay", "--overlay-ms=800", "--autosound" },
            add: System.Array.Empty<string>(), removeAnyOf: new[] { "--overlay", "--overlay-ms" });
        Assert.DoesNotContain("--overlay", merged);
        Assert.DoesNotContain("--overlay-ms=800", merged);
        Assert.Contains("--autosound", merged);
    }
}
```

- [ ] **Step 2: Run test to verify it fails** — Expected: FAIL (`ConfigArgsMerge` missing).

- [ ] **Step 3: Implement `ConfigArgsMerge`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace FlaUI.Mcp.Server.Install;

/// <summary>Non-destructive merge of the server's launch-arg flags (spec §4.4 ops fold). The CLI now manages
/// several independent flag groups (--overlay[/-ms], --autosound, --presence[/thresholds]); a verb must
/// inject/remove ONLY its own group and preserve the others. `removeAnyOf` matches a flag OR any flag that
/// starts with `flag` + "=" (so removing "--overlay" also drops "--overlay-ms=800" — same group by prefix).</summary>
public static class ConfigArgsMerge
{
    public static string[] Apply(IReadOnlyList<string>? existing, IReadOnlyList<string> add, IReadOnlyList<string> removeAnyOf)
    {
        var result = new List<string>();
        foreach (var a in existing ?? Array.Empty<string>())
            if (!removeAnyOf.Any(r => a == r || a.StartsWith(r + "=", StringComparison.Ordinal)) && !add.Contains(a))
                result.Add(a);
        result.AddRange(add);
        return result.ToArray();
    }
}
```

- [ ] **Step 4: Run test to verify it passes** — `dotnet build -clp:ErrorsOnly && dotnet test --filter "FullyQualifiedName~ConfigArgsMergeTests" --no-build` — Expected: PASS.

- [ ] **Step 5: Add a merge overload to each config writer.** Each writer already loads the config with `JsoncFile.Load` and reads `servers[ServerName] as JsonObject existing`. Add an overload:
  `AgentResult Install(string exePath, string[] addArgs, string[] removeArgs)` that:
  1. loads the config, reads the existing `mcpServers.flaui-mcp.args` JsonArray into `string[]` (empty if absent),
  2. `var merged = ConfigArgsMerge.Apply(existing, addArgs, removeArgs);`
  3. writes `McpServerEntry.ForExe(exePath, merged).ToJsonNode()` (+ the permission edit, unchanged for `AgyConfigWriter`).

  Keep the existing `Install(exePath, extraArgs)` for `install`/`uninstall` (unchanged). Extract the "read existing args" step as a small local helper. Do this identically in all three writers (`AgyConfigWriter`, `ClaudeCodeConfigWriter`, `GenericMcpConfigWriter`). For `GenericMcpConfigWriter.Install(path, exePath, extraArgs)` the merge overload is `Install(string path, string exePath, string[] addArgs, string[] removeArgs)`.

- [ ] **Step 6: Add `autosound` verb + migrate `overlay` in `CliRouter`.** Add `"autosound"` to the `Verbs` set. Add an `ApplyMerge(agent, paths, exePath, add, remove)` sibling of `Apply(...)` that dispatches to each writer's merge overload. Add the `autosound` case:

```csharp
case "autosound":
{
    var mode = args.Length > 1 ? args[1].ToLowerInvariant() : "";
    if (mode != "on" && mode != "off")
    { outp.WriteLine("usage: flaui-mcp autosound on|off [--agent agy|claude|generic|all]"); return 2; }
    var add = mode == "on" ? new[] { "--autosound" } : System.Array.Empty<string>();
    var remove = new[] { "--autosound" };
    foreach (var r in ApplyMerge(agent, paths, exePath, add, remove))
        outp.WriteLine($"[{r.Agent}] {r.Change}: {r.Detail}");
    outp.WriteLine($"Autosound {mode.ToUpperInvariant()}. Reconnect the MCP client (/mcp) to apply; restart agy if you use it.");
    return 0;
}
```
Migrate the existing `overlay` case to `ApplyMerge` with `add = mode=="on" ? McpServerEntry.OverlayArgs.ToArray() : empty`, `remove = new[]{ "--overlay", "--overlay-ms" }` — **both** group members (SEAT-E fold: `--overlay-ms=800` does not match the `--overlay=` prefix, so omitting `--overlay-ms` would orphan and duplicate the valued flag on every toggle). So overlay+autosound now coexist (the fix the fold requires). Update `PrintHelp` to document `autosound on|off`.

- [ ] **Step 7: Extend `CliRouterTests`** — assert `overlay on` then `autosound on` against one temp `--config` yields a config whose `mcpServers.flaui-mcp.args` contains BOTH `--overlay` and `--autosound` (the non-destructive property). Grounding: `CliRouterTests.cs` already drives verbs against a temp config path — follow its pattern (`FLAUI_MCP_DATA_DIR` / `--config`).

- [ ] **Step 8: Build + full headless suite** — Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/ test/FlaUI.Mcp.Tests/Install/
git commit -m "feat(install): autosound on|off + non-destructive multi-flag config merge (SP-A T9)"
```

---

### Task 10: Long-lease disclaiming warning + `--accept-risk`

**Files:**
- Create: `src/FlaUI.Mcp.Server/Lease/LeaseWarning.cs`
- Modify: `src/FlaUI.Mcp.Server/Install/CliRouter.cs` (the `unlock` case)
- Test: `test/FlaUI.Mcp.Tests/Server/LongLeaseWarningTests.cs`

**Context (Step 0):** `LeaseWriter.Grant(int minutes, bool allowShells)` returns a status string; called from `CliRouter` `unlock` case (grounding lines ~68-73): `var minutes = int.TryParse(OptionValue(args, "--minutes"), out var m) ? m : 5; outp.WriteLine(Lease.LeaseWriter.Grant(minutes, HasFlag(args, "--allow-shells")));`. Confirm both.

**Oracle:** spec §4.6 — on a lease **> 60 min**, print the exact honest safety-DISCLAIMING warning and require `'I understand'` (interactive) OR `--accept-risk`/`--i-understand` (non-interactive, prints the same text to the log then proceeds); absence of BOTH a TTY and the flag on a long-lease request → refuse. Short leases stay frictionless.

- [ ] **Step 1: Write the failing test**

```csharp
using FlaUI.Mcp.Server.Lease;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

public class LongLeaseWarningTests
{
    [Theory]
    [InlineData(30, false, false, LeaseWarningDecision.NoWarning)]   // short → frictionless even non-interactive
    [InlineData(60, false, false, LeaseWarningDecision.NoWarning)]   // threshold is > 60, so exactly 60 is fine
    [InlineData(120, true, false, LeaseWarningDecision.ProceedWithLoggedWarning)]  // flag acks non-interactively
    [InlineData(120, false, true, LeaseWarningDecision.ProceedWithLoggedWarning)]  // TTY: prompt handled by caller
    [InlineData(120, false, false, LeaseWarningDecision.RefuseNeedsAck)]           // no TTY, no flag → refuse
    public void Decide(int minutes, bool flag, bool tty, LeaseWarningDecision expected)
        => Assert.Equal(expected, LeaseWarning.Decide(minutes, flag, tty));

    [Fact]
    public void Warning_text_discloses_no_sandbox_and_names_the_minutes()
    {
        var t = LeaseWarning.Text(999);
        Assert.Contains("999", t);
        Assert.Contains("NO sandboxing", t);
        Assert.Contains("I understand", t);
    }
}
```

- [ ] **Step 2: Run test to verify it fails** — Expected: FAIL.

- [ ] **Step 3: Implement `LeaseWarning`**

```csharp
namespace FlaUI.Mcp.Server.Lease;

public enum LeaseWarningDecision { NoWarning, ProceedWithLoggedWarning, RefuseNeedsAck }

/// <summary>Pure policy for the long-lease disclaiming warning (spec §4.6). Threshold: > 60 minutes.</summary>
public static class LeaseWarning
{
    public const int ThresholdMinutes = 60;

    public static LeaseWarningDecision Decide(int minutes, bool hasAcceptFlag, bool isInteractive)
    {
        if (minutes <= ThresholdMinutes) return LeaseWarningDecision.NoWarning;
        if (hasAcceptFlag || isInteractive) return LeaseWarningDecision.ProceedWithLoggedWarning;
        return LeaseWarningDecision.RefuseNeedsAck;
    }

    public static string Text(int minutes) =>
        $"WARNING: You are granting an uncontained {minutes}-minute input lease. FlaUI.Mcp provides NO " +
        "sandboxing or protection during this window. A prompt-injected agent can take full control of this " +
        "machine and your credentials. Only an ephemeral VM or low-privilege guest account can contain this " +
        "risk. Type 'I understand' to continue.";
}
```

- [ ] **Step 4: Wire it into the `unlock` case in `CliRouter`** (replace the current case body):

```csharp
case "unlock":
{
    var minutes = int.TryParse(OptionValue(args, "--minutes"), out var m) ? m : 5;
    bool acceptFlag = HasFlag(args, "--accept-risk") || HasFlag(args, "--i-understand");
    bool interactive = !Console.IsInputRedirected; // a real TTY; redirected stdin (CI) is non-interactive
    switch (Lease.LeaseWarning.Decide(minutes, acceptFlag, interactive))
    {
        case Lease.LeaseWarningDecision.RefuseNeedsAck:
            outp.WriteLine(Lease.LeaseWarning.Text(minutes));
            outp.WriteLine("Refusing a long lease without acknowledgment. Re-run with --accept-risk (non-interactive) or from a terminal.");
            return 2;
        case Lease.LeaseWarningDecision.ProceedWithLoggedWarning:
            outp.WriteLine(Lease.LeaseWarning.Text(minutes)); // on record in the log
            if (interactive && !acceptFlag)
            {
                var line = Console.ReadLine();
                if (!string.Equals(line?.Trim(), "I understand", StringComparison.Ordinal))
                { outp.WriteLine("Not acknowledged; lease not granted."); return 2; }
            }
            break;
        case Lease.LeaseWarningDecision.NoWarning: break;
    }
    outp.WriteLine(Lease.LeaseWriter.Grant(minutes, HasFlag(args, "--allow-shells")));
    return 0;
}
```
Update `PrintHelp` `unlock` line to mention `--accept-risk` for long non-interactive leases. (`LeaseWriter.Grant`'s `Math.Clamp(minutes,1,1440)` is unchanged — the warning is orthogonal.)

- [ ] **Step 5: Build + headless suite** — Expected: PASS. Existing `LeaseCliTests` short-lease cases still pass (≤ 60 min = `NoWarning`, no behavior change). Note: any existing `LeaseCliTests` that grants a long lease non-interactively must now pass `--accept-risk` — update those fixtures if present.

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Server/Lease/LeaseWarning.cs src/FlaUI.Mcp.Server/Install/CliRouter.cs test/FlaUI.Mcp.Tests/Server/LongLeaseWarningTests.cs
git commit -m "feat(lease): long-lease disclaiming warning + --accept-risk non-interactive ack (SP-A T10)"
```

---

### Task 11: Docs + version bump

**Files:**
- Modify: `README.md`, `CHANGELOG.md`, `ROADMAP.md`, `.claude/skills/driving-flaui-mcp/SKILL.md`, the version-source file.

**Context (Step 0):** Grep for the current version string (`0.11.2`) to find the authoritative version property file (check `Directory.Build.props` or the Server `.csproj` for `<Version>`/`AppVersion`). Confirm `README.md` has a tools list and an overlay/"Watching & auditing" section to mirror for `autosound`. **README-before-push gate** (project rule): the README MUST reflect the new tools before any push.

**Oracle:** spec §6 (wire-contract change documented in CHANGELOG + driving skill) + the repo's "update README before every push" rule.

- [ ] **Step 1:** Bump the version property `0.11.2 → 0.12.0` in its authoritative file.
- [ ] **Step 2:** `CHANGELOG.md` — add `## [0.12.0] - 2026-07-05` with: enriched `targetNotForeground` result on `desktop_type`/`desktop_key` (replaces the generic abort for the not-foreground cause; clicks unchanged); `desktop_focus_window` now returns `currentForeground`/`recommendedAction` when the lock blocks it (additive; `foregroundGained` preserved); new `desktop_wait_for_foreground` tool; new `flaui-mcp autosound on|off` (off by default, flash always on); non-destructive multi-flag config merge (overlay/autosound now coexist); long-lease disclaiming warning + `--accept-risk`.
- [ ] **Step 3:** `README.md` — add `desktop_wait_for_foreground` to the tools list; document the `targetNotForeground` result shape + the wait pattern; add an `autosound on|off` note beside the overlay docs (off by default; `/mcp` reconnect to apply); note the long-lease warning + `--accept-risk`.
- [ ] **Step 4:** `.claude/skills/driving-flaui-mcp/SKILL.md` — document: on a `targetNotForeground` result (or `desktop_focus_window` with `foregroundGained:false`), call `desktop_wait_for_foreground` (do NOT yield the chat turn); the leak-safe `currentForeground` (process only). Keep version-agnostic (no product version stamps).
- [ ] **Step 5:** `ROADMAP.md` — add the SP-A "Human-Attention Toolset" entry.
- [ ] **Step 6:** Final full build + headless suite: `dotnet build -clp:ErrorsOnly && dotnet test --filter "Category!=Desktop" --no-build` — Expected: `0 Warning(s) 0 Error(s)`, `Failed: 0`.
- [ ] **Step 7: Commit**

```bash
git add README.md CHANGELOG.md ROADMAP.md .claude/skills/driving-flaui-mcp/SKILL.md <version-file>
git commit -m "docs+release(0.12.0): SP-A human-attention toolset — wait_for_foreground, autosound, enriched not-foreground (SP-A T11)"
```

---

## Self-review (author) — spec coverage

- §4.1 enriched `TargetNotForeground` (leak-safe, owner-only title, recommendedAction routes to the tool) → T1 + T6; `desktop_focus_window` variant → T7.
- §4.2 `IAttentionSignal` seam (Null + composable fan-out, never throws) → T2; DI → T6.
- §4.3 Flash (`FlashWindowEx`, `FLASHW_TIMERNOFG`) → T4.
- §4.4 TTS + `autosound on|off` (off default, reconnect-to-apply, channel-wide debounce, app-name-only utterance) → T3 (debounce) + T5 (channel) + T9 (verb) + T6 (DI).
- §4.5 `desktop_wait_for_foreground` (non-STA event-driven, 45s cap, single-waiter, destroy early-exit, lease-exempt) → T8.
- §4.6 long-lease disclaiming warning + `--accept-risk` → T10.
- §6 wire-contract (result via `ToolResponse`, generic error remains for the real mid-send steal, documented) → T6 (result-not-error test) + T11 (docs); the generic `Win32SyntheticInput.Reverify` is untouched.
- §4.4 non-destructive multi-flag merge → T9 (also retro-fixes overlay).

**Type consistency:** `TargetNotForeground`/`ForegroundIdentity` (T1) used unchanged in T6/T7; `WaitResult`/`WaitReason` (T8) consistent tool-side; `ConfigArgsMerge.Apply` signature stable across T9 (autosound) and SP-B (presence). `WindowManager.TryGetHwnd`/`OwnerHwnd`/`WindowTitle`/`TryGetAppName` added once (T4/T6), reused in T7/T8.

## Execution handoff

Two options: **(1) Subagent-Driven (recommended)** — dispatch a fresh subagent per task, spec-review then code-quality-review between tasks; **(2) Inline** — execute in-session with checkpoints. Model tiering: T1–T5, T9, T10 are well-specified mechanical/contained (cheaper model OK); T6, T7, T8 touch multi-file DI + Win32 concurrency (standard model); the Opus driver reviews between tasks. Desktop smoke (flash/TTS/wait/gate on a real non-foreground window) runs at the console after T11, before any branch→main decision.
