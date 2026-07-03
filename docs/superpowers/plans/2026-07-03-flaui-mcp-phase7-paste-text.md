# Phase 7 — `desktop_paste_text` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `desktop_paste_text` — an atomic, clipboard-preserving `Ctrl+V` paste tool for reactive editors that garble `SendInput`, gated by the existing input-safety pipeline and reusing the `desktop_type` verify.

**Architecture:** Pure classification core (`ClipboardClassifier`) + thin Win32 leaf (`ClipboardAccess.Snapshot`), matching the repo's pure-core/thin-leaf idiom (cf. `TypedTextVerifier`/`VerifyReader`). All refusal gates run **before** the first clipboard mutation via a new `InputGuard.PreflightInput`; the prior clipboard is restored **only on confirmed consumption** (containment). Non-text is fail-fast; mixed text+rich degrades-and-reports.

**Tech Stack:** C# / .NET 10, FlaUI.UIA3, official MCP C# SDK, xUnit. Spec: `docs/superpowers/specs/2026-07-03-flaui-mcp-phase7-paste-text-design.md`.

**Build/test gates (repo-standard — use exactly these):**
- Headless gate: `dotnet test -c Release --filter "Category!=Desktop"` → expect `Passed!` with `Failed: 0`.
- Desktop/manual (connected+unlocked session + `flaui-mcp unlock --minutes 5`): `dotnet test --filter "Category=Desktop&<name>"`.

**Oracle:** the spec above (§3 contract, §4 algorithm, §5 clipboard table, §6 verify, §7 remedy re-point). If a value looks wrong, the spec wins — surface the conflict, do not edit tests to match code.

**Consumer decision (Claude as end consumer, 2026-07-03):** on the known wart that clipboard *restore*
is often `"abandoned"` in the very reactive editors this targets (they transform pasted text, so the
containment consumption-gate can't confirm), the call is **(a) ship now with BLUNT docs** — the tool
guarantees the *paste* and the *safety* (no leak, no wrong text), and restore is an explicit best-effort
courtesy that degrades to `"abandoned"` (payload left on clipboard). The "correct" mechanism — a
delayed-render clipboard (`WM_RENDERFORMAT` fires exactly when the target reads the clipboard, confirming
consumption without depending on the field's final text) — is **(b) tracked as a Phase 7.1 follow-up**
(needs a message-pump window; do NOT block v1). Do NOT weaken containment to "field changed" — that
re-opens the Seat-2 race. This decision is FINAL for v1; T9 docs must state the `"abandoned"` reality plainly.

---

## Task 1 (PREREQ): Fix the broken `InputToolsTests` end-to-end harness

The real end-to-end `SendInput` test currently fails BEFORE input at `RefForAid(tree,"Input")` ("Sequence contains no matching element") — the WPF TestApp snapshot has no `aid=Input` node. It is a `[SkippableFact]`, so it silently skipped. Phase 7's Desktop paste test reuses the same TestApp, so fix this first. This is a **diagnosis** task (root cause unknown until observed).

**Files:**
- Inspect: `test/FlaUI.Mcp.Tests/Interaction/InputToolsTests.cs:36-56`
- Inspect: the WPF TestApp source (find it: `test/**/TestApp*/**` — the app `TestAppFixture` launches)
- Likely modify one of: the TestApp XAML (`AutomationProperties.AutomationId`), or `InputToolsTests.RefForAid`'s expected aid.

- [ ] **Step 1: Locate the TestApp and its controls**

Run: `git ls-files | grep -i testapp` and open the app's XAML/window. Grep for the input control:
`grep -rn "AutomationId\|x:Name\|Input" <testapp dir>`
Record the ACTUAL automation id of the text input control the test targets.

- [ ] **Step 2: Reproduce and dump the live tree**

With a connected session + lease granted, add a temporary diagnostic to see what the snapshot actually contains:
Run: `dotnet test --filter "FullyQualifiedName~InputToolsTests.Type_writes_text_into_the_focused_textbox" -v n`
If it still throws at `RefForAid`, the tree lacks `aid=Input`. Confirm the real aid from Step 1 (e.g. it may be `TextInput`, `MainInput`, or the control may expose `Name` not `AutomationId`).

- [ ] **Step 3: Apply the minimal fix**

Two possibilities — apply whichever matches the finding:
- If the control's real aid differs from `"Input"`: update the literal in `InputToolsTests.cs:53` (and any sibling Desktop tests) to the real aid, OR
- If the control has no `AutomationId`: add `AutomationProperties.AutomationId="Input"` to the control in the TestApp XAML (preferred — keeps tests stable and gives the new paste test a target).

Prefer adding `AutomationId="Input"` to the TestApp so both `desktop_type` and `desktop_paste_text` Desktop tests share a stable, named target.

- [ ] **Step 4: Verify the end-to-end test now passes**

Run (connected + `flaui-mcp unlock --minutes 5`): `dotnet test --filter "FullyQualifiedName~InputToolsTests.Type_writes_text_into_the_focused_textbox" -v n`
Expected: `Passed! - Failed: 0` (NOT skipped — the lease is granted).

- [ ] **Step 5: Commit**

```bash
git add test/ <testapp path>
git commit -m "fix(test): make InputToolsTests end-to-end target resolvable (aid=Input)"
```

---

## Task 2: `PriorClipboardKind` + pure `ClipboardClassifier`

Extract the classification decision as a pure function so the Text/Rich/NonText/Empty logic is headless-testable without staging a real image clipboard.

**Files:**
- Create: `src/FlaUI.Mcp.Core/Interaction/ClipboardClassifier.cs`
- Test: `test/FlaUI.Mcp.Tests/Interaction/ClipboardClassifierTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

// Headless: pure classification logic over a set of present clipboard format ids.
public class ClipboardClassifierTests
{
    const uint CF_TEXT = 1, CF_OEMTEXT = 7, CF_UNICODETEXT = 13, CF_LOCALE = 16, CF_DIB = 8;
    const uint CF_HTML = 49999; // registered-format id stand-in (any non-synonym)

    [Fact] public void No_formats_is_Empty() =>
        Assert.Equal(PriorClipboardKind.Empty, ClipboardClassifier.Classify(new uint[0]));

    [Fact] public void Unicode_plus_synthesized_text_synonyms_is_Text() =>
        Assert.Equal(PriorClipboardKind.Text,
            ClipboardClassifier.Classify(new[] { CF_UNICODETEXT, CF_TEXT, CF_OEMTEXT, CF_LOCALE }));

    [Fact] public void Unicode_plus_html_is_TextWithRichFormats() =>
        Assert.Equal(PriorClipboardKind.TextWithRichFormats,
            ClipboardClassifier.Classify(new[] { CF_UNICODETEXT, CF_HTML }));

    [Fact] public void Image_only_no_text_is_NonText() =>
        Assert.Equal(PriorClipboardKind.NonText, ClipboardClassifier.Classify(new[] { CF_DIB }));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ClipboardClassifierTests" -v n`
Expected: FAIL — `ClipboardClassifier` / `PriorClipboardKind` do not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Collections.Generic;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>What the clipboard held before desktop_paste_text borrowed it. Text-only layer:
/// Text is fully restorable; TextWithRichFormats restores only its plain-text projection (formatting
/// lost); NonText (image/files, no text) is not restorable at all; Empty had nothing.</summary>
public enum PriorClipboardKind { Text, TextWithRichFormats, NonText, Empty }

/// <summary>Pure classification of a clipboard from the set of currently-present format ids. Separated
/// from the Win32 enumeration in <see cref="ClipboardAccess"/> so the decision is headless-testable.</summary>
public static class ClipboardClassifier
{
    private const uint CF_TEXT = 1, CF_OEMTEXT = 7, CF_UNICODETEXT = 13, CF_LOCALE = 16;

    // OS auto-synthesizes these from CF_UNICODETEXT; a clipboard holding ONLY these is pure text.
    private static readonly HashSet<uint> TextSynonyms = new() { CF_UNICODETEXT, CF_TEXT, CF_OEMTEXT, CF_LOCALE };

    public static PriorClipboardKind Classify(IReadOnlyCollection<uint> presentFormats)
    {
        if (presentFormats.Count == 0) return PriorClipboardKind.Empty;
        bool hasUnicode = false, hasNonSynonym = false;
        foreach (var f in presentFormats)
        {
            if (f == CF_UNICODETEXT) hasUnicode = true;
            if (!TextSynonyms.Contains(f)) hasNonSynonym = true;
        }
        if (hasUnicode) return hasNonSynonym ? PriorClipboardKind.TextWithRichFormats : PriorClipboardKind.Text;
        return PriorClipboardKind.NonText; // formats present, none is CF_UNICODETEXT
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~ClipboardClassifierTests" -v n`
Expected: `Passed! - Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Interaction/ClipboardClassifier.cs test/FlaUI.Mcp.Tests/Interaction/ClipboardClassifierTests.cs
git commit -m "feat(clipboard): pure ClipboardClassifier + PriorClipboardKind (Text/Rich/NonText/Empty)"
```

---

## Task 3: `ClipboardAccess.Snapshot()` + `ClipboardSnapshot` (Win32 enumeration leaf)

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Interaction/ClipboardAccess.cs` (add P/Invokes + `ClipboardSnapshot` record + `Snapshot()`)
- Test: `test/FlaUI.Mcp.Tests/Interaction/ClipboardAccessTests.cs` (Desktop-category, real clipboard)

- [ ] **Step 1: Write the failing Desktop test**

Append to `ClipboardAccessTests.cs` (class is already `[Trait("Category","Desktop")]`):

```csharp
    [Fact]
    public async Task Snapshot_of_text_clipboard_is_Text_with_the_string()
    {
        var probe = "snap-" + System.Guid.NewGuid().ToString("N");
        await ClipboardAccess.SetTextAsync(probe);
        var snap = await ClipboardAccess.Snapshot();
        Assert.Equal(PriorClipboardKind.Text, snap.Kind);
        Assert.Equal(probe, snap.Text);
    }

    [Fact]
    public async Task Snapshot_of_empty_clipboard_is_Empty()
    {
        await ClipboardAccess.SetTextAsync(""); // EmptyClipboard
        var snap = await ClipboardAccess.Snapshot();
        Assert.Equal(PriorClipboardKind.Empty, snap.Kind);
        Assert.Null(snap.Text);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run (connected session): `dotnet test --filter "FullyQualifiedName~ClipboardAccessTests.Snapshot" -v n`
Expected: FAIL — `Snapshot` / `ClipboardSnapshot` do not exist.

- [ ] **Step 3: Write minimal implementation**

Add these P/Invokes to `ClipboardAccess` (next to the existing DllImports, ~line 26):

```csharp
    [DllImport("user32.dll", SetLastError = true)] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool IsClipboardFormatAvailable(uint format);
```

Add the record (top-level in the same file, after the class or in its own file — keep with `ClipboardAccess`):

```csharp
/// <summary>A best-effort text-only snapshot of the clipboard taken before a paste borrows it.
/// <see cref="Text"/> is non-null only for <see cref="PriorClipboardKind.Text"/> and
/// <see cref="PriorClipboardKind.TextWithRichFormats"/> (the plain-text projection).</summary>
public readonly record struct ClipboardSnapshot(PriorClipboardKind Kind, string? Text);
```

Add the method to `ClipboardAccess` (mirrors the existing `GetTextAsync` open/close discipline):

```csharp
    public static Task<ClipboardSnapshot> Snapshot() => Task.Run(() =>
    {
        if (!TryOpen())
            throw new ToolException(ToolErrorCode.ClipboardUnavailable, "Could not open the clipboard (locked by another process).", "retry in a moment");
        try
        {
            var formats = new System.Collections.Generic.List<uint>();
            uint f = 0;
            while ((f = EnumClipboardFormats(f)) != 0) formats.Add(f);
            var kind = ClipboardClassifier.Classify(formats);
            string? text = null;
            if (kind is PriorClipboardKind.Text or PriorClipboardKind.TextWithRichFormats)
            {
                IntPtr h = GetClipboardData(CF_UNICODETEXT);
                if (h != IntPtr.Zero)
                {
                    IntPtr p = GlobalLock(h);
                    if (p != IntPtr.Zero) { try { text = Marshal.PtrToStringUni(p) ?? string.Empty; } finally { GlobalUnlock(h); } }
                }
                text ??= string.Empty;
            }
            return new ClipboardSnapshot(kind, text);
        }
        finally { CloseClipboard(); }
    });
```

- [ ] **Step 4: Run test to verify it passes**

Run (connected session): `dotnet test --filter "FullyQualifiedName~ClipboardAccessTests.Snapshot" -v n`
Expected: `Passed! - Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Interaction/ClipboardAccess.cs test/FlaUI.Mcp.Tests/Interaction/ClipboardAccessTests.cs
git commit -m "feat(clipboard): ClipboardAccess.Snapshot() enumerates formats -> ClipboardSnapshot"
```

---

## Task 4: `ToolErrorCode.ClipboardHoldsNonText`

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Errors/ToolErrorCode.cs:33` (add enum member)

- [ ] **Step 1: Add the enum member**

Change the tail of the enum (currently `SinkInterlocked`) to append the new code:

```csharp
    InputBudgetExceeded,
    SinkInterlocked,
    ClipboardHoldsNonText
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build -c Release`
Expected: `Build succeeded` (0 errors).

- [ ] **Step 3: Commit**

```bash
git add src/FlaUI.Mcp.Core/Errors/ToolErrorCode.cs
git commit -m "feat(errors): add ClipboardHoldsNonText tool error code"
```

---

## Task 5: `ActionBudget.HasFreeSlot` (non-consuming peek)

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Interaction/ActionBudget.cs` (add method after `TryConsume`)
- Test: `test/FlaUI.Mcp.Tests/Interaction/ActionBudgetTests.cs` (create if absent; else append)

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class ActionBudgetTests
{
    [Fact]
    public void HasFreeSlot_is_true_with_spare_budget_and_does_not_consume()
    {
        var b = new ActionBudget(maxPerWindow: 1, windowSeconds: 60);
        var now = DateTime.UtcNow;
        nint w = 1;
        Assert.True(b.HasFreeSlot(w, now));       // peek does not consume
        Assert.True(b.HasFreeSlot(w, now));        // still free (proves no consume)
        Assert.True(b.TryConsume(w, now, DateTime.UtcNow)); // now consume the single slot
        Assert.False(b.HasFreeSlot(w, now));       // budget exhausted
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ActionBudgetTests" -v n`
Expected: FAIL — `HasFreeSlot` not defined.

- [ ] **Step 3: Write minimal implementation**

Add after `TryConsume` (before `SecondsUntilFreeSlot`) in `ActionBudget`:

```csharp
    /// <summary>Non-consuming peek: does this window currently have a free budget slot? Used by
    /// desktop_paste_text's pre-flight to fail-closed BEFORE mutating the clipboard, without spending
    /// the slot (the later KeyChord's TryConsume spends it). Prunes the window's expired hits like
    /// TryConsume, but never enqueues.</summary>
    public bool HasFreeSlot(nint window, DateTime now)
    {
        lock (_gate)
        {
            if (!_hits.TryGetValue(window, out var q)) return true;
            var cutoff = now.AddSeconds(-_windowSeconds);
            while (q.Count > 0 && q.Peek() <= cutoff) q.Dequeue();
            return q.Count < _max;
        }
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~ActionBudgetTests" -v n`
Expected: `Passed! - Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Interaction/ActionBudget.cs test/FlaUI.Mcp.Tests/Interaction/ActionBudgetTests.cs
git commit -m "feat(input): ActionBudget.HasFreeSlot non-consuming peek"
```

---

## Task 6: `InputGuard.PreflightInput` (all refusal gates, no consume/audit)

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Interaction/InputGuard.cs` (add public method; reuses private `CheckTarget`)
- Test: `test/FlaUI.Mcp.Tests/Interaction/InputGuardTests.cs` (append — follow the file's existing fakes)

- [ ] **Step 1: Write the failing tests**

Append to `InputGuardTests.cs`, matching the existing fixture's fake `ISyntheticInput`/`IPlatformEnvironment`/`ILeaseProvider` construction (mirror how existing tests build an `InputGuard`; reuse the same helpers). Assert:

```csharp
    [Fact]
    public void PreflightInput_throws_InputNotLeased_when_no_lease()
    {
        var guard = BuildGuard(lease: null);                 // reuse existing test helper
        var target = new ActionTarget(root: 1, pid: 0, processName: "notepad", windowClass: "Notepad");
        var ex = Assert.Throws<ToolException>(() => guard.PreflightInput(target));
        Assert.Equal(ToolErrorCode.InputNotLeased, ex.Code); // property name per existing InputGuardTests
    }

    [Fact]
    public void PreflightInput_throws_TargetDenied_for_a_denied_window()
    {
        var guard = BuildGuard(lease: ValidLease());         // reuse existing helpers
        var denied = new ActionTarget(root: 1, pid: 0, processName: "consent", windowClass: "Credential Dialog Xaml Host");
        var ex = Assert.Throws<ToolException>(() => guard.PreflightInput(denied));
        Assert.Equal(ToolErrorCode.TargetDenied, ex.Code);
    }

    [Fact]
    public void PreflightInput_does_not_consume_budget()
    {
        var budget = new ActionBudget(maxPerWindow: 1);
        var guard = BuildGuard(lease: ValidLease(), budget: budget);
        var target = new ActionTarget(root: 1, pid: 0, processName: "notepad", windowClass: "Notepad");
        guard.PreflightInput(target);                        // passes all gates
        Assert.True(budget.HasFreeSlot(1, DateTime.UtcNow)); // slot NOT spent
    }
```

> STATE-VERIFICATION: open `InputGuardTests.cs` first and match its ACTUAL fake/helper names (`BuildGuard`, `ValidLease`, the `ToolException` code property). If they differ, adapt the test to the real helpers; do NOT invent new ones. If the file has no such helpers, construct `InputGuard` inline exactly as `InputToolsTests.BuildTools` does (real `Win32*` leaves are Desktop-only — use the existing fakes the headless `InputGuardTests` already defines).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~InputGuardTests.PreflightInput" -v n`
Expected: FAIL — `PreflightInput` not defined.

- [ ] **Step 3: Write minimal implementation**

Add to `InputGuard` (public; sits beside `Authorize`). Messages MUST match `Authorize`'s existing wording (lines 36-58) so the two paths surface identical errors:

```csharp
    /// <summary>Run every REFUSAL gate a real send runs — elevation, lease, deny-list/interlock,
    /// session-state, and a NON-consuming budget peek — WITHOUT consuming a slot or writing audit.
    /// desktop_paste_text calls this to fail-closed BEFORE it mutates the clipboard, so a paste that
    /// will be refused never clobbers the user's clipboard. The subsequent KeyChord re-runs the full
    /// Authorize (idempotent re-check + budget consume + audit).</summary>
    public void PreflightInput(ActionTarget target)
    {
        if (_isElevated && !_allowElevation)
            throw new ToolException(ToolErrorCode.AccessDeniedIntegrity,
                "Synthetic input is refused while the server runs elevated.",
                "restart without elevation, or pass --unsafe-allow-elevation if you accept the risk");

        var now = _clock();
        var lease = _leases.Read(out _);
        if (lease is null || !lease.IsValidNow(now, _currentSid))
            throw new ToolException(ToolErrorCode.InputNotLeased,
                "Synthetic input is locked. No unexpired lease for this user.",
                "run `flaui-mcp unlock --minutes N` on the host to enable input");

        CheckTarget(target, lease.HasCapability("shells"));

        if (!_env.SessionState().CanDeliverInput)
            throw new ToolException(ToolErrorCode.InputDesktopUnavailable,
                "The interactive input desktop is unavailable (locked / disconnected / secure desktop).",
                "connect and unlock the session, then retry");

        if (!_budget.HasFreeSlot(target.Root, now))
            throw new ToolException(ToolErrorCode.InputBudgetExceeded,
                $"Synthetic-input rate limit exceeded for this window. Retry in ~{_budget.SecondsUntilFreeSlot(target.Root, now)}s.",
                "wait for the window to clear, or re-grant the lease with `flaui-mcp unlock` to reset the budget");
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~InputGuardTests.PreflightInput" -v n`
Expected: `Passed! - Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Interaction/InputGuard.cs test/FlaUI.Mcp.Tests/Interaction/InputGuardTests.cs
git commit -m "feat(input): InputGuard.PreflightInput — refusal gates without consume/audit"
```

---

## Task 7: Re-point `VerifyResult` remedy to `desktop_paste_text`

Per spec §7. Shared by `desktop_type` AND (later) `desktop_paste_text` verify.

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Tools/VerifyResult.cs:17-24` (RemedyProse) and `:63` (branch)
- Test: `test/FlaUI.Mcp.Tests/Interaction/VerifyResultTests.cs` (append)

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void Mismatch_with_no_writable_valuepattern_recommends_desktop_paste_text()
    {
        var outcome = new VerifyOutcome(VerifyStatus.Mismatch, null, "expected", "actual");
        var r = VerifyResult.From(outcome, canSetValue: false);
        Assert.Equal("desktop_paste_text", r.RecommendedFallbackTool);
    }

    [Fact]
    public void Mismatch_with_writable_valuepattern_still_recommends_set_value()
    {
        var outcome = new VerifyOutcome(VerifyStatus.Mismatch, null, "expected", "actual");
        var r = VerifyResult.From(outcome, canSetValue: true);
        Assert.Equal("desktop_set_value", r.RecommendedFallbackTool);
    }
```

> STATE-VERIFICATION: confirm `VerifyOutcome`'s constructor arg order in `TypedTextVerifier`/its definition before writing the test (the spec shows `new VerifyOutcome(status, reason, expected, actual)` per InputTools.cs usage). Match the real signature.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~VerifyResultTests.Mismatch_with_no_writable" -v n`
Expected: FAIL — recommends `desktop_clipboard_set`, not `desktop_paste_text`.

- [ ] **Step 3: Write minimal implementation**

In `VerifyResult.cs:63`, change the branch:

```csharp
                RecommendedFallbackTool = canSetValue == false ? "desktop_paste_text" : "desktop_set_value",
```

Replace `RemedyProse` (lines 17-24) so the prose names the atomic tool instead of the manual two-step:

```csharp
    private const string RemedyProse =
        "Text was not entered correctly — the target likely races synthetic keystrokes. " +
        "If canSetValue is true, use desktop_set_value (UIA ValuePattern) for byte-exact entry. " +
        "If canSetValue is false (e.g. an Electron contenteditable with no ValuePattern), use " +
        "desktop_paste_text with your ORIGINAL full text (do NOT use the truncated 'expected' echo) " +
        "— it pastes atomically and restores the prior clipboard. If canSetValue is absent/unknown, " +
        "try desktop_set_value first and fall back to desktop_paste_text on PatternUnsupported.";
```

Also update the class doc-comment (lines 10-12) reference "either desktop_set_value or desktop_clipboard_set" → "either desktop_set_value or desktop_paste_text".

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~VerifyResultTests" -v n`
Expected: `Passed! - Failed: 0` (existing VerifyResult tests still green — verify none asserted the old `desktop_clipboard_set` string; if one did, it encoded the OLD contract and must be updated to `desktop_paste_text`).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Tools/VerifyResult.cs test/FlaUI.Mcp.Tests/Interaction/VerifyResultTests.cs
git commit -m "feat(verify): re-point no-ValuePattern remedy to atomic desktop_paste_text"
```

---

## Task 8a: `PasteFlow` orchestrator + `IPasteEffects` (pure, headless behavioral tests)

Extract the paste ORCHESTRATION (ordering + gating + restore + verify-outcome) into a pure unit over an
injected effects seam, so the spec §9 SAFETY INVARIANTS are enforced by headless CI (not just Desktop
smoke). Matches the repo's pure-core idiom (`UnicodeKeyTyper.Drive`, `TypedTextVerifier`). *(Panel r1 Seat 3.)*

**Files:**
- Create: `src/FlaUI.Mcp.Server/Tools/PasteFlow.cs` (`IPasteEffects`, `PasteOutcome`, `PasteFlow.RunAsync`)
- Test: `test/FlaUI.Mcp.Tests/Interaction/PasteFlowTests.cs` (headless; a recording fake effects)

- [ ] **Step 1: Write the failing tests (behavioral, with a recording fake)**

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class PasteFlowTests
{
    // Recording fake: logs the ORDER of side-effects and lets each test script the reads/snapshot.
    sealed class FakeEffects : IPasteEffects
    {
        public List<string> Log = new();
        public ActionTarget Target = new(1, 0, "notepad", "Notepad");
        public VerifyRead Before, After;
        public bool AfterThrows;
        public ClipboardSnapshot Snap = new(PriorClipboardKind.Empty, null);
        public Exception? PreflightThrows, PasteThrows;

        public Task<(ActionTarget, VerifyRead)> FocusAndBeforeReadAsync(bool verify)
        { Log.Add("focus"); return Task.FromResult((Target, Before)); }
        public void Preflight(ActionTarget t) { Log.Add("preflight"); if (PreflightThrows is { } e) throw e; }
        public Task<ClipboardSnapshot> SnapshotAsync() { Log.Add("snapshot"); return Task.FromResult(Snap); }
        public Task SetClipboardAsync(string text) { Log.Add("set:" + text); return Task.CompletedTask; }
        public Task PasteAsync(ActionTarget t) { Log.Add("paste"); if (PasteThrows is { } e) throw e; return Task.CompletedTask; }
        public void AuditForceOverwrite() => Log.Add("audit-force");
        public Task<VerifyRead> ReadAfterAsync() { Log.Add("read-after"); if (AfterThrows) throw new Exception("uia"); return Task.FromResult(After); }
    }

    static Task<PasteOutcome> Run(FakeEffects fx, string text = "bar", bool verify = true, bool force = false)
        => PasteFlow.RunAsync(fx, text, verify, force, _ => Task.CompletedTask); // delay is a no-op in tests

    [Fact]
    public async Task Refused_preflight_never_touches_the_clipboard()
    {
        var fx = new FakeEffects { PreflightThrows = new ToolException(ToolErrorCode.InputNotLeased, "x", "y") };
        await Assert.ThrowsAsync<ToolException>(() => Run(fx));
        Assert.DoesNotContain(fx.Log, s => s.StartsWith("set:"));   // clipboard NEVER mutated
        Assert.DoesNotContain("snapshot", fx.Log);                  // refused before we even classify
    }

    [Fact]
    public async Task NonText_without_force_throws_before_any_set()
    {
        var fx = new FakeEffects { Snap = new(PriorClipboardKind.NonText, null) };
        var ex = await Assert.ThrowsAsync<ToolException>(() => Run(fx, force: false));
        Assert.Equal(ToolErrorCode.ClipboardHoldsNonText, ex.Code);
        Assert.DoesNotContain(fx.Log, s => s.StartsWith("set:"));
    }

    [Fact]
    public async Task NonText_with_force_audits_BEFORE_it_overwrites()
    {
        var fx = new FakeEffects { Snap = new(PriorClipboardKind.NonText, null) };
        await Run(fx, force: true);
        Assert.True(fx.Log.IndexOf("audit-force") < fx.Log.IndexOf("set:bar")); // audit precedes clobber (Seat 2)
    }

    [Fact]
    public async Task Preflight_runs_before_the_first_clipboard_set()
    {
        var fx = new FakeEffects { Snap = new(PriorClipboardKind.Empty, null),
            Before = new VerifyRead { Text = "" }, After = new VerifyRead { Text = "bar" } };
        await Run(fx);
        Assert.True(fx.Log.IndexOf("preflight") < fx.Log.IndexOf("set:bar"));
    }

    [Fact]
    public async Task Verify_false_never_restores_and_reports_abandoned()
    {
        var fx = new FakeEffects { Snap = new(PriorClipboardKind.Text, "PRIOR") };
        var r = await Run(fx, verify: false);
        Assert.Equal("abandoned", r.ClipboardRestored);
        Assert.DoesNotContain("read-after", fx.Log);
        Assert.DoesNotContain(fx.Log, s => s == "set:PRIOR");       // prior NOT restored
    }

    [Fact]
    public async Task Confirmed_containment_restores_prior_text()
    {
        var fx = new FakeEffects { Snap = new(PriorClipboardKind.Text, "PRIOR"),
            Before = new VerifyRead { Text = "foo" }, After = new VerifyRead { Text = "foobar" } };
        var r = await Run(fx);                                       // after contains "bar", before did not
        Assert.Equal("text", r.ClipboardRestored);
        Assert.Contains("set:PRIOR", fx.Log);
    }

    [Fact]
    public async Task Unconfirmed_containment_abandons_and_leaves_payload()
    {
        var fx = new FakeEffects { Snap = new(PriorClipboardKind.Text, "PRIOR"),
            Before = new VerifyRead { Text = "foo" }, After = new VerifyRead { Text = "foo" } }; // paste didn't land
        var r = await Run(fx);
        Assert.Equal("abandoned", r.ClipboardRestored);
    }

    [Fact]
    public async Task Rich_clipboard_confirmed_reports_text_degraded()
    {
        var fx = new FakeEffects { Snap = new(PriorClipboardKind.TextWithRichFormats, "PRIOR"),
            Before = new VerifyRead { Text = "" }, After = new VerifyRead { Text = "bar" } };
        var r = await Run(fx);
        Assert.Equal("text-degraded", r.ClipboardRestored);
    }

    [Fact]
    public async Task After_read_failure_is_soft_and_abandons()
    {
        var fx = new FakeEffects { Snap = new(PriorClipboardKind.Text, "PRIOR"),
            Before = new VerifyRead { Text = "" }, AfterThrows = true };
        var r = await Run(fx);
        Assert.Equal("abandoned", r.ClipboardRestored);
        Assert.Equal("read-failed", r.Verify.Reason);
    }

    [Fact]
    public async Task Keystroke_fault_propagates_and_never_restores()   // Seat 2: paste throws -> abort, no restore
    {
        var fx = new FakeEffects { Snap = new(PriorClipboardKind.Text, "PRIOR"),
            Before = new VerifyRead { Text = "" }, PasteThrows = new ToolException(ToolErrorCode.InputBudgetExceeded, "x", "y") };
        await Assert.ThrowsAsync<ToolException>(() => Run(fx));
        Assert.DoesNotContain("read-after", fx.Log);                    // never reached confirmation
        Assert.DoesNotContain(fx.Log, s => s == "set:PRIOR");           // prior clipboard NOT restored
    }

    [Fact]
    public async Task Empty_prior_clipboard_confirmed_reports_empty()   // Seat 2
    {
        var fx = new FakeEffects { Snap = new(PriorClipboardKind.Empty, null),
            Before = new VerifyRead { Text = "" }, After = new VerifyRead { Text = "bar" } };
        var r = await Run(fx);
        Assert.Equal("empty", r.ClipboardRestored);
    }

    [Fact]
    public async Task Forced_nontext_reports_none_nontext()             // Seat 2
    {
        var fx = new FakeEffects { Snap = new(PriorClipboardKind.NonText, null),
            Before = new VerifyRead { Text = "" }, After = new VerifyRead { Text = "bar" } };
        var r = await Run(fx, force: true);
        Assert.Equal("none-nontext", r.ClipboardRestored);
    }
}
```

> NOTE ON THE CONTAINMENT GATE (accepted best-effort limitation, Claude-as-consumer decision):
> restore is confirmed via `after.Contains(payload) && !before.Contains(payload)`. It therefore CANNOT
> confirm — and so reports `"abandoned"` (safe; payload left on clipboard) — when the pasted text
> **already appears elsewhere in the field** (e.g. a lone `}` or a common keyword), or when a reactive
> editor **transforms** the pasted text. This is intentional: restore is a best-effort courtesy, not a
> guarantee (the paste + safety ARE guaranteed). The precise consumption signal (a delayed-render
> clipboard `WM_RENDERFORMAT`) is the tracked Phase 7.1 follow-up; do NOT weaken containment here.

> STATE-VERIFICATION: confirm `VerifyRead`'s real shape (it is used in `InputTools.DesktopType` as a value with `.Text`(string?), `.Redacted`(bool), `.CanSetValue`(bool?)). If `VerifyRead` is a positional record (not an object-initializer type), adjust the fake's `new VerifyRead { ... }` to the real ctor. Confirm `ToolException` exposes `.Code`. STOP + report divergence rather than adapting silently.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~PasteFlowTests" -v n`
Expected: FAIL — `PasteFlow` / `IPasteEffects` / `PasteOutcome` do not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/FlaUI.Mcp.Server/Tools/PasteFlow.cs`:

```csharp
using System;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;

namespace FlaUI.Mcp.Server.Tools;

/// <summary>The side-effects desktop_paste_text needs, injected so the ordering/gating/restore logic
/// in <see cref="PasteFlow"/> is headless-testable with a recording fake (spec §9 safety invariants).
/// The production impl (in InputTools) wraps PerceptionManager / InputGuard / ClipboardAccess.</summary>
public interface IPasteEffects
{
    Task<(ActionTarget target, VerifyRead before)> FocusAndBeforeReadAsync(bool verify);
    void Preflight(ActionTarget target);          // throws ToolException on refusal
    Task<ClipboardSnapshot> SnapshotAsync();
    Task SetClipboardAsync(string text);
    Task PasteAsync(ActionTarget target);         // Ctrl+V; throws on guard refusal
    void AuditForceOverwrite();
    Task<VerifyRead> ReadAfterAsync();            // may throw; PasteFlow treats a throw as read-failed
}

/// <summary>The clipboardRestored wire value + the verify object, produced by the pure flow.</summary>
public readonly record struct PasteOutcome(string ClipboardRestored, VerifyResult Verify);

/// <summary>Pure orchestration of a paste: focus/before-read -> ALL refusal gates -> classify ->
/// (fail-fast non-text) -> set -> Ctrl+V -> confirm-consumption(containment) -> conditional restore +
/// verify. No UIA/clipboard/Win32 here — all effects are injected. Spec §4/§5/§6.</summary>
public static class PasteFlow
{
    private const int VerifySettleMs = 100;

    public static async Task<PasteOutcome> RunAsync(IPasteEffects fx, string text, bool verify,
        bool forceOverwriteClipboard, Func<int, Task> delay)
    {
        var (target, before) = await fx.FocusAndBeforeReadAsync(verify);

        fx.Preflight(target); // ALL refusal gates BEFORE any clipboard mutation (spec §4 step 3)

        var snap = await fx.SnapshotAsync();
        if (snap.Kind == PriorClipboardKind.NonText && !forceOverwriteClipboard)
            throw new ToolException(ToolErrorCode.ClipboardHoldsNonText,
                "The clipboard holds non-text content (image/files) that cannot be preserved.",
                "re-call with forceOverwriteClipboard=true to overwrite it, or clear the clipboard first");

        if (snap.Kind == PriorClipboardKind.NonText) fx.AuditForceOverwrite(); // BEFORE the clobber (Seat 2)
        await fx.SetClipboardAsync(text);   // first mutation
        await fx.PasteAsync(target);        // Ctrl+V through the full guard pipeline

        if (!verify)
            return new PasteOutcome("abandoned", VerifyResult.Disabled);

        await delay(VerifySettleMs);
        VerifyRead after;
        try { after = await fx.ReadAfterAsync(); }
        catch
        {
            return new PasteOutcome("abandoned",
                VerifyResult.From(new VerifyOutcome(VerifyStatus.Skipped, "read-failed", null, null)));
        }

        // Consumption gate = containment (INDEPENDENT of the agent-facing verify outcome; the two answer
        // different questions and MAY disagree, e.g. clipboardRestored:"text" with verify field-not-empty).
        bool consumed = !after.Redacted && after.Text is not null
            && after.Text.Contains(text, StringComparison.Ordinal)
            && !(before.Text?.Contains(text, StringComparison.Ordinal) ?? false);

        string restored = "abandoned";
        if (consumed)
            restored = snap.Kind switch
            {
                PriorClipboardKind.Text => await SetAnd(fx, snap.Text ?? string.Empty, "text"),
                PriorClipboardKind.TextWithRichFormats => await SetAnd(fx, snap.Text ?? string.Empty, "text-degraded"),
                PriorClipboardKind.Empty => await SetAnd(fx, string.Empty, "empty"),
                PriorClipboardKind.NonText => "none-nontext", // forced; cannot restore
                _ => "abandoned",
            };
        else if (snap.Kind == PriorClipboardKind.NonText) restored = "none-nontext";

        VerifyOutcome outcome =
            before.Redacted ? new VerifyOutcome(VerifyStatus.Skipped, "redacted", null, null)
            : before.Text is null ? new VerifyOutcome(VerifyStatus.Skipped, "no-textpattern", null, null)
            : before.Text.Length != 0 ? new VerifyOutcome(VerifyStatus.Skipped, "field-not-empty", null, null)
            : after.Redacted ? new VerifyOutcome(VerifyStatus.Skipped, "redacted", null, null)
            : after.Text is null ? new VerifyOutcome(VerifyStatus.Skipped, "read-failed", null, null)
            : TypedTextVerifier.Check(before.Text, after.Text, text);

        return new PasteOutcome(restored, VerifyResult.From(outcome, after.CanSetValue));
    }

    private static async Task<string> SetAnd(IPasteEffects fx, string text, string label)
    { await fx.SetClipboardAsync(text); return label; }
}
```

> SHAPE-DIVERGENCE STOP: `ClipboardRestored` is a WIRE STRING enum — exactly one of `text|text-degraded|empty|abandoned|none-nontext`. If `VerifyRead`/`VerifyOutcome`/`VerifyResult.From` signatures differ from those pasted (verified from `InputTools.cs`/`VerifyResult.cs` reads), STOP + report `[pasted] -> [actual]`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~PasteFlowTests" -v n`
Expected: `Passed! - Failed: 0` (all 9 behavioral invariants green).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Tools/PasteFlow.cs test/FlaUI.Mcp.Tests/Interaction/PasteFlowTests.cs
git commit -m "feat(input): pure PasteFlow orchestrator + IPasteEffects (headless safety invariants)"
```

---

## Task 8b: `InputTools.DesktopPasteText` — thin wiring over `PasteFlow`

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Tools/InputTools.cs` (add `MaxPasteUnits`, the tool, and a nested real `IPasteEffects`)
- Test: `test/FlaUI.Mcp.Tests/Interaction/InputToolsContractTests.cs` (headless reflection) + `InputToolsTests.cs` (Desktop e2e)

- [ ] **Step 1: Write the failing headless contract test**

Append to `InputToolsContractTests.cs` (follows the file's existing reflection style):

```csharp
    [Fact]
    public void DesktopPasteText_is_declared_Destructive_with_expected_params()
    {
        var m = typeof(InputTools).GetMethod("DesktopPasteText");
        Assert.NotNull(m);
        var names = m!.GetParameters().Select(p => p.Name).ToArray();
        Assert.Contains("forceOverwriteClipboard", names);
        Assert.Contains("verify", names);
    }
```

> STATE-VERIFICATION: open `InputToolsContractTests.cs` + `ToolReadOnlyInvariantTests.cs`; the latter already asserts EVERY McpServerTool is exactly one of ReadOnly/Destructive and that Destructive tools short-circuit in read-only — so `desktop_paste_text` being `Destructive` is auto-covered there once it exists. Match that file's attribute accessor; don't hand-roll a Destructive assertion if the invariant test already covers it.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~InputToolsContractTests.DesktopPasteText" -v n`
Expected: FAIL — `DesktopPasteText` not defined.

- [ ] **Step 3: Write the tool + a real `IPasteEffects`**

Add `private const int MaxPasteUnits = 1_000_000;` near `MaxTypeUnits` (InputTools.cs:15). Add after `DesktopType` (line 143):

```csharp
    [McpServerTool(Destructive = true), Description("Paste text into the focused element via an atomic clipboard-backed Ctrl+V — the reliable path for reactive editors (new Win11 Notepad, Chromium contenteditable) that garble desktop_type keystrokes. ref = the element to focus. Up to 1,000,000 UTF-16 units. ALL input gates (lease/deny-list/budget/session) are checked BEFORE the clipboard is touched. By default (verify=true) the element is read back and a soft `verify` object is returned; the prior clipboard is restored ONLY when the paste is confirmed to have landed (else `clipboardRestored:\"abandoned\"`, leaving your text on the clipboard — expect this in reactive editors that transform pasted text, and whenever verify=false). A NON-text clipboard (image/files) is refused (ClipboardHoldsNonText) unless forceOverwriteClipboard=true. Mixed text+rich clipboards restore as plain text (`clipboardRestored:\"text-degraded\"`). Requires an active input lease; InputNotLeased/TargetDenied/etc. otherwise. Blocked in --read-only-mode.")]
    public Task<string> DesktopPasteText(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Element ref to focus and paste into, e.g. e23.")] string @ref,
        [Description("Text to paste (<=1,000,000 UTF-16 units).")] string text,
        [Description("Block timeout ms (default 4000).")] int timeoutMs = DefaultTimeoutMs,
        [Description("Read the element back and report whether the paste landed (default true). Soft — never throws. ALSO gates clipboard restore: with verify=false the prior clipboard is not restored (clipboardRestored:\"abandoned\").")] bool verify = true,
        [Description("Proceed even if the clipboard holds NON-text content (image/files) that cannot be preserved. Default false = refuse with ClipboardHoldsNonText before any mutation.")] bool forceOverwriteClipboard = false)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            if (string.IsNullOrEmpty(text))
                throw new ToolException(ToolErrorCode.InvalidArguments,
                    "No text to paste.", "pass the text to paste (empty is rejected so a degenerate call can't clobber the clipboard)");
            if (text.Length > MaxPasteUnits)
                throw new ToolException(ToolErrorCode.InvalidArguments,
                    $"Text exceeds the {MaxPasteUnits} UTF-16 unit per-call cap.", "split the paste across calls on a whole-character boundary");

            var fx = new PasteEffects(_perception, _guard, new WindowHandle(window), @ref, timeoutMs);
            var outcome = await PasteFlow.RunAsync(fx, text, verify, forceOverwriteClipboard, ms => Task.Delay(ms));
            return ToolResponse.Ok(new { ok = true, pathUsed = "clipboard-paste",
                clipboardRestored = outcome.ClipboardRestored, verify = outcome.Verify });
        });

    /// <summary>Production IPasteEffects: focus/read via the perception STA, gate via InputGuard, and
    /// borrow the clipboard via ClipboardAccess. Effect ORDER + gating live in PasteFlow (tested headless).</summary>
    private sealed class PasteEffects : IPasteEffects
    {
        private readonly PerceptionManager _p; private readonly InputGuard _g;
        private readonly WindowHandle _win; private readonly string _ref; private readonly int _timeout;
        public PasteEffects(PerceptionManager p, InputGuard g, WindowHandle win, string @ref, int timeout)
        { _p = p; _g = g; _win = win; _ref = @ref; _timeout = timeout; }

        public Task<(ActionTarget, VerifyRead)> FocusAndBeforeReadAsync(bool verify) =>
            _p.RunOnRefForInputAsync(_win, _ref, (win, el) =>
            {
                el.Focus();
                var t = InputTargeting.ResolveElementTarget(win, el);
                var b = verify ? VerifyReader.FromElement(el) : default;
                return (t, b);
            }, _timeout);

        public void Preflight(ActionTarget target) => _g.PreflightInput(target);
        public Task<ClipboardSnapshot> SnapshotAsync() => ClipboardAccess.Snapshot();
        public Task SetClipboardAsync(string text) => ClipboardAccess.SetTextAsync(text);
        public Task PasteAsync(ActionTarget target) => Task.Run(() => _g.KeyChord(new[] { "Ctrl" }, "V", target));
        public void AuditForceOverwrite() => System.Console.Error.WriteLine("[audit] desktop_paste_text: force-overwrite of a non-text clipboard.");
        public Task<VerifyRead> ReadAfterAsync() =>
            _p.RunOnRefReadAsync(_win, _ref, el => VerifyReader.FromElement(el, readCapability: true), _timeout);
    }
```

> STATE-VERIFICATION: confirm `RunOnRefForInputAsync` returns `Task<(ActionTarget, VerifyRead)>`-compatible for the tuple lambda (per DesktopType usage) and `RunOnRefReadAsync` returns `Task<VerifyRead>`. If the tuple element ordering or a name differs, STOP + report rather than adapt.

- [ ] **Step 4: Run headless gate**

Run: `dotnet test -c Release --filter "Category!=Desktop"`
Expected: `Passed! - Failed: 0` (contract + PasteFlow tests green; ToolReadOnlyInvariantTests now covers the new Destructive tool; nothing else regressed).

- [ ] **Step 5: Write + run the Desktop end-to-end test (connected + lease)**

Append to `InputToolsTests.cs` (reuses the TestApp `aid=Input` fixed in Task 1):

```csharp
    [SkippableFact]
    public async Task PasteText_writes_text_into_the_focused_textbox_and_restores_clipboard()
    {
        Skip.If(InputLocked(), "no active input lease — grant one with `flaui-mcp unlock`");
        await ClipboardAccess.SetTextAsync("PRIOR-CLIP-guard");        // stage a text clipboard
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var perception = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
        var handle = await mgr.OpenByPidAsync(app.Process.Id);
        var snap = await perception.SnapshotAsync(handle, new SnapshotOptions());
        var inputRef = RefForAid(snap.Tree, "Input");

        var tools = BuildTools(mgr, perception);
        var json = await tools.DesktopPasteText(handle.Id, inputRef, "pasted-hello", 4000);
        Assert.DoesNotContain("\"error\"", json);
        Assert.Contains("clipboard-paste", json);

        var val = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            win.FindFirstDescendant(cf => cf.ByAutomationId("Input"))!.AsTextBox().Text);
        Assert.Contains("pasted-hello", val);
        Assert.Equal("PRIOR-CLIP-guard", await ClipboardAccess.GetTextAsync()); // empty field -> confirmed -> restored
    }
```

Run (connected + `flaui-mcp unlock --minutes 5`): `dotnet test --filter "FullyQualifiedName~InputToolsTests.PasteText" -v n`
Expected: `Passed! - Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Server/Tools/InputTools.cs test/FlaUI.Mcp.Tests/Interaction/
git commit -m "feat(input): desktop_paste_text tool — thin wiring over PasteFlow"
```

---

## Task 9: Version bump + docs

**Files:**
- Modify: `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj` (Version/AppVersion 0.7.6 → 0.7.7)
- Modify: `installer/flaui-mcp.iss` (version 0.7.6 → 0.7.7)
- Modify: `CHANGELOG.md` (add `[0.7.7]` entry)
- Modify: `ROADMAP.md` (add Phase 7 delivered entry under Phase 6)
- Modify: `README.md` (tool list: add `desktop_paste_text` in the Synthetic input section)
- Modify: `.claude/skills/driving-flaui-mcp/SKILL.md` (note the paste tool + the verify remedy re-point)

- [ ] **Step 1: Bump versions**

In `FlaUI.Mcp.Server.csproj` change `<Version>0.7.6</Version>` and any `<AppVersion>`/`<AssemblyVersion>` 0.7.6 → `0.7.7`. In `installer/flaui-mcp.iss` change the `0.7.6` version constant → `0.7.7`.
Run: `grep -rn "0\.7\.6" src installer` — confirm no stale `0.7.6` remains in version fields.

- [ ] **Step 2: CHANGELOG + ROADMAP + README + SKILL**

- `CHANGELOG.md`: add a `## [0.7.7] - 2026-07-03` entry: "Added `desktop_paste_text` — atomic clipboard-preserving paste for reactive editors (session-state safe: all input gates precede the clipboard borrow; restore only on confirmed consumption; non-text fail-fast with `forceOverwriteClipboard`; mixed text+rich reports `text-degraded`). `desktop_type` verify now recommends `desktop_paste_text` for no-ValuePattern targets."
- `ROADMAP.md`: add a `- **Phase 7** ✅ **(v0.7.7) — `desktop_paste_text`.** ...` bullet after the Phase 6 entry; strike the "Promote the clipboard-paste remedy" backlog item as delivered.
- `README.md`: in the Synthetic input tool list, add `desktop_paste_text` next to `desktop_type` with a one-line description + the `forceOverwriteClipboard`/`clipboardRestored` note; update the "prefer desktop_set_value" limitation line to also name `desktop_paste_text` as the contenteditable path. **Be honest:** note clipboard restore is best-effort and reports `"abandoned"` when the paste can't be confirmed (incl. `verify=false` and reactive editors that transform pasted text).
- `SKILL.md`: add `desktop_paste_text` to the synthetic-input tool line + a one-line "reactive editor → paste" driving note.

- [ ] **Step 3: Verify build + full headless gate**

Run: `dotnet build -c Release && dotnet test -c Release --filter "Category!=Desktop"`
Expected: `Build succeeded`, `Passed! - Failed: 0`.

- [ ] **Step 4: Commit**

```bash
git add src installer CHANGELOG.md ROADMAP.md README.md .claude/skills/driving-flaui-mcp/SKILL.md
git commit -m "chore(release): v0.7.7 — desktop_paste_text (version + CHANGELOG + ROADMAP + docs)"
```

---

## Self-review (author checklist — completed 2026-07-03)

**Spec coverage:** §3 contract → T8 (tool) + T4 (error) + T8 (cap). §4 algorithm ordering (preflight-before-mutation) → T6 + T8. §5 clipboard model (classify/fail-fast/restore table) → T2 + T3 + T8. §6 verify + containment gate → T8. §7 remedy re-point → T7. §9 testing (pure + Desktop split; broken-harness prereq) → T1 + every task's tests. Version/docs → T9. **All spec sections map to a task.**

**Placeholder scan:** T1 is intentionally investigative (root cause is observed at runtime) but gives a concrete diagnostic procedure + the two concrete fixes; not a "TODO". No other vague steps; all code steps show complete code.

**Type consistency:** `PriorClipboardKind` (T2) used identically in T3/T8; `ClipboardSnapshot(Kind, Text)` (T3) consumed in T8; `HasFreeSlot` (T5) called in T6; `PreflightInput` (T6) called in T8; `clipboardRestored` enum strings identical across spec §5 and T8; `RecommendedFallbackTool="desktop_paste_text"` consistent T7↔spec §7.

**Known verification debt flagged for the executor (STATE-VERIFICATION steps in T6/T7/T8):** the exact `InputGuardTests` fake helpers, `VerifyOutcome` ctor arg order, and `RunOnRef*`/`VerifyReader` signatures must be confirmed against the live files at implementation time — the plan pastes them from current reads but marks each as a STOP-on-divergence point.
