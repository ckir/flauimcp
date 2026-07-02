# Phase 4b.3 — ValuePattern-aware verify remedy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** On a `desktop_type` verify mismatch, branch the fallback remedy on the target's ValuePattern write-capability (emit a `canSetValue` wire fact + best-effort tool hint + strategy-listing prose) so a no-ValuePattern target (Electron `contenteditable`) is routed to the clipboard-paste path instead of a dead-end `desktop_set_value`.

**Architecture:** A new Core leaf `ValueCapability.CanSetValue(el) -> bool?` is read INSIDE the after-read's STA lambda on the already-resolved element (gated by a `readCapability` flag so only the after-read pays ~2 COM calls; no extra resolution, no cross-thread element). It rides on `VerifyRead.CanSetValue` to the Server-layer mapper `VerifyResult.From(outcome, canSetValue)`, which branches only the Mismatch arm. `TypedTextVerifier` stays pure and untouched. The headless-testable nugget is the `From` mapping (VerifyResultTests).

**Tech Stack:** C# / .NET 10, FlaUI/UIA3, xUnit. Build: `dotnet build`. Headless tests: `dotnet test --filter "Category!=Desktop"`.

**Spec:** `docs/superpowers/specs/2026-07-02-flaui-mcp-phase4b3-valuepattern-remedy-design.md`

---

## File Structure

- **Create** `src/FlaUI.Mcp.Core/Interaction/ValueCapability.cs` — the `CanSetValue` leaf.
- **Modify** `src/FlaUI.Mcp.Core/Interaction/VerifyReader.cs` — `VerifyRead` gains `bool? CanSetValue`; `FromElement` gains `bool readCapability = false`.
- **Modify** `src/FlaUI.Mcp.Server/Tools/VerifyResult.cs` — `CanSetValue` wire field; `From(VerifyOutcome, bool? canSetValue = null)`; branch mismatch arm; new strategy-listing prose.
- **Modify** `src/FlaUI.Mcp.Server/Tools/InputTools.cs` — before-read `FromElement(el)` (default false), after-read `FromElement(el, readCapability: true)`, final `From(outcome, after.CanSetValue)`.
- **Modify** `test/FlaUI.Mcp.Tests/Interaction/VerifyResultTests.cs` — add true/false/null mismatch mapping tests.
- **Modify** `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj`, `installer/flaui-mcp.iss`, `CHANGELOG.md`, `ROADMAP.md`, `README.md`, `.claude/skills/driving-flaui-mcp/SKILL.md` — version + docs.

---

### Task 1: `ValueCapability.CanSetValue` leaf (Core)

**Files:**
- Create: `src/FlaUI.Mcp.Core/Interaction/ValueCapability.cs`

Touches live UIA (like `VerifyReader`) → no headless unit test; exercised in the documented smoke. This task is create + build only.

- [ ] **Step 0 — STATE-VERIFY:** Open `src/FlaUI.Mcp.Core/Interaction/Interactor.cs` lines 24-32 and confirm the FlaUI idiom in use is `el.Patterns.Value.IsSupported` (bool) and `el.Patterns.Value.Pattern.IsReadOnly.ValueOrDefault` (bool). If the accessor shape differs, STOP and report `STATE_MISMATCH: <what>`.

- [ ] **Step 1 — Create the leaf:**

```csharp
using FlaUI.Core.AutomationElements;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Can <c>desktop_set_value</c> write this element via UIA ValuePattern?
/// <c>true</c> = ValuePattern supported AND not read-only; <c>false</c> = no ValuePattern OR
/// read-only; <c>null</c> = couldn't determine (a UIA read threw). Advisory only — feeds the
/// desktop_type verify-mismatch remedy so a no-ValuePattern target (e.g. an Electron
/// contenteditable) is routed to the clipboard path instead of a dead-end set_value.</summary>
public static class ValueCapability
{
    public static bool? CanSetValue(AutomationElement el)
    {
        // WHOLE predicate in the try: `!...IsReadOnly` dereferences the pattern and makes its own
        // cross-process COM read that can throw independently of IsSupported. C# && short-circuits,
        // so `.Pattern` is never touched when IsSupported is false (no throw on unsupported).
        try { return el.Patterns.Value.IsSupported && !el.Patterns.Value.Pattern.IsReadOnly.ValueOrDefault; }
        catch { return null; }
    }
}
```

- [ ] **Step 2 — Build:**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` (repo baseline is 5 projects, 0/0).

- [ ] **Step 3 — Commit:**

```bash
git add src/FlaUI.Mcp.Core/Interaction/ValueCapability.cs
git commit -m "feat(verify): ValueCapability.CanSetValue leaf (bool? set_value writability)"
```

---

### Task 2: `VerifyResult` wire branch + strategy prose (Server) — TDD, headless

**Files:**
- Test: `test/FlaUI.Mcp.Tests/Interaction/VerifyResultTests.cs`
- Modify: `src/FlaUI.Mcp.Server/Tools/VerifyResult.cs`

**Oracle:** the new tests below are the spec's wire contract. Implement until they pass; do not edit the tests to match the code. The pre-existing `Mismatch_carries_stable_fallback_prose_remedy_and_bounded_echoes` test (calls `From(outcome)` single-arg) MUST stay green — the new `canSetValue` param defaults `null → "desktop_set_value"`.

- [ ] **Step 0 — STATE-VERIFY:** Open `src/FlaUI.Mcp.Server/Tools/VerifyResult.cs` and confirm: `From(VerifyOutcome o)` is single-arg; the Mismatch arm hardcodes `RecommendedFallbackTool = "desktop_set_value"` and `Remedy = RemedyProse`; `const int VerifyEchoMax = 256`. If different, STOP and report `STATE_MISMATCH`.

- [ ] **Step 1 — Write the failing tests.** Append these to `VerifyResultTests.cs` before the closing brace:

```csharp
    [Fact]
    public void Mismatch_canSetValue_true_recommends_set_value_and_emits_the_fact()
    {
        var o = new VerifyOutcome(VerifyStatus.Mismatch, null, Expected: "x", Actual: "y");
        var e = Wire(VerifyResult.From(o, canSetValue: true));
        Assert.True(e.GetProperty("mismatch").GetBoolean());
        Assert.True(Has(e, "canSetValue"));
        Assert.True(e.GetProperty("canSetValue").GetBoolean());
        Assert.Equal("desktop_set_value", e.GetProperty("recommendedFallbackTool").GetString());
    }

    [Fact]
    public void Mismatch_canSetValue_false_recommends_clipboard_and_emits_the_fact()
    {
        var o = new VerifyOutcome(VerifyStatus.Mismatch, null, Expected: "x", Actual: "y");
        var e = Wire(VerifyResult.From(o, canSetValue: false));
        Assert.True(Has(e, "canSetValue"));
        Assert.False(e.GetProperty("canSetValue").GetBoolean());
        Assert.Equal("desktop_clipboard_set", e.GetProperty("recommendedFallbackTool").GetString());
    }

    [Fact]
    public void Mismatch_canSetValue_null_defaults_to_set_value_and_omits_the_fact()
    {
        var o = new VerifyOutcome(VerifyStatus.Mismatch, null, Expected: "x", Actual: "y");
        var e = Wire(VerifyResult.From(o, canSetValue: null));
        Assert.False(Has(e, "canSetValue")); // JsonIgnore-when-null: unknown -> absent
        Assert.Equal("desktop_set_value", e.GetProperty("recommendedFallbackTool").GetString()); // safe default
    }

    [Fact]
    public void Mismatch_remedy_prose_names_both_strategies_and_warns_off_the_truncated_echo()
    {
        var o = new VerifyOutcome(VerifyStatus.Mismatch, null, Expected: "x", Actual: "y");
        var remedy = Wire(VerifyResult.From(o, canSetValue: false)).GetProperty("remedy").GetString()!;
        Assert.Contains("desktop_set_value", remedy);
        Assert.Contains("desktop_clipboard_set", remedy);
        Assert.Contains("desktop_key", remedy);
        Assert.Contains("expected", remedy); // the "do NOT use the truncated 'expected' echo" warning
    }

    [Fact]
    public void NonMismatch_arms_never_emit_canSetValue()
    {
        Assert.False(Has(Wire(VerifyResult.From(new VerifyOutcome(VerifyStatus.Match, null, null, null))), "canSetValue"));
        Assert.False(Has(Wire(VerifyResult.From(new VerifyOutcome(VerifyStatus.Skipped, "field-not-empty", null, null))), "canSetValue"));
        Assert.False(Has(Wire(VerifyResult.Disabled), "canSetValue"));
    }
```

- [ ] **Step 2 — Run tests, verify they fail:**

Run: `dotnet test --filter "Category!=Desktop&FullyQualifiedName~VerifyResultTests"`
Expected: FAIL — `From` has no 2-arg overload / `canSetValue` property missing.

- [ ] **Step 3 — Implement.** In `VerifyResult.cs`:

(a) Add the wire field after the `Actual` property (before `RecommendedFallbackTool`):

```csharp
    [JsonPropertyName("canSetValue"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CanSetValue { get; init; }
```

(b) Replace the `RemedyProse` const with the strategy-listing prose:

```csharp
    private const string RemedyProse =
        "Text was not entered correctly — the target likely races synthetic keystrokes. " +
        "If canSetValue is true, use desktop_set_value (UIA ValuePattern) for byte-exact entry. " +
        "If canSetValue is false (e.g. an Electron contenteditable with no ValuePattern), use the " +
        "clipboard-paste path: desktop_clipboard_set with your ORIGINAL full text — do NOT use the " +
        "truncated 'expected' echo in this result — then desktop_key \"Ctrl+V\" targeting this " +
        "element's ref+window (which focuses it first). If canSetValue is absent/unknown, try " +
        "desktop_set_value first and fall back to the clipboard path on PatternUnsupported.";
```

(c) Change the `From` signature and the Mismatch arm:

```csharp
    public static VerifyResult From(VerifyOutcome o, bool? canSetValue = null) => o.Status switch
    {
        VerifyStatus.Match =>
            new() { Ran = true, Verified = true, Mismatch = false },
        VerifyStatus.Mismatch =>
            new()
            {
                Ran = true,
                Verified = false,
                Mismatch = true,
                Expected = TypedTextVerifier.Truncate(o.Expected ?? string.Empty, VerifyEchoMax),
                Actual = TypedTextVerifier.Truncate(o.Actual ?? string.Empty, VerifyEchoMax),
                CanSetValue = canSetValue,
                // true/null -> set_value (null is the SAFE default: a wrong set_value guess yields a
                // recoverable PatternUnsupported; defaulting to clipboard would clobber the clipboard).
                RecommendedFallbackTool = canSetValue == false ? "desktop_clipboard_set" : "desktop_set_value",
                Remedy = RemedyProse,
            },
        VerifyStatus.Skipped =>
            new() { Ran = true, Verified = false, Mismatch = false, Reason = o.Reason },
        _ => throw new ArgumentOutOfRangeException(nameof(o), o.Status, "unknown VerifyStatus"),
    };
```

Update the `RemedyProse`/`recommendedFallbackTool` note in the class XML doc if it names only `desktop_set_value`.

- [ ] **Step 4 — Run tests, verify PASS:**

Run: `dotnet test --filter "Category!=Desktop&FullyQualifiedName~VerifyResultTests"`
Expected: PASS (all new tests + the pre-existing Mismatch/Match/Skipped/Disabled/truncation tests).

- [ ] **Step 5 — Commit:**

```bash
git add src/FlaUI.Mcp.Server/Tools/VerifyResult.cs test/FlaUI.Mcp.Tests/Interaction/VerifyResultTests.cs
git commit -m "feat(verify): branch mismatch remedy on canSetValue wire fact + strategy prose"
```

---

### Task 3: `VerifyRead.CanSetValue` + `FromElement` readCapability gate (Core)

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Interaction/VerifyReader.cs`

Touches live UIA → no headless test; build only. `VerifyRead` is a `readonly record struct` with positional params `(string? Text, bool Redacted)` — adding a third positional updates all 3 construction sites in this file.

- [ ] **Step 0 — STATE-VERIFY:** Open `VerifyReader.cs` and confirm `VerifyRead` is `public readonly record struct VerifyRead(string? Text, bool Redacted);` and `FromElement` constructs it at three sites (`new VerifyRead(null, true)`, `new VerifyRead(null, false)`, `new VerifyRead(tp.DocumentRange.GetText(MaxReadChars), false)`). If different, STOP and report `STATE_MISMATCH`.

- [ ] **Step 1 — Add the field to the record:**

```csharp
public readonly record struct VerifyRead(string? Text, bool Redacted, bool? CanSetValue);
```

Update its XML doc to note: `CanSetValue` is populated only when `FromElement` is called with `readCapability:true` (the after-read); it is the set_value writability fact (see `ValueCapability`).

- [ ] **Step 2 — Add the gate to `FromElement` and update all three returns:**

```csharp
    public static VerifyRead FromElement(AutomationElement el, bool readCapability = false)
    {
        bool isPwd = RedactionPolicy.IsPasswordOrFailClosed(() => el.Properties.IsPassword.ValueOrDefault);
        if (isPwd) return new VerifyRead(null, true, null); // redacted short-circuits before the remedy branch
        bool? canSet = readCapability ? ValueCapability.CanSetValue(el) : null; // reuse this live element
        try
        {
            var tp = el.Patterns.Text.PatternOrDefault;
            if (tp is null) return new VerifyRead(null, false, canSet);
            return new VerifyRead(tp.DocumentRange.GetText(MaxReadChars), false, canSet);
        }
        catch { return new VerifyRead(null, false, canSet); }
    }
```

- [ ] **Step 3 — Build:**

Run: `dotnet build`
Expected: `Build succeeded` — BUT note `InputTools.cs`'s two `FromElement(el)` call sites still compile (the new param is optional). The `default` VerifyRead in InputTools (the `!verify` branch) also still compiles.

- [ ] **Step 4 — Commit:**

```bash
git add src/FlaUI.Mcp.Core/Interaction/VerifyReader.cs
git commit -m "feat(verify): VerifyRead.CanSetValue + FromElement readCapability gate"
```

---

### Task 4: Wire the fact through `InputTools.DesktopType` (Server)

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Tools/InputTools.cs`

- [ ] **Step 0 — STATE-VERIFY:** Open `InputTools.cs` and confirm the `DesktopType` body has: the before-read line `var b = verify ? VerifyReader.FromElement(el) : default;` (~L96); the after-read `after = await _perception.RunOnRefReadAsync(new WindowHandle(window), @ref, el => VerifyReader.FromElement(el), timeoutMs);` (~L126-127); the final `return ToolResponse.Ok(new { ok = true, pathUsed = "synthetic", verify = VerifyResult.From(outcome) });` (~L142). If different, STOP and report `STATE_MISMATCH`.

- [ ] **Step 1 — Before-read: leave capability OFF** (the before-read must not pay the COM read). It is already `FromElement(el)` (default `readCapability:false`) — no change needed; confirm it stays default-false.

- [ ] **Step 2 — After-read: turn capability ON.** Change the after-read lambda:

```csharp
                after = await _perception.RunOnRefReadAsync(new WindowHandle(window), @ref,
                    el => VerifyReader.FromElement(el, readCapability: true), timeoutMs);
```

- [ ] **Step 3 — Final mapping: pass the fact.** Change the final return:

```csharp
            return ToolResponse.Ok(new { ok = true, pathUsed = "synthetic", verify = VerifyResult.From(outcome, after.CanSetValue) });
```

(The earlier short-circuit returns — redacted / no-textpattern / field-not-empty — keep calling `VerifyResult.From(...)` single-arg: they are Skipped, emit no remedy, need no capability. Do NOT touch them.)

- [ ] **Step 4 — Build + full headless suite:**

Run: `dotnet build` then `dotnet test --filter "Category!=Desktop"`
Expected: `Build succeeded` 0/0; all headless tests green (baseline 242 + 5 new = **247**; report the actual passed count).

- [ ] **Step 5 — Commit:**

```bash
git add src/FlaUI.Mcp.Server/Tools/InputTools.cs
git commit -m "feat(verify): desktop_type wires after-read canSetValue into the mismatch remedy"
```

---

### Task 5: Version bump + CHANGELOG + ROADMAP

**Files:**
- Modify: `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj` (`<Version>`)
- Modify: `installer/flaui-mcp.iss` (`#define AppVersion`)
- Modify: `CHANGELOG.md`
- Modify: `ROADMAP.md`

- [ ] **Step 0 — STATE-VERIFY:** Confirm `FlaUI.Mcp.Server.csproj` has `<Version>0.7.4</Version>` and `installer/flaui-mcp.iss` has `#define AppVersion "0.7.4"`. If not 0.7.4, STOP and report `STATE_MISMATCH` (do not guess the next version).

- [ ] **Step 1 — Bump both version sources** `0.7.4 → 0.7.5`:
  - `FlaUI.Mcp.Server.csproj`: `<Version>0.7.5</Version>`
  - `installer/flaui-mcp.iss`: `#define AppVersion "0.7.5"`

- [ ] **Step 2 — CHANGELOG.** Add above the `[0.7.4]` entry (match the file's existing heading/date style — read the `[0.7.4]` block first):

```markdown
## [0.7.5] - 2026-07-02

### Changed
- `desktop_type` verify: on a mismatch, the recovery remedy is now chosen by the target's
  ValuePattern write-capability. The result carries a new `canSetValue` fact and the
  `recommendedFallbackTool` points to `desktop_clipboard_set` (clipboard-paste path) when the
  element has no writable ValuePattern (e.g. an Electron `contenteditable`) instead of always
  advising `desktop_set_value` (which returns `PatternUnsupported` there). The `remedy` prose now
  lists both strategies. Additive/backward-compatible; `verify` still never throws.
```

- [ ] **Step 3 — ROADMAP.** Mark Phase 4b.3 done (read the current Phase 4b.3 block near ROADMAP.md:105-108 and flip its status marker to match how shipped phases are marked in this file).

- [ ] **Step 4 — Build** (picks up the new version):

Run: `dotnet build`
Expected: `Build succeeded` 0/0.

- [ ] **Step 5 — Commit:**

```bash
git add src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj installer/flaui-mcp.iss CHANGELOG.md ROADMAP.md
git commit -m "chore(release): prep v0.7.5 — version, CHANGELOG, ROADMAP Phase 4b.3"
```

---

### Task 6: Docs — README + SKILL + XML contract note

**Files:**
- Modify: `README.md`
- Modify: `.claude/skills/driving-flaui-mcp/SKILL.md`

The repo is PUBLIC — README must not go stale (never push a stale README).

- [ ] **Step 0 — STATE-VERIFY:** Grep `README.md` and `SKILL.md` for the existing `desktop_type` / `verify` / `set_value`-vs-clipboard notes so the additions extend the real text (SKILL.md's gotchas table already states the set_value-vs-clipboard rule). If the anchor text is absent, STOP and report `STATE_MISMATCH`.

- [ ] **Step 1 — README verify note.** In the `desktop_type` description, add one sentence: on a verify mismatch the result now includes `canSetValue` and `recommendedFallbackTool` points to the clipboard-paste path (`desktop_clipboard_set` → `desktop_key "Ctrl+V"`) when the target has no writable ValuePattern.

- [ ] **Step 2 — SKILL.md.** In the gotchas table row for garbled typed text (the `verify.mismatch:true` row), add a clause: the mismatch now reports `canSetValue` (set_value writability) and `recommendedFallbackTool` picks `desktop_set_value` (canSetValue:true) vs the clipboard path (canSetValue:false). Keep it version-agnostic.

- [ ] **Step 3 — Verify no other stale references.** Grep the repo for `recommendedFallbackTool` and confirm every human-facing mention is consistent with the new branch (not "always desktop_set_value").

Run: `git grep -n "always.*desktop_set_value\|recommendedFallbackTool"`
Expected: no doc claims that the fallback is unconditionally `desktop_set_value`.

- [ ] **Step 4 — Commit:**

```bash
git add README.md .claude/skills/driving-flaui-mcp/SKILL.md
git commit -m "docs(verify): document canSetValue + clipboard-path remedy (v0.7.5)"
```

---

## Self-Review

- **Spec coverage:** Read-the-fact leaf → T1. Gate/carry (VerifyRead + readCapability) → T3. Wire branch + prose + safe null default + JsonIgnore-when-null → T2. Hot-path (before-read off, after-read on) + final mapping → T4. Version/CHANGELOG/ROADMAP → T5. README/SKILL/XML note → T6. Deferred `desktop_paste_text` — correctly NOT in this plan. JsonIgnore-when-null (user-approved) → T2 field attribute. Every spec section maps to a task.
- **Placeholder scan:** all code steps carry real code; version/CHANGELOG/ROADMAP steps cite the exact strings and read-before-edit for style. No TBD/TODO.
- **Type consistency:** `ValueCapability.CanSetValue(AutomationElement) -> bool?` (T1) is used in `FromElement`'s `canSet` (T3); `VerifyRead.CanSetValue` (T3) feeds `From(outcome, after.CanSetValue)` (T4); `From(VerifyOutcome, bool? canSetValue = null)` (T2) matches both the new call in T4 and the pre-existing single-arg call site. `recommendedFallbackTool` values `"desktop_set_value"` / `"desktop_clipboard_set"` consistent across T2 tests and impl.

## Exhaustiveness Self-Audit
- **Under-specified 'what':** none — DTO field (`canSetValue: bool?`, JsonIgnore-when-null), tool-hint mapping (true/null→set_value, false→clipboard_set), predicate (`IsSupported && !IsReadOnly`, whole-expr try/catch), and prose are all pinned with literal code.
- **Placeholders/TBD:** none.
- **Missing cases/edges:** covered — true/false/null branch; non-mismatch arms omit the fact; password/redacted short-circuits upstream (unchanged); read-throw → null; before-read stays capability-off (hot path); truncated-echo warning in prose. NOT re-testing UIA read paths headlessly (documented as live-smoke — box is headless RDP).
- **Requirement → task mapping:** complete (see Spec coverage).
- **Open item deferred to review gate (not this plan):** explicit-`canSetValue: null` vs JsonIgnore-when-null — user chose JsonIgnore-when-null; implemented as such in T2.
- **Verification of citations:** all file/line/symbol references (Interactor.cs:24-32, PerceptionManager.RunOnRefReadAsync:80, InputTools L96/L126-127/L142, VerifyResult From/RemedyProse/VerifyEchoMax, VerifyResultTests structure, csproj `<Version>0.7.4>`, iss AppVersion) grep-verified against the working tree this session.

## Release note
Cutting/pushing v0.7.5 (tag → release.yml → 4 assets) is a SEPARATE, user-gated step AFTER this plan lands and both review gates (agy merge-gate + ecc:csharp-reviewer) are GO — not part of task execution.
