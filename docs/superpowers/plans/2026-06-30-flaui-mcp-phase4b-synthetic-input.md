# FlaUI.Mcp Phase 4b (v0.7.0) — Synthetic Input Tools Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Layer the real synthetic-input MCP tools (`desktop_type/key/click/click_at/drag`), the two UIA `TextPattern` tools (`desktop_set_caret` / `desktop_select_text_range`), and a read-only `desktop_input_status` pre-flight tool onto the dormant Phase-4a safety stack, plus the real Win32 leaves (`Win32SyntheticInput`, `Win32PlatformEnvironment`), wiring `InputGuard` into DI — folding the five carried merge-gate findings F1–F5. (`desktop_set_caret` / `desktop_select_text_range` were re-added to v0.7.0 per the 2026-06-30 user ruling reversing their earlier same-day deferral — **8 tools ship in v0.7.0.**)

**Architecture:** The decision pipeline (`InputGuard`) and all targeting/chord/coordinate logic stay PURE and headless-testable; the only un-automated surface is the two thin Win32 leaves, already discharged by the 2026-06-30 active-RDP spike. Tools resolve an `ActionTarget` (ref path from the window's UIA properties; coordinate path from `IPlatformEnvironment.HitTestRoot`), run the guard pipeline, and the leaf performs the atomic pre-send re-verify on the same thread as `SendInput`. `set_caret`/`select_text_range` use UIA `TextPattern` (no `SendInput`) and route through the deny-list/interlock ONLY (lease + session-guard exempt).

**Tech Stack:** C#/.NET 10 (`net10.0-windows`), FlaUI.UIA3 5.0.0, ModelContextProtocol, xUnit. Solution `FlaUI.Mcp.slnx`. Branch off `master` (HEAD `75fde12`, Phase 4a v0.6.0 LIVE).

**Locked design source:** `docs/superpowers/specs/2026-06-30-flaui-mcp-phase4-design.md` (commit `1e1f2c3`) — §3 safety stack, §4 tool surface, §5 coordinate contract, §6 testing strategy.

**Spike provenance (the un-automated leaves are pre-validated):** Active-RDP throwaway spikes (`<scratchpad>/spike.cs`, `spike_focus.cs`, 2026-06-30) machine-proved: `SendInput` fires when the RDP session is connected; `OpenInputDesktop(0,FALSE,MAXIMUM_ALLOWED)` succeeds; §5 VIRTUALDESK 0–65535 normalization is pixel-perfect; `WindowFromPhysicalPoint→GetAncestor(GA_ROOT)` resolves process+class; **FlaUI UIA `Focus()` from a background process moves the OS foreground so `SendInput` lands** (`foreground==GA_ROOT(target)` = YES; token landed). The spec's ref-first `Focus → re-verify → ABORT-on-mismatch` contract holds as written.

---

## Environment & test-strategy constraints (read before starting)

1. **This dev/CI box is headless RDP-only.** Synthetic-input end-to-end (`SendInput`) tests CANNOT pass here and are NOT the gate. The CI-equivalent gate stays `dotnet test --filter "Category!=Desktop"`. The real leaves are validated by the spike (done) + the user's manual validation on an active session (4b release gate).
2. **Maximize headless coverage by extraction.** Every decision — chord grammar, coordinate normalization, surrogate emission, pct→physical mapping, target classification, lease validity, audit — lives in a PURE class with a headless test. Only the two thin Win32 leaves (`Win32SyntheticInput`, `Win32PlatformEnvironment`) are build-only/spiked.
3. **Synthetic-input Desktop tests are CONSOLE-MACHINE-ONLY.** Where this plan adds `[Trait("Category","Desktop")]` synthetic-input smoke tests (Tasks 10–12), they are runnable only on a physical-console machine, never on this RDP box; they are written so the user can run them there, and are not part of the headless gate.
4. **Run Desktop chunks bounded** and loop-kill orphans (`Get-Process testhost,FlaUI.Mcp.TestApp | Stop-Process -Force`) — the full serialized Desktop suite hangs under load.
5. **Exact build/test commands (repo gate, do not invent stricter flags):**
   - Build: `dotnet build FlaUI.Mcp.slnx` — expect `Build succeeded. 0 Warning(s) 0 Error(s)` (5 projects).
   - Headless gate: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop"` — expect `Passed!  - Failed: 0`. Current baseline is **114** non-Desktop tests on `master`; each task states the new expected total.

## File structure (created / modified)

**New — Core/Interaction (pure decision + helpers, all headless-tested):**
- `src/FlaUI.Mcp.Core/Interaction/KeyChordParser.cs` — chord grammar → modifiers + VK (Task 4)
- `src/FlaUI.Mcp.Core/Interaction/VirtualDesktopMap.cs` — physical px → 0–65535 virtual-desktop absolute (Task 5)
- `src/FlaUI.Mcp.Core/Interaction/UnicodeKeyInput.cs` — string → keyboard `INPUT[]` incl. surrogate pairs (Task 5)
- `src/FlaUI.Mcp.Core/Interaction/CoordinateMath.cs` — window-relative pct → physical point (Task 6)
- `src/FlaUI.Mcp.Core/Interaction/InputTargeting.cs` — UIA window element → `ActionTarget` (Task 9)

**New — Core/Interaction (real Win32 leaves, build-only / spiked):**
- `src/FlaUI.Mcp.Core/Interaction/Win32Interop.cs` — shared P/Invoke + `INPUT` struct layout (Task 5/7)
- `src/FlaUI.Mcp.Core/Interaction/Win32SyntheticInput.cs` — real `SendInput` leaf (Task 7)
- `src/FlaUI.Mcp.Core/Interaction/Win32PlatformEnvironment.cs` — real Win32 probes (Task 8)

**New — Server/Tools:**
- `src/FlaUI.Mcp.Server/Tools/InputTools.cs` — the 8 MCP tools: 5 SendInput (Tasks 10–11) + `desktop_input_status` (Task 12) + `desktop_set_caret`/`desktop_select_text_range` (Task 13)
- `src/FlaUI.Mcp.Core/Interaction/TextRangeInteractor.cs` — UIA `TextPattern` caret/selection ops for `set_caret`/`select_text_range` (Task 13)

**Modified:**
- `src/FlaUI.Mcp.Core/Interaction/ActionPolicy.cs` — F5 fail-closed (Task 1)
- `src/FlaUI.Mcp.Core/Interaction/InputLease.cs` — F1 empty-SID guard (Task 2)
- `src/FlaUI.Mcp.Server/Lease/LeaseWriter.cs` — F1 fail-closed SID (Task 2)
- `src/FlaUI.Mcp.Core/Interaction/InputGuard.cs` — F4 drop-endpoint audit + `AuthorizeTextMutation` (Tasks 3, 12)
- `src/FlaUI.Mcp.Core/Interaction/Interactor.cs` — `SetCaret`/`SelectTextRange` TextPattern ops (Task 12)
- `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` — `RunOnRefForInputAsync` overload exposing `win` (Task 9)
- `src/FlaUI.Mcp.Server/Program.cs` — DI registration of the input stack (Task 9)
- `README.md`, `ROADMAP.md`, `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj`, the Inno Setup `.iss` — docs + version bump (Task 14)

**New — tests (`test/FlaUI.Mcp.Tests/Interaction/` unless noted):**
- `ActionPolicyFailClosedTests.cs` (T1), additions to `InputLeaseTests.cs` (T2), additions to `InputGuardTests.cs` (T3),
  `KeyChordParserTests.cs` (T4), `VirtualDesktopMapTests.cs` + `UnicodeKeyInputTests.cs` (T5), `CoordinateMathTests.cs` (T6),
  `InputTargetingTests.cs` (T9, Desktop), `InputToolsTests.cs` (T10–T11, Desktop console-only), `TextRangeInteractorTests.cs` (T12, Desktop console-only).

---

## Targeting contract (pinned — applies to every tool)

- **`ActionTarget(nint Root, int Pid, string? ProcessName, string? WindowClass)`** (existing, `InputGuard.cs:106`).
- **Ref path** (`type`/`key`/`click`/`set_caret`/`select_text_range`): resolved from the **top-level window** UIA element via `InputTargeting.ResolveRefTarget(win)` — `Root = win.Properties.NativeWindowHandle`, `Pid = win.Properties.ProcessId`, `ProcessName` via `Process.GetProcessById`, `WindowClass = win.Properties.ClassName`. A top-level window's `NativeWindowHandle` IS its `GA_ROOT`, so it compares equal to the leaf's `GetForegroundRoot()` (which is `GA_ROOT(GetForegroundWindow())`).
- **Coordinate path** (`click_at`/`drag`): resolved from `IPlatformEnvironment.HitTestRoot(physX, physY)` → `PointTarget(Root, ProcessName, WindowClass)`; `Pid` is left `0` (audit shows `pid=0`; identity for the deny-list comes from process name + class).
- **Atomic pre-send re-verify** lives IN the leaf (`Win32SyntheticInput`), on the same thread as `SendInput`, via the already-shipped `InputReverify.AssertSameRoot`. Key verbs re-verify against `GetForegroundRoot()`; mouse verbs against `HitTestRoot(x,y).Root`.

---

### Task 1: F5 — fail-closed classification of an unidentifiable target

**Why:** `ActionPolicy.Classify(null, null)` currently returns `Allowed` (verified `ActionPolicy.cs:24-34`). The coordinate path fills `ProcessName`/`WindowClass` from `HitTestRoot`, which returns nulls when no window is under the point → the deny-list would fail OPEN. Fix centrally: an untaggable target is `Denied`.

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Interaction/ActionPolicy.cs:24-34`
- Test: `test/FlaUI.Mcp.Tests/Interaction/ActionPolicyFailClosedTests.cs`

- [ ] **Step 1: Write the failing test**

Create `test/FlaUI.Mcp.Tests/Interaction/ActionPolicyFailClosedTests.cs`:

```csharp
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class ActionPolicyFailClosedTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", null)]
    [InlineData(null, "   ")]
    [InlineData(null, "DirectUIHWND")]      // BLOCKER (agy): proc unresolvable (elevated/protected) but
    [InlineData("", "Chrome_WidgetWin_1")]  // class IS resolvable -> must NOT fall through to Allowed
    public void Unidentifiable_target_is_denied_not_allowed(string? proc, string? cls)
        => Assert.Equal(ActionVerdict.Denied, ActionPolicy.Classify(proc, cls));

    [Fact]
    public void A_named_benign_target_is_still_allowed()
        => Assert.Equal(ActionVerdict.Allowed, ActionPolicy.Classify("notepad", "Notepad"));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&FullyQualifiedName~ActionPolicyFailClosed"`
Expected: FAIL — `Unidentifiable_target_is_denied_not_allowed` asserts `Denied` but gets `Allowed`.

- [ ] **Step 3: Implement — add the fail-closed branch FIRST in `Classify`**

In `src/FlaUI.Mcp.Core/Interaction/ActionPolicy.cs`, replace the body of `Classify` (currently lines 24-34) with:

```csharp
    public static ActionVerdict Classify(string? processName, string? windowClass)
    {
        var proc = processName?.Trim();
        var cls = windowClass?.Trim();
        // F5 (fail-closed, hardened per agy BLOCKER): a target with NO resolvable process name is
        // intrinsically untrustworthy and must be refused — never allowed. This closes the bypass where
        // an elevated/protected target makes Process.GetProcessById throw (proc=null) yet Win32
        // GetClassName still returns a class (e.g. "DirectUIHWND"); the old `proc && cls` empty check
        // skipped the fail-closed branch AND the denied-process check, falling through to Allowed.
        // Every legitimate target (ref or coordinate) resolves an in-session process name.
        if (string.IsNullOrEmpty(proc))
            return ActionVerdict.Denied;
        if (DeniedProcesses.Contains(proc) || PerceptionPolicy.IsDenied(proc))
            return ActionVerdict.Denied;
        if ((!string.IsNullOrEmpty(cls) && InterlockedClasses.Contains(cls)) ||
            InterlockedProcesses.Contains(proc))
            return ActionVerdict.Interlocked;
        return ActionVerdict.Allowed;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop"`
Expected: `Passed!  - Failed: 0` — **121** tests (114 baseline + 7 new cases). (Counts in this plan are indicative; the binding gate is `Failed: 0`.)

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Interaction/ActionPolicy.cs test/FlaUI.Mcp.Tests/Interaction/ActionPolicyFailClosedTests.cs
git commit -m "fix(input): F5 — classify an unidentifiable target as Denied (fail-closed coordinate deny-list)"
```

---

### Task 2: F1 — lease SID fail-closed

**Why:** Two seams (verified): `LeaseWriter.CurrentSid()` (`LeaseWriter.cs:30-34`) writes a lease with `sid="unknown"` if `WindowsIdentity` fails; `InputLease.IsValidNow` (`InputLease.cs:14-15`) does an ordinal-ignore-case equality, so an `"unknown"` lease validated against an `"unknown"` server SID makes SID-binding a no-op. Harden both ends: the writer refuses to write an unsecured lease; validity rejects an empty/`"unknown"` current SID.

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Interaction/InputLease.cs:14-15`
- Modify: `src/FlaUI.Mcp.Server/Lease/LeaseWriter.cs:30-34`
- Test: append to `test/FlaUI.Mcp.Tests/Interaction/InputLeaseTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `test/FlaUI.Mcp.Tests/Interaction/InputLeaseTests.cs` (inside the existing test class — STATE-VERIFY the class name/namespace before inserting):

```csharp
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    public void A_lease_is_invalid_when_the_current_sid_is_unresolved(string? currentSid)
    {
        var sid = "S-1-5-21-7";
        var lease = new InputLease(new DateTime(2999, 1, 1, 0, 0, 0, DateTimeKind.Utc), sid, Array.Empty<string>());
        Assert.False(lease.IsValidNow(DateTime.UtcNow, currentSid!));
    }

    [Fact]
    public void A_lease_whose_own_sid_is_unknown_never_validates()
    {
        var lease = new InputLease(new DateTime(2999, 1, 1, 0, 0, 0, DateTimeKind.Utc), "unknown", Array.Empty<string>());
        Assert.False(lease.IsValidNow(DateTime.UtcNow, "unknown"));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&FullyQualifiedName~InputLease"`
Expected: FAIL — current `IsValidNow` returns `true` for the `"unknown"`/`"unknown"` case and may NRE/return true for empty.

- [ ] **Step 3a: Implement the validity guard**

In `src/FlaUI.Mcp.Core/Interaction/InputLease.cs`, replace `IsValidNow` (lines 14-15) with:

```csharp
    public bool IsValidNow(DateTime utcNow, string currentSid) =>
        ExpiryUtc > utcNow
        && !string.IsNullOrWhiteSpace(currentSid)
        && !string.Equals(currentSid, "unknown", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(Sid, "unknown", StringComparison.OrdinalIgnoreCase)
        && string.Equals(Sid, currentSid, StringComparison.OrdinalIgnoreCase);
```

- [ ] **Step 3b: Implement the writer fail-closed**

In `src/FlaUI.Mcp.Server/Lease/LeaseWriter.cs`, replace `CurrentSid()` (lines 30-34) with a version that throws rather than writing an unsecured lease:

```csharp
    private static string CurrentSid()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var sid = id.User?.Value;
            if (string.IsNullOrWhiteSpace(sid))
                throw new InvalidOperationException("Could not resolve the current user's SID; refusing to write an unsecured lease.");
            return sid;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Could not resolve the current user's SID; refusing to write an unsecured lease.", ex);
        }
    }
```

> SHAPE NOTE: `Grant` (`LeaseWriter.cs:12-21`) calls `CurrentSid()`; the throw now propagates out of `Grant` → `CliRouter` `unlock` case (`CliRouter.cs:51-56`). That is the intended fail-closed behavior (the CLI prints the framework error and returns non-zero rather than silently writing an unusable lease). Do NOT add a catch that re-prints `"unknown"`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop"`
Expected: `Passed!  - Failed: 0` — **125** tests (120 + 5 new).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Interaction/InputLease.cs src/FlaUI.Mcp.Server/Lease/LeaseWriter.cs test/FlaUI.Mcp.Tests/Interaction/InputLeaseTests.cs
git commit -m "fix(input): F1 — lease SID fail-closed (reject unknown/empty SID; writer refuses unsecured lease)"
```

---

### Task 3: F4 + F2 + F3 — audit the drag drop endpoint; pin budget + wrong-SID integration

**Why:** F4 — `InputGuard.Authorize` (`InputGuard.cs:33-61`) records audit for `primary` only; a drag's drop endpoint goes unaudited. F2/F3 — the `InputBudgetExceeded` and wrong-SID paths are exercised only by unit tests of `ActionBudget`/`InputLease`, not through the assembled `InputGuard` pipeline; close that gap with integration tests.

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Interaction/InputGuard.cs:33-61`
- Test: append to `test/FlaUI.Mcp.Tests/Interaction/InputGuardTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to the `InputGuardTests` class (`InputGuardTests.cs`) — reuse the existing `Build`, `ValidLease`, `Sid`, `Now`, `StubLeaseProvider` helpers (verified present). The `Build` helper currently discards the audit sink; add a sink-capturing builder beside it:

```csharp
    private static (InputGuard guard, RecordingSyntheticInput sink, System.IO.StringWriter audit) BuildWithAudit(
        InputLease? lease, DateTime leaseWrite = default, ActionBudget? budget = null)
    {
        var env = new FakePlatformEnvironment { CanDeliver = true, ForegroundRoot = nint.Zero };
        var sink = new RecordingSyntheticInput(env);
        var leaseProv = new StubLeaseProvider(lease, leaseWrite);
        var audit = new System.IO.StringWriter();
        var guard = new InputGuard(sink, env, leaseProv,
            budget ?? new ActionBudget(60, 60), new InputAudit(audit),
            currentSid: Sid, isElevated: false, allowElevation: false, clock: () => Now);
        return (guard, sink, audit);
    }

    [Fact]
    public void Budget_exhaustion_refuses_with_InputBudgetExceeded()
    {
        var (g, sink, _) = BuildWithAudit(ValidLease(), budget: new ActionBudget(maxPerWindow: 2, windowSeconds: 60));
        var t = new ActionTarget(nint.Zero, 0, "notepad", "Notepad");
        g.KeyType("a", t);
        g.KeyType("b", t);
        var ex = Assert.Throws<ToolException>(() => g.KeyType("c", t));
        Assert.Equal(ToolErrorCode.InputBudgetExceeded, ex.Code);
        Assert.Equal(2, sink.Calls.Count); // the 3rd never reached the sink
    }

    [Fact]
    public void A_lease_for_a_different_sid_refuses_with_InputNotLeased()
    {
        var foreign = new InputLease(new DateTime(2030, 1, 1, 0, 1, 0, DateTimeKind.Utc), "S-1-5-21-OTHER", Array.Empty<string>());
        var (g, _, _) = BuildWithAudit(foreign);
        var ex = Assert.Throws<ToolException>(() => g.KeyType("hi", new(nint.Zero, 0, "notepad", "Notepad")));
        Assert.Equal(ToolErrorCode.InputNotLeased, ex.Code);
    }

    [Fact]
    public void Drag_audits_BOTH_endpoints()
    {
        var (g, _, audit) = BuildWithAudit(ValidLease());
        var start = new ActionTarget((nint)11, 100, "explorer", "CabinetWClass");
        var end   = new ActionTarget((nint)22, 200, "notepad", "Notepad");
        g.MouseDrag(0, 0, 10, 10, "left", start, end);
        var log = audit.ToString();
        Assert.Contains("window=11", log);   // start endpoint
        Assert.Contains("window=22", log);   // drop endpoint (F4)
    }
```

- [ ] **Step 2: Run tests to verify failure state**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&FullyQualifiedName~InputGuardTests"`
Expected: `Budget_exhaustion...` and `A_lease_for_a_different_sid...` PASS already (pinning existing behavior); `Drag_audits_BOTH_endpoints` FAILS (drop endpoint `window=22` absent).

- [ ] **Step 3: Implement the F4 drop-endpoint audit**

In `src/FlaUI.Mcp.Core/Interaction/InputGuard.cs`, replace the single audit line at the end of `Authorize` (currently line 60) with both endpoints:

```csharp
        _audit.Record(primary.Root, primary.Pid, primary.ProcessName, action, payloadLength);
        if (secondary is { } drop)
            _audit.Record(drop.Root, drop.Pid, drop.ProcessName, action + "-drop", 1);
```

- [ ] **Step 3b: Budget retry-after legibility (agy R3, user-approved)**

So an over-budget agent waits the right amount instead of blind-retrying, expose the seconds until a slot frees and name it in the refusal. In `src/FlaUI.Mcp.Core/Interaction/ActionBudget.cs`, add (the oldest queued hit ages out of the window at `oldest + windowSeconds`):

```csharp
    /// <summary>Whole seconds until this window's oldest action ages out and a budget slot frees
    /// (0 if the window currently has spare budget). For the InputBudgetExceeded recovery hint.</summary>
    public int SecondsUntilFreeSlot(nint window, DateTime now)
    {
        lock (_gate)
        {
            if (!_hits.TryGetValue(window, out var q) || q.Count < _max) return 0;
            var secs = (q.Peek().AddSeconds(_windowSeconds) - now).TotalSeconds;
            return (int)Math.Max(0, Math.Ceiling(secs));
        }
    }
```

In `src/FlaUI.Mcp.Core/Interaction/InputGuard.cs`, replace the `InputBudgetExceeded` throw in `Authorize` (currently lines 55-58) with one that names the wait:

```csharp
        if (!_budget.TryConsume(primary.Root, now, leaseWrite))
            throw new ToolException(ToolErrorCode.InputBudgetExceeded,
                $"Synthetic-input rate limit exceeded for this window. Retry in ~{_budget.SecondsUntilFreeSlot(primary.Root, now)}s.",
                "wait for the window to clear, or re-grant the lease with `flaui-mcp unlock` to reset the budget");
```

Add a test to `InputGuardTests.cs` asserting the hint is present:

```csharp
    [Fact]
    public void Budget_exceeded_message_names_a_retry_wait()
    {
        var (g, _, _) = BuildWithAudit(ValidLease(), budget: new ActionBudget(maxPerWindow: 1, windowSeconds: 60));
        var t = new ActionTarget((nint)5, 0, "notepad", "Notepad");
        g.KeyType("a", t);
        var ex = Assert.Throws<ToolException>(() => g.KeyType("b", t));
        Assert.Contains("Retry in", ex.Message);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop"`
Expected: `Passed!  - Failed: 0` (gate is `Failed: 0`; +4 new InputGuard tests this task: budget-exceeded, wrong-SID, drag-both-endpoints, budget-retry-wait).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Interaction/InputGuard.cs src/FlaUI.Mcp.Core/Interaction/ActionBudget.cs test/FlaUI.Mcp.Tests/Interaction/InputGuardTests.cs
git commit -m "feat(input): F4 audit drag drop endpoint; budget retry-after hint; pin budget + wrong-SID guard integration (F2/F3)"
```

---

### Task 4: `KeyChordParser` — the `desktop_key` grammar (pure)

**Why:** Spec §4 `desktop_key` grammar: `+`-delimited, zero-or-more modifiers from `Ctrl|Alt|Shift|Win` + exactly one key from a fixed table (letters/digits, the named keys, `F1`–`F24`); unknown token ⇒ `InvalidArguments`. Pure, fully headless-testable; the leaf consumes its output.

**Files:**
- Create: `src/FlaUI.Mcp.Core/Interaction/KeyChordParser.cs`
- Test: `test/FlaUI.Mcp.Tests/Interaction/KeyChordParserTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `test/FlaUI.Mcp.Tests/Interaction/KeyChordParserTests.cs`:

```csharp
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class KeyChordParserTests
{
    [Fact]
    public void Parses_a_single_letter()
    {
        var c = KeyChordParser.Parse("a");
        Assert.Empty(c.ModifierVks);
        Assert.Equal((ushort)0x41, c.KeyVk); // VK_A
    }

    [Fact]
    public void Parses_modifiers_then_key_case_insensitively()
    {
        var c = KeyChordParser.Parse("Ctrl+Shift+S");
        Assert.Equal(new ushort[] { 0x11, 0x10 }, c.ModifierVks); // VK_CONTROL, VK_SHIFT
        Assert.Equal((ushort)0x53, c.KeyVk); // VK_S
    }

    [Theory]
    [InlineData("Enter", (ushort)0x0D)]
    [InlineData("Tab", (ushort)0x09)]
    [InlineData("Esc", (ushort)0x1B)]
    [InlineData("F5", (ushort)0x74)]
    [InlineData("PageDown", (ushort)0x22)]
    [InlineData("Left", (ushort)0x25)]
    public void Parses_named_keys(string token, ushort expectedVk)
        => Assert.Equal(expectedVk, KeyChordParser.Parse(token).KeyVk);

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl")]              // modifier with no key
    [InlineData("Ctrl+Alt")]         // two modifiers, no key
    [InlineData("Nope")]             // unknown key token
    [InlineData("Ctrl+Ctrl+A")]      // duplicate modifier as key slot
    [InlineData("A+B")]              // two keys
    public void Rejects_bad_grammar_with_InvalidArguments(string chord)
    {
        var ex = Assert.Throws<ToolException>(() => KeyChordParser.Parse(chord));
        Assert.Equal(ToolErrorCode.InvalidArguments, ex.Code);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&FullyQualifiedName~KeyChordParser"`
Expected: FAIL — `KeyChordParser` does not exist (compile error).

- [ ] **Step 3: Implement `KeyChordParser`**

Create `src/FlaUI.Mcp.Core/Interaction/KeyChordParser.cs`:

```csharp
using System;
using System.Collections.Generic;
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Parses a `desktop_key` chord string into Win32 virtual-key codes. Grammar (spec §4):
/// `+`-delimited, zero-or-more modifiers (Ctrl|Alt|Shift|Win) followed by exactly one key from the
/// fixed table. Any unknown/empty token or a malformed shape throws InvalidArguments — never a silent
/// mis-key.</summary>
public readonly record struct ParsedChord(ushort[] ModifierVks, ushort KeyVk);

public static class KeyChordParser
{
    private static readonly Dictionary<string, ushort> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = 0x11, ["Alt"] = 0x12, ["Shift"] = 0x10, ["Win"] = 0x5B, // VK_LWIN
    };

    private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = 0x0D, ["Tab"] = 0x09, ["Esc"] = 0x1B, ["Backspace"] = 0x08, ["Delete"] = 0x2E,
        ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27, ["Space"] = 0x20,
    };

    private static ToolException Bad(string chord) => new(
        ToolErrorCode.InvalidArguments,
        $"Unrecognized key chord '{chord}'. Use modifiers Ctrl|Alt|Shift|Win + one key (letter/digit, Enter Tab Esc Backspace Delete Home End PageUp PageDown Up Down Left Right Space, or F1-F24).",
        "send a single valid chord, e.g. \"Ctrl+S\" or \"Enter\"");

    public static ParsedChord Parse(string? chord)
    {
        if (string.IsNullOrWhiteSpace(chord)) throw Bad(chord ?? "");
        var tokens = chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) throw Bad(chord);

        var mods = new List<ushort>();
        for (int i = 0; i < tokens.Length - 1; i++) // every token except the last must be a modifier
        {
            if (!Modifiers.TryGetValue(tokens[i], out var mvk)) throw Bad(chord);
            if (mods.Contains(mvk)) throw Bad(chord); // duplicate modifier
            mods.Add(mvk);
        }
        var key = ResolveKey(tokens[^1]) ?? throw Bad(chord);
        return new ParsedChord(mods.ToArray(), key);
    }

    private static ushort? ResolveKey(string token)
    {
        if (Modifiers.ContainsKey(token)) return null; // a bare modifier is not a key
        if (NamedKeys.TryGetValue(token, out var nk)) return nk;
        if (token.Length == 1)
        {
            char ch = char.ToUpperInvariant(token[0]);
            if (ch is >= 'A' and <= 'Z') return (ushort)ch;       // VK_A..VK_Z == 'A'..'Z'
            if (ch is >= '0' and <= '9') return (ushort)ch;       // VK_0..VK_9 == '0'..'9'
            return null;
        }
        if ((token[0] is 'F' or 'f') && int.TryParse(token.AsSpan(1), out var n) && n is >= 1 and <= 24)
            return (ushort)(0x70 + (n - 1));                       // VK_F1 == 0x70
        return null;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop"`
Expected: `Passed!  - Failed: 0` — **142** tests (128 + 14 new cases).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Interaction/KeyChordParser.cs test/FlaUI.Mcp.Tests/Interaction/KeyChordParserTests.cs
git commit -m "feat(input): KeyChordParser — desktop_key chord grammar (pure, InvalidArguments on bad token)"
```

---

### Task 5: `Win32Interop` + `VirtualDesktopMap` + `UnicodeKeyInput` (struct layout + pure mappers)

**Why:** The `INPUT` struct/P/Invoke layout must exactly match the spiked layout (or `SendInput` silently returns 0 on x64). `VirtualDesktopMap` (physical px → 0–65535 over the virtual desktop) and `UnicodeKeyInput` (string → keyboard `INPUT[]` incl. surrogate pairs) are pure given the virtual-screen bounds — headless-testable, exactly the math the spike proved pixel-perfect.

**Files:**
- Create: `src/FlaUI.Mcp.Core/Interaction/Win32Interop.cs`
- Create: `src/FlaUI.Mcp.Core/Interaction/VirtualDesktopMap.cs`
- Create: `src/FlaUI.Mcp.Core/Interaction/UnicodeKeyInput.cs`
- Test: `test/FlaUI.Mcp.Tests/Interaction/VirtualDesktopMapTests.cs`, `UnicodeKeyInputTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `test/FlaUI.Mcp.Tests/Interaction/VirtualDesktopMapTests.cs`:

```csharp
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class VirtualDesktopMapTests
{
    // virtual screen origin (0,0), size 1920x1080 (the spike box).
    [Theory]
    [InlineData(0, 0, 0, 0)]                 // top-left
    [InlineData(1919, 1079, 65535, 65535)]   // bottom-right maps to max
    [InlineData(960, 540, 32777, 32784)]     // spike center (round((px - origin) * 65535 / (size-1)))
    public void Maps_physical_to_absolute_0_65535(int px, int py, int ax, int ay)
    {
        var (gotX, gotY) = VirtualDesktopMap.ToAbsolute(px, py, originX: 0, originY: 0, width: 1920, height: 1080);
        Assert.Equal(ax, gotX);
        Assert.Equal(ay, gotY);
    }

    [Fact]
    public void Honors_a_negative_virtual_origin_secondary_monitor_left_of_primary()
    {
        // virtual screen from x=-1920 width 3840: a point at x=-1920 maps to 0.
        var (gotX, _) = VirtualDesktopMap.ToAbsolute(-1920, 0, originX: -1920, originY: 0, width: 3840, height: 1080);
        Assert.Equal(0, gotX);
    }
}
```

Create `test/FlaUI.Mcp.Tests/Interaction/UnicodeKeyInputTests.cs`:

```csharp
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class UnicodeKeyInputTests
{
    [Fact]
    public void Emits_a_keydown_and_keyup_per_bmp_char()
    {
        var inputs = UnicodeKeyInput.Build("ab");
        Assert.Equal(4, inputs.Length); // 2 chars * (down+up)
    }

    [Fact]
    public void Emits_surrogate_pairs_as_separate_units()
    {
        // U+1F600 GRINNING FACE is a surrogate pair -> 2 UTF-16 units -> 4 INPUTs.
        var inputs = UnicodeKeyInput.Build("\U0001F600");
        Assert.Equal(4, inputs.Length);
    }

    [Fact]
    public void Each_unit_carries_KEYEVENTF_UNICODE_and_the_scan_code_is_the_utf16_unit()
    {
        var inputs = UnicodeKeyInput.Build("A");
        Assert.Equal((uint)1, inputs[0].type);               // INPUT_KEYBOARD
        Assert.Equal((ushort)'A', inputs[0].U.ki.wScan);
        Assert.Equal(Win32Interop.KEYEVENTF_UNICODE, inputs[0].U.ki.dwFlags);
        Assert.Equal(Win32Interop.KEYEVENTF_UNICODE | Win32Interop.KEYEVENTF_KEYUP, inputs[1].U.ki.dwFlags);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&(FullyQualifiedName~VirtualDesktopMap|FullyQualifiedName~UnicodeKeyInput)"`
Expected: FAIL — types do not exist (compile error).

- [ ] **Step 3a: Implement `Win32Interop`** (struct layout copied verbatim from the validated spike)

Create `src/FlaUI.Mcp.Core/Interaction/Win32Interop.cs`:

```csharp
using System;
using System.Runtime.InteropServices;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Shared Win32 P/Invoke surface for the synthetic-input leaf and platform probes. The INPUT
/// struct layout is byte-for-byte the layout validated by the 2026-06-30 active-RDP spike (SendInput
/// returned non-zero / fired). Do NOT reshape — a wrong size/alignment makes SendInput silently
/// return 0 on x64.</summary>
public static class Win32Interop
{
    public const uint INPUT_MOUSE = 0;
    public const uint INPUT_KEYBOARD = 1;

    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;

    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    public const uint GA_ROOT = 2;

    // GetSystemMetrics indices for the virtual screen.
    public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    public const uint MAXIMUM_ALLOWED = 0x02000000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] public static extern nint WindowFromPhysicalPoint(POINT p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(nint hWnd, System.Text.StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)] public static extern nint OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseDesktop(nint hDesktop);
}

[StructLayout(LayoutKind.Sequential)]
public struct POINT { public int X; public int Y; }

[StructLayout(LayoutKind.Sequential)]
public struct INPUT { public uint type; public InputUnion U; }

[StructLayout(LayoutKind.Explicit)]
public struct InputUnion
{
    [FieldOffset(0)] public MOUSEINPUT mi;
    [FieldOffset(0)] public KEYBDINPUT ki;
    [FieldOffset(0)] public HARDWAREINPUT hi;
}

[StructLayout(LayoutKind.Sequential)]
public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public nint dwExtraInfo; }

[StructLayout(LayoutKind.Sequential)]
public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public nint dwExtraInfo; }

[StructLayout(LayoutKind.Sequential)]
public struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }
```

> SHAPE NOTE: structs are declared at file scope after the static class. Public so the pure mappers/tests can assert field values. If the C# version rejects a struct member access, STOP and report `STATE_MISMATCH` — do not reshape the struct.

- [ ] **Step 3b: Implement `VirtualDesktopMap`**

Create `src/FlaUI.Mcp.Core/Interaction/VirtualDesktopMap.cs`:

```csharp
using System;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Maps a physical screen pixel into the 0-65535 absolute coordinate space over the VIRTUAL
/// desktop that MOUSEEVENTF_ABSOLUTE|MOUSEEVENTF_VIRTUALDESK expects. Pure given the virtual-screen
/// bounds (origin can be negative for monitors left of / above the primary). The 2026-06-30 spike
/// proved this formula pixel-perfect (target 960,540 -> landed 960,540).</summary>
public static class VirtualDesktopMap
{
    public static (int ax, int ay) ToAbsolute(int physX, int physY, int originX, int originY, int width, int height)
    {
        int ax = Scale(physX - originX, width);
        int ay = Scale(physY - originY, height);
        return (ax, ay);
    }

    // 65535 * offset / (span - 1), rounded, clamped to [0, 65535]. (span-1 so the last pixel hits 65535.)
    private static int Scale(int offset, int span)
    {
        if (span <= 1) return 0;
        long v = (long)Math.Round(65535.0 * offset / (span - 1));
        return (int)Math.Clamp(v, 0, 65535);
    }
}
```

- [ ] **Step 3c: Implement `UnicodeKeyInput`**

Create `src/FlaUI.Mcp.Core/Interaction/UnicodeKeyInput.cs`:

```csharp
using System.Collections.Generic;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Builds the keyboard INPUT[] for a unicode string via KEYEVENTF_UNICODE (wVk=0, wScan=UTF-16
/// unit). Each UTF-16 code unit emits a down + up; a non-BMP char is naturally two units (surrogate
/// pair) -> four INPUTs, exactly as Windows expects. Pure (no SendInput) -> headless-testable.</summary>
public static class UnicodeKeyInput
{
    public static INPUT[] Build(string text)
    {
        var list = new List<INPUT>((text?.Length ?? 0) * 2);
        foreach (char ch in text ?? string.Empty)
        {
            list.Add(Unit(ch, Win32Interop.KEYEVENTF_UNICODE));
            list.Add(Unit(ch, Win32Interop.KEYEVENTF_UNICODE | Win32Interop.KEYEVENTF_KEYUP));
        }
        return list.ToArray();
    }

    private static INPUT Unit(char ch, uint flags) => new()
    {
        type = Win32Interop.INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = flags, time = 0, dwExtraInfo = 0 } }
    };
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop"`
Expected: `Passed!  - Failed: 0` — **150** tests (142 + 8 new cases). Build still 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Interaction/Win32Interop.cs src/FlaUI.Mcp.Core/Interaction/VirtualDesktopMap.cs src/FlaUI.Mcp.Core/Interaction/UnicodeKeyInput.cs test/FlaUI.Mcp.Tests/Interaction/VirtualDesktopMapTests.cs test/FlaUI.Mcp.Tests/Interaction/UnicodeKeyInputTests.cs
git commit -m "feat(input): Win32Interop struct layout + VirtualDesktopMap + UnicodeKeyInput (pure, spike-validated)"
```

---

### Task 6: `CoordinateMath` — window-relative pct → physical point (pure)

**Why:** Spec §5: `xPct`/`yPct` ∈ [0,1] are relative to the target window's physical bounding rect. The mapping is pure given the rect; `desktop_click_at`/`desktop_drag` consume it before hit-testing.

**Files:**
- Create: `src/FlaUI.Mcp.Core/Interaction/CoordinateMath.cs`
- Test: `test/FlaUI.Mcp.Tests/Interaction/CoordinateMathTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `test/FlaUI.Mcp.Tests/Interaction/CoordinateMathTests.cs`:

```csharp
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class CoordinateMathTests
{
    [Theory]
    [InlineData(0.0, 0.0, 100, 200)]     // top-left corner of a rect at (100,200)
    [InlineData(1.0, 1.0, 740, 680)]     // bottom-right: 100+640=740, 200+480=680
    [InlineData(0.5, 0.5, 420, 440)]     // center: 100+320, 200+240
    public void Maps_pct_into_the_window_rect(double xp, double yp, int ex, int ey)
    {
        var (px, py) = CoordinateMath.PctToPhysical(left: 100, top: 200, width: 640, height: 480, xp, yp);
        Assert.Equal(ex, px);
        Assert.Equal(ey, py);
    }

    [Theory]
    [InlineData(-0.01, 0.5)]
    [InlineData(0.5, 1.01)]
    public void Rejects_out_of_range_fractions(double xp, double yp)
    {
        var ex = Assert.Throws<ToolException>(() => CoordinateMath.PctToPhysical(0, 0, 100, 100, xp, yp));
        Assert.Equal(ToolErrorCode.InvalidArguments, ex.Code);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&FullyQualifiedName~CoordinateMath"`
Expected: FAIL — `CoordinateMath` does not exist.

- [ ] **Step 3: Implement `CoordinateMath`**

Create `src/FlaUI.Mcp.Core/Interaction/CoordinateMath.cs`:

```csharp
using System;
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Maps a window-relative fractional point (spec §5: xPct/yPct in [0,1], relative to the
/// target window's physical bounding rect) to a physical screen pixel. Pure given the rect.</summary>
public static class CoordinateMath
{
    public static (int px, int py) PctToPhysical(int left, int top, int width, int height, double xPct, double yPct)
    {
        if (xPct is < 0.0 or > 1.0 || yPct is < 0.0 or > 1.0)
            throw new ToolException(ToolErrorCode.InvalidArguments,
                $"Coordinate fractions must be in [0,1]; got ({xPct},{yPct}).",
                "pass xPct/yPct as fractions of the window's width/height");
        int px = left + (int)Math.Round(xPct * width);
        int py = top + (int)Math.Round(yPct * height);
        return (px, py);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop"`
Expected: `Passed!  - Failed: 0` — **155** tests (150 + 5 new cases).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Interaction/CoordinateMath.cs test/FlaUI.Mcp.Tests/Interaction/CoordinateMathTests.cs
git commit -m "feat(input): CoordinateMath — window-relative pct to physical point (pure, §5 contract)"
```

---

### Task 7: `Win32SyntheticInput` — the real `SendInput` leaf (build-only / spiked)

**Why:** The single un-automated surface. Mirrors the validated spike exactly. Performs the atomic pre-send re-verify (via injected `IPlatformEnvironment` + `InputReverify`) on the same thread as `SendInput`.

**Files:**
- Create: `src/FlaUI.Mcp.Core/Interaction/Win32SyntheticInput.cs`

> No automated test — this is the deliberately-tiny leaf the spec (§6) excludes from automated coverage; it is validated by the spike + the user's manual validation at the 4b release gate. Gate this task on **build success only** plus the unchanged headless suite.

- [ ] **Step 1: Implement `Win32SyntheticInput`**

Create `src/FlaUI.Mcp.Core/Interaction/Win32SyntheticInput.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>The real SendInput leaf (4b). Apartment-agnostic — the caller invokes it on a Task.Run so
/// the atomic pre-send re-verify and the SendInput call share one thread (spec §2). Re-verifies the
/// expected target with the LIVE foreground/point root immediately before firing; a mismatch aborts and
/// fires nothing (InputReverify throws). Validated by the 2026-06-30 active-RDP spike.</summary>
public sealed class Win32SyntheticInput : ISyntheticInput
{
    private readonly IPlatformEnvironment _env;
    public Win32SyntheticInput(IPlatformEnvironment env) => _env = env;

    public void KeyType(string text, nint expectedForegroundRoot)
    {
        Reverify(expectedForegroundRoot, _env.GetForegroundRoot());
        Send(UnicodeKeyInput.Build(text ?? string.Empty));
    }

    public void KeyChord(string[] modifiers, string key, nint expectedForegroundRoot)
    {
        Reverify(expectedForegroundRoot, _env.GetForegroundRoot());
        var chord = KeyChordParser.Parse((modifiers is { Length: > 0 } ? string.Join("+", modifiers) + "+" : "") + key);
        var inputs = new List<INPUT>();
        foreach (var m in chord.ModifierVks) inputs.Add(Vk(m, 0));
        inputs.Add(Vk(chord.KeyVk, 0));
        inputs.Add(Vk(chord.KeyVk, Win32Interop.KEYEVENTF_KEYUP));
        for (int i = chord.ModifierVks.Length - 1; i >= 0; i--) inputs.Add(Vk(chord.ModifierVks[i], Win32Interop.KEYEVENTF_KEYUP));
        Send(inputs.ToArray());
    }

    public void MouseClick(int physX, int physY, string button, int count, string[] modifiers, nint expectedRootAtPoint)
    {
        Reverify(expectedRootAtPoint, _env.HitTestRoot(physX, physY).Root);
        var (down, up) = ButtonFlags(button);
        var (ax, ay) = AbsolutePoint(physX, physY);
        var inputs = new List<INPUT> { Move(ax, ay) };
        for (int i = 0; i < Math.Clamp(count, 1, 2); i++) { inputs.Add(Mouse(ax, ay, down)); inputs.Add(Mouse(ax, ay, up)); }
        Send(inputs.ToArray());
    }

    public void MouseDrag(int startX, int startY, int endX, int endY, string button, nint expectedRootAtEnd)
    {
        var (down, up) = ButtonFlags(button);
        var (sax, say) = AbsolutePoint(startX, startY);
        Send(new[] { Move(sax, say), Mouse(sax, say, down) });
        // re-verify the END root immediately before the drop (spec §3.2: an overlay can move in the gap).
        // CRITICAL (agy BLOCKER): once the button is DOWN it MUST be released on every path, or the OS
        // mouse is left globally stuck-down. If the re-verify aborts, release at the START point — this
        // cancels the drag harmlessly (drops back onto the origin) and NEVER drops into the suspect/denied
        // end window — then propagate the abort.
        try { Reverify(expectedRootAtEnd, _env.HitTestRoot(endX, endY).Root); }
        catch
        {
            Send(new[] { Move(sax, say), Mouse(sax, say, up) }); // release at origin, not at the suspect end
            throw;
        }
        var (eax, eay) = AbsolutePoint(endX, endY);
        Send(new[] { Move(eax, eay), Mouse(eax, eay, up) });
    }

    // agy R2: distinguish a focus-steal (root changed to ANOTHER window -> ElementDisappearedDuringAction,
    // re-focus + retry) from the desktop locking mid-send (foreground/hit-test root collapses to 0 ->
    // InputDesktopUnavailable, the agent must get the session unlocked, not retry the element).
    private void Reverify(nint expected, nint actual)
    {
        if (actual == 0 && !_env.SessionState().CanDeliverInput)
            throw new ToolException(ToolErrorCode.InputDesktopUnavailable,
                "The interactive input desktop became unavailable mid-send (locked / disconnected).",
                "connect and unlock the session, then retry");
        InputReverify.AssertSameRoot(expected, actual);
    }

    private static (int ax, int ay) AbsolutePoint(int physX, int physY)
    {
        int ox = Win32Interop.GetSystemMetrics(Win32Interop.SM_XVIRTUALSCREEN);
        int oy = Win32Interop.GetSystemMetrics(Win32Interop.SM_YVIRTUALSCREEN);
        int w = Win32Interop.GetSystemMetrics(Win32Interop.SM_CXVIRTUALSCREEN);
        int h = Win32Interop.GetSystemMetrics(Win32Interop.SM_CYVIRTUALSCREEN);
        return VirtualDesktopMap.ToAbsolute(physX, physY, ox, oy, w, h);
    }

    private static (uint down, uint up) ButtonFlags(string button) => button?.Trim().ToLowerInvariant() switch
    {
        "right" => (Win32Interop.MOUSEEVENTF_RIGHTDOWN, Win32Interop.MOUSEEVENTF_RIGHTUP),
        "middle" => (Win32Interop.MOUSEEVENTF_MIDDLEDOWN, Win32Interop.MOUSEEVENTF_MIDDLEUP),
        _ => (Win32Interop.MOUSEEVENTF_LEFTDOWN, Win32Interop.MOUSEEVENTF_LEFTUP),
    };

    private static INPUT Vk(ushort vk, uint flags) => new()
    { type = Win32Interop.INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = flags, time = 0, dwExtraInfo = 0 } } };

    private static INPUT Move(int ax, int ay) => Mouse(ax, ay, Win32Interop.MOUSEEVENTF_MOVE);

    private static INPUT Mouse(int ax, int ay, uint action) => new()
    {
        type = Win32Interop.INPUT_MOUSE,
        U = new InputUnion { mi = new MOUSEINPUT { dx = ax, dy = ay, mouseData = 0,
            dwFlags = action | Win32Interop.MOUSEEVENTF_ABSOLUTE | Win32Interop.MOUSEEVENTF_VIRTUALDESK, time = 0, dwExtraInfo = 0 } }
    };

    private static void Send(INPUT[] inputs)
    {
        if (inputs.Length == 0) return;
        Win32Interop.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }
}
```

> SHAPE NOTE: `KeyChord` re-assembles the chord string and calls `KeyChordParser.Parse` so the leaf validates the chord identically to the tool layer (single source of grammar truth). If `modifiers` is empty it parses the bare key. Modifiers press in order and release in reverse.

- [ ] **Step 2: Build**

Run: `dotnet build FlaUI.Mcp.slnx`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 3: Run the headless suite (unchanged)**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop"`
Expected: `Passed!  - Failed: 0` — **155** (no new tests; leaf is build-only).

- [ ] **Step 4: Commit**

```bash
git add src/FlaUI.Mcp.Core/Interaction/Win32SyntheticInput.cs
git commit -m "feat(input): Win32SyntheticInput real SendInput leaf (apartment-agnostic, atomic re-verify; spike-validated)"
```

---

### Task 8: `Win32PlatformEnvironment` — the real Win32 probes (build-only / spiked)

**Why:** The real `IPlatformEnvironment`: foreground root, coordinate hit-test resolver, and the fail-closed session-state oracle. All three probes were machine-validated by the spike.

**Files:**
- Create: `src/FlaUI.Mcp.Core/Interaction/Win32PlatformEnvironment.cs`

> Build-only/spiked, like Task 7.

- [ ] **Step 1: Implement `Win32PlatformEnvironment`**

Create `src/FlaUI.Mcp.Core/Interaction/Win32PlatformEnvironment.cs`:

```csharp
using System;
using System.Diagnostics;
using System.Text;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>The real Win32 probe seam (4b). Foreground root, coordinate hit-test resolver (DPI-correct
/// WindowFromPhysicalPoint -> GA_ROOT -> owning process+class), and the fail-closed session oracle
/// (OpenInputDesktop succeeds AND a foreground window exists). Validated by the 2026-06-30 spike.</summary>
public sealed class Win32PlatformEnvironment : IPlatformEnvironment
{
    public nint GetForegroundRoot()
    {
        var fg = Win32Interop.GetForegroundWindow();
        return fg == 0 ? 0 : Win32Interop.GetAncestor(fg, Win32Interop.GA_ROOT);
    }

    public PointTarget HitTestRoot(int physX, int physY)
    {
        var hit = Win32Interop.WindowFromPhysicalPoint(new POINT { X = physX, Y = physY });
        if (hit == 0) return new PointTarget(0, null, null);
        var root = Win32Interop.GetAncestor(hit, Win32Interop.GA_ROOT);
        if (root == 0) return new PointTarget(0, null, null);

        Win32Interop.GetWindowThreadProcessId(root, out uint pid);
        string? proc = null;
        try { using var p = Process.GetProcessById((int)pid); proc = p.ProcessName; } catch { }

        var sb = new StringBuilder(256);
        string? cls = Win32Interop.GetClassName(root, sb, sb.Capacity) > 0 ? sb.ToString() : null;
        return new PointTarget(root, proc, cls);
    }

    public SessionInputState SessionState()
    {
        bool foreground = Win32Interop.GetForegroundWindow() != 0;
        nint desk = Win32Interop.OpenInputDesktop(0, false, Win32Interop.MAXIMUM_ALLOWED);
        bool deskOk = desk != 0;
        if (deskOk) Win32Interop.CloseDesktop(desk);
        return new SessionInputState(deskOk && foreground); // fail-closed: both must hold
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build FlaUI.Mcp.slnx`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 3: Headless suite unchanged**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop"` → **155** PASS.

- [ ] **Step 4: Commit**

```bash
git add src/FlaUI.Mcp.Core/Interaction/Win32PlatformEnvironment.cs
git commit -m "feat(input): Win32PlatformEnvironment real probes (foreground/hit-test/fail-closed session oracle; spike-validated)"
```

---

### Task 9: `InputTargeting` + `RunOnRefForInputAsync` overload + DI wiring

**Why:** Resolve a ref's top-level `ActionTarget` from the WINDOW element (the ref `el` may be a windowless child), then wire the whole input stack into DI so the tools can resolve `InputGuard`. `InputAudit` writes to `Console.Error` (matching the clipboard/stderr audit precedent). The server's `currentSid` and elevation feed the guard.

**Files:**
- Create: `src/FlaUI.Mcp.Core/Interaction/InputTargeting.cs`
- Modify: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` (add `RunOnRefForInputAsync` overload after `RunOnRefActionAsync`, ~line 50)
- Modify: `src/FlaUI.Mcp.Server/Program.cs:31-36` (DI registrations)
- Test: `test/FlaUI.Mcp.Tests/Interaction/InputTargetingTests.cs` (Desktop)

- [ ] **Step 1: Write the failing test (Desktop — runs on this RDP box; UIA-only, no synthetic input)**

Create `test/FlaUI.Mcp.Tests/Interaction/InputTargetingTests.cs`:

```csharp
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

[Trait("Category", "Desktop")]
public class InputTargetingTests
{
    [Fact]
    public async Task Resolves_root_pid_process_and_class_from_a_window_element()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(app.Process.Id);
        var target = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) => InputTargeting.ResolveRefTarget(win));

        Assert.NotEqual(nint.Zero, target.Root);
        Assert.Equal(app.Process.Id, target.Pid);
        Assert.False(string.IsNullOrEmpty(target.ProcessName));
        Assert.False(string.IsNullOrEmpty(target.WindowClass));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category=Desktop&FullyQualifiedName~InputTargeting"` (this RDP box; UIA-pattern read works while connected).
Expected: FAIL — `InputTargeting` does not exist.

- [ ] **Step 3a: Implement `InputTargeting`**

Create `src/FlaUI.Mcp.Core/Interaction/InputTargeting.cs`:

```csharp
using System.Diagnostics;
using FlaUI.Core.AutomationElements;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Resolves the ref-path ActionTarget from a TOP-LEVEL window UIA element. A top-level window's
/// NativeWindowHandle IS its GA_ROOT, so it compares equal to the leaf's GetForegroundRoot()
/// (GA_ROOT(GetForegroundWindow())). Identity for the deny-list/audit comes from UIA (ProcessId +
/// ClassName) plus a process-name lookup — no Win32 needed here.</summary>
public static class InputTargeting
{
    public static ActionTarget ResolveRefTarget(AutomationElement window)
    {
        nint root = window.Properties.NativeWindowHandle.ValueOrDefault;
        int pid = -1;
        try { pid = window.Properties.ProcessId.ValueOrDefault; } catch { }
        string? proc = null;
        if (pid >= 0) { try { using var p = Process.GetProcessById(pid); proc = p.ProcessName; } catch { } }
        string? cls = null;
        try { cls = window.Properties.ClassName.ValueOrDefault; } catch { }
        return new ActionTarget(root, pid < 0 ? 0 : pid, proc, cls);
    }
}
```

- [ ] **Step 3b: Add the `win`-aware ref overload to `PerceptionManager`**

In `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs`, insert AFTER `RunOnRefActionAsync` (after line 50) — it is the same body but the callback also receives the resolved top-level `win` (needed for `InputTargeting.ResolveRefTarget`):

```csharp
    /// <summary>Like RunOnRefActionAsync but the callback also receives the resolved top-level WINDOW
    /// element (for input targeting: the ActionTarget's Root/Pid/Class come from the window, while the
    /// action runs against the ref'd element). Same transient-action-STA + offscreen preflight.</summary>
    public Task<T> RunOnRefForInputAsync<T>(WindowHandle handle, string @ref,
        Func<AutomationElement, AutomationElement, T> func, int timeoutMs)
    {
        var descriptor = _refs.Lookup(handle.Id, @ref).Descriptor;
        return _windows.RunOnWindowActionAsync(handle, (win, desktop) =>
        {
            var roots = PopupFinder.SearchRoots(win, desktop);
            var el = _refs.ResolveDescriptor(descriptor, roots, @ref);
            if (el.Properties.IsOffscreen.ValueOrDefault)
                throw new ToolException(ToolErrorCode.ElementNotActionable,
                    "Element is off-screen; cannot act on it reliably.", "desktop_scroll_into_view then retry");
            return func(win, el);
        }, timeoutMs);
    }
```

- [ ] **Step 3c: Wire DI in `Program.cs`**

In `src/FlaUI.Mcp.Server/Program.cs`, after the `ClipboardTools` registration (line 36) and before the `AddMcpServer()` block (line 38), insert the input stack. Add `using FlaUI.Mcp.Core.Interaction;` and `using System.Security.Principal;` at the top:

```csharp
// --- Phase 4b synthetic-input stack (InputGuard now LIVE in DI) ---
builder.Services.AddSingleton<IPlatformEnvironment, Win32PlatformEnvironment>();
builder.Services.AddSingleton<ISyntheticInput>(sp =>
    new Win32SyntheticInput(sp.GetRequiredService<IPlatformEnvironment>()));
builder.Services.AddSingleton<ILeaseProvider, FileLeaseProvider>();
builder.Services.AddSingleton(_ => new ActionBudget());            // defaults: 60 / 60s (spec §3.4)
builder.Services.AddSingleton(_ => new InputAudit(Console.Error)); // event-only, stderr (spec §3.4)
builder.Services.AddSingleton(sp =>
{
    var opts = sp.GetRequiredService<ServerOptions>();
    return new InputGuard(
        sp.GetRequiredService<ISyntheticInput>(),
        sp.GetRequiredService<IPlatformEnvironment>(),
        sp.GetRequiredService<ILeaseProvider>(),
        sp.GetRequiredService<ActionBudget>(),
        sp.GetRequiredService<InputAudit>(),
        currentSid: CurrentUserSid(),
        isElevated: ElevationGuard.IsElevated(),
        allowElevation: opts.AllowElevation);
});
builder.Services.AddSingleton<InputTools>();
```

Then add this local helper at the BOTTOM of `Program.cs` (top-level programs allow trailing local functions; if the file already ends with `return 0;`, place it after that line):

```csharp
static string CurrentUserSid()
{
    // Fail-soft to "unknown" here (NOT a throw — the server must still start for perception tools);
    // an "unknown" SID is rejected by InputLease.IsValidNow (F1), so input stays locked rather than
    // mis-binding. The lease WRITER (LeaseWriter, CLI) is the side that hard-fails on an unresolved SID.
    try { using var id = WindowsIdentity.GetCurrent(); return id.User?.Value ?? "unknown"; }
    catch { return "unknown"; }
}
```

> SHAPE NOTE: `InputGuard`, `ActionBudget`, `InputAudit` are concrete classes (no interface) — register the concrete type. `ISyntheticInput`/`IPlatformEnvironment`/`ILeaseProvider` register interface→impl. STATE-VERIFY that `Microsoft.Extensions.DependencyInjection` `GetRequiredService` is in scope (it is — `Program.cs:6`).

- [ ] **Step 4: Build + run both gates**

Run: `dotnet build FlaUI.Mcp.slnx` → 0 warnings.
Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop"` → **155** PASS (DI smoke is implicit; no new headless test).
Run: `dotnet test FlaUI.Mcp.slnx --filter "Category=Desktop&FullyQualifiedName~InputTargeting"` → 1 PASS (this box).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Interaction/InputTargeting.cs src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs src/FlaUI.Mcp.Server/Program.cs test/FlaUI.Mcp.Tests/Interaction/InputTargetingTests.cs
git commit -m "feat(input): InputTargeting + RunOnRefForInputAsync overload + DI wiring (InputGuard now live)"
```

---

### Task 10: `desktop_type` + `desktop_key` tools

**Why:** The first two synthetic-input tools. Ref/foreground path; `Focus()` then guard pipeline then leaf re-verify (spec §4). Type caps at 4096 UTF-16 units → `InvalidArguments`.

**Files:**
- Create: `src/FlaUI.Mcp.Server/Tools/InputTools.cs`
- Test: `test/FlaUI.Mcp.Tests/Interaction/InputToolsTests.cs` (Desktop, console-machine-only)

- [ ] **Step 1: Write the smoke tests (Desktop — synthetic input; CONSOLE-MACHINE-ONLY, not the headless gate)**

Create `test/FlaUI.Mcp.Tests/Interaction/InputToolsTests.cs`. These require a physical console + an active lease and are NOT runnable on the RDP CI box — they exist for the user's manual validation pass (spec §6). Mark each with a skip-reason guard that reads the lease so they no-op cleanly when input is locked:

```csharp
using System.Text.Json;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

// CONSOLE-MACHINE-ONLY: these fire real SendInput and need an active unlocked session + a granted lease
// (`flaui-mcp unlock --minutes 5`). They CANNOT pass on the headless RDP CI box. Not part of the
// Category!=Desktop gate. Run manually on a physical console as the 4b validation pass.
// agy R2: a no-lease run uses Assert.SkipWhen so it registers SKIPPED, never a silent PASS. STEP-0
// VERIFY: confirm the test framework provides Assert.SkipWhen(bool,string) (xUnit v3 — likely on .NET 10);
// if the repo is on xUnit v2, switch these to Xunit.SkippableFact ([SkippableFact] + Skip.If) instead —
// do NOT fall back to `if (...) return;` (that reads as PASS and gives false confidence). Report
// STATE_MISMATCH if neither skip API is available.
[Trait("Category", "Desktop")]
[Trait("Category", "SyntheticInput")]
public class InputToolsTests
{
    private static InputTools BuildTools(WindowManager mgr, PerceptionManager perception)
    {
        var env = new Win32PlatformEnvironment();
        var sink = new Win32SyntheticInput(env);
        var leases = new FileLeaseProvider();
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        var guard = new InputGuard(sink, env, leases, new ActionBudget(), new InputAudit(System.IO.TextWriter.Null),
            currentSid: sid, isElevated: false, allowElevation: false);
        return new InputTools(perception, mgr, new ServerOptions(ReadOnly: false, AllowElevation: false), guard, env);
    }

    private static bool InputLocked()
    {
        var lease = new FileLeaseProvider().Read(out _);
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        return lease is null || !lease.IsValidNow(System.DateTime.UtcNow, sid);
    }

    [Fact]
    public async Task Type_writes_text_into_the_focused_textbox()
    {
        Assert.SkipWhen(InputLocked(), "no active input lease — grant one on a console with `flaui-mcp unlock`");
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var handle = await mgr.OpenByPidAsync(app.Process.Id);

        // Snapshot to mint a ref for the Input TextBox.
        var snap = await perception.SnapshotAsync(handle, new SnapshotOptions());
        var inputRef = FindRefByAid(snap, "Input");

        var tools = BuildTools(mgr, perception);
        var json = await tools.DesktopType(handle.Id, inputRef, "hello-4b", 4000);
        Assert.DoesNotContain("\"error\"", json);

        var val = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            win.FindFirstDescendant(cf => cf.ByAutomationId("Input"))!.AsTextBox().Text);
        Assert.Equal("hello-4b", val);
    }

    // Helper: pull the first ref whose AutomationId matches (snapshot text contains aid=<id> ref=<rN>).
    private static string FindRefByAid(SnapshotResult snap, string aid)
        => SnapshotRefHelper.RefForAid(snap, aid); // reuse the existing 3b-2 helper (STATE-VERIFY name)
}
```

> STATE-VERIFY before writing: confirm the snapshot ref-extraction helper used by the 3b-2 `ContentToolsTests` (the task summary called it `RefForAid`). If its name/shape differs, match the existing helper rather than inventing one; if none is shared, inline the same regex the existing Desktop tests use. Report `STATE_MISMATCH` if the snapshot API (`SnapshotAsync`/`SnapshotOptions`/`SnapshotResult`) differs from this shape.

- [ ] **Step 2: Run to verify it fails to compile**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop"` (the project must still compile for the headless gate).
Expected: FAIL — `InputTools` does not exist (compile error across the test assembly).

- [ ] **Step 3: Implement `desktop_type` + `desktop_key` in `InputTools`**

Create `src/FlaUI.Mcp.Server/Tools/InputTools.cs`:

```csharp
using System.ComponentModel;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Windows;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Tools;

[McpServerToolType]
public sealed class InputTools
{
    private const int DefaultTimeoutMs = 4000;
    private const int MaxTypeUnits = 4096;
    private readonly PerceptionManager _perception;
    private readonly WindowManager _windows;
    private readonly ServerOptions _options;
    private readonly InputGuard _guard;
    private readonly IPlatformEnvironment _env;

    public InputTools(PerceptionManager perception, WindowManager windows, ServerOptions options,
        InputGuard guard, IPlatformEnvironment env)
    { _perception = perception; _windows = windows; _options = options; _guard = guard; _env = env; }

    [McpServerTool(Destructive = true), Description("Type text into the focused element via real synthetic keyboard input (SendInput). ref = the element to focus first. Up to 4096 UTF-16 units per call (InvalidArguments over cap). Focuses the element, then re-verifies the OS foreground is still that window immediately before sending; ABORTs (ElementDisappearedDuringAction) if focus was stolen. Requires an active input lease (`flaui-mcp unlock`); InputNotLeased / InputDesktopUnavailable / InputBudgetExceeded / TargetDenied / SinkInterlocked otherwise. Blocked in --read-only-mode.")]
    public Task<string> DesktopType(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Element ref to focus and type into, e.g. e23.")] string @ref,
        [Description("Text to type (<=4096 UTF-16 units).")] string text,
        [Description("Block timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            if ((text?.Length ?? 0) > MaxTypeUnits)
                throw new ToolException(ToolErrorCode.InvalidArguments,
                    $"Text exceeds the {MaxTypeUnits} UTF-16 unit per-call cap.", "split the text across multiple desktop_type calls, slicing on a whole-character boundary (never between the two halves of a surrogate pair / an emoji)");

            var target = await _perception.RunOnRefForInputAsync(new WindowHandle(window), @ref,
                (win, el) => { el.Focus(); return InputTargeting.ResolveElementTarget(win, el); }, timeoutMs);

            // Pipeline + atomic re-verify + SendInput share one (non-STA) thread per spec §2.
            await Task.Run(() => _guard.KeyType(text ?? string.Empty, target));
            return ToolResponse.Ok(new { ok = true, pathUsed = "synthetic" });
        });

    [McpServerTool(Destructive = true), Description("Send one keyboard chord via real synthetic input. chord grammar: `+`-delimited, zero-or-more modifiers Ctrl|Alt|Shift|Win + one key (letter/digit; Enter Tab Esc Backspace Delete Home End PageUp PageDown Up Down Left Right Space; F1-F24). e.g. \"Ctrl+S\", \"Enter\". Omit ref/window to target the current FOREGROUND window; pass BOTH ref AND window to focus a specific element first. Unknown token -> InvalidArguments. Same lease/deny-list/session gates as desktop_type. Blocked in --read-only-mode.")]
    public Task<string> DesktopKey(
        [Description("Chord, e.g. \"Ctrl+S\" or \"Enter\".")] string chord,
        [Description("Optional element ref to focus first; omit to target the current foreground window.")] string? @ref = null,
        [Description("Window handle (REQUIRED only when ref is given), e.g. w1.")] string? window = null,
        [Description("Block timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            // agy R2: `window` is now optional so the foreground path is callable without a dummy handle.
            // A ref requires a window to resolve against; reject the half-specified combination explicitly.
            KeyChordParser.Parse(chord); // validate grammar up front -> InvalidArguments (discard result)
            var (modNames, keyToken) = SplitChord(chord);
            bool haveRef = !string.IsNullOrEmpty(@ref);
            if (haveRef && string.IsNullOrEmpty(window))
                throw new ToolException(ToolErrorCode.InvalidArguments,
                    "A ref needs its window handle.", "pass `window` alongside `ref`, or omit both to target the foreground window");

            ActionTarget target = haveRef
                ? await _perception.RunOnRefForInputAsync(new WindowHandle(window!), @ref!,
                    (win, el) => { el.Focus(); return InputTargeting.ResolveElementTarget(win, el); }, timeoutMs)
                : ResolveForegroundTarget();

            await Task.Run(() => _guard.KeyChord(modNames, keyToken, target));
            return ToolResponse.Ok(new { ok = true, pathUsed = "synthetic" });
        });

    // The foreground target for a no-ref key: resolve via the platform env hit of the foreground root.
    private ActionTarget ResolveForegroundTarget()
    {
        nint root = _env.GetForegroundRoot();
        if (root == 0)
            throw new ToolException(ToolErrorCode.ElementNotActionable,
                "No foreground window to target.", "focus a window, or pass an explicit ref");
        // Identity (process/class) for the deny-list comes from a hit-test of the root's own origin is
        // unreliable; instead reuse HitTestRoot at the foreground window — but we only have the root.
        // The deny-list still runs in the guard against whatever identity we can resolve; resolve it via
        // the env's hit-test of the foreground window's top-left is fragile, so resolve process/class
        // from the root handle directly.
        var pt = _env.HitTestRoot(0, 0); // not used for identity; placeholder to keep the seam — see note
        return new ActionTarget(root, 0, pt.ProcessName, pt.WindowClass);
    }

    private static (string[] mods, string key) SplitChord(string chord)
    {
        var tokens = chord.Split('+', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
        var mods = tokens.Length > 1 ? tokens[..^1] : System.Array.Empty<string>();
        return (mods, tokens[^1]);
    }
}
```

> ⚠️ DESIGN GAP TO RESOLVE IN THIS TASK (do not paper over): the no-ref `desktop_key` foreground path needs the foreground window's PROCESS + CLASS for the deny-list, but `IPlatformEnvironment` currently exposes only `GetForegroundRoot()` (a bare HWND) and `HitTestRoot(x,y)` (a point). Resolving identity from a bare root requires a Win32 `GetWindowThreadProcessId`+`GetClassName` on the root. **Correct fix:** add `PointTarget ResolveRoot(nint root)` to `IPlatformEnvironment` (real impl: the same process/class resolution `HitTestRoot` already does, factored out; fake: returns a scripted value), and have `ResolveForegroundTarget` call it. This is the clean seam-respecting resolution — the placeholder `HitTestRoot(0,0)` above is a STOP marker, not shippable. Implement `ResolveRoot`, add a headless `FakePlatformEnvironment` field for it, and a guard test that a foreground key into a denied/interlocked root is refused. (This keeps the no-ref path a first-class deny-list citizen per spec §3.2.)

- [ ] **Step 3b: Add `ResolveRoot` to the seam (resolves the §3.2 no-ref design gap)**

Modify `src/FlaUI.Mcp.Core/Interaction/IPlatformEnvironment.cs` — add to the interface:

```csharp
    /// <summary>Owning process base-name + window class for an already-resolved top-level root (for the
    /// deny-list on the no-ref foreground path). Root echoes back; process/class null if unresolvable.</summary>
    PointTarget ResolveRoot(nint root);
```

Implement in `Win32PlatformEnvironment` (factor the identity read out of `HitTestRoot`):

```csharp
    public PointTarget ResolveRoot(nint root)
    {
        if (root == 0) return new PointTarget(0, null, null);
        Win32Interop.GetWindowThreadProcessId(root, out uint pid);
        string? proc = null;
        try { using var p = Process.GetProcessById((int)pid); proc = p.ProcessName; } catch { }
        var sb = new StringBuilder(256);
        string? cls = Win32Interop.GetClassName(root, sb, sb.Capacity) > 0 ? sb.ToString() : null;
        return new PointTarget(root, proc, cls);
    }
```

Refactor `HitTestRoot` to call `ResolveRoot(root)` after the `WindowFromPhysicalPoint`→`GetAncestor` resolution (replace its identity block). Add the field + method to `FakePlatformEnvironment`:

```csharp
    public PointTarget RootResult { get; set; }
    public PointTarget ResolveRoot(nint root) => RootResult;
```

Then in `InputTools.ResolveForegroundTarget`, replace the placeholder block with:

```csharp
        var id = _env.ResolveRoot(root);
        return new ActionTarget(root, 0, id.ProcessName, id.WindowClass);
```

And delete the now-unused `mods`/`parsed` placeholder line in `DesktopKey` (keep the up-front `KeyChordParser.Parse(chord)` validation call, discarding its result, OR use `SplitChord` + let the leaf validate; pick one — validate up front so a bad chord fails before touching the window).

- [ ] **Step 3c: Add the no-ref deny-list guard test (headless)**

Append to `InputGuardTests.cs`:

```csharp
    [Fact]
    public void Key_into_a_denied_foreground_root_refuses()
    {
        var (g, sink, _) = BuildWithAudit(ValidLease());
        // a foreground key resolves identity via the (fake) ResolveRoot -> a denied process
        var ex = Assert.Throws<ToolException>(() => g.KeyChord(System.Array.Empty<string>(), "Enter",
            new ActionTarget(nint.Zero, 0, "consent", "Credential")));
        Assert.Equal(ToolErrorCode.TargetDenied, ex.Code);
        Assert.Empty(sink.Calls);
    }
```

- [ ] **Step 3e: ELEMENT-identity for the SendInput ref-path (agy R4 #3 parallel fix — user-approved 2026-06-30)**

The user folded the same element-vs-host identity fix from Task 13 into the SendInput **ref** path: `desktop_type` and `desktop_key`-with-ref now classify the focused element via `InputTargeting.ResolveElementTarget(win, el)` (Task 9/10 definition; see Task 13 Step 3d) instead of `ResolveRefTarget(win)`. After `el.Focus()`, `el` IS the control that will receive keystrokes, so its identity is the right deny-list subject — closing the embedded-cross-process interlock evasion (an interlocked terminal pane inside an Allowed host like `Code.exe`). The lambdas in Step 3 already call `ResolveElementTarget(win, el)`. Add a headless guard test:

```csharp
    [Fact]
    public void Type_into_an_embedded_interlocked_element_needs_the_shells_cap()
    {
        // ref-path resolves the ELEMENT's identity (a windowsterminal pane), even if its host window is Allowed
        var (g, sink, _) = BuildWithAudit(ValidLease()); // valid lease, NO shells cap
        var ex = Assert.Throws<ToolException>(() => g.KeyType("dir",
            new ActionTarget((nint)42, 300, "windowsterminal", "CASCADIA_HOSTING_WINDOW_CLASS")));
        Assert.Equal(ToolErrorCode.SinkInterlocked, ex.Code);
        Assert.Empty(sink.Calls);
    }
```

**Residual — `desktop_key` NO-ref foreground path (narrower, must be handled, not silently left host-classified):** the no-ref path has no `el`; `ResolveForegroundTarget` resolves the foreground ROOT identity (`ResolveRoot(root)`), so embedded content in the foreground would still be host-classified. Close it best-effort by resolving the foreground FOCUSED element's identity: in `ResolveForegroundTarget`, after `GetForegroundRoot()`, obtain the UIA focused element (FlaUI `automation.FocusedElement()`, dispatched on the automation thread) and prefer `ResolveElementTarget(rootEl, focusedEl)`; fall back to `_env.ResolveRoot(root)` if the focused element is null/unavailable. STATE-VERIFY the FlaUI 5 `FocusedElement()` accessor + that the automation/dispatcher is reachable from `InputTools` (it holds `WindowManager`); if wiring the focused-element read is disproportionate, the fallback (`ResolveRoot`) keeps the EXISTING root-level deny-list behavior (no regression) — but document the no-ref foreground embedded-content case as a known residual in the README and report the choice as a `SHAPE_DIVERGENCE` for the user, do NOT silently drop it.

- [ ] **Step 4: Build + gates**

Run: `dotnet build FlaUI.Mcp.slnx` → 0 warnings.
Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop"` → **156** PASS (155 + the new no-ref guard test). The `InputToolsTests` are Desktop+SyntheticInput → excluded here; they compile but do not run.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Tools/InputTools.cs src/FlaUI.Mcp.Core/Interaction/IPlatformEnvironment.cs src/FlaUI.Mcp.Core/Interaction/Win32PlatformEnvironment.cs test/FlaUI.Mcp.Tests/Interaction/FakePlatformEnvironment.cs test/FlaUI.Mcp.Tests/Interaction/InputGuardTests.cs test/FlaUI.Mcp.Tests/Interaction/InputToolsTests.cs
git commit -m "feat(input): desktop_type + desktop_key tools; IPlatformEnvironment.ResolveRoot for no-ref deny-list"
```

---

### Task 11: `desktop_click` + `desktop_click_at` + `desktop_drag` tools

**Why:** The mouse tools. `desktop_click` is ref-anchored (click the element's clickable point); `desktop_click_at`/`desktop_drag` are coordinate (§5 pct) with the coordinate hit-test deny-list (§3.2) and the F5 fail-closed guarantee (unidentifiable point → `TargetDenied`).

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Tools/InputTools.cs` (add three tools + helpers)
- Test: add cases to `InputToolsTests.cs` (Desktop console-only) + a headless `CoordinateMath`/F5 routing assertion

- [ ] **Step 1: Write the tests**

Append to `InputToolsTests.cs` (Desktop, console-only — same lease/skip guard pattern):

```csharp
    [Fact]
    public async Task Click_at_a_denied_empty_point_is_refused_fail_closed()
    {
        Assert.SkipWhen(InputLocked(), "no active input lease — grant one on a console with `flaui-mcp unlock`");
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var handle = await mgr.OpenByPidAsync(app.Process.Id);
        var tools = BuildTools(mgr, perception);

        // A point far off any window (1,1 of a zero-size virtual corner is environment-specific; instead
        // assert the GUARD path: a coordinate whose hit-test yields no process is TargetDenied). Here we
        // drive click_at at the window's own area to confirm the happy path returns no error.
        var json = await tools.DesktopClickAt(handle.Id, 0.5, 0.5, "left", 1, 4000);
        Assert.DoesNotContain("\"error\"", json);
    }
```

Append a headless F5 routing assertion to `InputGuardTests.cs` (proves the coordinate-shaped target with no identity is refused through the live guard):

```csharp
    [Fact]
    public void Click_at_an_unidentifiable_point_is_denied()
    {
        var (g, sink, _) = BuildWithAudit(ValidLease());
        var ex = Assert.Throws<ToolException>(() => g.MouseClick(5, 5, "left", 1,
            System.Array.Empty<string>(), new ActionTarget((nint)1, 0, null, null))); // no proc, no class
        Assert.Equal(ToolErrorCode.TargetDenied, ex.Code);
        Assert.Empty(sink.Calls);
    }
```

- [ ] **Step 2: Run to verify the headless one fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&FullyQualifiedName~InputGuardTests"`
Expected: FAIL to compile (`DesktopClickAt` not yet defined is in the Desktop test; the headless `Click_at_an_unidentifiable_point_is_denied` compiles and should PASS already given Task 1's F5 — run it to confirm F5 routes through the guard). If it already passes, it is a pinning test; proceed.

- [ ] **Step 3: Implement the three mouse tools**

Add to `src/FlaUI.Mcp.Server/Tools/InputTools.cs`:

```csharp
    [McpServerTool(Destructive = true), Description("Synthetic mouse click at an element's clickable point (ref path). button=left|right|middle, count=1|2, modifiers optional. Re-hit-tests that the point still maps to the target window immediately before sending. Same lease/deny-list/session gates. Blocked in --read-only-mode.")]
    public Task<string> DesktopClick(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Element ref to click, e.g. e23.")] string @ref,
        [Description("left|right|middle (default left).")] string button = "left",
        [Description("1 or 2 (default 1).")] int count = 1,
        [Description("Block timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            // BLOCKER (agy): the click point may belong to a SEPARATE top-level window — a context menu,
            // tooltip, or WPF Popup is its own HWND, NOT win's root. So derive the ActionTarget from a
            // hit-test of the element's clickable point (the surface actually under the pixel), not from
            // ResolveRefTarget(win) — otherwise every menu/dropdown click spuriously aborts the leaf's
            // HitTestRoot(point)==root re-verify, and the deny-list would classify the wrong window. The
            // leaf re-hit-tests the same point just before send, so the TOCTOU check still holds (two
            // hit-tests at different instants catch an overlay that slides in after resolution).
            var (target, px, py) = await _perception.RunOnRefForInputAsync(new WindowHandle(window), @ref,
                (win, el) =>
                {
                    var p = el.GetClickablePoint();
                    var pt = _env.HitTestRoot(p.X, p.Y); // Win32, thread-agnostic — safe on the action STA
                    var t = new ActionTarget(pt.Root, 0, pt.ProcessName, pt.WindowClass);
                    return (t, p.X, p.Y);
                }, timeoutMs);
            await Task.Run(() => _guard.MouseClick(px, py, button, count, System.Array.Empty<string>(), target));
            return ToolResponse.Ok(new { ok = true, pathUsed = "synthetic" });
        });

    [McpServerTool(Destructive = true), Description("Synthetic mouse click at a window-relative point. xPct/yPct in [0,1] relative to the target window's bounding rect (the same fractional space desktop_screenshot/desktop_get_bounds publish). The point is hit-tested + deny-listed in the immediate pre-send instant; an unidentifiable point is refused (TargetDenied). button=left|right|middle, count=1|2. Blocked in --read-only-mode.")]
    public Task<string> DesktopClickAt(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("X fraction 0..1 of the window width.")] double xPct,
        [Description("Y fraction 0..1 of the window height.")] double yPct,
        [Description("left|right|middle (default left).")] string button = "left",
        [Description("1 or 2 (default 1).")] int count = 1,
        [Description("Block timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            var (px, py) = await ResolveWindowPctAsync(window, xPct, yPct, timeoutMs);
            var pt = _env.HitTestRoot(px, py);
            var target = new ActionTarget(pt.Root, 0, pt.ProcessName, pt.WindowClass);
            await Task.Run(() => _guard.MouseClick(px, py, button, count, System.Array.Empty<string>(), target));
            return ToolResponse.Ok(new { ok = true, pathUsed = "coordinate" });
        });

    [McpServerTool(Destructive = true), Description("Synthetic mouse drag between two window-relative points (§5 pct space). BOTH endpoints are hit-tested + deny-listed; the END point is re-hit-tested immediately before the mouse-up. button=left|right|middle. Blocked in --read-only-mode.")]
    public Task<string> DesktopDrag(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Start X fraction 0..1.")] double startXPct,
        [Description("Start Y fraction 0..1.")] double startYPct,
        [Description("End X fraction 0..1.")] double endXPct,
        [Description("End Y fraction 0..1.")] double endYPct,
        [Description("left|right|middle (default left).")] string button = "left",
        [Description("Block timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            var (sx, sy) = await ResolveWindowPctAsync(window, startXPct, startYPct, timeoutMs);
            var (ex, ey) = await ResolveWindowPctAsync(window, endXPct, endYPct, timeoutMs);
            var sPt = _env.HitTestRoot(sx, sy);
            var ePt = _env.HitTestRoot(ex, ey);
            var startTarget = new ActionTarget(sPt.Root, 0, sPt.ProcessName, sPt.WindowClass);
            var endTarget = new ActionTarget(ePt.Root, 0, ePt.ProcessName, ePt.WindowClass);
            await Task.Run(() => _guard.MouseDrag(sx, sy, ex, ey, button, startTarget, endTarget));
            return ToolResponse.Ok(new { ok = true, pathUsed = "coordinate" });
        });

    // Resolve a window-relative fraction to a physical screen pixel via the window's physical bounds.
    private Task<(int px, int py)> ResolveWindowPctAsync(string window, double xPct, double yPct, int timeoutMs)
        => _windows.RunOnWindowActionAsync(new WindowHandle(window), (win, _) =>
        {
            var r = win.BoundingRectangle; // System.Drawing.Rectangle, physical px
            return CoordinateMath.PctToPhysical(r.Left, r.Top, r.Width, r.Height, xPct, yPct);
        }, timeoutMs);
```

> STATE-VERIFY before writing: confirm (a) `AutomationElement.GetClickablePoint()` exists in FlaUI 5 and returns a `System.Drawing.Point` (if it instead throws `NoClickablePointException`, wrap it and fall back to `el.BoundingRectangle.Center()`); (b) `win.BoundingRectangle` returns `System.Drawing.Rectangle` with `Left/Top/Width/Height` (used across the perception code — `GetBoundsTests`/`DpiHelper` confirm physical-px rects). Report `STATE_MISMATCH` if either differs.

- [ ] **Step 4: Build + gates**

Run: `dotnet build FlaUI.Mcp.slnx` → 0 warnings.
Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop"` → **157** PASS (156 + `Click_at_an_unidentifiable_point_is_denied`).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Tools/InputTools.cs test/FlaUI.Mcp.Tests/Interaction/InputGuardTests.cs test/FlaUI.Mcp.Tests/Interaction/InputToolsTests.cs
git commit -m "feat(input): desktop_click + desktop_click_at + desktop_drag (coordinate hit-test deny-list, F5 fail-closed)"
```

---

### Task 12: `desktop_input_status` — lease-status read-only tool

> **SCOPE (user ruling 2026-06-30, superseding the earlier same-day deferral):** `desktop_set_caret` / `desktop_select_text_range` ship in **v0.7.0** as **Task 13** (the user reversed their earlier same-day deferral-to-v0.7.1). v0.7.0 therefore ships **5 synthetic-input tools + this read-only status tool + 2 TextPattern tools = 8 tools.** Their design (deny-list-only route via `InputGuard.AuthorizeTextMutation`, `TextPattern` range ops in `TextRangeInteractor`, the Desktop smoke test, and the FlaUI 5 range-API Step-0 verify-gate) is fully specified in Task 13 below + locked spec §4.

**Why:** Today the agent only discovers the input lock by attempting a destructive action and getting `InputNotLeased`. A read-only status tool lets it pre-flight — check whether a human has granted input (and how long remains) BEFORE a multi-step input plan, instead of learning the lock by failing. (agy R3 ergonomics finding; user-approved.) Reads the lease through the guard so the SID/clock stay in one place; exposes nothing secret.

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Interaction/InputGuard.cs` (add `Status()` + `LeaseStatus` record)
- Modify: `src/FlaUI.Mcp.Server/Tools/InputTools.cs` (add `DesktopInputStatus`)
- Test: append to `test/FlaUI.Mcp.Tests/Interaction/InputGuardTests.cs`

- [ ] **Step 1: Write the failing tests (headless — the status logic is fully testable via the guard)**

Append to `InputGuardTests.cs` (reuse `BuildWithAudit`, `ValidLease`, `Now` = 2030-01-01 00:00:30; `ValidLease` expiry is 00:01:00 → 30s remaining):

```csharp
    [Fact]
    public void Status_reports_active_with_seconds_remaining_and_shells()
    {
        var (g, _, _) = BuildWithAudit(ValidLease(caps: new[] { "shells" }));
        var s = g.Status();
        Assert.True(s.Active);
        Assert.Equal(30, s.SecondsRemaining);
        Assert.True(s.Shells);
    }

    [Fact]
    public void Status_reports_locked_without_a_lease()
    {
        var (g, _, _) = BuildWithAudit(lease: null);
        var s = g.Status();
        Assert.False(s.Active);
        Assert.Equal(0, s.SecondsRemaining);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&FullyQualifiedName~InputGuardTests"`
Expected: FAIL to compile — `InputGuard.Status()` / `LeaseStatus` undefined.

- [ ] **Step 3a: Add `Status()` + `LeaseStatus` to `InputGuard`**

In `src/FlaUI.Mcp.Core/Interaction/InputGuard.cs`, add a public method (after `MouseDrag`) and the record (beside the existing `ActionTarget` at the file's end):

```csharp
    /// <summary>Read-only lease status for the pre-flight tool — no input, no side effects. Active iff a
    /// lease is present AND valid for this user right now; SecondsRemaining is clamped to >= 0.</summary>
    public LeaseStatus Status()
    {
        var now = _clock();
        var lease = _leases.Read(out _);
        if (lease is null || !lease.IsValidNow(now, _currentSid))
            return new LeaseStatus(false, 0, false);
        int secs = (int)Math.Max(0, (lease.ExpiryUtc - now).TotalSeconds);
        return new LeaseStatus(true, secs, lease.HasCapability("shells"));
    }
```

```csharp
/// <summary>Read-only lease status surfaced by desktop_input_status (carries no secret content).</summary>
public readonly record struct LeaseStatus(bool Active, int SecondsRemaining, bool Shells);
```

- [ ] **Step 3b: Add the `desktop_input_status` tool to `InputTools`**

```csharp
    [McpServerTool(ReadOnly = true), Description("Report the synthetic-input lease status WITHOUT firing any input or touching any window. Returns { leaseStatus: \"active\"|\"locked\", secondsRemaining, shells }. Call this BEFORE a multi-step input plan to confirm a human has granted input via `flaui-mcp unlock` (the synthetic-input tools fail InputNotLeased until then), instead of discovering the lock by failing. Always safe / read-only.")]
    public Task<string> DesktopInputStatus()
        => ToolResponse.Guard(() =>
        {
            var s = _guard.Status();
            return Task.FromResult(ToolResponse.Ok(new
            { leaseStatus = s.Active ? "active" : "locked", secondsRemaining = s.SecondsRemaining, shells = s.Shells }));
        });
```

- [ ] **Step 4: Build + gates**

Run: `dotnet build FlaUI.Mcp.slnx` → 0 warnings.
Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop"` → green (gate is `Failed: 0`; +2 status tests). The tool's whole surface is headless-tested via `InputGuard.Status()`; no Desktop test needed.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Interaction/InputGuard.cs src/FlaUI.Mcp.Server/Tools/InputTools.cs test/FlaUI.Mcp.Tests/Interaction/InputGuardTests.cs
git commit -m "feat(input): desktop_input_status read-only lease pre-flight tool"
```

---

### Task 13: `desktop_set_caret` + `desktop_select_text_range` — TextPattern tools (deny-list-only)

**Why:** The two caret/selection tools deferred *from* Phase 3 as typing precursors, re-added to v0.7.0 per the 2026-06-30 user ruling. Spec §4: they mutate caret/selection via UIA `TextPattern` and synthesize **no** OS input, so the **time-lease + session-state + budget gates are EXEMPT** — but the **deny-list / sink-interlock ALWAYS run** (red-team: selecting text in a *denied* credential window can exfiltrate text the app fails to flag `IsPassword`). So a NEW guard path is needed — the existing `InputGuard.Authorize` couples deny-list to the lease/session/budget/SendInput pipeline, which is wrong for these. Both are **ref-path** tools (resolve identity from the window via `InputTargeting.ResolveRefTarget`).

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Interaction/InputGuard.cs` (add `AuthorizeTextMutation` after `MouseDrag`)
- Create: `src/FlaUI.Mcp.Core/Interaction/TextRangeInteractor.cs` (the FlaUI 5 `TextPattern` ops)
- Modify: `src/FlaUI.Mcp.Server/Tools/InputTools.cs` (add `DesktopSetCaret` + `DesktopSelectTextRange`)
- Test (headless): append to `test/FlaUI.Mcp.Tests/Interaction/InputGuardTests.cs` (deny-list/lease-exempt classification)
- Test (Desktop, console-only): append to `test/FlaUI.Mcp.Tests/Interaction/InputToolsTests.cs` (real range op on the TestApp multiline TextBox)

- [ ] **Step 0: STATE-VERIFY the FlaUI 5 `TextPattern` range API (do NOT invent member names)**

Open the FlaUI 5 `AutomationElement`/`TextPattern` surface (the installed FlaUI.UIA3 / FlaUI.Core DLLs — reflect or read the package). Confirm the exact shape used below: (a) `el.Patterns.Text.IsSupported` + `el.Patterns.Text.PatternOrDefault` (or `.Pattern`) returning the text pattern; (b) the pattern exposes `DocumentRange` and `GetSelection()`; (c) an `ITextRange`/`TextRange` exposes `Clone()`, `MoveEndpointByUnit(TextPatternRangeEndpoint, TextUnit, int)→int`, `MoveEndpointByRange(...)`, and `Select()`; (d) the enum names `TextPatternRangeEndpoint.{Start,End}` and `TextUnit.Character`. If any name/shape differs, MATCH the real API and report `STATE_MISMATCH: [expected]->[actual]`. This is the load-bearing verify-gate for the whole task — the `TextPattern` op is the one surface not headless-tested.

> RESIDUAL (document, do not paper over): UIA `TextPattern` addresses text in `TextUnit.Character` units, which for non-BMP text / line breaks may NOT equal raw UTF-16 code-unit offsets. `set_caret`/`select_text_range` offsets are therefore "best-effort UIA character offsets," not guaranteed UTF-16-exact for surrogate-pair text. Accepted residual — state it in the tool descriptions; do not claim pixel-exact offset semantics.

- [ ] **Step 1: Write the failing tests**

**Headless (deny-list / lease-exempt — the guard path is fully testable)** — append to `InputGuardTests.cs` (reuse `BuildWithAudit`, `ValidLease`, `Sid`, `Now`):

```csharp
    [Fact]
    public void Text_mutation_into_a_denied_target_refuses_even_with_no_lease()
    {
        var (g, _, _) = BuildWithAudit(lease: null); // text-mutation is lease-EXEMPT; deny-list still runs
        var ex = Assert.Throws<ToolException>(() => g.AuthorizeTextMutation(
            new ActionTarget(nint.Zero, 0, "consent", "Credential"), "set_caret"));
        Assert.Equal(ToolErrorCode.TargetDenied, ex.Code);
    }

    [Fact]
    public void Text_mutation_into_an_allowed_target_passes_without_a_lease_and_audits()
    {
        var (g, _, audit) = BuildWithAudit(lease: null); // lease-EXEMPT: no throw despite null lease
        g.AuthorizeTextMutation(new ActionTarget((nint)7, 100, "notepad", "Edit"), "select_text_range");
        Assert.Contains("action=select_text_range", audit.ToString());
        Assert.Contains("window=7", audit.ToString());
    }

    [Fact]
    public void Text_mutation_into_an_interlocked_sink_needs_the_shells_cap()
    {
        // interlock OVERRIDE still lives in the lease's 'shells' cap (spec §3.2); no shells -> refused
        var (g, _, _) = BuildWithAudit(ValidLease()); // valid lease but NO shells cap
        var ex = Assert.Throws<ToolException>(() => g.AuthorizeTextMutation(
            new ActionTarget((nint)9, 200, "windowsterminal", "CASCADIA_HOSTING_WINDOW_CLASS"), "set_caret"));
        Assert.Equal(ToolErrorCode.SinkInterlocked, ex.Code);
    }
```

**Desktop (real `TextPattern` op — CONSOLE-MACHINE-ONLY, no lease gate since text-mutation is lease-exempt)** — append to `InputToolsTests.cs`. Use the 3b-2 multiline TextBox `TextDoc` (STATE-VERIFY it exists in the TestApp; it was added in the 3b-2 plan T2 — if its AutomationId differs, match the real one):

```csharp
    [Fact]
    public async Task Select_text_range_selects_the_requested_span()
    {
        // No InputLocked() skip — set_caret/select_text_range are lease-EXEMPT (no SendInput). Desktop trait
        // is for the real UIA text provider, which the headless box still has over RDP for UIA (not SendInput).
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var handle = await mgr.OpenByPidAsync(app.Process.Id);
        var snap = await perception.SnapshotAsync(handle, new SnapshotOptions());
        var docRef = FindRefByAid(snap, "TextDoc");

        var tools = BuildTools(mgr, perception);
        var json = await tools.DesktopSelectTextRange(handle.Id, docRef, start: 0, length: 5, 4000);
        Assert.DoesNotContain("\"error\"", json);

        // verify the selection length via TextPattern GetSelection on the element
        var selLen = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            var el = win.FindFirstDescendant(cf => cf.ByAutomationId("TextDoc"))!;
            var sel = el.Patterns.Text.Pattern.GetSelection(); // STATE-VERIFY shape in Step 0
            return sel[0].GetText(-1).Length;
        });
        Assert.Equal(5, selLen);
    }
```

> Note: this Desktop test exercises a UIA `TextPattern` op (NOT `SendInput`), so unlike the `desktop_type`/`click` smoke tests it CAN run over RDP on the headless box — keep the `Desktop` trait (needs a real provider) but no lease/console requirement. STATE-VERIFY `GetSelection()`/`GetText(-1)` shape in Step 0.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&FullyQualifiedName~InputGuardTests"`
Expected: FAIL to compile — `InputGuard.AuthorizeTextMutation` undefined.

- [ ] **Step 3a: Extract a shared deny-list/interlock helper, then add `AuthorizeTextMutation` (agy R4 #4 — no DRY drift)**

To avoid the two authorization paths drifting (agy R4 finding #4), first REFACTOR the existing `CheckTarget(ActionTarget, InputLease)` in `src/FlaUI.Mcp.Core/Interaction/InputGuard.cs` to take the resolved shells-capability as a `bool` so both `Authorize` and `AuthorizeTextMutation` share ONE classification-interpretation:

```csharp
    // was: CheckTarget(ActionTarget target, InputLease lease) — now lease-agnostic; caller resolves the cap.
    private static void CheckTarget(ActionTarget target, bool hasShellsCap)
    {
        var verdict = ActionPolicy.Classify(target.ProcessName, target.WindowClass);
        if (verdict == ActionVerdict.Denied)
            throw new ToolException(ToolErrorCode.TargetDenied,
                $"Synthetic input into '{target.ProcessName}' is refused (UAC/secure-desktop/credential store).",
                "target a different, non-sensitive window");
        if (verdict == ActionVerdict.Interlocked && !hasShellsCap)
            throw new ToolException(ToolErrorCode.SinkInterlocked,
                $"Synthetic input into the interlocked sink '{target.ProcessName}' requires the 'shells' lease capability.",
                "re-grant with `flaui-mcp unlock --minutes N --allow-shells` (human, out-of-band)");
    }
```

Update the two existing `Authorize` call sites (`InputGuard.cs:47-48`) from `CheckTarget(primary, lease)` / `CheckTarget(s, lease)` to `CheckTarget(primary, lease.HasCapability("shells"))` / `CheckTarget(s, lease.HasCapability("shells"))` (the lease is already validated above in `Authorize`, so its caps are authoritative). Then add `AuthorizeTextMutation` after `MouseDrag` (line 100) — kept SEPARATE from `Authorize` because text-mutation's lease is OPTIONAL (consulted only for the interlock override) — DO NOT route it through `Authorize` (that would wrongly require a lease + consume budget + check session-state for a no-SendInput op):

```csharp
    /// <summary>Authorize a UIA TextPattern caret/selection mutation (desktop_set_caret / _select_text_range).
    /// Spec §4: these synthesize NO OS input, so the lease + session-state + budget gates are EXEMPT — but the
    /// deny-list / sink-interlock ALWAYS run (selecting text in a denied credential window can exfiltrate it).
    /// The interlock OVERRIDE still lives in the lease's 'shells' cap, so an optional valid lease is consulted
    /// purely for that override; no lease is required for an allowed (non-interlocked) target. `target` MUST be
    /// resolved from the ELEMENT being mutated, not its host window (agy R4 #3 — an embedded cross-process
    /// interlocked element inside an allowed host must not be classified as the host). Audits event-only (len=0).
    /// Performs no input — the caller runs the TextPattern op on the automation thread after this returns.
    /// Elevation hard-fail does NOT apply here (it gates SendInput; the deny-list already blocks credential/
    /// secure-desktop targets).</summary>
    public void AuthorizeTextMutation(ActionTarget target, string action)
    {
        var lease = _leases.Read(out _);
        bool hasShellsCap = lease is { } l && l.IsValidNow(_clock(), _currentSid) && l.HasCapability("shells");
        CheckTarget(target, hasShellsCap); // shared deny-list/interlock (TargetDenied / SinkInterlocked)
        _audit.Record(target.Root, target.Pid, target.ProcessName, action, 0);
    }
```

> NOTE on the `SinkInterlocked` UX when NO lease exists (agy R4 ambiguity 1): the shared `CheckTarget` recovery hint ("re-grant with `--allow-shells`") reads slightly oddly for a lease-exempt tool when the caller never had a lease — but it is still the correct remedy (an interlocked sink ALWAYS needs the `shells` cap, lease-exempt or not). Keep the shared message; the deny-list/interlock contract is identical across both paths and a single message avoids divergence.

- [ ] **Step 3b: Create `TextRangeInteractor` (the FlaUI 5 `TextPattern` ops — names per Step 0)**

Create `src/FlaUI.Mcp.Core/Interaction/TextRangeInteractor.cs`. Every `TextPattern` member name below is a Step-0 verify-gate; match the real FlaUI 5 surface:

```csharp
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions; // TextPatternRangeEndpoint, TextUnit (STATE-VERIFY namespace)
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>UIA TextPattern caret/selection mutation for desktop_set_caret / desktop_select_text_range.
/// No OS input — pure UIA range ops; runs on the automation thread (caller's RunOnRefForInput callback).
/// Offsets are UIA TextUnit.Character (may diverge from raw UTF-16 for non-BMP text — accepted residual).</summary>
public static class TextRangeInteractor
{
    public static void SetCaret(AutomationElement el, int offset)
    {
        var range = RequirePattern(el).DocumentRange.Clone();
        range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, offset);
        // collapse END onto START -> a degenerate (caret) range
        range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
        range.Select();
    }

    public static void SelectRange(AutomationElement el, int start, int length)
    {
        var range = RequirePattern(el).DocumentRange.Clone();
        range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, start);
        range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, start + length);
        range.Select();
    }

    private static FlaUI.Core.Patterns.ITextPattern RequirePattern(AutomationElement el)
        => el.Patterns.Text.PatternOrDefault
           ?? throw new ToolException(ToolErrorCode.PatternUnsupported,
               "Element has no text provider (TextPattern).", "target an editable text element");
}
```

> SHAPE NOTE: if FlaUI 5 names the pattern accessor `.Pattern` (throws when unsupported) rather than `.PatternOrDefault` (null when unsupported), use `el.Patterns.Text.IsSupported ? el.Patterns.Text.Pattern : throw …`. Confirm `ToolErrorCode.PatternUnsupported` exists (3b-2 used a "SelectionItem" PatternUnsupported message — STATE-VERIFY the code name; if it's `NotImplemented`/another code, match it).

- [ ] **Step 3c: Add the two tools to `InputTools`**

In `src/FlaUI.Mcp.Server/Tools/InputTools.cs`, add after `DesktopInputStatus`. The deny-list authorize + the `TextPattern` op BOTH run inside the `RunOnRefForInputAsync` callback (the automation thread) so they are atomic on the same thread the UIA op requires — NO `Task.Run` (there is no `SendInput`, so the spec §2 STA/background split does not apply):

```csharp
    [McpServerTool(Destructive = true), Description("Position the text caret in an element via UIA TextPattern (NO OS input — synthesizes no keystrokes). ref = the text element to act on; offset = UIA character offset for the caret. Routes through the deny-list (TargetDenied for credential/secure windows; interlocked shells need the 'shells' lease cap) but needs NO input lease. PatternUnsupported if the element exposes no TextPattern. Offsets are UIA character units (may differ from raw UTF-16 for emoji/non-BMP text). Blocked in --read-only-mode.")]
    public Task<string> DesktopSetCaret(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Text element ref to act on, e.g. e23.")] string @ref,
        [Description("UIA character offset for the caret.")] int offset,
        [Description("Block timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.GuardWrite(_options, () =>
            _perception.RunOnRefForInputAsync(new WindowHandle(window), @ref, (win, el) =>
            {
                if (offset < 0)
                    throw new ToolException(ToolErrorCode.InvalidArguments, "offset must be >= 0.", "pass a non-negative offset");
                var target = InputTargeting.ResolveElementTarget(win, el); // identity from el, not host win (agy R4 #3)
                _guard.AuthorizeTextMutation(target, "set_caret"); // deny-list (lease-exempt) on the automation thread
                TextRangeInteractor.SetCaret(el, offset);
                return ToolResponse.Ok(new { ok = true, pathUsed = "textpattern" });
            }, timeoutMs));

    [McpServerTool(Destructive = true), Description("Select a text range in an element via UIA TextPattern (NO OS input). ref = the text element; start = UIA character start offset; length = character count. Same deny-list gate as desktop_set_caret; NO input lease required. PatternUnsupported if no TextPattern; InvalidArguments for negative start/length. Offsets are UIA character units (may differ from raw UTF-16 for emoji/non-BMP text). Blocked in --read-only-mode.")]
    public Task<string> DesktopSelectTextRange(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Text element ref to act on, e.g. e23.")] string @ref,
        [Description("UIA character start offset.")] int start,
        [Description("Character count to select.")] int length,
        [Description("Block timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs)
        => ToolResponse.GuardWrite(_options, () =>
            _perception.RunOnRefForInputAsync(new WindowHandle(window), @ref, (win, el) =>
            {
                if (start < 0 || length < 0)
                    throw new ToolException(ToolErrorCode.InvalidArguments, "start and length must be >= 0.", "pass non-negative offsets");
                var target = InputTargeting.ResolveElementTarget(win, el); // identity from el, not host win (agy R4 #3)
                _guard.AuthorizeTextMutation(target, "select_text_range");
                TextRangeInteractor.SelectRange(el, start, length);
                return ToolResponse.Ok(new { ok = true, pathUsed = "textpattern" });
            }, timeoutMs));
```

> SHAPE NOTE: `RunOnRefForInputAsync<T>(handle, ref, Func<win,el,T>, timeoutMs)` returns `Task<T>`; here `T` is the response string so `ToolResponse.GuardWrite(_options, () => _perception.RunOnRefForInputAsync(...))` type-checks (GuardWrite takes a `Func<Task<string>>`). A `ToolException` thrown inside the callback propagates through `RunOnRefForInputAsync` and is mapped by `GuardWrite` — same pattern as `desktop_type` (Task 10). If `GuardWrite` requires an `async` lambda specifically, wrap as `async () => await _perception.RunOnRefForInputAsync(...)`.

- [ ] **Step 3d: `InputTargeting.ResolveElementTarget(win, el)` — ELEMENT identity, not host (agy R4 #3) — DEFINED IN TASK 9/10, reused here**

The deny-list MUST classify the element being acted on, not its host window — otherwise an interlocked terminal/WebView pane embedded **cross-process** in an Allowed host (e.g. an integrated terminal inside `Code.exe`) is classified as the host and bypasses the `shells` interlock. **ORDERING:** because Task 10's ref-path (`desktop_type`/`desktop_key`) is now the FIRST consumer (per the 2026-06-30 user ruling folding the parallel fix into the SendInput ref-path — see Task 10 Step 3e), this method MUST be added to `src/FlaUI.Mcp.Core/Interaction/InputTargeting.cs` when `InputTargeting` is first authored (Task 9), or at the latest at the start of Task 10. It is shown here for reference; do NOT add it a second time in Task 13 (Task 13 only *consumes* it). Definition (beside `ResolveRefTarget`):

```csharp
    /// <summary>Resolve the deny-list/interlock identity from the ELEMENT being acted on (not its host window),
    /// so an embedded cross-process interlocked element is classified by ITS owner. Root stays the host window's
    /// handle (the element often has no HWND) for audit; process/class come from the element.</summary>
    public static ActionTarget ResolveElementTarget(AutomationElement win, AutomationElement el)
    {
        int pid = el.Properties.ProcessId.ValueOrDefault;          // STATE-VERIFY: FlaUI 5 prop accessor shape
        string? proc = null;
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); proc = p.ProcessName; } catch { }
        string? cls = el.Properties.ClassName.ValueOrDefault;       // null/empty if the element has no class
        return new ActionTarget(win.Properties.NativeWindowHandle, pid, proc, cls);
    }
```

> STATE-VERIFY: confirm FlaUI 5 exposes `el.Properties.ProcessId.ValueOrDefault` (int) and `el.Properties.ClassName.ValueOrDefault` (string?). If the accessor is `.Value` (throwing) or a different `TryGetValue` shape, match it and fall back to null/0 on absence. Report `STATE_MISMATCH` on divergence. Add a headless guard test that an element-identity of an interlocked process is refused even when the host `win` would classify Allowed — assert via `AuthorizeTextMutation` with an `ActionTarget` carrying the embedded `windowsterminal` identity (the +3 guard tests in the ledger already cover Denied/Allowed/Interlocked at the guard boundary; this `ResolveElementTarget` mapping itself is exercised by the Desktop smoke test).

- [ ] **Step 4: Build + run both gates**

Run: `dotnet build FlaUI.Mcp.slnx` → 0 warnings.
Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop"` → green (gate is `Failed: 0`; +3 `AuthorizeTextMutation` guard tests).
Run (this box / over-RDP, UIA-only): `dotnet test FlaUI.Mcp.slnx --filter "Category=Desktop&FullyQualifiedName~Select_text_range"` → 1 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Interaction/InputGuard.cs src/FlaUI.Mcp.Core/Interaction/TextRangeInteractor.cs src/FlaUI.Mcp.Server/Tools/InputTools.cs test/FlaUI.Mcp.Tests/Interaction/InputGuardTests.cs test/FlaUI.Mcp.Tests/Interaction/InputToolsTests.cs
git commit -m "feat(input): desktop_set_caret + desktop_select_text_range (TextPattern, deny-list-only, lease-exempt)"
```

---

### Task 14: Docs + version bump 0.6.0 → 0.7.0

**Why:** v0.7.0 ships real synthetic input. README is public and must never go stale (new tools, the `unlock` workflow now being load-bearing, the active-session operational requirement). ROADMAP marks Phase 4b shipped. Versions bump in lockstep (csproj + Inno `.iss`).

**Files:**
- Modify: `README.md`, `ROADMAP.md`
- Modify: `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj` (`<Version>`)
- Modify: the Inno Setup `.iss` (`#define AppVersion`)

- [ ] **Step 1: STATE-VERIFY the version anchors**

Run: `git grep -n "0.6.0" -- README.md ROADMAP.md "*.csproj" "*.iss"` and confirm the exact `<Version>0.6.0</Version>` and `#define AppVersion "0.6.0"` lines (the 4a wrap `deffcf1` set both). Note the real `.iss` path.

- [ ] **Step 2: README** — add a "Synthetic input — Phase 4b (new in v0.7.0)" section documenting: the 8 tools and their gates (5 SendInput + `desktop_input_status` + the 2 lease-exempt `TextPattern` tools `desktop_set_caret`/`desktop_select_text_range`); the **out-of-band lease workflow** (`flaui-mcp unlock --minutes N [--allow-shells]` / `flaui-mcp lock`) now being the REQUIRED enabler (no input fires without it); the operational requirement that the RDP/console session stay **active and unlocked** (locked/disconnected → `InputDesktopUnavailable`); the elevation hard-fail (`--unsafe-allow-elevation`); and the honest lease boundary (no wall against a same-user host shell). Update the opening blurb + "On the roadmap" (move Phase 4b from next → shipped). Apply the "update README before every push" rule.

- [ ] **Step 3: ROADMAP** — mark Phase 4b ✅ (v0.7.0): real `Win32SyntheticInput`/`Win32PlatformEnvironment` + the 5 SendInput tools + `desktop_input_status` + the 2 `TextPattern` tools (`desktop_set_caret`/`desktop_select_text_range`, deny-list-only, lease-exempt), F1–F5 folded, spike-validated. Note the next horizon (the dogfood "driving FlaUI.Mcp" skill; AOT/trim exe-size backlog).

- [ ] **Step 4: Version bump** — set `<Version>0.7.0</Version>` (csproj) and `#define AppVersion "0.7.0"` (`.iss`) in lockstep.

- [ ] **Step 5: Final gates + commit**

Run: `dotnet build FlaUI.Mcp.slnx` → 0 warnings; `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop"` → **159** PASS.

```bash
git add README.md ROADMAP.md src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj <path-to>.iss
git commit -m "docs+release: Phase 4b synthetic input tools (v0.7.0)"
```

---

## Self-review (run against the spec with fresh eyes)

**Spec coverage:**
- §3.2 coordinate hit-test deny-list → Tasks 8 (`HitTestRoot`/`ResolveRoot`), 11 (`click_at`/`drag` wiring), 1 (F5 fail-closed). ✅
- §3.2 both drag endpoints + end re-hit-test before MouseUp → Task 7 (`MouseDrag` re-verify) + Task 11. ✅
- §3.2 no-ref foreground path is a first-class deny-list citizen → Task 10 (`ResolveRoot` + `ResolveForegroundTarget`). ✅
- §3.4 budget defaults (60/60s) + stderr event-only audit + F4 drop audit → Task 9 DI + Task 3. ✅
- §3.5 elevation hard-fail → already in `InputGuard` (4a); wired via DI `isElevated`/`allowElevation` in Task 9. ✅
- §4 tool surface (5 SendInput tools, `InvalidArguments` over cap, chord grammar, ABORT-on-foreground-mismatch) → Tasks 10–11 + KeyChordParser (T4) + InputReverify in the leaf (T7). ✅ (`desktop_input_status` pre-flight = Task 12.)
- §4 set_caret/select route deny-list only (no SendInput → lease/session/budget EXEMPT, deny-list/interlock ALWAYS) → Task 13 (`InputGuard.AuthorizeTextMutation` + `TextRangeInteractor`). ✅
- §5 coordinate space + VIRTUALDESK 0–65535 + INPUT layout → Tasks 5, 6, 7. ✅
- §6 headless via three seams + 4b = spike + manual → every decision extracted+tested; leaves build-only/spiked. ✅
- F1–F5 → Tasks 2, 3, 1 (F1, F2/F3/F4, F5). ✅

**Type consistency:** `ActionTarget(nint,int,string?,string?)`, `PointTarget(nint,string?,string?)`, `ParsedChord(ushort[],ushort)`, `InputGuard.{KeyType,KeyChord,MouseClick,MouseDrag,SetCaretAuthorize,SelectTextRangeAuthorize}`, `IPlatformEnvironment.{GetForegroundRoot,HitTestRoot,SessionState,ResolveRoot}`, `RunOnRefForInputAsync<T>(handle,ref,Func<win,el,T>,timeoutMs)` — used consistently across tasks.

**Known verify-gates (flagged inline, NOT fabricated precision):** FlaUI 5 `GetClickablePoint()` shape (T11 Step-0), `BoundingRectangle` type (T11 Step-0), the `TextPattern` range-mutation API + `PatternOrDefault`/`TextPatternRangeEndpoint`/`TextUnit` names (T13 Step-0), the snapshot ref-extraction helper name (T10 Step-0), the TestApp `TextDoc` AutomationId (T13). Each carries a `STATE_MISMATCH`/oracle instruction.

**Test-count ledger (headless `Category!=Desktop`, indicative — gate is `Failed: 0`):** 114 → 121 (T1) → 126 (T2) → 129 (T3) → 143 (T4) → 151 (T5) → 156 (T6) → 156 (T7) → 156 (T8) → 156 (T9) → 157 (T10) → 158 (T11) → 160 (T12) → 163 (T13, +3 `AuthorizeTextMutation` guard tests) → 163 (T14 docs).

**AGY-AFTER round 1 (relentless-adversarial-auditor persona, cascade `01dc3bb0`) — 3 BLOCKERs folded, all controller-verified against the live 4a code before folding:**
- **F5 fail-open (Task 1):** `Classify` denied only on `proc && cls` empty; a target whose process is unresolvable (elevated/protected → `Process.GetProcessById` throws) but whose class resolves (`GetClassName`) fell through to `Allowed`. Hardened: **no resolvable process name ⇒ Denied.** Added the `proc=null, class="DirectUIHWND"` pinning case.
- **MouseDrag stuck-button (Task 7):** an end-point re-verify throwing after button-down left `Send(up)` unreached → OS mouse globally stuck down. Fixed: on abort, **release at the START point** (cancels the drag, never drops into the suspect/denied end window) then rethrow.
- **desktop_click popup abort (Task 11):** target root taken from the main window, but a ref in a context menu/tooltip/WPF popup hit-tests to the popup's own HWND → 100% spurious abort. Fixed: **derive the `ActionTarget` from `HitTestRoot(clickPoint)`** (the surface actually under the pixel); the leaf's second hit-test keeps the TOCTOU check live.
- agy's SAFE verdicts on the no-ref identity-drift (Q1), focus-steal TOCTOU (Q2), TextPattern lease-exempt-but-deny-listed wiring (Q5), and per-window budget (Q6) were independently re-checked and confirmed — no change needed.

**AGY-AFTER round 2 (release/ops + API-contract persona) — 3 folded, 3 rejected as verified-false, 2 minor:**
- **FOLDED — `desktop_key` wire contract (T10):** `window` was required-by-schema but ignored on the foreground path, forcing an LLM to fabricate a handle. Made `window` (and `@ref`) optional; pass BOTH to focus an element, omit both for the foreground window; `ref` without `window` ⇒ `InvalidArguments`.
- **FOLDED — `InputLocked()` silent-pass (T10–12):** `if (InputLocked()) return;` reads as PASS in xUnit → false confidence. Switched to `Assert.SkipWhen(...)` with a Step-0 gate to confirm the skip API (xUnit v3) / fall back to `[SkippableFact]` on v2.
- **FOLDED — mid-lock error code (T7):** a desktop lock mid-send collapses the root to 0 and surfaced `ElementDisappearedDuringAction`; added a leaf `Reverify` helper that re-probes `SessionState()` on a zero root → `InputDesktopUnavailable` (so the agent unlocks the session instead of retrying the element).
- **REJECTED (verified false) — "missing MCP server registration":** `Program.cs:41` `.WithToolsFromAssembly()` auto-discovers every `[McpServerToolType]`; `AddSingleton<InputTools>()` + the scan is the same pattern `ContentTools`/`ClipboardTools` already ship with.
- **REJECTED (verified false) — "RunOnWindowActionAsync signature mismatch":** the existing `RunOnRefActionAsync` (`PerceptionManager.cs:41`) already calls `RunOnWindowActionAsync(handle, (win, desktop) => …)`; the new overload copies that exact shape.
- **REJECTED — "rename `@ref`→`targetRef`":** every shipped 3a/3b tool uses `@ref` over the wire successfully; renaming would break the convention to chase a `$ref` collision the live tools disprove.
- **MINOR (noted, no change):** `ActionBudget`'s dictionary growth is already bounded by the existing `_hits.Clear()` on lease-write advance (`ActionBudget.cs:24`); per-task test counts are indicative (gate is `Failed: 0`).

**AGY-AFTER round 3 (UX/operator-ergonomics persona) — DIMINISHING-RETURNS STOP. No new correctness/security defect; agy: "clear to exit the design loop."** Residue was all enhancement beyond the locked spec — surfaced to the user as scope/spec decisions (AGY-FIRST); **ALL RESOLVED by USER ruling 2026-06-30 and folded below:**
- **SPEC CHALLENGE — `desktop_type` 4096-cap chunking (T10): RESOLVED → KEEP the cap.** agy proposed dropping the cap and chunking internally on surrogate boundaries; the spec §4 cap is a *deliberate* contract that also bounds per-call keystroke volume (one `type` = one budget unit regardless of length, so internal chunking would let a single call emit an unbounded SendInput burst). **USER ruled KEEP the cap + improve the message** — T10's `InvalidArguments` text already tells the agent to split on a surrogate-safe boundary. ✅ Folded.
- **NEW SCOPE — lease-status read-only tool (`desktop_input_status`): RESOLVED → ADD.** **USER ruled ADD it** — shipped as Task 12 (read-only, no secret exposure). ✅ Folded.
- **OPTIONAL POLISH — `InputBudgetExceeded` retry-after (T3): RESOLVED → ADD.** **USER ruled ADD it** — T3 Step 3b adds `ActionBudget.SecondsUntilFreeSlot` and names the wait in the `InputBudgetExceeded` refusal ("Retry in ~Ns"). ✅ Folded.
- **SET_CARET/SELECT scope: RESOLVED → SHIP IN v0.7.0.** An earlier same-day ruling deferred `desktop_set_caret`/`desktop_select_text_range` to v0.7.1; **USER reversed it 2026-06-30** — both ship in v0.7.0 as Task 13 (deny-list-only, lease-exempt). ✅ Folded.
- **ALREADY-FOLDED (R2) / re-raised:** the `Assert.SkipWhen` xUnit-version hazard — the R2 Step-0 gate already says "confirm xUnit v3 / else `[SkippableFact]`"; no further change.
- **CONFIRMED-SAFE by agy:** dropping `dpiScale` from `CoordinateMath` is correct — fractional window coordinates are scale-invariant, so applying the fraction to the physical `BoundingRectangle` maps back to physical pixels with no DPI double-apply.
