# SP3 — Per-field redaction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended)
> or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Extend redaction beyond the OS `IsPassword` flag with operator-configured rules, and make
redaction unforgettable across every egress path.

**Architecture:** One `SensitivityClassifier` decides once per unit of work (walk / operation / event);
four egress families apply that decision at their own medium (JSON payload, rendered text, pixel mask,
name-match suppression); a Roslyn source sweep in the test project asserts every content-bearing property
read in `src/` sits inside one of two closed lists.

**Tech Stack:** C# / .NET 10, FlaUI (UIA3), xUnit, `Microsoft.CodeAnalysis.CSharp` (test project only).

**Spec:** `docs/superpowers/specs/2026-07-30-sp3-per-field-redaction-design.md` @ `9c7588e`.
Read **§0** for the settled design. Everything else in the spec is rationale and *rejected* designs,
retained on purpose — **do not re-propose anything it records as rejected.**

**Solution file is `FlaUI.Mcp.slnx`. There is no `.sln`.**

---

## Binding constraints (spec §9) — restated because violating either invalidates the work

- **BC-1 — redaction is strictly an EGRESS transformation. The stored descriptor stays RAW.**
  `RefRegistry.cs:184-185` falls back to `Name`+`ControlType` as a ref's identity when `AutomationId` is
  absent, and `:337` compares that `Name` on the cached fast path. Redacting the stored descriptor makes
  exactly the controls being protected permanently unresolvable. **ROADMAP item 7 was RETIRED as invalid
  for proposing precisely this.** If a task seems to require redacting a descriptor, the task is wrong.
- **BC-2 — over-redaction is a real cost.** A false positive blinds the agent, and it cannot tell an
  empty field from a hidden one. When a choice is between over- and under-matching, neither is
  automatically safe; consult the spec section that governs it.

## Acceptance constraints (settled by negotiation — code and tests, not more prose)

Ten adversarial rounds could not settle these two areas in prose; they are to be settled here, in code,
with tests. **A reviewer checks these explicitly:**

**(a) The guarantee mechanism (§7.2).** Two **closed** lists — egress accessors (return the
already-redacted string) and identity readers (read raw, in two forms by what the caller holds). Roslyn
syntax walk in the TEST project. `dynamic` banned in `src/`. No member outside the lists may take a
caller-supplied UIA property id and forward it to a generic accessor. The legacy twelve-entry inventory is
a migration checklist, ticked to empty, then **deleted**.

**(b) The state-file lifecycle (§5.5).** Pruning requires **positive proof of death**. Access-denied is
"cannot determine" — it must **never** prune and **never** report "no server running". The server prunes
at its own boot.

## Gate commands (verified against `.github/workflows/ci.yml:20` and `docs/building.md:16`)

```bash
# Headless gate — baseline 789 passed / 0 skipped at 57bc7ef. Includes Category=SourceSweep.
dotnet test -c Release --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"

# Desktop gate — MAIN THREAD ONLY, quiet machine, physical console, user-granted lease. ~12 min.
dotnet test --filter "Category=Desktop&Category!=KnownDefect&FullyQualifiedName!~PopupGrafting"
dotnet test --filter "FullyQualifiedName~PopupGrafting"
```

⚠ **Never run the Desktop suite while a subagent is working** — a prior co-run produced 4 spurious
failures and a 40% longer run. ⚠ **Never chain `git commit` after a `grep` of test output**: `grep` exits
0 when it *matches* `"Failed!"`, which has already caused a commit on red in this repo.
⚠ **Rebuild before running after deleting a test file** — `--no-build` runs deleted tests from a stale DLL.

---

## File structure

**Create (src):**
- `src/FlaUI.Mcp.Core/Perception/Sensitivity.cs` — `RedactionSource`, `Sensitivity`.
- `src/FlaUI.Mcp.Core/Perception/SensitivityClassifier.cs` — the one decision point + `HasRules`.
- `src/FlaUI.Mcp.Core/Perception/RedactionRules.cs` — rule record, file schema, loader, validation errors.
- `src/FlaUI.Mcp.Core/Perception/ElementContent.cs` — the closed egress-accessor set.
- `src/FlaUI.Mcp.Server/Install/ServerStateFile.cs` — per-instance state write/read/prune.
- `src/FlaUI.Mcp.Server/Install/CheckRedactionRulesCommand.cs` — the CLI verb.

**Modify (src):**
- `Perception/SnapshotNode.cs:23` — `bool IsPassword` → `Sensitivity Sensitivity`.
- `Perception/SnapshotEngine.cs:83`, `:139` — classify at node build; render marker.
- `Perception/SnapshotDiff.cs:25`, `Perception/WaitCoordinator.cs:88`.
- `Perception/PerceptionManager.cs` — `:187`, `:317`, `:354`, `:599`, `:746`, `:797`, `:818`.
- `Watch/WatchPayloadBuilder.cs:34`, `Watch/WatchPump.cs:207-211`.
- `Interaction/VerifyReader.cs:24-25`.
- `Server/ServerOptions.cs:11-21`, `Server/Program.cs`, `Server/Install/CliRouter.cs:8-9,:21`.
- `Server/Tools/ScreenshotTools.cs:37`, `:44`; `Server/Tools/ContentTools.cs:36,39,81,84`.

**Create (test):**
- `test/FlaUI.Mcp.Tests/Perception/SensitivityClassifierTests.cs`
- `test/FlaUI.Mcp.Tests/Perception/RedactionRuleLoaderTests.cs`
- `test/FlaUI.Mcp.Tests/Perception/RedactionEgressTests.cs`
- `test/FlaUI.Mcp.Tests/Perception/RedactionOracleTests.cs` (Desktop)
- `test/FlaUI.Mcp.Tests/Server/RedactionSurfaceInventoryTests.cs` (`Category=SourceSweep`)
- `test/FlaUI.Mcp.Tests/Server/ServerStateFileTests.cs`
- `test/FlaUI.Mcp.Tests/Server/CheckRedactionRulesCliTests.cs`
- `docs/superpowers/plans/sp3-census.md` — Task 1's output artifact.

---

## Task 1 — THE CENSUS (task zero; non-negotiable, nothing else may start first)

**Why first:** the sweep in Task 12 asserts every content-bearing property read in `src/` sits inside one
of two closed lists. Those lists do not exist yet. **Measured:** `src/` contains **31** `.Name` reads
(excluding `ProcessName`/`ClassName`/`FileName`/`nameof`) and **22** `GetText`/`DocumentRange`/
`GetSelection` sites, against the **12** redaction sites of spec §3.1. ⚠ **Do NOT derive a count by
subtraction** — the twelve span all properties (A1 redacts a grid `Value`, A2/A5 a `Text` read, D3 a
`Value`, C1 only bounds), so `31 − 12` mixes populations and understates the work. Spec §7.2.1 forbids it.

**Files:** Create `docs/superpowers/plans/sp3-census.md`.

- [ ] **Step 1: Enumerate every candidate read**

```bash
cd "C:/Users/user/Development/c#/flauimcp"
grep -rn "\.Name\b" src --include=*.cs | grep -v "ProcessName\|ClassName\|FileName\|RuleName\|nameof" > /tmp/census-name.txt
grep -rn "GetText\|DocumentRange\|GetSelection\|GetVisibleRanges\|RangeFromPoint" src --include=*.cs > /tmp/census-text.txt
grep -rn "Patterns\.Value\|LegacyIAccessible\|Properties\.Value" src --include=*.cs > /tmp/census-value.txt
grep -rn "GetCurrentPropertyValue\|TryGetCurrentPropertyValue" src --include=*.cs > /tmp/census-generic.txt
wc -l /tmp/census-*.txt
```

- [ ] **Step 2: Classify EVERY line into exactly one bucket**

Write `docs/superpowers/plans/sp3-census.md` as a table with one row per read:
`file:line | member | property | class | disposition`, where **class** is exactly one of:

| Class | Meaning | Disposition |
|---|---|---|
| `egress` | the value can reach a caller | must move behind an `ElementContent` accessor (Task 6) |
| `identity` | ref resolution, descriptor key, cached-path compare — never leaves the process | goes on the identity-reader list; reads raw |
| `neither` | not element content (a window title, a `nameof`, a log string) | excluded from the swept set, **with a stated reason** |

Known `identity` readers, verified at `9c7588e` — this is a **starting point, not the total**:
`RefRegistry.cs:184-185`, `:304-305`, `:337`; `SnapshotEngine.cs:73`; `PerceptionManager.cs:186`, `:598`.

- [ ] **Step 3: Sanity-check the classification**

Every one of the 12 redaction sites in spec §3.1 must appear as `egress`. If one does not, the
classification is wrong — stop and re-derive rather than adjusting the table to fit.

- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/plans/sp3-census.md
git commit -m "docs(sp3): the census — every content-bearing property read in src/, classified"
```

**A read wrongly classed `identity` is exactly how a leak survives this whole design.** The census is the
artifact a reviewer checks hardest.

---

## Task 2 — `Sensitivity` and the classifier (headless, no wiring)

**Files:**
- Create: `src/FlaUI.Mcp.Core/Perception/Sensitivity.cs`
- Create: `src/FlaUI.Mcp.Core/Perception/SensitivityClassifier.cs`
- Test: `test/FlaUI.Mcp.Tests/Perception/SensitivityClassifierTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class SensitivityClassifierTests
{
    private static SensitivityClassifier Rules(params RedactionRule[] rules) =>
        SensitivityClassifier.ForRules(rules);

    [Fact]
    public void OsOnly_reports_no_rules_and_redacts_only_password_fields()
    {
        var c = SensitivityClassifier.OsOnly;
        Assert.False(c.HasRules);
        Assert.Equal(RedactionSource.Os, c.Classify("app", () => "aid", () => "name", () => true).Source);
        Assert.False(c.Classify("app", () => "aid", () => "name", () => false).Redact);
    }

    [Fact]
    public void Os_wins_over_a_matching_rule()
    {
        var c = Rules(new RedactionRule("r1", "app", false, "aid", null, null));
        var s = c.Classify("app", () => "aid", () => "name", () => true);
        Assert.True(s.Redact);
        Assert.Equal(RedactionSource.Os, s.Source);
        Assert.Null(s.RuleName);
    }

    [Fact]
    public void A_throwing_IsPassword_read_fails_closed()
    {
        var c = SensitivityClassifier.OsOnly;
        var s = c.Classify("app", () => "aid", () => "name", () => throw new InvalidOperationException());
        Assert.True(s.Redact);
        Assert.Equal(RedactionSource.Os, s.Source);
    }

    [Fact]
    public void Predicates_AND_together()
    {
        var c = Rules(new RedactionRule("r1", "app", false, "aid", null, "^nope$"));
        Assert.False(c.Classify("app", () => "aid", () => "name", () => false).Redact);
    }

    [Fact]
    public void A_rule_scoped_to_another_process_does_not_match()
    {
        var c = Rules(new RedactionRule("r1", "other", false, "aid", null, null));
        Assert.False(c.Classify("app", () => "aid", () => "name", () => false).Redact);
    }

    [Fact]
    public void First_matching_rule_wins_and_names_itself()
    {
        var c = Rules(new RedactionRule("first", "app", false, "aid", null, null),
                      new RedactionRule("second", "app", false, "aid", null, null));
        var s = c.Classify("app", () => "aid", () => "name", () => false);
        Assert.Equal(RedactionSource.Rule, s.Source);
        Assert.Equal("first", s.RuleName);
    }

    /// A null Name must NOT throw into fail-closed. Regex.IsMatch(null, ...) throws
    /// ArgumentNullException, and many UIA elements legitimately have no Name — under a naive
    /// fail-closed rule ONE namePattern rule would redact every nameless element in its process.
    /// Spec §5.2: an absent value normalises to empty and evaluates normally.
    [Fact]
    public void A_null_name_does_not_match_a_name_pattern_and_does_not_fail_closed()
    {
        var c = Rules(new RedactionRule("r1", "app", false, null, null, "secret"));
        Assert.False(c.Classify("app", () => null, () => null, () => false).Redact);
    }

    [Fact]
    public void An_empty_matching_pattern_still_matches_an_absent_name()
    {
        var c = Rules(new RedactionRule("r1", "app", false, null, null, "^$"));
        Assert.True(c.Classify("app", () => null, () => null, () => false).Redact);
    }

    /// A determinate FALSE makes the AND determinate regardless of the throwing predicate,
    /// so the element is correctly left unredacted (spec §5.2).
    [Fact]
    public void A_throwing_predicate_does_not_force_a_match_when_another_is_determinately_false()
    {
        var c = Rules(new RedactionRule("r1", "app", false, "nomatch", null, "whatever"));
        Assert.False(c.Classify("app", () => "aid", () => throw new InvalidOperationException(), () => false).Redact);
    }

    /// Spec §7.1 mandates a `global` handling test and an earlier draft of this plan had none.
    [Fact]
    public void A_global_rule_matches_regardless_of_process()
    {
        var c = Rules(new RedactionRule("g", null, true, "aid", null, null));
        Assert.True(c.Classify("anything", () => "aid", () => "n", () => false).Redact);
        Assert.True(c.Classify("other", () => "aid", () => "n", () => false).Redact);
        Assert.False(c.Classify("anything", () => "different", () => "n", () => false).Redact);
    }

    /// Cheapest-first: a false processName must short-circuit before any name thunk is pulled.
    [Fact]
    public void A_non_matching_process_never_evaluates_the_name_thunk()
    {
        bool pulled = false;
        var c = Rules(new RedactionRule("r1", "other", false, null, null, ".*"));
        c.Classify("app", () => "aid", () => { pulled = true; return "n"; }, () => false);
        Assert.False(pulled);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~SensitivityClassifierTests"`
Expected: FAIL — `Sensitivity`, `SensitivityClassifier`, `RedactionRule` do not exist.

- [ ] **Step 3: Implement `Sensitivity.cs`**

```csharp
namespace FlaUI.Mcp.Core.Perception;

/// <summary>Why a value was withheld. None = not redacted.</summary>
public enum RedactionSource { None, Os, Rule }

/// <summary>The one redaction decision. Source/RuleName exist so a false positive is debuggable —
/// BC-2 makes false positives the dominant risk and an untraceable redaction is undebuggable.</summary>
public readonly record struct Sensitivity(bool Redact, RedactionSource Source, string? RuleName)
{
    public static readonly Sensitivity Visible = new(false, RedactionSource.None, null);
    public static readonly Sensitivity OsPassword = new(true, RedactionSource.Os, null);
}
```

- [ ] **Step 4: Implement `SensitivityClassifier.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>The ONE place a redaction decision is made. Immutable after construction and safe for
/// concurrent use — it is consulted from the query STA, the action STA and the watch pump's own
/// thread (spec §5.2).
///
/// HasRules exists for the DEFAULT PATH: with no rules configured, callers must not even construct
/// the Func thunks, because a closure over the element heap-allocates a delegate and a closure on
/// every egress call. Deferring a COM read while allocating two objects is not a zero-cost default
/// (spec §4.4).</summary>
public sealed class SensitivityClassifier
{
    private readonly RedactionRule[] _rules;

    private SensitivityClassifier(RedactionRule[] rules) => _rules = rules;

    public static SensitivityClassifier OsOnly { get; } = new(Array.Empty<RedactionRule>());

    public static SensitivityClassifier ForRules(IEnumerable<RedactionRule> rules) =>
        new(rules.ToArray());

    public bool HasRules => _rules.Length > 0;

    public Sensitivity Classify(string? processName, Func<string?> automationId, Func<string?> rawName,
                                Func<bool> readIsPassword)
    {
        // OS first, always on, fail-closed on a throwing read.
        if (RedactionPolicy.IsPasswordOrFailClosed(readIsPassword)) return Sensitivity.OsPassword;

        foreach (var rule in _rules)
            if (rule.Matches(processName, automationId, rawName))
                return new Sensitivity(true, RedactionSource.Rule, rule.Name);

        return Sensitivity.Visible;
    }
}
```

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~SensitivityClassifierTests"`
Expected: PASS, 11/11.

⚠ **The `RedactionRule` RECORD belongs to THIS task, not Task 3.** An earlier draft put it in Task 3 and
told the agent to reach forward — a real ordering hazard, since this task cannot compile without it.
Create `src/FlaUI.Mcp.Core/Perception/RedactionRules.cs` here containing **only** `RedactionRule`
(the class shown in Task 3 Step 3, its constructor, `Compile`, `Matches`, `Safe`, `IsMatch`).
Task 3 then adds `RedactionConfigException`, the DTOs and `RedactionRuleFile` to the same file.
Neither task reaches forward.

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/Sensitivity.cs src/FlaUI.Mcp.Core/Perception/SensitivityClassifier.cs test/FlaUI.Mcp.Tests/Perception/SensitivityClassifierTests.cs
git commit -m "feat(sp3): the sensitivity classifier — one decision point, fail-closed, HasRules fast path"
```

---

## Task 3 — Rule record, file schema and loader

**Files:**
- Create: `src/FlaUI.Mcp.Core/Perception/RedactionRules.cs`
- Test: `test/FlaUI.Mcp.Tests/Perception/RedactionRuleLoaderTests.cs`

- [ ] **Step 1: Write the failing tests** — one per §5.4 rejection reason, each asserting the message
      names the offending rule.

```csharp
using System;
using System.IO;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class RedactionRuleLoaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sp3rules").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Write(string json)
    {
        var p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(p, json);
        return p;
    }

    private static RedactionConfigException Rejects(string path) =>
        Assert.Throws<RedactionConfigException>(() => RedactionRuleFile.Load(path));

    [Fact]
    public void A_valid_file_loads_its_rules_in_order()
    {
        var p = Write("""
        { "version": 1, "rules": [
          { "name": "a", "processName": "devenv", "automationId": "tokenBox" },
          { "name": "b", "processName": "chrome", "automationIdPattern": "^cc_" } ] }
        """);
        var rules = RedactionRuleFile.Load(p);
        Assert.Equal(new[] { "a", "b" }, Array.ConvertAll(rules, r => r.Name));
    }

    [Fact] public void A_missing_file_is_rejected() =>
        Assert.Contains("not found", Rejects(Path.Combine(_dir, "nope.json")).Message, StringComparison.OrdinalIgnoreCase);

    [Fact] public void Malformed_json_is_rejected() =>
        Assert.Contains("parse", Rejects(Write("{ not json")).Message, StringComparison.OrdinalIgnoreCase);

    [Fact] public void An_unknown_version_is_rejected() =>
        Assert.Contains("version", Rejects(Write("""{ "version": 2, "rules": [] }""")).Message, StringComparison.OrdinalIgnoreCase);

    [Fact] public void A_duplicate_rule_name_is_rejected_and_named() =>
        Assert.Contains("dup", Rejects(Write("""
        { "version": 1, "rules": [
          { "name": "dup", "processName": "a", "automationId": "x" },
          { "name": "dup", "processName": "b", "automationId": "y" } ] }
        """)).Message, StringComparison.Ordinal);

    [Fact] public void A_rule_with_no_element_predicate_is_rejected_and_named() =>
        Assert.Contains("bare", Rejects(Write("""
        { "version": 1, "rules": [ { "name": "bare", "processName": "a" } ] }
        """)).Message, StringComparison.Ordinal);

    [Fact] public void Global_together_with_processName_is_rejected_and_named() =>
        Assert.Contains("both", Rejects(Write("""
        { "version": 1, "rules": [ { "name": "both", "global": true, "processName": "a", "automationId": "x" } ] }
        """)).Message, StringComparison.Ordinal);

    [Fact] public void A_rule_that_is_neither_global_nor_process_scoped_is_rejected_and_named() =>
        Assert.Contains("floating", Rejects(Write("""
        { "version": 1, "rules": [ { "name": "floating", "automationId": "x" } ] }
        """)).Message, StringComparison.Ordinal);

    /// NonBacktracking cannot compile a backreference. Rejecting at LOAD is the whole point:
    /// a per-match timeout is charged per rule PER NODE, so one pathological pattern would burn
    /// 100ms x N nodes on every snapshot (spec §5.1).
    [Fact] public void A_pattern_NonBacktracking_cannot_compile_is_rejected_and_named() =>
        Assert.Contains("backref", Rejects(Write("""
        { "version": 1, "rules": [ { "name": "backref", "global": true, "namePattern": "(a)\\1" } ] }
        """)).Message, StringComparison.Ordinal);

    [Fact] public void More_than_MaxRules_is_rejected()
    {
        var items = string.Join(",", Enumerable.Range(0, RedactionRuleFile.MaxRules + 1)
            .Select(i => $$"""{ "name": "r{{i}}", "global": true, "automationId": "x" }"""));
        Assert.Contains("64", Rejects(Write($$"""{ "version": 1, "rules": [ {{items}} ] }""")).Message, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~RedactionRuleLoaderTests"`
Expected: FAIL — `RedactionRuleFile` / `RedactionConfigException` do not exist.

- [ ] **Step 3: Implement the loader in `RedactionRules.cs`**

⚠ `RedactionRule` itself was created in **Task 2**. Add only `RedactionConfigException`, `RuleDto`,
`RuleFileDto` and `RedactionRuleFile` to that existing file — the full listing below shows the whole file
for context, so do not paste a second copy of `RedactionRule`.

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>A malformed rule file is FATAL at startup (spec §5.4): an operator who authored a rule
/// file is depending on a shield, and starting with an empty rule set silently voids protection they
/// explicitly asked for.</summary>
public sealed class RedactionConfigException(string message) : Exception(message);

/// <summary>One operator rule. All specified predicates must match (AND). Evaluated
/// cheapest-first — processName, then exact automationId, then regexes — so a false cheap predicate
/// short-circuits before any regex runs (spec §4.1, §5.2).</summary>
public sealed class RedactionRule
{
    private readonly Regex? _aidPattern, _namePattern;

    public RedactionRule(string name, string? processName, bool global,
                         string? automationId, string? automationIdPattern, string? namePattern)
    {
        Name = name; ProcessName = processName; Global = global; AutomationId = automationId;
        _aidPattern = Compile(automationIdPattern, name, nameof(automationIdPattern));
        _namePattern = Compile(namePattern, name, nameof(namePattern));
    }

    public string Name { get; }
    public string? ProcessName { get; }
    public bool Global { get; }
    public string? AutomationId { get; }

    private static Regex? Compile(string? pattern, string ruleName, string field)
    {
        if (string.IsNullOrEmpty(pattern)) return null;
        try
        {
            // NonBacktracking guarantees linear time and has no catastrophic backtracking by
            // construction. It rejects backreferences/lookaround — rejecting expressive-but-unbounded
            // patterns at LOAD is the design, not a limitation to work around (spec §5.1).
            return new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
                             TimeSpan.FromMilliseconds(100));
        }
        catch (Exception ex)
        {
            throw new RedactionConfigException(
                $"Rule '{ruleName}': {field} cannot be compiled with RegexOptions.NonBacktracking " +
                $"({ex.Message}). Backreferences, lookaround and atomic groups are not supported — " +
                "rewrite the pattern without them.");
        }
    }

    public bool Matches(string? processName, Func<string?> automationId, Func<string?> rawName)
    {
        if (!Global && !string.Equals(ProcessName, processName, StringComparison.OrdinalIgnoreCase))
            return false;                                    // cheapest predicate, short-circuits

        if (AutomationId is not null &&
            !string.Equals(AutomationId, Safe(automationId), StringComparison.Ordinal)) return false;

        if (_aidPattern is not null && !IsMatch(_aidPattern, Safe(automationId))) return false;
        if (_namePattern is not null && !IsMatch(_namePattern, Safe(rawName))) return false;
        return true;
    }

    // An ABSENT value is not an exception: Regex.IsMatch(null, ...) throws, and many UIA elements
    // legitimately have no Name. Normalising to "" keeps one namePattern rule from redacting every
    // nameless element in its scoped process (spec §5.2).
    private static string Safe(Func<string?> read)
    {
        try { return read() ?? string.Empty; } catch { return string.Empty; }
    }

    // A genuinely UNEXPECTED failure (regex timeout on pathological input) fails closed at THIS
    // predicate only; the rule's AND still applies (spec §5.2).
    private static bool IsMatch(Regex re, string value)
    {
        try { return re.IsMatch(value); } catch { return true; }
    }
}

internal sealed record RuleDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("processName")] string? ProcessName,
    [property: JsonPropertyName("global")] bool Global,
    [property: JsonPropertyName("automationId")] string? AutomationId,
    [property: JsonPropertyName("automationIdPattern")] string? AutomationIdPattern,
    [property: JsonPropertyName("namePattern")] string? NamePattern);

internal sealed record RuleFileDto(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("rules")] List<RuleDto>? Rules);

public static class RedactionRuleFile
{
    public const int MaxRules = 64;
    private const int SupportedVersion = 1;

    public static RedactionRule[] Load(string path)
    {
        if (!File.Exists(path))
            throw new RedactionConfigException($"Redaction rule file not found: '{path}'.");

        RuleFileDto? dto;
        try { dto = JsonSerializer.Deserialize<RuleFileDto>(File.ReadAllText(path)); }
        catch (Exception ex) { throw new RedactionConfigException($"Could not parse '{path}': {ex.Message}"); }

        if (dto is null) throw new RedactionConfigException($"Could not parse '{path}': empty document.");
        if (dto.Version != SupportedVersion)
            throw new RedactionConfigException(
                $"Unsupported redaction rule file version {dto.Version} (this build supports {SupportedVersion}).");

        var rules = dto.Rules ?? new List<RuleDto>();
        if (rules.Count > MaxRules)
            throw new RedactionConfigException(
                $"{rules.Count} rules exceeds the maximum of {MaxRules}. More than {MaxRules} rules is a policy " +
                "problem, not a configuration problem.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var built = new List<RedactionRule>(rules.Count);
        for (int i = 0; i < rules.Count; i++)
        {
            var r = rules[i];
            var label = string.IsNullOrWhiteSpace(r.Name) ? $"at index {i}" : $"'{r.Name}'";
            if (string.IsNullOrWhiteSpace(r.Name))
                throw new RedactionConfigException($"Rule {label}: 'name' is required and must be non-empty.");
            if (!seen.Add(r.Name!))
                throw new RedactionConfigException($"Rule '{r.Name}': duplicate rule name.");
            if (r.Global && !string.IsNullOrEmpty(r.ProcessName))
                throw new RedactionConfigException(
                    $"Rule '{r.Name}': 'global' and 'processName' are mutually exclusive.");
            if (!r.Global && string.IsNullOrEmpty(r.ProcessName))
                throw new RedactionConfigException(
                    $"Rule '{r.Name}': needs 'processName', or 'global': true to opt into a machine-wide rule.");
            if (string.IsNullOrEmpty(r.AutomationId) && string.IsNullOrEmpty(r.AutomationIdPattern)
                && string.IsNullOrEmpty(r.NamePattern))
                throw new RedactionConfigException(
                    $"Rule '{r.Name}': needs at least one of automationId / automationIdPattern / namePattern. " +
                    "A rule with no element predicate would redact an entire process.");

            built.Add(new RedactionRule(r.Name!, r.ProcessName, r.Global,
                                        r.AutomationId, r.AutomationIdPattern, r.NamePattern));
        }
        return built.ToArray();
    }
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~RedactionRuleLoaderTests"`
Expected: PASS, 11/11.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/RedactionRules.cs test/FlaUI.Mcp.Tests/Perception/RedactionRuleLoaderTests.cs
git commit -m "feat(sp3): rule schema + loader — every rejection names its rule, regexes rejected at load"
```

---

## Task 4 — `--redaction-rules`, refuse-to-start, and the boot state file

Implements **acceptance constraint (b)**. `ServerOptions` is a positional record
(`ServerOptions.cs:11`) whose doc-comment says new params carry defaults so existing call sites compile
unchanged — **append, do not reorder.**

**Files:**
- Modify: `src/FlaUI.Mcp.Server/ServerOptions.cs:11-21`
- Create: `src/FlaUI.Mcp.Server/Install/ServerStateFile.cs`
- Modify: `src/FlaUI.Mcp.Server/Program.cs` (after `ElevationGuard.WarnIfElevated`, `:26`)
- Test: `test/FlaUI.Mcp.Tests/Server/ServerStateFileTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using System.Diagnostics;
using System.IO;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

public class ServerStateFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sp3state").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void A_written_instance_round_trips_and_reports_live_for_this_process()
    {
        var me = Process.GetCurrentProcess();
        ServerStateFile.Write(_dir, me.Id, me.StartTime.ToUniversalTime(), "C:/rules.json", "abc123");
        var found = Assert.Single(ServerStateFile.ReadAll(_dir));
        Assert.Equal(ServerLiveness.Live, found.Liveness);
        Assert.Equal("abc123", found.RulesSha256);
        Assert.Equal(1, found.StateVersion);
    }

    /// A recycled PID must not read as the same server: the start time cannot match.
    [Fact]
    public void A_recycled_pid_is_detected_by_start_time_and_reports_dead()
    {
        var me = Process.GetCurrentProcess();
        ServerStateFile.Write(_dir, me.Id, me.StartTime.ToUniversalTime().AddDays(-1), "C:/r.json", "x");
        Assert.Equal(ServerLiveness.Dead, Assert.Single(ServerStateFile.ReadAll(_dir)).Liveness);
    }

    [Fact]
    public void A_pid_that_does_not_exist_reports_dead()
    {
        ServerStateFile.Write(_dir, 0x7FFFFFFE, DateTime.UtcNow, "C:/r.json", "x");
        Assert.Equal(ServerLiveness.Dead, Assert.Single(ServerStateFile.ReadAll(_dir)).Liveness);
    }

    [Fact]
    public void An_unrecognised_stateVersion_reports_unknown_not_dead()
    {
        File.WriteAllText(Path.Combine(_dir, "4242.json"),
            """{ "stateVersion": 99, "pid": 4242, "processStartTimeUtc": "2020-01-01T00:00:00Z",
                 "redactionRulesPath": "C:/r.json", "redactionRulesSha256": "x" }""");
        Assert.Equal(ServerLiveness.Unknown, Assert.Single(ServerStateFile.ReadAll(_dir)).Liveness);
    }

    /// ACCEPTANCE CONSTRAINT (b): pruning requires POSITIVE proof of death. A non-elevated CLI that
    /// cannot inspect an elevated server's process must NOT delete that server's state file.
    [Fact]
    public void Prune_removes_only_dead_instances_and_never_unknown_ones()
    {
        var me = Process.GetCurrentProcess();
        ServerStateFile.Write(_dir, 0x7FFFFFFE, DateTime.UtcNow, "C:/r.json", "dead");
        ServerStateFile.Write(_dir, me.Id, me.StartTime.ToUniversalTime(), "C:/r.json", "live");
        File.WriteAllText(Path.Combine(_dir, "4242.json"), """{ "stateVersion": 99, "pid": 4242 }""");

        ServerStateFile.PruneDead(_dir);

        var left = ServerStateFile.ReadAll(_dir);
        Assert.Equal(2, left.Count);
        Assert.DoesNotContain(left, i => i.RulesSha256 == "dead");
        Assert.Contains(left, i => i.Liveness == ServerLiveness.Unknown);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~ServerStateFileTests"`
Expected: FAIL — `ServerStateFile` does not exist.

- [ ] **Step 3: Implement `ServerStateFile.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlaUI.Mcp.Server.Install;

public enum ServerLiveness { Live, Dead, Unknown }

public sealed record ServerInstance(int StateVersion, int Pid, string? RulesPath, string? RulesSha256,
                                    ServerLiveness Liveness, string FilePath);

internal sealed record StateDto(
    [property: JsonPropertyName("stateVersion")] int StateVersion,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("processStartTimeUtc")] DateTime ProcessStartTimeUtc,
    [property: JsonPropertyName("redactionRulesPath")] string? RedactionRulesPath,
    [property: JsonPropertyName("redactionRulesSha256")] string? RedactionRulesSha256);

/// <summary>Per-INSTANCE boot state, so the check-redaction-rules CLI can tell whether a running
/// server is enforcing the rule file on disk. One file per pid — a single well-known path breaks the
/// moment two servers run, because the last to boot silently overwrites the first (spec §5.5).</summary>
public static class ServerStateFile
{
    public const int CurrentStateVersion = 1;

    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "flaui-mcp");

    public static string DefaultDirectory => Path.Combine(Root, "instances");

    /// <summary>Durable, human-readable record of a startup refusal (spec §5.4). ⚠ There is no existing
    /// `InstallPaths` type and no diagnostic-log helper in this repo — `DataDir`/`StateDir` live in a
    /// PRIVATE record inside CliRouter (`CliRouter.cs:239`) and are not reachable from Program.cs. So
    /// this writes its own file. Never throws: failing to log must not change the exit path.</summary>
    public static void TryLogStartupError(string message)
    {
        try
        {
            Directory.CreateDirectory(Root);
            File.AppendAllText(Path.Combine(Root, "startup-errors.log"),
                $"{DateTime.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch { }
    }

    public static void Write(string dir, int pid, DateTime startUtc, string? rulesPath, string? sha)
    {
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new StateDto(CurrentStateVersion, pid, startUtc, rulesPath, sha));
        var final = Path.Combine(dir, $"{pid}.json");
        var tmp = final + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, final, overwrite: true);   // atomic replace
    }

    public static void Delete(string dir, int pid)
    {
        try { File.Delete(Path.Combine(dir, $"{pid}.json")); } catch { }
    }

    public static IReadOnlyList<ServerInstance> ReadAll(string dir)
    {
        var result = new List<ServerInstance>();
        if (!Directory.Exists(dir)) return result;

        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            StateDto? dto;
            try { dto = JsonSerializer.Deserialize<StateDto>(File.ReadAllText(file)); }
            catch { continue; }                              // unreadable/garbage: not an instance
            if (dto is null) continue;

            var liveness = dto.StateVersion != CurrentStateVersion
                ? ServerLiveness.Unknown                     // version skew is UNKNOWN, never dead
                : Liveness(dto);
            result.Add(new ServerInstance(dto.StateVersion, dto.Pid, dto.RedactionRulesPath,
                                          dto.RedactionRulesSha256, liveness, file));
        }
        return result;
    }

    // ACCEPTANCE CONSTRAINT (b). Three outcomes, and "cannot determine" is a REAL one: reading another
    // process's StartTime needs PROCESS_QUERY_INFORMATION, and this server commonly runs elevated, so a
    // non-elevated CLI gets Access Denied on a perfectly healthy server.
    private static ServerLiveness Liveness(StateDto dto)
    {
        Process p;
        try { p = Process.GetProcessById(dto.Pid); }
        catch (ArgumentException) { return ServerLiveness.Dead; }   // positively gone
        catch { return ServerLiveness.Unknown; }

        try
        {
            var start = p.StartTime.ToUniversalTime();
            return Math.Abs((start - dto.ProcessStartTimeUtc).TotalSeconds) < 2
                ? ServerLiveness.Live
                : ServerLiveness.Dead;                              // PID recycled onto another process
        }
        catch { return ServerLiveness.Unknown; }                    // access denied — do NOT prune
    }

    /// <summary>Delete ONLY instances positively proven dead. Never deletes an Unknown: a diagnostic
    /// that destroys the state it is diagnosing would degrade a healthy elevated server into a
    /// permanent "no server running" (spec §5.5).</summary>
    public static void PruneDead(string dir)
    {
        foreach (var i in ReadAll(dir))
            if (i.Liveness == ServerLiveness.Dead)
                try { File.Delete(i.FilePath); } catch { }
    }
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~ServerStateFileTests"`
Expected: PASS, 5/5.

- [ ] **Step 5: Append the option to `ServerOptions`**

In `src/FlaUI.Mcp.Server/ServerOptions.cs`, append `string? RedactionRules = null` as the LAST
positional parameter of the record at `:11`, and add to `FromArgs` at `:13-21`:

```csharp
            RedactionRules: OptionValue(args, "--redaction-rules"));
```

plus this helper alongside `ParseIntArg`:

```csharp
    // "--redaction-rules <path>" or "--redaction-rules=<path>". ABSENT (null) means the feature is off
    // and the server starts normally; a path that is present but unreadable is FATAL (spec §5.4).
    private static string? OptionValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
                return i + 1 < args.Length ? args[i + 1] : string.Empty;
            if (args[i].StartsWith(name + "=", StringComparison.Ordinal))
                return args[i][(name.Length + 1)..];
        }
        return null;
    }
```

- [ ] **Step 6: Wire startup in `Program.cs`**

Immediately after `ElevationGuard.WarnIfElevated(...)` at `:26`, insert:

```csharp
// Redaction rules: ABSENT flag => feature off, server starts normally. Flag PRESENT but the file is
// missing/malformed => refuse to start (spec §5.4). An operator who authored a rule file is depending
// on a shield; starting with an empty rule set silently voids protection they asked for.
var startupOptions = ServerOptions.FromArgs(args);
var classifier = FlaUI.Mcp.Core.Perception.SensitivityClassifier.OsOnly;
string? rulesSha = null;
if (startupOptions.RedactionRules is not null)
{
    try
    {
        classifier = FlaUI.Mcp.Core.Perception.SensitivityClassifier.ForRules(
            FlaUI.Mcp.Core.Perception.RedactionRuleFile.Load(startupOptions.RedactionRules));
        rulesSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(startupOptions.RedactionRules)));
    }
    catch (FlaUI.Mcp.Core.Perception.RedactionConfigException ex)
    {
        // The agent cannot read this: a server that refused to start has no tool surface. It is for the
        // HUMAN, out of band. Name both exits so recovery needs no guesswork (spec §5.4).
        var msg = $"flaui-mcp: refusing to start — {ex.Message}\n" +
                  "Fix the named rule, or remove --redaction-rules to start without rules.";
        Console.Error.WriteLine(msg);
        // Spec §5.4 requires this reach a durable log, NOT stderr alone: an MCP child that exits takes
        // its whole tool surface with it, and the operator may never open stderr. This log is for the
        // HUMAN, read out of band — the agent structurally cannot reach it.
        FlaUI.Mcp.Server.Install.ServerStateFile.TryLogStartupError(msg);
        return 2;
    }
}

// One file per instance; prune only positively-dead neighbours while we are here. A default-path
// operator never runs the CLI, so nothing else would ever collect orphans (spec §4.4).
var stateDir = FlaUI.Mcp.Server.Install.ServerStateFile.DefaultDirectory;
var self = System.Diagnostics.Process.GetCurrentProcess();
FlaUI.Mcp.Server.Install.ServerStateFile.PruneDead(stateDir);
FlaUI.Mcp.Server.Install.ServerStateFile.Write(stateDir, self.Id, self.StartTime.ToUniversalTime(),
                                               startupOptions.RedactionRules, rulesSha);
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    FlaUI.Mcp.Server.Install.ServerStateFile.Delete(stateDir, self.Id);
```

Then change the existing `builder.Services.AddSingleton(ServerOptions.FromArgs(args));` at `:36` to
`builder.Services.AddSingleton(startupOptions);` and add `builder.Services.AddSingleton(classifier);`
next to it, so the classifier is injectable wherever it is consumed.

- [ ] **Step 7: Build and run the headless gate**

Run: `dotnet build FlaUI.Mcp.slnx -c Release`
Then: `dotnet test -c Release --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: 0 errors, 0 warnings; headless count = 789 + 16 new (Tasks 2–4) = **805 passed / 0 skipped**.

- [ ] **Step 8: Commit**

```bash
git add src/FlaUI.Mcp.Server/ServerOptions.cs src/FlaUI.Mcp.Server/Program.cs src/FlaUI.Mcp.Server/Install/ServerStateFile.cs test/FlaUI.Mcp.Tests/Server/ServerStateFileTests.cs
git commit -m "feat(sp3): --redaction-rules, refuse-to-start, per-instance state with proof-of-death pruning"
```

---

## Task 4b — Thread the classifier through the object graph (do this BEFORE any consumer)

⚠ **This task exists because an earlier draft of this plan assumed the classifier was simply "in scope" at
every consumer, and it is not.** `SnapshotEngine` is static, `VerifyReader.FromElement` is `public
static`, and `WatchPump` builds its own reader. A review found the gap in three places at once; the fix is
one coherent task, done first, so Tasks 5–9 can consume the dependency instead of inventing it.

**All four signatures below were verified at `9c7588e`.**

**Files:** `Program.cs`; `Perception/PerceptionManager.cs:31`; `Perception/SnapshotEngine.cs:34`;
`Watch/WatchService.cs:37`; `Watch/WatchPump.cs:43`; `Interaction/VerifyReader.cs:22`;
`Server/Tools/InputTools.cs:182,:232,:301,:323`.

- [ ] **Step 1: Register the singleton** — already added in Task 4 Step 6
      (`builder.Services.AddSingleton(classifier);`). Confirm it is there before continuing.

- [ ] **Step 2: Constructor injection where a constructor exists**

| Type | Current | Change |
|---|---|---|
| `PerceptionManager` | `:31` `public PerceptionManager(WindowManager windows, RefRegistry refs, SnapshotCache …)` | **append** `SensitivityClassifier classifier`; store in a readonly field |
| `WatchService` | `:37` `public WatchService(` (multi-line) | **append** `SensitivityClassifier classifier`; pass to the pump |
| `WatchPump` | `:43` `public WatchPump(` (multi-line) | **append** `SensitivityClassifier classifier`; store for `LiveEventSourceReader` |

**Append, never reorder** — these are resolved by DI, and reordering silently rebinds arguments.

- [ ] **Step 3: Parameter threading where the member is STATIC**

- `SnapshotEngine.Build` (`:34`, `public static SnapshotModel Build(`): **append**
  `SensitivityClassifier classifier` and `string? processName`, and thread both down into the private
  `Visit` local function that reads properties at `:73-96`. `BuildModelAsync` in `PerceptionManager`
  is the only caller and now holds both — the process name is resolved **once per walk** there.
- `VerifyReader.FromElement` (`:22`, `public static VerifyRead FromElement(AutomationElement el, bool
  readCapability = false)`): change to
  `FromElement(AutomationElement el, SensitivityClassifier classifier, string? processName, bool readCapability = false)`.
  **It has exactly FOUR callers, all in `Server/Tools/InputTools.cs` — `:182`, `:232`, `:301`, `:323`**
  (verified). `InputTools` already holds a `PerceptionManager` (`_p`), so expose the classifier from it
  (`internal SensitivityClassifier Classifier => _classifier;`) rather than adding a second DI parameter
  to the tool class.

- [ ] **Step 4: Build — expect 0 errors, 0 warnings, and NO behaviour change**

Run: `dotnet build FlaUI.Mcp.slnx -c Release`
Then: `dotnet test -c Release --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: **805 passed / 0 skipped**, unchanged from Task 4. Nothing consumes the classifier yet; this
task only makes it reachable. A changed count here means a behaviour change slipped in — revert and redo.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs src/FlaUI.Mcp.Core/Perception/SnapshotEngine.cs src/FlaUI.Mcp.Core/Watch/WatchService.cs src/FlaUI.Mcp.Core/Watch/WatchPump.cs src/FlaUI.Mcp.Core/Interaction/VerifyReader.cs src/FlaUI.Mcp.Server/Tools/InputTools.cs
git commit -m "refactor(sp3): thread the classifier to every consumer — no behaviour change"
```

---

## Task 5 — Classify at node build; `SnapshotNode` carries `Sensitivity`

⚠ **`SnapshotNode` is a POSITIONAL record** (`SnapshotNode.cs:11-27`) with `bool IsPassword` at
position 12. Changing it touches every construction site and every read. **Grep before editing:**
`grep -rn "IsPassword" src test --include=*.cs`.

⚠ **BC-1: `SnapshotEngine.cs:87-92` carries a comment explaining that the RAW name is load-bearing.
Do not redact `name` at `:95`.** Update that comment's five stale cross-references while here
(spec §3.4): render is `:139` not `:133`; `RefRegistry` Name fallback is `:184-185` not `:181-182`;
the cached compare is `:337` not `:334`; `Key()` is `:211-212` not `:206-209`.

**Files:** Modify `SnapshotNode.cs:23`, `SnapshotEngine.cs:83,:95,:139`, `PerceptionManager.cs:797`.

- [ ] **Step 1: Write the failing test — the DEFAULT PATH stays byte-identical**

Add to `test/FlaUI.Mcp.Tests/Perception/RedactionEgressTests.cs`:

```csharp
using System.Drawing;
using FlaUI.Core.Definitions;
using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

public class RedactionEgressTests
{
    private static SnapshotNode Node(string name, Sensitivity s) =>
        new("e1", 0, "", ControlType.Edit, "aid", name, new Rectangle(0, 0, 10, 10),
            true, true, false, false, s, false, new[] { 1 }, new string[0], "");

    /// DEFAULT PATH (spec §4.4): with no rules configured the render must be BYTE-IDENTICAL to today.
    /// The redacted: marker is emitted ONLY for Source == Rule — emitting it for an OS password would
    /// change the rendered line of every password field in every existing install.
    [Fact]
    public void An_os_password_node_renders_exactly_as_before()
    {
        var text = SnapshotEngine.Render(new SnapshotModel(new[] { Node("secret", Sensitivity.OsPassword) }),
                                         new SnapshotOptions());
        Assert.Contains("\"[REDACTED]\"", text);
        Assert.DoesNotContain("redacted:", text);
    }

    [Fact]
    public void A_rule_redacted_node_names_its_rule_in_the_state_list()
    {
        var s = new Sensitivity(true, RedactionSource.Rule, "card-fields");
        var text = SnapshotEngine.Render(new SnapshotModel(new[] { Node("4111 1111", s) }), new SnapshotOptions());
        Assert.Contains("\"[REDACTED]\"", text);
        Assert.Contains("redacted:rule:card-fields", text);
    }

    [Fact]
    public void A_visible_node_renders_its_real_name()
    {
        var text = SnapshotEngine.Render(new SnapshotModel(new[] { Node("Username", Sensitivity.Visible) }),
                                         new SnapshotOptions());
        Assert.Contains("\"Username\"", text);
        Assert.DoesNotContain("[REDACTED]", text);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~RedactionEgressTests"`
Expected: FAIL to compile — `SnapshotNode` still takes `bool IsPassword`.

- [ ] **Step 3: Change the record member**

`SnapshotNode.cs:23`: replace `bool IsPassword,` with `Sensitivity Sensitivity,`.

- [ ] **Step 4: Classify at node build**

`SnapshotEngine.cs:83` — replace the bare `IsPassword` read with the classifier call. The walk already
materialises `aid` (`:93`, `:95`) and `name`, so the thunks cost nothing here:

```csharp
                Sensitivity sensitivity = classifier.HasRules
                    ? classifier.Classify(walkProcessName, () => aid, () => name,
                                          () => el.Properties.IsPassword.ValueOrDefault)
                    : (RedactionPolicy.IsPasswordOrFailClosed(() => el.Properties.IsPassword.ValueOrDefault)
                        ? Sensitivity.OsPassword : Sensitivity.Visible);
```

`walkProcessName` is resolved **once per walk**, not per node, and threaded in as a parameter.
⚠ **Task 13 must MEASURE the homogeneity assumption before this hoist ships.**

- [ ] **Step 5: Update the render**

`SnapshotEngine.cs:139` — `string shownName = n.Sensitivity.Redact ? "[REDACTED]" : n.Name;`
and in the state list built at `:134-138`, append **only** for a rule:

```csharp
        if (n.Sensitivity.Source == RedactionSource.Rule) state.Add($"redacted:rule:{n.Sensitivity.RuleName}");
```

- [ ] **Step 6: Fix the stats read**

`PerceptionManager.cs:797` — `nodes.Count(n => n.IsPassword)` becomes
`nodes.Count(n => n.Sensitivity.Source == RedactionSource.Os)`, so the shipped password counter keeps its
meaning, and add a sibling `nodes.Count(n => n.Sensitivity.Redact)` for `redactedCount` (Task 10).

- [ ] **Step 7: Build, fix every construction AND READ site, run the headless gate**

⚠ **This step must leave the tree compiling, and an earlier draft did not.** Changing the record member
breaks every *reader* of `n.IsPassword` too — `SnapshotDiff.cs:25` (Task 7), `WaitCoordinator.cs:88`
(Task 9) and the family-A reads in `PerceptionManager.cs` (Task 6) all still name the old member. Those
tasks change the *behaviour* at those sites; **this** step makes them **compile**, mechanically and with
no behaviour change:

```bash
grep -rn "IsPassword" src test --include=*.cs
```

For each hit that reads a `SnapshotNode`, substitute the exact equivalent of today's semantics:

```csharp
n.IsPassword   →   n.Sensitivity.Source == RedactionSource.Os
```

⚠ **Map to `Source == Os`, NOT to `.Redact` — and this is not pedantry, it is what keeps every later pin
honest.** `.Redact` is also true for a *rule* redaction, so using it here would silently implement Tasks
7 and 9 as a side effect of a compile fix. Their tests would then pass **with their implementation step
omitted** — a vacuous pin, and this repo has shipped two of those already. `Source == Os` reproduces
today's behaviour exactly, so each later task's switch to `.Redact` is a real behaviour change that its
own test catches.

Do **not** add rule handling here. The commit at Step 8 must build clean and change no behaviour.

Run: `dotnet build FlaUI.Mcp.slnx -c Release` — fix each remaining compile error by passing a `Sensitivity`.
Then: `dotnet test -c Release --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: **818 passed / 0 skipped** (815 + 3).

### ⚠ AMENDMENT — the "no existing test may be edited" rule needs a distinction, or this task deadlocks

An earlier draft said flatly *"No existing test may be edited to accommodate this."* **That is impossible
as written, and following it literally deadlocks the task.** `SnapshotNode` is a positional record, so
changing its member changes its CONSTRUCTOR, and **three existing test files already construct or read it**
(verified at HEAD):

| Site | What it does | Mechanical replacement |
|---|---|---|
| `test/…/Perception/WaitNameOracleTests.cs:21` | constructs, `IsPassword: isPassword` (a `bool` parameter) | `Sensitivity: isPassword ? Sensitivity.OsPassword : Sensitivity.Visible` |
| `test/…/Perception/PasswordRedactionTests.cs:52` | constructs, `IsPassword: true` | `Sensitivity: Sensitivity.OsPassword` |
| `test/…/Perception/SnapshotModelPinTests.cs:22` | **reads**, `n.IsPassword ? "[REDACTED]" : n.Name` | `n.Sensitivity.Source == RedactionSource.Os ? …` |

**The rule's real intent (spec §4.4) is about ASSERTIONS, not signatures.** Restate it as two rules:

- ✅ **ALLOWED — a mechanical CONSTRUCTION/READ fix** forced by the changed signature, using the exact
  equivalences in the table above. These preserve today's semantics precisely and change no assertion.
- ❌ **FORBIDDEN — editing any existing test's ASSERTION, expected string, or expected count** to make it
  agree with SP3. That is the default-path regression §4.4 exists to prevent. If an assertion needs
  changing, **STOP** — the rendering changed and it must not have.

⚠ Map reads to **`Source == Os`**, not `.Redact` — see the boxed warning above. The three sites in the
table are the COMPLETE set at HEAD; if `grep -rn "IsPassword" test --include=*.cs` shows a fourth
*`SnapshotNode`* site, that is new since this was written — report it rather than adapting. (Other
`IsPassword` hits in `test/` are unrelated: they belong to `TextReadResult`, `GridCellInfo`,
`RedactionPolicy` and the watch `FakeReader`, none of which this task changes.)

- [ ] **Step 8: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/SnapshotNode.cs src/FlaUI.Mcp.Core/Perception/SnapshotEngine.cs src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs test/FlaUI.Mcp.Tests/Perception/RedactionEgressTests.cs
git commit -m "feat(sp3): classify once at node build; render marker only for rule redactions"
```

---

## Task 6 — The egress accessor set (family A) — acceptance constraint (a)

**The accessors return the ALREADY-REDACTED string.** A type carrying `{raw, Sensitivity}` proves the
classification was *computed*, not *applied* — a developer who forgets writes `return result.RawName`
and ships the leak under a green gate. Raw access is a **separately named** member.

**Files:** Create `src/FlaUI.Mcp.Core/Perception/ElementContent.cs`; modify `PerceptionManager.cs:317`,
`:354`, `:599`; `Watch/WatchPayloadBuilder.cs:34`; `Interaction/VerifyReader.cs:24-25`;
`Perception/TerminalTabReader.cs:118`, `:145` (**the 13th site — see the amendment below**).

> **AMENDMENT (2026-07-31, operator-approved).** Spec §3.1 enumerates **twelve** egress sites. It is
> **incomplete**: `TerminalTabReader.cs:31-32` (`NameOf`) reads a tab's raw `.Name`, and it reaches the
> wire at **two** exits — `ContentTools.cs:114` (`title`, via `TabListing.Title` built at
> `TerminalTabReader.cs:118`) and `ContentTools.cs:101` (`tabTitle`, via the `Result.TabTitle` built from
> `:145`). Nothing in that file touches `RedactionPolicy`. Found INDEPENDENTLY by the Task 1 census and by
> a blind second pass, which is why it is credited rather than treated as noise. Tab titles routinely
> carry working directories, SSH destinations and command strings.
>
> ⚠ **`NameOf` has FIVE call sites and they SPLIT — do not blanket-redact it.** `:118` and `:145` are
> **egress**. `:133`, `:135` and `:207` compute `activeTitle` / `activeTitleUnique` / the fresh title list
> purely to match the tab to restore focus to; they never leave the process and are **identity**. Use the
> same `Read.Text` / `Read.RawForIdentity` split as the other dual-purpose sites.
>
> ⚠ **`:207` is `fresh.Select(NameOf)` — a METHOD-GROUP reference.** A `grep` for `NameOf\(` misses it and
> reports four call sites. It cost one review round here; Task 12's Roslyn sweep resolves by symbol and
> would have caught it, which is precisely why the sweep is not a `grep`.
>
> ### ⚠ DEF-4 — the restore-confidence oracle. **SP3 CREATES this; it does not exist today.**
>
> `:135`'s `activeTitleUnique` flows to `RestoreTarget.Resolve` (`:208`) → `d.Confidence` → `Result`
> (`:190`) → **`restoreConfidence` on the wire at `ContentTools.cs:101`**. So a *title comparison* is
> observable to the caller as a string.
>
> Today that is worthless — nothing redacts tab titles, so an agent that wants a title just reads it. The
> moment SP3 hides some titles, the field becomes a side channel: set the active tab's title to a guess,
> watch whether confidence drops from `high` to `reduced`, and learn that a hidden tab carries that exact
> title. **Same defect class as DEF-3, and SP3 would introduce it — so fixing it is mandatory, not
> optional scope.**
>
> **The fix is NOT to redact `:133`/`:135`/`:207`.** That was considered and rejected: every redacted tab
> would then compare equal to every other (all read as the same token), so `activeTitleUnique` goes false
> whenever two tabs are protected, ordinal fallback kicks in for exactly the tabs the operator asked to
> protect, and the field *still* leaks — now "two or more tabs are redacted". That trades one leak for a
> leak plus a regression.
>
> **The fix is to PARTITION THE IDENTITY SPACE BY `Sensitivity`.** Compare a tab's raw title only against
> tabs of the *same* `Sensitivity`. An unprotected tab then structurally cannot collide with a protected
> one, so the oracle closes; protected tabs still match each other on raw titles, so confident restore
> survives. With no rules configured every tab is `Visible`, the partition is a no-op, and the default
> path is byte-identical — which §4.4 requires.
>
> ⚠⚠ **DO NOT IMPLEMENT THE PARTITION BY FILTERING THE LIST.** `RestoreTarget.Resolve`
> (`RestoreTarget.cs:14-31`) returns `SelectIndex` as an index **into `freshTitles`**, and the caller does
> `fresh[idx]` at `:211`; `recordedOrdinal` is also bounds-checked against `freshTitles.Count` at `:28`.
> Filtering the list desynchronises both and **selects the wrong tab** — a worse bug than the leak. Pass a
> **parallel `IReadOnlyList<Sensitivity>` of the same length** and have `Resolve` skip non-matching
> entries in place, keeping every index an index into the full list.
>
> The spec is frozen at `9c7588e`; this amendment is recorded here because the plan is the executable
> artifact. Task 12 Step 5 ticks **thirteen** entries, not twelve.

- [ ] **Step 1: Implement `ElementContent.cs`**

```csharp
using System;
using FlaUI.Core.AutomationElements;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>THE closed set of egress accessors (spec §7.2, acceptance constraint (a)). Outside this
/// type and the pinned identity readers, src/ may not read a content-bearing UIA property at all —
/// RedactionSurfaceInventoryTests enforces it.
///
/// Every accessor returns the ALREADY-REDACTED string. The easy path (call it, return what it gave
/// you) is the SAFE path; that is the property earlier designs lacked.</summary>
public static class ElementContent
{
    public const string RedactedToken = "[REDACTED]";

    public readonly record struct Read(string Text, Sensitivity Sensitivity)
    {
        /// <summary>The RAW value, for identity paths only (BC-1). Its only legitimate callers are the
        /// descriptor / ref-resolution sites, which are few, pinned and reviewed. Building a payload
        /// from this is visibly wrong at the call site and the sweep flags it.</summary>
        public string RawForIdentity { get; init; }
    }

    public static Read Name(AutomationElement el, SensitivityClassifier classifier, string? processName)
    {
        string raw = Safe(() => el.Name);
        var s = Classify(el, classifier, processName, () => Safe(() => el.Properties.AutomationId.ValueOrDefault), () => raw);
        return new Read(s.Redact ? RedactedToken : raw, s) { RawForIdentity = raw };
    }

    public static Read Value(AutomationElement el, SensitivityClassifier classifier, string? processName)
    {
        var s = Classify(el, classifier, processName,
                         () => Safe(() => el.Properties.AutomationId.ValueOrDefault), () => Safe(() => el.Name));
        if (s.Redact) return new Read(RedactedToken, s) { RawForIdentity = string.Empty };
        string raw = Safe(() => el.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault ?? string.Empty);
        if (raw.Length == 0) raw = Safe(() => el.Name);
        return new Read(raw, s) { RawForIdentity = raw };
    }

    public static Read Text(AutomationElement el, SensitivityClassifier classifier, string? processName,
                            Func<string> readText)
    {
        var s = Classify(el, classifier, processName,
                         () => Safe(() => el.Properties.AutomationId.ValueOrDefault), () => Safe(() => el.Name));
        if (s.Redact) return new Read(RedactedToken, s) { RawForIdentity = string.Empty };
        string raw = Safe(readText);
        return new Read(raw, s) { RawForIdentity = raw };
    }

    // DEFAULT PATH: with no rules we never construct the thunks — a closure over `el` heap-allocates a
    // delegate AND a closure on every egress call, before Classify even runs (spec §4.4).
    private static Sensitivity Classify(AutomationElement el, SensitivityClassifier classifier,
                                        string? processName, Func<string?> aid, Func<string?> name)
    {
        if (!classifier.HasRules)
            return RedactionPolicy.IsPasswordOrFailClosed(() => el.Properties.IsPassword.ValueOrDefault)
                ? Sensitivity.OsPassword : Sensitivity.Visible;
        return classifier.Classify(processName, aid, name, () => el.Properties.IsPassword.ValueOrDefault);
    }

    private static string Safe(Func<string?> read)
    {
        try { return read() ?? string.Empty; } catch { return string.Empty; }
    }
}
```

- [ ] **Step 2: Route each family-A site through an accessor**

⚠ **Each accessor needs a `SensitivityClassifier` and a `processName`, and two of these five call sites
have neither in scope.** An earlier draft said "replace the branch, keeping surrounding behaviour", which
hid real signature and injection work. Site by site:

| Site | Accessor | How the classifier and processName get there |
|---|---|---|
| `PerceptionManager.cs:317` grid cell | `ElementContent.Value` | `PerceptionManager` takes the classifier as a **constructor dependency** (it is already a DI singleton, registered in `Program.cs`); `processName` comes from the operation root already resolved by `EnsureAllowed`/`SafeProcessName` on that path |
| `PerceptionManager.cs:354` `ReadText` | `ElementContent.Text` | same; ⚠ `ReadText` is `private static` — add `SensitivityClassifier` and `string? processName` as parameters and pass them from both callers (`GetTextAsync`, `GetTextBySelectorAsync`) |
| `PerceptionManager.cs:599` `find` | `ElementContent.Name` | same; pass `read.RawForIdentity` to the descriptor at `:606` — **BC-1, the descriptor keeps the RAW name** |
| `WatchPayloadBuilder.cs:34` | via the reader | change `IEventSourceReader` (`WatchPayloadBuilder.cs:11-13`) to expose `Sensitivity Sensitivity` and an already-redacted `Name`, instead of `bool IsPassword` + a RAW `Name`. ⚠ **`LiveEventSourceReader` (`WatchPump.cs:227+`) must obtain both by calling `ElementContent.Name`, NOT by classifying itself.** An earlier draft said "it holds the element, so it classifies" — which would read `el.Name` **outside the closed egress list and fail Task 12's sweep**, since that sweep is the whole guarantee. It reuses the `procName` already computed at `WatchPump.cs:207` for the denylist check, so this costs **zero** additional COM reads, and takes the classifier from the `WatchPump` constructor (Task 4b). `NullEventSourceReader` (`WatchPump.cs:283`) returns `Sensitivity.Visible`. |
| `VerifyReader.cs:24-25` | `ElementContent.Text` | ⚠ `VerifyReader.FromElement` is **`public static`** with no classifier in scope. Add two parameters: `FromElement(AutomationElement el, SensitivityClassifier classifier, string? processName, bool readCapability = false)`. Find and update every caller first: `grep -rn "VerifyReader.FromElement" src test --include=*.cs` |

| `TerminalTabReader.cs:118` (list) and `:145` (read) | `ElementContent.Name` | ⚠ **the 13th site (amendment above)**. `TerminalTabReader` is a `static` helper with no DI: **append** `SensitivityClassifier classifier` and `string? processName` to `ListTabs`/`Run` and thread them from `PerceptionManager.ListTerminalTabsAsync` / the `desktop_read_terminal_tab` path, which already hold both. `:118` and `:145` take `read.Text`; **`:133`/`:135`/`:207` keep the RAW value** (`RawForIdentity`). Then fix **DEF-4** (see the amendment): thread a parallel `IReadOnlyList<Sensitivity>` into `RestoreTarget.Resolve` and match only within the same `Sensitivity` — **a parallel mask, never a filtered list**, or `SelectIndex` desynchronises from `fresh` and restores the wrong tab |

Keep each site's surrounding behaviour otherwise unchanged — this task is otherwise a refactor. The one
behaviour change is DEF-4, pinned below.

- [ ] **Step 2b: Pin DEF-4 — headless, because `RestoreTarget.Resolve` is a PURE static function**

No Desktop lease is needed: `Resolve` takes titles and returns a `Result`. Add to
`test/FlaUI.Mcp.Tests/Perception/` — four facts, each one a distinct way the partition can be got wrong:

1. **The oracle is closed.** A `Visible` active tab titled `"Guess"`, plus a `Rule`-redacted background tab
   whose RAW title is also `"Guess"`, resolves with confidence **`high`** — the collision must not be seen,
   because seeing it is the leak.
2. **Restore still works for protected tabs.** Two `Rule`-redacted tabs with DISTINCT raw titles: the
   active one resolves to its own index with confidence `high`.
3. **A genuine same-partition collision still degrades honestly.** Two `Rule`-redacted tabs with the SAME
   raw title fall back to ordinal with confidence `reduced` — the partition must not manufacture false
   confidence.
4. **⚠ INDEX ALIGNMENT — the fact that catches the filtered-list implementation.** Build a list where the
   target sits at a full-list index that a filtered list would renumber (e.g. `[redacted, redacted,
   visible-target]`, matching on the visible one). Assert `SelectIndex == 2`, the index into the FULL list.
   **A filtered-list implementation returns 0 here and silently restores the wrong tab.** Without this
   fact, facts 1–3 all pass on the broken implementation.

⚠ **Do NOT pin DEF-4 only through the tool surface.** The oracle is a property of `Resolve`'s pure logic;
testing it end-to-end would need a live Terminal and would still not distinguish fact 4's failure.

- [ ] **Step 3: Build and run the headless gate**

Run: `dotnet build FlaUI.Mcp.slnx -c Release && dotnet test -c Release --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: **808 passed / 0 skipped**, unchanged — this task is a refactor and must change no behaviour.

- [ ] **Step 4: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/ElementContent.cs src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs src/FlaUI.Mcp.Core/Watch/WatchPayloadBuilder.cs src/FlaUI.Mcp.Core/Interaction/VerifyReader.cs
git commit -m "refactor(sp3): family A behind the closed egress accessor set"
```

---

## Task 7 — Family B: diff

`SnapshotDiff.cs:25` `ShownName` becomes `n.Sensitivity.Redact ? "[REDACTED]" : n.Name`. Per spec §5.3
the diff emits **no** provenance field — stated explicitly so a reviewer does not read it as an omission.

- [ ] **Step 1: Add the pin** to `RedactionEgressTests.cs`:

```csharp
    [Fact]
    public void A_rule_redacted_node_is_redacted_in_a_diff()
    {
        var before = new SnapshotModel(new[] { Node("old", Sensitivity.Visible) });
        var after = new SnapshotModel(new[] { Node("4111", new Sensitivity(true, RedactionSource.Rule, "card")) });
        var d = SnapshotDiff.Compute("w1:1", before, "w1:2", after);
        Assert.DoesNotContain("4111", System.Text.Json.JsonSerializer.Serialize(d));
    }
```

- [ ] **Step 2: Run — expect FAIL.** `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~RedactionEgressTests"`
- [ ] **Step 3: Make the change** at `SnapshotDiff.cs:25`.
- [ ] **Step 4: Run — expect PASS**, and headless **809 / 0**.
- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/SnapshotDiff.cs test/FlaUI.Mcp.Tests/Perception/RedactionEgressTests.cs
git commit -m "feat(sp3): family B — diff redacts on the classifier's decision"
```

---

## Task 8 — Family C: pixels, plus DEF-1 and DEF-2

**DEF-1:** `PerceptionManager.cs:818` reads `d.Properties.IsPassword.ValueOrDefault` **raw** inside
`try/catch{}` while all eleven text sites fail closed — so a throwing provider gets its text redacted and
its **pixels captured**. **DEF-2:** `ScreenshotTools.cs:37` passes `Array.Empty<Rectangle>()`, so
full-desktop capture masks nothing.

- [ ] **Step 1: Write the failing Desktop pins** in `test/FlaUI.Mcp.Tests/Perception/RedactionOracleTests.cs`
      with `[Trait("Category","Desktop")]`: a fixture element whose `IsPassword` read throws yields a mask
      rect (DEF-1); a full-desktop capture of a window containing a password box reports
      `redactions > 0` (DEF-2).
- [ ] **Step 2: Run — expect FAIL** (main thread only):
      `dotnet test --filter "FullyQualifiedName~RedactionOracleTests"`
- [ ] **Step 3: Fix DEF-1** — `:818` uses `RedactionPolicy.IsPasswordOrFailClosed(...)`, and when the
      classifier `HasRules`, collects a rect for any element whose `Sensitivity.Redact` is true.
- [ ] **Step 4: Fix DEF-2** — `ScreenshotTools.cs:33-37` resolves password rects for each visible
      non-denied window and passes them to `CaptureRectangle` instead of `Array.Empty<Rectangle>()`.
- [ ] **Step 5: Run — expect PASS.**
- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs test/FlaUI.Mcp.Tests/Perception/RedactionOracleTests.cs
git commit -m "fix(sp3): DEF-1 the pixel path failed open; DEF-2 full-desktop capture masked nothing"
```

---

## Task 9 — Family D: oracle suppression and DEF-3

**DEF-3 is reachable today:** `find` matches on the POST-redaction name
(`PerceptionManager.cs:599-601`) and so does the selector walk (`:187-189`), so
`desktop_find name="[REDACTED]" mode=eq` returns the ref and bounds of **every password field** in a
window. No value crosses the wire — what leaks is the handle set.

**The fix filters RESULTS, not QUERIES.** An element whose `Sensitivity.Redact` is true is excluded from
name-predicate matching **whatever** was searched. ⚠ **Do NOT ban a query equal to the token** — that was
tried and refuted: `[REDACTED]` is ordinary content in redacted PDFs, scrubbed logs and compliance
reports, and banning it creates a permanent blind spot.

- [ ] **Step 1: Write the failing pins — BOTH halves, on all THREE paths** (six facts), in
      `RedactionOracleTests.cs`:
      (1) a redacted element is **not** returned by a name query, for its real name AND for the token;
      (2) an element **genuinely named `[REDACTED]` that is not redacted IS** returned.
      One fact per path — an aggregate test would let two of three regress silently.
- [ ] **Step 2: Run — expect FAIL.**
- [ ] **Step 3: Implement** at `PerceptionManager.cs:601` (`find`), `:189` (selector post-filter) and
      `WaitCoordinator.cs:88` (`wait_for by=name`): skip the element when its `Sensitivity.Redact` is
      true, before the name predicate is evaluated.
- [ ] **Step 4: Run — expect PASS, 6/6.**

- [ ] **Step 4b: Pin BC-1 TARGETABILITY — spec §7.1 demands this be PROVEN, not asserted**

⚠ **This is the exact hazard that retired ROADMAP item 7, and an earlier draft of this plan omitted it.**
Because D2/D4 exclude redacted elements from name matching, a `namePattern` rule can hit an element with
**no `AutomationId` whose Name was its only searchable identity**. It must remain targetable. Add a
Desktop fact in `RedactionOracleTests.cs`:

```csharp
    /// BC-1: a rule-redacted element with NO AutomationId is still resolvable by ref and still
    /// actionable. The descriptor keeps the RAW name (RefRegistry.cs:184-185 falls back to
    /// Name+ControlType; :337 compares it), so only NAME SEARCH is withheld — which is the intent.
    [SkippableFact, Trait("Category", "Desktop")]
    public async Task A_rule_redacted_element_without_an_automationId_is_still_targetable_by_ref()
    {
        // arrange: a fixture element with no AutomationId, matched by a namePattern rule
        var snap = await Snapshot(window);
        var refId = RefOfRedactedElement(snap);          // present in the snapshot, name shows [REDACTED]
        Assert.NotNull(refId);

        // it is NOT findable by name (the oracle is closed) ...
        Assert.Empty((await Find(window, name: RawName)).Matches);

        // ... but the ref still resolves and still acts.
        var set = await SetValue(window, refId!, "typed");
        Assert.True(set.Ok);
    }
```

Run: `dotnet test --filter "FullyQualifiedName~RedactionOracleTests"` (main thread). Expected: PASS.
If this fails, **stop** — SP3 has reintroduced the defect that invalidated item 7.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs src/FlaUI.Mcp.Core/Perception/WaitCoordinator.cs test/FlaUI.Mcp.Tests/Perception/RedactionOracleTests.cs
git commit -m "fix(sp3): DEF-3 the redaction token was a locator oracle — filter results, not queries"
```

---

## Task 10 — Wire fields: `redacted`, `redactedBy`, `redactedCount`

`isPassword` **keeps its literal OS meaning** — it is already on the wire at `ContentTools.cs:36,39,81,84`
and an existing consumer keys off it. `redacted` is the one field a consumer should branch on.

- [ ] **Step 1: Write the failing pin** — for a rule-redacted grid cell and `get_text`:
      `isPassword == false`, `redacted == true`, `redactedBy == "rule:<name>"`; for an OS password:
      `isPassword == true`, `redacted == true`, `redactedBy == "os"`.
      ⚠ Call the **real tool method**; do not re-declare the anonymous projection locally — a locally
      re-declared projection cannot catch a field rename and produces a vacuous test.
- [ ] **Step 2: Run — expect FAIL.**
- [ ] **Step 2b: Pin the `SnapshotStats` tallies** — an earlier draft wired them and tested neither.
      A headless fact over a hand-built `SnapshotModel` containing one OS-password node, one rule-redacted
      node and one visible node asserts the shipped password counter counts **only** the OS one (its
      meaning must not change) while `redactedCount` counts **both** redacted nodes.
- [ ] **Step 3: Add the fields** to `GridCellInfo` / `TextReadResult` (`PerceptionManager.cs:868`, `:870`)
      and to the four `ContentTools` projections; add `redactedCount` to `SnapshotStats`.
- [ ] **Step 4: Run — expect PASS**; headless gate green.
- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs src/FlaUI.Mcp.Server/Tools/ContentTools.cs test/FlaUI.Mcp.Tests/Perception/RedactionEgressTests.cs
git commit -m "feat(sp3): redacted/redactedBy/redactedCount — isPassword keeps its OS meaning"
```

---

## Task 11 — `check-redaction-rules` CLI

**Files:** Create `src/FlaUI.Mcp.Server/Install/CheckRedactionRulesCommand.cs`; modify
`CliRouter.cs:8-9` (add the verb to `Verbs`) and `:21` (add the `case`).

Exit codes (spec §5.5): `0` valid and live · `1` invalid, offending rule named · `3` valid but the
running server loaded something else · `4` cannot determine (two distinct messages: access-denied vs
`stateVersion` skew) · `5` valid, no server running.

⚠ **Every dry-run output carries a BANNER** (spec §5.5) stating that results reflect the **file**, and
that a running server enforces what it loaded at boot. An earlier draft of this plan printed results and
exited with no banner — the banner is what stops an operator reading a passing dry-run as proof the
server is enforcing it. Exact text:

```
NOTE: these results reflect the RULE FILE on disk. A running server enforces the rules it loaded at
boot; restart it to apply changes. Exit code 0 means the running server is already enforcing this file.
```

⚠ **The dry-run ALWAYS prints its results.** Withholding output on a hash mismatch forces a server
restart per regex tweak, destroying the loop it exists to protect.
⚠ **Rule-matched nodes show their RAW name**; OS password fields stay `[REDACTED]`; **no values ever**.
The operator is a local human debugging their own rule — redacting there makes rule refinement impossible.
⚠ `--list-windows` reuses `WindowManager.ListWindowsAsync`, **not** a raw UIA walk.

- [ ] **Step 1: Write the failing tests** in `CheckRedactionRulesCliTests.cs` covering: a valid file with
      no server → `5`; an invalid file → `1` naming the rule; a hash mismatch → `3` **with results still
      printed**; an `Unknown` instance → `4` with the access-denied message; a version-skew instance → `4`
      with the **upgrade** message (distinct text — sending an operator with a version skew to debug
      permissions is the defect this separation exists to prevent); and **every** exit path emits the
      banner above.
- [ ] **Step 2: Run — expect FAIL.**
- [ ] **Step 3: Implement the command and register the verb.**
- [ ] **Step 4: Run — expect PASS**; headless gate green.
- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/CheckRedactionRulesCommand.cs src/FlaUI.Mcp.Server/Install/CliRouter.cs test/FlaUI.Mcp.Tests/Server/CheckRedactionRulesCliTests.cs
git commit -m "feat(sp3): check-redaction-rules — always prints results, exit 0/1/3/4/5"
```

---

## Task 12 — The source sweep — acceptance constraint (a)

**Files:** Modify `test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj` (add
`<PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.14.0" />` — pin alongside the SDK
and bump it in the same change as any `LangVersion` move); create
`test/FlaUI.Mcp.Tests/Server/RedactionSurfaceInventoryTests.cs` with `[Trait("Category","SourceSweep")]`.

The sweep asserts, over `src/**/*.cs` parsed with Roslyn:

1. Every read of a **content-bearing** property sits inside a member on the **egress-accessor list** or
   the **identity-reader list** from Task 1's census. Swept: `AutomationElement.Name` / `.Current.Name` /
   `Properties.Name`; `ValuePattern.Value`, `LegacyIAccessiblePattern.Value`, `Properties.Value`;
   `ITextRange.GetText` **by method, whatever produced the range**; `SnapshotNode.Name`,
   `ElementDescriptor.Name`. **Structural properties are explicitly OUT of scope** — `ControlType`,
   `BoundingRectangle`, `IsEnabled`, `IsOffscreen`, `ProcessId`, `RuntimeId` are read throughout and
   carry no content.
2. **No `dynamic` anywhere in `src/`** — a runtime-bound receiver is invisible to a `SemanticModel`.
   Measured: zero uses today, so the ban costs nothing.
3. **No caller-supplied-property-id passthrough** — outside the lists, no member may take a UIA
   property-id-typed parameter and forward it to a generic accessor. One suppressed
   `GetProp(el, PropertyId id)` helper would otherwise become a universal raw accessor.
4. Generic accessors (`GetCurrentPropertyValue`, `TryGetCurrentPropertyValue`) are resolved by **static
   field reference**; an unresolvable argument **fails** and needs an explicit reasoned suppression.
5. The repo root is located by walking up from `AppContext.BaseDirectory` to the first directory
   containing **`FlaUI.Mcp.slnx`**, and **fails loudly** if not found — a sweep that silently matches
   zero files is the false-GREEN this whole task exists to prevent.
6. Parse diagnostics are reported as **toolchain skew**, not as a redaction failure.

- [ ] **Step 1: Add the package reference and write the sweep.**
- [ ] **Step 2: Run it — expect FAIL**, listing every unlisted read.
      Run: `dotnet test FlaUI.Mcp.slnx --filter "Category=SourceSweep"`
- [ ] **Step 3: Reconcile** — each failure is either an egress read that must move behind an accessor, or
      an identity read that belongs on the census list. **Do not widen the list to silence a failure**
      without classifying the read first.
- [ ] **Step 4: MUTATION-VERIFY.** Add a throwaway `src/` method reading `el.Name` outside both lists and
      confirm the sweep fails naming it; add a `dynamic` local and confirm it fails; add a
      `GetProp(el, PropertyId)` passthrough and confirm it fails. **Delete all three, rebuild** (⚠
      `--no-build` runs deleted tests from a stale DLL), re-run green.
- [ ] **Step 5: Retire the legacy inventory.** Tick each of the **thirteen** entries — §3.1's twelve plus
      `TerminalTabReader` (the amendment in Task 6) — an entry is ticked
      when the member no longer contains the legacy literal AND appears in the census classification.
      ⚠ **Do NOT verify a tick by "the member contains a `Classify` call"** — a dead call whose result is
      discarded would mark a still-leaking site migrated. When the list is empty, **delete it**.
- [ ] **Step 6: Commit**

```bash
git add test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj test/FlaUI.Mcp.Tests/Server/RedactionSurfaceInventoryTests.cs
git commit -m "test(sp3): the source sweep — every content read inside one of two closed lists"
```

---

## Task 13 — Measurements (three; assert nothing without them)

- [ ] **Step 1: Process homogeneity.** Snapshot a window hosting an **embedded cross-process HWND**
      (WebView2/CEF/Electron) and compare each node's owning process id against the window root's.
      **If they differ, the once-per-walk hoist is unsound** — resolve `processName` per HWND boundary
      (re-read when a node's `NativeWindowHandle` differs from the current root's) and re-run Task 5.
      ⚠ Record the result either way; the same doubt applies to the shipped whole-window denylist, which
      is **out of scope for SP3** but must be written up as a follow-up finding.
- [ ] **Step 2: Zero-rule regression.** Time the 95-node WPF TestApp walk with no rule file against the
      pre-SP3 baseline. Expected: no regression. Record both numbers.
- [ ] **Step 3: Worst-case rules.** Time the same walk with 64 global rules. Record the number.
      Do **not** claim a speedup or a no-cost result anywhere without these figures.
- [ ] **Step 4: Record all three in `ROADMAP.md`** under item 9 and commit.

```bash
git add ROADMAP.md
git commit -m "docs(sp3): measured — homogeneity, zero-rule baseline, worst-case rule set"
```

---

## Task 14 — Parallel surfaces (spec §11) — a task with its own audit step

This project's recurring failure: the fix lands and a parallel surface still says the old thing. It hit
SP2 **five times**. Every path below was verified to exist at `9c7588e`.

- [ ] **Step 1: Update each surface.**
      `docs/agent-contract.md` (payload shapes; it mentions redaction once and documents none of the new
      fields) · `docs/operator-manual.md` (no `--redaction-rules`, no `check-redaction-rules` — the
      operator's primary reference for a feature only an operator can enable; state that rule NAMES reach
      the agent, so `acme-prod-vault` is a poor name) · `docs/architecture-and-safety.md` (describes the
      floor as denylist + `IsPassword`) · **BOTH** `.claude/skills/driving-flaui-mcp/SKILL.md` **and**
      `plugins/flaui-mcp/skills/driving-flaui-mcp/SKILL.md` · tool `[Description]` strings (deprecate
      `isPassword` in favour of `redacted`) · `README.md` (zero redaction mentions today) · recovery
      strings that say "password".
- [ ] **Step 2: Make the twins identical by COPY, never by editing twice.**

```bash
cp .claude/skills/driving-flaui-mcp/SKILL.md plugins/flaui-mcp/skills/driving-flaui-mcp/SKILL.md
diff .claude/skills/driving-flaui-mcp/SKILL.md plugins/flaui-mcp/skills/driving-flaui-mcp/SKILL.md && echo IDENTICAL
```

⚠ Edits belong in the **hand-authored floor**, not the GROWTH region (30-line hard cap).

- [ ] **Step 3: AUDIT.** Re-grep every surface for the superseded wording and show it is gone:

```bash
grep -rn "isPassword" docs/ README.md .claude/skills/ plugins/flaui-mcp/skills/ | grep -v "redacted"
```

Expected: no line that presents `isPassword` as the sensitivity signal without naming `redacted`.

- [ ] **Step 4: Run the guards.**
      `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~SkillLoadLineTests|FullyQualifiedName~ToolTrapFactInvariantTests"`
      Expected: PASS — twins byte-identical, every description ≤ 1500 chars.
- [ ] **Step 5: Commit** (stage paths BY NAME — never `git add -A`, which has committed a peer's scratch
      files into this repo before).

---

## Task 15 — Scope check and full gates

⚠ **SP1 shipped only half its scope because its plan never mentioned item 2.** This task exists so that
cannot recur.

- [ ] **Step 1: Walk the spec section by section** and name the task that implements each: §0 all ·
      §3.1 four families → Tasks 5–9 · §3.3 DEF-1/DEF-2 → Task 8 · §4.1 signals → Tasks 2–3 · §4.4
      default path → Tasks 5, 13 · §5.1–5.2 → Tasks 2–3 · §5.3 wire + DEF-3 → Tasks 9–10 · §5.4–5.5 →
      Tasks 4, 11 · §6.1 → Task 5 · §7.1 → Tasks 5–11 · §7.2/§7.2.1/§7.2.2 → Tasks 1, 12 · §9 BC-1/BC-2 →
      all · §11 → Task 14. **List any spec requirement with no task and add the task.**
- [ ] **Step 2: Verify the two acceptance constraints** are satisfied in code, not prose:
      (a) two closed lists, sweep mutation-verified, legacy inventory deleted;
      (b) pruning on positive proof of death only, access-denied never prunes and never reports no-server.
- [ ] **Step 3: Headless gate.**
      `dotnet test -c Release --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
      Expected: **0 failed, 0 skipped**, count ≥ 789 + every new headless fact.
- [ ] **Step 4: Desktop gate — MAIN THREAD, quiet machine, physical console, user-granted lease.**

```bash
dotnet test --filter "Category=Desktop&Category!=KnownDefect&FullyQualifiedName!~PopupGrafting"
dotnet test --filter "FullyQualifiedName~PopupGrafting"
```

Expected: **0 failed, 0 skipped.** ⚠ A green is green **at a SHA** — re-run after the last fix, not before.

- [ ] **Step 5: Default-path regression check.** Run `git diff 3b0ab8d -- test/` (`3b0ab8d` is the branch
      point; use the diff, not just `--stat`, because the distinction below is invisible in a stat).

      Expect new files and new facts — **plus exactly three mechanically-edited pre-existing files**, all
      forced by `SnapshotNode`'s changed constructor and all listed in Task 5's amendment:
      `WaitNameOracleTests.cs`, `PasswordRedactionTests.cs`, `SnapshotModelPinTests.cs`.

      **Read each of those three diffs by eye.** Every hunk must be a `IsPassword: …` → `Sensitivity: …`
      construction change or the `n.IsPassword` → `n.Sensitivity.Source == RedactionSource.Os` read
      change. **Any hunk that touches an `Assert`, an expected string, or an expected count is a
      default-path regression** — spec §4.4 forbids it, and the task is not done until it is reverted and
      the production code fixed instead.

      A fourth edited pre-existing test file is a finding: name it and justify it, or revert it.
- [ ] **Step 6: Update `ROADMAP.md`** — mark item 9 done, and commit.

---

## Self-review (run by the plan author, completed)

**1. Spec coverage.** Every §0 point maps to a task (Task 15 Step 1 is the enumeration). Two spec
sections are deliberately *not* implemented and both say so in the spec: §3.2 (the denylist is unchanged)
and the embedded-HWND doubt about that denylist (out of scope, recorded as a follow-up in Task 13).

**2. Placeholder scan.** No "TBD" / "add error handling" / "similar to Task N". Tasks 7–11 give the exact
file, line and change rather than a full code block where the change is a one-line substitution at a
verified citation; Tasks 2–6 and 12 carry complete code because they create new types or new mechanisms.

**3. Type consistency.** `Sensitivity(Redact, Source, RuleName)`, `RedactionSource{None,Os,Rule}`,
`SensitivityClassifier.{OsOnly,ForRules,HasRules,Classify}`, `RedactionRule.Matches`,
`RedactionRuleFile.{Load,MaxRules}`, `RedactionConfigException`, `ElementContent.{Name,Value,Text,Read}`,
`Read.{Text,Sensitivity,RawForIdentity}`, `ServerStateFile.{Write,Delete,ReadAll,PruneDead,
DefaultDirectory,CurrentStateVersion}`, `ServerLiveness{Live,Dead,Unknown}` are used identically
throughout. `SnapshotNode`'s member is `Sensitivity` (Task 5) and every later task reads
`n.Sensitivity.Redact` / `.Source` / `.RuleName`.

**Known ordering constraint:** Task 2's tests reference `RedactionRule`, which Task 3 creates. Executing
strictly in order, write Task 3's `RedactionRule` record before running Task 2 Step 5 — noted in that
step rather than reordering, because Task 3's loader depends on nothing in Task 2.

**Exhaustiveness audit.** Contracts are fully specified (rule schema with every field, default and
rejection reason; classifier signature and precedence; state-file schema; five CLI exit codes; the three
new wire fields). No decision is deferred without an owner. Edges covered: throwing `IsPassword`; a
throwing predicate beside a determinately-false one; null/absent name; empty-matching pattern; process
short-circuit; recycled PID; access-denied; version skew; two servers; missing vs absent flag; an element
genuinely named `[REDACTED]`; a redacted element with no `AutomationId`. **Deliberately left to
execution:** the exact `Microsoft.CodeAnalysis.CSharp` version must match the SDK in use at execution
time (Task 12 states the pinning rule; the literal version is verified then, not now), and Task 13's
measurements are numbers by definition.
