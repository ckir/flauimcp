# Phase 4a — Safety Stack Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build and prove the entire Phase-4 synthetic-input safety machinery (lease, deny-list/sink-interlock, session guard, budget, audit, elevation hard-fail) + the seam interfaces + the 2 carried SHOULD-FIX — shipping **no input-firing MCP tool** — all unit-testable on the headless box.

**Architecture:** A single `InputGuard` decision pipeline composes pure-over-injected dependencies behind three seams — `ISyntheticInput` (act), `IPlatformEnvironment` (Win32 probes), `ILeaseProvider` (lease+clock). 4a ships the interfaces, the file-backed lease + its `unlock`/`lock` CLI, and `InputGuard`, all driven in tests by in-memory fakes. The real Win32 leaves (`Win32SyntheticInput`, `Win32PlatformEnvironment`) are deferred to 4b, so nothing in 4a touches `SendInput` or a fixed-state OS probe — the "fully headless-testable" claim holds by construction. `InputGuard` is registered into DI only in 4b (it is a dormant, fully-tested engine in the v0.6.0 binary).

**Tech Stack:** C#/.NET 10 (`net10.0-windows`), xUnit, the MCP C# SDK. No new NuGet deps. Spec: `docs/superpowers/specs/2026-06-30-flaui-mcp-phase4-design.md`.

---

## File structure

**New (Core/Interaction):**
- `ISyntheticInput.cs` — act seam (verbs carry the expected target; 4b's real impl re-verifies atomically).
- `IPlatformEnvironment.cs` — read seam: foreground-root, coordinate→top-level hit-test, session-input state. Records: `PointTarget`, `SessionInputState`.
- `InputReverify.cs` — pure decision: expected-root vs actual-root → throw on mismatch (the leaf's re-verify logic, testable without Win32).
- `ILeaseProvider.cs` — lease read seam (current lease + its write-time).
- `InputLease.cs` — the lease record + `Parse`/`Format` of the `expiryUtc=…;sid=…;caps=…` line.
- `FileLeaseProvider.cs` — file-backed `ILeaseProvider`; reads `%LOCALAPPDATA%\FlaUI.Mcp\input.lease` (dir overridable for tests), `FileShare.ReadWrite` + retry.
- `ActionPolicy.cs` — classifies a target (process+class) as `Denied` / `Interlocked` / `Allowed`.
- `ActionBudget.cs` — per-window rolling rate limit; resets when the lease write-time advances.
- `InputAudit.cs` — event-only audit line (no payload text) to a `TextWriter`.
- `InputGuard.cs` — the pipeline (+ `ActionTarget` record).

**New (Core/Perception):**
- `RedactionPolicy.cs` — fail-closed IsPassword decision (SHOULD-FIX #2).

**New (Server):**
- `Lease/LeaseWriter.cs` — writes/removes the lease file for the CLI (shares the path logic with `FileLeaseProvider`).

**Modified:**
- `Core/Errors/ToolErrorCode.cs` — +4 codes.
- `Core/Perception/PerceptionManager.cs` — SHOULD-FIX #1 (+#2).
- `Server/ServerOptions.cs` — +`AllowElevation`.
- `Server/ElevationGuard.cs` — +`InputHardFailIfElevated` decision seam.
- `Server/Install/CliRouter.cs` — route `unlock` / `lock`.

**New tests (test/FlaUI.Mcp.Tests):**
- `Interaction/RecordingSyntheticInput.cs`, `Interaction/FakePlatformEnvironment.cs` (fakes).
- `Interaction/InputReverifyTests.cs`, `RecordingSyntheticInputTests.cs`, `InputLeaseTests.cs`, `FileLeaseProviderTests.cs`, `ActionPolicyTests.cs`, `ActionBudgetTests.cs`, `InputAuditTests.cs`, `InputGuardTests.cs`.
- `Server/LeaseCliTests.cs`, extend `Server/ElevationGuardTests.cs`.
- `Perception/PerceptionManagerShouldFixTests.cs` (the SHOULD-FIX decision — non-Desktop, pure mapping).

All new tests are **non-Desktop** (`Category!=Desktop`) — the CI-green oracle is `dotnet test -c Release --filter "Category!=Desktop"`.

---

## Task 1: New error codes

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Errors/ToolErrorCode.cs`
- Test: `test/FlaUI.Mcp.Tests/Errors/ToolExceptionTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `ToolExceptionTests.cs`:

```csharp
[Fact]
public void New_phase4a_codes_serialize_by_name()
{
    foreach (var code in new[] { ToolErrorCode.InputNotLeased, ToolErrorCode.InputDesktopUnavailable,
                                 ToolErrorCode.InputBudgetExceeded, ToolErrorCode.SinkInterlocked })
        Assert.Equal(code.ToString(), new ToolException(code, "m", "r").Code.ToString());
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~New_phase4a_codes_serialize_by_name"`
Expected: FAIL (compile error — codes not defined).

- [ ] **Step 3: Add the codes**

In `ToolErrorCode.cs`, append after `GridCellOutOfRange` (match existing style — no trailing comma):

```csharp
    ClipboardUnavailable,
    GridCellOutOfRange,
    InputNotLeased,
    InputDesktopUnavailable,
    InputBudgetExceeded,
    SinkInterlocked
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~New_phase4a_codes_serialize_by_name"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Errors/ToolErrorCode.cs test/FlaUI.Mcp.Tests/Errors/ToolExceptionTests.cs
git commit -m "feat(errors): add Phase-4a input error codes"
```

---

## Task 2: SHOULD-FIX — fail-closed password redaction (#2) + defensive grid reads (#1)

**Files:**
- Create: `src/FlaUI.Mcp.Core/Perception/RedactionPolicy.cs`
- Modify: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` (GetGridCellAsync ~82-112, GetTextAsync ~121-122)
- Test: `test/FlaUI.Mcp.Tests/Perception/PerceptionManagerShouldFixTests.cs` (new)

> Verified against current code: the grid-level reads (`RowCount`/`ColumnCount`/`GetItem`) sit in an outer try that catches **only** `UnauthorizedAccessException` → a COMException there leaks as `INTERNAL`; both methods do `bool isPwd=false; try{...}catch{}` → a throwing `IsPassword` read falls through fail-**open**. These can't be exercised through a real grid headlessly, so the test asserts the extracted fail-closed *decision*.

- [ ] **Step 1: Write the failing test (new file)**

```csharp
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class PerceptionManagerShouldFixTests
{
    [Fact]
    public void IsPassword_read_that_throws_is_treated_as_password_fail_closed()
    {
        Assert.True(RedactionPolicy.IsPasswordOrFailClosed(() => throw new System.Runtime.InteropServices.COMException()));
        Assert.True(RedactionPolicy.IsPasswordOrFailClosed(() => true));
        Assert.False(RedactionPolicy.IsPasswordOrFailClosed(() => false));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~PerceptionManagerShouldFixTests"`
Expected: FAIL (compile — `RedactionPolicy` not defined).

- [ ] **Step 3: Add the helper + apply both fixes**

Create `src/FlaUI.Mcp.Core/Perception/RedactionPolicy.cs`:

```csharp
namespace FlaUI.Mcp.Core.Perception;

/// <summary>Fail-closed password decision: if the IsPassword read throws, treat the value as a
/// password (redact), never fall through to reading the text. A false-positive only over-redacts a
/// non-password field — harmless; the safe default. (SHOULD-FIX #2 from Phase 3b-2.)</summary>
public static class RedactionPolicy
{
    public static bool IsPasswordOrFailClosed(System.Func<bool> read)
    {
        try { return read(); } catch { return true; }
    }
}
```

In `PerceptionManager.cs` GetGridCellAsync, replace the fail-open read (currently `bool isPwd = false; try { isPwd = cell.Properties.IsPassword.ValueOrDefault; } catch { }`):

```csharp
                bool isPwd = RedactionPolicy.IsPasswordOrFailClosed(() => cell.Properties.IsPassword.ValueOrDefault);
```

In `PerceptionManager.cs` GetTextAsync, replace the fail-open read (currently `bool isPwd = false; try { isPwd = el.Properties.IsPassword.ValueOrDefault; } catch { }`):

```csharp
            bool isPwd = RedactionPolicy.IsPasswordOrFailClosed(() => el.Properties.IsPassword.ValueOrDefault);
```

For SHOULD-FIX #1, add a COMException catch to GetGridCellAsync's outer try — immediately after the existing `catch (System.UnauthorizedAccessException) { ... }` block:

```csharp
            catch (System.Runtime.InteropServices.COMException)
            { throw new ToolException(ToolErrorCode.ElementNotActionable, "The grid provider threw while reporting its cells.", "re-snapshot the grid and retry"); }
```

(The deliberate `PatternUnsupported`/`GridCellOutOfRange` ToolExceptions are not COMExceptions, so they still propagate unmapped to `ToolResponse.Guard`.)

- [ ] **Step 4: Run tests + build**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "Category!=Desktop"`
Expected: PASS (new test green, no regressions); build 0 warn.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/RedactionPolicy.cs src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs test/FlaUI.Mcp.Tests/Perception/PerceptionManagerShouldFixTests.cs
git commit -m "fix(perception): fail-closed password redaction + defensive grid-level reads (3b-2 SHOULD-FIX)"
```

---

## Task 3: Seams — `ISyntheticInput` + `IPlatformEnvironment` + `InputReverify`

**Files:**
- Create: `src/FlaUI.Mcp.Core/Interaction/ISyntheticInput.cs`, `src/FlaUI.Mcp.Core/Interaction/IPlatformEnvironment.cs`, `src/FlaUI.Mcp.Core/Interaction/InputReverify.cs`
- Create: `test/FlaUI.Mcp.Tests/Interaction/FakePlatformEnvironment.cs`
- Test: `test/FlaUI.Mcp.Tests/Interaction/InputReverifyTests.cs`

> Define all three seams + the re-verify decision + the env fake FIRST, so the re-verifying recording fake (Task 4) can consume them (fixes the ordering hazard).

- [ ] **Step 1: Write the failing test**

```csharp
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class InputReverifyTests
{
    [Fact]
    public void Matching_root_passes()
        => InputReverify.AssertSameRoot(expected: (nint)7, actual: (nint)7); // no throw

    [Fact]
    public void Mismatched_root_aborts_with_ElementDisappeared()
    {
        var ex = Assert.Throws<ToolException>(() => InputReverify.AssertSameRoot((nint)7, (nint)9));
        Assert.Equal(ToolErrorCode.ElementDisappearedDuringAction, ex.Code);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~InputReverifyTests"`
Expected: FAIL (compile).

- [ ] **Step 3: Create the seams + the re-verify decision + the env fake**

`src/FlaUI.Mcp.Core/Interaction/ISyntheticInput.cs`:

```csharp
namespace FlaUI.Mcp.Core.Interaction;

/// <summary>The ACT half of the synthetic-input seam — the only boundary to the OS SendInput call.
/// Each verb carries the EXPECTED target so the real 4b impl (and the 4a recording fake) can run the
/// atomic pre-send re-verify (foreground-root / point hit-test) before the call. 4a ships only the
/// interface + the recording fake; the real Win32SyntheticInput is 4b.</summary>
public interface ISyntheticInput
{
    void KeyType(string text, nint expectedForegroundRoot);
    void KeyChord(string[] modifiers, string key, nint expectedForegroundRoot);
    void MouseClick(int physX, int physY, string button, int count, string[] modifiers, nint expectedRootAtPoint);
    void MouseDrag(int startX, int startY, int endX, int endY, string button, nint expectedRootAtEnd);
}
```

`src/FlaUI.Mcp.Core/Interaction/IPlatformEnvironment.cs`:

```csharp
namespace FlaUI.Mcp.Core.Interaction;

/// <summary>The READ half of the seam: every Win32 probe the guard/leaf needs, behind an interface so
/// each branch is deterministically assertable on a single-state CI runner. 4a ships only the interface
/// + the fake; the real Win32PlatformEnvironment is 4b (alongside Win32SyntheticInput).</summary>
public interface IPlatformEnvironment
{
    /// <summary>Top-level root (GA_ROOT) of the current foreground window, or 0.</summary>
    nint GetForegroundRoot();

    /// <summary>Top-level root (GA_ROOT) of the window under a physical screen point, with its owning
    /// process base-name (no ".exe") and window class — for the coordinate deny-list. Root 0 if none.</summary>
    PointTarget HitTestRoot(int physX, int physY);

    /// <summary>Whether synthetic input can actually reach the interactive user desktop right now
    /// (OpenInputDesktop succeeds AND a foreground window exists). Fail-closed when false.</summary>
    SessionInputState SessionState();
}

public readonly record struct PointTarget(nint Root, string? ProcessName, string? WindowClass);
public readonly record struct SessionInputState(bool CanDeliverInput);
```

`src/FlaUI.Mcp.Core/Interaction/InputReverify.cs`:

```csharp
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>The atomic pre-send re-verify DECISION (pure). The real 4b leaf (and the 4a recording fake)
/// call this with the live foreground/point root read just before the send; a mismatch means a
/// focus-steal / overlay moved in the gap → abort, fire nothing.</summary>
public static class InputReverify
{
    public static void AssertSameRoot(nint expected, nint actual)
    {
        if (expected != actual)
            throw new ToolException(ToolErrorCode.ElementDisappearedDuringAction,
                "The target's window lost focus / changed under the pointer just before sending.",
                "re-snapshot, re-focus the target, and retry");
    }
}
```

`test/FlaUI.Mcp.Tests/Interaction/FakePlatformEnvironment.cs`:

```csharp
using FlaUI.Mcp.Core.Interaction;

namespace FlaUI.Mcp.Tests.Interaction;

/// <summary>Scriptable IPlatformEnvironment for headless guard + fake-reverify tests.</summary>
public sealed class FakePlatformEnvironment : IPlatformEnvironment
{
    public nint ForegroundRoot { get; set; }
    public PointTarget PointResult { get; set; }
    public bool CanDeliver { get; set; } = true;

    public nint GetForegroundRoot() => ForegroundRoot;
    public PointTarget HitTestRoot(int x, int y) => PointResult;
    public SessionInputState SessionState() => new(CanDeliver);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~InputReverifyTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Interaction/ISyntheticInput.cs src/FlaUI.Mcp.Core/Interaction/IPlatformEnvironment.cs src/FlaUI.Mcp.Core/Interaction/InputReverify.cs test/FlaUI.Mcp.Tests/Interaction/FakePlatformEnvironment.cs test/FlaUI.Mcp.Tests/Interaction/InputReverifyTests.cs
git commit -m "feat(input): ISyntheticInput + IPlatformEnvironment + InputReverify seams"
```

---

## Task 4: `RecordingSyntheticInput` — re-verifying test fake

**Files:**
- Create: `test/FlaUI.Mcp.Tests/Interaction/RecordingSyntheticInput.cs`
- Test: `test/FlaUI.Mcp.Tests/Interaction/RecordingSyntheticInputTests.cs`

> The fake consults the injected `IPlatformEnvironment` and runs `InputReverify` BEFORE recording — so the atomic-re-verify ABORT path (focus stolen / point changed) is exercised headlessly, per spec §2. Key verbs check `GetForegroundRoot()`; mouse verbs check `HitTestRoot(...).Root`.

- [ ] **Step 1: Write the failing test**

```csharp
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class RecordingSyntheticInputTests
{
    [Fact]
    public void Records_when_the_expected_root_still_matches()
    {
        var env = new FakePlatformEnvironment { ForegroundRoot = (nint)1 };
        var rec = new RecordingSyntheticInput(env);
        rec.KeyType("hi", (nint)1);
        rec.KeyChord(new[] { "Ctrl" }, "S", (nint)1);
        Assert.Equal(new[] { "KeyType:hi", "KeyChord:Ctrl+S" }, rec.Calls);
    }

    [Fact]
    public void Aborts_without_recording_when_foreground_changed()
    {
        var env = new FakePlatformEnvironment { ForegroundRoot = (nint)2 };   // focus stolen
        var rec = new RecordingSyntheticInput(env);
        var ex = Assert.Throws<ToolException>(() => rec.KeyType("secret", (nint)1));
        Assert.Equal(ToolErrorCode.ElementDisappearedDuringAction, ex.Code);
        Assert.Empty(rec.Calls);
    }

    [Fact]
    public void Mouse_verbs_reverify_against_the_hit_test_root()
    {
        var env = new FakePlatformEnvironment { PointResult = new((nint)5, "notepad", "Notepad") };
        var rec = new RecordingSyntheticInput(env);
        rec.MouseClick(10, 20, "left", 1, System.Array.Empty<string>(), (nint)5);   // matches → records
        Assert.Single(rec.Calls);
        env.PointResult = new((nint)6, "x", "Y");                                   // moved under pointer
        Assert.Throws<ToolException>(() => rec.MouseClick(10, 20, "left", 1, System.Array.Empty<string>(), (nint)5));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~RecordingSyntheticInputTests"`
Expected: FAIL (compile — `RecordingSyntheticInput` missing).

- [ ] **Step 3: Create the re-verifying fake**

`test/FlaUI.Mcp.Tests/Interaction/RecordingSyntheticInput.cs`:

```csharp
using System.Collections.Generic;
using FlaUI.Mcp.Core.Interaction;

namespace FlaUI.Mcp.Tests.Interaction;

/// <summary>The 4a test double: runs the SAME atomic pre-send re-verify the real 4b leaf will (via the
/// injected IPlatformEnvironment + InputReverify) and, if it passes, records the call instead of firing.
/// Never reaches the session guard in prod, so it is the DI-only "bypass" of the auto-lockout paradox —
/// never a shipped runtime flag.</summary>
public sealed class RecordingSyntheticInput : ISyntheticInput
{
    private readonly IPlatformEnvironment _env;
    public RecordingSyntheticInput(IPlatformEnvironment env) => _env = env;
    public List<string> Calls { get; } = new();

    public void KeyType(string text, nint root)
    { InputReverify.AssertSameRoot(root, _env.GetForegroundRoot()); Calls.Add($"KeyType:{text}"); }

    public void KeyChord(string[] mods, string key, nint root)
    { InputReverify.AssertSameRoot(root, _env.GetForegroundRoot()); Calls.Add($"KeyChord:{string.Join("+", mods)}+{key}"); }

    public void MouseClick(int x, int y, string button, int count, string[] mods, nint root)
    { InputReverify.AssertSameRoot(root, _env.HitTestRoot(x, y).Root); Calls.Add($"MouseClick:{button}:{x},{y}:{count}"); }

    public void MouseDrag(int sx, int sy, int ex, int ey, string button, nint root)
    { InputReverify.AssertSameRoot(root, _env.HitTestRoot(ex, ey).Root); Calls.Add($"MouseDrag:{button}:{sx},{sy}->{ex},{ey}"); }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~RecordingSyntheticInputTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add test/FlaUI.Mcp.Tests/Interaction/RecordingSyntheticInput.cs test/FlaUI.Mcp.Tests/Interaction/RecordingSyntheticInputTests.cs
git commit -m "feat(input): re-verifying RecordingSyntheticInput fake"
```

---

## Task 5: `InputLease` — parse/format the lease line

**Files:**
- Create: `src/FlaUI.Mcp.Core/Interaction/InputLease.cs`
- Test: `test/FlaUI.Mcp.Tests/Interaction/InputLeaseTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class InputLeaseTests
{
    [Fact]
    public void Round_trips_with_caps()
    {
        var expiry = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var line = InputLease.Format(expiry, "S-1-5-21-1", new[] { "shells" });
        Assert.True(InputLease.TryParse(line, out var lease));
        Assert.Equal(expiry, lease.ExpiryUtc);
        Assert.Equal("S-1-5-21-1", lease.Sid);
        Assert.True(lease.HasCapability("shells"));
    }

    [Fact]
    public void Empty_caps_means_no_shells()
    {
        var line = InputLease.Format(DateTime.UtcNow.AddMinutes(5), "S-1-5-21-1", System.Array.Empty<string>());
        Assert.True(InputLease.TryParse(line, out var lease));
        Assert.False(lease.HasCapability("shells"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("expiryUtc=not-a-date;sid=x;caps=")]
    public void Unparseable_is_rejected(string line) => Assert.False(InputLease.TryParse(line, out _));

    [Fact]
    public void IsValidNow_checks_expiry_and_sid()
    {
        var now = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var lease = new InputLease(now.AddMinutes(5), "S-1-5-21-1", System.Array.Empty<string>());
        Assert.True(lease.IsValidNow(now, "S-1-5-21-1"));
        Assert.False(lease.IsValidNow(now.AddMinutes(6), "S-1-5-21-1"));  // expired
        Assert.False(lease.IsValidNow(now, "S-1-5-21-OTHER"));            // foreign sid
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~InputLeaseTests"`
Expected: FAIL (compile).

- [ ] **Step 3: Implement**

`src/FlaUI.Mcp.Core/Interaction/InputLease.cs`:

```csharp
using System;
using System.Globalization;
using System.Linq;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>The out-of-band time-lease. Default-closed: synthetic input is OFF unless an unexpired
/// lease exists. Granted by the human via `flaui-mcp unlock`; never writable by an MCP-only agent.
/// Line format: <c>expiryUtc=&lt;ISO-8601 UTC&gt;;sid=&lt;granting SID&gt;;caps=&lt;comma-list&gt;</c>.</summary>
public sealed record InputLease(DateTime ExpiryUtc, string Sid, string[] Caps)
{
    public bool HasCapability(string cap) => Caps.Contains(cap, StringComparer.OrdinalIgnoreCase);

    public bool IsValidNow(DateTime utcNow, string currentSid) =>
        ExpiryUtc > utcNow && string.Equals(Sid, currentSid, StringComparison.OrdinalIgnoreCase);

    public static string Format(DateTime expiryUtc, string sid, string[] caps) =>
        $"expiryUtc={expiryUtc.ToUniversalTime():O};sid={sid};caps={string.Join(",", caps)}";

    public static bool TryParse(string? line, out InputLease lease)
    {
        lease = null!;
        if (string.IsNullOrWhiteSpace(line)) return false;
        var kv = line.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
        if (!kv.TryGetValue("expiryUtc", out var exp) ||
            !DateTime.TryParse(exp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiry))
            return false;
        if (!kv.TryGetValue("sid", out var sid) || string.IsNullOrWhiteSpace(sid)) return false;
        var caps = kv.TryGetValue("caps", out var c) && !string.IsNullOrWhiteSpace(c)
            ? c.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();
        lease = new InputLease(expiry.ToUniversalTime(), sid, caps);
        return true;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~InputLeaseTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Interaction/InputLease.cs test/FlaUI.Mcp.Tests/Interaction/InputLeaseTests.cs
git commit -m "feat(input): InputLease parse/format + validity"
```

---

## Task 6: `ILeaseProvider` + `FileLeaseProvider`

**Files:**
- Create: `src/FlaUI.Mcp.Core/Interaction/ILeaseProvider.cs`, `src/FlaUI.Mcp.Core/Interaction/FileLeaseProvider.cs`
- Test: `test/FlaUI.Mcp.Tests/Interaction/FileLeaseProviderTests.cs`

> `FileLeaseProvider` resolves the lease dir from `FLAUI_MCP_DATA_DIR` (same override the installer uses) falling back to `%LOCALAPPDATA%\FlaUI.Mcp`, so tests never touch the real path. It opens `FileShare.ReadWrite` with a short retry to race the `unlock` writer.

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using System.IO;
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

// Serialized with LeaseCliTests: both mutate the process-wide FLAUI_MCP_DATA_DIR env var, which would
// race under xUnit's default parallel-by-class execution.
[CollectionDefinition("LeaseEnv")] public class LeaseEnvCollection { }

[Collection("LeaseEnv")]
public class FileLeaseProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "flaui-lease-" + Guid.NewGuid().ToString("N"));
    public FileLeaseProviderTests() => Environment.SetEnvironmentVariable("FLAUI_MCP_DATA_DIR", _dir);
    public void Dispose() { Environment.SetEnvironmentVariable("FLAUI_MCP_DATA_DIR", null); try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void No_file_means_no_lease()
    {
        var p = new FileLeaseProvider();
        Assert.Null(p.Read(out _));
    }

    [Fact]
    public void Reads_a_written_lease_and_its_write_time()
    {
        Directory.CreateDirectory(_dir);
        var line = InputLease.Format(DateTime.UtcNow.AddMinutes(5), "S-1-5-21-99", new[] { "shells" });
        File.WriteAllText(Path.Combine(_dir, "input.lease"), line);
        var p = new FileLeaseProvider();
        var lease = p.Read(out var writeTime);
        Assert.NotNull(lease);
        Assert.True(lease!.HasCapability("shells"));
        Assert.NotEqual(default, writeTime);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~FileLeaseProviderTests"`
Expected: FAIL (compile).

- [ ] **Step 3: Implement**

`src/FlaUI.Mcp.Core/Interaction/ILeaseProvider.cs`:

```csharp
using System;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Reads the current lease and its last-write time (for the budget reset). Abstracted so the
/// guard's lease/expiry/budget-reset paths are testable without the filesystem or wall clock.</summary>
public interface ILeaseProvider
{
    /// <summary>The parsed current lease (null if absent/unparseable), and its file write time.</summary>
    InputLease? Read(out DateTime lastWriteUtc);
}
```

`src/FlaUI.Mcp.Core/Interaction/FileLeaseProvider.cs`:

```csharp
using System;
using System.IO;
using System.Threading;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>File-backed lease at <c>%LOCALAPPDATA%\FlaUI.Mcp\input.lease</c> (dir overridable via
/// FLAUI_MCP_DATA_DIR). Opened FileShare.ReadWrite with a short retry to race the `unlock` writer.</summary>
public sealed class FileLeaseProvider : ILeaseProvider
{
    public static string LeaseDir()
    {
        var dir = Environment.GetEnvironmentVariable("FLAUI_MCP_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlaUI.Mcp");
        return dir;
    }

    public static string LeasePath() => Path.Combine(LeaseDir(), "input.lease");

    public InputLease? Read(out DateTime lastWriteUtc)
    {
        lastWriteUtc = default;
        var path = LeasePath();
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                lastWriteUtc = File.GetLastWriteTimeUtc(path);
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                var line = sr.ReadLine();
                return InputLease.TryParse(line, out var lease) ? lease : null;
            }
            catch (IOException) { Thread.Sleep(20); }      // sharing violation — retry
            catch (UnauthorizedAccessException) { return null; }
        }
        return null;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~FileLeaseProviderTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Interaction/ILeaseProvider.cs src/FlaUI.Mcp.Core/Interaction/FileLeaseProvider.cs test/FlaUI.Mcp.Tests/Interaction/FileLeaseProviderTests.cs
git commit -m "feat(input): ILeaseProvider + file-backed FileLeaseProvider"
```

---

## Task 7: `unlock` / `lock` CLI

**Files:**
- Create: `src/FlaUI.Mcp.Server/Lease/LeaseWriter.cs`
- Modify: `src/FlaUI.Mcp.Server/Install/CliRouter.cs` (add verbs + dispatch)
- Test: `test/FlaUI.Mcp.Tests/Server/LeaseCliTests.cs`

> The CLI is a run-and-exit verb like the installer verbs (handled before the MCP host starts, via `CliRouter`; see `Program.cs:10` `CliRouter.IsInstallerVerb`). `unlock --minutes N [--allow-shells]` writes the lease for the current user's SID; `lock` deletes it.

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using System.IO;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

[Collection("LeaseEnv")]   // shares the LeaseEnv collection (defined in FileLeaseProviderTests) to serialize env-var mutation
public class LeaseCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "flaui-leasecli-" + Guid.NewGuid().ToString("N"));
    public LeaseCliTests() => Environment.SetEnvironmentVariable("FLAUI_MCP_DATA_DIR", _dir);
    public void Dispose() { Environment.SetEnvironmentVariable("FLAUI_MCP_DATA_DIR", null); try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Unlock_is_a_recognized_verb()
        => Assert.True(CliRouter.IsInstallerVerb(new[] { "unlock" }));

    [Fact]
    public void Unlock_writes_a_valid_future_lease_then_lock_removes_it()
    {
        var outp = new StringWriter();
        CliRouter.Run(new[] { "unlock", "--minutes", "5", "--allow-shells" }, "exe", outp);

        var lease = new FileLeaseProvider().Read(out _);
        Assert.NotNull(lease);
        Assert.True(lease!.ExpiryUtc > DateTime.UtcNow.AddMinutes(4));
        Assert.True(lease.HasCapability("shells"));

        CliRouter.Run(new[] { "lock" }, "exe", outp);
        Assert.Null(new FileLeaseProvider().Read(out _));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~LeaseCliTests"`
Expected: FAIL (verb not recognized / no lease written).

- [ ] **Step 3: Implement the writer + wire the verbs**

`src/FlaUI.Mcp.Server/Lease/LeaseWriter.cs`:

```csharp
using System;
using System.IO;
using System.Security.Principal;
using FlaUI.Mcp.Core.Interaction;

namespace FlaUI.Mcp.Server.Lease;

/// <summary>Writes/removes the input lease for the CLI. The SID binds the lease to the granting user so
/// a cross-session writer can't grant input to a different session.</summary>
public static class LeaseWriter
{
    public static string Grant(int minutes, bool allowShells)
    {
        var dir = FileLeaseProvider.LeaseDir();
        Directory.CreateDirectory(dir);
        var sid = CurrentSid();
        var caps = allowShells ? new[] { "shells" } : Array.Empty<string>();
        var expiry = DateTime.UtcNow.AddMinutes(Math.Clamp(minutes, 1, 1440));
        File.WriteAllText(FileLeaseProvider.LeasePath(), InputLease.Format(expiry, sid, caps));
        return $"input unlocked until {expiry:O} (sid {sid}{(allowShells ? ", shells" : "")})";
    }

    public static string Revoke()
    {
        var path = FileLeaseProvider.LeasePath();
        if (File.Exists(path)) { File.Delete(path); return "input locked (lease removed)"; }
        return "input already locked (no lease)";
    }

    private static string CurrentSid()
    {
        try { using var id = WindowsIdentity.GetCurrent(); return id.User?.Value ?? "unknown"; }
        catch { return "unknown"; }
    }
}
```

In `CliRouter.cs`, add `"unlock"`, `"lock"` to the `Verbs` set:

```csharp
    private static readonly HashSet<string> Verbs =
        new(StringComparer.OrdinalIgnoreCase) { "install", "uninstall", "print-config", "unlock", "lock", "--version", "-v", "--help", "-h" };
```

Add cases to the `switch (verb)` in `Run` (before `default`):

```csharp
            case "unlock":
            {
                var minutes = int.TryParse(OptionValue(args, "--minutes"), out var m) ? m : 5;
                outp.WriteLine(Lease.LeaseWriter.Grant(minutes, HasFlag(args, "--allow-shells")));
                return 0;
            }
            case "lock":
                outp.WriteLine(Lease.LeaseWriter.Revoke());
                return 0;
```

Update the `usage:` line in `default` to mention `unlock`/`lock`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~LeaseCliTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Lease/LeaseWriter.cs src/FlaUI.Mcp.Server/Install/CliRouter.cs test/FlaUI.Mcp.Tests/Server/LeaseCliTests.cs
git commit -m "feat(input): unlock/lock CLI subcommands (out-of-band lease grant)"
```

---

## Task 8: `ActionPolicy` — deny-list + sink interlock

**Files:**
- Create: `src/FlaUI.Mcp.Core/Interaction/ActionPolicy.cs`
- Test: `test/FlaUI.Mcp.Tests/Interaction/ActionPolicyTests.cs`

> Reuses `PerceptionPolicy.IsDenied` for credential stores; adds UAC/secure-desktop processes + the interlock-sink classification by process/window-class. Pure classification over (processName, windowClass).

- [ ] **Step 1: Write the failing test**

```csharp
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class ActionPolicyTests
{
    [Theory]
    [InlineData("consent", null)]
    [InlineData("1password", null)]
    public void Credential_and_uac_targets_are_denied(string proc, string? cls)
        => Assert.Equal(ActionVerdict.Denied, ActionPolicy.Classify(proc, cls));

    [Theory]
    [InlineData("WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS")]
    [InlineData("cmd", "ConsoleWindowClass")]
    public void Shells_are_interlocked(string proc, string cls)
        => Assert.Equal(ActionVerdict.Interlocked, ActionPolicy.Classify(proc, cls));

    [Fact]
    public void Ordinary_app_is_allowed()
        => Assert.Equal(ActionVerdict.Allowed, ActionPolicy.Classify("notepad", "Notepad"));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~ActionPolicyTests"`
Expected: FAIL (compile).

- [ ] **Step 3: Implement**

`src/FlaUI.Mcp.Core/Interaction/ActionPolicy.cs`:

```csharp
using System;
using System.Collections.Generic;
using FlaUI.Mcp.Core.Perception;

namespace FlaUI.Mcp.Core.Interaction;

public enum ActionVerdict { Allowed, Interlocked, Denied }

/// <summary>Classifies a synthetic-input TARGET (owning process base-name + window class). Denied =
/// hard refuse (UAC/secure-desktop/credential stores). Interlocked = refuse unless the lease carries
/// the `shells` capability (terminal / Win+R / browser address bar). Same logic for the ref path and
/// the coordinate hit-test path.</summary>
public static class ActionPolicy
{
    private static readonly HashSet<string> DeniedProcesses =
        new(StringComparer.OrdinalIgnoreCase) { "consent", "winlogon", "lockapp", "logonui", "credentialuihost" };

    private static readonly HashSet<string> InterlockedClasses =
        new(StringComparer.OrdinalIgnoreCase) { "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS" };

    private static readonly HashSet<string> InterlockedProcesses =
        new(StringComparer.OrdinalIgnoreCase) { "windowsterminal", "cmd", "powershell", "pwsh", "conhost" };

    public static ActionVerdict Classify(string? processName, string? windowClass)
    {
        var proc = processName?.Trim();
        var cls = windowClass?.Trim();
        if (!string.IsNullOrEmpty(proc) && (DeniedProcesses.Contains(proc) || PerceptionPolicy.IsDenied(proc)))
            return ActionVerdict.Denied;
        if ((!string.IsNullOrEmpty(cls) && InterlockedClasses.Contains(cls)) ||
            (!string.IsNullOrEmpty(proc) && InterlockedProcesses.Contains(proc)))
            return ActionVerdict.Interlocked;
        return ActionVerdict.Allowed;
    }
}
```

> NB the Win+R run dialog and browser address bar are interlocked at the *element* level in 4b's tool layer (a class match isn't sufficient for the address bar); the 4b plan adds those element checks. `ActionPolicy` covers the process/class-identifiable sinks here.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~ActionPolicyTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Interaction/ActionPolicy.cs test/FlaUI.Mcp.Tests/Interaction/ActionPolicyTests.cs
git commit -m "feat(input): ActionPolicy deny-list + sink interlock classification"
```

---

## Task 9: `ActionBudget` — per-window rate limit, lease-write reset

**Files:**
- Create: `src/FlaUI.Mcp.Core/Interaction/ActionBudget.cs`
- Test: `test/FlaUI.Mcp.Tests/Interaction/ActionBudgetTests.cs`

> Deterministic: time + the lease write-time are passed in (no `DateTime.Now` inside). Default 60 actions / 60 s rolling window per target window; resets when the lease write-time advances.

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class ActionBudgetTests
{
    private static DateTime T(int s) => new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(s);

    [Fact]
    public void Allows_up_to_limit_then_refuses_within_window()
    {
        var b = new ActionBudget(maxPerWindow: 3, windowSeconds: 60);
        var lease = T(0);
        for (int i = 0; i < 3; i++) Assert.True(b.TryConsume(window: (nint)1, now: T(i), leaseWriteUtc: lease));
        Assert.False(b.TryConsume((nint)1, T(3), lease));        // 4th within 60s → refused
    }

    [Fact]
    public void Window_slides_so_old_actions_expire()
    {
        var b = new ActionBudget(3, 60);
        var lease = T(0);
        for (int i = 0; i < 3; i++) b.TryConsume((nint)1, T(i), lease);
        Assert.True(b.TryConsume((nint)1, T(61), lease));        // first three aged out
    }

    [Fact]
    public void New_lease_write_resets_the_budget()
    {
        var b = new ActionBudget(3, 60);
        for (int i = 0; i < 3; i++) b.TryConsume((nint)1, T(i), T(0));
        Assert.True(b.TryConsume((nint)1, T(3), leaseWriteUtc: T(2)));   // lease re-granted → reset
    }

    [Fact]
    public void Budget_is_per_window()
    {
        var b = new ActionBudget(1, 60);
        Assert.True(b.TryConsume((nint)1, T(0), T(0)));
        Assert.True(b.TryConsume((nint)2, T(0), T(0)));          // different window, own budget
        Assert.False(b.TryConsume((nint)1, T(0), T(0)));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~ActionBudgetTests"`
Expected: FAIL (compile).

- [ ] **Step 3: Implement**

`src/FlaUI.Mcp.Core/Interaction/ActionBudget.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Per-target-window rolling rate limit. Stops an injected click/keystroke flood from turning
/// a valid lease window into a blank cheque. Resets a window's counter when the lease write-time
/// advances (a fresh `unlock`). Deterministic — caller passes `now` and `leaseWriteUtc`.</summary>
public sealed class ActionBudget
{
    private readonly int _max;
    private readonly double _windowSeconds;
    private readonly Dictionary<nint, Queue<DateTime>> _hits = new();
    private DateTime _seenLeaseWrite = default;
    private readonly object _gate = new();

    public ActionBudget(int maxPerWindow = 60, double windowSeconds = 60)
    { _max = maxPerWindow; _windowSeconds = windowSeconds; }

    public bool TryConsume(nint window, DateTime now, DateTime leaseWriteUtc)
    {
        lock (_gate)
        {
            if (leaseWriteUtc > _seenLeaseWrite) { _hits.Clear(); _seenLeaseWrite = leaseWriteUtc; }
            if (!_hits.TryGetValue(window, out var q)) { q = new Queue<DateTime>(); _hits[window] = q; }
            var cutoff = now.AddSeconds(-_windowSeconds);
            while (q.Count > 0 && q.Peek() <= cutoff) q.Dequeue();
            if (q.Count >= _max) return false;
            q.Enqueue(now);
            return true;
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~ActionBudgetTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Interaction/ActionBudget.cs test/FlaUI.Mcp.Tests/Interaction/ActionBudgetTests.cs
git commit -m "feat(input): ActionBudget rolling rate limit + lease-write reset"
```

---

## Task 10: `InputAudit` — event-only audit

**Files:**
- Create: `src/FlaUI.Mcp.Core/Interaction/InputAudit.cs`
- Test: `test/FlaUI.Mcp.Tests/Interaction/InputAuditTests.cs`

> Event-only: timestamp, target window + pid + process, action kind, payload LENGTH — never the typed text. Writes one line to an injected `TextWriter` (stderr in prod, like the clipboard audit).

- [ ] **Step 1: Write the failing test**

```csharp
using System.IO;
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class InputAuditTests
{
    [Fact]
    public void Writes_event_only_never_the_payload()
    {
        var sw = new StringWriter();
        new InputAudit(sw).Record(window: (nint)42, pid: 1234, process: "notepad", action: "type", payloadLength: 7);
        var line = sw.ToString();
        Assert.Contains("action=type", line);
        Assert.Contains("len=7", line);
        Assert.Contains("pid=1234", line);
        Assert.Contains("process=notepad", line);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~InputAuditTests"`
Expected: FAIL (compile).

- [ ] **Step 3: Implement**

`src/FlaUI.Mcp.Core/Interaction/InputAudit.cs`:

```csharp
using System;
using System.IO;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Event-only synthetic-input audit. Logs WHEN + WHICH target + WHAT KIND + payload LENGTH —
/// never the typed text (a payload can BE the secret; keeps Phase-2's no-secrets-on-disk stance).</summary>
public sealed class InputAudit
{
    private readonly TextWriter _sink;
    public InputAudit(TextWriter sink) => _sink = sink;

    public void Record(nint window, int pid, string? process, string action, int payloadLength) =>
        _sink.WriteLine($"[flaui-mcp][input-audit] ts={DateTime.UtcNow:O} window={window} pid={pid} " +
                        $"process={process ?? "?"} action={action} len={payloadLength}");
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~InputAuditTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Interaction/InputAudit.cs test/FlaUI.Mcp.Tests/Interaction/InputAuditTests.cs
git commit -m "feat(input): InputAudit event-only logging"
```

---

## Task 11: Elevation hard-fail seam + `ServerOptions.AllowElevation`

**Files:**
- Modify: `src/FlaUI.Mcp.Server/ServerOptions.cs`, `src/FlaUI.Mcp.Server/ElevationGuard.cs`
- Test: `test/FlaUI.Mcp.Tests/Server/ElevationGuardTests.cs`

> Upgrades the Phase-2 warn-only into a hard-fail *decision* for the input sink: if elevated and not `--unsafe-allow-elevation`, input must be refused. Pure decision seam (no real elevation check in the test).

- [ ] **Step 1: Write the failing test**

Add to `ElevationGuardTests.cs`:

```csharp
[Theory]
[InlineData(true,  false, true)]   // elevated, not allowed -> refuse
[InlineData(true,  true,  false)]  // elevated, --unsafe-allow-elevation -> permit
[InlineData(false, false, false)]  // not elevated -> permit
public void Input_hard_fail_decision(bool elevated, bool allow, bool expectedRefuse)
    => Assert.Equal(expectedRefuse, ElevationGuard.InputHardFailIfElevated(elevated, allow));

[Fact]
public void AllowElevation_flag_parsed()
{
    Assert.True(FlaUI.Mcp.Server.ServerOptions.FromArgs(new[] { "--unsafe-allow-elevation" }).AllowElevation);
    Assert.False(FlaUI.Mcp.Server.ServerOptions.FromArgs(System.Array.Empty<string>()).AllowElevation);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~ElevationGuardTests"`
Expected: FAIL (compile).

- [ ] **Step 3: Implement**

In `ServerOptions.cs`:

```csharp
public sealed record ServerOptions(bool ReadOnly, bool AllowElevation)
{
    public static ServerOptions FromArgs(string[] args) =>
        new(ReadOnly: args.Contains("--read-only-mode"),
            AllowElevation: args.Contains("--unsafe-allow-elevation"));
}
```

> This changes the positional record arity. `Program.cs` uses only `ServerOptions.FromArgs(args)` (no change). The compiler will flag any `new ServerOptions(ReadOnly: true)` in the test project (e.g. `Tools/ClipboardToolsReadOnlyTests.cs`, `ReadOnlyGateTests`) — change each to `new ServerOptions(ReadOnly: true, AllowElevation: false)`.

In `ElevationGuard.cs`, add the decision seam (alongside `WarnIfElevated`):

```csharp
    /// <summary>Hard-fail decision for the synthetic-input sink: refuse input when elevated unless the
    /// operator opted in with --unsafe-allow-elevation. (Perception/pattern tools are unaffected.)</summary>
    public static bool InputHardFailIfElevated(bool isElevated, bool allowElevation) => isElevated && !allowElevation;
```

- [ ] **Step 4: Run tests + build**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "Category!=Desktop"`
Expected: PASS (fix any `new ServerOptions(...)` call sites the compiler flags).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/ServerOptions.cs src/FlaUI.Mcp.Server/ElevationGuard.cs test/FlaUI.Mcp.Tests/
git commit -m "feat(input): elevation hard-fail decision + --unsafe-allow-elevation"
```

---

## Task 12: `InputGuard` — the pipeline

**Files:**
- Create: `src/FlaUI.Mcp.Core/Interaction/InputGuard.cs` (+ `ActionTarget` record)
- Test: `test/FlaUI.Mcp.Tests/Interaction/InputGuardTests.cs`

> Composes the seams in order: elevation → lease → deny-list/interlock → session-state → budget → audit → delegate to `ISyntheticInput`. Pure over injected deps. Throws the mapped `ToolException` on each refusal. This is the dormant engine 4b wires to the tools. (The MCP read-only-mode gate is enforced one layer up by `ToolResponse.GuardWrite` in 4b's tools, not here.)

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using System.IO;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class InputGuardTests
{
    private const string Sid = "S-1-5-21-1";
    private static DateTime Now => new(2030, 1, 1, 0, 0, 30, DateTimeKind.Utc);

    private static (InputGuard guard, RecordingSyntheticInput sink, FakePlatformEnvironment env) Build(
        InputLease? lease, bool elevated = false, bool allowElevation = false)
    {
        var env = new FakePlatformEnvironment { CanDeliver = true, ForegroundRoot = nint.Zero };
        var sink = new RecordingSyntheticInput(env);   // targets below use Root=nint.Zero, so re-verify matches
        var leaseProv = new StubLeaseProvider(lease, default);
        var guard = new InputGuard(sink, env, leaseProv,
            new ActionBudget(60, 60), new InputAudit(TextWriter.Null),
            currentSid: Sid, isElevated: elevated, allowElevation: allowElevation, clock: () => Now);
        return (guard, sink, env);
    }

    private static InputLease ValidLease(string[]? caps = null) =>
        new(new DateTime(2030, 1, 1, 0, 1, 0, DateTimeKind.Utc), Sid, caps ?? Array.Empty<string>());

    [Fact]
    public void No_lease_refuses_with_InputNotLeased()
    {
        var (g, sink, _) = Build(lease: null);
        var ex = Assert.Throws<ToolException>(() => g.KeyType("hi", new(nint.Zero, 0, "notepad", "Notepad")));
        Assert.Equal(ToolErrorCode.InputNotLeased, ex.Code);
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public void Expired_lease_refuses()
    {
        var expired = new InputLease(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), Sid, Array.Empty<string>());
        var (g, _, _) = Build(expired);
        Assert.Throws<ToolException>(() => g.KeyType("hi", new(nint.Zero, 0, "notepad", "Notepad")));
    }

    [Fact]
    public void Denied_target_refuses_with_TargetDenied()
    {
        var (g, _, _) = Build(ValidLease());
        var ex = Assert.Throws<ToolException>(() => g.KeyType("hi", new(nint.Zero, 0, "consent", "Credential")));
        Assert.Equal(ToolErrorCode.TargetDenied, ex.Code);
    }

    [Fact]
    public void Interlocked_without_shells_cap_refuses_with_SinkInterlocked()
    {
        var (g, _, _) = Build(ValidLease(caps: Array.Empty<string>()));
        var ex = Assert.Throws<ToolException>(() => g.KeyType("ls", new(nint.Zero, 0, "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS")));
        Assert.Equal(ToolErrorCode.SinkInterlocked, ex.Code);
    }

    [Fact]
    public void Interlocked_with_shells_cap_is_permitted()
    {
        var (g, sink, _) = Build(ValidLease(caps: new[] { "shells" }));
        g.KeyType("ls", new(nint.Zero, 0, "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS"));
        Assert.Single(sink.Calls);
    }

    [Fact]
    public void Locked_session_refuses_with_InputDesktopUnavailable()
    {
        var (g, _, env) = Build(ValidLease());
        env.CanDeliver = false;
        var ex = Assert.Throws<ToolException>(() => g.KeyType("hi", new(nint.Zero, 0, "notepad", "Notepad")));
        Assert.Equal(ToolErrorCode.InputDesktopUnavailable, ex.Code);
    }

    [Fact]
    public void Elevated_without_optin_refuses()
    {
        var (g, _, _) = Build(ValidLease(), elevated: true, allowElevation: false);
        var ex = Assert.Throws<ToolException>(() => g.KeyType("hi", new(nint.Zero, 0, "notepad", "Notepad")));
        Assert.Equal(ToolErrorCode.AccessDeniedIntegrity, ex.Code);
    }

    [Fact]
    public void Allowed_target_with_lease_delegates_to_the_sink()
    {
        var (g, sink, _) = Build(ValidLease());
        g.KeyType("hi", new(nint.Zero, 0, "notepad", "Notepad"));
        Assert.Equal("KeyType:hi", Assert.Single(sink.Calls));
    }

    [Fact]
    public void Drag_denies_when_the_END_endpoint_is_a_denied_target()
    {
        var (g, sink, _) = Build(ValidLease());
        var start = new ActionTarget(nint.Zero, 0, "explorer", "CabinetWClass");
        var end   = new ActionTarget(nint.Zero, 0, "consent", "Credential");   // drop into UAC
        var ex = Assert.Throws<ToolException>(() => g.MouseDrag(0, 0, 10, 10, "left", start, end));
        Assert.Equal(ToolErrorCode.TargetDenied, ex.Code);
        Assert.Empty(sink.Calls);
    }

    private sealed class StubLeaseProvider : ILeaseProvider
    {
        private readonly InputLease? _lease; private readonly DateTime _w;
        public StubLeaseProvider(InputLease? lease, DateTime w) { _lease = lease; _w = w; }
        public InputLease? Read(out DateTime lastWriteUtc) { lastWriteUtc = _w; return _lease; }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "FullyQualifiedName~InputGuardTests"`
Expected: FAIL (compile — `InputGuard` + `ActionTarget` missing).

- [ ] **Step 3: Implement**

`src/FlaUI.Mcp.Core/Interaction/InputGuard.cs`:

```csharp
using System;
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>The synthetic-input decision pipeline: elevation -> lease -> deny-list/interlock ->
/// session-state -> budget -> audit -> delegate to ISyntheticInput. Pure over its injected deps; the
/// atomic pre-send re-verify is delegated into the ISyntheticInput leaf (not here, to stay on the same
/// thread as SendInput). 4b registers this in DI and wires the tools to it; in 4a it is dormant.</summary>
public sealed class InputGuard
{
    private readonly ISyntheticInput _sink;
    private readonly IPlatformEnvironment _env;
    private readonly ILeaseProvider _leases;
    private readonly ActionBudget _budget;
    private readonly InputAudit _audit;
    private readonly string _currentSid;
    private readonly bool _isElevated;
    private readonly bool _allowElevation;
    private readonly Func<DateTime> _clock;

    public InputGuard(ISyntheticInput sink, IPlatformEnvironment env, ILeaseProvider leases,
        ActionBudget budget, InputAudit audit, string currentSid, bool isElevated, bool allowElevation,
        Func<DateTime>? clock = null)
    {
        _sink = sink; _env = env; _leases = leases; _budget = budget; _audit = audit;
        _currentSid = currentSid; _isElevated = isElevated; _allowElevation = allowElevation;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Run the full pipeline; throws the mapped ToolException on any refusal. `secondary` is the
    /// second deny-list target for a drag (its drop endpoint) — both endpoints must pass the deny-list.</summary>
    private void Authorize(ActionTarget primary, string action, int payloadLength, ActionTarget? secondary = null)
    {
        if (_isElevated && !_allowElevation)
            throw new ToolException(ToolErrorCode.AccessDeniedIntegrity,
                "Synthetic input is refused while the server runs elevated.",
                "restart without elevation, or pass --unsafe-allow-elevation if you accept the risk");

        var now = _clock();
        var lease = _leases.Read(out var leaseWrite);
        if (lease is null || !lease.IsValidNow(now, _currentSid))
            throw new ToolException(ToolErrorCode.InputNotLeased,
                "Synthetic input is locked. No unexpired lease for this user.",
                "run `flaui-mcp unlock --minutes N` on the host to enable input");

        CheckTarget(primary, lease);
        if (secondary is { } s) CheckTarget(s, lease);

        if (!_env.SessionState().CanDeliverInput)
            throw new ToolException(ToolErrorCode.InputDesktopUnavailable,
                "The interactive input desktop is unavailable (locked / disconnected / secure desktop).",
                "connect and unlock the session, then retry");

        if (!_budget.TryConsume(primary.Root, now, leaseWrite))
            throw new ToolException(ToolErrorCode.InputBudgetExceeded,
                "Synthetic-input rate limit exceeded for this window.",
                "slow down, or re-grant the lease with `flaui-mcp unlock` to reset the budget");

        _audit.Record(primary.Root, primary.Pid, primary.ProcessName, action, payloadLength);
    }

    private static void CheckTarget(ActionTarget target, InputLease lease)
    {
        var verdict = ActionPolicy.Classify(target.ProcessName, target.WindowClass);
        if (verdict == ActionVerdict.Denied)
            throw new ToolException(ToolErrorCode.TargetDenied,
                $"Synthetic input into '{target.ProcessName}' is refused (UAC/secure-desktop/credential store).",
                "target a different, non-sensitive window");
        if (verdict == ActionVerdict.Interlocked && !lease.HasCapability("shells"))
            throw new ToolException(ToolErrorCode.SinkInterlocked,
                $"Synthetic input into the interlocked sink '{target.ProcessName}' requires the 'shells' lease capability.",
                "re-grant with `flaui-mcp unlock --minutes N --allow-shells` (human, out-of-band)");
    }

    public void KeyType(string text, ActionTarget target)
    {
        Authorize(target, "type", text?.Length ?? 0);
        _sink.KeyType(text ?? string.Empty, target.Root);
    }

    public void KeyChord(string[] modifiers, string key, ActionTarget target)
    {
        Authorize(target, "key", (modifiers?.Length ?? 0) + 1);
        _sink.KeyChord(modifiers ?? Array.Empty<string>(), key, target.Root);
    }

    public void MouseClick(int physX, int physY, string button, int count, string[] modifiers, ActionTarget target)
    {
        Authorize(target, "click", count);
        _sink.MouseClick(physX, physY, button, count, modifiers ?? Array.Empty<string>(), target.Root);
    }

    // Drag carries TWO targets: both endpoints run the deny-list; the END root is the re-verify root
    // the sink fires against (a drag can DROP INTO a denied window — spec §4 / red-team finding).
    public void MouseDrag(int sx, int sy, int ex, int ey, string button, ActionTarget startTarget, ActionTarget endTarget)
    {
        Authorize(startTarget, "drag", 1, secondary: endTarget);
        _sink.MouseDrag(sx, sy, ex, ey, button, endTarget.Root);
    }
}

/// <summary>The resolved target of a synthetic-input action: its top-level window root + identity for
/// the deny-list/budget/audit. For the ref path the tool resolves these from the window handle; for the
/// coordinate path 4b fills them from IPlatformEnvironment.HitTestRoot.</summary>
public readonly record struct ActionTarget(nint Root, int Pid, string? ProcessName, string? WindowClass);
```

- [ ] **Step 4: Run tests + build**

Run: `dotnet test test/FlaUI.Mcp.Tests --filter "Category!=Desktop"`
Expected: PASS (all InputGuard cases green, no regressions).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Interaction/InputGuard.cs test/FlaUI.Mcp.Tests/Interaction/InputGuardTests.cs
git commit -m "feat(input): InputGuard decision pipeline (lease/deny-list/session/budget/audit)"
```

---

## Task 13: Docs + version bump + wrap

**Files:**
- Modify: `README.md`, `ROADMAP.md`, `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj` (`<Version>`), `installer/flaui-mcp.iss` (`AppVersion`)

- [ ] **Step 1: Bump versions in lockstep**

In `FlaUI.Mcp.Server.csproj` set `<Version>0.6.0</Version>`; in `installer/flaui-mcp.iss` set `#define AppVersion "0.6.0"`. (Both must move together — the 3b-1 release missed the `.iss`; do not repeat.)

- [ ] **Step 2: Update docs**

In `README.md`: add a "Phase 4a — input safety foundation (v0.6.0)" note — the `unlock`/`lock` CLI, the default-closed lease, `--unsafe-allow-elevation`, and that **no input tool ships yet** (4a is the safety foundation; synthetic input lands in v0.7.0). In `ROADMAP.md` Phase 4: mark 4a shipped with the seam set + lease + guards; note 4b (real `SendInput` tools + the active-RDP spike) is next.

- [ ] **Step 3: Full gate**

Run: `dotnet build -c Release` then `dotnet test -c Release --filter "Category!=Desktop"`
Expected: build 0 warn / 0 err; all non-Desktop tests PASS.

- [ ] **Step 4: Commit**

```bash
git add README.md ROADMAP.md src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj installer/flaui-mcp.iss
git commit -m "docs+release: Phase 4a safety stack (v0.6.0)"
```

---

## Notes for the implementer

- **No Desktop tests in 4a.** Every test is `Category!=Desktop`; the green gate is `dotnet test -c Release --filter "Category!=Desktop"`. Do not add a Desktop-tagged test in this phase.
- **`InputGuard` is intentionally not DI-registered.** It is dormant in the v0.6.0 binary (no tool consumes it). 4b adds the `Program.cs` registration + the real `Win32SyntheticInput`/`Win32PlatformEnvironment` + the tools. Do not wire it into `Program.cs` here.
- **Record arity change (Task 11).** `ServerOptions` gains a second positional member — the compiler points you at every `new ServerOptions(...)` call site in the test project; fix each to the 2-arg form.
- **STATE-VERIFY before editing (Step 0 of each task):** open the cited file and confirm the lines match before editing; if reality differs, STOP and report `STATE_MISMATCH` rather than adapting. In particular re-confirm the exact `isPwd` lines in `PerceptionManager.cs` (Task 2) and the `Verbs`/`switch` shape in `CliRouter.cs` (Task 7).
- **Wrap = NOT a release.** Task 13 bumps to v0.6.0 and updates docs but does NOT merge/tag — finishing-the-branch (merge `phase-4a-safety-stack` → master, tag `v0.6.0`) is a separate, user-gated step after the controller verifies the full non-Desktop gate.
