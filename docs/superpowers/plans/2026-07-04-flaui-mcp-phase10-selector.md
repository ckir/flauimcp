# Phase 10 #2 — First-class `selector` on interaction tools — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let interaction tools target a control by a stable `selector` ({automationId, name, controlType, scope, ignoreCase}) resolved atomically at action time — killing the `eN` re-snapshot churn — while preserving every safety gate (lease, deny-list, TOCTOU, count==1 fail-closed).

**Architecture:** Add an optional `selector` object *alongside* the existing `@ref` param on each state-changing interaction tool, enforcing **exactly-one-of {ref | selector}**. The selector reuses the existing window-rooted `FindQuery`/`FindQuerySpec` resolution, resolved on the **action STA** atomically with the TOCTOU re-verify. Exactly one match required (0 → not-present; >1 → `AMBIGUOUS_MATCH`). The minted `eN` is **descriptor-only** (no COM pin — action-STA COM objects must not enter the query-STA cache). A new shared `FindQuery.IgnoreCase` (find defaults false / selector defaults true) makes selector `name` matching case-insensitive, previewable via `desktop_find(ignoreCase:true)`.

**Tech Stack:** C#/.NET 10 (net10.0-windows10.0.19041.0), FlaUI/UIA3, xUnit (headless `Category!=Desktop`), ModelContextProtocol SDK.

**Base:** v0.9.0 (`0a247c2`). Spec: `docs/superpowers/specs/2026-07-04-flaui-mcp-phase10-consumer-ergonomics-design.md` (AGY-AFTER-converged, 6 rounds).

**Build/test gate (repo-as-is):** `dotnet build` → `5/0/0`; headless `dotnet test --filter "Category!=Desktop"`; Desktop `--filter "Category=Desktop"` (run by controller, not subagents; RDP-headless box can't fire synthetic input).

---

## Recon corrections folded (2026-07-04 session 2)

Code reconnaissance corrected four assumptions — all reflected in the tasks below:
- **0-match uses the pre-existing `ToolErrorCode.SelectorNoMatch`** (not `RefStaleUnresolvable`). The spec's #2 "0 matches → REF_STALE_UNRESOLVABLE-class" wording should be synced to `SelectorNoMatch`.
- **The action path resolves via `ConditionFactory` + `FindQuerySpec`** (mirroring `FindAsync`'s `Build` local fn, `PerceptionManager.cs:255-263`), **bypassing** `RefRegistry`'s descriptor resolution — then mints a **descriptor-only** `eN` for `resolvedElement` the same way `FindAsync` registers a match (`PerceptionManager.cs:~305-313`, with `cached: null`).
- **Three sibling wrappers** exist and must each get a selector twin: `RunOnRefActionAsync` (InteractionTools), `RunOnRefForInputAsync` (InputTools — derives the `ActionTarget` for audit/lease), `RunOnRefReadAsync` (ContentTools reads, `RefResolveMode.Lenient`). A shared on-STA `ResolveSelectorOnSta` core feeds all three. There is **no** cheap off-STA existence pre-check for a selector (unlike ref's `Lookup`), so 0-match is detected on-STA.
- **The audit sink is window-identity-shaped** (`InputAudit.Record(nint window,int pid,string? process,string action,int len)` via `ActionTarget{Root,Pid,ProcessName,WindowClass}`) — recording the resolved element's RuntimeId/bounds is a **wire-shape change** (Task 8, candidate to defer).

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

## Task 2: `ignoreCase` on `desktop_find` + name post-filter authority (headless + Desktop)

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` (the `Build` local fn inside `FindAsync`, ~`:255-263`)
- Modify: `src/FlaUI.Mcp.Server/Tools/FindTools.cs` (`DesktopFind` signature `:23-35`)
- Test: `test/FlaUI.Mcp.Tests/Perception/FindQuerySpecTests.cs` (headless), `test/FlaUI.Mcp.Tests/Desktop/DesktopFindTests.cs` (Desktop)

**Contract:** `desktop_find` gains `bool ignoreCase = false` (Ordinal default preserved — back-compat), threaded into `FindQuery`. Design decision (avoids an uncertain FlaUI `PropertyConditionFlags.IgnoreCase` API): when `IgnoreCase` is true, **do not push the Name into the native UIA condition** — let Task 1's managed `OrdinalIgnoreCase` post-filter be authoritative for the name match (same shape as how `"contains"` already post-filters today). `automationId` and `controlType` still push down natively, so the search stays narrowed; only the case-insensitive name check runs managed. (A future optimization could use a native case-insensitive `PropertyCondition` if profiling shows it matters — out of scope.)

- [ ] **Step 1: Add the `ignoreCase` param to `desktop_find`**

In `FindTools.cs`, add the param (after `scope`) and thread it into `FindQuery`:

```csharp
        [Description("Optional live ref to search within (its subtree only).")] string? scope = null,
        [Description("Case-insensitive name match (eq and contains). Default false (exact/ordinal). The Phase-10 selector defaults this true; set it here to preview selector matching.")] bool ignoreCase = false)
        => ToolResponse.Guard(async () =>
        {
            var query = new FindQuery(automationId, name, nameMatch, controlType, enabledOnly, ignoreCase);
```

- [ ] **Step 2: Make `FindAsync`'s native `Build` skip Name when `IgnoreCase`**

In `PerceptionManager.FindAsync`'s `Build` local fn, change the native-name clause so Name is pushed to UIA **only** for a case-sensitive `eq` (else the post-filter handles it):

```csharp
            // native Name pushdown ONLY for case-sensitive eq; ignoreCase (and contains) → post-filter authority
            if (string.Equals(query.NameMatch, "eq", StringComparison.Ordinal)
                && !query.IgnoreCase && !string.IsNullOrEmpty(query.Name))
                cond = cond is null ? cf.ByName(query.Name) : cond.And(cf.ByName(query.Name));
```

(Replace the existing unconditional `eq`→`ByName` clause. Verify the exact local-variable name (`cond`/`c`) and the `hasNative` flag interaction at implement time — the rule is: an `ignoreCase` name must NOT contribute to `hasNative`, so the post-filter runs.)

- [ ] **Step 3: Headless test — post-filter is authoritative under ignoreCase**

Already covered by Task 1's `FindQuerySpecTests` (the post-filter is where correctness lives). Add one binding test that `new FindQuery(null,"memory","eq",null,false, IgnoreCase:true)` post-filter matches `"Memory"`. Run: `dotnet test --filter "Category!=Desktop&FullyQualifiedName~FindQuerySpecTests"` → PASS.

- [ ] **Step 4: Desktop test (controller-run) — end-to-end ignoreCase**

Append to `DesktopFindTests.cs` a `[Trait("Category","Desktop")]` test: launch Calculator, `FindAsync(handle, new FindQuery(null,"memory","contains",null,false,IgnoreCase:true), 20, null)` returns the capital-**M** "Memory …" controls **and** "Clear all **m**emory"/"Open **m**emory flyout" (which Ordinal misses). Assert `TotalMatches >= 7`.

- [ ] **Step 5: Build + headless green, then commit**

Run: `dotnet build` → `5/0/0`; `dotnet test --filter "Category!=Desktop"` → all green.
```bash
git add src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs src/FlaUI.Mcp.Server/Tools/FindTools.cs test/FlaUI.Mcp.Tests/Perception/FindQuerySpecTests.cs test/FlaUI.Mcp.Tests/Desktop/DesktopFindTests.cs
git commit -m "feat(find): desktop_find ignoreCase — post-filter-authoritative case-insensitive name (Phase 10 #2 T2)"
```

---

## Task 3: `Selector` value type + validation (pure/headless)

**Files:**
- Create: `src/FlaUI.Mcp.Core/Perception/Selector.cs`
- Test: `test/FlaUI.Mcp.Tests/Perception/SelectorTests.cs`

**Contract:** A validated selector value type carrying the selector fields, `IgnoreCase` defaulting **true** (the ergonomic call). `Validate()` requires ≥1 material field among {AutomationId, Name, ControlType} → else `InvalidArguments` **before any UIA**; an unparseable ControlType → `InvalidArguments`. `ToFindQuery()` maps to `FindQuery` (EnabledOnly false; carries `IgnoreCase`). Exactly-one-of `{ref | selector}` is enforced at the tool layer (T5–T7), not here.

- [ ] **Step 1: Write the failing tests** (`SelectorTests.cs`)

```csharp
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using Xunit;

public class SelectorTests
{
    [Fact]
    public void Validate_accepts_any_single_material_field()
    {
        new Selector(AutomationId: "num5Button").Validate();   // no throw
        new Selector(Name: "Five").Validate();
        new Selector(ControlType: "Button").Validate();
    }

    [Fact]
    public void Validate_rejects_no_material_field()
    {
        var ex = Assert.Throws<ToolException>(() => new Selector(Scope: "e12").Validate());
        Assert.Equal(ToolErrorCode.InvalidArguments, ex.Code);
    }

    [Fact]
    public void Validate_rejects_unknown_controlType()
    {
        var ex = Assert.Throws<ToolException>(() => new Selector(ControlType: "Widget").Validate());
        Assert.Equal(ToolErrorCode.InvalidArguments, ex.Code);
    }

    [Fact]
    public void ToFindQuery_defaults_ignoreCase_true()
    {
        Assert.True(new Selector(Name: "Five").ToFindQuery().IgnoreCase);
        Assert.False(new Selector(Name: "Five", IgnoreCase: false).ToFindQuery().IgnoreCase);
    }
}
```

- [ ] **Step 2: Run to verify they fail** — `dotnet test --filter "Category!=Desktop&FullyQualifiedName~SelectorTests"` → FAIL to compile (`Selector` undefined).

- [ ] **Step 3: Implement `Selector`**

```csharp
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>A stable, identity-based target for an interaction tool, resolved fresh at action time
/// (Phase 10 #2). Exactly-one-of {ref | selector} is enforced at the tool boundary. IgnoreCase defaults
/// TRUE (ergonomic); set false to disambiguate a genuine Submit/submit collision. Scope is an eN ref that
/// narrows the search to that element's subtree (resolved under existing ref rules first).</summary>
public sealed record Selector(
    string? AutomationId = null,
    string? Name = null,
    string NameMatch = "eq",
    string? ControlType = null,
    string? Scope = null,
    bool IgnoreCase = true)
{
    /// <summary>Fail-closed at the tool layer BEFORE any UIA walk: a selector with no material field
    /// ({automationId,name,controlType} all absent) is rejected rather than translated into a whole-tree
    /// walk. An unparseable ControlType is rejected here too.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AutomationId)
            && string.IsNullOrWhiteSpace(Name)
            && string.IsNullOrWhiteSpace(ControlType))
            throw new ToolException(ToolErrorCode.InvalidArguments,
                "selector needs at least one of automationId / name / controlType.",
                "add a material field (automationId is the most stable), or use a ref from desktop_snapshot");

        if (!string.IsNullOrWhiteSpace(ControlType)
            && !FindQuerySpec.TryParseControlType(ControlType, out _))
            throw new ToolException(ToolErrorCode.InvalidArguments,
                $"selector controlType '{ControlType}' is not a known UIA ControlType.",
                "use a UIA ControlType name, e.g. Button, Edit, ListItem");
    }

    /// <summary>Map to the shared FindQuery (EnabledOnly is not a selector dimension — always false).</summary>
    public FindQuery ToFindQuery() => new(AutomationId, Name, NameMatch, ControlType, EnabledOnly: false, IgnoreCase);
}
```

- [ ] **Step 4: Run to verify they pass** — same filter → PASS; then `dotnet test --filter "Category!=Desktop"` → all green.

- [ ] **Step 5: Commit**
```bash
git add src/FlaUI.Mcp.Core/Perception/Selector.cs test/FlaUI.Mcp.Tests/Perception/SelectorTests.cs
git commit -m "feat(selector): Selector value type + fail-closed validation (Phase 10 #2 T3)"
```

---

## Task 4: Selector resolver on the action STA (the crux — count==1, descriptor-only mint)

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` (add `ResolveSelectorOnSta` + `RunOnSelectorActionAsync<T>` / `RunOnSelectorForInputAsync<T>` / `RunOnSelectorReadAsync<T>`)
- Test: Desktop-category `test/FlaUI.Mcp.Tests/Desktop/DesktopSelectorTests.cs` (controller-run; the resolve is on-STA so it needs real UIA)

**Contract:** A shared on-STA core resolves a `Selector` against a root (window, or its `scope` subtree) to **exactly one** live element, mirroring `FindAsync`'s `Build`+`FindQuerySpec` path — NOT `RefRegistry` descriptor resolution. `0` → `SelectorNoMatch`; `>1` → `AmbiguousMatch` (recovery names the `ignoreCase:false` escape + add controlType/scope); `1` → return it. Three thin wrappers mirror the existing ref trio. The action/input wrappers offscreen-guard (`ElementNotActionable`) exactly like `RunOnRefActionAsync`; the read wrapper does not. Each wrapper mints a **descriptor-only** `eN` (`cached: null`) via the same descriptor construction `FindAsync` uses, and returns it so the tool can surface `resolvedElement`. The action-STA `timeoutMs` (already enforced by `RunOnWindowActionAsync`) bounds the walk — no separate cap needed for the first cut (note it; revisit if a pathological tree is observed).

- [ ] **Step 1: Implement the shared resolve core** (add to `PerceptionManager`)

```csharp
    /// <summary>Resolve a selector to EXACTLY ONE live element under <paramref name="root"/>, on the
    /// calling (action or query) STA. Mirrors FindAsync: native condition narrows, FindQuerySpec post-filters
    /// (name-contains / ignoreCase / enabledOnly). 0 -> SelectorNoMatch; >1 -> AmbiguousMatch. Never a
    /// FindFirst silent-pick. Also builds + registers a descriptor-only ref for resolvedElement.</summary>
    private (AutomationElement El, string Ref) ResolveSelectorOnSta(
        string windowId, AutomationElement root, Selector sel, ConditionFactory cf)
    {
        var q = sel.ToFindQuery();
        var spec = new FindQuerySpec(q);
        // native narrow (automationId/controlType, and case-sensitive-eq name — same rule as FindAsync.Build)
        ConditionBase? cond = null;
        if (!string.IsNullOrEmpty(q.AutomationId)) cond = cf.ByAutomationId(q.AutomationId);
        if (FindQuerySpec.TryParseControlType(q.ControlType, out var ct))
            cond = cond is null ? cf.ByControlType(ct) : cond.And(cf.ByControlType(ct));
        if (string.Equals(q.NameMatch, "eq", System.StringComparison.Ordinal)
            && !q.IgnoreCase && !string.IsNullOrEmpty(q.Name))
            cond = cond is null ? cf.ByName(q.Name) : cond.And(cf.ByName(q.Name));

        var candidates = cond is null ? root.FindAllDescendants() : root.FindAllDescendants(_ => cond);
        var hits = new System.Collections.Generic.List<AutomationElement>();
        foreach (var el in candidates)
        {
            // read the ALREADY-REDACTED name exactly as FindAsync does (INV-5) before post-filter
            var name = SafeRedactedName(el);            // reuse FindAsync's name/redaction helper
            var enabled = el.Properties.IsEnabled.ValueOrDefault;
            if (spec.MatchesPostFilter(name, enabled)) hits.Add(el);
        }

        if (hits.Count == 0)
            throw new ToolException(ToolErrorCode.SelectorNoMatch,
                "selector matched no element in this window right now.",
                "the target may not be present yet — reveal it (act / desktop_wait_for) then retry, or desktop_snapshot to see current state");
        if (hits.Count > 1)
            throw new ToolException(ToolErrorCode.AmbiguousMatch,
                $"selector matched {hits.Count} elements; cannot safely pick one.",
                "refine: add controlType/automationId, add a scope, set ignoreCase:false for an exact-case name, or desktop_snapshot and target a unique eN");

        var descriptor = DescriptorFor(hits[0]);        // reuse FindAsync's descriptor construction (~PerceptionManager.cs:305-313)
        var @ref = _refs.Register(windowId, descriptor, cached: null); // descriptor-only: action-STA COM must not enter the query-STA cache
        return (hits[0], @ref);
    }
```

> **Implement-time verification (plan-discipline):** `SafeRedactedName` and `DescriptorFor` name the two helpers `FindAsync` already uses (name-read-with-INV-5-redaction at `PerceptionManager.cs:285-292`, and the descriptor construction at `~:305-313`). Confirm their exact names/signatures when implementing and reuse them verbatim — do NOT reimplement redaction or descriptor logic. `ConditionBase`/`ConditionFactory` are the FlaUI types already used by `FindAsync.Build`.

- [ ] **Step 2: Add the three wrappers** (mirror the verified ref trio `RunOnRefActionAsync`/`RunOnRefForInputAsync`/`RunOnRefReadAsync`)

```csharp
    /// <summary>Selector twin of RunOnRefActionAsync: resolve on the action STA, offscreen-guard, act.
    /// Returns (action result, resolved eN) so the tool can report resolvedElement.</summary>
    public Task<(T Value, string ResolvedRef)> RunOnSelectorActionAsync<T>(
        WindowHandle handle, Selector sel, Func<AutomationElement, T> func, int timeoutMs)
        => _windows.RunOnWindowActionAsync(handle, (win, desktop) =>
        {
            var root = string.IsNullOrEmpty(sel.Scope)
                ? win
                : _refs.Resolve(handle.Id, sel.Scope!, PopupFinder.SearchRoots(win, desktop)); // scope's own RefNotFound/RefStaleUnresolvable
            var (el, @ref) = ResolveSelectorOnSta(handle.Id, root, sel, win.Automation.ConditionFactory);
            if (el.Properties.IsOffscreen.ValueOrDefault)
                throw new ToolException(ToolErrorCode.ElementNotActionable,
                    "Element is off-screen; cannot act on it reliably.", "desktop_scroll_into_view then retry");
            return (func(el), @ref);
        }, timeoutMs);
```

Add `RunOnSelectorForInputAsync<T>` (mirror `RunOnRefForInputAsync` — same body but pass the resolved **window** element into the callback so the tool can build the `ActionTarget`) and `RunOnSelectorReadAsync<T>` (mirror `RunOnRefReadAsync` — no offscreen guard; still count==1). Confirm the exact callback shapes of the two ref siblings (`PerceptionManager.cs:73-102`) and mirror them.

- [ ] **Step 3: Desktop test (controller-run) — the friction is gone**

`DesktopSelectorTests.cs`, `[Trait("Category","Desktop")]`: launch Calculator; `RunOnSelectorActionAsync(handle, new Selector(AutomationId:"num5Button"), el => { Interactor.Invoke(el); return true; }, 4000)` returns `(true, "eNN")`; take a fresh `SnapshotAsync` (renumbers refs); call the SAME selector again → still resolves + invokes (proving it survives the churn a held `eN` would not). Ambiguity: `new Selector(ControlType:"Button")` → `AmbiguousMatch`. Zero: `new Selector(AutomationId:"nope")` → `SelectorNoMatch`.

- [ ] **Step 4: Build green; commit**

Run: `dotnet build` → `5/0/0`; `dotnet test --filter "Category!=Desktop"` green (no headless regressions). Controller runs the Desktop test.
```bash
git add src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs test/FlaUI.Mcp.Tests/Desktop/DesktopSelectorTests.cs
git commit -m "feat(selector): action-STA resolver — count==1 fail-closed, descriptor-only mint (Phase 10 #2 T4)"
```

---

## Task 5: Wire `selector` into `InteractionTools` (exactly-one-of, resolvedElement)

**Files:** Modify `src/FlaUI.Mcp.Server/Tools/InteractionTools.cs`; Desktop test `test/FlaUI.Mcp.Tests/Desktop/DesktopSelectorActionTests.cs`

**Contract:** Each of `desktop_invoke/set_focus/set_value/toggle/expand/select/scroll/scroll_into_view` gains an optional `Selector? selector = null` param (a nested object `{automationId?,name?,nameMatch?,controlType?,scope?,ignoreCase?}`). A shared helper enforces **exactly-one-of {@ref | selector}** and routes to the ref or selector wrapper; on selector success the response gains `resolvedElement:"eN"`. All GuardWrite/lease/deny-list/TOCTOU gates are unchanged (they run inside the wrappers, after resolution).

- [ ] **Step 1: Refactor the `Act` helper to accept either target** (verified current helper: `Act(string window, string @ref, Action<AutomationElement> act, int timeoutMs)` → `RunOnRefActionAsync`)

```csharp
    private Task<string> Act(string window, string? @ref, Selector? selector,
        Action<FlaUI.Core.AutomationElements.AutomationElement> act, int timeoutMs)
        => ToolResponse.GuardWrite(_options, async () =>
        {
            RequireExactlyOne(@ref, selector);   // both/neither -> InvalidArguments
            if (selector is { } sel)
            {
                sel.Validate();
                var (_, resolved) = await _perception.RunOnSelectorActionAsync(
                    new WindowHandle(window), sel, el => { act(el); return true; }, timeoutMs);
                return ToolResponse.Ok(new { ok = true, pathUsed = "pattern", resolvedElement = resolved });
            }
            await _perception.RunOnRefActionAsync(new WindowHandle(window), @ref!,
                el => { act(el); return true; }, timeoutMs);
            return ToolResponse.Ok(new { ok = true, pathUsed = "pattern" });
        });

    private static void RequireExactlyOne(string? @ref, Selector? selector)
    {
        bool hasRef = !string.IsNullOrEmpty(@ref), hasSel = selector is not null;
        if (hasRef == hasSel)
            throw new ToolException(ToolErrorCode.InvalidArguments,
                "provide exactly one of ref or selector.",
                hasRef ? "drop one — ref and selector are mutually exclusive" : "pass a ref (from a snapshot) or a selector {automationId|name|controlType}");
    }
```

- [ ] **Step 2: Update each tool signature** — make `@ref` nullable and add `selector`; forward both to `Act`. Example (`DesktopInvoke`):

```csharp
    public Task<string> DesktopInvoke(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Element ref from a snapshot, e.g. e23. Exactly one of ref | selector.")] string? @ref = null,
        [Description("Stable target {automationId?,name?,nameMatch?,controlType?,scope?,ignoreCase?} resolved at action time. Exactly one of ref | selector. Returns resolvedElement.")] Selector? selector = null,
        [Description("Block timeout in ms (default 4000).")] int timeoutMs = DefaultActionTimeoutMs)
        => Act(window, @ref, selector, Interactor.Invoke, timeoutMs);
```

Repeat for `set_focus/set_value/toggle/expand/select/scroll/scroll_into_view` — same two-param change, same forward. (`set_value`/`scroll` keep their extra params.)

- [ ] **Step 3: Desktop test (controller-run)** — invoke `{automationId:"num5Button"}`, assert `resolvedElement` present + display changes; assert `desktop_invoke` with BOTH ref and selector → `InvalidArguments`; with NEITHER → `InvalidArguments`.

- [ ] **Step 4: Build green; commit**
```bash
git add src/FlaUI.Mcp.Server/Tools/InteractionTools.cs test/FlaUI.Mcp.Tests/Desktop/DesktopSelectorActionTests.cs
git commit -m "feat(selector): InteractionTools selector-or-ref (exactly-one-of, resolvedElement) (Phase 10 #2 T5)"
```

---

## Task 6: Wire `selector` into `InputTools` (the six ref-taking tools)

**Files:** Modify `src/FlaUI.Mcp.Server/Tools/InputTools.cs`

**Contract:** Apply the **Task 5 exactly-one-of pattern** to the six verified ref-taking tools, using the **input** wrapper (`RunOnSelectorForInputAsync`) so the `ActionTarget` is derived from the resolved element (lease/deny-list/audit unchanged, after resolution). Signatures (verified):
- `DesktopSetCaret(string window, string @ref, int offset, int timeoutMs=4000)` — lease-EXEMPT (`AuthorizeTextMutation`); still resolves selector.
- `DesktopSelectTextRange(string window, string @ref, int start, int length, int timeoutMs=4000)` — lease-exempt.
- `DesktopType(string window, string @ref, string text, int timeoutMs=4000, int interKeyDelayMs=15, bool verify=true)` — lease-required.
- `DesktopPasteText(string window, string @ref, string text, int timeoutMs=4000, bool verify=true, bool forceOverwriteClipboard=false)` — lease-required.
- `DesktopClick(string window, string @ref, string button="left", int count=1, int timeoutMs=4000)` — lease-required.
- `DesktopKey(string chord, string? @ref=null, string? window=null, int timeoutMs=4000)` — **at-most-one-of {@ref | selector}** (neither ⇒ existing foreground path); add `Selector? selector=null` + require `window` when `selector` is given.

For each: make `@ref` nullable (already is on `DesktopKey`), add `Selector? selector=null`, `RequireExactlyOne` (or at-most-one for `DesktopKey`), route to the selector-for-input wrapper, add `resolvedElement` on success. `DesktopClickAt`/`DesktopDrag` are coordinate-only — **not** touched.

- [ ] **Step 1–N:** for each tool, apply the T5 pattern (nullable `@ref` + `Selector? selector` + exactly-one-of + route + `resolvedElement`); reuse a shared `InputTools`-local `RequireExactlyOne`. Build `5/0/0`; headless green. **Step commit:** `feat(selector): InputTools selector-or-ref on the six ref tools (Phase 10 #2 T6)`. Desktop verification (controller): `desktop_type selector:{automationId:...}` lands text; `desktop_set_caret` selector works while input is LOCKED (lease-exempt path preserved).

---

## Task 7: Wire `selector` into ref-taking `ContentTools`

**Files:** Modify `src/FlaUI.Mcp.Server/Tools/ContentTools.cs`

**Contract:** `DesktopGetText` + `DesktopGetGridCell` (ReadOnly, via `RunOnSelectorReadAsync`) and `DesktopGridSelect` (Destructive, via `RunOnSelectorActionAsync`) gain `Selector? selector=null` with the same exactly-one-of gate + `resolvedElement`. Reads use the read wrapper (Lenient, no offscreen guard) but the selector still requires **count==1** (a read whose selector matches >1 fails `AmbiguousMatch`, consistent with today's lenient-but-duplicate-fail-closed reads). Verified signatures: `DesktopGetGridCell(string window, string @ref, int row, int col, int timeoutMs=4000)`; `DesktopGridSelect(string window, string @ref, int row, int col, int timeoutMs=4000)`; `DesktopGetText(string window, string @ref, bool selectionOnly=false, int maxLength=10000, int timeoutMs=4000)`.

- [ ] **Steps:** apply the T5 pattern to the three tools (read tools use `RunOnSelectorReadAsync`; grid-select uses `RunOnSelectorActionAsync`). Build `5/0/0`; headless green. **Commit:** `feat(selector): ContentTools selector-or-ref on ref reads (Phase 10 #2 T7)`.

---

## Task 8: (OPTIONAL / DEFER-CANDIDATE) element-identity audit trace

**Files:** `src/FlaUI.Mcp.Core/Interaction/InputAudit.cs`, `InputGuard.cs`, `InputTargeting.cs`

**Contract & scoping note:** The R3 audit finding (record the resolved target's stable identity for forensics) is a **wire-shape change**, not additive — verified: `InputAudit.Record(nint window,int pid,string? process,string action,int len)` and `ActionTarget{Root,Pid,ProcessName,WindowClass}` capture only **window** identity, no element RuntimeId/AutomationId/bounds. To close it: extend `ActionTarget` (and `Record`/`Authorize`/`AuthorizeTextMutation`) with an optional resolved-element identity — **RuntimeId + AutomationId + bounds, NEVER the raw Name** for a redacted/password element (INV-5). Because it touches the shipped audit shape and applies equally to the ref path, **recommend DEFERRING to a Phase-10.1 fast-follow** rather than bundling into #2's first cut (the selector feature is fully functional without it; the existing window-level audit still fires). If kept: add the field as append-only/optional so the log line stays back-compatible; TDD against `InputAudit` with a `StringWriter` sink asserting the redacted shape.

- [ ] **Decision step:** confirm defer-vs-include with the user before implementing (it's the only task that mutates a shipped wire shape).

---

## Task 9: Docs + version bump

**Files:** `README.md`, `.claude/skills/driving-flaui-mcp/SKILL.md`, `CHANGELOG.md`, `ROADMAP*`, `src/**/*.csproj`, `installer/flaui-mcp.iss`

- [ ] **Step 1:** README "targeting" — document `selector` alongside `ref` (exactly-one-of), the fields, `ignoreCase` (default true on selector, previewable via `desktop_find(ignoreCase:true)`, `ignoreCase:false` escape), `resolvedElement` + its **durability caveat** (dies on the next re-walk — reuse for immediate follow-ups only), and the **honest limitation** (payoff scales with `automationId` coverage; no-id + non-unique-name degrades to `AmbiguousMatch`, fall back to snapshot). Sync the spec's "0 matches → REF_STALE_UNRESOLVABLE-class" to **`SelectorNoMatch`**.
- [ ] **Step 2:** SKILL.md decision guidance (selector-first for known controls; snapshot for discovery).
- [ ] **Step 3:** CHANGELOG `[0.10.0]`; ROADMAP Phase 10 shipped; version `0.9.0→0.10.0` in the csproj `<Version>`/`<AppVersion>` and `installer/flaui-mcp.iss` (install.ps1 is a release artifact, not in-repo).
- [ ] **Step 4:** cross-check every changed tool description against its shipped signature. Build `5/0/0`; headless green. **Commit:** `docs(release): Phase 10 selector — v0.10.0 (Phase 10 #2 T9)`.

---

## Self-Review (run after Tasks 2–9 are authored, before AGY-AFTER)

1. **Spec coverage:** map every spec §#2 requirement (selector shape, exactly-one-of, count==1 0/>1, descriptor-only mandate, action-STA-atomic-TOCTOU, resolvedElement + durability, under-constrained fast-fail, ignoreCase shared flag + preview/escape, scope-fault taxonomy, bounded walk, native pushdown, redaction-safe audit, honest-limitation docs) to a task. List gaps.
2. **Placeholder scan:** no TBD/"handle edge cases"/uncoded steps.
3. **Type consistency:** `RunOnSelectorActionAsync`, `Selector`, `FindQuery.IgnoreCase`, `resolvedElement` named identically across tasks.
