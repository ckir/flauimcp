# Phase 10 #2 — First-class `selector` on interaction tools — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let interaction tools target a control by a stable `selector` ({automationId, name, controlType, scope, ignoreCase}) resolved atomically at action time — killing the `eN` re-snapshot churn — while preserving every safety gate (lease, deny-list, TOCTOU, count==1 fail-closed).

**Architecture:** Add an optional `selector` object *alongside* the existing `@ref` param on each state-changing interaction tool, enforcing **exactly-one-of {ref | selector}**. The selector reuses the existing window-rooted `FindQuery`/`FindQuerySpec` resolution, resolved on the **action STA** atomically with the TOCTOU re-verify. Exactly one match required (0 → not-present; >1 → `AMBIGUOUS_MATCH`). The minted `eN` is **descriptor-only** (no COM pin — action-STA COM objects must not enter the query-STA cache). A new shared `FindQuery.IgnoreCase` (find defaults false / selector defaults true) makes selector `name` matching case-insensitive, previewable via `desktop_find(ignoreCase:true)`.

**Tech Stack:** C#/.NET 10 (net10.0-windows10.0.19041.0), FlaUI/UIA3, xUnit (headless `Category!=Desktop`), ModelContextProtocol SDK.

**Base:** v0.9.0 (`0a247c2`). Spec: `docs/superpowers/specs/2026-07-04-flaui-mcp-phase10-consumer-ergonomics-design.md` (AGY-AFTER-converged, 6 rounds).

**Build/test gate (repo-as-is):** `dotnet build` → `5/0/0`; headless `dotnet test --filter "Category!=Desktop"`; Desktop `--filter "Category=Desktop"` (run by controller, not subagents; RDP-headless box can't fire synthetic input).

---

## ⚠️ PLAN STATUS: STARTED, NOT COMPLETE (2026-07-04 session 2)

Task 1 is fully authored below (verified against `FindQuery.cs`). **Tasks 2–9 are scaffolded** with their exact verified touchpoints + contracts, but their detailed TDD code-blocks are **pending code-verified authoring** — each is marked `▶ RESUME`. Do NOT execute a `▶ RESUME` task until its steps are authored against the real code (plan-discipline: no fabricated line numbers/signatures). **Resume point = expand Task 2 next.** The whole-plan AGY-AFTER adversarial review is DEFERRED until all tasks are authored (do not review a mid-draft plan).

---

## File Structure (verified touchpoints)

- **`src/FlaUI.Mcp.Core/Perception/FindQuery.cs`** — `FindQuery` record (add `IgnoreCase`); `FindQuerySpec.MatchesPostFilter` (case-fold). *(T1)*
- **`src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs`** — `FindAsync(WindowHandle, FindQuery, int max, string? scope)` builds native UIA conditions (add `PropertyConditionFlags.IgnoreCase` on `ByName` when `IgnoreCase`); `RunOnRefActionAsync<T>(WindowHandle, string ref, Func<AutomationElement,T>, int)` is the ref action path — a **sibling** `RunOnSelectorActionAsync<T>` resolves a selector on the same action STA. *(T2, T4)*
- **`src/FlaUI.Mcp.Core/Perception/RefRegistry.cs`** — reuse `ResolveDescriptor`/search primitives; mint selector `eN` **descriptor-only** via `Register(windowId, descriptor, cached: null)`. *(T4)*
- **`src/FlaUI.Mcp.Core/Perception/Selector.cs`** *(CREATE)* — the validated selector value type + `Validate()` (≥1 material field else `InvalidArguments`; carries `IgnoreCase` default true). *(T3)*
- **`src/FlaUI.Mcp.Server/Tools/InteractionTools.cs`** — `desktop_invoke/set_focus/set_value/toggle/expand/select/scroll/scroll_into_view`: add optional `selector` param, enforce exactly-one-of `{@ref|selector}`, return `resolvedElement`. Current param name is `@ref`; wrapper `Act(window, @ref, act, timeoutMs)` → `RunOnRefActionAsync`. *(T5)*
- **`src/FlaUI.Mcp.Server/Tools/InputTools.cs`** — `desktop_click/key/set_caret/select_text_range/paste_text` (ref-targeted): same wiring. *(T6)*
- **`src/FlaUI.Mcp.Server/Tools/ContentTools.cs`** — ref-targeted reads (`desktop_get_text` etc.): same wiring (reads stay lenient but selector still count==1). *(T7)*
- **`src/FlaUI.Mcp.Server/Tools/FindTools.cs`** — `desktop_find` gains `ignoreCase` param (default **false**), passed into `FindQuery`. *(T1 wire / T2)*
- **Audit** — resolved target's stable identity (RuntimeId + AutomationId + bounds; **never raw Name** if redacted) traced at resolution. *(T8)*
- **Tests:** `test/FlaUI.Mcp.Tests/Perception/FindQuerySpecTests.cs` (T1), new `SelectorTests.cs` (T3), resolver tests (T4); Desktop-category selector-action tests (controller-run).
- **Docs/version:** `README.md`, `.claude/skills/driving-flaui-mcp/SKILL.md`, `CHANGELOG.md`, `ROADMAP`, `*.csproj`/`flaui-mcp.iss` 0.9.0→0.10.0. *(T9)*

---

## Task 1: `FindQuery.IgnoreCase` + case-insensitive post-filter (pure/headless)

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/FindQuery.cs`
- Test: `test/FlaUI.Mcp.Tests/Perception/FindQuerySpecTests.cs`

**Contract:** `FindQuery` gains `bool IgnoreCase`. `FindQuerySpec.MatchesPostFilter` uses `OrdinalIgnoreCase` for both `contains` and `eq` when `IgnoreCase` is true, else `Ordinal` (today's behavior byte-for-byte). Password redaction unchanged (the name passed in is already `[REDACTED]`; case-folding a redacted literal is still a non-match against a real query). Native `eq` pushdown is Task 2 — this task covers the managed post-filter only.

- [ ] **Step 1: Write the failing tests** (append to `FindQuerySpecTests.cs`)

```csharp
[Fact]
public void MatchesPostFilter_contains_ignoreCase_matches_across_case()
{
    var spec = new FindQuerySpec(new FindQuery(
        AutomationId: null, Name: "memory", NameMatch: "contains",
        ControlType: null, EnabledOnly: false, IgnoreCase: true));
    Assert.True(spec.MatchesPostFilter("Clear all Memory", enabled: true));
}

[Fact]
public void MatchesPostFilter_eq_ignoreCase_matches_across_case()
{
    var spec = new FindQuerySpec(new FindQuery(
        null, "five", "eq", null, false, IgnoreCase: true));
    Assert.True(spec.MatchesPostFilter("Five", enabled: true));
}

[Fact]
public void MatchesPostFilter_ordinal_default_is_case_sensitive()
{
    var spec = new FindQuerySpec(new FindQuery(
        null, "memory", "contains", null, false, IgnoreCase: false));
    Assert.False(spec.MatchesPostFilter("Clear all Memory", enabled: true));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "Category!=Desktop&FullyQualifiedName~FindQuerySpecTests"`
Expected: FAIL to COMPILE — `FindQuery` has no `IgnoreCase` parameter (CS7036 / CS1739). This confirms the tests bind to the new field before it exists.

- [ ] **Step 3: Add the `IgnoreCase` field to `FindQuery`**

In `src/FlaUI.Mcp.Core/Perception/FindQuery.cs`, extend the record (append the new positional param **last** so existing positional call sites are the only ones needing the new arg — the tool boundary is the sole caller):

```csharp
/// <summary>Flattened desktop_find query (validated/carried from the tool boundary).
/// NameMatch is "eq" (ordinal exact) or "contains" (ordinal substring); ignored when Name is null.
/// IgnoreCase folds Name matching (both eq and contains) with OrdinalIgnoreCase — culture-invariant,
/// deterministic across machines. desktop_find defaults it false (Ordinal, back-compat); the Phase-10
/// selector defaults it true. Native eq pushdown honours it via PropertyConditionFlags.IgnoreCase (see
/// PerceptionManager); this post-filter honours it for contains.</summary>
public sealed record FindQuery(
    string? AutomationId,
    string? Name,
    string NameMatch,
    string? ControlType,
    bool EnabledOnly,
    bool IgnoreCase = false);
```

- [ ] **Step 4: Case-fold the post-filter**

In `FindQuerySpec.MatchesPostFilter`, replace the fixed-`Ordinal` comparison with a comparison selected by `_q.IgnoreCase`:

```csharp
        if (_q.Name is { } wanted)
        {
            var n = name ?? string.Empty; // null == unnamed container; keep the predicate total
            var cmp = _q.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            bool ok = string.Equals(_q.NameMatch, "contains", StringComparison.Ordinal)
                ? n.Contains(wanted, cmp)
                : string.Equals(n, wanted, cmp);
            if (!ok) return false;
        }
```

(Note: the `NameMatch == "contains"` *mode* check stays `Ordinal` — it compares a fixed protocol literal, not user content.)

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "Category!=Desktop&FullyQualifiedName~FindQuerySpecTests"`
Expected: PASS (all existing FindQuerySpecTests + the 3 new ones). Then full headless: `dotnet test --filter "Category!=Desktop"` → all green (the new optional param defaults false, so every existing `new FindQuery(...)` call site compiles unchanged).

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/FindQuery.cs test/FlaUI.Mcp.Tests/Perception/FindQuerySpecTests.cs
git commit -m "feat(find): FindQuery.IgnoreCase — case-insensitive name post-filter (Phase 10 #2 T1)"
```

---

## Task 2: Native `ByName` IgnoreCase pushdown + `desktop_find(ignoreCase)` wire  ▶ RESUME

**Contract:** In `PerceptionManager.FindAsync`'s native-condition build, when `FindQuery.IgnoreCase` is true, construct the Name `PropertyCondition` with `PropertyConditionFlags.IgnoreCase` (keeps the `eq` case-insensitive match native, not a managed post-filter — honours the R3 native-pushdown requirement). `FindTools.DesktopFind` gains an `ignoreCase` param (default **false**) threaded into `FindQuery`. **Author against:** the real condition-building lines in `PerceptionManager.FindAsync` (read them first — verify how `ByName`/`ConditionFactory` is currently constructed) and `FindTools.cs:23-35`. Add a Desktop-category test that `desktop_find(name:"memory", ignoreCase:true)` matches capitalized "Memory" controls, and a headless test if the condition build is unit-reachable.

## Task 3: `Selector` value type + validation (pure/headless)  ▶ RESUME

**Contract:** CREATE `src/FlaUI.Mcp.Core/Perception/Selector.cs` — a validated record `{ AutomationId?, Name?, NameMatch="eq", ControlType?, Scope?(eN), IgnoreCase=true }`. `Validate()`: require **≥1 material field** among {AutomationId, Name, ControlType} else throw `ToolException(InvalidArguments, …)` **before any UIA**; unknown ControlType → InvalidArguments (reuse `FindQuerySpec.TryParseControlType`); produce a `FindQuery` (mapping `IgnoreCase`, default true). Exactly-one-of `{ref | selector}` is enforced at the tool layer (T5-7), not here. **Author against:** `FindQuery.cs` (mapping) + `ToolErrorCode` (InvalidArguments exists). Full TDD: tests for each material-field-present case (ok), all-absent (InvalidArguments), unknown controlType (InvalidArguments), IgnoreCase default true.

## Task 4: Selector resolver on the action STA (descriptor-only, count==1)  ▶ RESUME

**Contract:** Add `PerceptionManager.RunOnSelectorActionAsync<T>(WindowHandle, Selector, Func<AutomationElement,T>, int timeoutMs)` — resolves the selector **on the action STA**, atomic with the existing TOCTOU re-verify, mirroring `RunOnRefActionAsync<T>` (verified generic `Task<T>`). Resolution: window-rooted search (+ optional `scope` eN resolved under existing ref rules first — a stale scope fails with its own `RefNotFound`/`RefStaleUnresolvable`, distinct from the selector's 0-match); apply `FindQuerySpec`; **exactly one** live match required. 0 → not-present error with actionable recovery; >1 → `AMBIGUOUS_MATCH` whose recovery names the `ignoreCase:false` escape + add controlType/scope. Bound the walk (time/node cap; reuse `FLAUI_MCP_REF_MAXSCOPES`-style guard) so a broad-but-material selector can't hang the STA. Mint the resolved `eN` **descriptor-only** (`Register(windowId, descriptor, cached: null)` — no COM pin across STAs). Emit the redaction-safe audit trace (→ T8). Return the minted `eN` for `resolvedElement`. **Author against:** `RefRegistry.cs` (`ResolveDescriptor`/`GatherScopes`/`Register`), `PerceptionManager.RunOnRefActionAsync`, `WindowManager.RunOnWindowActionAsync<T>`. Headless tests with a fake/UIA-light harness where possible; Desktop-category end-to-end (controller-run).

## Task 5: Wire `selector` into `InteractionTools` (exactly-one-of, resolvedElement)  ▶ RESUME

**Contract:** Each of `desktop_invoke/set_focus/set_value/toggle/expand/select/scroll/scroll_into_view` gains an optional `selector` param. Enforce **exactly-one-of {@ref | selector}** (both or neither → InvalidArguments) in the shared `Act` helper; route ref→`RunOnRefActionAsync`, selector→`RunOnSelectorActionAsync`; on selector success add `resolvedElement:"eN"` to the response. GuardWrite/lease/deny-list/TOCTOU unchanged (apply after resolution). **Author against:** `InteractionTools.cs` (the `Act(string window, string @ref, …)` helper + each tool signature — param name is `@ref`, verified). Desktop-category tests (controller-run): invoke by `{automationId:"num5Button"}` across a re-snapshot survives where a held `eN` dies.

## Task 6: Wire `selector` into `InputTools` (ref-targeted synthetic input)  ▶ RESUME

**Contract:** `desktop_click/key/set_caret/select_text_range/paste_text` where ref-targeted: same exactly-one-of wiring + `resolvedElement`. Lease gates (InputGuard) unchanged, applied after resolution. **Author against:** `InputTools.cs` (find the ref-taking signatures; `RunOnWindowActionAsync` used at InputTools.cs:308). Note lease-exempt reads (`set_caret`/`select_text_range`) still resolve the selector but do not need a lease.

## Task 7: Wire `selector` into ref-targeted `ContentTools` reads  ▶ RESUME

**Contract:** `desktop_get_text` (+ any ref-targeted content read) gains `selector`; reads use lenient ref resolution but the selector still requires count==1 (a read selecting >1 is `AMBIGUOUS_MATCH`, consistent with today's lenient-but-fail-closed-on-duplicate reads). **Author against:** `ContentTools.cs:42` (`RunOnRefActionAsync`).

## Task 8: Redaction-safe audit trace of the resolved target  ▶ RESUME

**Contract:** At selector resolution (T4), emit a durable audit record of the resolved element's **stable identity** — RuntimeId + AutomationId + bounding rect — **never the raw Name** for a password/redacted element (consistent with `[REDACTED]`/INV-5). If ref-based actions don't already emit resolved-identity audit, close the gap for both paths. **Author against:** the existing audit/log sink (locate it — Phase-4 InputGuard budget/audit) and INV-5 redaction rules.

## Task 9: Docs + version bump  ▶ RESUME

**Contract:** README "targeting" section (`selector` alongside `ref`, exactly-one-of, `ignoreCase`, resolvedElement durability caveat, honest automationId-coverage limitation); SKILL.md decision guidance; CHANGELOG `[0.10.0]`; ROADMAP Phase 10; version `0.9.0→0.10.0` in csproj + `flaui-mcp.iss`. Cross-check tool descriptions against shipped signatures.

---

## Self-Review (run after Tasks 2–9 are authored, before AGY-AFTER)

1. **Spec coverage:** map every spec §#2 requirement (selector shape, exactly-one-of, count==1 0/>1, descriptor-only mandate, action-STA-atomic-TOCTOU, resolvedElement + durability, under-constrained fast-fail, ignoreCase shared flag + preview/escape, scope-fault taxonomy, bounded walk, native pushdown, redaction-safe audit, honest-limitation docs) to a task. List gaps.
2. **Placeholder scan:** no TBD/"handle edge cases"/uncoded steps.
3. **Type consistency:** `RunOnSelectorActionAsync`, `Selector`, `FindQuery.IgnoreCase`, `resolvedElement` named identically across tasks.
